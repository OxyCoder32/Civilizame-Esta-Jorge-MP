using System;
using System.Collections.Generic;
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
        public event Action<byte[]> OnStateReceived;
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

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            CivilizameMPPlugin.Log.LogInfo("[Photon] PhotonManager inicializado");
        }

        public void Connect()
        {
            string appId = MPConfig.PhotonAppID.Value;
            
            if (string.IsNullOrEmpty(appId))
            {
                CivilizameMPPlugin.Log.LogError("[Photon] AppID vacío. Configura en BepInEx/config/CivilizameMP.cfg");
                return;
            }

            if (appId == "YOUR_PHOTON_APPID_HERE")
            {
                CivilizameMPPlugin.Log.LogError("[Photon] AppID por defecto. Debes obtener una AppID gratis en https://dashboard.photonengine.com");
                return;
            }

            CivilizameMPPlugin.Log.LogInfo($"[Photon] Conectando con AppID: {appId.Substring(0, 4)}...");

            Disconnect();
            
            _client = new LoadBalancingClient(ConnectionProtocol.Udp);
            _client.AddCallbackTarget(this);
            _client.AppId = appId;
            _client.AppVersion = MPConstants.GAME_VERSION;
            _client.AuthValues = new AuthenticationValues(Guid.NewGuid().ToString());
            _client.NickName = MPStateManager.Instance?.LocalPlayerName ?? MPConfig.DefaultPlayerName.Value;
            
            CivilizameMPPlugin.Log.LogInfo($"[Photon] NickName: {_client.NickName}");
            
            _isConnecting = true;
            _connectionTimeout = Time.time + 10f;
            
            if (!_client.ConnectToRegionMaster("eu"))
            {
                CivilizameMPPlugin.Log.LogError("[Photon] Error al iniciar conexión");
                _isConnecting = false;
            }
            else
            {
                CivilizameMPPlugin.Log.LogInfo("[Photon] Conexión iniciada... esperando respuesta");
            }
        }

        void Update()
        {
            _client?.Service();
            
            if (_isConnecting && Time.time > _connectionTimeout)
            {
                _isConnecting = false;
                CivilizameMPPlugin.Log.LogError("[Photon] Timeout de conexión (10s)");
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
            
            CivilizameMPPlugin.Log.LogInfo("[Photon] Desconectado");
        }

        public void CreateRoom(string roomName, int maxPlayers = 20)
        {
            if (!IsConnected)
            {
                CivilizameMPPlugin.Log.LogError("[Photon] No conectado a Master, no se puede crear sala");
                return;
            }
            
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Creando sala: {roomName} (max: {maxPlayers} jugadores)");
            
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
            
            if (!_client.OpCreateRoom(opts))
            {
                CivilizameMPPlugin.Log.LogError("[Photon] Error al crear sala");
            }
        }

        public void JoinRoom(string roomName)
        {
            if (!IsConnected)
            {
                CivilizameMPPlugin.Log.LogError("[Photon] No conectado a Master, no se puede unir");
                return;
            }
            
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Uniéndose a sala: {roomName}");
            
            var opts = new EnterRoomParams { RoomName = roomName };
            if (!_client.OpJoinRoom(opts))
            {
                CivilizameMPPlugin.Log.LogError("[Photon] Error al unirse a sala");
            }
        }

        public void JoinRandomRoom()
        {
            if (!IsConnected) return;
            CivilizameMPPlugin.Log.LogInfo("[Photon] Buscando sala aleatoria...");
            _client.OpJoinRandomRoom(new OpJoinRandomRoomParams());
        }

        public void LeaveRoom()
        {
            if (IsInRoom)
            {
                CivilizameMPPlugin.Log.LogInfo("[Photon] Saliendo de sala...");
                _client.OpLeaveRoom(false);
            }
        }

        public void SendState(byte[] compressedState, int targetActorId = -1)
        {
            if (!IsInRoom) return;
            var opts = new RaiseEventOptions();
            if (targetActorId >= 0)
                opts.TargetActors = new[] { targetActorId };
            else
                opts.Receivers = ReceiverGroup.Others;
            _client.OpRaiseEvent(STATE_EVENT, compressedState, opts, SendOptions.SendReliable);
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

        public void SendStateToAll(byte[] compressedState)
        {
            if (!IsInRoom) return;
            var opts = new RaiseEventOptions { Receivers = ReceiverGroup.All };
            _client.OpRaiseEvent(STATE_EVENT, compressedState, opts, SendOptions.SendReliable);
        }

        public void SendReady(bool ready)
        {
            if (!IsInRoom) return;
            _client.OpRaiseEvent(READY_EVENT, ready, new RaiseEventOptions(), SendOptions.SendReliable);
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
            {
                list.Add(kvp.Value);
            }
            return list.ToArray();
        }

        void IConnectionCallbacks.OnConnectedToMaster()
        {
            _isConnecting = false;
            MPStateManager.Instance?.SetState(MPGameState.InMenu);
            CivilizameMPPlugin.Log.LogInfo("[Photon] Conectado a Master Server!");
            OnConnectedToMasterEvent?.Invoke();
        }

        void IConnectionCallbacks.OnDisconnected(DisconnectCause cause)
        {
            _isConnecting = false;
            MPStateManager.Instance?.SetState(MPGameState.Disconnected);
            CivilizameMPPlugin.Log.LogWarning($"[Photon] Desconectado: {cause}");
            OnDisconnectedEvent?.Invoke();
        }

        void IConnectionCallbacks.OnConnected()
        {
            CivilizameMPPlugin.Log.LogInfo("[Photon] Conectado al servidor");
        }

        void IConnectionCallbacks.OnRegionListReceived(RegionHandler regionHandler)
        {
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Regiones recibidas: {regionHandler?.EnabledRegions?.Count ?? 0}");
        }

        void IConnectionCallbacks.OnCustomAuthenticationResponse(Dictionary<string, object> data)
        {
            CivilizameMPPlugin.Log.LogInfo("[Photon] Autenticación personalizada");
        }

        void IConnectionCallbacks.OnCustomAuthenticationFailed(string debugMessage)
        {
            CivilizameMPPlugin.Log.LogError($"[Photon] Autenticación falló: {debugMessage}");
        }

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
                    state.RegisterPlayer(
                        player.ActorNumber,
                        player.NickName,
                        player.IsMasterClient
                    );
                }
            }
            
            CivilizameMPPlugin.Log.LogInfo($"[Photon] En sala: {_client.CurrentRoom?.Name}");
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Jugadores: {_client.CurrentRoom?.PlayerCount}/{_client.CurrentRoom?.MaxPlayers}");
            
            OnJoinedRoomEvent?.Invoke();
        }

        void IInRoomCallbacks.OnPlayerEnteredRoom(Player newPlayer)
        {
            if (newPlayer == null) return;
            
            MPStateManager.Instance.RegisterPlayer(
                newPlayer.ActorNumber,
                newPlayer.NickName,
                newPlayer.IsMasterClient
            );
            
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Jugador entró: {newPlayer.NickName} (Actor: {newPlayer.ActorNumber})");
            OnPlayerJoinedEvent?.Invoke(newPlayer);
        }

        void IInRoomCallbacks.OnPlayerLeftRoom(Player otherPlayer)
        {
            if (otherPlayer == null) return;
            
            MPStateManager.Instance.UnregisterPlayer(otherPlayer.ActorNumber);
            
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Jugador salió: {otherPlayer.NickName} (Actor: {otherPlayer.ActorNumber})");
            OnPlayerLeftEvent?.Invoke(otherPlayer);
        }

        void IInRoomCallbacks.OnMasterClientSwitched(Player newMasterClient)
        {
            bool isMaster = newMasterClient?.IsLocal == true;
            MPStateManager.Instance.IsHost = isMaster;
            MPStateManager.Instance.IsClient = !isMaster;
            CivilizameMPPlugin.Log.LogInfo($"[Photon] MasterClient cambiado: {newMasterClient?.NickName}, ¿Local es Master? {isMaster}");
            OnMasterClientSwitchedEvent?.Invoke(isMaster);
        }

        void IInRoomCallbacks.OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
        {
            CivilizameMPPlugin.Log.LogInfo("[Photon] Propiedades de sala actualizadas");
        }

        void IInRoomCallbacks.OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
        {
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Propiedades de {targetPlayer?.NickName} actualizadas");
        }

        void IMatchmakingCallbacks.OnCreateRoomFailed(short returnCode, string message)
        {
            MPStateManager.Instance.LastError = $"Create failed: {message}";
            MPStateManager.Instance.SetState(MPGameState.Disconnected);
            CivilizameMPPlugin.Log.LogError($"[Photon] Crear sala falló: {message} (code {returnCode})");
        }

        void IMatchmakingCallbacks.OnJoinRandomFailed(short returnCode, string message)
        {
            MPStateManager.Instance.LastError = "No rooms available";
            MPStateManager.Instance.SetState(MPGameState.Disconnected);
            CivilizameMPPlugin.Log.LogWarning($"[Photon] Unirse aleatorio falló: {message} (code {returnCode})");
        }

        void IMatchmakingCallbacks.OnJoinRoomFailed(short returnCode, string message)
        {
            CivilizameMPPlugin.Log.LogError($"[Photon] Unirse falló: {message} (code {returnCode})");
            MPStateManager.Instance.LastError = $"Join failed: {message}";
            OnJoinFailedEvent?.Invoke(returnCode, message);
            MPStateManager.Instance.SetState(MPGameState.Disconnected);
        }

        void IMatchmakingCallbacks.OnLeftRoom()
        {
            CivilizameMPPlugin.Log.LogInfo("[Photon] Sala abandonada");
        }

        void IMatchmakingCallbacks.OnFriendListUpdate(List<FriendInfo> friendList)
        {
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Lista de amigos actualizada: {friendList?.Count ?? 0}");
        }

        void IMatchmakingCallbacks.OnCreatedRoom()
        {
            CivilizameMPPlugin.Log.LogInfo("[Photon] Sala creada exitosamente");
        }

        void IOnEventCallback.OnEvent(EventData photonEvent)
        {
            if (photonEvent == null) return;
            
            switch (photonEvent.Code)
            {
                case STATE_EVENT:
                    if (photonEvent.CustomData is byte[] stateData)
                    {
                        CivilizameMPPlugin.Log.LogInfo($"[Photon] Estado recibido: {stateData.Length} bytes");
                        OnStateReceived?.Invoke(stateData);
                    }
                    break;
                case CONFIG_EVENT:
                    if (photonEvent.CustomData is string configData)
                    {
                        CivilizameMPPlugin.Log.LogInfo($"[Photon] Config recibida: {configData}");
                         
                        if (configData.Contains("\"hostStarting\":true"))
                        {
                            if (!MPStateManager.Instance.IsHost && ClientManager.Instance != null)
                            {
                                ClientManager.Instance.OnHostStartedGame();
                            }
                            return;
                        }

                        if (configData.Contains("\"ready\":true"))
                        {
                            CivilizameMPPlugin.Log.LogInfo("[Photon] Ready recibido como payload de configuración, ignorando");
                            return;
                        }
                         
                        OnConfigReceived?.Invoke(configData);
                    }
                    break;
                case READY_EVENT:
                    if (photonEvent.CustomData is bool ready)
                    {
                        CivilizameMPPlugin.Log.LogInfo($"[Photon] Ready recibido del Actor {photonEvent.Sender}: {ready}");
                        OnRemoteReadyEvent?.Invoke(photonEvent.Sender, ready);

                        if (MPStateManager.Instance.IsHost && ready && HostManager.Instance != null)
                        {
                            HostManager.Instance.OnClientReady(photonEvent.Sender, ready);
                        }
                    }
                    break;
            }
        }

        void ILobbyCallbacks.OnRoomListUpdate(List<RoomInfo> roomList)
        {
            CivilizameMPPlugin.Log.LogInfo($"[Photon] Lista de salas actualizada: {roomList?.Count ?? 0}");
            OnRoomListUpdateEvent?.Invoke(roomList);
        }

        void ILobbyCallbacks.OnJoinedLobby()
        {
            CivilizameMPPlugin.Log.LogInfo("[Photon] Unido al Lobby");
        }

        void ILobbyCallbacks.OnLeftLobby()
        {
            CivilizameMPPlugin.Log.LogInfo("[Photon] Salido del Lobby");
        }

        void ILobbyCallbacks.OnLobbyStatisticsUpdate(List<TypedLobbyInfo> lobbyStatistics)
        {
        }
    }
}