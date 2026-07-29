using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Ramassage et lacher d'objets, cote joueur.
///
/// Le client ne fait que demander : il vise, appuie sur E, et envoie une requete. C'est le
/// serveur qui verifie et decide. Aucune logique locale ne modifie l'etat d'un objet — sinon
/// deux joueurs pourraient ramasser la meme valise chacun de leur cote.
///
/// Une seule main aujourd'hui. Les RPC prennent deja un <see cref="HandSlot"/> pour que la
/// deuxieme main n'oblige pas a changer la signature ni le protocole reseau.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class PlayerCarry : NetworkBehaviour, ICarryAnchor
{
    public enum HandSlot
    {
        Right = 0,
        Left = 1,       // pas encore cablee
    }

    [Header("References")]
    [Tooltip("Point ou l'objet porte vient se placer. Enfant du pivot camera.")]
    [SerializeField] private Transform handAnchor;
    [Tooltip("Origine du rayon de visee : le pivot camera.")]
    [SerializeField] private Transform aimSource;

    [Header("Portee")]
    [SerializeField] private float reach = 2.5f;
    [Tooltip("Tolerance de la verification serveur. Plus large que la portee : le serveur " +
             "voit le joueur avec un peu de retard reseau.")]
    [SerializeField] private float serverMaxDistance = 4f;
    [SerializeField] private LayerMask interactMask = ~0;
    [Tooltip("Plafond applique a l'elan transmis a l'objet lache.")]
    [SerializeField] private float maxCarrierSpeed = 8f;

    [Header("Point de visee")]
    [SerializeField] private bool showCrosshair = true;
    [SerializeField] private float crosshairSize = 4f;
    [SerializeField] private Color crosshairIdle = new(1f, 1f, 1f, 0.5f);
    [SerializeField] private Color crosshairOnTarget = new(0.4f, 1f, 0.5f, 0.95f);

    private InputSystem_Actions input;
    private CharacterController controller;

    // Cible sous le viseur, reevaluee a chaque frame. On garde le NetworkObject a cote :
    // c'est lui qu'on envoie au serveur, une interface ne se serialise pas.
    private NetworkObject aimObject;
    private bool aimCanInteract;                 // E : prendre / retirer
    private bool aimCanUse;                      // clic gauche : visser / actionner
    private bool aimIsHeldInteraction;           // E se maintient au lieu de s'appuyer
    private float aimHoldProgress;               // avancement repliqué, pour la jauge

    // Cible que ce joueur maintient. Cote proprietaire seulement : c'est le serveur qui
    // fait foi, ceci ne sert qu'a savoir quand envoyer le message de fin.
    private NetworkObject holdTarget;

    // Cote serveur : ce que ce joueur maintient, pour pouvoir le relacher proprement s'il
    // se deconnecte en pleine reparation.
    private IInteractable serverHoldTarget;

    private Cart ghostCart;                      // chariot dont on affiche le repere de pose

    // Ce que porte ce joueur. Ecrit par le serveur, lu par tous : un autre client peut ainsi
    // savoir si ce joueur a les mains prises (utile pour les animations plus tard).
    private readonly NetworkVariable<NetworkObjectReference> heldItem = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Chariot pousse par ce joueur. Sur le joueur et non seulement sur le chariot : c'est
    // l'etat des mains, et chaque machine doit pouvoir le lire pour n'importe quel joueur.
    private readonly NetworkVariable<NetworkObjectReference> pushedCart = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public Transform HandAnchor => handAnchor;

    /// <summary>Ancre vue par un Carryable : la main de ce joueur.</summary>
    public Transform Anchor => handAnchor;

    /// <summary>Pousse-t-il un chariot ? Les deux mains sont alors prises.</summary>
    public bool IsPushingCart => TryGetCart(out _);

    /// <summary>Le joueur peut-il prendre quelque chose ? Une seule main pour l'instant.</summary>
    public bool HasFreeHand => Resolvable && !heldItem.Value.TryGet(out _) && !IsPushingCart;

    /// <summary>
    /// Les references reseau sont-elles resolvables ? TryGet passe par le NetworkManager, qui
    /// n'existe plus pendant l'arret de la partie — alors que la visee et l'interface, elles,
    /// continuent de tourner une frame ou deux.
    /// </summary>
    private bool Resolvable => IsSpawned && NetworkManager != null;

    /// <summary>
    /// Attenuation du balancement de camera : un joueur appuye sur une barre a la tete plus
    /// stable qu'un joueur mains nues.
    /// </summary>
    public float CameraBobFactor => TryGetCart(out var cart) ? cart.BobDamping : 1f;

    /// <summary>Secousse demandee par l'outil en main, de 0 a 1.</summary>
    public float HeldShake => TryGetTool(out var tool) ? tool.CameraShake : 0f;

    /// <summary>L'outil en main est-il en pleine action ? On ne le lache pas dans ce cas.</summary>
    public bool HeldToolBusy => TryGetTool(out var tool) && tool.IsBusy;

    /// <summary>
    /// Outil continu tenu en main, s'il y en a un. Un objet ordinaire n'en est pas un : la
    /// valise se pose au clic gauche, l'extincteur s'en sert.
    /// </summary>
    private bool TryGetTool(out IHandTool tool)
    {
        tool = null;

        return TryGetHeld(out var held) && held.TryGetComponent(out tool);
    }

    /// <summary>
    /// Chariot conduit par ce joueur, s'il y en a un. Le PlayerController s'en sert pour lui
    /// transmettre les commandes et en deduire sa propre position.
    /// </summary>
    public bool TryGetCart(out Cart cart)
    {
        cart = null;

        // TryGet passe par le NetworkManager : hors partie il n'existe plus et l'appel leve.
        if (!IsSpawned || NetworkManager == null) return false;

        return pushedCart.Value.TryGet(out var target)
               && target != null
               && target.TryGetComponent(out cart);
    }

    /// <summary>
    /// Met un objet dans la main de ce joueur. Serveur uniquement.
    /// Appele par les IInteractable — un Carryable ramasse au sol, une douille qui rend
    /// son ampoule — plutot que par le joueur lui-meme.
    /// </summary>
    public void ServerTake(Carryable carryable)
    {
        if (!IsServer || carryable == null || carryable.NetworkObject == null) return;

        carryable.ServerAttachTo(NetworkObject);
        heldItem.Value = new NetworkObjectReference(carryable.NetworkObject);
    }

    /// <summary>
    /// Oublie l'objet tenu sans le lacher physiquement : il part ailleurs, dans une douille
    /// ou un support, qui prend le relais. Serveur uniquement.
    /// </summary>
    public void ServerReleaseHand()
    {
        if (!IsServer) return;

        heldItem.Value = default;
    }

    public bool TryGetHeld(out Carryable carryable)
    {
        carryable = null;

        if (!Resolvable) return false;

        if (!heldItem.Value.TryGet(out var netObj) || netObj == null)
            return false;

        carryable = netObj.GetComponent<Carryable>();
        return carryable != null;
    }

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
    }

    public override void OnNetworkSpawn()
    {
        pushedCart.OnValueChanged += OnPushedCartChanged;

        // Un client qui rejoint doit trouver un conducteur deja en place dans le bon etat.
        ApplyCartCollision(default, pushedCart.Value);

        if (!IsOwner) return;

        input = new InputSystem_Actions();
        input.Player.Interact.Enable();
        input.Player.Attack.Enable();
    }

    /// <summary>
    /// Met un chariot dans les mains de ce joueur, ou l'en retire. Serveur uniquement :
    /// appele par <see cref="Cart"/> apres validation.
    /// </summary>
    public void ServerSetPushedCart(NetworkObject cart)
    {
        if (!IsServer) return;

        pushedCart.Value = cart != null ? new NetworkObjectReference(cart) : default;
    }

    private void OnPushedCartChanged(NetworkObjectReference previous, NetworkObjectReference current)
        => ApplyCartCollision(previous, current);

    /// <summary>
    /// Le conducteur et son chariot cessent de se percuter le temps de la conduite.
    ///
    /// Sans ca, la boite qui entoure la place du conducteur chevauche en permanence sa
    /// capsule. Un CharacterController ne pouvant pas etre pousse par un Rigidbody, c'est le
    /// **chariot** qui encaisse l'impulsion de separation, a chaque pas de physique : il est
    /// maintenu en l'air des que le sol se derobe, et sursaute en permanence. La gravite ne
    /// pouvait plus faire son travail.
    ///
    /// La boite retrouve ainsi son seul role : bloquer contre le decor. Le joueur ne risque
    /// pas de traverser le chariot pour autant — il ne se deplace plus par lui-meme, sa
    /// position est deduite de celle du chariot.
    ///
    /// Purement local, aucun etat reseau : la physique se calcule sur chaque machine, donc
    /// chacune applique l'exception de son cote.
    /// </summary>
    private void ApplyCartCollision(NetworkObjectReference previous, NetworkObjectReference current)
    {
        if (controller == null || NetworkManager == null) return;

        if (previous.TryGet(out var old) && old != null)
            SetCartCollisionIgnored(old, false);

        if (current.TryGet(out var now) && now != null)
            SetCartCollisionIgnored(now, true);
    }

    private void SetCartCollisionIgnored(NetworkObject cart, bool ignored)
    {
        foreach (var col in cart.GetComponentsInChildren<Collider>(true))
        {
            if (col.isTrigger) continue;

            Physics.IgnoreCollision(controller, col, ignored);
        }
    }

    /// <summary>
    /// Cet objet est-il le chariot que ce joueur pousse ? Sert au PlayerController a ne pas
    /// bousculer son propre chariot : il le bloque physiquement, mais ne lui envoie pas de
    /// poussee par-dessus sa conduite.
    /// </summary>
    public bool IsPushing(NetworkObject candidate)
    {
        return candidate != null
               && TryGetCart(out var cart)
               && cart.NetworkObject == candidate;
    }

    public override void OnNetworkDespawn()
    {
        ReleaseInput();
        holdTarget = null;
        RefreshPlacementGhost(null);

        pushedCart.OnValueChanged -= OnPushedCartChanged;
        ApplyCartCollision(pushedCart.Value, default);

        // Un joueur qui se deconnecte ne doit rien emporter dans le vide : ni la valise, ni
        // le generateur qu'il reparait, ni le chariot qu'il poussait.
        if (!IsServer) return;

        ServerEndHold();

        // Resolution directe et non via TryGetCart : celui-ci exige IsSpawned, qui n'est
        // deja plus fiable pendant un despawn.
        if (pushedCart.Value.TryGet(out var cartObject) && cartObject != null
            && cartObject.TryGetComponent<Cart>(out var cart))
        {
            cart.ServerRelease(this);
        }

        if (TryGetHeld(out var carried))
            carried.ServerDetach(transform);
    }

    // override, pas une nouvelle methode : NetworkBehaviour.OnDestroy() desenregistre le
    // composant aupres de son NetworkObject. Le masquer laisse NGO avec des references
    // mortes, d'ou des NullReference a la fermeture.
    public override void OnDestroy()
    {
        ReleaseInput();
        base.OnDestroy();
    }

    private void ReleaseInput()
    {
        if (input == null) return;

        input.Player.Interact.Disable();
        input.Player.Attack.Disable();
        input.Dispose();
        input = null;
    }

    private void Update()
    {
        if (!IsOwner || input == null) return;

        // Visee evaluee a chaque frame : elle sert a l'interaction comme au point de visee.
        RefreshAim();

        // Un maintien en cours monopolise la touche : tant qu'on repare, E ne ramasse ni
        // ne repose quoi que ce soit.
        if (UpdateHold()) return;

        // Clic gauche : appliquer ce qu'on tient. Prioritaire sur le reste, sinon visser
        // une ampoule reviendrait a la lacher par terre.
        //
        // Un outil continu — l'extincteur — partage cette touche, et c'est la DISTANCE qui
        // departage : aimCanUse n'est vrai que sous sa portee de pose, tres courte. Colle a
        // un chariot on y depose l'extincteur, partout ailleurs on s'en sert.
        if (input.Player.Attack.WasPressedThisFrame() && aimObject != null && aimCanUse
            && !HeldToolBusy)
        {
            RequestUseServerRpc(new NetworkObjectReference(aimObject));
            return;
        }

        UpdateHandTool();

        if (!input.Player.Interact.WasPressedThisFrame()) return;

        // Aux poignees, E sert a lacher. Rien d'autre n'est possible : les deux mains sont
        // prises, donc HasFreeHand est deja faux partout ailleurs.
        if (IsPushingCart)
        {
            StepAsideFromCart();
            RequestCartReleaseServerRpc();
            return;
        }

        // On ne lache pas un extincteur en pleine action : il atterrirait au sol en continuant
        // d'arroser le plafond.
        if (HeldToolBusy) return;

        // Mains pleines : E repose. Mains libres : E declenche l'interaction visee.
        if (!HasFreeHand)
        {
            // On transmet notre elan : lachee en pleine course, la valise doit glisser
            // devant nous et non tomber sur place.
            var carrierVelocity = controller != null ? controller.velocity : Vector3.zero;
            RequestDropServerRpc(HandSlot.Right, carrierVelocity);
            return;
        }

        if (aimObject != null && aimCanInteract)
            RequestInteractServerRpc(new NetworkObjectReference(aimObject));
    }

    /// <summary>
    /// Transmet a l'outil en main que le clic gauche est maintenu ou relache.
    ///
    /// On ne decide de rien ici : l'outil recoit l'intention et fait suivre au serveur ce qui
    /// doit etre repliqué. C'est ce qui fait que les autres joueurs voient le jet.
    /// </summary>
    private void UpdateHandTool()
    {
        if (!TryGetTool(out var tool)) return;

        tool.SetUsing(input.Player.Attack.IsPressed());
    }

    /// <summary>
    /// Gere le debut et la fin d'une interaction maintenue. Retourne vrai si cette frame
    /// appartient a un maintien — auquel cas E ne doit rien declencher d'autre.
    ///
    /// Le client n'avance aucune progression : il signale seulement qu'il commence et qu'il
    /// arrete. Le compte se fait sur le serveur, sinon deux joueurs qui reparent ensemble
    /// tiendraient chacun leur propre jauge.
    /// </summary>
    private bool UpdateHold()
    {
        if (holdTarget != null)
        {
            // On arrete des que l'une des trois conditions tombe : touche relachee, regard
            // detourne, ou action devenue impossible (le generateur vient de repartir).
            var stillHolding = input.Player.Interact.IsPressed()
                               && aimObject == holdTarget
                               && aimCanInteract;

            if (!stillHolding)
            {
                holdTarget = null;
                RequestHoldEndRpc();
            }

            return true;
        }

        if (!input.Player.Interact.WasPressedThisFrame()) return false;

        // On ne verifie plus les mains libres ici : certaines actions maintenues exigent au
        // contraire un objet en main, comme reparer une fuite avec la cle. C'est a la cible
        // de dire ce qu'il lui faut, et CanInteract l'a deja tranche. Celles qui veulent des
        // mains nues — le generateur, la vanne — le demandent dans leur propre CanInteract.
        //
        // Si aucune action maintenue n'est possible, on retombe sur le comportement normal :
        // E repose ce qu'on tient.
        if (aimObject == null || !aimIsHeldInteraction || !aimCanInteract) return false;

        holdTarget = aimObject;
        RequestHoldBeginRpc(new NetworkObjectReference(holdTarget));

        return true;
    }

    /// <summary>
    /// Cherche un element interactif dans l'axe du regard, a portee de bras : une valise au
    /// sol, une douille, et demain une porte ou un generateur.
    /// </summary>
    private void RefreshAim()
    {
        aimObject = null;
        aimCanInteract = false;
        aimCanUse = false;
        aimIsHeldInteraction = false;
        aimHoldProgress = 0f;

        // Eteint d'abord : la visee sort de cette methode par plusieurs chemins, et un repere
        // oublie resterait allume dans notre dos.
        RefreshPlacementGhost(null);

        var origin = aimSource != null ? aimSource : transform;

        if (!Physics.Raycast(origin.position, origin.forward, out var hit, reach,
                             interactMask, QueryTriggerInteraction.Ignore))
            return;

        // GetComponentInParent : on touche presque toujours un morceau du mesh, pas la racine.
        var owner = hit.collider.GetComponentInParent<NetworkObject>();
        if (owner == null) return;

        var interactable = Resolve(owner, this);
        if (interactable == null) return;

        // Les deux actions sont evaluees separement : viser une douille vide avec une ampoule
        // en main n'autorise pas E, mais autorise le clic gauche.
        aimObject = owner;
        aimCanInteract = interactable.CanInteract(this);
        aimCanUse = interactable.CanUse(this);

        // Un outil continu raccourcit la portee de pose : c'est ce qui permet au clic gauche
        // de servir a deux choses sans les confondre. Sans ca, vouloir arroser a deux metres
        // d'un chariot y deposerait l'extincteur.
        if (aimCanUse && TryGetTool(out var tool) && hit.distance > tool.UseReach)
            aimCanUse = false;

        aimIsHeldInteraction = interactable.IsHeldInteraction;
        aimHoldProgress = interactable.HoldProgress;

        RefreshPlacementGhost(interactable as Cart);
    }

    /// <summary>
    /// Montre le repere de pose sur le chariot vise, tant qu'on tient quelque chose.
    ///
    /// Pilote depuis le joueur et non depuis le chariot : lui seul sait ou l'on regarde. Et
    /// on garde le chariot precedent sous la main pour eteindre son repere des qu'on detourne
    /// les yeux, sinon il resterait allume derriere nous.
    /// </summary>
    private void RefreshPlacementGhost(Cart aimedCart)
    {
        var wanted = aimedCart != null && TryGetHeld(out _) ? aimedCart : null;

        if (ghostCart != null && ghostCart != wanted)
            ghostCart.ShowPlacementGhost(false);

        ghostCart = wanted;

        if (ghostCart != null)
            ghostCart.ShowPlacementGhost(true);
    }

    /// <summary>
    /// Choisit l'interaction a proposer parmi celles que porte l'objet.
    ///
    /// Un meme objet peut en avoir plusieurs : un evier est a la fois un Carryable qu'on
    /// decroche et un Sink qu'on repare. Prendre le premier composant venu donnerait un
    /// resultat arbitraire — et selon l'ordre d'ajout dans le prefab, la reparation serait
    /// tout simplement inatteignable.
    ///
    /// On retient donc **la premiere qui peut reellement agir**. Comme l'etat dont depend
    /// CanInteract est repliqué, le client et le serveur choisissent la meme.
    ///
    /// GetComponents et non GetComponentsInChildren : l'ampoule vissee est un enfant de la
    /// douille et implemente elle aussi l'interface. On veut la cible visee, pas son contenu.
    /// </summary>
    private static IInteractable Resolve(NetworkObject target, PlayerCarry player)
    {
        var candidates = target.GetComponents<IInteractable>();
        if (candidates.Length == 0) return null;

        foreach (var candidate in candidates)
        {
            if (candidate.CanInteract(player) || candidate.CanUse(player))
                return candidate;
        }

        // Aucune n'est disponible : on renvoie la premiere quand meme, pour que le viseur
        // sache qu'il y a bien quelque chose la — simplement rien a en faire pour l'instant.
        return candidates[0];
    }

    /// <summary>
    /// Point de visee minimaliste, en IMGUI comme le reste de l'interface provisoire.
    /// Il change de couleur quand une cible est atteignable : sans ce retour, on ne sait
    /// pas si on rate l'objet ou si l'action a echoue.
    /// </summary>
    private void OnGUI()
    {
        // IsSpawned et NetworkManager : l'interface continue de se dessiner pendant l'arret
        // de la partie, alors que TryGet a besoin du NetworkManager pour resoudre sa
        // reference. C'est le meme garde-fou que dans Carryable.
        if (!IsOwner || !showCrosshair || !IsSpawned || NetworkManager == null) return;

        var onTarget = aimCanInteract || aimCanUse || TryGetHeld(out _);
        var size = onTarget ? crosshairSize * 1.75f : crosshairSize;

        var rect = new Rect(
            (Screen.width - size) * 0.5f,
            (Screen.height - size) * 0.5f,
            size, size);

        var previous = GUI.color;
        GUI.color = onTarget ? crosshairOnTarget : crosshairIdle;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;

        // Une action maintenue sans retour visuel est injouable : on ne sait pas si ca
        // avance, ni combien il reste.
        if (aimIsHeldInteraction && aimCanInteract)
            DrawHoldBar(aimHoldProgress);
    }

    /// <summary>
    /// Jauge de progression sous le viseur. Elle affiche une valeur repliquee : quand un
    /// collegue repare le meme generateur, on voit sa jauge monter sans le toucher.
    /// </summary>
    private void DrawHoldBar(float progress)
    {
        const float width = 140f;
        const float height = 6f;

        var x = (Screen.width - width) * 0.5f;
        var y = Screen.height * 0.5f + 24f;

        var previous = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);

        GUI.color = crosshairOnTarget;
        GUI.DrawTexture(new Rect(x, y, width * Mathf.Clamp01(progress), height), Texture2D.whiteTexture);

        GUI.color = previous;
    }

    // ---------- Requetes au serveur ----------

    [Rpc(SendTo.Server)]
    private void RequestInteractServerRpc(NetworkObjectReference targetRef)
    {
        // Rien de ce qui suit ne fait confiance au client : il a pu mentir, ou l'etat a pu
        // changer entre sa demande et son arrivee ici.
        if (!targetRef.TryGet(out var targetObject)) return;   // cible disparue entre-temps

        // On verifie la distance, pas la visee : le serveur ne connait pas l'angle de la
        // camera du client (le pitch reste local), donc rejouer le raycast serait faux.
        if (Vector3.Distance(transform.position, targetObject.transform.position) > serverMaxDistance)
            return;

        var interactable = Resolve(targetObject, this);

        // CanInteract est reevalue ici : entre la demande et son arrivee, un autre joueur a
        // pu prendre l'objet ou vider la douille.
        if (interactable == null || !interactable.CanInteract(this)) return;

        interactable.ServerInteract(this);
    }

    [Rpc(SendTo.Server)]
    private void RequestUseServerRpc(NetworkObjectReference targetRef)
    {
        if (!targetRef.TryGet(out var targetObject)) return;

        if (Vector3.Distance(transform.position, targetObject.transform.position) > serverMaxDistance)
            return;

        var interactable = Resolve(targetObject, this);

        // Reevalue ici : entre la demande et son arrivee, un autre joueur a pu visser une
        // ampoule dans cette douille.
        if (interactable == null || !interactable.CanUse(this)) return;

        interactable.ServerUse(this);
    }

    [Rpc(SendTo.Server)]
    private void RequestHoldBeginRpc(NetworkObjectReference targetRef)
    {
        if (!targetRef.TryGet(out var targetObject)) return;

        if (Vector3.Distance(transform.position, targetObject.transform.position) > serverMaxDistance)
            return;

        var interactable = Resolve(targetObject, this);

        if (interactable == null || !interactable.IsHeldInteraction || !interactable.CanInteract(this))
            return;

        // Un joueur ne maintient qu'une chose a la fois : s'il en tenait deja une, elle est
        // relachee proprement plutot que laissee inscrite dessus.
        ServerEndHold();

        serverHoldTarget = interactable;
        interactable.ServerHoldBegin(this);
    }

    [Rpc(SendTo.Server)]
    private void RequestHoldEndRpc()
    {
        ServerEndHold();
    }

    /// <summary>
    /// Repose le joueur a cote de la barre en lachant, plutot que de le laisser dans le
    /// volume du chariot. C'est le proprietaire qui le fait : sa position lui appartient.
    ///
    /// Aucune teleportation — on passe par le CharacterController, donc le decor s'applique
    /// et un cote encombre ne fait rien traverser. Le chariot, lui, ne bouge pas.
    /// </summary>
    private void StepAsideFromCart()
    {
        if (controller == null || !TryGetCart(out var cart)) return;

        var target = cart.DetachPosition(controller.radius, controller.height);
        var delta = target - transform.position;
        delta.y = 0f;

        controller.Move(delta);
    }

    [Rpc(SendTo.Server)]
    private void RequestCartReleaseServerRpc()
    {
        // Aucune distance a verifier : on ne fait que lacher ce qu'on tenait deja.
        if (TryGetCart(out var cart))
            cart.ServerRelease(this);
    }

    /// <summary>Retire ce joueur de l'action qu'il maintenait, quelle qu'en soit la raison.</summary>
    private void ServerEndHold()
    {
        if (!IsServer || serverHoldTarget == null) return;

        serverHoldTarget.ServerHoldEnd(this);
        serverHoldTarget = null;
    }

    [Rpc(SendTo.Server)]
    private void RequestDropServerRpc(HandSlot slot, Vector3 carrierVelocity)
    {
        if (!TryGetHeld(out var carried)) return;

        // Vitesse bornee : au pire le joueur lache comme s'il sprintait, jamais plus.
        carrierVelocity = Vector3.ClampMagnitude(carrierVelocity, maxCarrierSpeed);

        carried.ServerDetach(aimSource != null ? aimSource : transform, carrierVelocity);
        heldItem.Value = default;
    }
}
