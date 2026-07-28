using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Cote joueur : vise un mur avec un objet accrochable en main, previsualise, et pose.
///
/// Composant separe de <see cref="PlayerCarry"/> a dessein : celui-ci depasse deja les 500
/// lignes et fait trop de metiers. Accrocher au mur est un metier a part, il vit a part.
///
/// Le clic gauche ne se marche pas dessus avec l'action secondaire de PlayerCarry : un mur
/// n'est pas un IInteractable, donc quand on en vise un, PlayerCarry n'a aucune cible et ne
/// declenche rien.
/// </summary>
[RequireComponent(typeof(PlayerCarry))]
[RequireComponent(typeof(WallPlacementGhost))]
public class WallPlacer : NetworkBehaviour
{
    [Header("Visee")]
    [Tooltip("Origine du rayon : le pivot camera.")]
    [SerializeField] private Transform aimSource;

    [SerializeField] private float reach = 3.5f;

    [Tooltip("Calque des murs. Sans lui, le rayon ne saurait pas distinguer un mur d'un " +
             "meuble — c'est le reglage a ne pas oublier sur le decor.")]
    [SerializeField] private LayerMask wallMask;

    [Tooltip("Ce qui compte comme obstacle a l'emplacement vise. Y laisser tout sauf les " +
             "murs eux-memes : le rayon touche forcement le mur, ce n'est pas un obstacle.")]
    [SerializeField] private LayerMask obstacleMask = ~0;

    [Header("Verification serveur")]
    [Tooltip("Tolerance de distance cote serveur. Plus large que la portee : il voit le " +
             "joueur avec un peu de retard reseau.")]
    [SerializeField] private float serverMaxDistance = 5.5f;

    private PlayerCarry carry;
    private WallPlacementGhost ghost;
    private InputSystem_Actions input;

    // Emplacement valide de la frame courante, cote proprietaire.
    private WallPlaceable target;
    private Vector3 spot;
    private Quaternion facing;
    private bool valid;

    private void Awake()
    {
        carry = GetComponent<PlayerCarry>();
        ghost = GetComponent<WallPlacementGhost>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        input = new InputSystem_Actions();
        input.Player.Attack.Enable();
    }

    public override void OnNetworkDespawn()
    {
        ReleaseInput();
        ghost.Hide();
    }

    public override void OnDestroy()
    {
        ReleaseInput();
        base.OnDestroy();
    }

    private void ReleaseInput()
    {
        if (input == null) return;

        input.Player.Attack.Disable();
        input.Dispose();
        input = null;
    }

    private void Update()
    {
        if (!IsOwner || input == null) return;

        RefreshTarget();

        if (target != null && valid && input.Player.Attack.WasPressedThisFrame())
            RequestPlaceRpc(new NetworkObjectReference(target.NetworkObject), spot, facing);
    }

    /// <summary>
    /// Cherche un mur dans l'axe du regard et calcule l'emplacement que prendrait l'objet.
    /// </summary>
    private void RefreshTarget()
    {
        target = null;
        valid = false;

        if (!carry.TryGetHeld(out var carried)
            || !carried.TryGetComponent<WallPlaceable>(out var placeable))
        {
            ghost.Hide();
            return;
        }

        var origin = aimSource != null ? aimSource : transform;

        if (!Physics.Raycast(origin.position, origin.forward, out var hit, reach,
                             wallMask, QueryTriggerInteraction.Ignore))
        {
            ghost.Hide();
            return;
        }

        // L'objet colle au mur, dos contre lui : il regarde vers l'exterieur. On redresse la
        // normale a l'horizontale, sinon un mur legerement penche ferait pencher l'evier.
        var outward = Vector3.ProjectOnPlane(hit.normal, Vector3.up);

        // Surface trop horizontale : un sol ou un plafond, meme s'il porte le calque des
        // murs. On ne s'y accroche pas — et ça evite d'avoir a etre parfait dans
        // l'assignation des calques.
        if (outward.sqrMagnitude < 0.05f)
        {
            ghost.Hide();
            return;
        }

        target = placeable;
        facing = Quaternion.LookRotation(outward.normalized, Vector3.up);
        spot = hit.point + Vector3.up * placeable.HeightOffset;

        valid = Fits(placeable, hit.collider, spot, facing);

        // La previsualisation montre exactement la pose que prendra l'objet, calculee par
        // lui : c'est son point d'accrochage qui vient sur le mur, pas son origine.
        placeable.GetMountPose(spot, facing, out var pose, out var poseRotation);

        ghost.Show(placeable, pose, poseRotation, valid);
    }

    /// <summary>
    /// L'objet rentre-t-il ici ? Deux conditions distinctes :
    ///
    /// 1. rien ne gene a l'emplacement — un meuble, un autre objet deja pose ;
    /// 2. l'objet ne deborde pas du mur. On lance un rayon depuis chacun de ses quatre coins :
    ///    s'ils touchent tous le meme mur, il est entierement dessus ; si l'un rate ou tombe
    ///    sur un autre mur, on est a cheval sur un angle ou sur un bord.
    /// </summary>
    private bool Fits(WallPlaceable placeable, Collider wall, Vector3 position, Quaternion rotation)
    {
        var size = placeable.Footprint;
        var half = size * 0.5f;

        // La boite est plaquee devant le mur, pas dedans : sinon le mur lui-meme compterait
        // comme obstacle a chaque fois.
        var center = position + rotation * Vector3.forward * half.z;

        // 0.95 : une marge, sans quoi un objet pose pile a cote se declencherait mutuellement.
        // Les murs sont retires du masque d'obstacles ici plutot que dans l'inspecteur : le
        // rayon touche forcement un mur, ce n'est jamais un obstacle, et l'oublier rendrait
        // tout emplacement invalide sans qu'on comprenne pourquoi.
        var overlaps = Physics.OverlapBox(center, half * 0.95f, rotation,
                                          obstacleMask & ~wallMask,
                                          QueryTriggerInteraction.Ignore);

        foreach (var other in overlaps)
        {
            if (other.transform.root == placeable.transform.root) continue;   // l'objet tenu
            if (other.transform.root == transform.root) continue;             // nous-memes

            return false;
        }

        // Les quatre coins de la face avant, ramenes vers le mur.
        var back = rotation * Vector3.back;

        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        {
            var corner = center
                         + rotation * new Vector3(half.x * x * 0.95f, half.y * y * 0.95f, 0f)
                         - back * 0.05f;

            if (!Physics.Raycast(corner, back, out var cornerHit, half.z + 0.2f,
                                 wallMask, QueryTriggerInteraction.Ignore))
            {
                return false;                                  // ce coin est dans le vide
            }

            if (cornerHit.collider != wall) return false;      // ce coin est sur un autre mur
        }

        return true;
    }

    /// <summary>
    /// Le client propose un emplacement, le serveur le revalide entierement avant d'accrocher.
    /// Sans cette verification, un client pourrait poser un evier dans un mur ou en plein air.
    /// </summary>
    [Rpc(SendTo.Server)]
    private void RequestPlaceRpc(NetworkObjectReference targetRef, Vector3 position, Quaternion rotation)
    {
        if (!targetRef.TryGet(out var netObject) || netObject == null) return;
        if (!netObject.TryGetComponent<WallPlaceable>(out var placeable)) return;

        // L'objet doit toujours etre dans la main de ce joueur.
        if (!carry.TryGetHeld(out var carried) || carried.NetworkObject != netObject) return;

        if (Vector3.Distance(transform.position, position) > serverMaxDistance) return;

        // On refait le test d'encombrement ici, sur la simulation du serveur : il faut
        // retrouver le mur vise depuis la position annoncee.
        if (!Physics.Raycast(position + rotation * Vector3.forward * 0.05f,
                             rotation * Vector3.back, out var hit, 0.6f,
                             wallMask, QueryTriggerInteraction.Ignore))
        {
            return;
        }

        if (!Fits(placeable, hit.collider, position, rotation)) return;

        // Le serveur recalcule la pose finale lui-meme, a partir du seul point de contact
        // annonce. Le client ne decide donc pas ou l'objet se retrouve exactement.
        placeable.GetMountPose(position, rotation, out var pose, out var poseRotation);

        placeable.ServerMount(carry, pose, poseRotation);
    }
}
