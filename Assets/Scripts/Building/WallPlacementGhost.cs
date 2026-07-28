using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Previsualisation d'un accrochage mural : une copie translucide de l'objet, verte si
/// l'emplacement convient, rouge sinon.
///
/// **Purement local, aucune replication.** Chaque joueur voit le sien, il n'existe pas pour
/// les autres. Meme principe que le repere de pose du chariot.
///
/// La copie ne clone pas le prefab : elle recree seulement ses maillages. Cloner l'objet
/// entrainerait avec lui son NetworkObject, son Rigidbody et ses scripts — soit un second
/// evier fantome qui essaierait d'exister sur le reseau.
/// </summary>
public class WallPlacementGhost : MonoBehaviour
{
    [SerializeField] private Color validColor = new(0.25f, 1f, 0.35f, 0.4f);
    [SerializeField] private Color blockedColor = new(1f, 0.2f, 0.2f, 0.4f);

    private GameObject ghost;
    private Renderer[] renderers;
    private Material material;
    private Object source;                       // modele actuellement copie
    private bool failed;                         // aucun shader utilisable

    /// <summary>Affiche la previsualisation a cet endroit, dans cet etat.</summary>
    public void Show(WallPlaceable model, Vector3 position, Quaternion rotation, bool valid)
    {
        if (model == null)
        {
            Hide();
            return;
        }

        if (!Build(model)) return;

        ghost.transform.SetPositionAndRotation(position, rotation);

        // L'echelle est reprise a chaque affichage : les morceaux sont places dans le repere
        // local de l'objet, donc la racine du fantome doit porter la meme echelle que lui.
        // Sans ca, un modele a l'echelle 0,01 donnait des decalages cent fois trop grands.
        ghost.transform.localScale = model.transform.lossyScale;

        ghost.SetActive(true);

        if (material != null)
        {
            var color = valid ? validColor : blockedColor;
            material.SetColor("_BaseColor", color);
            material.color = color;
        }
    }

    public void Hide()
    {
        if (ghost != null)
            ghost.SetActive(false);
    }

    private void OnDestroy()
    {
        if (ghost != null) Destroy(ghost);
        if (material != null) Destroy(material);
    }

    /// <summary>
    /// Fabrique la copie si elle n'existe pas encore, ou si l'on previsualise un autre objet.
    /// </summary>
    private bool Build(WallPlaceable model)
    {
        if (failed) return false;
        if (ghost != null && ReferenceEquals(source, model)) return true;

        if (ghost != null) Destroy(ghost);

        material = material != null ? material : BuildMaterial();

        if (material == null)
        {
            Debug.LogWarning("[Placement] Aucun shader utilisable pour la previsualisation.");
            failed = true;
            return false;
        }

        ghost = new GameObject("Previsualisation");
        source = model;

        var root = model.transform;

        foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null) continue;

            var piece = new GameObject(filter.name);
            piece.transform.SetParent(ghost.transform, false);

            // On recopie la pose de chaque morceau relativement a la racine de l'objet, pour
            // que la silhouette soit fidele quel que soit l'assemblage du modele.
            piece.transform.localPosition = root.InverseTransformPoint(filter.transform.position);
            piece.transform.localRotation = Quaternion.Inverse(root.rotation) * filter.transform.rotation;
            piece.transform.localScale = Divide(filter.transform.lossyScale, root.lossyScale);

            piece.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;

            var renderer = piece.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        renderers = ghost.GetComponentsInChildren<Renderer>();
        ghost.SetActive(false);

        return renderers.Length > 0;
    }

    private static Vector3 Divide(Vector3 value, Vector3 by)
    {
        return new Vector3(
            Mathf.Approximately(by.x, 0f) ? value.x : value.x / by.x,
            Mathf.Approximately(by.y, 0f) ? value.y : value.y / by.y,
            Mathf.Approximately(by.z, 0f) ? value.z : value.z / by.z);
    }

    /// <summary>
    /// Materiau translucide fabrique par code, pour n'avoir aucun asset a preparer a la main.
    /// </summary>
    private static Material BuildMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
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
