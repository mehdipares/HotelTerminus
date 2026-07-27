using System.Collections;
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
public class BulbSocket : NetworkBehaviour, ICarryAnchor, IInteractable
{
    [Header("References")]
    [Tooltip("Ou l'ampoule vient se placer. Le point de prehension de l'ampoule s'aligne dessus.")]
    [SerializeField] private Transform bulbAnchor;
    [Tooltip("Lumiere pilotee par la douille. Allumee seulement si une ampoule saine est vissee.")]
    [SerializeField] private Light lamp;

    [Header("Surtension")]
    [Tooltip("Risque que l'ampoule grille au retour du courant. Reglable par douille : le " +
             "sous-sol peut etre plus capricieux que le hall.")]
    [Range(0f, 1f)]
    [SerializeField] private float surgeBurnChance = 0.18f;

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
    private Coroutine resolveRoutine;            // attente de l'ampoule chez un client qui rejoint

    /// <summary>Ancre vue par un Carryable : l'emplacement de l'ampoule.</summary>
    public Transform Anchor => bulbAnchor != null ? bulbAnchor : transform;

    public bool HasBulb => installedBulb.Value.TryGet(out var bulb) && bulb != null;

    public override void OnNetworkSpawn()
    {
        installedBulb.OnValueChanged += OnInstalledChanged;
        PowerManager.PowerChanged += OnPowerChanged;

        if (IsServer && startBulbPrefab != null)
            ServerSpawnStartBulb();

        // Un client qui rejoint doit voir la lumiere dans l'etat courant, pas attendre
        // le prochain changement.
        RefreshTrackedBulb();
    }

    public override void OnNetworkDespawn()
    {
        installedBulb.OnValueChanged -= OnInstalledChanged;
        PowerManager.PowerChanged -= OnPowerChanged;
        UnsubscribeTrackedBulb();
    }

    private void OnInstalledChanged(NetworkObjectReference previous, NetworkObjectReference current)
        => RefreshTrackedBulb();

    // ---------- Interaction ----------

    /// <summary>
    /// Aujourd'hui : retirer l'ampoule presente, si le joueur a les mains libres.
    /// Le vissage d'une ampoule tenue en main viendra a l'etape suivante.
    /// </summary>
    public bool CanInteract(PlayerCarry player)
    {
        return HasBulb && player != null && player.HasFreeHand;
    }

    public void ServerInteract(PlayerCarry player)
    {
        if (!IsServer || player == null) return;

        ServerReleaseBulbTo(player);
    }

    /// <summary>
    /// Visser l'ampoule tenue en main, si la douille est vide et si l'ampoule est saine.
    /// On refuse une ampoule grillee : la remplacer par une autre ampoule morte n'a aucun
    /// sens, et le refus se lit tout de suite puisque rien ne s'allume.
    /// </summary>
    public bool CanUse(PlayerCarry player)
    {
        if (HasBulb || player == null) return false;
        if (!player.TryGetHeld(out var carried)) return false;

        return carried.TryGetComponent<Bulb>(out var bulb) && !bulb.IsBurnt;
    }

    public void ServerUse(PlayerCarry player)
    {
        if (!IsServer || !CanUse(player)) return;
        if (!player.TryGetHeld(out var carried)) return;

        // La main se vide d'abord : l'objet passe de la main au receptacle sans jamais
        // etre libre entre les deux, sinon il tomberait le temps d'une frame.
        player.ServerReleaseHand();
        carried.ServerAttachToSocket(NetworkObject);

        installedBulb.Value = new NetworkObjectReference(carried.NetworkObject);
    }

    /// <summary>
    /// Devisse l'ampoule et la met dans la main du joueur. La douille se vide, donc
    /// <see cref="RefreshTrackedBulb"/> eteint la lumiere sans qu'on ait a s'en occuper.
    /// </summary>
    private void ServerReleaseBulbTo(PlayerCarry player)
    {
        if (!installedBulb.Value.TryGet(out var netObject) || netObject == null) return;
        if (!netObject.TryGetComponent<Carryable>(out var carryable)) return;

        installedBulb.Value = default;
        player.ServerTake(carryable);
    }

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
        else if (installedBulb.Value.NetworkObjectId != 0)
            resolveRoutine = StartCoroutine(ResolveWhenSpawned());

        RefreshLight();
    }

    /// <summary>
    /// Attend que l'ampoule referencee existe vraiment sur cette machine.
    ///
    /// Un client qui rejoint recoit la douille et son ampoule dans deux messages distincts :
    /// quand la douille se reveille, la reference pointe un objet qui n'est pas encore la.
    /// Sans cette attente la lampe restait eteinte **pour toujours** chez ce joueur — la
    /// reference ne changeant plus jamais, OnValueChanged ne repasse pas.
    /// </summary>
    private IEnumerator ResolveWhenSpawned()
    {
        for (var attempt = 0; attempt < 120 && IsSpawned; attempt++)
        {
            yield return null;

            if (!installedBulb.Value.TryGet(out var netObject) || netObject == null)
                continue;

            if (!netObject.TryGetComponent(out trackedBulb))
                break;

            trackedBulb.BurntChanged += OnBulbBurntChanged;
            RefreshLight();
            break;
        }

        resolveRoutine = null;
    }

    private void UnsubscribeTrackedBulb()
    {
        if (resolveRoutine != null)
        {
            StopCoroutine(resolveRoutine);
            resolveRoutine = null;
        }

        if (trackedBulb == null) return;

        trackedBulb.BurntChanged -= OnBulbBurntChanged;
        trackedBulb = null;
    }

    private void OnBulbBurntChanged(bool burnt) => RefreshLight();

    private void OnPowerChanged(bool powered)
    {
        RefreshLight();

        if (powered)
            ServerRollSurge();
    }

    /// <summary>
    /// Coup de jus au retour du courant : l'ampoule peut lacher.
    ///
    /// Le tirage se fait **sur le serveur uniquement**. L'evenement de courant se declenche
    /// sur toutes les machines : un de lance localement ferait griller des ampoules
    /// differentes chez chaque joueur, et l'hotel n'aurait plus la meme tete pour deux
    /// personnes cote a cote. Une seule machine tire, le resultat descend par
    /// <see cref="Bulb.IsBurnt"/>, deja repliqué.
    /// </summary>
    private void ServerRollSurge()
    {
        if (!IsServer || surgeBurnChance <= 0f) return;

        // Rien a griller : douille vide, ou ampoule deja morte.
        if (trackedBulb == null || trackedBulb.IsBurnt) return;

        if (Random.value >= surgeBurnChance) return;

        trackedBulb.ServerSetBurnt(true);
        Debug.Log($"[Surtension] {name} : ampoule grillee.");
    }

    private void RefreshLight()
    {
        if (lamp == null) return;

        // Trois conditions, toutes deduites d'un etat replique : une ampoule est vissee,
        // elle n'est pas grillee, et l'hotel a du courant.
        lamp.enabled = trackedBulb != null && !trackedBulb.IsBurnt && PowerManager.HasPower;
    }
}
