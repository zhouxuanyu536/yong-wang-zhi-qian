using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Services.Authentication;
using UnityEngine.SceneManagement;
using System.Threading.Tasks;
using Unity.Netcode.Transports.UTP;
using Unity.Netcode;
using Unity.Networking.Transport.Relay;

using IEnumerator = System.Collections.IEnumerator;
using System.Threading;
public class GameLobby : NetworkBehaviour
{
    private const string KEY_RELAY_JOIN_CODE = "RelayJoinCode";

    public static GameLobby Instance;

    public event EventHandler OnCreateLobbyStarted;
    public event EventHandler OnCreateLobbyFailed;
    public event EventHandler OnJoinStarted;
    public event EventHandler OnQuickJoinFailed;
    public event EventHandler OnJoinFailed;
    public event EventHandler<OnLobbyListChangedEventArgs> OnLobbyListChanged;
    public class OnLobbyListChangedEventArgs : EventArgs
    {
        public List<Lobby> lobbyList;
    }

    private Lobby joinedLobby;
    private float heartbeatTimer;
    private float listLobbiesTimer;

    private GameObject LoadingCanvas;
    void Awake()
    {
        Instance = this;
        DontDestroyOnLoad(gameObject);
        InitializeUnityAuthenication();
        LoadingCanvas = GameObject.Find("LoadingCanvas");
        LoadingCanvas.SetActive(false);
        GameMultiplayer.Instance.ServerOnClientDisconnectedEvent += LetPlayerLeaveLobby;
    }

    private async void InitializeUnityAuthenication()
    {
        Debug.Log("isInitialized:" + (UnityServices.State != ServicesInitializationState.Initialized));
        // 检查是否已初始化
        if (UnityServices.State == ServicesInitializationState.Initialized)
        {
            Debug.Log("UGS already initialized, PlayerId: " + AuthenticationService.Instance.PlayerId);
        }

        // 尝试获取已保存的Profile
        string existingProfile = GetSavedProfile();

        InitializationOptions initializationOptions = new InitializationOptions();

        if (string.IsNullOrEmpty(existingProfile))
        {
            // 首次运行，生成新的唯一Profile
            string newProfile = GenerateUniqueProfile();
            initializationOptions.SetProfile(newProfile);
            SaveProfile(newProfile);
            Debug.Log($"Generated new profile: {newProfile}");
        }
        else
        {
            // 使用已有的Profile
            initializationOptions.SetProfile(existingProfile);
            Debug.Log($"Using existing profile: {existingProfile}");
        }

        // 初始化UGS
        await UnityServices.InitializeAsync(initializationOptions);

        // 检查是否已登录
        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            Debug.Log($"Signed in successfully, PlayerId: {AuthenticationService.Instance.PlayerId}");
        }
        else
        {
            Debug.Log($"Already signed in, PlayerId: {AuthenticationService.Instance.PlayerId}");
        }
    }
    private string savedProfileKey = "UnityAuthProfile";

    private string GetSavedProfile()
    {
        return PlayerPrefs.GetString(savedProfileKey, null);
    }
    
    private string GenerateUniqueProfile()
    {
        return Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    private void SaveProfile(string profile)
    {
        PlayerPrefs.SetString(savedProfileKey, profile);
        PlayerPrefs.Save();
    }

    // Update is called once per frame
    void Update()
    {
        HandleHeartbeat();
        HandlePeriodicListLobbies();
    }

    private void HandlePeriodicListLobbies()
    {
        if (joinedLobby == null && AuthenticationService.Instance.IsSignedIn
            && SceneManager.GetActiveScene().name == Loader.Scene.LobbyScene.ToString())
        {
            listLobbiesTimer -= Time.deltaTime;

            if (listLobbiesTimer <= 0f)
            {
                float listLobbiesTimerMax = 3f;
                listLobbiesTimer = listLobbiesTimerMax;
                ListLobbies();
            }
        }
    }

    private void HandleHeartbeat()
    {
        if (IsLobbyHost())
        {
            heartbeatTimer -= Time.deltaTime;
            if (heartbeatTimer <= 0f)
            {
                float heartbeatTimerMax = 15f;
                heartbeatTimer = heartbeatTimerMax;
                //将HeartbeatTimer 同步到LobbyService
                LobbyService.Instance.SendHeartbeatPingAsync(joinedLobby.Id);
            }
        }
    }

    public bool IsLobbyHost()
    {
        return joinedLobby != null && joinedLobby.HostId == AuthenticationService.Instance.PlayerId;
    }

    public async void ListLobbies()
    {
        try
        {
            QueryLobbiesOptions queryLobbiesOptions = new QueryLobbiesOptions
            {
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots,"0",QueryFilter.OpOptions.GT)
                }
            };
            QueryResponse queryResponse = await LobbyService.Instance.QueryLobbiesAsync(queryLobbiesOptions);
            OnLobbyListChanged?.Invoke(this, new OnLobbyListChangedEventArgs
            {
                lobbyList = queryResponse.Results
            });
        }
        catch (LobbyServiceException e)
        {
            Debug.Log(e);
        }
    }

    public async void LockLobby()
    {
        try
        {
            UpdateLobbyOptions updateOptions = new UpdateLobbyOptions
            {
                IsLocked = true,
            };
            await LobbyService.Instance.UpdateLobbyAsync(joinedLobby.Id, updateOptions);
        }
        catch (LobbyServiceException e)
        {
            Debug.Log(e);
        }
    }
    public async void UnlockLobby()
    {
        try
        {
            UpdateLobbyOptions updateOptions = new UpdateLobbyOptions
            {
                IsLocked = false,
            };
            await LobbyService.Instance.UpdateLobbyAsync(joinedLobby.Id, updateOptions);
        }
        catch (LobbyServiceException e)
        {
            Debug.Log(e);
        }
    }
    private async Task<Allocation> AllocateRelay()
    {
        try
        {
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(GameMultiplayer.LOBBY_MAX_PLAYER_NUM);
            return allocation;
        }
        catch (RelayServiceException e)
        {
            Debug.Log(e);
            return default;
        }
    }

    private async Task<string> GetRelayJoinCode(Allocation allocation)
    {
        try
        {
            string relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            return relayJoinCode;
        }
        catch (RelayServiceException e)
        {
            Debug.Log(e);
            return default;
        }

    }

    private async Task<JoinAllocation> JoinRelay(string joinCode)
    {
        try
        {
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
            return joinAllocation;
        }
        catch (RelayServiceException e)
        {
            Debug.Log(e);
            return default;
        }
    }

    [Range(0,1)] private float PreLoadingProgress;

    public float GetPreLoadingProgress()
    {
        return PreLoadingProgress;
    }

    private void UpdateProgress(float progress,string message,float showDuration = -1f)
    {
        LoadingCanvas canvas = LoadingCanvas.GetComponent<LoadingCanvas>();
        canvas.UpdateProgress(progress * PreLoadingProgress, message);
        if(showDuration != -1f)
        {
            //等待5秒
            StartCoroutine(ShowCanvasCoroutine(showDuration));
        }
    }

    private IEnumerator ShowCanvasCoroutine(float showDuration)
    {
        yield return new WaitForSeconds(showDuration);
        LoadingCanvas.SetActive(false);
    }
    private const int TimeoutMilliseconds = 10000;

    private async Task<T> WithTimeout<T>(Task<T> task, int milliseconds)
    {
        milliseconds = 100000000;
        using (var cts = new CancellationTokenSource(milliseconds))
        {
            var delayTask = Task.Delay(milliseconds, cts.Token);
            Debug.Log("taskDelay");
            var completedTask = await Task.WhenAny(task, delayTask);
            Debug.Log("taskDelay2");
            if (completedTask == delayTask)
            {
                throw new TimeoutException("操作超时！");
            }

            cts.Cancel(); // 取消 delay
            return await task; // 返回原始任务结果
        }
    }
    public async void CreateLobby(string lobbyName, bool isPrivate)
    {
       
        OnCreateLobbyStarted?.Invoke(this, EventArgs.Empty);
        PreLoadingProgress = 0.8f;
        try
        {
            LoadingCanvas.SetActive(true);
            UpdateProgress(0.1f, "创建房间...");
            joinedLobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName,GameMultiplayer.LOBBY_MAX_PLAYER_NUM, new CreateLobbyOptions
            {
                IsPrivate = isPrivate,
            });
            Debug.Log("playerId:" + AuthenticationService.Instance.PlayerId);
            Debug.Log("lobbyId:" + joinedLobby.Id);
            UpdateProgress(0.3f, "分配中继服务器...");
            Allocation allocation = await AllocateRelay();

            UpdateProgress(0.5f, "获取中继连接代码...");
            string relayJoinCode = await GetRelayJoinCode(allocation);
            UpdateProgress(0.7f, "更新房间数据...");
            await LobbyService.Instance.UpdateLobbyAsync(joinedLobby.Id, new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    {KEY_RELAY_JOIN_CODE,new DataObject(DataObject.VisibilityOptions.Member,relayJoinCode)}
                }
            });
            UpdateProgress(0.85f, "连接中继服务器...");
            // dtls 使用安全传输
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(new RelayServerData(allocation, "dtls"))
                ;
            UpdateProgress(1.0f, "启动主机...");
            //StartHost
            GameMultiplayer.Instance.StartHost();
            LoadingCanvas.SetActive(false);
            Loader.LoadNetwork(Loader.Scene.InLobbyScene);

        }
        catch (TimeoutException tex)
        {
            Debug.LogWarning("操作超时: " + tex.Message);
            OnJoinFailed?.Invoke(this, EventArgs.Empty);
            UpdateProgress(-1f, "连接超时", 2f);
        }
        catch (Exception e)
        {
            Debug.Log(e);
            UpdateProgress(-1f, "创建失败", 2f);
        }
    }

    
    public async void QuickJoin()
    {
        OnJoinStarted?.Invoke(this, EventArgs.Empty);
        PreLoadingProgress = 0.9f;
        UpdateProgress(0f, "开始加入...");
        try
        {
            LoadingCanvas.SetActive(true);
            UpdateProgress(0.25f, "加入房间...");
            joinedLobby = await WithTimeout(LobbyService.Instance.QuickJoinLobbyAsync(),TimeoutMilliseconds);

            UpdateProgress(0.5f, "获取中继连接代码...");
            string relayJoinCode = joinedLobby.Data[KEY_RELAY_JOIN_CODE].Value;

            UpdateProgress(0.75f, "正在加入中继服务器...");
            JoinAllocation joinAllocation = await JoinRelay(relayJoinCode);

            // dtls 使用安全传输
            UpdateProgress(1.0f, "连接中继服务器...");
            NetworkManager.Singleton.GetComponent<UnityTransport>()
                .SetRelayServerData(new RelayServerData(joinAllocation, "dtls"));
            GameMultiplayer.Instance.StartClient();

            
        }
        catch (TimeoutException tex)
        {
            Debug.LogWarning("操作超时: " + tex.Message);
            OnJoinFailed?.Invoke(this, EventArgs.Empty);
            UpdateProgress(-1f, "连接超时", 2f);
        }
        catch (Exception e)
        {
            Debug.Log(e);
            OnQuickJoinFailed?.Invoke(this, EventArgs.Empty);
            UpdateProgress(-1f, "快速加入失败", 2f);
        }
    }

    public async void JoinWithId(string lobbyId)
    {
        OnJoinStarted?.Invoke(this, EventArgs.Empty);
        PreLoadingProgress = 0.9f;
        try
        {
            
            LoadingCanvas.SetActive(true);
            UpdateProgress(0f, "加入房间...");
            joinedLobby = await WithTimeout(LobbyService.Instance.JoinLobbyByIdAsync(lobbyId), TimeoutMilliseconds);
            UpdateProgress(0.3f, "获取中继连接代码...");
            string relayJoinCode = joinedLobby.Data[KEY_RELAY_JOIN_CODE].Value;
            UpdateProgress(0.6f, "正在加入中继服务器...");
            JoinAllocation joinAllocation = await JoinRelay(relayJoinCode);

            UpdateProgress(0.8f, "配置中继服务器...");
            // dtls 使用安全传输
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData
                (new RelayServerData(joinAllocation, "dtls"));
            UpdateProgress(1.0f, "正在连接客户端...");
            GameMultiplayer.Instance.StartClient();
        }
        catch (TimeoutException tex)
        {
            Debug.LogWarning("操作超时: " + tex.Message);
            OnJoinFailed?.Invoke(this, EventArgs.Empty);
            UpdateProgress(-1f, "连接超时", 2f);
        }
        catch (Exception e)
        {
            Debug.Log(e);
            OnJoinFailed?.Invoke(this, EventArgs.Empty);
            UpdateProgress(-1f, "通过id加入失败", 2f);
        }

    }
    public async void JoinWithCode(string lobbyCode)
    {
        OnJoinStarted?.Invoke(this, EventArgs.Empty);
        PreLoadingProgress = 0.9f;
        try
        {
            LoadingCanvas.SetActive(true);
            UpdateProgress(0f, "加入房间...");
            joinedLobby = await WithTimeout(LobbyService.Instance.JoinLobbyByCodeAsync(lobbyCode), TimeoutMilliseconds);
            Debug.Log(joinedLobby.Name);
            UpdateProgress(0.3f, "获取中继连接代码...");
            string relayJoinCode = joinedLobby.Data[KEY_RELAY_JOIN_CODE].Value;
            UpdateProgress(0.6f, "正在加入中继服务器...");
            JoinAllocation joinAllocation = await JoinRelay(relayJoinCode);
            UpdateProgress(0.8f, "配置中继服务器...");
            // dtls 使用安全传输
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(new RelayServerData(joinAllocation, "dtls"));
            UpdateProgress(1.0f, "正在连接客户端...");
            GameMultiplayer.Instance.StartClient();
        }
        catch (TimeoutException tex)
        {
            Debug.LogWarning("操作超时: " + tex.Message);
            OnJoinFailed?.Invoke(this, EventArgs.Empty);
            UpdateProgress(-1f, "连接超时", 2f);
        }
        catch (Exception e)
        {
            Debug.Log(e);
            OnJoinFailed?.Invoke(this, EventArgs.Empty);
            UpdateProgress(-1f, "通过代码加入失败", 2f);
        }

    }
    public async void KickPlayer(string playerId)
    {
        if (IsLobbyHost())
        {
            try
            {
                await LobbyService.Instance.RemovePlayerAsync(joinedLobby.Id, playerId);
            }
            catch (Exception e)
            {
                Debug.Log(e);
            }
        }
    }

    public void QuitLobby()
    {
        if (GameMultiplayer.Instance.IsServer)
        {
            DeleteLobby();
        }
        else
        {
            LeaveLobby();
        }
    }
    public async void LeaveLobby()
    {
        if (joinedLobby != null)
        {
            try
            {
                await LobbyService.Instance.RemovePlayerAsync(joinedLobby.Id, AuthenticationService.Instance.PlayerId);
            }
            catch (LobbyServiceException e)
            {
                Debug.Log(e);
            }
            joinedLobby = null;
        }
    }
    private async void LetPlayerLeaveLobby(object sender,PlayerEventArgs playerEventArgs)
    {
        try
        {
            int playerDataIndex = GameMultiplayer.Instance.GetPlayerDataIndexFromClientId(playerEventArgs.clientId);
            PlayerData playerData = GameMultiplayer.Instance.GetPlayerDataNetworkList()[playerDataIndex];
            await LobbyService.Instance.RemovePlayerAsync(GameLobby.Instance.joinedLobby.Id, playerData.playerId.ToString());
        }
        catch (Exception e)
        {
            Debug.Log(e);
        }
    }
    public async void DeleteLobby()
    {
        if (joinedLobby != null)
        {
            try
            {
                await LobbyService.Instance.DeleteLobbyAsync(joinedLobby.Id);
            }
            catch (LobbyServiceException e)
            {
                Debug.Log(e);
            }
            joinedLobby = null;
        }

    }

    public Lobby GetLobby()
    {
        return joinedLobby;
    }

    public bool IsServerOfLobby(PlayerData playerData)
    {
        return joinedLobby != null && playerData.playerId.ToString() == joinedLobby.HostId;
    }
    private void OnApplicationQuit()
    {
        QuitLobby();
    }

}
