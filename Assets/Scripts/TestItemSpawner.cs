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

        var position = player.transform.position
                       + player.transform.forward * spawnDistance
                       + Vector3.up * spawnHeight;

        var instance = Instantiate(itemPrefab, position, Quaternion.identity);
        instance.GetComponent<NetworkObject>().Spawn();

        Debug.Log($"[Test] Valise posee devant le client {clientId}.");
    }
}
