using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Chariot de service : on s'accroche aux poignees, on pousse, il roule et cogne les murs.
///
/// **Autorite : elle suit le pousseur.** C'est la seule exception du projet a la regle
/// "objets du monde = serveur", et elle est volontaire. L'avatar du joueur est en autorite
/// Owner, donc il bouge instantanement chez son proprietaire ; un chariot simule sur le
/// serveur repondrait un aller-retour reseau plus tard, tremblerait dans les virages, et le
/// joueur rentrerait dans son propre chariot.
///
/// Le serveur transfere donc la propriete du NetworkObject a celui qui prend les poignees,
/// et la reprend au relachement. Personne aux poignees = le serveur simule, exactement la
/// regle habituelle.
///
/// Consequence a retenir pour la suite : **celui qui simule le chariot devra aussi simuler
/// ce qui repose dessus.** Sans objet dessus (etape 1) et avec des objets cales et
/// cinematiques (etape 2), la question ne se pose pas. Elle se posera a l'etape 3, quand le
/// chargement devra glisser et tomber : il faudra transferer la propriete du chargement en
/// meme temps que celle du chariot.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public class Cart : NetworkBehaviour, IInteractable
{
    [Header("Prises")]
    [Tooltip("Points ou un joueur vient se placer pour pousser. Un seul exploite aujourd'hui : " +
             "la liste existe pour que la poussee a deux n'oblige pas a tout reecrire.")]
    [SerializeField] private Transform[] handleAnchors;

    [Tooltip("Nombre de pousseurs acceptes. A 1 pour l'instant — au-dela il faudra repliquer " +
             "un emplacement par prise, et decider lequel des deux simule la physique.")]
    [SerializeField] private int maxPushers = 1;

    [Header("Conduite")]
    [Tooltip("Distance a laquelle le chariot se tient devant le pousseur.")]
    [SerializeField] private float pushDistance = 0.9f;
    [Tooltip("Nervosite du suivi. Trop haut, le chariot devient collant et rigide.")]
    [SerializeField] private float followStiffness = 12f;

    [Tooltip("Marge de rattrapage sur la vitesse de la cible. Le plafond se calcule a partir " +
             "du deplacement reel de celle-ci et non d'un chiffre fixe : un chariot qu'on " +
             "peut semer se fait traverser, puis la prise cede. A 1 il suivrait sans jamais " +
             "rattraper son retard.")]
    [SerializeField] private float catchUpFactor = 1.8f;

    [Tooltip("Vitesse minimale de suivi, cible immobile : sert au recalage.")]
    [SerializeField] private float minFollowSpeed = 2f;

    [Tooltip("Vivacite de l'orientation. Le chariot garde un temps de retard volontaire — il " +
             "est lourd — mais il ne doit jamais decrocher du regard.")]
    [SerializeField] private float turnResponse = 14f;

    [Tooltip("Vitesse de rotation maximale, en degres par seconde.")]
    [SerializeField] private float maxTurnRate = 540f;

    [Tooltip("Ecart maximal, en degres, entre le regard du pousseur et l'orientation du " +
             "chariot. C'est ce qui fait qu'un chariot coince contre un mur empeche de " +
             "tourner la tete : il faut le lacher pour regarder ailleurs.")]
    [SerializeField] private float maxYawOffset = 30f;

    [Tooltip("Au-dela de cette distance la prise lache toute seule : le chariot est bloque " +
             "contre un mur et le joueur a continue d'avancer.")]
    [SerializeField] private float breakDistance = 1.6f;

    [Range(0.1f, 1f)]
    [Tooltip("Facteur applique a la vitesse du pousseur. C'est lui qui cree la tension " +
             "'presse mais ralenti'.")]
    [SerializeField] private float pushSpeedFactor = 0.6f;

    [Tooltip("Vitesse de redressement, en degres par seconde, si le chariot a malgre tout " +
             "reussi a s'incliner.")]
    [SerializeField] private float uprightSpeed = 240f;

    [Range(0f, 1f)]
    [Tooltip("Attenuation du balancement de camera pendant la poussee. Les mains posees sur " +
             "la barre, le buste est soutenu : la tete bouge moins qu'a mains nues. " +
             "S'ajoute au ralentissement deja du a la vitesse reduite.")]
    [SerializeField] private float bobDamping = 0.5f;

    // Le pousseur qui simule. Repliqué parce que sa machine doit savoir qui suivre, et que
    // les autres doivent pouvoir afficher l'etat sans deviner.
    private readonly NetworkVariable<NetworkObjectReference> driver = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Serveur uniquement : la liste complete, deja prete pour plusieurs pousseurs.
    private readonly List<PlayerCarry> pushers = new();

    private Rigidbody body;
    private Vector3 lastTarget;                                  // cible de la frame precedente
    private bool hasLastTarget;

    public float PushSpeedFactor => pushSpeedFactor;

    public float BobDamping => bobDamping;

    /// <summary>Orientation actuelle du chariot, sur laquelle le regard du pousseur est bride.</summary>
    public float Yaw => transform.eulerAngles.y;

    public float MaxYawOffset => maxYawOffset;

    public bool HasDriver => driver.Value.TryGet(out var target) && target != null;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();

        // On ne fait pas confiance au reglage de l'inspecteur, comme pour le isTrigger d'une
        // DeliveryZone : a cette etape le chariot est un bloc rigide qui ne bascule pas.
        // C'est une decision de conception, elle a sa place dans le code — une case oubliee
        // laisse un chariot couche sur le flanc dont le joueur ne peut plus rien faire.
        body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }

    // ---------- Interaction ----------

    /// <summary>
    /// Prenable si le joueur a les mains libres et que les poignees sont disponibles.
    /// Le relachement ne passe pas par ici : E pendant la poussee est traite par
    /// <see cref="PlayerCarry"/>, qui sait deja que les mains sont prises.
    /// </summary>
    public bool CanInteract(PlayerCarry player)
    {
        // On teste HasDriver et non la liste des pousseurs : la liste est serveur uniquement,
        // un client la verrait toujours vide et colorerait le viseur a tort.
        return player != null && !player.IsPushingCart && player.HasFreeHand && !HasDriver;
    }

    public void ServerInteract(PlayerCarry player)
    {
        if (!IsServer || !CanInteract(player)) return;

        ServerTakeHandles(player);
    }

    // ---------- Ordres serveur ----------

    private void ServerTakeHandles(PlayerCarry player)
    {
        if (pushers.Contains(player) || pushers.Count >= Mathf.Max(1, maxPushers)) return;

        pushers.Add(player);
        driver.Value = new NetworkObjectReference(player.NetworkObject);
        player.ServerSetPushedCart(NetworkObject);

        // Le coeur du choix d'architecture : la machine du pousseur devient celle qui simule.
        NetworkObject.ChangeOwnership(player.OwnerClientId);
    }

    /// <summary>Lache les poignees. Serveur uniquement.</summary>
    public void ServerRelease(PlayerCarry player)
    {
        if (!IsServer || player == null) return;
        if (!pushers.Remove(player)) return;

        player.ServerSetPushedCart(null);

        if (pushers.Count > 0) return;

        driver.Value = default;

        // Plus personne aux poignees : le serveur reprend la main, le chariot redevient un
        // objet du monde comme les autres.
        NetworkObject.RemoveOwnership();
    }

    public override void OnNetworkDespawn()
    {
        pushers.Clear();
    }

    // ---------- Conduite, chez le proprietaire ----------

    private void FixedUpdate()
    {
        // IsOwner : le serveur quand personne ne pousse, le pousseur sinon. Une seule machine
        // simule, jamais deux.
        if (!IsSpawned || !IsOwner || body == null) return;

        // Avant toute conduite, et meme sans pousseur : un chariot abandonne de travers doit
        // se relever tout seul.
        KeepUpright();

        if (!TryGetDriver(out var driverTransform))
        {
            hasLastTarget = false;
            return;
        }

        Drive(driverTransform);
    }

    /// <summary>
    /// Filet de securite : redresse le chariot s'il a reussi a s'incliner malgre les
    /// contraintes. Un chariot couche sur le flanc pour le reste de la partie est une
    /// situation dont le joueur ne peut pas se sortir.
    /// </summary>
    private void KeepUpright()
    {
        var euler = transform.eulerAngles;
        var pitch = Mathf.DeltaAngle(0f, euler.x);
        var roll = Mathf.DeltaAngle(0f, euler.z);

        if (Mathf.Abs(pitch) < 0.5f && Mathf.Abs(roll) < 0.5f) return;

        var upright = Quaternion.Euler(0f, euler.y, 0f);

        body.MoveRotation(Quaternion.RotateTowards(
            transform.rotation, upright, uprightSpeed * Time.fixedDeltaTime));
    }

    private bool TryGetDriver(out Transform driverTransform)
    {
        driverTransform = null;

        if (NetworkManager == null) return false;
        if (!driver.Value.TryGet(out var target) || target == null) return false;

        driverTransform = target.transform;
        return true;
    }

    private void Drive(Transform driverTransform)
    {
        var handle = Handle;

        // La prise cede quand le joueur s'eloigne **physiquement** du chariot, jamais quand
        // celui-ci n'est pas encore aligne. Mesurer l'ecart a la position ideale ferait
        // lacher au moindre coup de souris : cette cible orbite autour du joueur en un
        // instant, alors que le chariot est toujours a portee de bras.
        var toHandle = handle.position - driverTransform.position;
        toHandle.y = 0f;

        if (toHandle.magnitude > breakDistance)
        {
            ReleaseFromOwner(driverTransform);
            return;
        }

        // Cible horizontale seulement : la hauteur reste a la charge de la gravite, sinon le
        // chariot leviterait des que le joueur monterait une marche.
        var target = driverTransform.position + driverTransform.forward * pushDistance;
        var delta = target - handle.position;
        delta.y = 0f;

        // Le plafond depend de la vitesse de la cible, qui contient a la fois le deplacement
        // du joueur et sa rotation. Le calculer sur sa seule vitesse de marche bridait le
        // chariot a l'arret : le joueur tourne sur place, la cible decrit pourtant un arc de
        // cercle a bonne allure, et le chariot n'avait pas le droit de la suivre.
        var targetSpeed = hasLastTarget
            ? (target - lastTarget).magnitude / Time.fixedDeltaTime
            : 0f;

        lastTarget = target;
        hasLastTarget = true;

        var speedCap = Mathf.Max(minFollowSpeed, targetSpeed * catchUpFactor);

        // Par la velocite et non par MovePosition : c'est ce qui laisse un mur arreter
        // reellement le chariot au lieu de le faire traverser.
        var velocity = Vector3.ClampMagnitude(delta * followStiffness, speedCap);
        velocity.y = body.linearVelocity.y;
        body.linearVelocity = velocity;

        // Le chariot s'aligne sur le regard horizontal du pousseur. Les rotations X et Z sont
        // bloquees sur le Rigidbody : a cette etape c'est un bloc rigide, il ne bascule pas.
        var yawError = Mathf.DeltaAngle(transform.eulerAngles.y, driverTransform.eulerAngles.y);
        var turn = Mathf.Clamp(yawError * turnResponse, -maxTurnRate, maxTurnRate);

        body.angularVelocity = new Vector3(0f, turn * Mathf.Deg2Rad, 0f);
    }

    /// <summary>
    /// Le proprietaire constate que la prise a cede. Il ne decide rien lui-meme : il le
    /// signale au serveur, qui reste seul a ecrire l'etat.
    /// </summary>
    private void ReleaseFromOwner(Transform driverTransform)
    {
        if (!driverTransform.TryGetComponent<PlayerCarry>(out var carry)) return;

        if (IsServer)
            ServerRelease(carry);
        else
            RequestReleaseRpc(new NetworkObjectReference(carry.NetworkObject));
    }

    [Rpc(SendTo.Server)]
    private void RequestReleaseRpc(NetworkObjectReference playerRef)
    {
        if (!playerRef.TryGet(out var target) || target == null) return;
        if (!target.TryGetComponent<PlayerCarry>(out var carry)) return;

        ServerRelease(carry);
    }

    /// <summary>Point par lequel le chariot est tenu. L'origine de l'objet sinon.</summary>
    private Transform Handle =>
        handleAnchors != null && handleAnchors.Length > 0 && handleAnchors[0] != null
            ? handleAnchors[0]
            : transform;
}
