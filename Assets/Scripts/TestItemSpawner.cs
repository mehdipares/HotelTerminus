using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// TEMPORAIRE — outil de test uniquement.
/// Fait apparaitre un objet devant chaque joueur qui se connecte, pour pouvoir essayer le
/// ramassage sans rien placer a la main dans la scene.
/// A supprimer des que les objets seront poses dans le niveau.
/// </summary>
public class TestItemSpawner : MonoBehaviour
{
    [SerializeField] private GameObject itemPrefab;
    [Tooltip("Deux exemplaires seront poses : un neuf et un grille, pour comparer les etats.")]
    [SerializeField] private GameObject bulbPrefab;
    [SerializeField] private float spawnDistance = 2f;
    [SerializeField] private float spawnHeight = 0.5f;

    private void Start()
    {
        if (NetworkManager.Singleton == null) return;

        // L'hote passe aussi par ce callback : une valise apparait donc devant lui aussi.
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
    }

    private void OnClientConnected(ulong clientId)
    {
        // Spawner est une prerogative du serveur : un client qui le ferait creerait un objet
        // que personne d'autre ne verrait.
        if (!NetworkManager.Singleton.IsServer) return;

        StartCoroutine(SpawnForClient(clientId));
    }

    private IEnumerator SpawnForClient(ulong clientId)
    {
        if (itemPrefab == null) yield break;

        var spawnManager = NetworkManager.Singleton.SpawnManager;
        NetworkObject player = null;

        // L'avatar du joueur n'est pas toujours pret quand la connexion est signalee :
        // on patiente quelques frames plutot que de deviner un delai.
        for (var attempt = 0; attempt < 120 && player == null; attempt++)
        {
            player = spawnManager.GetPlayerNetworkObject(clientId);

            if (player == null)
                yield return null;
        }

        if (player == null)
        {
            Debug.LogWarning($"[Test] Avatar du client {clientId} introuvable, pas de valise.");
            yield break;
        }

        var origin = player.transform;

        SpawnAt(itemPrefab, origin, Vector3.zero);

        // Deux ampoules cote a cote : une neuve a gauche, une grillee a droite, pour
        // distinguer les deux etats d'un coup d'oeil.
        SpawnAt(bulbPrefab, origin, origin.right * -0.6f);

        var burnt = SpawnAt(bulbPrefab, origin, origin.right * 0.6f);
        if (burnt != null && burnt.TryGetComponent<Bulb>(out var bulb))
            bulb.ServerSetBurnt(true);

        Debug.Log($"[Test] Objets poses devant le client {clientId}.");
    }

    private GameObject SpawnAt(GameObject prefab, Transform origin, Vector3 offset)
    {
        if (prefab == null) return null;

        var position = origin.position
                       + origin.forward * spawnDistance
                       + Vector3.up * spawnHeight
                       + offset;

        var instance = Instantiate(prefab, position, Quaternion.identity);
        instance.GetComponent<NetworkObject>().Spawn();

        return instance;
    }
}
