using UnityEngine;
using CivilizameMP.Core;
using CivilizameMP.UI;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

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
                SceneManager.sceneLoaded += OnSceneLoaded;
                _initialized = true;
                CivilizameMPPlugin.Log.LogInfo("[ClientManager] Inicializado");
            }
        }

        private void OnConfigReceived(string json)
        {
            if (MPStateManager.Instance.IsHost) return;
            
            try
            {
                var config = JsonUtility.FromJson<GameConfigMessage>(json);
                if (config == null)
                {
                    CivilizameMPPlugin.Log.LogError("[Client] Config inválida");
                    return;
                }
                
                _pendingConfig = config;
                _configReceived = true;
                
                var state = MPStateManager.Instance;
                var slots = config.GetPlayerSlots();
                
                if (slots == null || slots.Count == 0)
                {
                    CivilizameMPPlugin.Log.LogWarning("[Client] Configuración sin jugadores");
                    return;
                }
                
                foreach (var slot in slots)
                {
                    if (slot.IsConnected && slot.ActorNumber >= 0)
                    {
                        state.RegisterPlayer(slot.ActorNumber, slot.PlayerName, slot.IsHost);
                    }
                }
                
                CivilizameMPPlugin.Log.LogInfo($"[Client] Config recibida: {config.TotalPlayers} jugadores");
                
                WriteNewGameData(config);
                
                if (_stateReceived && _pendingState != null)
                {
                    LoadGameState();
                }
                else
                {
                    MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO MAPA", "Esperando el mapa del host...");
                }
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en OnConfigReceived: {ex}");
            }
        }

        private void OnStateReceived(byte[] state)
        {
            if (MPStateManager.Instance.IsHost) return;
            if (_isLoading) return;
            if (state == null || state.Length == 0) return;
            
            try
            {
                CivilizameMPPlugin.Log.LogInfo($"[Client] Estado recibido: {state.Length} bytes");
                _pendingState = state;
                _stateReceived = true;
                
                if (_configReceived && _pendingConfig != null)
                {
                    LoadGameState();
                }
                else
                {
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO CONFIGURACIÓN", "Esperando configuración del host...");
                }
            }
            catch (System.Exception ex)
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
                CivilizameMPPlugin.Log.LogInfo("[Client] Cargando estado del juego...");
                
                StateSyncManager.Instance.DecompressAndLoad(_pendingState);
                StateSyncManager.Instance.WriteLoadDataForSync();
                
                MPWaitingPanel.Instance?.SetStatus("CARGANDO PARTIDA", "Cargando escena del juego...");
                
                _waitingForScene = true;
                SceneManager.LoadScene("Juego");
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en LoadGameState: {ex}");
                _isLoading = false;
                MPWaitingPanel.Instance?.SetStatus("ERROR", $"Error al cargar: {ex.Message}");
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
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error cargando guardado: {ex}");
                _isLoading = false;
                yield break;
            }
            
            yield return new WaitForSeconds(0.3f);
            
            try
            {
                AssignLocalPlayer();
                
                MPStateManager.Instance.SetState(MPGameState.PlayingClient);
                
                PhotonManager.Instance.SendConfigToAll("{\"ready\":true}");
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error en inicialización: {ex}");
                _isLoading = false;
                yield break;
            }
            
            yield return new WaitForSeconds(0.5f);
            
            try
            {
                MPWaitingPanel.Instance?.Hide();
                _isLoading = false;
                _configReceived = false;
                _stateReceived = false;
                
                CivilizameMPPlugin.Log.LogInfo("[Client] Partida cargada correctamente");
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Client] Error finalizando carga: {ex}");
                _isLoading = false;
            }
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
                var formatter = new BinaryFormatter();
                using (var stream = new FileStream(path, FileMode.Create))
                {
                    formatter.Serialize(stream, data);
                }
                
                CivilizameMPPlugin.Log.LogInfo($"[Client] NewGameData.gen escrito con modo carga=true");
            }
            catch (System.Exception ex)
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
            }
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }
}