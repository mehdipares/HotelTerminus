using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Fait naitre de nouveaux foyers autour de celui-ci.
///
/// **Composant a part, et volontairement.** <see cref="Fireplace"/> reste un endroit qui
/// brule, rien de plus : le generateur en surchauffe pourra allumer un feu sans que celui-ci
/// se mette a contaminer la piece, et un foyer de cheminee pourra bruler sans danger.
///
/// **Seul un foyer vigoureux se propage.** En dessous du seuil, il ne genere plus rien — donc
/// un feu qu'on arrose cesse de se repandre pendant qu'on s'en occupe. C'est ce qui recompense
/// d'attaquer le gros en priorite plutot que de gratter les petits.
///
/// Serveur uniquement : le de est lance une seule fois, comme la surtension qui grille les
/// ampoules et l'apparition des fuites. Un tirage local ferait naitre des foyers differents
/// chez chaque joueur.
/// </summary>
[RequireComponent(typeof(Fireplace))]
public class FireSpread : NetworkBehaviour
{
    [Header("Ce qui prend feu")]
    [Tooltip("Calque des surfaces inflammables. Meme principe que le calque Wall du placement " +
             "mural : sans lui, on ne saurait pas ou le feu a le droit de se propager.")]
    [SerializeField] private LayerMask flammableMask;

    [Tooltip("Rayon de recherche autour de ce foyer.")]
    [SerializeField] private float spreadRadius = 3.5f;

    [Header("Rythme")]
    [Tooltip("Intervalle entre deux tentatives, en secondes.")]
    [SerializeField] private float interval = 5f;

    [Range(0f, 1f)]
    [Tooltip("Chances qu'une tentative aboutisse.")]
    [SerializeField] private float chance = 0.5f;

    [Range(0f, 1f)]
    [Tooltip("Intensite minimale, en part du maximum, pour que ce foyer contamine. En dessous " +
             "il ne genere plus rien : arroser un foyer suspend donc sa propagation.")]
    [SerializeField] private float spreadThreshold = 0.6f;

    [Range(0f, 1f)]
    [Tooltip("Intensite de depart d'un foyer nouveau-ne, en part du maximum. Faible : il faut " +
             "qu'il ait le temps de grandir, et qu'on puisse l'etouffer vite si on le voit tot.")]
    [SerializeField] private float childIntensity = 0.25f;

    [Header("Taille du foyer engendre")]
    [Tooltip("Largeur d'objet qui correspond a un foyer de taille normale, en metres. Un " +
             "meuble deux fois plus large donnera un feu deux fois plus gros.")]
    [SerializeField] private float referenceWidth = 1f;

    [Tooltip("Echelle du foyer sur un objet de la largeur de reference.\n\n" +
             "**Absolue, et non relative au prefab.** L'echelle a laquelle le prefab a ete " +
             "enregistre n'influence donc pas les foyers engendres : un foyer de depart reduit " +
             "a la main ne donne pas naissance a des feux minuscules.")]
    [SerializeField] private float scaleForOneMeter = 1f;

    [SerializeField] private float minChildScale = 0.5f;
    [SerializeField] private float maxChildScale = 4f;

    [Header("Garde-fous")]
    [Tooltip("Distance minimale entre deux foyers. Sans elle, ils s'empilent au meme endroit " +
             "et l'incendie devient une bouillie illisible.\n\n" +
             "A garder plus petite que le mobilier : a 1,5 m, un meuble d'un metre de cote " +
             "colle a un foyer ne peut jamais prendre feu, tous ses points sont trop proches.")]
    [SerializeField] private float minSpacing = 0.8f;

    [Tooltip("Nombre d'emplacements tentes a chaque essai. Un seul tirage suffit a gaspiller " +
             "tout l'intervalle si le point tombe mal.")]
    [SerializeField] private int attemptsPerTick = 6;

    [Tooltip("Nombre maximal de foyers simultanes dans toute la partie.\n\n" +
             "Ce n'est pas un reglage d'equilibrage mais une protection : la simulation tourne " +
             "sur la machine d'un JOUEUR, pas sur un serveur dedie. Une propagation sans " +
             "plafond finirait par la mettre a genoux.")]
    [SerializeField] private int maxFireplaces = 24;

    [Tooltip("Le foyer a engendrer. Doit etre inscrit dans la liste des prefabs reseau.")]
    [SerializeField] private GameObject fireplacePrefab;

    private Fireplace fireplace;
    private float nextAttempt;
    private Transform source;                    // l'objet que ce foyer brule, s'il en a un

    // Les objets qui brulent deja. Serveur uniquement, et statique : c'est ce qui garantit
    // **un seul foyer par objet**. Un point tire au hasard sur la surface en faisait naitre
    // plusieurs sur le meme meuble, ce qui se lisait comme une nuee d'etincelles plutot que
    // comme un objet en feu.
    private static readonly HashSet<Transform> Burning = new();

    // Compte global des foyers actifs. Provisoire : il passera au FireManager, qui en fera
    // aussi l'etat "incendie maitrise".
    private static int activeCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        activeCount = 0;
        Burning.Clear();
    }

    /// <summary>
    /// Declare quel objet ce foyer brule. Serveur uniquement, appele juste apres la naissance.
    /// C'est ce qui reserve l'objet et empeche un second foyer d'y apparaitre.
    /// </summary>
    public void ServerSetSource(Transform target)
    {
        if (!IsServer || target == null) return;

        source = target;
        Burning.Add(target);
    }

    private void Awake()
    {
        fireplace = GetComponent<Fireplace>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        activeCount++;

        // Decale les tentatives entre foyers : sans ca, tous tirent dans la meme frame et la
        // propagation arrive par paquets.
        nextAttempt = Time.time + Random.Range(0f, interval);
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;

        activeCount = Mathf.Max(0, activeCount - 1);

        // L'objet redevient inflammable : un meuble eteint doit pouvoir reprendre feu.
        if (source != null)
            Burning.Remove(source);
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned || fireplacePrefab == null) return;
        if (Time.time < nextAttempt) return;

        nextAttempt = Time.time + Mathf.Max(0.5f, interval);

        // Un foyer mourant ou qu'on est en train d'arroser ne contamine plus.
        if (fireplace.Normalized < spreadThreshold) return;
        if (activeCount >= maxFireplaces) return;
        if (Random.value > chance) return;

        TrySpawnChild();
    }

    /// <summary>
    /// Cherche un objet inflammable a portee et l'embrase — **un seul foyer par objet**, a sa
    /// taille.
    ///
    /// Le foyer nait au sommet de l'objet et non sur un point tire au hasard de sa surface :
    /// un feu accroche au flanc d'un meuble se lit comme une etincelle, un feu pose dessus se
    /// lit comme un meuble qui brule.
    /// </summary>
    private void TrySpawnChild()
    {
        var candidates = Physics.OverlapSphere(transform.position, spreadRadius,
                                               flammableMask, QueryTriggerInteraction.Ignore);

        if (candidates.Length == 0) return;

        // Plusieurs essais : le premier objet tire peut deja bruler ou etre trop pres.
        for (var attempt = 0; attempt < Mathf.Max(1, attemptsPerTick); attempt++)
        {
            var target = candidates[Random.Range(0, candidates.Length)];
            var root = target.transform.root;

            if (Burning.Contains(root)) continue;

            var bounds = target.bounds;

            // Sur la face SUPERIEURE, a peine enfoncee. Un enfoncement proportionnel plongeait
            // les flammes a l'interieur d'un gros bloc, ou personne ne les voyait : cinq
            // centimetres suffisent a ce que la base morde dans l'objet.
            var spot = new Vector3(
                bounds.center.x,
                Mathf.Max(bounds.max.y - 0.05f, bounds.center.y),
                bounds.center.z);

            if (!IsFarEnough(spot)) continue;

            var child = Instantiate(fireplacePrefab, spot, Quaternion.identity);

            // Echelle imposee avant le spawn : elle part ainsi avec l'objet vers les clients,
            // au lieu d'arriver une frame plus tard.
            child.transform.localScale = Vector3.one * ChildScale(bounds);

            child.GetComponent<NetworkObject>().Spawn();

            if (child.TryGetComponent<Fireplace>(out var born))
                born.ServerSetIntensity(born.MaxIntensity * childIntensity);

            if (child.TryGetComponent<FireSpread>(out var spread))
                spread.ServerSetSource(root);

            Debug.Log($"[Feu] {name} embrase {target.name}.");
            return;
        }
    }

    /// <summary>
    /// Taille du foyer, deduite de l'emprise au sol de l'objet. Une armoire brule plus gros
    /// qu'une chaise, sans qu'on ait a le regler objet par objet.
    /// </summary>
    private float ChildScale(Bounds bounds)
    {
        var width = Mathf.Max(bounds.size.x, bounds.size.z);
        var scale = scaleForOneMeter * width / Mathf.Max(0.1f, referenceWidth);

        return Mathf.Clamp(scale, minChildScale, maxChildScale);
    }

    /// <summary>
    /// Aucun foyer trop pres ? Le garde-fou vaut entre objets voisins : la reservation par
    /// objet suffit deja a en interdire deux sur le meme meuble.
    /// </summary>
    private bool IsFarEnough(Vector3 spot)
    {
        // Triggers inclus : le collider d'un foyer en est un, sans quoi on ne verrait pas les
        // feux existants et ils s'empileraient.
        foreach (var other in Physics.OverlapSphere(spot, minSpacing, ~0, QueryTriggerInteraction.Collide))
        {
            if (other.GetComponentInParent<Fireplace>() != null) return false;
        }

        return true;
    }
}
