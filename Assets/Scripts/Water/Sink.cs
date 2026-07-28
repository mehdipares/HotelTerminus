using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>Etat de degat d'un evier. Ce qui est stocke, et la seule chose qui l'est.</summary>
public enum SinkState
{
    Normal = 0,
    SmallLeak = 1,
    BigLeak = 2,
}

/// <summary>
/// Un evier et son etat de degat.
///
/// **Deux dimensions separees, et c'est tout le principe.** L'etat de degat est stocke ici et
/// repliqué. Le fait que ca coule, lui, n'est **jamais** stocke : il se deduit de
/// <c>fuite &amp;&amp; WaterManager.HasWater</c>. C'est le pattern de la douille, ou la lumiere
/// se deduit de l'ampoule vissee, saine, et du courant.
///
/// Le stocker reviendrait a garder la meme information a deux endroits — l'etat global et un
/// booleen par evier. Le jour ou un message reseau se perd, on aurait un evier qui coule
/// alors que l'eau est coupee. En le deduisant, l'incoherence est impossible.
///
/// Autorite serveur : lui seul tire les probabilites et fait courir les minuteurs. Sinon
/// chaque client verrait des eviers differents fuir.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Sink : NetworkBehaviour, IPickupBlocker, IInteractable
{
    /// <summary>Quelles fuites exigent la cle en main.</summary>
    public enum ToolRequirement
    {
        None = 0,
        BigLeakOnly = 1,
        AllLeaks = 2,
    }

    [Header("Reparation")]
    [Tooltip("Quelles fuites exigent d'avoir la cle en main.\n\n" +
             "Grosse seulement : une petite fuite se resserre a la main, une grosse demande " +
             "l'outil. C'est ce qui hierarchise les deux corvees — la grosse devient vraiment " +
             "lourde, il faut couper l'eau ET trouver la cle.\n\n" +
             "Passe a Toutes le jour ou l'inventaire existera : garder sa cle sur soi ne " +
             "coutera plus une main.")]
    [SerializeField] private ToolRequirement toolRequirement = ToolRequirement.BigLeakOnly;

    [Tooltip("Duree du maintien de E pour reparer une fuite.")]
    [SerializeField] private float repairDuration = 3f;

    [Tooltip("Vitesse a laquelle la reparation se defait quand on relache, en multiple de la " +
             "vitesse de progression.")]
    [SerializeField] private float repairDecay = 2f;

    [Tooltip("Distance au-dela de laquelle le serveur retire un joueur de la reparation.")]
    [SerializeField] private float repairMaxDistance = 4f;

    [Header("Apparition des fuites")]
    [Tooltip("Chances qu'un evier sain se mette a fuir, par minute. Monte-le tres haut pour " +
             "tester sans attendre.")]
    [SerializeField] private float leakChancePerMinute = 3f;

    [Tooltip("Delai avant qu'une petite fuite negligee ne devienne une grosse. C'est la " +
             "punition de l'inaction.")]
    [SerializeField] private float worsenAfterSeconds = 120f;

    [Tooltip("Intervalle entre deux tirages. Inutile de verifier a chaque frame.")]
    [SerializeField] private float checkInterval = 4f;

    private readonly NetworkVariable<SinkState> state = new(
        SinkState.Normal,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> repairProgress = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private float nextCheck;
    private float smallLeakSince;
    private IInstallable installable;
    private PlayerCarry repairer;                // serveur uniquement

    public SinkState State => state.Value;

    public bool IsLeaking => state.Value != SinkState.Normal;

    /// <summary>
    /// En place, donc en service. Un evier qu'on porte ou qui traine par terre n'est pas
    /// raccorde : il ne se degrade pas et ne coule pas.
    ///
    /// Defaut permissif : sans composant d'installation, l'evier est considere en service.
    /// </summary>
    public bool IsInstalled => installable == null || installable.IsInstalled;

    /// <summary>
    /// Est-ce que ca coule, la, maintenant ? **Deduit, jamais stocke.** Une fuite dont l'eau
    /// est coupee ne coule pas, mais elle est toujours la.
    /// </summary>
    public bool IsRunning => IsInstalled && IsLeaking && WaterManager.HasWater;

    /// <summary>
    /// Un evier **qui fuit** ne se decroche pas, petite fuite comprise.
    ///
    /// Pour la grosse, la raison est de conception : l'emporter serait la facon la plus
    /// simple d'arreter une inondation, et toute la boucle — couper l'eau, remonter reparer —
    /// n'aurait plus de raison d'exister.
    ///
    /// Pour la petite, la raison est l'ambiguite : l'evier porte deux interactions sur E,
    /// decrocher et reparer. Si les deux sont disponibles en meme temps, celle qui l'emporte
    /// depend de l'ordre des composants dans le prefab — un comportement qu'on ne peut ni
    /// expliquer au joueur ni deviner en lisant le code. Tant qu'il fuit, E repare ; une fois
    /// sec, E decroche.
    /// </summary>
    public bool BlocksPickup => IsInstalled && IsLeaking;

    // ---------- Reparation ----------

    public bool IsHeldInteraction => true;

    public float HoldProgress => repairProgress.Value;

    /// <summary>
    /// Reparable en maintenant E — mais une grosse fuite exige que **l'eau soit coupee**.
    ///
    /// C'est toute la boucle de coordination : le jet gicle, l'interaction est refusee, il
    /// faut descendre fermer les deux roues, remonter reparer, puis rouvrir. Une petite
    /// fuite, elle, se repare a tout moment : c'est la corvee legere.
    ///
    /// La condition se lit sur la donnee stockee, jamais sur ce qu'on voit couler.
    /// </summary>
    public bool CanInteract(PlayerCarry player)
    {
        if (player == null || !IsInstalled || !IsLeaking) return false;

        // Une grosse fuite exige l'eau coupee. Une petite se repare a tout moment.
        if (state.Value == SinkState.BigLeak && WaterManager.HasWater) return false;

        return HasRequiredTool(player);
    }

    /// <summary>
    /// Le joueur tient-il ce qu'il faut ?
    ///
    /// Quand l'outil est exige, il doit etre **en main** — on ne demande donc pas des mains
    /// libres mais l'inverse, ce qui est le contraire de toutes les autres interactions du
    /// jeu. Sans outil requis, on repare a mains nues et il faut donc les avoir libres.
    /// </summary>
    private bool HasRequiredTool(PlayerCarry player)
    {
        var needsTool = toolRequirement == ToolRequirement.AllLeaks
                        || (toolRequirement == ToolRequirement.BigLeakOnly
                            && state.Value == SinkState.BigLeak);

        if (!needsTool) return player.HasFreeHand;

        return player.TryGetHeld(out var held)
               && held.GetComponent<RepairTool>() != null;
    }

    /// <summary>Vide : tout passe par le maintien.</summary>
    public void ServerInteract(PlayerCarry player) { }

    public void ServerHoldBegin(PlayerCarry player)
    {
        if (!IsServer || player == null || !CanInteract(player)) return;

        repairer = player;
    }

    public void ServerHoldEnd(PlayerCarry player)
    {
        if (!IsServer || player == null) return;

        if (repairer == player)
            repairer = null;
    }

    /// <summary>
    /// Emis chez tout le monde a chaque changement d'etat, et une fois au spawn avec l'etat
    /// courant : un joueur qui rejoint doit voir les eviers deja abimes, pas seulement ceux
    /// qui s'abiment apres son arrivee.
    /// </summary>
    public event Action<SinkState> StateChanged;

    private void Awake()
    {
        installable = GetComponent<IInstallable>();
    }

    public override void OnNetworkSpawn()
    {
        state.OnValueChanged += OnStateChanged;

        if (IsServer)
        {
            // Decale les tirages entre eviers : sans ca, tous verifient dans la meme frame
            // et les fuites apparaissent par paquets.
            nextCheck = Time.time + UnityEngine.Random.Range(0f, checkInterval);
            smallLeakSince = Time.time;
        }

        StateChanged?.Invoke(state.Value);
    }

    public override void OnNetworkDespawn()
    {
        state.OnValueChanged -= OnStateChanged;
        repairer = null;
    }

    private void OnStateChanged(SinkState previous, SinkState current)
        => StateChanged?.Invoke(current);

    /// <summary>Change l'etat de degat. Serveur uniquement.</summary>
    public void ServerSetState(SinkState value)
    {
        if (!IsServer || !IsSpawned || state.Value == value) return;

        state.Value = value;

        if (value == SinkState.SmallLeak)
            smallLeakSince = Time.time;

        Debug.Log($"[Evier] {name} : {value}.");
    }

    // ---------- Degradation, serveur uniquement ----------

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;

        TickRepair();

        // Pas raccorde, pas de degradation : un evier stocke dans un placard ne s'abime pas.
        // Le minuteur de la petite fuite repart de la pose, sinon un evier laisse au sol
        // reviendrait au mur deja pret a empirer.
        if (!IsInstalled)
        {
            smallLeakSince = Time.time;
            return;
        }

        if (Time.time < nextCheck) return;

        nextCheck = Time.time + Mathf.Max(0.5f, checkInterval);

        // La degradation court MEME l'eau coupee. Sans ca, fermer les vannes en permanence
        // deviendrait la strategie optimale : plus de fuites, plus rien a faire. En laissant
        // la degradation courir, couper l'eau accumule une dette — on descend reparer une
        // grosse fuite pendant que trois petites empirent ailleurs.
        switch (state.Value)
        {
            case SinkState.Normal:
                TryStartLeak();
                break;

            case SinkState.SmallLeak:
                if (Time.time - smallLeakSince >= worsenAfterSeconds)
                    ServerSetState(SinkState.BigLeak);
                break;
        }
    }

    /// <summary>
    /// Fait avancer la reparation en cours. Le reparateur est retire s'il s'eloigne, se
    /// deconnecte, ou si les conditions changent — l'eau qui revient pendant qu'on repare une
    /// grosse fuite annule tout, et c'est voulu : quelqu'un a rouvert une roue trop tot.
    /// </summary>
    private void TickRepair()
    {
        if (repairer != null
            && (!repairer.IsSpawned
                || !CanInteract(repairer)
                || Vector3.Distance(repairer.transform.position, transform.position) > repairMaxDistance))
        {
            repairer = null;
        }

        var rate = 1f / Mathf.Max(0.1f, repairDuration);

        var delta = repairer != null
            ? rate * Time.deltaTime
            : -rate * repairDecay * Time.deltaTime;

        var next = Mathf.Clamp01(repairProgress.Value + delta);

        if (!Mathf.Approximately(next, repairProgress.Value))
            repairProgress.Value = next;

        if (next < 1f) return;

        repairProgress.Value = 0f;
        repairer = null;

        ServerSetState(SinkState.Normal);
    }

    private void TryStartLeak()
    {
        if (leakChancePerMinute <= 0f) return;

        // La chance annoncee est par minute : on la ramene a la duree reelle de l'intervalle,
        // pour que changer checkInterval ne change pas la frequence des fuites.
        var chance = leakChancePerMinute / 60f * Mathf.Max(0.5f, checkInterval);

        if (UnityEngine.Random.value < chance)
            ServerSetState(SinkState.SmallLeak);
    }
}
