using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Traduit l'etat d'un evier en particules d'eau.
///
/// Separe de <see cref="Sink"/> comme <see cref="BulbVisual"/> l'est de <see cref="Bulb"/> :
/// la logique ne rend rien, le rendu ne decide rien.
///
/// **Rien n'est stocke ici non plus.** A chaque changement d'etat de l'evier OU de
/// l'arrivee d'eau, on recalcule ce qui doit couler. Purement local : les particules ne
/// traversent pas le reseau, chaque machine les deduit du meme etat repliqué et arrive donc
/// au meme resultat.
/// </summary>
public class SinkVisual : MonoBehaviour
{
    [Tooltip("L'evier suivi. Vide = celui porte par cet objet ou l'un de ses parents.")]
    [SerializeField] private Sink sink;

    [Tooltip("D'ou l'eau sort : le bec du robinet, ou le raccord qui fuit. Vide = cet objet.")]
    [SerializeField] private Transform spout;

    [Header("Effets — facultatifs")]
    [Tooltip("Laisse vide et un goutte-a-goutte simple se fabrique au lancement.")]
    [SerializeField] private ParticleSystem dripEffect;

    [Tooltip("Laisse vide et un jet simple se fabrique au lancement.")]
    [SerializeField] private ParticleSystem jetEffect;

    [SerializeField] private Color waterColor = new(0.55f, 0.8f, 1f, 0.75f);

    private Material material;
    private bool built;

    private void Awake()
    {
        if (sink == null) sink = GetComponentInParent<Sink>();
        if (spout == null) spout = transform;
    }

    private void OnEnable()
    {
        if (sink != null)
            sink.StateChanged += OnSinkStateChanged;

        WaterManager.WaterChanged += OnWaterChanged;

        Refresh();
    }

    private void OnDisable()
    {
        if (sink != null)
            sink.StateChanged -= OnSinkStateChanged;

        WaterManager.WaterChanged -= OnWaterChanged;
    }

    private void OnDestroy()
    {
        if (material != null)
            Destroy(material);
    }

    private void OnSinkStateChanged(SinkState state) => Refresh();

    private void OnWaterChanged(bool hasWater) => Refresh();

    /// <summary>
    /// Recalcule ce qui coule. Les deux causes possibles — l'evier s'abime, ou l'eau est
    /// coupee — passent par ici, il n'y a donc qu'un seul endroit qui decide de l'affichage.
    /// </summary>
    private void Refresh()
    {
        if (sink == null) return;

        Build();

        // La seule ligne qui compte : ca coule si l'evier fuit ET que l'eau arrive.
        var running = sink.IsRunning;

        Play(dripEffect, running && sink.State == SinkState.SmallLeak);
        Play(jetEffect, running && sink.State == SinkState.BigLeak);
    }

    private static void Play(ParticleSystem effect, bool active)
    {
        if (effect == null) return;

        if (active && !effect.isEmitting)
        {
            effect.Play();
        }
        else if (!active && effect.isEmitting)
        {
            // Stop et non Clear : les gouttes deja en l'air finissent leur chute au lieu de
            // disparaitre d'un coup quand on ferme la vanne.
            effect.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    // ---------- Fabrication des effets ----------

    private void Build()
    {
        if (built) return;
        built = true;

        if (dripEffect == null)
            dripEffect = CreateEffect("Goutte", rate: 4f, speed: 0.4f, size: 0.02f, spread: 2f);

        if (jetEffect == null)
            jetEffect = CreateEffect("Jet", rate: 90f, speed: 2.6f, size: 0.035f, spread: 9f);
    }

    /// <summary>
    /// Fabrique un effet d'eau par code, pour n'avoir aucun asset a preparer a la main. Les
    /// champs de l'inspecteur permettent de le remplacer par un vrai effet plus tard.
    /// </summary>
    private ParticleSystem CreateEffect(string name, float rate, float speed, float size, float spread)
    {
        var holder = new GameObject(name);
        holder.transform.SetParent(spout, false);

        // Vers le bas : l'eau tombe. On oriente l'emetteur plutot que de bricoler la gravite.
        holder.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);

        var effect = holder.AddComponent<ParticleSystem>();
        effect.Stop();

        var main = effect.main;
        main.startLifetime = 0.8f;
        main.startSpeed = speed;
        main.startSize = size;
        main.startColor = waterColor;
        main.gravityModifier = 1f;
        main.playOnAwake = false;
        main.maxParticles = 300;

        var emission = effect.emission;
        emission.rateOverTime = rate;

        var shape = effect.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = spread;
        shape.radius = 0.01f;

        var renderer = effect.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material != null ? material : (material = BuildMaterial());
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        return effect;
    }

    private static Material BuildMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Sprites/Default");

        if (shader == null) return null;

        var built = new Material(shader);

        built.SetFloat("_Surface", 1f);
        built.SetFloat("_ZWrite", 0f);
        built.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        built.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        built.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        built.renderQueue = (int)RenderQueue.Transparent;

        return built;
    }
}
