using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Douille : receptacle qui accueille une ampoule et pilote une lumiere.
///
/// L'ampoule vissee est un **vrai objet** (NetworkObject + Bulb + Carryable), pas un simple
/// etat. Consequence : celle qu'on retire est exactement celle qui etait la, avec son
/// historique. Le jour ou un objet portera une identite (une valise et son contenu, un
/// cadavre), le modele tiendra toujours.
///
/// Autorite serveur : lui seul visse, devisse et decide de l'etat de la lumiere.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class BulbSocket : NetworkBehaviour, ICarryAnchor
{
    [Header("References")]
    [Tooltip("Ou l'ampoule vient se placer. Le point de prehension de l'ampoule s'aligne dessus.")]
    [SerializeField] private Transform bulbAnchor;
    [Tooltip("Lumiere pilotee par la douille. Allumee seulement si une ampoule saine est vissee.")]
    [SerializeField] private Light lamp;

    [Header("Contenu au demarrage — provisoire")]
    [Tooltip("Ampoule presente au lancement. Sert a tester le cycle tout de suite ; plus tard " +
             "les douilles demarreront allumees et grilleront en cours de partie.")]
    [SerializeField] private GameObject startBulbPrefab;
    [SerializeField] private bool startBulbIsBurnt = true;

    private readonly NetworkVariable<NetworkObjectReference> installedBulb = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private Bulb trackedBulb;                    // ampoule dont on suit l'etat, pour la lumiere

    /// <summary>Ancre vue par un Carryable : l'emplacement de l'ampoule.</summary>
    public Transform Anchor => bulbAnchor != null ? bulbAnchor : transform;

    public bool HasBulb => installedBulb.Value.TryGet(out var bulb) && bulb != null;

    public override void OnNetworkSpawn()
    {
        installedBulb.OnValueChanged += OnInstalledChanged;

        Debug.Log($"[Douille] {name} spawn reseau. IsServer={IsServer}, " +
                  $"prefab={(startBulbPrefab != null ? startBulbPrefab.name : "AUCUN")}, " +
                  $"anchor={(bulbAnchor != null ? bulbAnchor.name : "AUCUN")}, " +
                  $"lamp={(lamp != null ? lamp.name : "AUCUNE")}");

        if (IsServer && startBulbPrefab != null)
            ServerSpawnStartBulb();

        // Un client qui rejoint doit voir la lumiere dans l'etat courant, pas attendre
        // le prochain changement.
        RefreshTrackedBulb();
    }

    public override void OnNetworkDespawn()
    {
        installedBulb.OnValueChanged -= OnInstalledChanged;
        UnsubscribeTrackedBulb();
    }

    private void OnInstalledChanged(NetworkObjectReference previous, NetworkObjectReference current)
        => RefreshTrackedBulb();

    // ---------- Serveur ----------

    private void ServerSpawnStartBulb()
    {
        var instance = Instantiate(startBulbPrefab, Anchor.position, Anchor.rotation);
        var netObject = instance.GetComponent<NetworkObject>();

        if (netObject == null)
        {
            Debug.LogError($"[Douille] {startBulbPrefab.name} n'a pas de NetworkObject.");
            Destroy(instance);
            return;
        }

        netObject.Spawn();

        // L'etat se pose apres le Spawn : une NetworkVariable n'est ecrivable qu'une fois
        // l'objet enregistre sur le reseau.
        if (instance.TryGetComponent<Bulb>(out var bulb))
            bulb.ServerSetBurnt(startBulbIsBurnt);

        if (instance.TryGetComponent<Carryable>(out var carryable))
            carryable.ServerAttachToSocket(NetworkObject);

        installedBulb.Value = new NetworkObjectReference(netObject);

        Debug.Log($"[Douille] Ampoule vissee dans {name} en {instance.transform.position}, " +
                  $"echelle {instance.transform.lossyScale}.");
    }

    // ---------- Etat local, deduit du serveur ----------

    /// <summary>
    /// Se raccroche a l'ampoule actuellement vissee pour suivre son etat : c'est ce qui
    /// permettra a la lumiere de s'eteindre toute seule le jour ou une ampoule grillera
    /// en cours de partie.
    /// </summary>
    private void RefreshTrackedBulb()
    {
        UnsubscribeTrackedBulb();

        if (installedBulb.Value.TryGet(out var netObject) && netObject != null)
            netObject.TryGetComponent(out trackedBulb);

        if (trackedBulb != null)
            trackedBulb.BurntChanged += OnBulbBurntChanged;

        RefreshLight();
    }

    private void UnsubscribeTrackedBulb()
    {
        if (trackedBulb == null) return;

        trackedBulb.BurntChanged -= OnBulbBurntChanged;
        trackedBulb = null;
    }

    private void OnBulbBurntChanged(bool burnt) => RefreshLight();

    private void RefreshLight()
    {
        if (lamp == null) return;

        // Allumee seulement si une ampoule est vissee ET qu'elle n'est pas grillee.
        lamp.enabled = trackedBulb != null && !trackedBulb.IsBurnt;
    }
}
