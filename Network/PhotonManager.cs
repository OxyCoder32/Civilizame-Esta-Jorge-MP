using System;
using System.Collections.Generic;
using System.IO;
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
        public event Action<HandoffMessage, int> OnHandoffReceived;
        public event Action<string> OnConfigReceived;
        public event Action<int, bool> OnRemoteReadyEvent;
        public event Action<short, string> OnJoinFailedEvent;
        public event Action<List<RoomInfo>> OnRoomListUpdateEvent;
        public event Action<bool> OnMasterClientSwitchedEvent;

        private const byte CONFIG_EVENT = 2;
        private const byte READY_EVENT = 3;
        private const byte HANDOFF_CHUNK_EVENT = 4;
        private const byte LIVE_EVENT = 5;

        /// <summary>Eventos en vivo (movimientos, ataques...) del canal LiveSync.</summary>
        public event Action<byte[], int> OnLiveEvent;

        private const int CHUNK_SIZE = 24000; // dentro del límite de eventos UDP de Photon

        /// <summary>Respuesta de SendHandoff: el mensaje quedó en cola y se enviará al reconectar.</summary>
        public const int SEND_QUEUED = -2;

        // UserId único por instancia de juego. NO usamos PlayerPrefs compartido
        // porque host y cliente en la misma máquina colisionarían ("UserId already joined").
        // Cada instancia genera su propio GUID al despertar.
        private string _persistentUserId;

        private class OutgoingHandoff { public HandoffMessage Msg; public int Target; }
        private readonly List<OutgoingHandoff> _outbox = new List<OutgoingHandoff>();

        private string _lastRoomName;
        private bool _wantReconnect;
        private float _nextReconnectAt;

        private class IncomingTransfer
        {
            public int TotalChunks;
            public int PayloadLength;
            public byte[] Buffer;
            public bool[] Got;
            public int Received;
        }

        private readonly Dictionary<int, IncomingTransfer> _incoming = new Dictionary<int, IncomingTransfer>();
        private int _transferIdSeq;

        private bool _isConnecting;
        private float _connectionTimeout;
        private int _stateSequence = 0;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // UserId único por instancia de juego. NO usamos PlayerPrefs compartido
            // porque host y cliente en la misma máquina colisionarían ("UserId already joined").
            // Cada instancia genera su propio GUID al despertar (Core/MPIdentity.cs).
            _persistentUserId = MPIdentity.InstanceId;
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
            // UserId persistente por sesión: necesario para OpRejoinRoom (mismo actor)
            _client.AuthValues = new AuthenticationValues(_persistentUserId);
            _client.NickName = MPStateManager.Instance?.LocalPlayerName ?? MPConfig.DefaultPlayerName.Value;

            // La carga de mundos grandes congela el hilo principal muchos segundos:
            // ampliar el timeout evita cortes por "silencio" en cargas medianas.
            try
            {
                var peer = _client.LoadBalancingPeer;
                if (peer != null)
                    peer.DisconnectTimeout = 120000;   // ms
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[Photon] No se pudo ampliar DisconnectTimeout: {ex.Message}");
            }

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

            FlushOutbox();
            TryReconnect();
        }

        /// <summary>Reconexión transparente durante una partida: vuelve a conectarse y
        /// reentrar en la sala con el mismo UserId (mismo ActorNumber).</summary>
        private void TryReconnect()
        {
            if (!_wantReconnect) return;

            var st = MPStateManager.Instance;
            bool inMatch = st != null && (st.CurrentState >= MPGameState.PlayingHost && st.CurrentState <= MPGameState.PlayingClient);
            if (!inMatch) { _wantReconnect = false; return; }

            if (_client != null && _client.InRoom)
            {
                _wantReconnect = false;
                return;
            }
            if (Time.time < _nextReconnectAt) return;

            _nextReconnectAt = Time.time + 5f;
            CivilizameMPPlugin.Log.LogInfo("[Photon] Reintentando conexión...");
            Connect(); // reconstruye el cliente y OnConnectedToMaster hará el rejoin
        }

        /// <summary>Envía lo que quedó en cola cuando la sala vuelve a estar disponible.</summary>
        private void FlushOutbox()
        {
            if (_outbox.Count == 0) return;
            if (_client == null || !_client.InRoom) return;

            for (int i = _outbox.Count - 1; i >= 0; i--)
            {
                var o = _outbox[i];
                int r = SendNow(o.Msg, o.Target);
                if (r > 0)
                {
                    _outbox.RemoveAt(i);
                    CivilizameMPPlugin.Log.LogInfo($"[Photon] Handoff en cola enviado al reconectar ({o.Msg.JueBytes?.Length} B -> actor {o.Target})");
                }
                if (r < 0) break; // sin sala otra vez: esperar al próximo ciclo
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
                    EmptyRoomTtl = 0,
                    // Janela de rejoin: si la carga de un mapa grande corta la conexión,
                    // el jugador puede reentrar con el mismo ActorNumber durante 2 min.
                    PlayerTtl = 120000
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

        /// <summary>
        /// Envía un estado completo (turno) a un actor concreto. Con targetActor = -1
        /// lo envía al MasterClient (el host). Si no estamos en sala, el mensaje
        /// queda en COLA y se envía automáticamente al reconectar (devuelve
        /// SEND_QUEUED). Devuelve el id de transferencia >= 0 si se envió, -1 si falló.
        /// </summary>
        public int SendHandoff(HandoffMessage msg, int targetActor)
        {
            if (msg == null)
            {
                CivilizameMPPlugin.Log.LogError("[Photon] SendHandoff: mensaje nulo");
                return -1;
            }
            if (!IsInRoom)
            {
                _outbox.Add(new OutgoingHandoff { Msg = msg, Target = targetActor });
                CivilizameMPPlugin.Log.LogInfo($"[Photon] Sin sala: handoff ({msg.JueBytes?.Length} B -> actor {targetActor}) en cola ({_outbox.Count}); se enviará al reconectar");
                return SEND_QUEUED;
            }
            return SendNow(msg, targetActor);
        }

        private int SendNow(HandoffMessage msg, int targetActor)
        {
            if (!IsInRoom || msg == null)
                return -1;

            byte[] payload = msg.Serialize();
            int fileId = ++_transferIdSeq;
            int total = UnityEngine.Mathf.Max(1, (payload.Length + CHUNK_SIZE - 1) / CHUNK_SIZE);

            var opts = new RaiseEventOptions();
            if (targetActor >= 0) opts.TargetActors = new[] { targetActor };
            else opts.Receivers = ReceiverGroup.MasterClient;

            for (int i = 0; i < total; i++)
            {
                int offset = i * CHUNK_SIZE;
                int len = UnityEngine.Mathf.Min(CHUNK_SIZE, payload.Length - offset);
                byte[] chunk;
                using (var ms = new MemoryStream(32 + len))
                {
                    using (var w = new BinaryWriter(ms))
                    {
                        w.Write(fileId);
                        w.Write(i);
                        w.Write(total);
                        w.Write(payload.Length);
                        w.Write(len);
                        w.Write(payload, offset, len);
                        w.Flush();
                    }
                    chunk = ms.ToArray();
                }
                _client.OpRaiseEvent(HANDOFF_CHUNK_EVENT, chunk, opts, SendOptions.SendReliable);
            }

            CivilizameMPPlugin.Log.LogInfo($"[Photon] Handoff enviado: {payload.Length} B en {total} chunk(s) -> actor {targetActor}");
            return fileId;
        }

        private void HandleIncomingChunk(byte[] raw, int senderId)
        {
            try
            {
                int fileId, idx, total, payloadLen, chunkLen;
                byte[] bytes;
                using (var ms = new MemoryStream(raw))
                using (var r = new BinaryReader(ms))
                {
                    fileId = r.ReadInt32();
                    idx = r.ReadInt32();
                    total = r.ReadInt32();
                    payloadLen = r.ReadInt32();
                    chunkLen = r.ReadInt32();
                    bytes = r.ReadBytes(chunkLen);
                }

                if (fileId <= 0 || total <= 0 || idx < 0 || idx >= total)
                {
                    CivilizameMPPlugin.Log.LogWarning($"[Photon] Chunk inválido (file {fileId}, idx {idx}/{total})");
                    return;
                }

                IncomingTransfer tr;
                if (!_incoming.TryGetValue(fileId, out tr))
                {
                    if (_incoming.Count > 8) _incoming.Clear(); // purga de transferencias colgadas
                    tr = new IncomingTransfer
                    {
                        TotalChunks = total,
                        PayloadLength = payloadLen,
                        Buffer = new byte[payloadLen],
                        Got = new bool[total],
                        Received = 0
                    };
                    _incoming[fileId] = tr;
                }

                if (tr.Got[idx]) return; // duplicado
                int offset = idx * CHUNK_SIZE;
                int len = UnityEngine.Mathf.Min(bytes.Length, tr.PayloadLength - offset);
                Array.Copy(bytes, 0, tr.Buffer, offset, len);
                tr.Got[idx] = true;
                tr.Received++;

                if (tr.Received < tr.TotalChunks) return;

                _incoming.Remove(fileId);
                var msg = HandoffMessage.Deserialize(tr.Buffer);
                if (msg != null)
                {
                    CivilizameMPPlugin.Log.LogInfo($"[Photon] Handoff completo de actor {senderId}: {tr.PayloadLength} B (turno {msg.FromTurn} → {msg.LabelTurn})");
                    OnHandoffReceived?.Invoke(msg, senderId);
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Photon] Error ensamblando transferencia: {ex}");
            }
        }

        /// <summary>
        /// Envía un evento en vivo del canal LiveSync. Los eventos en vivo son
        /// EFÍMEROS: si no hay sala se descartan (no encolan: el estado de verdad
        /// llega siempre por el handoff completo).
        /// toOthers=true → broadcast a todos menos a uno mismo (host relaying);
        /// toOthers=false → solo al MasterClient (cliente → host).
        /// </summary>
        public bool SendLiveEvent(byte[] payload, bool toOthers)
        {
            if (_client == null || !_client.InRoom || payload == null || payload.Length == 0)
                return false;
            var opts = new RaiseEventOptions
            {
                Receivers = toOthers ? ReceiverGroup.Others : ReceiverGroup.MasterClient
            };
            _client.OpRaiseEvent(LIVE_EVENT, payload, opts, SendOptions.SendReliable);
            return true;
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

            // Reconexión en curso: reentrar en la sala (mismo UserId → mismo actor)
            if (_wantReconnect && !string.IsNullOrEmpty(_lastRoomName))
            {
                CivilizameMPPlugin.Log.LogInfo($"[Photon] Reconectado. Reentrando en la sala {_lastRoomName}...");
                _client.OpRejoinRoom(_lastRoomName);
                return;
            }

            MPStateManager.Instance?.SetState(MPGameState.InMenu);
            OnConnectedToMasterEvent?.Invoke();
        }

        void IConnectionCallbacks.OnDisconnected(DisconnectCause cause)
        {
            _isConnecting = false;

            var st = MPStateManager.Instance;
            bool inMatch = st != null && (st.CurrentState == MPGameState.PlayingHost ||
                                          st.CurrentState == MPGameState.PlayingClient);

            if (inMatch && !string.IsNullOrEmpty(_lastRoomName))
            {
                // Corte durante la partida (p.ej. hilo principal congelado cargando el
                // mundo). La partida sigue: reconexión transparente, el estado local
                // queda intacto (PlayingHost/PlayingClient).
                _wantReconnect = true;
                _nextReconnectAt = Time.time + 3f;
                CivilizameMPPlugin.Log.LogWarning($"[Photon] Desconexión durante la partida ({cause}). Reconectando/reentrando en {_lastRoomName}...");
                CivilizameMP.UI.MPWaitingPanel.Instance?.SetStatus("RECONECTANDO",
                    "Se perdió la conexión un momento.\nReentrando en la sala automáticamente...");
                return;
            }

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

            _wantReconnect = false;
            _lastRoomName = _client.CurrentRoom?.Name ?? _lastRoomName;

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
            if (_wantReconnect)
            {
                // La sala ya no existe (p.ej. el host cerró el juego): rendirse limpio.
                _wantReconnect = false;
                CivilizameMPPlugin.Log.LogError($"[Photon] No se pudo reentrar en {_lastRoomName} ({returnCode}: {message}).");
                MPStateManager.Instance.SetState(MPGameState.Disconnected);
                return;
            }

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
                case HANDOFF_CHUNK_EVENT:
                    if (photonEvent.CustomData is byte[] chunkData)
                        HandleIncomingChunk(chunkData, senderId);
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
                    }
                    break;
                case LIVE_EVENT:
                    if (photonEvent.CustomData is byte[] livePayload)
                        OnLiveEvent?.Invoke(livePayload, senderId);
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