using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;
public class GameMultiplayer : NetworkBehaviour
{
    public static GameMultiplayer Instance;
    public const int LOBBY_MAX_PLAYER_NUM = 4;

    public event EventHandler OnTryingToJoinGame;
    public event EventHandler OnFailedToJoinGame;
    public event EventHandler OnPlayerDataNetworkListChanged;

    public event EventHandler<PlayerEventArgs> ServerOnClientDisconnectedEvent;
    public event EventHandler<PlayerEventArgs> ClientOnClientDisconnectedEvent;
    private NetworkList<PlayerData> playerDataNetworkList;
    public NetworkVariable<int> level = new NetworkVariable<int>(1);
    public NetworkVariable<int> playerLevelLoadFinishedCount = new NetworkVariable<int>(0);
    private string playerName = "";
    private const string PLAYER_NAME_KEY = "PlayerName";
    public static bool playMultiplayer = false;
    public static int playSinglePlayerLevel;
    public bool isSpawned;
    public bool isInLobbyInitialized;
    // Start is called before the first frame update
    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        DontDestroyOnLoad(gameObject);
        playerDataNetworkList = new NetworkList<PlayerData>();
        playerDataNetworkList.OnListChanged += PlayerDataNetworkList_OnListChanged;
        playerName = PlayerPrefs.GetString(PLAYER_NAME_KEY, null);

        if (string.IsNullOrEmpty(playerName))
        {
            // 首次启动，生成新名称
            playerName = GeneratePlayerName();
            PlayerPrefs.SetString(PLAYER_NAME_KEY, playerName);
            PlayerPrefs.Save();
            Debug.Log($"首次生成玩家名称: {playerName}");
        }
        else
        {
            Debug.Log($"加载已保存的玩家名称: {playerName}");
        }
        Audioplay.Instance.CanDestroy = true;
        isInLobbyInitialized = false;
    }

    private string GeneratePlayerName()
    {
        string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"Player_{uniqueId}";
    }

    private void Start()
    {
        if (!playMultiplayer)
        {
            var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
            if (transport != null)
            {
                transport.ConnectionData.Port = (ushort)Random.Range(1000, 20000);
            }
            StartHost();
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!playMultiplayer)
        {
            level.Value = playSinglePlayerLevel;
            Loader.LoadNetwork(Loader.Scene.GameScene);
        }
        isSpawned = true;
    }

    // Update is called once per frame
    void Update()
    {

    }

    public void StartHost()
    {
        NetworkManager.Singleton.ConnectionApprovalCallback += NetworkManager_ConnectionApprovalCallback;
        NetworkManager.Singleton.OnClientConnectedCallback += NetworkManager_OnClientConnectedCallback;
        NetworkManager.Singleton.OnClientDisconnectCallback += NetworkManager_Server_OnClientDisconnectCallback;
        NetworkManager.Singleton.StartHost();
    }
    public void StartClient()
    {
        NetworkManager.Singleton.StartClient();
        NetworkManager.Singleton.OnClientConnectedCallback += NetworkManager_Client_OnClientConnectedCallback;
        NetworkManager.Singleton.OnClientDisconnectCallback += NetworkManager_Client_OnClientDisconnectCallback;
    }
    private void PlayerDataNetworkList_OnListChanged(NetworkListEvent<PlayerData> changeEvent)
    {
        OnPlayerDataNetworkListChanged?.Invoke(this, EventArgs.Empty);
    }
    private void NetworkManager_ConnectionApprovalCallback(NetworkManager.ConnectionApprovalRequest connectionApprovalRequest, NetworkManager.ConnectionApprovalResponse connectionApprovalResponse)
    {
        if (SceneManager.GetActiveScene().name != Loader.Scene.InLobbyScene.ToString())
        {
            connectionApprovalResponse.Approved = false;
            connectionApprovalResponse.Reason = "Game has already started.";
        }
        if (NetworkManager.Singleton.ConnectedClientsIds.Count >= LOBBY_MAX_PLAYER_NUM)
        {
            connectionApprovalResponse.Reason = "Game is full.";
            connectionApprovalResponse.Approved = false;
            return;
        }
        connectionApprovalResponse.Approved = true;
    }
    private void NetworkManager_OnClientConnectedCallback(ulong clientId)
    {

        playerDataNetworkList.Add(new PlayerData
        {
            clientId = clientId,

            isReady = false,
            playerColor = new Color(255, 255, 0)
        });
        SetPlayerNameServerRpc(GetPlayerName());
        Debug.Log("playerId:" + AuthenticationService.Instance.PlayerId);
        SetPlayerIdServerRpc(AuthenticationService.Instance.PlayerId);
    }

    private void NetworkManager_Server_OnClientDisconnectCallback(ulong clientId)
    {
        try
        {
            Debug.Log("ServerClientDisconnect");
            ServerOnClientDisconnectedEvent?.Invoke(this, new PlayerEventArgs(clientId));
            for (int i = 0; i < playerDataNetworkList.Count; i++)
            {
                PlayerData playerData = playerDataNetworkList[i];
                if (playerData.clientId == clientId)
                {
                    playerDataNetworkList.RemoveAt(i);
                }
            }
        }
        catch { }
        
    }
    private void NetworkManager_Client_OnClientConnectedCallback(ulong clientId)
    {
        SetPlayerNameServerRpc(GetPlayerName());
        SetPlayerIdServerRpc(AuthenticationService.Instance.PlayerId);
    }
    private void NetworkManager_Client_OnClientDisconnectCallback(ulong clientId)
    {
        Debug.Log("ClientDisconnect");
        ClientOnClientDisconnectedEvent?.Invoke(this, new PlayerEventArgs(clientId));
        OnFailedToJoinGame?.Invoke(this, EventArgs.Empty);
    }
    [ServerRpc(RequireOwnership = false)]
    public void SetPlayerNameServerRpc(string playerName, ServerRpcParams serverRpcParams = default)
    {
        int playerDataIndex = GetPlayerDataIndexFromClientId(serverRpcParams.Receive.SenderClientId);

        PlayerData playerData = playerDataNetworkList[playerDataIndex];
        playerData.playerName = playerName;

        playerDataNetworkList[playerDataIndex] = playerData;
    }
    [ServerRpc(RequireOwnership = false)]
    private void SetPlayerIdServerRpc(string playerId, ServerRpcParams serverRpcParams = default)
    {
        if (playerId == default) return;
        int playerDataIndex = GetPlayerDataIndexFromClientId(serverRpcParams.Receive.SenderClientId);

        PlayerData playerData = playerDataNetworkList[playerDataIndex];

        playerData.playerId = playerId;

        playerDataNetworkList[playerDataIndex] = playerData;
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetPlayerColorServerRpc(Color playerColor, ServerRpcParams serverRpcParams = default)
    {
        int playerDataIndex = GetPlayerDataIndexFromClientId(serverRpcParams.Receive.SenderClientId);

        PlayerData playerData = playerDataNetworkList[playerDataIndex];

        Debug.Log("ChangePlayerColor:" + playerColor);
        if(playerColor.r <= 1 && playerColor.g < 1 && playerColor.b <= 1)
        {
            playerColor.r *= 255;
            playerColor.g *= 255;
            playerColor.b *= 255;
        }
        playerData.playerColor = playerColor;

        playerDataNetworkList[playerDataIndex] = playerData;

    }
    public void SetAllPlayerNotReady()
    {
        for(int i = 0;i < playerDataNetworkList.Count;i++)
        {
            SetPlayerIsReadyServerRpc(false,i);
        }
    }
    [ServerRpc(RequireOwnership = false)]
    public void SetPlayerIsReadyServerRpc(bool autoAdjust, int index = -1,ServerRpcParams serverRpcParams = default)
    {
       
        if (autoAdjust)
        {
            int playerDataIndex = GetPlayerDataIndexFromClientId(serverRpcParams.Receive.SenderClientId);

            PlayerData playerData = playerDataNetworkList[playerDataIndex];
            playerData.isReady = !playerData.isReady;
            playerDataNetworkList[playerDataIndex] = playerData;
        }
        else
        {
            if (index == -1) return;
            PlayerData playerData = playerDataNetworkList[index];
            playerData.isReady = false;
            playerDataNetworkList[index] = playerData;
        }



    }
    public int GetPlayerDataIndexFromClientId(ulong clientId)
    {
        for(int i = 0;i < playerDataNetworkList.Count; i++)
        {
            if (playerDataNetworkList[i].clientId == clientId)
            {
                return i;
            }
        }
        return -1;
    }
    
    public void SetPlayerName(string playerName)
    {
        this.playerName = playerName;
        if(PlayerPrefs.GetString("MultiPlayer") != null)
        {
            PlayerPrefs.SetString("Multiplayer", playerName);
        }
        else
        {
            PlayerPrefs.SetString("Multiplayer", "Player");
        }
    }
    public string GetPlayerName()
    {
        return playerName;
    }

    public ulong GetPlayerClientId()
    {
        return NetworkManager.Singleton.LocalClientId;
    }
    public NetworkList<PlayerData> GetPlayerDataNetworkList()
    {
        return playerDataNetworkList;
    }

    public PlayerData GetPlayerDataFromNetworkList(ulong clientId)
    {
        foreach(PlayerData playerData in playerDataNetworkList)
        {
            if(playerData.clientId == clientId)
            {
                return playerData;
            }
        }
        return default;
    }
    [ServerRpc(RequireOwnership = false)]
    public void KickPlayerServerRpc(ulong clientId)
    {
        NetworkManager.Singleton.DisconnectClient(clientId);
        NetworkManager_Server_OnClientDisconnectCallback(clientId);
    }
}
