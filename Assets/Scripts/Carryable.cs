using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Objet manipulable en reseau : la valise aujourd'hui, le chariot, les cadavres et le
/// generateur ensuite. Rien ici n'est specifique a la valise.
///
/// Autorite serveur stricte : lui seul attache, detache et simule la physique.
/// <see cref="holder"/> est la source de verite unique ; chaque client en deduit son etat
/// local (colliders, physique, position dans la main) sans jamais decider quoi que ce soit.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public class Carryable : NetworkBehaviour, IInteractable
{
    /// <summary>Valeur de <see cref="holder"/> quand personne ne porte l'objet.</summary>
    public const ulong NoHolder = ulong.MaxValue;

    [Tooltip("Point par lequel l'objet est tenu : il vient se coller sur la main du porteur. " +
             "Vide = l'origine de l'objet. Sur la valise, c'est la poignee.")]
    [SerializeField] private Transform gripPoint;

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

    // Lisible par tous, ecrivable par le serveur uniquement : la regle du projet.
    private readonly NetworkVariable<ulong> holder = new(
        NoHolder,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Receptacle dans lequel l'objet est pose (une douille aujourd'hui). Distinct de holder :
    // un objet visse n'est tenu par personne, mais il n'est pas libre pour autant.
    private readonly NetworkVariable<NetworkObjectReference> socket = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private Rigidbody body;
    private Collider[] colliders;
    private NetworkTransform netTransform;
    private Transform anchor;                    // main ou receptacle, resolu localement
    private Vector3 baseScale;                   // taille reelle voulue, quelle que soit l'ancre

    public bool IsHeld => holder.Value != NoHolder;
    public ulong HolderClientId => holder.Value;
    public string ItemId => itemId;
    public Transform Grip => gripPoint != null ? gripPoint : transform;

    /// <summary>Pose dans un receptacle (douille, support). Ni libre, ni dans une main.</summary>
    public bool IsSocketed => socket.Value.TryGet(out var target) && target != null;

    /// <summary>Vrai des que l'objet est rattache a quelque chose : main ou receptacle.</summary>
    public bool IsAttached => IsHeld || IsSocketed;

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
        holder.OnValueChanged += OnHolderChanged;
        socket.OnValueChanged += OnSocketChanged;

        // Un client qui rejoint en cours de partie doit retrouver l'objet la ou il est :
        // dans une main, visse dans une douille, ou par terre. On applique l'etat courant,
        // pas seulement les changements a venir.
        ApplyAttachment();
    }

    public override void OnNetworkDespawn()
    {
        holder.OnValueChanged -= OnHolderChanged;
        socket.OnValueChanged -= OnSocketChanged;
    }

    private void OnHolderChanged(ulong previous, ulong current) => ApplyAttachment();

    private void OnSocketChanged(NetworkObjectReference previous, NetworkObjectReference current)
        => ApplyAttachment();

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
        socket.Value = default;                  // on quitte le receptacle s'il y en avait un
        holder.Value = carrier.OwnerClientId;
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
        holder.Value = NoHolder;
        socket.Value = new NetworkObjectReference(receptacle);
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
        holder.Value = NoHolder;
        socket.Value = default;

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

    // ---------- Etat local, deduit du serveur ----------

    private void ApplyAttachment()
    {
        anchor = ResolveAnchor();

        var attached = anchor != null;

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
        if (holder.Value != NoHolder)
        {
            var playerObject = NetworkManager.SpawnManager.GetPlayerNetworkObject(holder.Value);
            return playerObject != null ? AnchorOf(playerObject.gameObject) : null;
        }

        if (socket.Value.TryGet(out var receptacle) && receptacle != null)
            return AnchorOf(receptacle.gameObject);

        return null;
    }

    private static Transform AnchorOf(GameObject source)
    {
        return source.TryGetComponent<ICarryAnchor>(out var provider) && provider.Anchor != null
            ? provider.Anchor
            : source.transform;
    }

    private void LateUpdate()
    {
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
        var grip = Grip;

        // Rotation d'abord : elle deplace le grip, donc la position se calcule apres.
        var gripLocalRotation = Quaternion.Inverse(transform.rotation) * grip.rotation;
        transform.rotation = anchor.rotation * Quaternion.Inverse(gripLocalRotation);

        transform.position += anchor.position - grip.position;
    }
}
