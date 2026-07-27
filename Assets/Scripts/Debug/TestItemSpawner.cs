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
    [Tooltip("Ampoules de rechange, toutes neuves.")]
    [SerializeField] private GameObject bulbPrefab;
    [SerializeField] private int spareBulbCount = 3;
    [SerializeField] private float spawnDistance = 2f;
    [SerializeField] private float spawnHeight = 0.5f;

    private bool alreadySpawned;

    private void Start()
    {
        if (NetworkManager.Singleton == null) return;

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

        // Une seule fournee pour toute la partie, posee devant l'hote. Sans ce garde-fou,
        // chaque joueur qui rejoint ajoutait sa propre valise et ses deux ampoules — et le
        // callback se declenche meme deux fois pour l'hote.
        if (alreadySpawned) return;

        alreadySpawned = true;
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

        // Rechanges alignees et centrees devant le joueur. Toutes neuves : les grillees
        // arrivent maintenant toutes seules, par la surtension au retour du courant.
        for (var i = 0; i < spareBulbCount; i++)
        {
            var lateral = (i - (spareBulbCount - 1) * 0.5f) * 0.5f;

            // Reculees d'un pas : avec un nombre impair, celle du milieu apparaitrait
            // sinon a l'interieur de la valise.
            SpawnAt(bulbPrefab, origin, origin.right * lateral - origin.forward * 0.8f);
        }

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
