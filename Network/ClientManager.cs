using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
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
        private byte[] _lastAppliedState;

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
                PhotonManager.Instance.OnPlayerJoinedEvent += OnPlayerJoined;
                PhotonManager.Instance.OnPlayerLeftEvent += OnPlayerLeft;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _initialized = true;
                CivilizameMPPlugin.Log.LogInfo("[ClientManager] Inicializado");
            }
        }

        private void OnPlayerJoined(Photon.Realtime.Player player) { }

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
                    CivilizameMPPlugin.Log.LogInfo("[Client] Host inició partida");
                    _waitingForHostConfig = true;
                    MPPanelManager.Instance.HideCurrentPanel();
                    MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO PARTIDA", "El host está generando el mundo...");
                    return;
                }

                if (json.Contains("\"ready\":true"))
                {
                    CivilizameMPPlugin.Log.LogInfo("[Client] Ready recibido, ignorando como configuración de partida");
                    return;
                }
                
                var config = JsonUtility.FromJson<GameConfigMessage>(json);
                if (config == null)
                {
                    CivilizameMPPlugin.Log.LogError("[Client] Config es null");
                    return;
                }

                if (config.TotalPlayers <= 0 && (config.PlayerSlots == null || config.PlayerSlots.Count == 0) && string.IsNullOrEmpty(config.HostName))
                {
                    CivilizameMPPlugin.Log.LogWarning("[Client] Config recibida vacía o inválida, ignorando");
                    return;
                }
                 
                // Validar que tenga al menos un jugador
                if (config.PlayerSlots == null || config.PlayerSlots.Count == 0)
                {
                    CivilizameMPPlugin.Log.LogWarning("[Client] Config sin PlayerSlots, creando desde TotalPlayers");
                    // Crear slots básicos
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
                    {
                        state.RegisterPlayer(slot.ActorNumber, slot.PlayerName, slot.IsHost);
                    }
                }
                
                CivilizameMPPlugin.Log.LogInfo($"[Client] Config recibida: {config.TotalPlayers} jugadores, {config.PlayerSlots.Count} slots");
                
                WriteNewGameData(config);
                
                if (_stateReceived && _pendingState != null)
                {
                    LoadGameState();
                }
                else
                {
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO MAPA", "Esperando el mapa del host...");
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en OnConfigReceived: {ex}");
            }
        }

        private void OnStateReceived(byte[] state)
        {
            if (MPStateManager.Instance.IsHost) return;
            if (state == null || state.Length == 0) return;
            
            try
            {
                CivilizameMPPlugin.Log.LogInfo($"[Client] Estado recibido: {state.Length} bytes");
                
                _pendingState = state;
                _stateReceived = true;

                if (_isLoading)
                {
                    CivilizameMPPlugin.Log.LogInfo("[Client] Estado recibido mientras se cargaba, se aplicará cuando termine la carga");
                    MPWaitingPanel.Instance?.SetStatus("CARGANDO ESTADO", "Aplicando el último estado del host...");
                    MPPanelManager.Instance?.HideCurrentPanel();
                    MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                    return;
                }

                if (!HasStateChanged(state))
                {
                    CivilizameMPPlugin.Log.LogInfo("[Client] Estado ya aplicado, ignorando");
                    return;
                }
                
                if (_initialStateLoaded || (_configReceived && _pendingConfig != null))
                {
                    LoadGameState();
                }
                else
                {
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO CONFIG", "Recibiendo configuración de partida...");
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en OnStateReceived: {ex}");
            }
        }

        private bool HasStateChanged(byte[] state)
        {
            if (state == null) return false;
            if (_lastAppliedState == null) return true;
            if (_lastAppliedState.Length != state.Length) return true;
            for (int i = 0; i < state.Length; i++)
            {
                if (_lastAppliedState[i] != state[i]) return true;
            }
            return false;
        }

        private void UpdateTurnUI(GameManager gm)
        {
            if (gm == null) return;
            if (MPMatchState.MiIndiceLocal < 0) return;

            var currentPlayer = gm.Jug();
            bool isHumanTurn = currentPlayer != null && currentPlayer.RealPlayer;

            if (!isHumanTurn)
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo("[Client] Turno de IA/host: sin cartel de espera");
                return;
            }

            if (gm.TurnOrder == MPMatchState.MiIndiceLocal)
            {
                MPWaitingPanel.Instance?.Hide();
                if (MPPanelManager.Instance != null && MPPanelManager.Instance.GetPanel(MPPanelType.Waiting) != null)
                {
                    MPPanelManager.Instance.HideCurrentPanel();
                }
                CivilizameMPPlugin.Log.LogInfo("[Client] ¡Es tu turno!");
            }
            else
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO TU TURNO", $"Turno del jugador {gm.TurnOrder + 1}");
                MPPanelManager.Instance?.HideCurrentPanel();
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                CivilizameMPPlugin.Log.LogInfo($"[Client] Esperando turno del jugador {gm.TurnOrder + 1}");
            }
        }

        private void LoadGameState()
        {
            if (_isLoading) return;
            if (_pendingState == null || _pendingState.Length == 0)
            {
                CivilizameMPPlugin.Log.LogWarning("[Client] No hay estado para cargar");
                return;
            }
            if (_pendingConfig == null)
            {
                CivilizameMPPlugin.Log.LogWarning("[Client] No hay configuración para cargar");
                return;
            }

            MPWaitingPanel.Instance?.SetStatus("CARGANDO ESTADO", "Aplicando estado sincronizado...");
            MPPanelManager.Instance?.HideCurrentPanel();
            MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
            if (!HasStateChanged(_pendingState))
            {
                CivilizameMPPlugin.Log.LogInfo("[Client] Estado ya aplicado, se omite la carga");
                return;
            }
            
            _isLoading = true;
            
            try
            {
                CivilizameMPPlugin.Log.LogInfo("[Client] Cargando estado del juego...");
                
                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                File.WriteAllBytes(path, _pendingState);
                
                // Verificar si ya estamos en la escena Juego
                Scene currentScene = SceneManager.GetActiveScene();
                if (currentScene.name == "Juego")
                {
                    // Ya estamos en la escena, cargar directamente
                    var gm = GameManager.Instance;
                    if (gm != null)
                    {
                        gm.LoadGuardadoSeguridad();
                        AssignLocalPlayer();
                        MPStateManager.Instance.SetState(MPGameState.PlayingClient);
                        PhotonManager.Instance.SendReady(true);
                        
                        UpdateTurnUI(gm);
                        
                        _lastAppliedState = _pendingState != null ? (byte[])_pendingState.Clone() : null;
                        _isLoading = false;
                        _configReceived = true;
                        _stateReceived = true;
                        _initialStateLoaded = true;
                        _waitingForHostConfig = false;
                        
                        if (_stateReceived && _pendingState != null && HasStateChanged(_pendingState))
                        {
                            CivilizameMPPlugin.Log.LogInfo("[Client] Hay un estado posterior pendiente, reaplicándolo");
                            LoadGameState();
                        }
                        
                        CivilizameMPPlugin.Log.LogInfo("[Client] Partida cargada correctamente");
                    }
                    else
                    {
                        CivilizameMPPlugin.Log.LogError("[Client] GameManager es null en escena Juego");
                        _isLoading = false;
                    }
                }
                else
                {
                    // Cargar la escena Juego
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
                    CivilizameMPPlugin.Log.LogError("[Client] GameManager no encontrado");
                    _isLoading = false;
                    yield break;
                }
                
                MPWaitingPanel.Instance?.SetStatus("CARGANDO PARTIDA", "Cargando mundo sincronizado...");
                gm.LoadGuardadoSeguridad();
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en InitializeGameAfterSceneLoad: {ex}");
                _isLoading = false;
                yield break;
            }
            
            yield return new WaitForSeconds(0.3f);
            
            try
            {
                AssignLocalPlayer();
                MPStateManager.Instance.SetState(MPGameState.PlayingClient);
                PhotonManager.Instance.SendReady(true);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en AssignLocalPlayer: {ex}");
                _isLoading = false;
                yield break;
            }
            
            yield return new WaitForSeconds(0.5f);
            
            try
            {
                UpdateTurnUI(gm);
                
                _lastAppliedState = _pendingState != null ? (byte[])_pendingState.Clone() : null;
                _isLoading = false;
                _configReceived = true;
                _stateReceived = true;
                _initialStateLoaded = true;
                _waitingForHostConfig = false;
                
                if (_stateReceived && _pendingState != null && HasStateChanged(_pendingState))
                {
                    CivilizameMPPlugin.Log.LogInfo("[Client] Hay un estado posterior pendiente, reaplicándolo");
                    LoadGameState();
                }
                
                CivilizameMPPlugin.Log.LogInfo("[Client] Partida cargada correctamente");
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error finalizando carga: {ex}");
                _isLoading = false;
            }
        }

        public void OnHostStartedGame()
        {
            if (MPStateManager.Instance.IsHost) return;
            
            _waitingForHostConfig = true;
            MPPanelManager.Instance.HideCurrentPanel();
            MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
            MPWaitingPanel.Instance?.SetStatus("ESPERANDO CONFIGURACIÓN", "El host está configurando la partida...");
            
            CivilizameMPPlugin.Log.LogInfo("[Client] Host inició la partida - Mostrando cartel de espera");
        }

        private void AssignLocalPlayer()
        {
            var state = MPStateManager.Instance;
            var gm = GameManager.Instance;
            
            if (gm == null || gm.jugadores == null)
            {
                CivilizameMPPlugin.Log.LogError("[Client] GameManager o jugadores null");
                return;
            }
            
            var localPlayer = state.GetPlayerSlot(state.LocalActorNumber);
            if (localPlayer == null)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] No se encontró slot para Actor {state.LocalActorNumber}");
                return;
            }
            
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
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador local asignado: {jug.Nombre} (Índice: {i})");
                    return;
                }
            }
            
            if (localPlayer.SlotIndex >= 0 && localPlayer.SlotIndex < gm.jugadores.Length)
            {
                var jug = gm.jugadores[localPlayer.SlotIndex];
                if (jug != null && jug.RealPlayer)
                {
                    MPMatchState.SetLocalIndex(localPlayer.SlotIndex);
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador local asignado por slot: {jug.Nombre} (Índice: {localPlayer.SlotIndex})");
                    return;
                }
            }
            
            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                var jug = gm.jugadores[i];
                if (jug != null && jug.RealPlayer)
                {
                    MPMatchState.SetLocalIndex(i);
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador local asignado por fallback: {jug.Nombre} (Índice: {i})");
                    return;
                }
            }
            
            CivilizameMPPlugin.Log.LogError("[Client] No se pudo asignar el jugador local");
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
                    numPlayers,
                    false,
                    config.MapSize,
                    config.MapType,
                    lideres,
                    realPlayers,
                    colors,
                    nombres,
                    true,
                    false,
                    false,
                    config.Difficulty,
                    false,
                    false,
                    config.Seed
                );
                
                string path = Application.persistentDataPath + "/NewGameData.gen";
                var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                using (var stream = new FileStream(path, FileMode.Create))
                {
                    formatter.Serialize(stream, data);
                }
                
                CivilizameMPPlugin.Log.LogInfo($"[Client] NewGameData.gen escrito con modo carga=true");
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
                PhotonManager.Instance.OnPlayerJoinedEvent -= OnPlayerJoined;
                PhotonManager.Instance.OnPlayerLeftEvent -= OnPlayerLeft;
            }
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }
}