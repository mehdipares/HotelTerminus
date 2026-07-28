using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Outil d'editeur : transforme un modele importe en objet portable complet.
///
/// Monte la recette du projet d'un coup — racine a l'origine, modele en enfant, collider
/// ajuste, Rigidbody, NetworkObject, NetworkTransform en autorite serveur, NetworkRigidbody,
/// Carryable — cree les points d'ancrage **et les cable**, puis inscrit le prefab dans la
/// liste des prefabs reseau.
///
/// Il existe parce que le montage a la main a coute plusieurs heures : a chaque fois, un
/// champ de reference cree mais laisse vide, avec un symptome qui ressemblait a un bug de
/// code. Ici la reference est posee par le meme geste que l'objet.
///
/// Dans Assets/Editor, donc jamais inclus dans un build.
/// </summary>
public class CarryableBuilder : EditorWindow
{
    private const string PrefabFolder = "Assets/Prefabs";
    private const string NetworkPrefabsAsset = "Assets/DefaultNetworkPrefabs.asset";

    private GameObject model;
    private string prefabName = "";
    private float modelScale = 1f;

    private float mass = 5f;
    private bool continuousCollision;
    private string itemId = "";

    private bool addStowPoint = true;
    private bool addWallPlaceable;

    private bool registerAsNetworkPrefab = true;

    [MenuItem("Tools/HotelTerminus/Creer un objet portable")]
    private static void Open()
    {
        GetWindow<CarryableBuilder>("Objet portable");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Modele", EditorStyles.boldLabel);

        var previous = model;
        model = (GameObject)EditorGUILayout.ObjectField("Modele 3D", model, typeof(GameObject), false);

        // Nom pre-rempli au premier choix, sans ecraser ce que l'utilisateur a tape ensuite.
        if (model != previous && model != null && string.IsNullOrEmpty(prefabName))
            prefabName = model.name;

        prefabName = EditorGUILayout.TextField("Nom du prefab", prefabName);
        modelScale = EditorGUILayout.FloatField("Multiplicateur d'echelle", modelScale);

        EditorGUILayout.HelpBox(
            "1 = on garde l'echelle telle que le modele a ete importe. Si l'objet sort trop " +
            "petit ou trop grand, le bon reglage est le Scale Factor de l'importateur, sur " +
            "le fichier du modele lui-meme — ce champ n'est qu'un rattrapage.",
            MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Physique", EditorStyles.boldLabel);

        mass = EditorGUILayout.FloatField("Masse", mass);
        continuousCollision = EditorGUILayout.Toggle("Collision continue", continuousCollision);

        EditorGUILayout.HelpBox(
            "Collision continue : a cocher pour les objets petits et legers, comme une " +
            "ampoule. C'est ce qui les empeche de traverser le sol quand on les lance.",
            MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Points d'ancrage", EditorStyles.boldLabel);

        EditorGUILayout.LabelField("Grip Point", "toujours cree — comment on le tient");
        addStowPoint = EditorGUILayout.Toggle("Stow Point", addStowPoint);
        addWallPlaceable = EditorGUILayout.Toggle("Accrochable au mur", addWallPlaceable);

        EditorGUILayout.HelpBox(
            "Stow Point : la face du DESSOUS, pour se poser sur un chariot.\n" +
            "Mount Point : la face du DOS, pour s'accrocher a un mur.\n\n" +
            "Les deux sont crees a un emplacement plausible et cables. Il te restera a " +
            "les orienter : leur fleche verte finit vers le haut, la bleue vers l'avant.",
            MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Divers", EditorStyles.boldLabel);

        itemId = EditorGUILayout.TextField("Item Id", itemId);
        registerAsNetworkPrefab = EditorGUILayout.Toggle("Inscrire au reseau", registerAsNetworkPrefab);

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(model == null || string.IsNullOrWhiteSpace(prefabName)))
        {
            if (GUILayout.Button("Creer le prefab", GUILayout.Height(30)))
                Build();
        }
    }

    private void Build()
    {
        var root = new GameObject(prefabName);

        try
        {
            Assemble(root);
        }
        catch
        {
            DestroyImmediate(root);
            throw;
        }

        var path = AssetDatabase.GenerateUniqueAssetPath($"{PrefabFolder}/{prefabName}.prefab");
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);

        DestroyImmediate(root);

        if (registerAsNetworkPrefab)
            Register(prefab);

        AssetDatabase.SaveAssets();

        Selection.activeObject = prefab;
        EditorGUIUtility.PingObject(prefab);

        Debug.Log($"[Outil] {path} cree. Il reste a orienter les points d'ancrage et a " +
                  "verifier la boite de collision.");
    }

    private void Assemble(GameObject root)
    {
        // Racine vide a l'origine, modele en enfant : la convention du projet. Elle permet de
        // corriger l'orientation ou la hauteur d'un modele importe sans toucher au fichier
        // source, et garde l'origine de l'objet la ou on l'attend.
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
        visual.name = "Modele";
        visual.transform.SetParent(root.transform, false);

        // On MULTIPLIE l'echelle du modele au lieu de la remplacer : beaucoup d'imports
        // portent deja un facteur dans leur racine, et l'ecraser reduisait l'objet a rien.
        visual.transform.localScale = Vector3.Scale(visual.transform.localScale,
                                                    Vector3.one * modelScale);

        var bounds = LocalBounds(root);

        var box = root.AddComponent<BoxCollider>();
        box.center = bounds.center;
        box.size = bounds.size;

        var body = root.AddComponent<Rigidbody>();
        body.mass = mass;
        body.collisionDetectionMode = continuousCollision
            ? CollisionDetectionMode.Continuous
            : CollisionDetectionMode.Discrete;

        root.AddComponent<NetworkObject>();

        // Autorite serveur : c'est la regle du projet pour les objets du monde. Seul l'avatar
        // du joueur et le chariot conduit font exception.
        var netTransform = root.AddComponent<NetworkTransform>();
        SetEnum(netTransform, "AuthorityMode", 0);

        root.AddComponent<NetworkRigidbody>();

        var carryable = root.AddComponent<Carryable>();

        var grip = CreateAnchor(root, "GripPoint",
            new Vector3(bounds.center.x, bounds.max.y, bounds.center.z));

        SetReference(carryable, "gripPoint", grip);
        SetString(carryable, "itemId", itemId);

        if (addStowPoint)
        {
            var stow = CreateAnchor(root, "StowPoint",
                new Vector3(bounds.center.x, bounds.min.y, bounds.center.z));

            SetReference(carryable, "stowPoint", stow);
        }

        if (!addWallPlaceable) return;

        var placeable = root.AddComponent<WallPlaceable>();

        var mount = CreateAnchor(root, "MountPoint",
            new Vector3(bounds.center.x, bounds.center.y, bounds.min.z));

        SetReference(placeable, "mountPoint", mount);
        SetVector(placeable, "footprint", bounds.size);
    }

    /// <summary>Encombrement du modele, exprime dans le repere de la racine.</summary>
    private static Bounds LocalBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
            return new Bounds(Vector3.zero, Vector3.one * 0.5f);

        var world = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
            world.Encapsulate(renderers[i].bounds);

        // La racine est a l'origine sans rotation ni echelle : le monde et son repere local
        // coincident, on se contente donc de recentrer.
        return new Bounds(world.center - root.transform.position, world.size);
    }

    private static Transform CreateAnchor(GameObject root, string name, Vector3 localPosition)
    {
        var anchor = new GameObject(name).transform;
        anchor.SetParent(root.transform, false);
        anchor.localPosition = localPosition;

        return anchor;
    }

    // ---------- Ecriture des champs prives ----------
    //
    // Par SerializedObject et non par affectation directe : ces champs sont prives, et c'est
    // tres bien ainsi. L'editeur, lui, a le droit d'y ecrire.

    private static void SetReference(Object target, string field, Object value)
    {
        var serialized = new SerializedObject(target);
        serialized.FindProperty(field).objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetString(Object target, string field, string value)
    {
        var serialized = new SerializedObject(target);
        serialized.FindProperty(field).stringValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetVector(Object target, string field, Vector3 value)
    {
        var serialized = new SerializedObject(target);
        serialized.FindProperty(field).vector3Value = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetEnum(Object target, string field, int value)
    {
        var serialized = new SerializedObject(target);
        var property = serialized.FindProperty(field);

        if (property == null) return;                 // le champ a change de nom dans NGO

        property.enumValueIndex = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>
    /// Inscrit le prefab dans la liste des prefabs reseau. Sans ca, l'objet ne peut pas
    /// apparaitre en cours de partie — l'oubli classique.
    /// </summary>
    private static void Register(GameObject prefab)
    {
        var list = AssetDatabase.LoadAssetAtPath<Object>(NetworkPrefabsAsset);

        if (list == null)
        {
            Debug.LogWarning($"[Outil] {NetworkPrefabsAsset} introuvable : prefab non inscrit.");
            return;
        }

        var serialized = new SerializedObject(list);
        var entries = serialized.FindProperty("List");

        if (entries == null)
        {
            Debug.LogWarning("[Outil] Format de liste inattendu : prefab non inscrit.");
            return;
        }

        for (var i = 0; i < entries.arraySize; i++)
        {
            if (entries.GetArrayElementAtIndex(i).FindPropertyRelative("Prefab").objectReferenceValue == prefab)
                return;                               // deja inscrit
        }

        entries.InsertArrayElementAtIndex(entries.arraySize);

        var entry = entries.GetArrayElementAtIndex(entries.arraySize - 1);
        entry.FindPropertyRelative("Override").enumValueIndex = 0;
        entry.FindPropertyRelative("Prefab").objectReferenceValue = prefab;

        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(list);
    }
}
