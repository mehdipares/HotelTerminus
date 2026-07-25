using System;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Point d'entree Steam + reseau : init du client Steam, pompe des callbacks,
/// creation/rejoint de lobby et demarrage de NGO (host / client).
/// </summary>
public class SteamManager : MonoBehaviour
{
    public static SteamManager Instance { get; private set; }

    [SerializeField] private uint appId = 480;          // 480 = Spacewar (sandbox de test)
    [SerializeField] private int maxPlayers = 5;

    public bool SteamReady { get; private set; }
    public Lobby? CurrentLobby { get; private set; }

    private FacepunchTransport facepunchTransport;       // null si on tourne sur un autre transport
    private string joinInput = "";                       // SteamId saisi a la main dans l'UI de test

    /// <summary>True si le transport actif est Facepunch (relais Steam), false en local (UnityTransport).</summary>
    public bool UsingSteamTransport => facepunchTransport != null;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        try
        {
            // asyncCallbacks = false : on pompe les callbacks nous-memes dans Update()
            SteamClient.Init(appId, false);
            SteamReady = true;
            Debug.Log($"[Steam] OK - {SteamClient.Name} ({SteamClient.SteamId})");
        }
        catch (Exception e)
        {
            SteamReady = false;
            Debug.LogError($"[Steam] Init impossible : {e.Message}");
        }
    }

    private void Start()
    {
        // On lit le transport reellement ACTIF (champ Network Transport), pas le simple composant :
        // Facepunch et UnityTransport peuvent cohabiter sur le NetworkManager.
        var active = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.NetworkConfig.NetworkTransport
            : null;

        facepunchTransport = active as FacepunchTransport;

        Debug.Log(UsingSteamTransport
            ? "[Net] Transport actif : Facepunch (relais Steam)"
            : $"[Net] Transport actif : {(active == null ? "aucun" : active.GetType().Name)} - mode local");

        SteamMatchmaking.OnLobbyCreated += OnLobbyCreated;
        SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
        SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
    }

    private void Update()
    {
        if (SteamReady)
            SteamClient.RunCallbacks();
    }

    private void OnDestroy()
    {
        if (Instance != this)
            return;

        SteamMatchmaking.OnLobbyCreated -= OnLobbyCreated;
        SteamMatchmaking.OnLobbyEntered -= OnLobbyEntered;
        SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;

        if (SteamReady)
            SteamClient.Shutdown();
    }

    private void OnApplicationQuit()
    {
        Disconnect();
    }

    // ---------- API publique ----------

    public async void Host()
    {
        if (!NetworkManager.Singleton.StartHost())
        {
            Debug.LogError("[Net] StartHost a echoue.");
            return;
        }

        // Le lobby Steam ne sert qu'au relais Facepunch : inutile en local.
        if (UsingSteamTransport && SteamReady)
            await SteamMatchmaking.CreateLobbyAsync(maxPlayers);
    }

    /// <summary>Rejoint un hote via le relais Steam.</summary>
    public void Join(SteamId hostSteamId)
    {
        if (!UsingSteamTransport)
        {
            Debug.LogWarning("[Net] Transport actif non-Steam : utilise JoinLocal().");
            return;
        }

        facepunchTransport.targetSteamId = hostSteamId;
        NetworkManager.Singleton.StartClient();
        Debug.Log($"[Steam] Connexion vers {hostSteamId}...");
    }

    /// <summary>Rejoint l'adresse configuree sur le transport local (127.0.0.1 par defaut).</summary>
    public void JoinLocal()
    {
        NetworkManager.Singleton.StartClient();
        Debug.Log("[Net] Connexion locale...");
    }

    public void Disconnect()
    {
        CurrentLobby?.Leave();
        CurrentLobby = null;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();
    }

    // ---------- Callbacks Steam ----------

    private void OnLobbyCreated(Result result, Lobby lobby)
    {
        if (result != Result.OK)
        {
            Debug.LogError($"[Steam] Creation du lobby echouee : {result}");
            return;
        }

        lobby.SetPublic();
        lobby.SetJoinable(true);
        lobby.SetData("HostSteamId", SteamClient.SteamId.ToString());
        CurrentLobby = lobby;

        Debug.Log($"[Steam] Lobby cree. SteamId hote a partager : {SteamClient.SteamId}");
    }

    private void OnLobbyEntered(Lobby lobby)
    {
        CurrentLobby = lobby;

        // L'hote est deja lance, il ne doit pas se reconnecter a lui-meme.
        if (NetworkManager.Singleton.IsHost) return;

        Join(lobby.Owner.Id);
    }

    private void OnGameLobbyJoinRequested(Lobby lobby, SteamId hostId)
    {
        // Declenche quand on accepte une invitation Steam / "Rejoindre la partie".
        lobby.Join();
    }

    // ---------- UI de test (IMGUI, a jeter plus tard) ----------

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 320, 200), GUI.skin.box);

        var nm = NetworkManager.Singleton;

        GUILayout.Label(UsingSteamTransport ? "Transport : Steam (Facepunch)" : "Transport : local");
        GUILayout.Label(SteamReady
            ? $"Steam : {SteamClient.Name} - {SteamClient.SteamId}"
            : "Steam : non initialise");

        if (nm != null && nm.IsListening)
        {
            GUILayout.Label(nm.IsHost ? "Mode : HOST" : "Mode : CLIENT");
            GUILayout.Label($"Mon clientId : {nm.LocalClientId}");
            // ConnectedClientsIds inclut l'hote lui-meme (clientId 0) : 1 = seul, 2 = un joueur distant.
            GUILayout.Label($"Joueurs dans la partie : {nm.ConnectedClientsIds.Count}");
            if (GUILayout.Button("Deconnecter")) Disconnect();
        }
        else
        {
            if (GUILayout.Button("Host")) Host();

            if (UsingSteamTransport)
            {
                GUILayout.Label("SteamId de l'hote :");
                joinInput = GUILayout.TextField(joinInput);

                if (GUILayout.Button("Join") && ulong.TryParse(joinInput, out var id))
                    Join(id);
            }
            else if (GUILayout.Button("Join (127.0.0.1)"))
            {
                JoinLocal();
            }
        }

        GUILayout.EndArea();
    }
}
