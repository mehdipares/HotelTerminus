using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Repere de pose : le cube orange qui annonce ou le bagage vise va se poser.
///
/// **Purement local et sans reseau.** C'est un repere d'interface : chaque joueur voit le
/// sien, il n'a aucune existence pour les autres, et rien ici n'est repliqué. C'est ce qui
/// permet de le sortir du chariot sans precaution particuliere.
///
/// La separation de responsabilite : le chariot decide **quelle** place est libre, ce
/// composant decide **a quoi elle ressemble**. Le meme decoupage que Bulb / BulbVisual.
/// </summary>
public class CartPlacementGhost : MonoBehaviour
{
    [Tooltip("Taille du repere. Purement visuel : une place accueille un bagage quelle que " +
             "soit sa taille reelle.")]
    [SerializeField] private Vector3 size = new(0.5f, 0.4f, 0.55f);

    [SerializeField] private Color color = new(1f, 0.55f, 0.1f, 0.35f);

    [Tooltip("Facultatif. Laisse vide et le materiau se fabrique tout seul au lancement.")]
    [SerializeField] private Material material;

    private GameObject cube;
    private bool failed;                         // aucun shader utilisable, on a renonce

    /// <summary>Place le repere sur cet emplacement et l'affiche.</summary>
    public void ShowAt(Transform slot)
    {
        if (slot == null)
        {
            Hide();
            return;
        }

        EnsureCube();
        if (cube == null) return;

        cube.transform.SetParent(slot, false);
        cube.transform.localPosition = Vector3.zero;
        cube.transform.localRotation = Quaternion.identity;
        cube.transform.localScale = size;

        cube.SetActive(true);
    }

    public void Hide()
    {
        if (cube != null)
            cube.SetActive(false);
    }

    private void OnDestroy()
    {
        if (cube != null)
            Destroy(cube);
    }

    private void EnsureCube()
    {
        if (cube != null || failed) return;

        cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Repere_de_pose";

        // Un repere ne doit rien percuter, surtout pas le chariot qui le porte.
        Destroy(cube.GetComponent<Collider>());

        var renderer = cube.GetComponent<MeshRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        var applied = material != null ? material : BuildMaterial();

        if (applied == null)
        {
            // Aucun shader utilisable : on renonce plutot que d'afficher un cube rose au
            // milieu de l'ecran. Le champ Material permet de rattraper le coup.
            Debug.LogWarning("[Chariot] Pas de materiau pour le repere de pose. " +
                             "Assigne-en un dans le champ Material.");

            Destroy(cube);
            cube = null;
            failed = true;
            return;
        }

        renderer.sharedMaterial = applied;
        cube.SetActive(false);
    }

    /// <summary>
    /// Fabrique un materiau translucide, pour n'avoir aucun asset a preparer a la main. Si
    /// le shader est introuvable dans un build, le champ Material prend le relais.
    /// </summary>
    private Material BuildMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        if (shader == null) return null;

        var built = new Material(shader);

        built.SetFloat("_Surface", 1f);                 // transparent
        built.SetFloat("_ZWrite", 0f);
        built.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        built.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        built.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        built.renderQueue = (int)RenderQueue.Transparent;

        built.SetColor("_BaseColor", color);
        built.color = color;

        return built;
    }
}
