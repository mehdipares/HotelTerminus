using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Emplacement ou un objet doit etre depose : la chambre d'un client, aujourd'hui une simple
/// boite de test.
///
/// Autorite serveur : lui seul observe le trigger et decide qu'une livraison est valide.
/// Les clients ne font qu'afficher <see cref="isDelivered"/> — aucun d'eux ne peut declarer
/// une livraison de son cote.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(BoxCollider))]
public class DeliveryZone : NetworkBehaviour
{
    [Header("Regles")]
    [Tooltip("Identifiant attendu. Vide = n'importe quel objet est accepte. Servira a exiger " +
             "la bonne valise dans la bonne chambre.")]
    [SerializeField] private string acceptedItemId;

    [Tooltip("Vitesse maximale pour qu'un objet compte comme pose. Empeche de valider une " +
             "livraison en lancant la valise a travers la piece depuis le couloir.")]
    [SerializeField] private float restingSpeed = 0.5f;

    [Header("Rendu de test")]
    [SerializeField] private Renderer zoneRenderer;
    [SerializeField] private Color pendingColor = new(1f, 0.25f, 0.25f, 0.25f);
    [SerializeField] private Color deliveredColor = new(0.25f, 1f, 0.4f, 0.25f);

    // Lisible par tous, ecrivable par le serveur : la meme regle que le holder d'un Carryable.
    private readonly NetworkVariable<bool> isDelivered = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// Emis sur le serveur quand une livraison est validee.
    /// Point d'accroche du futur systeme de satisfaction : il pourra noter la livraison
    /// (retards, chocs, mauvaise chambre) sans qu'on touche a cette classe.
    /// </summary>
    public event Action<Carryable> Delivered;

    public bool IsDelivered => IsSpawned && isDelivered.Value;

    private void Awake()
    {
        // On ne fait pas confiance au reglage de l'inspecteur : une zone de livraison qui
        // bloquerait physiquement le joueur serait un bug tres desagreable a diagnostiquer.
        GetComponent<BoxCollider>().isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        isDelivered.OnValueChanged += OnDeliveredChanged;

        // Un joueur qui rejoint apres coup doit voir les livraisons deja faites.
        ApplyVisual(isDelivered.Value);
    }

    public override void OnNetworkDespawn()
    {
        isDelivered.OnValueChanged -= OnDeliveredChanged;
    }

    private void OnDeliveredChanged(bool previous, bool current) => ApplyVisual(current);

    // Enter couvre l'objet qui arrive deja au repos, Stay celui qui doit d'abord se stabiliser
    // apres avoir ete lache. Les deux passent par la meme validation.
    private void OnTriggerEnter(Collider other) => TryDeliver(other);
    private void OnTriggerStay(Collider other) => TryDeliver(other);

    private void TryDeliver(Collider other)
    {
        if (!IsServer || !IsSpawned || isDelivered.Value) return;

        // GetComponentInParent : le collider touche est celui de la racine, mais un objet
        // compose pourrait tres bien presenter le collider d'un enfant.
        var carryable = other.GetComponentInParent<Carryable>();
        if (carryable == null || !Accepts(carryable)) return;

        isDelivered.Value = true;

        Debug.Log($"[Livraison] {carryable.name} depose dans {name}.");
        Delivered?.Invoke(carryable);
    }

    private bool Accepts(Carryable carryable)
    {
        // Objet rattache — tenu en main ou pose dans un receptacle : ce n'est pas une
        // livraison. Aujourd'hui ce cas ne peut pas arriver, car un objet rattache a ses
        // colliders coupes et ne declenche aucun trigger. On garde le test pour que la
        // regle metier reste ecrite, meme si la gestion des colliders change un jour.
        if (carryable.IsAttached) return false;

        if (!string.IsNullOrEmpty(acceptedItemId) && carryable.ItemId != acceptedItemId)
            return false;

        var body = carryable.GetComponent<Rigidbody>();
        return body == null || body.linearVelocity.magnitude <= restingSpeed;
    }

    private void ApplyVisual(bool delivered)
    {
        if (zoneRenderer == null) return;

        zoneRenderer.material.color = delivered ? deliveredColor : pendingColor;
    }

    private void OnDrawGizmos()
    {
        var box = GetComponent<BoxCollider>();
        if (box == null) return;

        Gizmos.color = IsDelivered
            ? new Color(0.25f, 1f, 0.4f, 0.4f)
            : new Color(1f, 0.25f, 0.25f, 0.4f);

        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(box.center, box.size);
    }
}
