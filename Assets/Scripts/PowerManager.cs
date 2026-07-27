using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Courant de l'hotel : premier etat global du jeu.
///
/// Un seul exemplaire dans la scene. Tout ce qui consomme du courant (douilles aujourd'hui,
/// ascenseur et cameras plus tard) lit <see cref="HasPower"/> et s'abonne a
/// <see cref="PowerChanged"/>.
///
/// Autorite serveur : lui seul coupe et retablit le courant, les clients ne font que
/// constater. Un client qui couperait la lumiere de son cote verrait une piece noire que
/// personne d'autre ne voit.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class PowerManager : NetworkBehaviour
{
    [Tooltip("Etat au lancement de la partie. Le generateur prendra le relais plus tard.")]
    [SerializeField] private bool startPowered = true;

    public static PowerManager Instance { get; private set; }

    /// <summary>
    /// Emis chez tout le monde a chaque changement, et une fois au spawn de chaque
    /// consommateur avec l'etat courant. Statique volontairement : l'ordre de spawn reseau
    /// n'est pas garanti, une douille peut arriver avant le manager et doit pouvoir
    /// s'abonner quand meme.
    /// </summary>
    public static event Action<bool> PowerChanged;

    /// <summary>
    /// Etat lu par les consommateurs. Vrai par defaut : une scene de test sans circuit
    /// electrique continue de fonctionner comme avant.
    ///
    /// C'est une copie statique et non "Instance.hasPower.Value" : la lecture ne doit pas
    /// dependre de la sante d'une reference. Une douille qui lisait pendant que la scene se
    /// rechargeait trouvait Instance a null, en deduisait "pas de circuit, donc du courant",
    /// et ne s'eteignait jamais chez le client.
    /// </summary>
    public static bool HasPower => powered;

    private static bool powered = true;

    private readonly NetworkVariable<bool> hasPower = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Sans rechargement de domaine (Enter Play Mode Options), les statiques survivent d'une
    // session de Play a l'autre : l'Instance pointerait sur un objet detruit et l'evenement
    // garderait des abonnes fantomes.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
        PowerChanged = null;
        powered = true;
    }

    private void Awake()
    {
        // Le dernier arrive gagne. Quand un client rejoint, NGO recharge la scene : refuser
        // le nouvel exemplaire laisserait Instance sur l'ancien, detruit dans la foulee.
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        hasPower.OnValueChanged += OnPowerValueChanged;

        if (IsServer)
            hasPower.Value = startPowered;

        // Un joueur qui rejoint en pleine coupure doit arriver dans le noir, pas attendre
        // le prochain changement.
        Apply(hasPower.Value);
    }

    public override void OnNetworkDespawn()
    {
        hasPower.OnValueChanged -= OnPowerValueChanged;
    }

    public override void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        base.OnDestroy();
    }

    private void OnPowerValueChanged(bool previous, bool current) => Apply(current);

    /// <summary>
    /// Unique endroit qui met a jour l'etat lu par les consommateurs. Tourne chez tout le
    /// monde, serveur comme clients : c'est la valeur repliquee qui declenche, jamais une
    /// decision locale.
    /// </summary>
    private static void Apply(bool value)
    {
        if (powered == value) return;

        // Transition OFF -> ON : c'est ici que se branchera la surtension qui grillera une
        // partie des ampoules.
        powered = value;
        Debug.Log($"[Courant] {(value ? "retabli" : "coupe")}.");
        PowerChanged?.Invoke(value);
    }

    /// <summary>
    /// Coupe ou retablit le courant. Serveur uniquement — unique porte d'entree, aujourd'hui
    /// empruntee par <see cref="Generator"/>.
    /// </summary>
    public void ServerSetPower(bool value)
    {
        if (!IsServer || !IsSpawned) return;

        // On n'ecrit que la NetworkVariable : l'etat local en decoule via OnValueChanged,
        // au meme titre que chez les clients. Pas de chemin court reserve au serveur.
        hasPower.Value = value;
    }
}
