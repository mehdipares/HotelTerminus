using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Objet manipulable en reseau : la valise aujourd'hui, le chariot, les cadavres et le
/// generateur ensuite. Rien ici n'est specifique a la valise.
///
/// Autorite serveur stricte : lui seul attache, detache et simule la physique.
/// <see cref="attachedTo"/> est la source de verite unique ; chaque client en deduit son
/// etat local (colliders, physique, position dans la main) sans jamais decider quoi que ce
/// soit.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public class Carryable : NetworkBehaviour, IInteractable
{
    [Tooltip("Point par lequel l'objet est tenu : il vient se coller sur la main du porteur. " +
             "Vide = l'origine de l'objet. Sur la valise, c'est la poignee.")]
    [SerializeField] private Transform gripPoint;

    [Tooltip("Point par lequel l'objet se POSE dans un receptacle — plateau de chariot, " +
             "etagere. Vide = on reutilise le Grip Point.\n\n" +
             "On tient une valise par sa poignee, mais on la pose sur son flanc : aligner " +
             "un objet range par son point de prise le laisse debout et a moitie enfonce.")]
    [SerializeField] private Transform stowPoint;

    [Header("Identification")]
    [Tooltip("Associe l'objet a une destination precise (la bonne valise dans la bonne " +
             "chambre). Vide = accepte partout. Pas encore exploite.")]
    [SerializeField] private string itemId;

    [Header("Lacher")]
    [SerializeField] private float dropForwardOffset = 0.7f;
    [SerializeField] private float dropUpOffset = 0.2f;
    [Tooltip("Poussee vers l'avant ajoutee a l'elan du porteur.")]
    [SerializeField] private float dropImpulse = 1.2f;
    [Range(0f, 1f)]
    [Tooltip("Part de la vitesse du porteur transmise a l'objet. 1 = l'objet part avec toute " +
             "la course du joueur et glisse devant lui.")]
    [SerializeField] private float carrierVelocityTransfer = 0.9f;
    [Tooltip("Rotation donnee au lacher : sans elle, l'objet reste plante bien droit.")]
    [SerializeField] private float dropSpin = 1.5f;
    [Tooltip("Vitesse maximale au lacher. Laisse passer l'elan du sprint — jeter un objet en " +
             "courant fait partie du jeu — sans l'expedier a l'autre bout du couloir. " +
             "A regler par objet : une ampoule de 200 g part bien plus loin qu'une valise.")]
    [SerializeField] private float maxDropSpeed = 5.5f;

    // Ce a quoi l'objet est rattache : la main d'un joueur ou un receptacle. Les deux cas
    // partagent la meme variable parce que les deux implementent ICarryAnchor.
    //
    // On reference le NetworkObject et non un clientId : dans NGO, un client ne peut pas
    // retrouver l'avatar d'un *autre* joueur a partir de son identifiant. Passer par le
    // clientId rendait les objets portes invisibles pour tout le monde sauf leur porteur.
    //
    // Lisible par tous, ecrivable par le serveur uniquement : la regle du projet.
    private readonly NetworkVariable<NetworkObjectReference> attachedTo = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private Rigidbody body;
    private Collider[] colliders;
    private NetworkTransform netTransform;
    private Transform anchor;                    // main ou receptacle, resolu localement
    private bool stowed;                         // pose dans un receptacle, et non tenu
    private Vector3 baseScale;                   // taille reelle voulue, quelle que soit l'ancre

    public string ItemId => itemId;
    public Transform Grip => gripPoint != null ? gripPoint : transform;

    /// <summary>Point de pose. A defaut, on retombe sur le point de prise.</summary>
    public Transform Stow => stowPoint != null ? stowPoint : Grip;

    /// <summary>Vrai des que l'objet est rattache a quelque chose : main ou receptacle.</summary>
    public bool IsAttached => TryGetAttachment(out _);

    /// <summary>Tenu par un joueur (par opposition a pose dans un receptacle).</summary>
    public bool IsHeld => TryGetAttachment(out var target)
                          && target.GetComponent<PlayerCarry>() != null;

    /// <summary>Pose dans un receptacle (douille, support). Ni libre, ni dans une main.</summary>
    public bool IsSocketed => IsAttached && !IsHeld;

    private bool TryGetAttachment(out NetworkObject target)
    {
        target = null;

        // TryGet passe par le NetworkManager pour resoudre l'identifiant : hors partie —
        // avant le spawn ou apres l'arret — il n'existe plus et l'appel leve.
        if (!IsSpawned || NetworkManager == null) return false;

        return attachedTo.Value.TryGet(out target) && target != null;
    }

    // TODO satisfaction client : c'est ici que viendra le comptage des mauvais traitements
    // (OnCollisionEnter cote serveur, au-dela d'un seuil d'impulsion). La DeliveryZone
    // pourra alors noter la livraison au lieu de la valider betement. Rien n'est compte
    // aujourd'hui, on ne stocke aucun etat inutile.

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        colliders = GetComponentsInChildren<Collider>(true);
        netTransform = GetComponent<NetworkTransform>();

        // Taille reelle de l'objet, capturee avant tout parentage : elle sert de reference
        // pour ne pas se laisser deformer par l'echelle d'un porteur ou d'un receptacle.
        baseScale = transform.localScale;
    }

    public override void OnNetworkSpawn()
    {
        attachedTo.OnValueChanged += OnAttachmentChanged;

        // Un client qui rejoint en cours de partie doit retrouver l'objet la ou il est :
        // dans une main, visse dans une douille, ou par terre. On applique l'etat courant,
        // pas seulement les changements a venir.
        ApplyAttachment();
    }

    public override void OnNetworkDespawn()
    {
        attachedTo.OnValueChanged -= OnAttachmentChanged;
    }

    private void OnAttachmentChanged(NetworkObjectReference previous, NetworkObjectReference current)
        => ApplyAttachment();

    /// <summary>
    /// Recalcule l'ancre et l'etat local.
    ///
    /// Appele par un receptacle a plusieurs places — le plateau d'un chariot — quand
    /// l'emplacement attribue change. L'attachement et l'emplacement voyagent dans deux
    /// messages distincts : sans ce rappel, un bagage arrive avant sa place resterait colle
    /// a la premiere du plateau.
    /// </summary>
    public void RefreshAttachment()
    {
        if (!IsSpawned) return;

        ApplyAttachment();
    }

    // ---------- Interaction ----------

    /// <summary>Ramassable si libre et si le joueur a une main disponible.</summary>
    public bool CanInteract(PlayerCarry player)
    {
        return !IsAttached && player != null && player.HasFreeHand;
    }

    public void ServerInteract(PlayerCarry player)
    {
        if (!IsServer || player == null) return;

        player.ServerTake(this);
    }

    // ---------- Ordres serveur ----------

    /// <summary>
    /// Attache l'objet a un joueur. Serveur uniquement : c'est le <see cref="PlayerCarry"/>
    /// du porteur qui appelle, apres validation.
    /// </summary>
    public void ServerAttachTo(NetworkObject carrier)
    {
        if (!IsServer || carrier == null) return;

        // Le parentage passe par NGO : la hierarchie est repliquee a tous les clients,
        // l'objet suit donc le joueur sans qu'on envoie une position par frame.
        NetworkObject.TrySetParent(carrier, false);
        attachedTo.Value = new NetworkObjectReference(carrier);
    }

    /// <summary>
    /// Pose l'objet dans un receptacle (une douille) plutot que dans une main.
    /// L'objet n'est tenu par personne mais reste indisponible : ses colliders sont coupes,
    /// donc on ne peut pas le ramasser en le visant — l'interaction passe par le receptacle.
    /// Serveur uniquement.
    /// </summary>
    public void ServerAttachToSocket(NetworkObject receptacle)
    {
        if (!IsServer || receptacle == null) return;

        NetworkObject.TrySetParent(receptacle, false);
        attachedTo.Value = new NetworkObjectReference(receptacle);
    }

    /// <summary>
    /// Detache l'objet et lui rend sa physique. Serveur uniquement.
    /// <paramref name="carrierVelocity"/> est la vitesse du porteur au moment du lacher :
    /// c'est elle qui fait glisser la valise devant un joueur qui court.
    /// </summary>
    public void ServerDetach(Transform dropOrigin, Vector3 carrierVelocity = default)
    {
        if (!IsServer) return;

        NetworkObject.TryRemoveParent(true);
        attachedTo.Value = default;

        if (dropOrigin == null) return;

        // Direction horizontale : le forward de la camera contient l'angle de vue vertical.
        // Lacher un objet en regardant le plafond l'expedierait a l'etage du dessus.
        var forward = Vector3.ProjectOnPlane(dropOrigin.forward, Vector3.up);
        forward = forward.sqrMagnitude > 0.001f ? forward.normalized : dropOrigin.right;

        // On repose l'objet devant le joueur plutot qu'a l'endroit exact de la main :
        // sinon il naitrait dans sa capsule de collision et serait ejecte n'importe ou.
        var target = dropOrigin.position
                     + forward * dropForwardOffset
                     + Vector3.up * dropUpOffset;

        transform.position += target - Grip.position;

        // On ne redresse pas l'objet : il garde l'inclinaison qu'il avait en main et finit
        // de basculer tout seul. Reposer une valise parfaitement d'aplomb la laisse plantee
        // comme un piquet, ce qui trahit immediatement le code derriere.
        var velocity = carrierVelocity * carrierVelocityTransfer + forward * dropImpulse;

        // Aucune composante ascendante : on pose ou on laisse tomber, on ne lance jamais.
        velocity.y = Mathf.Min(velocity.y, 0f);

        body.linearVelocity = Vector3.ClampMagnitude(velocity, maxDropSpeed);
        body.angularVelocity = UnityEngine.Random.insideUnitSphere * dropSpin;
    }

    /// <summary>
    /// Detache l'objet **sur place** et lui donne une vitesse. Serveur uniquement.
    ///
    /// Different de <see cref="ServerDetach"/>, qui repositionne l'objet devant celui qui le
    /// lache : un chargement renverse doit partir d'ou il etait, sinon toute la pile se
    /// recentrerait au meme endroit avant de tomber.
    /// </summary>
    public void ServerSpill(Vector3 velocity)
    {
        if (!IsServer) return;

        NetworkObject.TryRemoveParent(true);
        attachedTo.Value = default;

        // Apres le detachement : l'ecriture ci-dessus a rendu le corps non cinematique.
        body.linearVelocity = velocity;
        body.angularVelocity = UnityEngine.Random.insideUnitSphere * dropSpin;
    }

    // ---------- Etat local, deduit du serveur ----------

    private void ApplyAttachment()
    {
        // L'etat repliqué fait foi, pas la resolution de l'ancre : chez un client qui
        // rejoint, l'objet porte arrive parfois avant l'avatar de son porteur. Deduire
        // "libre" d'une ancre introuvable rendrait l'objet physique chez lui seul.
        var attached = IsAttached;

        // Retenu ici plutot que teste a chaque frame dans SnapToAnchor : savoir si l'on est
        // dans une main ou dans un receptacle demande de resoudre une reference reseau.
        stowed = attached && !IsHeld;

        anchor = attached ? ResolveAnchor() : null;

        // Colliders coupes : un objet tenu ne doit pas percuter son porteur, et un objet
        // visse ne doit pas etre ramassable en le visant — on passe par la douille.
        foreach (var col in colliders)
            col.enabled = !attached;

        body.isKinematic = attached;

        // Tant que l'objet est rattache, sa position se deduit de son ancre chez chaque
        // client. La repliquer en plus serait du trafic inutile, et les deux se
        // marcheraient dessus.
        if (netTransform != null)
            netTransform.enabled = !attached;

        RestoreScale();

        if (attached)
            SnapToAnchor();
    }

    /// <summary>
    /// Annule l'echelle du parent pour que l'objet garde sa taille reelle.
    /// Sans ca, une ampoule a l'echelle 0.001 vissee dans une douille a l'echelle 0.1
    /// se retrouve mille fois trop petite — et une echelle non uniforme l'ecraserait.
    /// </summary>
    private void RestoreScale()
    {
        var parent = transform.parent;

        if (parent == null)
        {
            transform.localScale = baseScale;
            return;
        }

        var parentScale = parent.lossyScale;

        transform.localScale = new Vector3(
            Mathf.Approximately(parentScale.x, 0f) ? baseScale.x : baseScale.x / parentScale.x,
            Mathf.Approximately(parentScale.y, 0f) ? baseScale.y : baseScale.y / parentScale.y,
            Mathf.Approximately(parentScale.z, 0f) ? baseScale.z : baseScale.z / parentScale.z);
    }

    /// <summary>
    /// Retrouve l'ancre courante : la main du porteur, ou le receptacle dans lequel l'objet
    /// est pose. Le Carryable ne sait pas lequel des deux le tient, il suit un ICarryAnchor.
    /// </summary>
    private Transform ResolveAnchor()
    {
        if (!TryGetAttachment(out var target)) return null;

        // AnchorFor et non Anchor : un receptacle a plusieurs places — le plateau d'un
        // chariot — doit pouvoir repondre celle qui nous est reservee.
        if (!target.TryGetComponent<ICarryAnchor>(out var provider)) return target.transform;

        return provider.AnchorFor(this) != null ? provider.AnchorFor(this) : target.transform;
    }

    private void LateUpdate()
    {
        // Resolution differee : si le porteur n'existait pas encore au moment ou l'etat est
        // arrive, on retente jusqu'a le trouver. C'est le cas d'un joueur qui rejoint une
        // partie ou quelqu'un tient deja un objet.
        if (anchor == null && IsAttached)
        {
            anchor = ResolveAnchor();

            if (anchor != null)
                RestoreScale();
        }

        // LateUpdate : la camera et le joueur ont fini de bouger, l'objet ne traine donc
        // pas d'une frame derriere la main.
        if (anchor != null)
            SnapToAnchor();
    }

    /// <summary>
    /// Amene le <see cref="Grip"/> exactement sur la main du porteur. On raisonne sur le
    /// point de prise et non sur l'origine de l'objet : le mesh d'une valise importee est
    /// rarement centre sur son pivot.
    /// </summary>
    private void SnapToAnchor()
    {
        // Tenu, on s'aligne par la poignee ; pose, par le point de pose. Une valise ne se
        // range pas comme elle se porte.
        var reference = stowed ? Stow : Grip;

        // Rotation d'abord : elle deplace le point de reference, donc la position se calcule
        // apres.
        var localRotation = Quaternion.Inverse(transform.rotation) * reference.rotation;
        transform.rotation = anchor.rotation * Quaternion.Inverse(localRotation);

        transform.position += anchor.position - reference.position;
    }
}
