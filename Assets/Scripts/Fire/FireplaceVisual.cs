using UnityEngine;

/// <summary>
/// Traduit l'etat d'un foyer en flammes.
///
/// Separe de <see cref="Fireplace"/> comme <see cref="BulbVisual"/> l'est de
/// <see cref="Bulb"/> : la logique ne rend rien, le rendu ne decide rien. Le jour ou tu
/// changes d'effet, seul ce composant bouge.
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

    private GameObject instance;
    private ParticleSystem[] systems;
    private bool warned;

    private void Awake()
    {
        if (fireplace == null) fireplace = GetComponentInParent<Fireplace>();
        if (flameAnchor == null) flameAnchor = transform;
    }

    private void OnEnable()
    {
        if (fireplace != null)
        {
            fireplace.BurningChanged += OnBurningChanged;
        }

        Apply(fireplace != null && fireplace.IsBurning);
    }

    private void OnDisable()
    {
        if (fireplace != null)
        {
            fireplace.BurningChanged -= OnBurningChanged;
        }
    }

    private void OnDestroy()
    {
        if (instance != null)
            Destroy(instance);
    }

    private void OnBurningChanged(bool burning) => Apply(burning);

    private void Apply(bool burning)
    {
        // Rien n'est instancie tant que le foyer n'a jamais pris feu : un hotel plein de
        // foyers eteints ne coute alors rien.
        if (burning && !EnsureInstance()) return;
        if (systems == null) return;

        foreach (var system in systems)
        {
            if (system == null) continue;

            if (burning)
            {
                system.Play();
            }
            else
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
        systems = instance.GetComponentsInChildren<ParticleSystem>(true);

        // On coupe tout de suite ce que le prefab jouerait de lui-meme : c'est l'etat
        // repliqué qui decide, pas le reglage de l'effet.
        foreach (var system in systems)
        {
            if (system != null)
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        return true;
    }
}
