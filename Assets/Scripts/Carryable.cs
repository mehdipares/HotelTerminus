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
public class Carryable : NetworkBehaviour
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

    // Lisible par tous, ecrivable par le serveur uniquement : la regle du projet.
    private readonly NetworkVariable<ulong> holder = new(
        NoHolder,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private Rigidbody body;
    private Collider[] colliders;
    private NetworkTransform netTransform;
    private Transform anchor;                    // main du porteur, resolue localement

    public bool IsHeld => holder.Value != NoHolder;
    public ulong HolderClientId => holder.Value;
    public string ItemId => itemId;
    public Transform Grip => gripPoint != null ? gripPoint : transform;

    // TODO satisfaction client : c'est ici que viendra le comptage des mauvais traitements
    // (OnCollisionEnter cote serveur, au-dela d'un seuil d'impulsion). La DeliveryZone
    // pourra alors noter la livraison au lieu de la valider betement. Rien n'est compte
    // aujourd'hui, on ne stocke aucun etat inutile.

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        colliders = GetComponentsInChildren<Collider>(true);
        netTransform = GetComponent<NetworkTransform>();
    }

    public override void OnNetworkSpawn()
    {
        holder.OnValueChanged += OnHolderChanged;

        // Un client qui rejoint en cours de partie doit retrouver l'objet dans les mains
        // de celui qui le porte : on applique l'etat courant, pas seulement les changements.
        ApplyHolder(holder.Value);
    }

    public override void OnNetworkDespawn()
    {
        holder.OnValueChanged -= OnHolderChanged;
    }

    private void OnHolderChanged(ulong previous, ulong current) => ApplyHolder(current);

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
        holder.Value = carrier.OwnerClientId;
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

        if (dropOrigin == null) return;

        // On repose l'objet devant le joueur plutot qu'a l'endroit exact de la main :
        // sinon il naitrait dans sa capsule de collision et serait ejecte n'importe ou.
        var target = dropOrigin.position
                     + dropOrigin.forward * dropForwardOffset
                     + Vector3.up * dropUpOffset;

        transform.position += target - Grip.position;

        // On ne redresse pas l'objet : il garde l'inclinaison qu'il avait en main et finit
        // de basculer tout seul. Reposer une valise parfaitement d'aplomb la laisse plantee
        // comme un piquet, ce qui trahit immediatement le code derriere.
        body.linearVelocity = carrierVelocity * carrierVelocityTransfer
                              + dropOrigin.forward * dropImpulse;

        body.angularVelocity = UnityEngine.Random.insideUnitSphere * dropSpin;
    }

    // ---------- Etat local, deduit du serveur ----------

    private void ApplyHolder(ulong holderId)
    {
        var held = holderId != NoHolder;

        anchor = held ? ResolveAnchor(holderId) : null;

        // Colliders coupes : sinon l'objet porte percute son propre porteur.
        foreach (var col in colliders)
            col.enabled = !held;

        body.isKinematic = held;

        // Pendant le port, la position se deduit du porteur chez chaque client. Repliquer
        // en plus serait du trafic inutile, et les deux se marcheraient dessus.
        if (netTransform != null)
            netTransform.enabled = !held;

        if (held)
            SnapToAnchor();
    }

    private Transform ResolveAnchor(ulong holderId)
    {
        var playerObject = NetworkManager.SpawnManager.GetPlayerNetworkObject(holderId);
        if (playerObject == null) return null;

        var carry = playerObject.GetComponent<PlayerCarry>();
        return carry != null ? carry.HandAnchor : playerObject.transform;
    }

    private void LateUpdate()
    {
        // LateUpdate : la camera et le joueur ont fini de bouger, l'objet ne traine donc
        // pas d'une frame derriere la main.
        if (IsHeld && anchor != null)
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
