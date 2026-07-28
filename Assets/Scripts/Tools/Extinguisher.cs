using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Extincteur : l'outil qui eteint un foyer, et le jet blanc qui va avec.
///
/// **Le jet ne se declenche pas tout seul.** C'est le foyer qui replique quel extincteur est
/// en train de l'eteindre, et qui appelle <see cref="SetSpraying"/> chez tout le monde. Sans
/// ca, chacun ne verrait que son propre jet — l'appui d'une touche n'est repliqué nulle part.
///
/// Le jet est fait de **trois couches** superposees plutot que d'un seul cone. C'est ce qui
/// distingue un extincteur d'un pulverisateur : un noyau rapide et serre qui donne la
/// direction, un nuage large et turbulent qui donne la masse, et une trainee lente qui reste
/// dans l'air apres coup.
///
/// A poser sur un objet portable : c'est en l'ayant en main qu'on s'en sert, comme la cle.
/// </summary>
[RequireComponent(typeof(Carryable))]
public class Extinguisher : MonoBehaviour
{
    [Tooltip("Sortie du tuyau. Le jet part de la et suit son orientation. Vide = cet objet.")]
    [SerializeField] private Transform nozzle;

    [Tooltip("Portee visuelle du jet, en metres.")]
    [SerializeField] private float range = 3.5f;

    [Tooltip("Densite generale. Multiplie le debit des trois couches d'un coup.")]
    [SerializeField] private float density = 1f;

    [SerializeField] private Color sprayColor = new(1f, 1f, 1f, 0.55f);

    private readonly List<ParticleSystem> layers = new();
    private Material material;

    private void Awake()
    {
        if (nozzle == null) nozzle = transform;
    }

    private void OnDestroy()
    {
        if (material != null)
            Destroy(material);
    }

    /// <summary>
    /// Ouvre ou ferme le jet. Appele sur toutes les machines a partir de l'etat repliqué du
    /// foyer, jamais depuis l'appui d'une touche.
    /// </summary>
    public void SetSpraying(bool spraying)
    {
        if (spraying && !EnsureSpray()) return;

        foreach (var layer in layers)
        {
            if (layer == null) continue;

            if (spraying)
            {
                layer.Play();
                continue;
            }

            // Sans effacer : le nuage deja sorti finit sa course au lieu de disparaitre d'un
            // coup, comme les gouttes d'un evier quand on ferme une vanne.
            layer.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    private bool EnsureSpray()
    {
        if (layers.Count > 0) return true;

        material = BuildMaterial();

        if (material == null)
        {
            Debug.LogWarning($"[Extincteur] {name} : aucun shader utilisable pour le jet.");
            return false;
        }

        // Le noyau : rapide, serre, court. C'est lui qui donne la direction et le punch.
        layers.Add(BuildLayer("Jet_Noyau",
            rate: 120f, speed: range * 2.2f, size: 0.12f, growth: 2.5f,
            spread: 6f, gravity: 0f, noise: 0f, lifetimeScale: 0.55f));

        // Le nuage : lent, large, turbulent. C'est lui qui donne la masse et le volume.
        layers.Add(BuildLayer("Jet_Nuage",
            rate: 70f, speed: range * 1.1f, size: 0.3f, growth: 3.5f,
            spread: 20f, gravity: -0.08f, noise: 0.35f, lifetimeScale: 1f));

        // La trainee : ce qui flotte encore quand on a relache, et qui monte doucement.
        layers.Add(BuildLayer("Jet_Trainee",
            rate: 25f, speed: range * 0.35f, size: 0.45f, growth: 2f,
            spread: 34f, gravity: -0.25f, noise: 0.5f, lifetimeScale: 2.2f));

        return true;
    }

    private ParticleSystem BuildLayer(string name, float rate, float speed, float size,
                                      float growth, float spread, float gravity,
                                      float noise, float lifetimeScale)
    {
        var holder = new GameObject(name);
        holder.transform.SetParent(nozzle, false);

        var effect = holder.AddComponent<ParticleSystem>();
        effect.Stop();

        var main = effect.main;

        // La duree de vie decoule de la portee : on regle une distance, pas un temps.
        main.startLifetime = range / Mathf.Max(0.5f, speed) * lifetimeScale;
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.75f, speed);
        main.startSize = new ParticleSystem.MinMaxCurve(size * 0.7f, size);
        main.startColor = sprayColor;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = gravity;
        main.playOnAwake = false;
        main.maxParticles = 600;

        var emission = effect.emission;
        emission.rateOverTime = rate * Mathf.Max(0f, density);

        var shape = effect.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = spread;
        shape.radius = 0.04f;

        // Le nuage s'etale en avancant : sans ca le jet reste un tube et ne ressemble a rien.
        var overLife = effect.sizeOverLifetime;
        overLife.enabled = true;
        overLife.size = new ParticleSystem.MinMaxCurve(
            1f, AnimationCurve.EaseInOut(0f, 0.35f, 1f, growth));

        var fade = effect.colorOverLifetime;
        fade.enabled = true;
        fade.color = new ParticleSystem.MinMaxGradient(Fade());

        // Une rotation lente et desordonnee : c'est ce qui empeche de reconnaitre les
        // quadrilateres et fait lire le tout comme un nuage.
        var spin = effect.rotationOverLifetime;
        spin.enabled = true;
        spin.z = new ParticleSystem.MinMaxCurve(-1.2f, 1.2f);

        if (noise > 0f)
        {
            var turbulence = effect.noise;
            turbulence.enabled = true;
            turbulence.strength = noise;
            turbulence.frequency = 1.4f;
            turbulence.scrollSpeed = 0.6f;
            turbulence.damping = true;
        }

        var renderer = effect.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.sortMode = ParticleSystemSortMode.Distance;

        return effect;
    }

    private static Gradient Fade()
    {
        var gradient = new Gradient();

        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(new Color(0.92f, 0.94f, 0.96f), 1f),
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),        // apparait au lieu de surgir
                new GradientAlphaKey(1f, 0.12f),
                new GradientAlphaKey(0.7f, 0.5f),
                new GradientAlphaKey(0f, 1f),
            });

        return gradient;
    }

    // TODO menage : ce materiau translucide fabrique par code existe maintenant en quatre
    // exemplaires — repere de pose du chariot, previsualisation murale, particules d'eau, et
    // ici. Bon candidat a une extraction dans Core quand on repassera sur l'architecture.
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
