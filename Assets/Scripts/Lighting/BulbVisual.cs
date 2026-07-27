using UnityEngine;

/// <summary>
/// Traduit l'etat d'une <see cref="Bulb"/> en apparence.
///
/// Volontairement separe de la logique : passer d'une simple teinte a un mesh de verre
/// noirci, un filament casse ou un bruit de claquement ne doit toucher que ce fichier.
/// Rien ici ne passe sur le reseau — chaque client applique le rendu de son cote a partir
/// de l'etat replique.
/// </summary>
[RequireComponent(typeof(Bulb))]
public class BulbVisual : MonoBehaviour
{
    [Tooltip("Renderers a teinter. Laisse vide pour prendre tous ceux de l'objet.")]
    [SerializeField] private Renderer[] targets;

    // Couleurs de debug volontairement irrealistes : vert = neuve, rouge = grillee.
    // A remplacer par un vrai contraste de matiere (verre clair / verre noirci) quand la
    // douille pilotera la lumiere.
    [Header("Teinte de base")]
    [SerializeField] private Color freshColor = new(0.1f, 1f, 0.2f);
    [SerializeField] private Color burntColor = new(1f, 0.08f, 0.08f);

    [Header("Emission")]
    [Tooltip("Une teinte seule se voit mal sur un modele texture ou en verre : l'emission, " +
             "elle, reste lisible quel que soit le materiau. Valeurs au-dela de 1 = HDR, " +
             "l'ampoule neuve brille franchement. Noir = aucune emission.")]
    [ColorUsage(false, true)]
    [SerializeField] private Color freshEmission = new(0.2f, 2.5f, 0.4f);
    [ColorUsage(false, true)]
    [SerializeField] private Color burntEmission = new(2.5f, 0.15f, 0.15f);

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private Bulb bulb;

    private void Awake()
    {
        bulb = GetComponent<Bulb>();

        if (targets == null || targets.Length == 0)
            targets = GetComponentsInChildren<Renderer>(true);

        // Abonnement des Awake : Bulb diffuse l'etat courant dans son OnNetworkSpawn,
        // qui arrive toujours apres les Awake.
        bulb.BurntChanged += Apply;
    }

    private void OnDestroy()
    {
        if (bulb != null)
            bulb.BurntChanged -= Apply;
    }

    private void Apply(bool burnt)
    {
        var color = burnt ? burntColor : freshColor;
        var emission = burnt ? burntEmission : freshEmission;

        foreach (var target in targets)
        {
            if (target == null) continue;

            var material = target.material;
            material.color = color;

            // Le mot-cle doit etre actif, sinon URP ignore _EmissionColor sur un materiau
            // importe qui n'avait pas d'emission.
            material.EnableKeyword("_EMISSION");
            material.SetColor(EmissionColorId, emission);
        }
    }
}
