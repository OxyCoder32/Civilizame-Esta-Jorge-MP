using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using ExitGames.Client.Photon;
using Photon.Realtime;
using UnityEngine;
using CivilizameMP.Core;

namespace CivilizameMP.Network
{
    public class PhotonManager : MonoBehaviour, IConnectionCallbacks, IMatchmakingCallbacks, IInRoomCallbacks, IOnEventCallback, ILobbyCallbacks
    {
        public static PhotonManager Instance { get; private set; }
        public bool IsConnected => _client?.IsConnectedAndReady ?? false;
        public bool IsInRoom => _client?.InRoom ?? false;
        public bool IsMasterClient => _client?.LocalPlayer?.IsMasterClient ?? false;
        public string RoomName => _client?.CurrentRoom?.Name;
        internal LoadBalancingClient _client;

        public event Action OnConnectedToMasterEvent;
        public event Action OnDisconnectedEvent;
        public event Action OnJoinedRoomEvent;
        public event Action<Player> OnPlayerJoinedEvent;
        public event Action<Player> OnPlayerLeftEvent;
        public event Action<byte[], int, string, int> OnStateReceived;
        public event Action<string> OnConfigReceived;
        public event Action<int, bool> OnRemoteReadyEvent;
        public event Action<short, string> OnJoinFailedEvent;
        public event Action<List<RoomInfo>> OnRoomListUpdateEvent;
        public event Action<bool> OnMasterClientSwitchedEvent;

        private const byte STATE_EVENT = 1;
        private const byte CONFIG_EVENT = 2;
        private const byte READY_EVENT = 3;

        private bool _isConnecting;
        private float _connectionTimeout;
        private int _stateSequence = 0;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void Connect()
        {
            string appId = MPConfig.PhotonAppID.Value;
            if (string.IsNullOrEmpty(appId) || appId == "YOUR_PHOTON_APPID_HERE")
            {
                CivilizameMPPlugin.Log.LogError("[Photon] AppID invalido");
                return;
            }
            Disconnect();
            _client = new LoadBalancingClient(ConnectionProtocol.Udp);
            _client.AddCallbackTarget(this);
            _client.AppId = appId;
            _client.AppVersion = MPConstants.GAME_VERSION;
            _client.AuthValues = new AuthenticationValues(Guid.NewGuid().ToString());
            _client.NickName = MPStateManager.Instance?.LocalPlayerName ?? MPConfig.DefaultPlayerName.Value;
            _isConnecting = true;
            _connectionTimeout = Time.time + 10f;
            if (!_client.ConnectToRegionMaster("eu"))
                _isConnecting = false;
        }

        void Update()
        {
            _client?.Service();
            if (_isConnecting && Time.time > _connectionTimeout)
            {
                _isConnecting = false;
                OnDisconnectedEvent?.Invoke();
            }
        }

        void OnDestroy()
        {
            Disconnect();
        }

        public void Disconnect()
        {
            if (_client == null) return;
            _client.RemoveCallbackTarget(this);
            if (_client.InRoom) _client.OpLeaveRoom(false);
            _client.Disconnect();
            _client = null;
            _isConnecting = false;
        }

        public void CreateRoom(string roomName, int maxPlayers = 20)
        {
            if (!IsConnected) return;
            var opts = new EnterRoomParams
            {
                RoomName = roomName,
                RoomOptions = new RoomOptions
                {
                    MaxPlayers = (byte)maxPlayers,
                    IsVisible = true,
                    IsOpen = true,
                    PublishUserId = true,
                    EmptyRoomTtl = 0
                }
            };
            _client.OpCreateRoom(opts);
        }

        public void JoinRoom(string roomName)
        {
            if (!IsConnected) return;
            var opts = new EnterRoomParams { RoomName = roomName };
            _client.OpJoinRoom(opts);
        }

        public void JoinRandomRoom()
        {
            if (!IsConnected) return;
            _client.OpJoinRandomRoom(new OpJoinRandomRoomParams());
        }

        public void LeaveRoom()
        {
            if (IsInRoom) _client.OpLeaveRoom(false);
        }

        // Envia estado a TODOS (incluido el propio host, para que HostStateProcessor lo procese)
        public void SendStateToAll(byte[] stateData)
        {
            if (!IsInRoom || stateData == null) return;
            int seq = GetNextSequence();
            string hash = ComputeHash(stateData);
            var payload = new Dictionary<string, object>
            {
                { "data", stateData },
                { "seq", seq },
                { "hash", hash }
            };
            var opts = new RaiseEventOptions { Receivers = ReceiverGroup.All };
            _client.OpRaiseEvent(STATE_EVENT, payload, opts, SendOptions.SendReliable);
        }

        // Envia estado al HOST (usado por cliente cuando termina su turno)
        public void SendStateToHost(byte[] stateData)
        {
            if (!IsInRoom || stateData == null) return;
            int seq = GetNextSequence();
            string hash = ComputeHash(stateData);
            var payload = new Dictionary<string, object>
            {
                { "data", stateData },
                { "seq", seq },
                { "hash", hash }
            };
            var opts = new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient };
            _client.OpRaiseEvent(STATE_EVENT, payload, opts, SendOptions.SendReliable);
        }

        public void SendConfig(string jsonConfig, int targetActorId = -1)
        {
            if (!IsInRoom) return;
            var opts = new RaiseEventOptions();
            if (targetActorId >= 0) opts.TargetActors = new[] { targetActorId };
            else opts.Receivers = ReceiverGroup.Others;
            _client.OpRaiseEvent(CONFIG_EVENT, jsonConfig, opts, SendOptions.SendReliable);
        }

        public void SendConfigToAll(string jsonConfig)
        {
            if (!IsInRoom) return;
            var opts = new RaiseEventOptions { Receivers = ReceiverGroup.All };
            _client.OpRaiseEvent(CONFIG_EVENT, jsonConfig, opts, SendOptions.SendReliable);
        }

        public void SendReady(bool ready)
        {
            if (!IsInRoom) return;
            _client.OpRaiseEvent(READY_EVENT, ready, new RaiseEventOptions(), SendOptions.SendReliable);
        }

        public int GetNextSequence()
        {
            return ++_stateSequence;
        }

        public static string ComputeHash(byte[] data)
        {
            if (data == null) return string.Empty;
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public Room GetCurrentRoom()
        {
            return _client?.CurrentRoom;
        }

        public Player[] GetPlayerList()
        {
            if (_client?.CurrentRoom?.Players == null) return new Player[0];
            var list = new List<Player>();
            foreach (var kvp in _client.CurrentRoom.Players)
                list.Add(kvp.Value);
            return list.ToArray();
        }

        void IConnectionCallbacks.OnConnectedToMaster()
        {
            _isConnecting = false;
            MPStateManager.Instance?.SetState(MPGameState.InMenu);
            OnConnectedToMasterEvent?.Invoke();
        }

        void IConnectionCallbacks.OnDisconnected(DisconnectCause cause)
        {
            _isConnecting = false;
            MPStateManager.Instance?.SetState(MPGameState.Disconnected);
            OnDisconnectedEvent?.Invoke();
        }

        void IConnectionCallbacks.OnConnected() { }
        void IConnectionCallbacks.OnRegionListReceived(RegionHandler regionHandler) { }
        void IConnectionCallbacks.OnCustomAuthenticationResponse(Dictionary<string, object> data) { }
        void IConnectionCallbacks.OnCustomAuthenticationFailed(string debugMessage) { }

        void IMatchmakingCallbacks.OnJoinedRoom()
        {
            if (_client?.LocalPlayer == null) return;
            var state = MPStateManager.Instance;
            state.LocalActorNumber = _client.LocalPlayer.ActorNumber;
            state.LocalPlayerName = _client.LocalPlayer.NickName;
            if (_client.CurrentRoom?.Players != null)
            {
                foreach (var kvp in _client.CurrentRoom.Players)
                {
                    var player = kvp.Value;
                    state.RegisterPlayer(player.ActorNumber, player.NickName, player.IsMasterClient);
                }
            }
            OnJoinedRoomEvent?.Invoke();
        }

        void IInRoomCallbacks.OnPlayerEnteredRoom(Player newPlayer)
        {
            if (newPlayer == null) return;
            MPStateManager.Instance.RegisterPlayer(newPlayer.ActorNumber, newPlayer.NickName, newPlayer.IsMasterClient);
            OnPlayerJoinedEvent?.Invoke(newPlayer);
        }

        void IInRoomCallbacks.OnPlayerLeftRoom(Player otherPlayer)
        {
            if (otherPlayer == null) return;
            MPStateManager.Instance.UnregisterPlayer(otherPlayer.ActorNumber);
            OnPlayerLeftEvent?.Invoke(otherPlayer);
        }

        void IInRoomCallbacks.OnMasterClientSwitched(Player newMasterClient)
        {
            bool isMaster = newMasterClient?.IsLocal == true;
            MPStateManager.Instance.IsHost = isMaster;
            MPStateManager.Instance.IsClient = !isMaster;
            OnMasterClientSwitchedEvent?.Invoke(isMaster);
        }

        void IInRoomCallbacks.OnRoomPropertiesUpdate(Hashtable propertiesThatChanged) { }
        void IInRoomCallbacks.OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps) { }

        void IMatchmakingCallbacks.OnCreateRoomFailed(short returnCode, string message)
        {
            MPStateManager.Instance.LastError = $"Create failed: {message}";
            MPStateManager.Instance.SetState(MPGameState.Disconnected);
        }

        void IMatchmakingCallbacks.OnJoinRandomFailed(short returnCode, string message)
        {
            MPStateManager.Instance.LastError = "No rooms available";
            MPStateManager.Instance.SetState(MPGameState.Disconnected);
        }

        void IMatchmakingCallbacks.OnJoinRoomFailed(short returnCode, string message)
        {
            MPStateManager.Instance.LastError = $"Join failed: {message}";
            OnJoinFailedEvent?.Invoke(returnCode, message);
            MPStateManager.Instance.SetState(MPGameState.Disconnected);
        }

        void IMatchmakingCallbacks.OnLeftRoom() { }
        void IMatchmakingCallbacks.OnFriendListUpdate(List<FriendInfo> friendList) { }
        void IMatchmakingCallbacks.OnCreatedRoom() { }

        void IOnEventCallback.OnEvent(EventData photonEvent)
        {
            if (photonEvent == null) return;
            int senderId = photonEvent.Sender;
            switch (photonEvent.Code)
            {
                case STATE_EVENT:
                    if (photonEvent.CustomData is Dictionary<string, object> statePayload)
                    {
                        var data = statePayload["data"] as byte[];
                        int seq = Convert.ToInt32(statePayload["seq"]);
                        string hash = statePayload["hash"] as string;
                        OnStateReceived?.Invoke(data, seq, hash, senderId);
                    }
                    break;
                case CONFIG_EVENT:
                    if (photonEvent.CustomData is string configData)
                    {
                        if (configData.Contains("\"hostStarting\":true"))
                        {
                            if (!MPStateManager.Instance.IsHost && ClientManager.Instance != null)
                                ClientManager.Instance.OnHostStartedGame();
                            return;
                        }
                        if (configData.Contains("\"ready\":true")) return;
                        OnConfigReceived?.Invoke(configData);
                    }
                    break;
                case READY_EVENT:
                    if (photonEvent.CustomData is bool ready)
                    {
                        OnRemoteReadyEvent?.Invoke(photonEvent.Sender, ready);
                        if (MPStateManager.Instance.IsHost && ready && HostManager.Instance != null)
                            HostManager.Instance.OnClientReady(photonEvent.Sender, ready);
                    }
                    break;
            }
        }

        void ILobbyCallbacks.OnRoomListUpdate(List<RoomInfo> roomList)
        {
            OnRoomListUpdateEvent?.Invoke(roomList);
        }

        void ILobbyCallbacks.OnJoinedLobby() { }
        void ILobbyCallbacks.OnLeftLobby() { }
        void ILobbyCallbacks.OnLobbyStatisticsUpdate(List<TypedLobbyInfo> lobbyStatistics) { }
    }
}