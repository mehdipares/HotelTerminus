using UnityEngine;

/// <summary>
/// Traduit la position d'une roue en apparence : le volant tourne, et on distingue ouvert de
/// ferme d'un coup d'oeil.
///
/// Separe de <see cref="WaterValve"/> comme <see cref="BulbVisual"/> l'est de
/// <see cref="Bulb"/> : la logique ne rend rien, le rendu ne decide rien. Le jour ou tu
/// remplaces le modele, seul ce composant change.
///
/// Il suit **sa propre roue**, pas l'etat global de l'eau : avec deux roues, une seule peut
/// etre fermee, et chacune doit montrer sa position reelle.
///
/// Purement local et deduit d'un etat repliqué : il n'y a rien a synchroniser ici.
/// </summary>
public class WaterValveVisual : MonoBehaviour
{
    [Tooltip("La roue suivie. Vide = celle portee par cet objet ou l'un de ses parents.")]
    [SerializeField] private WaterValve valve;

    [Tooltip("Ce qui tourne quand on manoeuvre : le volant. Vide = cet objet lui-meme.")]
    [SerializeField] private Transform wheel;

    [Tooltip("Axe de rotation du volant, dans son propre repere. Regarde le gizmo de la roue " +
             "et prends l'axe qui sort de son disque : bleu = 0,0,1, rouge = 1,0,0.")]
    [SerializeField] private Vector3 turnAxis = Vector3.up;

    [Tooltip("Angle du volant entre ouvert et ferme.")]
    [SerializeField] private float closedAngle = 90f;

    [Tooltip("Vitesse de rotation du volant, en degres par seconde.")]
    [SerializeField] private float turnSpeed = 180f;

    [Header("Teinte — provisoire, en attendant un vrai materiau")]
    [SerializeField] private Renderer tinted;
    [SerializeField] private Color openColor = new(0.3f, 0.7f, 1f);
    [SerializeField] private Color closedColor = new(1f, 0.4f, 0.2f);

    private Quaternion openRotation;
    private Material material;
    private bool isOpen = true;

    private void Awake()
    {
        if (wheel == null) wheel = transform;
        if (valve == null) valve = GetComponentInParent<WaterValve>();

        openRotation = wheel.localRotation;

        // Instance de materiau et non le partage : teinter le materiau partage repeindrait
        // tous les objets qui l'utilisent, jusque dans les fichiers du projet.
        if (tinted != null)
            material = tinted.material;
    }

    private void OnEnable()
    {
        if (valve == null) return;

        valve.OpenChanged += OnOpenChanged;

        // Cale sans animation sur la position courante : au chargement, une roue deja fermee
        // doit l'etre a l'ecran, pas se tourner toute seule sous nos yeux.
        isOpen = valve.IsOpen;
        wheel.localRotation = TargetRotation();
        ApplyTint();
    }

    private void OnDisable()
    {
        if (valve != null)
            valve.OpenChanged -= OnOpenChanged;
    }

    private void OnDestroy()
    {
        if (material != null)
            Destroy(material);
    }

    private void OnOpenChanged(bool open)
    {
        isOpen = open;
        ApplyTint();
    }

    private void Update()
    {
        wheel.localRotation = Quaternion.RotateTowards(
            wheel.localRotation, TargetRotation(), turnSpeed * Time.deltaTime);
    }

    private void ApplyTint()
    {
        if (material != null)
            material.color = isOpen ? openColor : closedColor;
    }

    private Quaternion TargetRotation()
    {
        if (isOpen) return openRotation;

        var axis = turnAxis.sqrMagnitude > 0.001f ? turnAxis.normalized : Vector3.forward;

        return openRotation * Quaternion.AngleAxis(closedAngle, axis);
    }
}
