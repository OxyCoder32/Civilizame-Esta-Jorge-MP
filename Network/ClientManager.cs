using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using CivilizameMP.Core;
using CivilizameMP.UI;

namespace CivilizameMP.Network
{
    public class ClientManager : MonoBehaviour
    {
        public static ClientManager Instance { get; private set; }
        
        private GameConfigMessage _pendingConfig;
        private byte[] _pendingState;
        private bool _isLoading;
        private bool _waitingForScene;
        private bool _configReceived;
        private bool _stateReceived;
        private bool _initialized;
        private bool _waitingForHostConfig;
        private bool _initialStateLoaded;
        private int _lastReceivedSequence = -1;
        private int _lastKnownTurnOrder = -1;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            if (!_initialized)
            {
                PhotonManager.Instance.OnConfigReceived += OnConfigReceived;
                PhotonManager.Instance.OnStateReceived += OnStateReceived;
                PhotonManager.Instance.OnPlayerLeftEvent += OnPlayerLeft;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _initialized = true;
                CivilizameMPPlugin.Log.LogInfo("[Client] Manager inicializado y suscrito a eventos");
            }
        }

        void Update()
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return;
            if (MPStateManager.Instance.IsHost) return;
            
            var gm = GameManager.Instance;
            if (gm == null) return;
            
            int currentTurn = gm.TurnOrder;
            
            if (currentTurn != _lastKnownTurnOrder)
            {
                _lastKnownTurnOrder = currentTurn;
                UpdateUIBasedOnTurn(gm);
            }
        }

        private void UpdateUIBasedOnTurn(GameManager gm)
        {
            if (gm == null) return;

            if (MPMatchState.MiIndiceLocal < 0)
            {
                MPWaitingPanel.Instance?.SetStatus("SINCRONIZANDO", "Esperando asignacion...");
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                return;
            }

            if (MPMatchState.IsLocalTurn(gm))
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo($"[Client] >>> ES TU TURNO <<< (Jugador {gm.TurnOrder + 1})");
            }
            else if (MPMatchState.IsAITurn(gm))
            {
                MPWaitingPanel.Instance?.SetStatus("TURNO DE IA", $"IA jugando... (Jugador {gm.TurnOrder + 1})");
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
            }
            else if (MPMatchState.IsRemoteHumanTurn(gm))
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO TU TURNO", $"Turno del jugador {gm.TurnOrder + 1}");
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
            }
            else
            {
                // Fallback: no sabemos de quién es el turno
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO", $"Turno del jugador {gm.TurnOrder + 1}");
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
            }
        }

        private void OnPlayerLeft(Photon.Realtime.Player player)
        {
            if (_waitingForHostConfig)
            {
                _waitingForHostConfig = false;
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance.ShowPanel(MPPanelType.Lobby);
            }
        }

        private void OnConfigReceived(string json)
        {
            if (MPStateManager.Instance.IsHost) return;
            try
            {
                if (json.Contains("\"hostStarting\":true"))
                {
                    _waitingForHostConfig = true;
                    MPPanelManager.Instance.HideCurrentPanel();
                    MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO PARTIDA", "El host esta generando el mundo...");
                    return;
                }
                if (json.Contains("\"ready\":true")) return;
                
                var config = JsonUtility.FromJson<GameConfigMessage>(json);
                if (config == null) return;
                if (config.TotalPlayers <= 0 && (config.PlayerSlots == null || config.PlayerSlots.Count == 0))
                    return;
                 
                if (config.PlayerSlots == null || config.PlayerSlots.Count == 0)
                {
                    config.PlayerSlots = new List<PlayerSlotConfig>();
                    for (int i = 0; i < config.TotalPlayers; i++)
                    {
                        config.PlayerSlots.Add(new PlayerSlotConfig
                        {
                            SlotIndex = i,
                            ActorNumber = i + 1,
                            PlayerName = i == 0 ? config.HostName : $"Jugador {i + 1}",
                            IsHuman = true,
                            IsHost = i == 0,
                            IsConnected = true,
                            IsReady = true,
                            LeaderIndex = 0
                        });
                    }
                }
                
                _pendingConfig = config;
                _configReceived = true;
                
                var state = MPStateManager.Instance;
                foreach (var slot in config.PlayerSlots)
                {
                    if (slot.IsConnected && slot.ActorNumber >= 0)
                        state.RegisterPlayer(slot.ActorNumber, slot.PlayerName, slot.IsHost);
                }
                
                WriteNewGameData(config);
                
                // Enviar ready al host para que envíe el estado del mundo
                PhotonManager.Instance.SendReady(true);
                CivilizameMPPlugin.Log.LogInfo("[Client] Config recibida, enviando ready al host");
                
                if (_stateReceived && _pendingState != null)
                    LoadGameState();
                else
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO MAPA", "Esperando el mapa del host...");
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en OnConfigReceived: {ex}");
            }
        }

        private void OnStateReceived(byte[] state, int sequence, string hash, int senderId)
        {
            if (MPStateManager.Instance.IsHost) return;
            if (state == null || state.Length == 0) return;
            
            CivilizameMPPlugin.Log.LogInfo($"[Client] Estado recibido seq={sequence} de sender={senderId}, bytes={state.Length}");
            
            try
            {
                if (sequence <= _lastReceivedSequence)
                {
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Ignorando estado antiguo seq={sequence}");
                    return;
                }
                
                string computedHash = PhotonManager.ComputeHash(state);
                if (computedHash != hash)
                {
                    CivilizameMPPlugin.Log.LogWarning("[Client] Hash mismatch, descartando");
                    return;
                }
                
                _pendingState = state;
                _stateReceived = true;
                _lastReceivedSequence = sequence;
                
                if (_configReceived && _pendingConfig != null)
                {
                    CivilizameMPPlugin.Log.LogInfo("[Client] Config ya recibida, cargando estado...");
                    LoadGameState();
                }
                else
                {
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO CONFIG", "Recibiendo configuracion...");
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en OnStateReceived: {ex}");
            }
        }

        private void LoadGameState()
        {
            if (_isLoading) return;
            if (_pendingState == null || _pendingState.Length == 0) return;
            if (_pendingConfig == null) return;
            
            _isLoading = true;
            
            try
            {
                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                File.WriteAllBytes(path, _pendingState);
                CivilizameMPPlugin.Log.LogInfo($"[Client] Estado escrito a disco: {_pendingState.Length} bytes");
                
                Scene currentScene = SceneManager.GetActiveScene();
                if (currentScene.name == "Juego")
                {
                    var gm = GameManager.Instance;
                    if (gm != null)
                    {
                        int turnoAntes = gm.TurnOrder;
                        gm.LoadGuardadoSeguridad();
                        int turnoDespues = gm.TurnOrder;
                        
                        CivilizameMPPlugin.Log.LogInfo($"[Client] Estado cargado. Turno: {turnoAntes} -> {turnoDespues}");
                        
                        AssignLocalPlayer();
                        MPStateManager.Instance.SetState(MPGameState.PlayingClient);
                        _initialStateLoaded = true;
                        _waitingForHostConfig = false;
                        
                        _isLoading = false;
                        UpdateUIBasedOnTurn(gm);
                    }
                    else
                    {
                        CivilizameMPPlugin.Log.LogError("[Client] GameManager.Instance es null");
                        _isLoading = false;
                    }
                }
                else
                {
                    _waitingForScene = true;
                    MPWaitingPanel.Instance?.SetStatus("CARGANDO PARTIDA", "Cargando escena del juego...");
                    SceneManager.LoadScene("Juego");
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en LoadGameState: {ex}");
                _isLoading = false;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_waitingForScene && scene.name == "Juego")
            {
                _waitingForScene = false;
                StartCoroutine(InitializeGameAfterSceneLoad());
            }
        }

        private IEnumerator InitializeGameAfterSceneLoad()
        {
            yield return new WaitForSeconds(0.5f);
            
            GameManager gm = null;
            try
            {
                gm = GameManager.Instance;
                if (gm == null)
                {
                    _isLoading = false;
                    yield break;
                }
                
                MPWaitingPanel.Instance?.SetStatus("CARGANDO PARTIDA", "Cargando mundo sincronizado...");
                gm.LoadGuardadoSeguridad();
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error cargando: {ex}");
                _isLoading = false;
                yield break;
            }
            
            yield return new WaitForSeconds(0.3f);
            
            try
            {
                AssignLocalPlayer();
                MPStateManager.Instance.SetState(MPGameState.PlayingClient);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error AssignLocalPlayer: {ex}");
                _isLoading = false;
                yield break;
            }
            
            yield return new WaitForSeconds(0.5f);
            
            try
            {
                _lastKnownTurnOrder = gm.TurnOrder;
                _initialStateLoaded = true;
                _waitingForHostConfig = false;
                _isLoading = false;
                
                UpdateUIBasedOnTurn(gm);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error finalizando: {ex}");
                _isLoading = false;
            }
        }

        public void OnHostStartedGame()
        {
            if (MPStateManager.Instance.IsHost) return;
            
            _waitingForHostConfig = true;
            MPPanelManager.Instance.HideCurrentPanel();
            MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
            MPWaitingPanel.Instance?.SetStatus("ESPERANDO CONFIGURACION", "El host esta configurando la partida...");
        }

        public void SendCurrentState()
        {
            if (MPStateManager.Instance.IsHost) return;
            
            try
            {
                var gm = GameManager.Instance;
                if (gm == null) return;
                
                var infoToFile = gm.GetComponent<InformationToFile>();
                if (infoToFile != null) infoToFile.GuardadoSeguridad();
                if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();

                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                if (File.Exists(path))
                {
                    byte[] stateData = File.ReadAllBytes(path);
                    PhotonManager.Instance.SendStateToHost(stateData);
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Estado enviado al host: {stateData.Length} bytes, turno {gm.TurnOrder}");
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error enviando estado: {ex}");
            }
        }

        private void AssignLocalPlayer()
        {
            var state = MPStateManager.Instance;
            var gm = GameManager.Instance;
            
            if (gm == null || gm.jugadores == null)
            {
                CivilizameMPPlugin.Log.LogError("[Client] GameManager o jugadores es null");
                return;
            }
            
            var localPlayer = state.GetPlayerSlot(state.LocalActorNumber);
            if (localPlayer == null)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] No se encontró slot para actor {state.LocalActorNumber}");
                return;
            }
            
            CivilizameMPPlugin.Log.LogInfo($"[Client] Buscando jugador local. Actor={state.LocalActorNumber}, SlotConfig={localPlayer.SlotIndex}, Nombre={localPlayer.PlayerName}");
            CivilizameMPPlugin.Log.LogInfo($"[Client] Jugadores en GameManager: {gm.jugadores.Length}");
            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                var jug = gm.jugadores[i];
                CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador[{i}]: Nombre={jug?.Nombre}, RealPlayer={jug?.RealPlayer}");
            }
            
            // Buscar por nombre exacto
            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                var jug = gm.jugadores[i];
                if (jug != null && jug.RealPlayer && jug.Nombre == localPlayer.PlayerName)
                {
                    MPMatchState.SetLocalIndex(i);
                    jug.Nombre = localPlayer.PlayerName;
                    jug.lider = (GameSettings.Lideres)localPlayer.LeaderIndex;
                    jug.color1 = localPlayer.PrimaryColor;
                    jug.color2 = localPlayer.SecondaryColor;
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador local asignado por nombre: slot {i}");
                    return;
                }
            }
            
            // Fallback por SlotIndex
            if (localPlayer.SlotIndex >= 0 && localPlayer.SlotIndex < gm.jugadores.Length)
            {
                var jug = gm.jugadores[localPlayer.SlotIndex];
                if (jug != null && jug.RealPlayer)
                {
                    MPMatchState.SetLocalIndex(localPlayer.SlotIndex);
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador local asignado por SlotIndex: {localPlayer.SlotIndex}");
                    return;
                }
            }
            
            // Último fallback: primer jugador RealPlayer
            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                var jug = gm.jugadores[i];
                if (jug != null && jug.RealPlayer)
                {
                    MPMatchState.SetLocalIndex(i);
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador local asignado fallback: slot {i}");
                    return;
                }
            }
            
            CivilizameMPPlugin.Log.LogError("[Client] NO SE PUDO ASIGNAR JUGADOR LOCAL");
        }

        void WriteNewGameData(GameConfigMessage config)
        {
            try
            {
                int numPlayers = config.TotalPlayers;
                var playerSlots = config.GetPlayerSlots();
                
                var lideres = new GameSettings.Lideres[numPlayers];
                var realPlayers = new bool[numPlayers];
                var colors = new Color[numPlayers, 2];
                var nombres = new string[numPlayers];
                
                for (int i = 0; i < numPlayers; i++)
                {
                    var slot = playerSlots.Find(s => s.SlotIndex == i);
                    if (slot != null && slot.IsConnected)
                    {
                        lideres[i] = (GameSettings.Lideres)slot.LeaderIndex;
                        realPlayers[i] = slot.IsHuman;
                        nombres[i] = slot.PlayerName;
                        colors[i, 0] = slot.PrimaryColor;
                        colors[i, 1] = slot.SecondaryColor;
                    }
                    else
                    {
                        lideres[i] = (GameSettings.Lideres)0;
                        realPlayers[i] = false;
                        nombres[i] = $"IA {i+1}";
                        colors[i, 0] = Color.gray;
                        colors[i, 1] = Color.white;
                    }
                }
                
                var data = new NewWorldData(
                    numPlayers, false, config.MapSize, config.MapType,
                    lideres, realPlayers, colors, nombres,
                    true, false, false, config.Difficulty, false, false, config.Seed
                );
                
                string path = Application.persistentDataPath + "/NewGameData.gen";
                var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                using (var stream = new FileStream(path, FileMode.Create))
                    formatter.Serialize(stream, data);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error escribiendo NewGameData.gen: {ex}");
            }
        }

        void OnDestroy()
        {
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnConfigReceived -= OnConfigReceived;
                PhotonManager.Instance.OnStateReceived -= OnStateReceived;
                PhotonManager.Instance.OnPlayerLeftEvent -= OnPlayerLeft;
            }
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }
}