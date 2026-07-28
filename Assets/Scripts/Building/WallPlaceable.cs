using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Marque un objet comme accrochable a un mur : evier, extincteur, applique, tableau.
///
/// Volontairement generique. Il ne sait rien de l'evier, et il ignore d'ou vient l'objet —
/// achete en boutique, livre dans une caisse ou trouve dans un placard, l'accrochage est le
/// meme.
///
/// **Un mur n'est pas un NetworkObject**, donc un objet accroche ne peut pas passer par le
/// systeme d'attachement de <see cref="Carryable"/>, qui reference toujours un porteur ou un
/// receptacle. L'objet pose porte donc son propre etat : cinematique, position et rotation
/// repliquees par son NetworkTransform en autorite serveur.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class WallPlaceable : NetworkBehaviour, IInstallable
{
    [Tooltip("Encombrement servant a verifier que l'objet rentre : largeur, hauteur, et " +
             "profondeur depuis le mur. A regler sur le volume reel, sinon la validation ment.")]
    [SerializeField] private Vector3 footprint = new(0.7f, 0.5f, 0.35f);

    [Tooltip("Decalage vertical par rapport au point vise. Permet d'accrocher un evier a " +
             "hauteur de taille en visant plus bas, par exemple.")]
    [SerializeField] private float heightOffset;

    [Tooltip("Point de l'objet qui vient toucher le MUR, distinct du Stow Point qui, lui, " +
             "designe la face du DESSOUS. Meme convention d'orientation que lui : fleche " +
             "verte vers le haut, fleche bleue vers l'avant de l'objet.\n\n" +
             "Deux points separes et non un seul reutilise : un evier accroche par son point " +
             "de pose se retrouverait suspendu par sa base, le bec en l'air. Plausible dans " +
             "l'inspecteur, faux en jeu.\n\n" +
             "Vide = centre visuel de l'objet, ce qui suffit pour tester.")]
    [SerializeField] private Transform mountPoint;

    private readonly NetworkVariable<bool> mounted = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private Rigidbody body;
    private Carryable carryable;

    public Vector3 Footprint => footprint;
    public float HeightOffset => heightOffset;
    public bool IsMounted => mounted.Value;

    /// <summary>Accroche au mur = en service. Un evier qu'on porte ne fuit pas.</summary>
    public bool IsInstalled => IsMounted;

    public event System.Action<bool> InstalledChanged;

    /// <summary>
    /// Ou l'objet doit se trouver pour que son point d'accrochage tombe sur
    /// <paramref name="spot"/>, tourne vers <paramref name="facing"/>.
    ///
    /// On raisonne sur un point de reference et non sur l'origine de l'objet : celle d'un
    /// modele importe est rarement dedans — celle de la valise est a plus d'un metre du sac.
    /// C'est le meme principe que le point de prise et le point de pose.
    /// </summary>
    public void GetMountPose(Vector3 spot, Quaternion facing, out Vector3 position, out Quaternion rotation)
    {
        var referenceRotation = mountPoint != null ? mountPoint.rotation : transform.rotation;
        var referencePosition = mountPoint != null ? mountPoint.position : VisualCenter();

        // L'ecart entre l'origine et le point de reference, exprime dans le repere de
        // l'objet. On passe par le monde, donc l'echelle est deja prise en compte.
        var offset = Quaternion.Inverse(transform.rotation) * (referencePosition - transform.position);
        var localRotation = Quaternion.Inverse(transform.rotation) * referenceRotation;

        rotation = facing * Quaternion.Inverse(localRotation);
        position = spot - rotation * offset;
    }

    /// <summary>
    /// Centre visuel, utilise faute de point d'accrochage explicite. Bien meilleur defaut que
    /// l'origine : l'objet tombe au moins la ou on le regarde.
    /// </summary>
    private Vector3 VisualCenter()
    {
        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return transform.position;

        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        return bounds.center;
    }

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        carryable = GetComponent<Carryable>();
    }

    public override void OnNetworkSpawn()
    {
        mounted.OnValueChanged += OnMountedChanged;

        if (carryable != null)
            carryable.AttachmentChanged += OnAttachmentChanged;

        ApplyMounted();
    }

    public override void OnNetworkDespawn()
    {
        mounted.OnValueChanged -= OnMountedChanged;

        if (carryable != null)
            carryable.AttachmentChanged -= OnAttachmentChanged;
    }

    private void OnMountedChanged(bool previous, bool current)
    {
        ApplyMounted();
        InstalledChanged?.Invoke(current);
    }

    /// <summary>
    /// Quelqu'un a repris l'objet en main : il n'est plus accroche. On ne fait que constater —
    /// c'est le systeme de port qui a decide, et le decrochage n'a donc pas besoin de sa
    /// propre interaction.
    /// </summary>
    private void OnAttachmentChanged()
    {
        if (!IsServer || !mounted.Value) return;

        if (carryable != null && carryable.IsAttached)
            mounted.Value = false;
    }

    private void ApplyMounted()
    {
        if (body == null) return;

        // Un objet accroche ne tombe pas et ne se fait pas bousculer. Le test isKinematic
        // des poussees le protege deja, sans qu'on ait a l'y ajouter.
        if (mounted.Value)
            body.isKinematic = true;
    }

    /// <summary>
    /// Accroche l'objet au mur, a l'endroit valide par le serveur. Serveur uniquement.
    /// </summary>
    public void ServerMount(PlayerCarry player, Vector3 position, Quaternion rotation)
    {
        if (!IsServer) return;

        // La main se vide d'abord, puis l'objet quitte le systeme de port sans etre lache :
        // on ne veut ni impulsion ni repositionnement, sa place est deja decidee.
        if (player != null)
            player.ServerReleaseHand();

        if (carryable != null)
            carryable.ServerDetachInPlace();

        transform.SetPositionAndRotation(position, rotation);

        mounted.Value = true;
        ApplyMounted();
    }
}
