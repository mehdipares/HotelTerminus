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

    // Decoche par defaut : il a ete decide qu'un escalier arrete le chariot, et que les
    // etages se desservent par l'ascenseur. Ne coche cette case que pour un escalier ou l'on
    // veut deliberement laisser passer les objets physiques.
    private bool collisionRamp;

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
            collisionRamp = EditorGUILayout.Toggle("Rampe de collision", collisionRamp);

            var angle = Mathf.Atan2(stepHeight, stepDepth) * Mathf.Rad2Deg;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                $"Hauteur totale : {stepCount * stepHeight:0.00} m\n" +
                $"Longueur : {stepCount * stepDepth:0.00} m\n" +
                $"Pente equivalente : {angle:0.0}°\n\n" +
                "Rampe de collision : un plan incline invisible pose sur le nez des marches. " +
                "Sans lui, aucun objet physique ne peut monter — un Rigidbody bute sur une " +
                "face verticale, il n'y a aucune composante vers le haut. Le joueur, lui, " +
                "monte de toute facon tant que la marche ne depasse pas son Step Offset.",
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

        if (collisionRamp)
            BuildStairCollisionRamp(parent);
    }

    /// <summary>
    /// Plan incline invisible pose exactement sur le nez des marches.
    ///
    /// Sans lui, aucun objet physique ne monte un escalier : la boite d'un Rigidbody bute
    /// contre une contremarche verticale, dont la normale de contact est horizontale — il n'y
    /// a rien qui pousse vers le haut. C'est le procede employe par a peu pres tous les jeux.
    ///
    /// Contrepartie a assumer : le chariot **glisse** au lieu de cahoter marche apres marche.
    /// </summary>
    private void BuildStairCollisionRamp(Transform parent)
    {
        var run = stepCount * stepDepth;
        var rise = stepCount * stepHeight;
        var slope = Mathf.Sqrt(run * run + rise * rise);
        var angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;

        // Pas de MeshRenderer : uniquement un collider, donc invisible en jeu.
        var ramp = new GameObject("Rampe_de_collision");
        ramp.transform.SetParent(parent, false);

        var box = ramp.AddComponent<BoxCollider>();
        box.size = new Vector3(width, rampThickness, slope);

        ramp.transform.localRotation = Quaternion.Euler(-angle, 0f, 0f);

        // La surface utile joint le pied de l'escalier au nez de chaque marche : elle part
        // donc d'une profondeur de marche EN AMONT du premier bloc, sinon elle formerait une
        // lèvre contre laquelle le chariot buterait.
        var middle = new Vector3(0f, rise * 0.5f, run * 0.5f - stepDepth);

        ramp.transform.localPosition = middle
                                       - ramp.transform.localRotation * (Vector3.up * (rampThickness * 0.5f));
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
