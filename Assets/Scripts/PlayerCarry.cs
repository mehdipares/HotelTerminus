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

    // Ce que porte ce joueur. Ecrit par le serveur, lu par tous : un autre client peut ainsi
    // savoir si ce joueur a les mains prises (utile pour les animations plus tard).
    private readonly NetworkVariable<NetworkObjectReference> heldItem = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public Transform HandAnchor => handAnchor;

    /// <summary>Ancre vue par un Carryable : la main de ce joueur.</summary>
    public Transform Anchor => handAnchor;

    /// <summary>Le joueur peut-il prendre quelque chose ? Une seule main pour l'instant.</summary>
    public bool HasFreeHand => !heldItem.Value.TryGet(out _);

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

        if (!heldItem.Value.TryGet(out var netObj) || netObj == null)
            return false;

        carryable = netObj.GetComponent<Carryable>();
        return carryable != null;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        controller = GetComponent<CharacterController>();

        input = new InputSystem_Actions();
        input.Player.Interact.Enable();
        input.Player.Attack.Enable();
    }

    public override void OnNetworkDespawn()
    {
        ReleaseInput();

        // Un joueur qui se deconnecte ne doit pas emporter la valise dans le vide.
        if (IsServer && TryGetHeld(out var carried))
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

        // Clic gauche : appliquer ce qu'on tient. Prioritaire sur le reste, sinon visser
        // une ampoule reviendrait a la lacher par terre.
        if (input.Player.Attack.WasPressedThisFrame() && aimObject != null && aimCanUse)
        {
            RequestUseServerRpc(new NetworkObjectReference(aimObject));
            return;
        }

        if (!input.Player.Interact.WasPressedThisFrame()) return;

        // Mains pleines : E repose. Mains libres : E declenche l'interaction visee.
        if (heldItem.Value.TryGet(out _))
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
    /// Cherche un element interactif dans l'axe du regard, a portee de bras : une valise au
    /// sol, une douille, et demain une porte ou un generateur.
    /// </summary>
    private void RefreshAim()
    {
        aimObject = null;
        aimCanInteract = false;
        aimCanUse = false;

        var origin = aimSource != null ? aimSource : transform;

        if (!Physics.Raycast(origin.position, origin.forward, out var hit, reach,
                             interactMask, QueryTriggerInteraction.Ignore))
            return;

        // GetComponentInParent : on touche presque toujours un morceau du mesh, pas la racine.
        var interactable = hit.collider.GetComponentInParent<IInteractable>();
        if (interactable == null) return;

        // L'interface ne se serialise pas : on doit retrouver le NetworkObject porteur pour
        // pouvoir designer la cible au serveur.
        if (interactable is not NetworkBehaviour behaviour || behaviour.NetworkObject == null)
            return;

        // Les deux actions sont evaluees separement : viser une douille vide avec une ampoule
        // en main n'autorise pas E, mais autorise le clic gauche.
        aimObject = behaviour.NetworkObject;
        aimCanInteract = interactable.CanInteract(this);
        aimCanUse = interactable.CanUse(this);
    }

    /// <summary>
    /// Point de visee minimaliste, en IMGUI comme le reste de l'interface provisoire.
    /// Il change de couleur quand une cible est atteignable : sans ce retour, on ne sait
    /// pas si on rate l'objet ou si l'action a echoue.
    /// </summary>
    private void OnGUI()
    {
        if (!IsOwner || !showCrosshair) return;

        var onTarget = aimCanInteract || aimCanUse || heldItem.Value.TryGet(out _);
        var size = onTarget ? crosshairSize * 1.75f : crosshairSize;

        var rect = new Rect(
            (Screen.width - size) * 0.5f,
            (Screen.height - size) * 0.5f,
            size, size);

        var previous = GUI.color;
        GUI.color = onTarget ? crosshairOnTarget : crosshairIdle;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
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

        // GetComponent et non GetComponentInChildren : l'ampoule vissee est un enfant de la
        // douille et implemente elle aussi IInteractable. On veut la cible designee, pas ce
        // qu'elle contient.
        var interactable = targetObject.GetComponent<IInteractable>();

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

        var interactable = targetObject.GetComponent<IInteractable>();

        // Reevalue ici : entre la demande et son arrivee, un autre joueur a pu visser une
        // ampoule dans cette douille.
        if (interactable == null || !interactable.CanUse(this)) return;

        interactable.ServerUse(this);
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
