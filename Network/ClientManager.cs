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
    /// Lado cliente del flujo de turnos:
    ///  1. Recibe la config de slots del host (con su ActorNumber -> SlotIndex).
    ///  2. Cuando el host le envía el estado que corresponde a SU slot, escribe los
    ///     archivos y carga la escena (o recarga desde guardado si ya está jugando).
    ///  3. Juega su turno. Al terminar, el parche de NextTurn llama a SendTurnEnd()
    ///     que captura el estado y se lo devuelve al host.
    /// </summary>
    public class ClientManager : MonoBehaviour
    {
        public static ClientManager Instance { get; private set; }

        private GameConfigMessage _pendingConfig;
        private bool _configReceived;
        private HandoffMessage _pendingState;
        private bool _isLoading;
        private bool _initialized;
        private int _lastReceivedSeq = -1;

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
            TurnController.ClientFrozen = false; // llega nuevo estado: se puede simular de nuevo
            MPStateManager.Instance.SetState(MPGameState.PlayingClient);

            CivilizameMPPlugin.Log.LogInfo($"[Client] Estado de mi turno recibido (label {msg.LabelTurn}, {msg.JueBytes.Length} B). Cargando...");

            if (_configReceived && MPMatchState.IsInitialized)
                LoadOwnTurnState();
            else
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO CONFIGURACION", "Recibiendo configuracion de slots...");
        }

        private void LoadOwnTurnState()
        {
            if (_isLoading || _pendingState == null) return;
            _isLoading = true;
            TurnController.ClientFrozen = false;

            try
            {
                if (!StateHandoff.WriteStateFiles(_pendingState.JueBytes, _pendingState.MapBytes))
                    throw new Exception("No se pudo escribir el estado en disco");

                TurnController.ReloadFromSave();
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error cargando estado: {ex}");
                _isLoading = false;
            }
        }

        // ------------------------------------------------------------------
        //  Envío del turno terminado al host (llamado por TurnController)
        // ------------------------------------------------------------------

        public void SendTurnEnd(GameManager gm)
        {
            if (MPStateManager.Instance.IsHost) return;
            if (gm == null || MPMatchState.MiIndiceLocal < 0) return;
            if (gm.TurnOrder != MPMatchState.MiIndiceLocal) return;

            int mySlot = MPMatchState.MiIndiceLocal;
            int next = StateHandoff.NextPlayableSlot(gm.TurnOrder);
            if (next < 0) next = mySlot;

            byte[] map;
            byte[] jue = StateHandoff.SaveStateFiles(next, out map);
            if (jue == null)
            {
                CivilizameMPPlugin.Log.LogError("[Client] No se pudo guardar el estado al terminar el turno");
                return;
            }

            var msg = new HandoffMessage
            {
                FromTurn = mySlot,
                LabelTurn = next,
                BaseSeq = Math.Max(0, _lastReceivedSeq),
                JueBytes = jue,
                MapBytes = map
            };

            // targetActor -1 = MasterClient (el host). Si perdimos la sala (p. ej. por
            // la carga del mapa), PhotonManager lo deja en cola y lo envía al
            // reconectar/reentrar — el turno no se pierde.
            int sent = PhotonManager.Instance.SendHandoff(msg, -1);
            if (sent < 0 && sent != PhotonManager.SEND_QUEUED)
            {
                CivilizameMPPlugin.Log.LogError("[Client] Fallo enviando mi turno al host");
                return;
            }

            if (sent == PhotonManager.SEND_QUEUED)
            {
                CivilizameMPPlugin.Log.LogInfo("[Client] Sala perdida: el turno quedó en cola; se enviará automáticamente al reconectar");
                MPWaitingPanel.Instance?.SetStatus("RECONECTANDO",
                    "Se perdió la conexión un momento.\nTu turno se enviará automáticamente al reconectar...");
            }
            else
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO AL HOST", "Turno enviado al servidor...");

            CivilizameMPPlugin.Log.LogInfo($"[Client] Turno {mySlot} terminado y {(sent == PhotonManager.SEND_QUEUED ? "EN COLA" : "enviado")} (label {next}, base seq {msg.BaseSeq})");
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
