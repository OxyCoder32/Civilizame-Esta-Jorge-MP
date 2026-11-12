using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using CivilizameMP.Core;
using CivilizameMP.UI;

namespace CivilizameMP.Network
{
    /// <summary>
    /// Lado cliente del flujo MIRROR (host = servidor):
    ///  1. Recibe la config de slots del host (con su ActorNumber -> SlotIndex).
    ///  2. Recibe UN estado completo (al crear la partida, o pedido por resync
    ///     al reentrar en la sala) y lo carga en MODO ESPEJO: TurnOrder =
    ///     etiqueta, SIN StartTurn: los efectos del turno activo llegan por
    ///     eventos en vivo. Después NO recibe más estados.
    ///  3. Cuando un EV_SLOT marca SU slot, se arma y juega su turno en su POV
    ///     (StartTurn propio + narración por LiveSync). Al terminar solo envía
    ///     el aviso ligero EV_TURN_DONE (con reintentos acotados).
    ///  4. Entra en MODO ESPECTADOR (LiveSync.Spectating): ve el resto de
    ///     turnos + la IA en directo hasta que el siguiente EV_SLOT arme su
    ///     turno de nuevo.
    /// </summary>
    public class ClientManager : MonoBehaviour
    {
        public static ClientManager Instance { get; private set; }

        private GameConfigMessage _pendingConfig;
        private bool _configReceived;
        private float _configAt;
        private HandoffMessage _pendingState;
        private bool _isLoading;
        private bool _initialized;
        private int _lastReceivedSeq = -1;

        // Pedido de resync: el cliente lo envía al (re)entrar en la sala
        // durante una partida y lo repite por Tick hasta recibir un estado.
        private bool _resyncRequested;
        private float _nextResyncAt;

        // Reintento del aviso ligero de fin de turno (TURN_DONE) — acotado: si
        // el host no lo recibe en 3 intentos, el siguiente EV_SLOT propio
        // re-arma el turno igualmente (el host deduplica por seq).
        private bool _turnDonePending;
        private int _turnDoneSeq;
        private int _turnDoneTries;
        private float _nextTurnDoneRetryAt;

        // Ping de carga (EV_LOAD_READY): confirma al host que ya aplicamos el
        // estado en la escena Juego. Se repite hasta que el host arme el ciclo
        // (llega nuestro EV_SLOT) o se agoten los intentos (el host deduplica).
        private bool _loadReadyPending;
        private int _loadReadyTries;
        private float _nextLoadReadyAt;

        /// <summary>El cliente está esperando un estado de resync (acepta
        /// handoffs con etiqueta distinta de su slot mientras tanto).</summary>
        public bool WaitingResyncState => _resyncRequested;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            if (_initialized) return;
            _initialized = true;

            PhotonManager.Instance.OnConfigReceived += OnConfigReceived;
            PhotonManager.Instance.OnHandoffReceived += OnHandoffReceived;
            PhotonManager.Instance.OnPlayerLeftEvent += OnPlayerLeft;
            PhotonManager.Instance.OnJoinedRoomEvent += OnJoinedRoom;
            SceneManager.sceneLoaded += OnSceneLoaded;
            CivilizameMPPlugin.Log.LogInfo("[Client] Manager inicializado y suscrito a eventos");
        }

        void OnDestroy()
        {
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnConfigReceived -= OnConfigReceived;
                PhotonManager.Instance.OnHandoffReceived -= OnHandoffReceived;
                PhotonManager.Instance.OnPlayerLeftEvent -= OnPlayerLeft;
                PhotonManager.Instance.OnJoinedRoomEvent -= OnJoinedRoom;
            }
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        // ------------------------------------------------------------------
        //  Config de slots del host
        // ------------------------------------------------------------------

        public void OnHostStartedGame()
        {
            if (MPStateManager.Instance.IsHost) return;
            MPPanelManager.Instance?.HideCurrentPanel();
            MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
            MPWaitingPanel.Instance?.SetStatus("HOST CONFIGURANDO LA PARTIDA", "El host esta creando la partida con los menus originales...");
        }

        private void OnPlayerLeft(Photon.Realtime.Player player)
        {
            if (!_configReceived) return;
            CivilizameMPPlugin.Log.LogWarning($"[Client] El jugador {player?.NickName} se fue de la sala");
        }

        /// <summary>(Re)entrada en la sala: si es durante una partida, pedir
        /// un estado fresco al host (resync). Cubre la reconexión tras un
        /// ServerTimeout y cualquier despiste del despacho inicial.</summary>
        private void OnJoinedRoom()
        {
            if (MPStateManager.Instance.IsHost) return;
            if (!StateHandoff.IsInGame()) return; // lobby/menú: nada que resincronizar
            RequestResync();
        }

        public void RequestResync()
        {
            if (MPStateManager.Instance.IsHost) return;
            _resyncRequested = true;
            _nextResyncAt = 0f;
            LiveSync.Spectating = true; // bloquear input mientras llega el estado
            CivilizameMPPlugin.Log.LogInfo("[Client] Pidiendo resync al host...");
            MPWaitingPanel.Instance?.SetStatus("SINCRONIZANDO", "Pidiendo el estado de la partida al host...");
        }

        /// <summary>Reintentos (cada frame vía LiveSyncRunner):
        ///  - EV_RESYNC hasta recibir un estado del host.
        ///  - TURN_DONE (acotado a 3 envíos; el host deduplica por seq).
        /// </summary>
        public void Tick()
        {
            // Backstop: si tras aplicar la config no ha llegado NINGÚN estado
            // (despacho inicial perdido), pedir resync al host.
            if (_configReceived && !_resyncRequested && _lastReceivedSeq < 0
                && StateHandoff.IsInGame() && Time.time - _configAt > 30f)
            {
                CivilizameMPPlugin.Log.LogWarning("[Client] Sin estado tras 30s de partida: pidiendo resync");
                RequestResync();
            }

            if (_resyncRequested && _configReceived && Time.time >= _nextResyncAt)
            {
                _nextResyncAt = Time.time + 6f;
                if (!LiveSync.SendResyncRequest())
                    CivilizameMPPlugin.Log.LogWarning("[Client] RESYNC sin sala; se reintentará al reconectar");
            }
            if (!_turnDonePending) return;
            if (Time.time < _nextTurnDoneRetryAt) return;
            PushTurnDone();

            if (!_loadReadyPending) return;
            if (Time.time < _nextLoadReadyAt) return;
            _nextLoadReadyAt = Time.time + 2f;
            PushLoadReady();
        }

        /// <summary>Confirma al host que esta PC ya cargó y aplicó el estado
        /// (escena Juego + guardado). El ciclo de turnos del host NO arranca
        /// hasta que todos los clientes hayan confirmado.</summary>
        public void NotifyLoadedToHost()
        {
            if (MPStateManager.Instance.IsHost) return;
            _loadReadyPending = true;
            _loadReadyTries = 0;
            _nextLoadReadyAt = 0f;
            PushLoadReady();
        }

        private void PushLoadReady()
        {
            int slot = MPMatchState.MiIndiceLocal;
            if (slot < 0) return;
            bool sent = LiveSync.SendLoadReady(slot);
            if (sent)
            {
                _loadReadyTries++;
                CivilizameMPPlugin.Log.LogInfo($"[Client] LOAD_READY enviado (slot {slot}, intento {_loadReadyTries})");
                if (_loadReadyTries >= 10) _loadReadyPending = false; // acotado
            }
            else
            {
                CivilizameMPPlugin.Log.LogWarning("[Client] LOAD_READY sin sala; se reintentará por Tick");
            }
        }

        private void OnConfigReceived(string json)
        {
            if (MPStateManager.Instance.IsHost) return;

            try
            {
                // Setup espejado del panel vanilla del host (nombres, IA/Real, mapa...)
                if (SetupSync.ApplyJson(json)) return;

                if (json.Contains("\"hostStarting\":true"))
                    { OnHostStartedGame(); return; }
                if (json.Contains("\"ready\":true")) return;

                var config = ParseGameConfigJson(json);
                if (config == null || config.PlayerSlotsArray == null || config.PlayerSlotsArray.Length == 0)
                {
                    CivilizameMPPlugin.Log.LogError("[Client] Config del host inválida");
                    return;
                }

                _pendingConfig = config;
                _configReceived = true;
                _configAt = Time.time;
                // Reset del control de secuencia al empezar una nueva partida para
                // no rechazar estados de la nueva partida por seq de la anterior.
                _lastReceivedSeq = -1;

                var state = MPStateManager.Instance;
                int myActor = state.LocalActorNumber;
                CivilizameMPPlugin.Log.LogInfo($"[Client] === APLICANDO CONFIG === Mi ActorNumber = {myActor}");

                state.ConnectedPlayers.Clear();
                state.PlayerSlots.Clear();
                // El nº de slots del juego lo fija la partida creada por el host
                int totalSlots = config.TotalPlayers > 0 ? config.TotalPlayers : Math.Max(state.MaxPlayers, 1);
                for (int i = 0; i < totalSlots; i++)
                {
                    state.PlayerSlots.Add(new PlayerSlotConfig
                    {
                        SlotIndex = i,
                        PlayerName = $"Slot {i + 1}",
                        IsConnected = false,
                        IsHuman = false,
                        IsReady = false,
                        ActorNumber = -1
                    });
                }

                bool foundMySlot = false;
                foreach (var slot in config.PlayerSlotsArray)
                {
                    if (slot == null) continue;

                    var newSlot = new PlayerSlotConfig
                    {
                        SlotIndex = slot.SlotIndex,
                        ActorNumber = slot.ActorNumber,
                        PlayerName = slot.PlayerName,
                        IsHuman = slot.IsHuman,
                        IsHost = slot.IsHost,
                        IsConnected = slot.IsConnected,
                        IsReady = slot.IsReady,
                        LeaderIndex = slot.LeaderIndex
                    };

                    if (slot.SlotIndex >= 0 && slot.SlotIndex < state.PlayerSlots.Count)
                        state.PlayerSlots[slot.SlotIndex] = newSlot;

                    if (slot.ActorNumber > 0)
                        state.ConnectedPlayers[slot.ActorNumber] = newSlot;

                    if (slot.ActorNumber == myActor)
                    {
                        state.LocalSlotIndex = slot.SlotIndex;
                        state.LocalPlayerName = slot.PlayerName;
                        state.IsHost = slot.IsHost;
                        state.IsClient = !slot.IsHost;
                        MPMatchState.SetLocalIndex(slot.SlotIndex);
                        foundMySlot = true;
                        CivilizameMPPlugin.Log.LogInfo($"[Client] ¡ENCONTRADO! Mi Actor {myActor} → SlotIndex = {slot.SlotIndex}");
                    }
                }

                if (!foundMySlot)
                    CivilizameMPPlugin.Log.LogError($"[Client] NO se encontró mi ActorNumber ({myActor}) en la config del host!");

                CivilizameMPPlugin.Log.LogInfo($"[Client] Config aplicada. Mi SlotIndex = {state.LocalSlotIndex}, HumanPlayers = {config.HumanPlayers}");

                PhotonManager.Instance.SendReady(true);

                // Si el estado del turno ya había llegado antes que la config, cargar ahora.
                if (_pendingState != null)
                    LoadOwnTurnState();
                else
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO TURNO", "Esperando el estado del host...");
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en OnConfigReceived: {ex}");
            }
        }

        // ------------------------------------------------------------------
        //  Recepción del estado del turno (vía TurnController)
        // ------------------------------------------------------------------

        private void OnHandoffReceived(HandoffMessage msg, int senderId)
        {
            TurnController.HandleIncomingHandoff(msg, senderId);
        }

        public void ReceiveOwnTurn(HandoffMessage msg)
        {
            if (msg == null || msg.JueBytes == null || msg.JueBytes.Length == 0)
            {
                CivilizameMPPlugin.Log.LogError("[Client] Estado recibido inválido");
                return;
            }

            if (_lastReceivedSeq >= 0 && msg.BaseSeq <= _lastReceivedSeq)
            {
                CivilizameMPPlugin.Log.LogInfo($"[Client] Estado antigo/repetido ignorado (seq {msg.BaseSeq} <= {_lastReceivedSeq})");
                return;
            }

            _lastReceivedSeq = msg.BaseSeq;
            _pendingState = msg;
            _turnDonePending = false; // nuevo estado: el aviso TURN_DONE ya llegó
            _turnDoneTries = 0;
            _resyncRequested = false; // el estado pedido (o el inicial) llegó
            MPStateManager.Instance.SetState(MPGameState.PlayingClient);
            StateHandoff.MirrorLoad = true; // la carga fija TurnOrder = MI slot y NO llama a NextTurn

            CivilizameMPPlugin.Log.LogInfo($"[Client] Estado recibido (label {msg.LabelTurn}, seq {msg.BaseSeq}, {msg.JueBytes.Length} B). Cargando en MODO ESPEJO...");

            if (_configReceived && MPMatchState.IsInitialized)
                LoadOwnTurnState();
            else
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO CONFIGURACION", "Recibiendo configuracion de slots...");
        }

        private void LoadOwnTurnState()
        {
            if (_isLoading || _pendingState == null) return;
            _isLoading = true;

            try
            {
                if (!StateHandoff.WriteStateFiles(_pendingState.JueBytes, _pendingState.MapBytes))
                    throw new Exception("No se pudo escribir el estado en disco");

                // Carga en MODO ESPEJO: esta PC fijará SU slot (sin StartTurn;
                // su turno llegará por EV_SLOT). Los eventos en vivo que
                // lleguen mientras carga se almacenan y se reproducen después;
                // los del host anteriores al snapshot (seq <= BaseSeq) se
                // descartan porque ya están dentro del estado.
                StateHandoff.PendingTurnOrderAfterLoad = _pendingState.LabelTurn;
                LiveSync.LoadSeq = _pendingState.BaseSeq;
                LiveSync.LoadPending = true;
                LiveSync.Spectating = true; // hasta que la carga diga lo contrario

                TurnController.ReloadFromSave();
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error cargando estado: {ex}");
                _isLoading = false;
                LiveSync.LoadPending = false;
            }
        }

        // ------------------------------------------------------------------
        //  Aviso ligero de fin de turno (llamado por TurnController)
        // ------------------------------------------------------------------

        /// <summary>
        /// Fin del turno del cliente: NO captura ni sube el estado. Solo manda
        /// EV_TURN_DONE (origen + slot + seq) al host, que ha seguido en directo
        /// cada acción de este turno por LiveSync. Reintento acotado: si el
        /// host no lo recibe, el siguiente EV_SLOT propio re-arma el turno de
        /// todos modos.
        /// </summary>
        public void SendTurnDone()
        {
            if (MPStateManager.Instance.IsHost) return;
            if (MPMatchState.MiIndiceLocal < 0) return;

            _turnDonePending = true;
            _turnDoneTries = 0;
            _turnDoneSeq++; // seq NUEVA solo al abrir la frontera de turno
            PushTurnDone();

            // SOLO el cartel vanilla de espera (el HUD encima se ve mal).
            TurnController.ShowVanillaWaiting(true);
        }

        private void PushTurnDone()
        {
            _nextTurnDoneRetryAt = Time.time + 4f;
            int slot = MPMatchState.MiIndiceLocal;
            if (slot < 0) return;
            // Reintentos repiten la MISMA seq: el host los deduplica por
            // (actor, seq). Con seq nueva por reintento, un reintento tardío
            // podía consumir el turno del ciclo siguiente (turnos rápidos).
            bool sent = LiveSync.SendTurnDone(slot, _turnDoneSeq);
            if (sent)
            {
                _turnDoneTries++;
                CivilizameMPPlugin.Log.LogInfo($"[Client] TURN_DONE del slot {slot} enviado (seq {_turnDoneSeq}, intento {_turnDoneTries})");
                if (_turnDoneTries >= 3) _turnDonePending = false; // acotado: el host lo tiene o el EV_SLOT re-arma
            }
            else
            {
                CivilizameMPPlugin.Log.LogWarning("[Client] TURN_DONE sin sala; se reintentará por Tick al reconectar");
            }
        }

        /// <summary>Llamado por TurnController.ArmMyTurn: el host nos devolvió
        /// el turno (EV_SLOT propio), así que el ciclo ya arrancó: el ping de
        /// carga y el aviso TURN_DONE ya llegaron.</summary>
        public void OnTurnArmed()
        {
            _loadReadyPending = false;
            _turnDonePending = false;
            _turnDoneTries = 0;
        }

        // ------------------------------------------------------------------
        //  Carga de la escena de juego
        // ------------------------------------------------------------------

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != MPConstants.GAME_SCENE) return;
            if (!MPStateManager.Instance.IsClient) return;
            StartCoroutine(AfterGameSceneLoaded());
        }

        private IEnumerator AfterGameSceneLoaded()
        {
            yield return new WaitForSeconds(0.5f);

            var gm = GameManager.Instance;
            if (gm == null)
            {
                _isLoading = false;
                LiveSync.Spectating = false;
                yield break;
            }

            var state = MPStateManager.Instance;
            state.SetState(MPGameState.PlayingClient);

            int mySlot = state.LocalSlotIndex;
            if (mySlot < 0) mySlot = MPMatchState.MiIndiceLocal;

            if (mySlot >= 0 && mySlot < gm.jugadores.Length && gm.jugadores[mySlot] != null)
            {
                gm.jugadores[mySlot].Nombre = state.LocalPlayerName;
                MPMatchState.SetLocalIndex(mySlot);
                CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador local asignado al slot {mySlot} ({state.LocalPlayerName})");
            }
            else
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Slot local inválido tras cargar: {mySlot}");
            }

            _isLoading = false;

            // SIN CAMBIO DE JUGADOR: tras cargar, esta PC está en SU jugador
            // (el TurnOrder lo fijó el parche de MundoCargado) pero en modo
            // ESPECTADOR hasta que un EV_SLOT arme su turno. El input lo
            // regula LiveSync.Spectating, no un cambio de dueño.
            LiveSync.Spectating = true;
            CivilizameMPPlugin.Log.LogInfo("[Client] Carga lista: en MI jugador, modo espectador hasta mi EV_SLOT");

            // SOLO el cartel vanilla de espera: la partida arranca (o sigue)
            // y aún no es tu turno. Los appliers de EV_SLOT/EV_TURN_START lo
            // quitan cuando toca jugar.
            TurnController.ShowVanillaWaiting(true);

            // Si llegó un estado MÁS NUEVO mientras cargábamos, aplicarlo ahora.
            if (_pendingState != null && _pendingState.BaseSeq > _lastReceivedSeq)
            {
                CivilizameMPPlugin.Log.LogInfo("[Client] Estado más nuevo pendiente; aplicándolo...");
                var newer = _pendingState;
                _pendingState = null;
                ReceiveOwnTurn(newer);
                yield break; // la nueva carga hará su propio NotifyLoadedToHost
            }

            // PING al host: estado aplicado y escena Juego cargada. El ciclo
            // de turnos arranca cuando TODOS los clientes hayan confirmado.
            NotifyLoadedToHost();
            // La UI de "es tu turno / esperando" la centraliza TurnController.OnTurnAdvanced.
        }

        // ------------------------------------------------------------------
        //  Parseo del JSON de config (ligero, sin dependencias)
        // ------------------------------------------------------------------

        private GameConfigMessage ParseGameConfigJson(string json)
        {
            try
            {
                var config = new GameConfigMessage();

                config.Seed         = ExtractInt(json, "Seed");
                config.MapSize      = ExtractInt(json, "MapSize");
                config.MapType      = ExtractInt(json, "MapType");
                config.Difficulty   = ExtractInt(json, "Difficulty");
                config.TotalPlayers = ExtractInt(json, "TotalPlayers");
                config.HumanPlayers = ExtractInt(json, "HumanPlayers");
                config.HostName     = ExtractString(json, "HostName");
                config.HostLeader   = ExtractInt(json, "HostLeader");

                int arrStart = json.IndexOf("\"PlayerSlotsArray\":[");
                if (arrStart < 0)
                {
                    CivilizameMPPlugin.Log.LogError("[Client] No se encontró PlayerSlotsArray en el JSON");
                    return null;
                }

                arrStart = json.IndexOf('[', arrStart) + 1;
                int arrEnd = FindMatchingBracket(json, arrStart - 1);
                if (arrEnd < 0) return null;

                string arrayContent = json.Substring(arrStart, arrEnd - arrStart);

                var slotStrings = SplitTopLevelObjects(arrayContent);
                var slots = new List<PlayerSlotNetwork>();

                foreach (var slotStr in slotStrings)
                {
                    if (string.IsNullOrWhiteSpace(slotStr)) continue;

                    var slot = new PlayerSlotNetwork
                    {
                        SlotIndex    = ExtractInt(slotStr, "SlotIndex"),
                        ActorNumber  = ExtractInt(slotStr, "ActorNumber"),
                        PlayerName   = ExtractString(slotStr, "PlayerName"),
                        IsHuman      = ExtractBool(slotStr, "IsHuman"),
                        IsHost       = ExtractBool(slotStr, "IsHost"),
                        IsConnected  = ExtractBool(slotStr, "IsConnected"),
                        IsReady      = ExtractBool(slotStr, "IsReady"),
                        LeaderIndex  = ExtractInt(slotStr, "LeaderIndex")
                    };
                    slots.Add(slot);
                }

                config.PlayerSlotsArray = slots.ToArray();
                CivilizameMPPlugin.Log.LogInfo($"[Client] Parseados {config.PlayerSlotsArray.Length} slots del JSON");
                return config;
            }
            catch (Exception e)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Excepción parseando JSON: {e.Message}");
                return null;
            }
        }

        private int ExtractInt(string json, string key)
        {
            string pattern = $"\"{key}\":";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return 0;
            idx += pattern.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            string numStr = json.Substring(idx, end - idx);
            return int.TryParse(numStr, out int val) ? val : 0;
        }

        private string ExtractString(string json, string key)
        {
            string pattern = $"\"{key}\":\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return "";
            idx += pattern.Length;
            int end = json.IndexOf('"', idx);
            if (end < 0) return "";
            return json.Substring(idx, end - idx);
        }

        private bool ExtractBool(string json, string key)
        {
            string pattern = $"\"{key}\":";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return false;
            idx += pattern.Length;
            return idx + 4 <= json.Length && json.Substring(idx, 4) == "true";
        }

        private int FindMatchingBracket(string json, int openBracketPos)
        {
            int depth = 0;
            for (int i = openBracketPos; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private List<string> SplitTopLevelObjects(string arrayContent)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;

            for (int i = 0; i < arrayContent.Length; i++)
            {
                char c = arrayContent[i];
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        result.Add(arrayContent.Substring(start, i - start + 1));
                }
            }
            return result;
        }
    }
}
