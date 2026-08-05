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
        private bool _isLoading;
        private GameConfigMessage _pendingConfig;
        private byte[] _pendingState;
        private bool _waitingForSceneLoad;
        private bool _configReceived;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            PhotonManager.Instance.OnConfigReceived += OnConfigReceived;
            PhotonManager.Instance.OnStateReceived += OnStateReceived;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnConfigReceived(string json)
        {
            if (MPStateManager.Instance.IsHost) return;
            
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
            
            foreach (var slot in slots)
            {
                if (slot.IsConnected && slot.ActorNumber >= 0)
                {
                    if (!state.ConnectedPlayers.ContainsKey(slot.ActorNumber))
                    {
                        state.RegisterPlayer(slot.ActorNumber, slot.PlayerName, slot.IsHost);
                    }
                    else
                    {
                        state.ConnectedPlayers[slot.ActorNumber].PlayerName = slot.PlayerName;
                        state.ConnectedPlayers[slot.ActorNumber].SlotIndex = slot.SlotIndex;
                        state.ConnectedPlayers[slot.ActorNumber].LeaderIndex = slot.LeaderIndex;
                        state.ConnectedPlayers[slot.ActorNumber].PrimaryColor = slot.PrimaryColor;
                        state.ConnectedPlayers[slot.ActorNumber].SecondaryColor = slot.SecondaryColor;
                    }
                }
            }
            
            CivilizameMPPlugin.Log.LogInfo($"[Client] Config recibida: {config.TotalPlayers} jugadores");
            
            MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
            MPWaitingPanel.Instance?.SetStatus("CONFIGURANDO PARTIDA", $"Esperando inicio de {config.TotalPlayers} jugadores...");
            
            WriteNewGameData(config);
        }

        void OnStateReceived(byte[] state)
        {
            if (MPStateManager.Instance.IsHost || _isLoading) return;
            
            var currentState = MPStateManager.Instance.CurrentState;
            if (currentState == MPGameState.PlayingClient || currentState == MPGameState.LoadingGame)
                return;
            
            _isLoading = true;
            MPStateManager.Instance.SetState(MPGameState.LoadingGame);
            
            _pendingState = state;
            
            if (_pendingConfig == null || !_configReceived)
            {
                CivilizameMPPlugin.Log.LogWarning("[Client] Esperando configuración...");
                StartCoroutine(WaitForConfigAndLoad());
                return;
            }
            
            if (_pendingConfig.GetPlayerSlots() == null || _pendingConfig.GetPlayerSlots().Count == 0)
            {
                CivilizameMPPlugin.Log.LogWarning("[Client] Configuración sin jugadores, esperando...");
                StartCoroutine(WaitForConfigAndLoad());
                return;
            }
            
            LoadGameState();
        }

        private IEnumerator WaitForConfigAndLoad()
        {
            int timeout = 0;
            while ((!_configReceived || _pendingConfig == null || _pendingConfig.GetPlayerSlots().Count == 0) && timeout < 100)
            {
                yield return new WaitForSeconds(0.1f);
                timeout++;
            }
            
            if (_configReceived && _pendingConfig != null && _pendingConfig.GetPlayerSlots().Count > 0)
            {
                AssignLocalPlayerSlot();
                LoadGameState();
            }
            else
            {
                CivilizameMPPlugin.Log.LogError("[Client] Timeout esperando configuración");
                MPPanelManager.Instance.ShowError("Error: No se recibió la configuración de la partida");
            }
        }

        private void AssignLocalPlayerSlot()
        {
            var state = MPStateManager.Instance;
            int localActor = state.LocalActorNumber;
            
            if (localActor == -1) return;
            
            if (state.ConnectedPlayers.TryGetValue(localActor, out var slot))
            {
                state.LocalSlotIndex = slot.SlotIndex;
                state.LocalPlayerName = slot.PlayerName;
                state.LocalActorNumber = slot.ActorNumber;
                CivilizameMPPlugin.Log.LogInfo($"[Client] Slot local asignado: {state.LocalSlotIndex} (Actor: {localActor})");
            }
        }

        private void LoadGameState()
        {
            StateSyncManager.Instance.DecompressAndLoad(_pendingState);
            StateSyncManager.Instance.WriteLoadDataForSync();
            
            MPWaitingPanel.Instance?.SetStatus("CARGANDO PARTIDA", "Cargando escena del juego...");
            
            _waitingForSceneLoad = true;
            SceneManager.LoadScene("Juego");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_waitingForSceneLoad && scene.name == "Juego")
            {
                _waitingForSceneLoad = false;
                StartCoroutine(LoadGameAfterSceneLoaded());
            }
        }

        private IEnumerator LoadGameAfterSceneLoaded()
        {
            yield return new WaitForSeconds(0.5f);
            
            var gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                MPWaitingPanel.Instance?.SetStatus("CARGANDO PARTIDA", "Cargando mundo sincronizado...");
                
                gameManager.LoadGuardadoSeguridad();
                CivilizameMPPlugin.Log.LogInfo("[Client] LoadGuardadoSeguridad() llamado");
                
                yield return new WaitForSeconds(0.5f);
                
                ForceLocalPlayerAssignment();
                
                yield return new WaitForSeconds(0.5f);
                
                MPStateManager.Instance.SetState(MPGameState.PlayingClient);
                MPWaitingPanel.Instance?.Hide();
                _isLoading = false;
                _configReceived = false;
                
                CivilizameMPPlugin.Log.LogInfo("[Client] Partida cargada correctamente");
            }
            else
            {
                CivilizameMPPlugin.Log.LogError("[Client] GameManager no encontrado después de cargar escena");
                _isLoading = false;
            }
        }

        private void ForceLocalPlayerAssignment()
        {
            var state = MPStateManager.Instance;
            int localActor = state.LocalActorNumber;
            
            if (localActor == -1) return;
            
            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.jugadores == null) return;
            
            var slot = state.GetPlayerSlot(localActor);
            if (slot == null) return;
            
            foreach (var jugador in gameManager.jugadores)
            {
                if (jugador != null && jugador.RealPlayer)
                {
                    jugador.Nombre = slot.PlayerName;
                    jugador.lider = (GameSettings.Lideres)slot.LeaderIndex;
                    jugador.color1 = slot.PrimaryColor;
                    jugador.color2 = slot.SecondaryColor;
                    
                    CivilizameMPPlugin.Log.LogInfo($"[Client] Jugador local asignado: {jugador.Nombre} (Líder: {jugador.lider})");
                    break;
                }
            }
        }

        void WriteNewGameData(GameConfigMessage config)
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
                false,
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
            
            CivilizameMPPlugin.Log.LogInfo($"[Client] NewGameData.gen escrito");
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