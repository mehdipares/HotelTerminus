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
public class Sink : NetworkBehaviour
{
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

    private float nextCheck;
    private float smallLeakSince;

    public SinkState State => state.Value;

    public bool IsLeaking => state.Value != SinkState.Normal;

    /// <summary>
    /// Est-ce que ca coule, la, maintenant ? **Deduit, jamais stocke.** Une fuite dont l'eau
    /// est coupee ne coule pas, mais elle est toujours la.
    /// </summary>
    public bool IsRunning => IsLeaking && WaterManager.HasWater;

    /// <summary>
    /// Emis chez tout le monde a chaque changement d'etat, et une fois au spawn avec l'etat
    /// courant : un joueur qui rejoint doit voir les eviers deja abimes, pas seulement ceux
    /// qui s'abiment apres son arrivee.
    /// </summary>
    public event Action<SinkState> StateChanged;

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
