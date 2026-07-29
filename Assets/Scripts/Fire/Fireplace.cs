using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Un foyer : un endroit qui peut prendre feu.
///
/// **Ne porte que l'etat**, jamais le rendu. Les flammes sont deduites par
/// <see cref="FireplaceVisual"/>, comme la lumiere d'une douille se deduit de l'ampoule et du
/// courant, et les particules d'un evier de sa fuite et de l'eau.
///
/// Autorite serveur : lui seul allume et eteint. Un client qui mettrait le feu de son cote
/// verrait une piece bruler que personne d'autre ne voit.
///
/// Volontairement minimal. L'extincteur, la propagation et le generateur en surchauffe
/// passeront tous par <see cref="ServerSetBurning"/> sans avoir a modifier cette classe.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Fireplace : NetworkBehaviour, IInteractable
{
    [Tooltip("Ce foyer brule-t-il des le lancement de la partie ? Utile pour tester.")]
    [SerializeField] private bool startBurning;

    [Header("Extinction")]
    [Tooltip("Duree du maintien de E, extincteur en main, pour venir a bout du foyer.")]
    [SerializeField] private float extinguishDuration = 4f;

    [Tooltip("Vitesse a laquelle le feu reprend quand on relache, en multiple de la vitesse " +
             "d'extinction. Superieur a 1 : un feu qu'on laisse repart plus vite qu'on ne " +
             "l'etouffe.")]
    [SerializeField] private float rekindleMultiplier = 1.5f;

    [Tooltip("Distance au-dela de laquelle le serveur retire un joueur de l'extinction.")]
    [SerializeField] private float extinguishMaxDistance = 5f;

    private readonly NetworkVariable<bool> burning = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> extinguishProgress = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private PlayerCarry firefighter;             // serveur uniquement

    public bool IsBurning => IsSpawned && burning.Value;

    /// <summary>
    /// Emis chez tout le monde a chaque changement, **et une fois au spawn avec l'etat
    /// courant**. C'est cette seconde partie qui compte : un joueur qui rejoint doit voir les
    /// foyers deja en feu, pas seulement ceux qui s'allument apres son arrivee.
    /// </summary>
    public event Action<bool> BurningChanged;

    public override void OnNetworkSpawn()
    {
        burning.OnValueChanged += OnBurningChanged;

        if (IsServer && startBurning)
            burning.Value = true;

        BurningChanged?.Invoke(burning.Value);
    }

    public override void OnNetworkDespawn()
    {
        burning.OnValueChanged -= OnBurningChanged;

        firefighter = null;
    }

    private void OnBurningChanged(bool previous, bool current) => BurningChanged?.Invoke(current);

    // ---------- Extinction ----------

    public bool IsHeldInteraction => true;

    public float HoldProgress => extinguishProgress.Value;

    /// <summary>
    /// Extinguible seulement si ca brule et si le joueur a un extincteur **en main** — comme
    /// la cle pour une grosse fuite. On ne demande donc pas des mains libres mais l'inverse.
    /// </summary>
    public bool CanInteract(PlayerCarry player)
    {
        if (player == null || !IsBurning) return false;

        return player.TryGetHeld(out var held) && held.GetComponent<Extinguisher>() != null;
    }

    /// <summary>Vide : tout passe par le maintien.</summary>
    public void ServerInteract(PlayerCarry player) { }

    public void ServerHoldBegin(PlayerCarry player)
    {
        if (!IsServer || player == null || !CanInteract(player)) return;
        firefighter = player;
    }

    public void ServerHoldEnd(PlayerCarry player)
    {
        if (!IsServer || player == null || firefighter != player) return;

        ServerStopSpraying();
    }

    private void ServerStopSpraying()
    {
        firefighter = null;
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;

        // On retire celui qui ne peut plus agir : trop loin, deconnecte, plus d'extincteur en
        // main, ou feu deja eteint par quelqu'un d'autre.
        if (firefighter != null
            && (!firefighter.IsSpawned
                || !CanInteract(firefighter)
                || Vector3.Distance(firefighter.transform.position, transform.position) > extinguishMaxDistance))
        {
            ServerStopSpraying();
        }

        if (!burning.Value)
        {
            if (extinguishProgress.Value != 0f)
                extinguishProgress.Value = 0f;

            return;
        }

        var rate = 1f / Mathf.Max(0.1f, extinguishDuration);

        // Le feu reprend plus vite qu'on ne l'etouffe : lacher a mi-parcours fait perdre plus
        // que le temps qu'on a mis. Un extincteur, ca se vide d'un coup.
        var delta = firefighter != null
            ? rate * Time.deltaTime
            : -rate * rekindleMultiplier * Time.deltaTime;

        var next = Mathf.Clamp01(extinguishProgress.Value + delta);

        if (!Mathf.Approximately(next, extinguishProgress.Value))
            extinguishProgress.Value = next;

        if (next < 1f) return;

        extinguishProgress.Value = 0f;
        ServerStopSpraying();
        ServerSetBurning(false);
    }

    /// <summary>
    /// Allume ou eteint. Serveur uniquement — unique porte d'entree, qu'emprunteront
    /// l'extincteur, la propagation et tout ce qui mettra le feu plus tard.
    /// </summary>
    public void ServerSetBurning(bool value)
    {
        if (!IsServer || !IsSpawned || burning.Value == value) return;

        burning.Value = value;
        Debug.Log($"[Feu] {name} : {(value ? "en feu" : "eteint")}.");
    }

    /// <summary>
    /// Bascule le foyer. Provisoire, pour la touche de debug : meme pour du debug, le client
    /// demande et le serveur decide — on s'est deja fait avoir a court-circuiter l'autorite,
    /// ca donne des resultats de test faux.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void RequestToggleRpc()
    {
        ServerSetBurning(!burning.Value);
    }
}
