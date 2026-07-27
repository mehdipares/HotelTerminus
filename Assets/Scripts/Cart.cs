using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Chariot de service. Le joueur ne le pousse pas : il le **conduit**.
///
/// Une fois aux poignees, l'ensemble {joueur + chariot} est un seul bloc solidaire. Les
/// touches de deplacement pilotent le chariot, et la position du joueur s'en deduit — il ne
/// se deplace plus par lui-meme. C'est ce qui rend le blocage solidaire gratuit : si le
/// chariot ne passe pas, personne ne passe.
///
/// La direction est celle d'un caddie : avancer / reculer, et pivoter **autour de l'essieu
/// arriere**. Le braquage depend de la vitesse, donc on ne tourne quasiment pas sur place.
/// C'est volontaire — un demi-tour dans un couloir etroit doit etre une galere.
///
/// **Autorite : elle suit le pousseur.** Le serveur transfere la propriete du NetworkObject a
/// celui qui prend les poignees et la reprend au relachement ; personne aux poignees = le
/// serveur simule. Un chariot simule sur le serveur repondrait un aller-retour reseau plus
/// tard, ce qui est inacceptable pour un objet dont depend le deplacement du joueur.
///
/// **Consequence : le chariot ne peut rien bousculer par la physique.** Chez le pousseur, les
/// objets du monde sont cinematiques puisque seul le serveur les simule. Le chariot constate
/// donc le contact et demande au serveur d'appliquer l'impulsion — le meme chemin que la
/// bousculade par le joueur, deja documente dans ARCHITECTURE.md.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public class Cart : NetworkBehaviour, IInteractable, ICarryAnchor
{
    [Header("Reperes")]
    [Tooltip("Ou se tient le conducteur : derriere la barre, au niveau du sol. L'origine du " +
             "joueur etant a ses pieds, ce point doit toucher le plancher.")]
    [SerializeField] private Transform operatorAnchor;

    [Tooltip("Essieu arriere : le point autour duquel le chariot pivote quand on braque. " +
             "Vide = le centre de l'objet, et le chariot tournera comme une toupie.")]
    [SerializeField] private Transform rearAxle;

    [Header("Chargement")]
    [Tooltip("Premiere place du plateau : celle du fond, en bas. Les autres en decoulent. " +
             "Son orientation donne celle des bagages poses.")]
    [SerializeField] private Transform loadOrigin;

    [Tooltip("Bagages par etage.")]
    [SerializeField] private int columns = 3;

    [Tooltip("Nombre d'etages empiles.")]
    [SerializeField] private int levels = 3;

    [Tooltip("Ecart entre deux places d'un meme etage, le long du plateau.")]
    [SerializeField] private float columnSpacing = 0.62f;

    [Tooltip("Hauteur d'un etage.")]
    [SerializeField] private float levelHeight = 0.45f;

    [Tooltip("Taille du repere orange. Purement visuel : une place accueille un bagage quelle " +
             "que soit sa taille reelle.")]
    [SerializeField] private Vector3 ghostSize = new(0.5f, 0.4f, 0.55f);

    [Tooltip("Facultatif. Laisse vide et le repere se fabrique tout seul au lancement.")]
    [SerializeField] private Material ghostMaterial;

    [Header("Vitesses")]
    [Tooltip("Allure normale, chariot pousse.")]
    [SerializeField] private float pushWalkSpeed = 4.2f;

    [Tooltip("Allure sprint, chariot pousse. Plus lente qu'un sprint a vide — c'est le prix " +
             "du chariot — mais nettement au-dessus de la marche poussee.")]
    [SerializeField] private float pushSprintSpeed = 6.2f;

    [Tooltip("Marche arriere : lente, et sans voir derriere soi. Elle sert a se degager d'un " +
             "cul-de-sac, pas a se deplacer. Le sprint ne s'y applique pas.")]
    [SerializeField] private float pushReverseSpeed = 1.8f;

    [Tooltip("Vivacite de la mise en mouvement. Basse = le chariot demarre lourdement.")]
    [SerializeField] private float pushAcceleration = 9f;

    [Tooltip("Vivacite de l'arret. Volontairement plus basse que l'acceleration : une masse " +
             "lancee continue sur son erre quand on lache les touches.")]
    [SerializeField] private float pushDeceleration = 6f;

    [Header("Direction")]
    [Tooltip("Vitesse de braquage a pleine allure, en degres par seconde.")]
    [SerializeField] private float turnSpeed = 110f;

    [Tooltip("Vitesse de rotation sur place, chariot a l'arret, en degres par seconde. " +
             "A l'arret le chariot tourne autour de son centre, comme un caddie dont on fait " +
             "pivoter les roues folles ; en mouvement il braque autour de l'essieu arriere.")]
    [SerializeField] private float pivotSpeed = 65f;

    [Header("Bousculade du decor")]
    [Tooltip("Vitesse minimale pour deranger quelque chose. En dessous on effleure.")]
    [SerializeField] private float minPushSpeed = 0.6f;

    [Tooltip("Force transmise, par unite de vitesse.")]
    [SerializeField] private float pushForce = 4.5f;

    [Tooltip("Delai entre deux bousculades, pour ne pas envoyer un message reseau par frame " +
             "de contact.")]
    [SerializeField] private float pushCooldown = 0.2f;

    [Tooltip("Portee de verification cote serveur. Large : il voit le chariot avec un peu de " +
             "retard reseau.")]
    [SerializeField] private float pushMaxDistance = 4f;

    [Header("Detachement")]
    [Tooltip("De combien le joueur est repose sur le cote en lachant, pour ne pas se " +
             "retrouver dans la barre.")]
    [SerializeField] private float detachSideStep = 0.85f;

    [Header("Divers")]
    [Range(0f, 1f)]
    [Tooltip("Attenuation du balancement de camera. Les mains posees sur la barre, le buste " +
             "est soutenu : la tete bouge moins qu'a mains nues.")]
    [SerializeField] private float bobDamping = 0.5f;

    [Tooltip("Vitesse de redressement si le chariot a malgre tout reussi a s'incliner.")]
    [SerializeField] private float uprightSpeed = 240f;

    // Conducteur actuel. Repliqué : les autres machines doivent savoir que le chariot est
    // occupe, et le refuser a un second joueur.
    private readonly NetworkVariable<NetworkObjectReference> driver = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Serveur uniquement. Un seul element en v1 : la poussee a deux est une mecanique
    // distincte, la liste existe pour ne pas avoir a tout reecrire ce jour-la.
    private readonly List<PlayerCarry> pushers = new();

    // Occupation du plateau : un identifiant d'objet par place, 0 = libre.
    //
    // On stocke l'identifiant et non une NetworkObjectReference : une NetworkList exige un
    // type non manage simple, et un ulong se resout parfaitement pour un objet du monde.
    // La regle "referencer un joueur par NetworkObject, jamais par identifiant" ne concerne
    // que les avatars, dont la resolution est bridee chez les clients.
    private readonly NetworkList<ulong> load = new();

    private Rigidbody body;
    private Vector2 driveInput;                  // commandes du conducteur, chez lui seulement
    private bool driveSprint;
    private float currentSpeed;                  // vitesse longitudinale lissee
    private float nextPushTime;

    private Transform[] slots;                   // ancres de chargement, fabriquees au lancement
    private GameObject ghost;                    // repere de pose, purement local

    public float BobDamping => bobDamping;

    /// <summary>Orientation du chariot. Le corps du conducteur y est soude.</summary>
    public float Yaw => transform.eulerAngles.y;

    /// <summary>Place du conducteur, dont sa position se deduit entierement.</summary>
    public Vector3 OperatorPosition => operatorAnchor != null ? operatorAnchor.position : transform.position;

    /// <summary>Vitesse du chariot, lue par le joueur pour son balancement de marche.</summary>
    public Vector3 Velocity => body != null ? body.linearVelocity : Vector3.zero;

    public bool HasDriver => driver.Value.TryGet(out var target) && target != null;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();

        // On ne fait pas confiance au reglage de l'inspecteur, comme pour le isTrigger d'une
        // DeliveryZone : le chariot ne bascule pas. Une case oubliee laisserait un chariot
        // couche sur le flanc dont le joueur ne peut plus rien faire.
        body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        BuildSlots();
    }

    /// <summary>
    /// Fabrique les places du plateau a partir de trois reglages, plutot que de faire placer
    /// neuf objets vides a la main : la grille reste reguliere par construction, et changer
    /// l'espacement ne demande pas de tout redisposer.
    ///
    /// Les places sont enfants du chariot, elles le suivent donc sans qu'on s'en occupe.
    /// </summary>
    private void BuildSlots()
    {
        var origin = loadOrigin != null ? loadOrigin : transform;

        columns = Mathf.Max(1, columns);
        levels = Mathf.Max(1, levels);

        slots = new Transform[columns * levels];

        for (var i = 0; i < slots.Length; i++)
        {
            var column = i % columns;
            var level = i / columns;

            var slot = new GameObject($"Place_{level + 1}_{column + 1}").transform;
            slot.SetParent(origin, false);
            slot.localPosition = new Vector3(0f, level * levelHeight, column * columnSpacing);
            slot.localRotation = Quaternion.identity;

            slots[i] = slot;
        }
    }

    public override void OnNetworkSpawn()
    {
        load.OnListChanged += OnLoadChanged;

        // Une place par emplacement, vide. La liste est repliquee, donc tout le monde range
        // les bagages au meme endroit.
        if (IsServer && load.Count == 0)
        {
            for (var i = 0; i < slots.Length; i++)
                load.Add(0UL);
        }
    }

    /// <summary>
    /// L'attachement d'un bagage et la place qu'on lui attribue voyagent dans deux messages
    /// distincts, et rien ne garantit lequel arrive en premier. Si la place arrive apres, on
    /// previent le bagage de recalculer son ancre — sinon il resterait sur la premiere.
    /// </summary>
    private void OnLoadChanged(NetworkListEvent<ulong> change)
    {
        if (change.Value == 0UL || NetworkManager == null) return;

        if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(change.Value, out var netObject)
            && netObject != null
            && netObject.TryGetComponent<Carryable>(out var carryable))
        {
            carryable.RefreshAttachment();
        }
    }

    // ---------- Chargement ----------

    /// <summary>Ancre vue par un Carryable : celle de la place qui lui est reservee.</summary>
    public Transform Anchor => slots != null && slots.Length > 0 ? slots[0] : transform;

    public Transform AnchorFor(Carryable item)
    {
        if (item == null || item.NetworkObject == null || slots == null) return Anchor;

        var index = IndexOf(item.NetworkObject.NetworkObjectId);

        return index >= 0 ? slots[index] : Anchor;
    }

    private int IndexOf(ulong objectId)
    {
        for (var i = 0; i < load.Count && i < slots.Length; i++)
        {
            if (load[i] == objectId) return i;
        }

        return -1;
    }

    /// <summary>Premiere place libre, en remplissant etage par etage. -1 si le plateau est plein.</summary>
    private int FirstFreeSlot() => IndexOf(0UL);

    /// <summary>Derniere place occupee : c'est celle qu'on reprend en premier.</summary>
    private int LastLoadedSlot()
    {
        for (var i = Mathf.Min(load.Count, slots?.Length ?? 0) - 1; i >= 0; i--)
        {
            if (load[i] != 0UL) return i;
        }

        return -1;
    }

    // ---------- Interaction ----------

    /// <summary>
    /// Un seul conducteur en v1 : un second joueur se voit refuser le chariot, il est occupe.
    /// Le relachement ne passe pas par ici — E pendant la conduite est traite par
    /// <see cref="PlayerCarry"/>, qui sait deja que les mains sont prises.
    /// </summary>
    public bool CanInteract(PlayerCarry player)
    {
        // HasDriver et non la liste : celle-ci est serveur uniquement, un client la verrait
        // toujours vide et colorerait le viseur alors que le chariot est pris.
        return player != null && !player.IsPushingCart && player.HasFreeHand && !HasDriver;
    }

    public void ServerInteract(PlayerCarry player)
    {
        if (!IsServer || !CanInteract(player)) return;

        pushers.Add(player);
        driver.Value = new NetworkObjectReference(player.NetworkObject);
        player.ServerSetPushedCart(NetworkObject);

        // Le coeur du choix d'architecture : la machine du conducteur devient celle qui simule.
        NetworkObject.ChangeOwnership(player.OwnerClientId);
    }

    /// <summary>
    /// Clic gauche : poser ce qu'on tient, ou reprendre le dernier bagage pose. C'est la meme
    /// touche que pour visser une ampoule — elle sert toujours a *appliquer* ce qu'on a en
    /// main, la ou E sert aux poignees.
    /// </summary>
    public bool CanUse(PlayerCarry player)
    {
        if (player == null || player.IsPushingCart) return false;

        return player.TryGetHeld(out _)
            ? FirstFreeSlot() >= 0
            : LastLoadedSlot() >= 0;
    }

    public void ServerUse(PlayerCarry player)
    {
        if (!IsServer || !CanUse(player)) return;

        if (player.TryGetHeld(out var carried))
            ServerLoad(player, carried);
        else
            ServerUnload(player);
    }

    private void ServerLoad(PlayerCarry player, Carryable carried)
    {
        var index = FirstFreeSlot();
        if (index < 0 || carried.NetworkObject == null) return;

        // La main se vide d'abord : l'objet passe de la main au plateau sans jamais etre
        // libre entre les deux, sinon il tomberait le temps d'une frame.
        player.ServerReleaseHand();

        load[index] = carried.NetworkObject.NetworkObjectId;
        carried.ServerAttachToSocket(NetworkObject);
    }

    private void ServerUnload(PlayerCarry player)
    {
        var index = LastLoadedSlot();
        if (index < 0) return;

        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(load[index], out var netObject)
            || netObject == null
            || !netObject.TryGetComponent<Carryable>(out var carryable))
        {
            // L'objet a disparu entre-temps : on libere la place plutot que de la bloquer.
            load[index] = 0UL;
            return;
        }

        load[index] = 0UL;
        player.ServerTake(carryable);
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
        load.OnListChanged -= OnLoadChanged;
        pushers.Clear();
    }

    // ---------- Repere de pose ----------

    /// <summary>
    /// Montre ou masque le cube orange qui annonce ou le bagage va se poser.
    ///
    /// Purement local : aucune replication, aucun cout reseau. C'est un repere d'interface,
    /// chaque joueur voit le sien, et il n'a aucune existence pour les autres.
    /// </summary>
    public void ShowPlacementGhost(bool visible)
    {
        var index = visible ? FirstFreeSlot() : -1;

        if (index < 0 || slots == null || index >= slots.Length)
        {
            if (ghost != null) ghost.SetActive(false);
            return;
        }

        EnsureGhost();
        if (ghost == null) return;

        ghost.transform.SetParent(slots[index], false);
        ghost.transform.localPosition = Vector3.zero;
        ghost.transform.localRotation = Quaternion.identity;
        ghost.transform.localScale = ghostSize;

        ghost.SetActive(true);
    }

    private void EnsureGhost()
    {
        if (ghost != null) return;

        ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ghost.name = "Repere_de_pose";

        // Un repere ne doit rien percuter, surtout pas le chariot qui le porte.
        Destroy(ghost.GetComponent<Collider>());

        var renderer = ghost.GetComponent<MeshRenderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        var material = ghostMaterial != null ? ghostMaterial : BuildGhostMaterial();

        if (material == null)
        {
            // Aucun shader utilisable : on renonce au repere plutot que d'afficher un cube
            // rose au milieu de l'ecran.
            Debug.LogWarning("[Chariot] Pas de materiau pour le repere de pose. " +
                             "Assigne-en un dans le champ Ghost Material.");
            Destroy(ghost);
            ghost = null;
            return;
        }

        renderer.sharedMaterial = material;
        ghost.SetActive(false);
    }

    /// <summary>
    /// Fabrique un materiau orange translucide, pour n'avoir aucun asset a preparer a la
    /// main. Si le shader est introuvable dans un build, le champ Ghost Material prend le
    /// relais.
    /// </summary>
    private static Material BuildGhostMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        if (shader == null) return null;

        var material = new Material(shader);

        material.SetFloat("_Surface", 1f);                 // transparent
        material.SetFloat("_ZWrite", 0f);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        var orange = new Color(1f, 0.55f, 0.1f, 0.35f);
        material.SetColor("_BaseColor", orange);
        material.color = orange;

        return material;
    }

    // ---------- Conduite ----------

    /// <summary>
    /// Commandes du conducteur. Appele par son propre PlayerController : la machine du
    /// conducteur est celle qui possede le chariot, donc l'appel reste local et il n'y a
    /// aucun RPC par frame.
    /// </summary>
    public void SetDriveInput(Vector2 input, bool sprinting)
    {
        driveInput = input;
        driveSprint = sprinting;
    }

    /// <summary>
    /// Emplacement libre ou reposer le joueur qui lache : sur un cote, jamais dans la barre.
    /// On essaie la droite puis la gauche, et on renonce si les deux sont encombrees — mieux
    /// vaut ne pas bouger que traverser un mur.
    /// </summary>
    public Vector3 DetachPosition(float capsuleRadius, float capsuleHeight)
    {
        var origin = OperatorPosition;

        foreach (var side in new[] { transform.right, -transform.right })
        {
            var candidate = origin + side * detachSideStep;

            var bottom = candidate + Vector3.up * capsuleRadius;
            var top = candidate + Vector3.up * (capsuleHeight - capsuleRadius);

            if (!Physics.CheckCapsule(bottom, top, capsuleRadius * 0.9f,
                                      ~0, QueryTriggerInteraction.Ignore))
            {
                return candidate;
            }
        }

        return origin;
    }

    private void FixedUpdate()
    {
        // IsOwner : le serveur quand personne ne conduit, le conducteur sinon. Une seule
        // machine simule, jamais deux.
        if (!IsSpawned || !IsOwner || body == null) return;

        KeepUpright();

        if (!HasDriver)
        {
            // Chariot abandonne : on ne lui impose plus rien, la physique fait le reste.
            currentSpeed = 0f;
            driveInput = Vector2.zero;
            driveSprint = false;
            return;
        }

        Drive();
    }

    private void Drive()
    {
        // Avancer et reculer n'ont pas la meme vitesse : la marche arriere sert a se degager,
        // pas a se deplacer, et le sprint ne s'y applique pas.
        var throttle = Mathf.Clamp(driveInput.y, -1f, 1f);

        var topSpeed = throttle >= 0f
            ? (driveSprint ? pushSprintSpeed : pushWalkSpeed)
            : pushReverseSpeed;

        var targetSpeed = throttle * topSpeed;

        // Deux taux distincts : une masse lancee se relance moins vite qu'elle ne s'arrete,
        // ou l'inverse selon le reglage. C'est ce qui donne le poids du chariot.
        var rate = Mathf.Abs(targetSpeed) > Mathf.Abs(currentSpeed)
            ? pushAcceleration
            : pushDeceleration;

        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, rate * Time.fixedDeltaTime);

        // La reference est l'allure de marche : en sprint on a donc tout le braquage, pas
        // davantage — un chariot lance ne doit pas tourner comme une moto.
        var speedRatio = Mathf.Clamp01(Mathf.Abs(currentSpeed) / Mathf.Max(pushWalkSpeed, 0.01f));

        var degrees = Mathf.Lerp(pivotSpeed, turnSpeed, speedRatio);
        var omega = Mathf.Clamp(driveInput.x, -1f, 1f) * degrees * Mathf.Deg2Rad;

        // Le point de pivot glisse avec l'allure, comme un caddie a roues folles :
        //   - a l'arret, autour du **centre** — les deux bouts balaient autant, on tourne
        //     sur place sans avancer, et il faut juste la place de le faire ;
        //   - lance, autour de l'**essieu arriere** — le chariot braque par l'avant.
        //
        // Le Rigidbody, lui, tourne toujours autour de son centre de masse : on compense donc
        // par la vitesse lineaire qu'il faut pour que le point de pivot, lui, ne bouge pas.
        var axle = rearAxle != null ? rearAxle.position : body.worldCenterOfMass;
        var pivot = Vector3.Lerp(body.worldCenterOfMass, axle, speedRatio);

        var arm = body.worldCenterOfMass - pivot;
        arm.y = 0f;

        var spin = Vector3.Cross(new Vector3(0f, omega, 0f), arm);
        var velocity = transform.forward * currentSpeed + spin;

        // La verticale reste a la gravite : sans ca le chariot flotterait des la moindre bosse.
        velocity.y = body.linearVelocity.y;

        body.linearVelocity = velocity;
        body.angularVelocity = new Vector3(0f, omega, 0f);
    }

    /// <summary>
    /// Filet de securite : redresse le chariot s'il a reussi a s'incliner malgre les
    /// contraintes. Un chariot couche sur le flanc pour le reste de la partie est une
    /// situation dont le joueur ne peut pas se sortir.
    /// </summary>
    private void KeepUpright()
    {
        var euler = transform.eulerAngles;

        if (Mathf.Abs(Mathf.DeltaAngle(0f, euler.x)) < 0.5f
            && Mathf.Abs(Mathf.DeltaAngle(0f, euler.z)) < 0.5f)
        {
            return;
        }

        body.MoveRotation(Quaternion.RotateTowards(
            transform.rotation, Quaternion.Euler(0f, euler.y, 0f),
            uprightSpeed * Time.fixedDeltaTime));
    }

    // ---------- Bousculade du decor ----------

    /// <summary>
    /// Le chariot derange ce qu'il percute : valises au sol, ampoules, chaises, plantes.
    /// Jamais les murs — ceux-ci n'ont pas de Rigidbody, donc ils le bloquent, point.
    ///
    /// Le conducteur constate le contact et demande ; le serveur applique. Sa machine ne peut
    /// pas le faire elle-meme : les objets du monde y sont cinematiques, seul le serveur les
    /// simule. Meme schema que la bousculade par le joueur.
    ///
    /// Les futurs clients PNJ passeront par ce meme chemin, sans rien changer ici.
    /// </summary>
    private void OnCollisionEnter(Collision collision) => TryPush(collision);

    private void OnCollisionStay(Collision collision) => TryPush(collision);

    private void TryPush(Collision collision)
    {
        if (!IsSpawned || !IsOwner || !HasDriver) return;
        if (Time.time < nextPushTime) return;

        // Sans Rigidbody, c'est du decor fixe : un mur, un sol. Rien a bousculer.
        var otherBody = collision.rigidbody;
        if (otherBody == null) return;

        // Pas de test isKinematic : chez le conducteur, NetworkRigidbody rend cinematique
        // tout ce que le serveur simule. Ce test appartient au serveur.
        var target = otherBody.GetComponentInParent<NetworkObject>();
        if (target == null || target == NetworkObject) return;

        var speed = Mathf.Abs(currentSpeed);
        if (speed < minPushSpeed) return;

        var direction = otherBody.position - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;

        nextPushTime = Time.time + pushCooldown;

        RequestPushRpc(new NetworkObjectReference(target), direction.normalized, speed);
    }

    [Rpc(SendTo.Server)]
    private void RequestPushRpc(NetworkObjectReference targetRef, Vector3 direction, float speed)
    {
        if (!targetRef.TryGet(out var target) || target == null) return;

        // Le serveur ne fait confiance a rien : ni a la cible, ni a la distance, ni a la
        // vitesse annoncee.
        if (Vector3.Distance(target.transform.position, transform.position) > pushMaxDistance)
            return;

        if (!target.TryGetComponent<Rigidbody>(out var otherBody) || otherBody.isKinematic)
            return;

        var force = Mathf.Min(speed, pushSprintSpeed) * pushForce;

        otherBody.AddForce(direction.normalized * force, ForceMode.Impulse);
    }
}
