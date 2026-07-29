using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Extincteur : un jet qui pousse tout ce qu'il touche, et qui existe **par lui-meme**.
///
/// Il s'utilise en permanence, feu ou pas. C'est un renversement par rapport a la premiere
/// version, ou c'etait le foyer qui pilotait le jet : desormais l'extincteur porte son propre
/// etat, et le feu ne fait que subir. L'extinction devient donc **spatiale** — viser a cote
/// n'eteint rien.
///
/// **L'etat est repliqué** parce que tout le monde doit voir le jet : l'appui d'une touche,
/// lui, ne l'est nulle part. Le porteur demande, le serveur ecrit, chaque machine affiche.
///
/// La force est appliquee **par le serveur**, dans un cone devant la buse. Meme regle que
/// partout : le client constate ou demande, le serveur applique sur sa simulation.
///
/// Le jet est fait de **trois couches** superposees plutot que d'un seul cone. C'est ce qui
/// distingue un extincteur d'un pulverisateur : un noyau rapide et serre qui donne la
/// direction, un nuage large et turbulent qui donne la masse, et une trainee lente qui reste
/// dans l'air apres coup.
/// </summary>
[RequireComponent(typeof(Carryable))]
[RequireComponent(typeof(NetworkObject))]
public class Extinguisher : NetworkBehaviour, IHandTool
{
    [Tooltip("Sortie du tuyau. Le jet part de la et suit son orientation. Vide = cet objet.")]
    [SerializeField] private Transform nozzle;

    [Tooltip("Portee du jet, en metres. Vaut pour le visuel comme pour la poussee.")]
    [SerializeField] private float range = 6.5f;

    [Tooltip("Ouverture du cone de poussee, en degres depuis l'axe de la buse.")]
    [SerializeField] private float coneAngle = 26f;

    [Tooltip("Acceleration au coeur du jet, a bout portant, en m/s². Decroit avec la distance " +
             "et l'ecart a l'axe.\n\n" +
             "En acceleration et non en newtons : sinon la masse divise tout, et le chariot " +
             "de 25 kg recevrait quatorze fois moins qu'une ampoule. Ici tout part, et c'est " +
             "Reference Mass qui redonne un peu de poids au lourd.")]
    [SerializeField] private float pushAcceleration = 55f;

    [Tooltip("Masse au-dela de laquelle un objet commence a resister. En dessous, il prend " +
             "toute la poussee. A 10 : l'ampoule et la valise partent pareil, le chariot " +
             "encaisse un peu moins.")]
    [SerializeField] private float referenceMass = 10f;

    [Tooltip("Vitesse communiquee a un joueur touche. Il n'a pas de Rigidbody : c'est sa " +
             "propre machine qui applique la poussee a son deplacement.")]
    [SerializeField] private float playerPushSpeed = 6f;

    [Tooltip("Distance de pose. Tres courte volontairement : sans ca, vouloir arroser a deux " +
             "metres d'un chariot y deposerait l'extincteur.")]
    [SerializeField] private float useReach = 0.8f;

    [Tooltip("Secousse de camera du porteur pendant le jet.")]
    [Range(0f, 1f)]
    [SerializeField] private float cameraShake = 0.55f;

    [Tooltip("Densite generale. Multiplie le debit des trois couches d'un coup.")]
    [SerializeField] private float density = 1f;

    [SerializeField] private Color sprayColor = new(1f, 1f, 1f, 0.55f);

    private readonly NetworkVariable<bool> spraying = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly List<ParticleSystem> layers = new();
    private Material material;
    private Carryable carryable;
    private Transform sprayRoot;                 // porte les couches, oriente vers la visee
    private float nextPlayerPush;

    /// <summary>
    /// D'ou part le jet, et dans quelle direction.
    ///
    /// **La direction vient du regard du porteur, pas de la buse.** L'extincteur est tenu par
    /// une ancre fixee sur le corps du joueur : elle suit son lacet mais jamais son regard
    /// vertical. Un jet aligne sur la buse ne pourrait donc ni monter ni descendre, et ne
    /// partirait jamais vers ce que le viseur designe.
    /// </summary>
    private bool TryGetAim(out Vector3 origin, out Vector3 direction)
    {
        origin = nozzle.position;
        direction = nozzle.forward;

        if (carryable == null || !carryable.TryGetHolder(out var holder)) return false;
        if (!holder.TryGetComponent<PlayerController>(out var controller)) return false;

        direction = controller.AimDirection;
        return true;
    }

    public bool IsSpraying => IsSpawned && spraying.Value;

    // ---------- IHandTool ----------

    public bool IsBusy => IsSpraying;

    public float UseReach => useReach;

    public float CameraShake => IsSpraying ? cameraShake : 0f;

    /// <summary>
    /// Le porteur maintient ou relache le clic gauche. Il ne bascule rien lui-meme : il
    /// demande, et attend la valeur repliquee — comme pour tout le reste.
    /// </summary>
    public void SetUsing(bool using_)
    {
        if (!IsSpawned || spraying.Value == using_) return;

        RequestSprayRpc(using_);
    }

    [Rpc(SendTo.Server)]
    private void RequestSprayRpc(bool value)
    {
        // Seul celui qui le tient peut s'en servir : sinon n'importe qui declencherait le jet
        // d'un extincteur pose a l'autre bout de l'hotel.
        if (value && (carryable == null || !carryable.IsHeld)) return;

        spraying.Value = value;
    }

    private void Awake()
    {
        if (nozzle == null) nozzle = transform;

        carryable = GetComponent<Carryable>();
    }

    public override void OnNetworkSpawn()
    {
        spraying.OnValueChanged += OnSprayingChanged;

        // Un joueur qui rejoint pendant qu'on arrose doit voir le jet.
        SetSpraying(spraying.Value);
    }

    public override void OnNetworkDespawn()
    {
        spraying.OnValueChanged -= OnSprayingChanged;

        SetSpraying(false);
    }

    private void OnSprayingChanged(bool previous, bool current) => SetSpraying(current);

    private void Update()
    {
        // Le jet s'oriente sur toutes les machines, pas seulement celle qui arrose : chacune
        // connait le regard du porteur, puisqu'il est repliqué.
        if (sprayRoot != null && spraying.Value && TryGetAim(out _, out var direction))
            sprayRoot.rotation = Quaternion.LookRotation(direction, Vector3.up);

        // Lache ou pose alors qu'il arrosait : on coupe. Le garde-fou de PlayerCarry evite
        // que ca arrive, celui-ci rattrape les chemins qu'on n'aurait pas prevus.
        if (!IsServer || !IsSpawned || !spraying.Value) return;

        if (carryable == null || !carryable.IsHeld)
            spraying.Value = false;
    }

    // ---------- La poussee, cote serveur ----------

    private void FixedUpdate()
    {
        if (!IsServer || !IsSpawned || !spraying.Value) return;

        Blow();
    }

    /// <summary>
    /// Souffle tout ce qui se trouve dans le cone devant la buse.
    ///
    /// Une sphere filtree par angle plutot qu'une boite orientee : c'est plus simple a lire,
    /// et la force decroit naturellement avec la distance **et** avec l'ecart a l'axe — au
    /// coeur du jet ca envoie valser, en bordure ca effleure.
    /// </summary>
    private void Blow()
    {
        TryGetAim(out var origin, out var direction);

        var cosLimit = Mathf.Cos(Mathf.Clamp(coneAngle, 1f, 89f) * Mathf.Deg2Rad);

        var pushPlayers = Time.time >= nextPlayerPush;
        var pushedSomeone = false;

        foreach (var other in Physics.OverlapSphere(origin, range, ~0, QueryTriggerInteraction.Ignore))
        {
            // Ni nous-memes, ni celui qui nous tient : on ne se souffle pas dessus.
            if (other.transform.root == transform.root) continue;

            var toTarget = other.bounds.center - origin;
            var distance = toTarget.magnitude;
            if (distance < 0.05f || distance > range) continue;

            var alignment = Vector3.Dot(toTarget / distance, direction);
            if (alignment < cosLimit) continue;

            // Deux attenuations qui se multiplient : la distance et l'ecart a l'axe.
            var falloff = (1f - distance / range)
                          * Mathf.InverseLerp(cosLimit, 1f, alignment);

            var body = other.attachedRigidbody;

            if (body != null && !body.isKinematic)
            {
                // Le lourd resiste un peu, sans jamais devenir insensible : au-dela de la
                // masse de reference l'acceleration diminue, mais elle ne s'annule pas.
                var weight = Mathf.Clamp01(referenceMass / Mathf.Max(0.1f, body.mass));

                body.AddForce(direction * (pushAcceleration * falloff * weight),
                              ForceMode.Acceleration);
                continue;
            }

            if (!pushPlayers) continue;

            // Un joueur n'a pas de Rigidbody : sa capsule ne subit aucune force. On lui
            // envoie donc la poussee, et c'est **sa** machine qui l'applique a son propre
            // deplacement. C'est le pendant exact de la bousculade, dans l'autre sens.
            var player = other.GetComponentInParent<PlayerController>();
            if (player == null || player.transform.root == carryable.transform.root) continue;

            player.ServerPush(direction * (playerPushSpeed * falloff));
            pushedSomeone = true;
        }

        if (pushedSomeone)
            nextPlayerPush = Time.time + 0.1f;
    }

    /// <summary>
    /// Ouvre ou ferme le jet. Deduit de l'etat repliqué, jamais de l'appui d'une touche —
    /// c'est ce qui fait que tout le monde voit le meme jet.
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

        // Le noyau : rapide, serre au depart, il porte loin et donne la direction.
        layers.Add(BuildLayer("Jet_Noyau",
            rate: 140f, speed: range * 2.4f, size: 0.12f, growth: 5f,
            spread: 4f, gravity: 0f, noise: 0.1f, lifetimeScale: 0.8f));

        // Le nuage : plus lent, il s'ouvre franchement en avancant. C'est lui qu'on voit
        // s'evaser au bout du jet.
        layers.Add(BuildLayer("Jet_Nuage",
            rate: 90f, speed: range * 1.3f, size: 0.3f, growth: 8f,
            spread: 9f, gravity: -0.05f, noise: 0.45f, lifetimeScale: 1.15f));

        // La trainee : ce qui flotte encore quand on a relache, et qui monte doucement.
        layers.Add(BuildLayer("Jet_Trainee",
            rate: 35f, speed: range * 0.5f, size: 0.5f, growth: 6f,
            spread: 22f, gravity: -0.22f, noise: 0.6f, lifetimeScale: 2.4f));

        return true;
    }

    private ParticleSystem BuildLayer(string name, float rate, float speed, float size,
                                      float growth, float spread, float gravity,
                                      float noise, float lifetimeScale)
    {
        // Toutes les couches vivent sous une racine commune, dont la rotation suit le regard
        // du porteur. Les attacher directement a la buse les figerait sur l'orientation de
        // l'objet tenu, qui ne monte ni ne descend.
        if (sprayRoot == null)
        {
            sprayRoot = new GameObject("Jet").transform;
            sprayRoot.SetParent(nozzle, false);
        }

        var holder = new GameObject(name);
        holder.transform.SetParent(sprayRoot, false);

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

        // L'evasement : les particules restent fines au depart puis s'ouvrent d'un coup vers
        // la fin. Une courbe lineaire donnerait un cone regulier, sans le renflement qui fait
        // lire le jet comme quelque chose de puissant qui se disperse en arrivant.
        var flare = new AnimationCurve(
            new Keyframe(0f, 0.25f),
            new Keyframe(0.45f, 0.6f),
            new Keyframe(1f, growth));

        var overLife = effect.sizeOverLifetime;
        overLife.enabled = true;
        overLife.size = new ParticleSystem.MinMaxCurve(1f, flare);

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
