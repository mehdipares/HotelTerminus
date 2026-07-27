using UnityEditor;
using UnityEngine;

/// <summary>
/// Outil d'editeur : fabrique escaliers et rampes de blockout.
///
/// Sert a monter vite un decor jouable pour tester le deplacement, le chariot et les pentes.
/// Ce fichier est dans Assets/Editor, il n'est donc jamais inclus dans un build.
/// </summary>
public class LevelShapeBuilder : EditorWindow
{
    private enum Shape
    {
        Escalier,
        Rampe,
    }

    private Shape shape = Shape.Escalier;

    private int stepCount = 12;
    private float stepHeight = 0.18f;      // hauteur d'une contremarche
    private float stepDepth = 0.3f;        // profondeur d'un giron
    private float width = 2f;

    private float rampLength = 6f;
    private float rampRise = 1.5f;
    private float rampThickness = 0.3f;

    [MenuItem("Tools/HotelTerminus/Escaliers et rampes")]
    private static void Open()
    {
        GetWindow<LevelShapeBuilder>("Blockout");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Forme", EditorStyles.boldLabel);
        shape = (Shape)EditorGUILayout.EnumPopup("Type", shape);

        EditorGUILayout.Space();

        if (shape == Shape.Escalier)
        {
            stepCount = Mathf.Max(1, EditorGUILayout.IntField("Nombre de marches", stepCount));
            stepHeight = EditorGUILayout.FloatField("Hauteur de marche", stepHeight);
            stepDepth = EditorGUILayout.FloatField("Profondeur de marche", stepDepth);
            width = EditorGUILayout.FloatField("Largeur", width);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                $"Hauteur totale : {stepCount * stepHeight:0.00} m\n" +
                $"Longueur : {stepCount * stepDepth:0.00} m\n\n" +
                "Le joueur ne montera une marche que si elle ne depasse pas le Step Offset " +
                "de son CharacterController. Au-dela il butera au pied de l'escalier.",
                MessageType.Info);
        }
        else
        {
            rampLength = Mathf.Max(0.1f, EditorGUILayout.FloatField("Longueur au sol", rampLength));
            rampRise = EditorGUILayout.FloatField("Denivele", rampRise);
            rampThickness = EditorGUILayout.FloatField("Epaisseur", rampThickness);
            width = EditorGUILayout.FloatField("Largeur", width);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                $"Pente : {Mathf.Atan2(rampRise, rampLength) * Mathf.Rad2Deg:0.0}°\n\n" +
                "Au-dela du Slope Limit du CharacterController (45° par defaut) le joueur " +
                "glissera au lieu de monter.",
                MessageType.Info);
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Creer", GUILayout.Height(30)))
            Build();
    }

    private void Build()
    {
        var root = new GameObject(shape == Shape.Escalier ? "Escalier" : "Rampe");
        Undo.RegisterCreatedObjectUndo(root, "Creer une forme de blockout");

        // Devant la camera de la vue Scene plutot qu'a l'origine : on construit la ou on
        // regarde, sans avoir a chercher l'objet ensuite.
        var view = SceneView.lastActiveSceneView;
        root.transform.position = view != null ? view.pivot : Vector3.zero;

        if (shape == Shape.Escalier)
            BuildStairs(root.transform);
        else
            BuildRamp(root.transform);

        Selection.activeGameObject = root;
        EditorGUIUtility.PingObject(root);
    }

    private void BuildStairs(Transform parent)
    {
        for (var i = 0; i < stepCount; i++)
        {
            // Chaque marche est un bloc plein partant du sol, et non une dalle posee en
            // l'air : sinon on peut passer dessous, et un objet physique s'y coince.
            var height = stepHeight * (i + 1);

            var step = CreateBlock(parent, $"Marche_{i + 1:00}");
            step.transform.localScale = new Vector3(width, height, stepDepth);
            step.transform.localPosition = new Vector3(0f, height * 0.5f, stepDepth * (i + 0.5f));
        }
    }

    private void BuildRamp(Transform parent)
    {
        var slope = Mathf.Sqrt(rampLength * rampLength + rampRise * rampRise);

        var ramp = CreateBlock(parent, "Plan_incline");
        ramp.transform.localScale = new Vector3(width, rampThickness, slope);

        // On pivote autour du bas de la pente, puis on descend la dalle de son epaisseur
        // pour que sa **face superieure** parte du niveau du sol.
        ramp.transform.localRotation = Quaternion.Euler(-Mathf.Atan2(rampRise, rampLength) * Mathf.Rad2Deg, 0f, 0f);
        ramp.transform.localPosition = new Vector3(0f, rampRise * 0.5f, rampLength * 0.5f)
                                       - ramp.transform.localRotation * (Vector3.up * (rampThickness * 0.5f));
    }

    private static GameObject CreateBlock(Transform parent, string name)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = name;
        block.transform.SetParent(parent, false);

        // Decor fixe : marque statique pour l'eclairage et l'occlusion.
        GameObjectUtility.SetStaticEditorFlags(block, StaticEditorFlags.ContributeGI
                                                      | StaticEditorFlags.OccluderStatic
                                                      | StaticEditorFlags.BatchingStatic);

        return block;
    }
}
