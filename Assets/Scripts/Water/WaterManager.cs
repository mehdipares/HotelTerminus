using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Arrivee d'eau de l'hotel. Jumeau de <see cref="PowerManager"/>, et volontairement ecrit
/// sur le meme modele — les deux etats globaux du jeu doivent se lire pareil.
///
/// Un seul exemplaire dans la scene. Tout ce qui consomme de l'eau lit <see cref="HasWater"/>
/// et s'abonne a <see cref="WaterChanged"/> : aucun registre de consommateurs a tenir, on
/// pourra poser cinquante eviers sans rien cabler.
///
/// **Couper l'eau suspend, ne repare pas.** Fermer la vanne arrete toutes les fuites
/// visuellement, mais chaque evier conserve son etat de degat. Rouvrir fait re-couler ce qui
/// n'a pas ete repare.
///
/// Autorite serveur : lui seul ouvre et ferme, les clients ne font que constater.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class WaterManager : NetworkBehaviour
{
    [Tooltip("Etat au lancement de la partie. L'eau coule par defaut.")]
    [SerializeField] private bool startSupplied = true;

    public static WaterManager Instance { get; private set; }

    /// <summary>
    /// Emis chez tout le monde a chaque changement. Statique volontairement : l'ordre de
    /// spawn reseau n'est pas garanti, un evier peut arriver avant le manager et doit
    /// pouvoir s'abonner quand meme.
    /// </summary>
    public static event Action<bool> WaterChanged;

    /// <summary>
    /// Etat lu par les consommateurs. Vrai par defaut : une scene de test sans plomberie
    /// continue de fonctionner.
    ///
    /// C'est une copie statique et non "Instance.hasWater.Value". La lecon vient du courant,
    /// ou l'on avait perdu une session dessus : une lecture qui traverse une reference peut
    /// tomber sur un objet detruit pendant que la scene se recharge, et en deduire le
    /// contraire de la verite — chez le seul client qui rejoint, donc invisible en solo.
    /// </summary>
    public static bool HasWater => supplied;

    private static bool supplied = true;

    private readonly NetworkVariable<bool> hasWater = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Sans rechargement de domaine, les statiques survivent d'une session de Play a l'autre :
    // l'Instance pointerait sur un objet detruit et l'evenement garderait des abonnes fantomes.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
        WaterChanged = null;
        supplied = true;
        Valves.Clear();
    }

    private void Awake()
    {
        // Le dernier arrive gagne. Quand un client rejoint, NGO recharge la scene : refuser
        // le nouvel exemplaire laisserait Instance sur l'ancien, detruit dans la foulee.
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        hasWater.OnValueChanged += OnWaterValueChanged;

        if (IsServer)
        {
            hasWater.Value = startSupplied;

            // Des roues ont pu se declarer avant nous : l'ordre de spawn reseau n'est pas
            // garanti, et le circuit doit refleter leur position reelle des le depart.
            ServerRecompute();
        }

        // Un joueur qui rejoint alors que l'eau est coupee doit arriver sur des eviers secs,
        // pas attendre le prochain changement.
        Apply(hasWater.Value);
    }

    public override void OnNetworkDespawn()
    {
        hasWater.OnValueChanged -= OnWaterValueChanged;
    }

    public override void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        base.OnDestroy();
    }

    private void OnWaterValueChanged(bool previous, bool current) => Apply(current);

    /// <summary>
    /// Unique endroit qui met a jour l'etat lu par les consommateurs. Tourne chez tout le
    /// monde, serveur comme clients : c'est la valeur repliquee qui declenche, jamais une
    /// decision locale.
    /// </summary>
    private static void Apply(bool value)
    {
        if (supplied == value) return;

        supplied = value;
        Debug.Log($"[Eau] {(value ? "ouverte" : "coupee")}.");
        WaterChanged?.Invoke(value);
    }

    // ---------- Le circuit ----------
    //
    // Les roues s'inscrivent d'elles-memes plutot que d'etre listees dans l'inspecteur : on
    // peut en poser une quatrieme sans rien recabler, et un champ oublie ne peut pas laisser
    // une roue hors du circuit.

    private static readonly List<WaterValve> Valves = new();

    public static void ServerRegister(WaterValve valve)
    {
        if (valve == null || Valves.Contains(valve)) return;

        Valves.Add(valve);
        ServerRecompute();
    }

    public static void ServerUnregister(WaterValve valve)
    {
        if (!Valves.Remove(valve)) return;

        ServerRecompute();
    }

    /// <summary>
    /// L'eau ne coule que si **toutes** les roues sont ouvertes. Aucune ne decide seule :
    /// a deux on se partage le travail, seul on les fait l'une apres l'autre.
    ///
    /// Serveur uniquement — c'est lui qui detient l'etat, les clients le lisent.
    /// </summary>
    public static void ServerRecompute()
    {
        if (Instance == null || !Instance.IsServer || !Instance.IsSpawned) return;

        foreach (var valve in Valves)
        {
            if (valve != null && !valve.IsOpen)
            {
                Instance.hasWater.Value = false;
                return;
            }
        }

        Instance.hasWater.Value = true;
    }
}
