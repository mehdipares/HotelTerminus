using UnityEngine;

/// <summary>
/// Traduit l'intensite d'un foyer en flammes.
///
/// Separe de <see cref="Fireplace"/> comme <see cref="BulbVisual"/> l'est de
/// <see cref="Bulb"/> : la logique ne rend rien, le rendu ne decide rien.
///
/// **Les flammes sont la jauge.** Il n'y a aucune barre a l'ecran : un foyer qu'on arrose
/// retrecit et faiblit sous les yeux, et c'est comme ca qu'on sait si l'on est en train de
/// gagner. C'est aussi ce qui rend l'information lisible de loin, pour tous les joueurs a la
/// fois.
///
/// **L'effet n'est pas un objet reseau.** Chaque machine l'instancie chez elle a partir de
/// l'etat repliqué. Le repliquer enverrait en continu ce que tout le monde peut deduire —
/// meme raison que la lumiere des douilles et les particules d'eau.
/// </summary>
public class FireplaceVisual : MonoBehaviour
{
    [Tooltip("Le foyer suivi. Vide = celui porte par cet objet ou l'un de ses parents.")]
    [SerializeField] private Fireplace fireplace;

    [Tooltip("Ou naissent les flammes. Vide = cet objet.")]
    [SerializeField] private Transform flameAnchor;

    [Tooltip("L'effet a jouer. Prends vfx_Flames_01 du pack : c'est un feu continu qui brule " +
             "sur place, la ou les Flamethrower sont directionnels et les Explosion ponctuels.")]
    [SerializeField] private GameObject flamePrefab;

    [Header("Reponse a l'intensite")]
    [Tooltip("Taille des flammes quand le foyer est presque eteint, en part de sa taille " +
             "pleine. Jamais zero : un feu mourant doit rester visible jusqu'au bout.")]
    [Range(0.05f, 1f)]
    [SerializeField] private float minScale = 0.3f;

    [Tooltip("Debit de particules quand le foyer est presque eteint, en part du debit plein.")]
    [Range(0.05f, 1f)]
    [SerializeField] private float minRate = 0.25f;

    [Tooltip("Vitesse a laquelle les flammes suivent l'intensite. Basse = elles s'affaissent " +
             "doucement au lieu de sauter d'une taille a l'autre.")]
    [SerializeField] private float response = 4f;

    private GameObject instance;
    private ParticleSystem[] systems;
    private float[] baseRates;
    private Vector3 baseScale;

    private float target;                        // intensite visee, 0 a 1
    private float current;                       // intensite affichee, lissee
    private bool warned;

    private void Awake()
    {
        if (fireplace == null) fireplace = GetComponentInParent<Fireplace>();
        if (flameAnchor == null) flameAnchor = transform;
    }

    private void OnEnable()
    {
        if (fireplace != null)
            fireplace.IntensityChanged += OnIntensityChanged;

        target = fireplace != null ? fireplace.Normalized : 0f;
        current = target;

        Apply();
    }

    private void OnDisable()
    {
        if (fireplace != null)
            fireplace.IntensityChanged -= OnIntensityChanged;
    }

    private void OnDestroy()
    {
        if (instance != null)
            Destroy(instance);
    }

    private void OnIntensityChanged(float normalized) => target = normalized;

    private void Update()
    {
        if (Mathf.Approximately(current, target)) return;

        // Lisse : le feu s'affaisse au lieu de sauter d'une taille a l'autre a chaque message
        // reseau. C'est aussi ce qui rend l'extinction agreable a regarder.
        current = Mathf.MoveTowards(current, target, response * Time.deltaTime);

        Apply();
    }

    private void Apply()
    {
        // Rien n'est instancie tant que le foyer n'a jamais pris feu : un hotel plein de
        // foyers eteints ne coute alors rien.
        if (current > 0f && !EnsureInstance()) return;
        if (systems == null) return;

        var alive = current > 0.001f;

        if (alive)
        {
            // Deux leviers a la fois : les flammes retrecissent ET s'espacent. L'un seul ne
            // suffit pas — une flamme petite mais dense reste un feu vif.
            instance.transform.localScale = baseScale * Mathf.Lerp(minScale, 1f, current);

            for (var i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null) continue;

                var emission = systems[i].emission;
                emission.rateOverTimeMultiplier = baseRates[i] * Mathf.Lerp(minRate, 1f, current);
            }
        }

        foreach (var system in systems)
        {
            if (system == null) continue;

            if (alive && !system.isEmitting)
            {
                system.Play();
            }
            else if (!alive && system.isEmitting)
            {
                // Stop sans effacer : les flammes deja en l'air finissent leur course au lieu
                // de disparaitre d'un coup, comme les gouttes quand on ferme une vanne.
                system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }

    private bool EnsureInstance()
    {
        if (instance != null) return true;

        if (flamePrefab == null)
        {
            if (!warned)
            {
                warned = true;
                Debug.LogWarning($"[Feu] {name} n'a pas d'effet de flammes assigne.");
            }

            return false;
        }

        instance = Instantiate(flamePrefab, flameAnchor.position, flameAnchor.rotation, flameAnchor);
        baseScale = instance.transform.localScale;

        systems = instance.GetComponentsInChildren<ParticleSystem>(true);
        baseRates = new float[systems.Length];

        for (var i = 0; i < systems.Length; i++)
        {
            if (systems[i] == null) continue;

            // Les debits d'origine sont retenus : on module a partir d'eux, sans quoi chaque
            // variation d'intensite ecraserait le reglage de l'effet.
            baseRates[i] = systems[i].emission.rateOverTimeMultiplier;

            // On coupe tout de suite ce que le prefab jouerait de lui-meme : c'est l'etat
            // repliqué qui decide, pas le reglage de l'effet.
            systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        return true;
    }
}
