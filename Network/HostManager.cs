using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using CivilizameMP.Core;
using CivilizameMP.UI;

namespace CivilizameMP.Network
{
    public class HostManager : MonoBehaviour
    {
        public static HostManager Instance { get; private set; }
        
        private bool _isGenerating;
        private int _clientsReady;
        private int _expectedClients;
        private bool _gameStarted;
        private bool _initialized;
        private readonly HashSet<int> _readyActors = new HashSet<int>();

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            
            PhotonManager.Instance.OnConfigReceived += OnConfigReceived;
            PhotonManager.Instance.OnPlayerLeftEvent += OnPlayerLeft;
            PhotonManager.Instance.OnStateReceived += OnStateReceived;
        }

        private void Start()
        {
            if (!_initialized)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                _initialized = true;
            }
        }

        private int GetExpectedClientCount()
        {
            var playerList = PhotonManager.Instance?.GetPlayerList();
            if (playerList == null) return 0;
            return Math.Max(0, playerList.Length - 1);
        }

        private void OnPlayerLeft(Photon.Realtime.Player player)
        {
            if (_isGenerating)
            {
                _expectedClients = Math.Max(0, _expectedClients - 1);
                CivilizameMPPlugin.Log.LogInfo($"[Host] Cliente desconectado, clientes restantes: {_expectedClients}");
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "Juego" && MPStateManager.Instance.IsHost)
            {
                CivilizameMPPlugin.Log.LogInfo("[Host] Escena Juego cargada - esperando generación del mundo");
                StartCoroutine(WaitForWorldGeneration());
            }
        }

        private IEnumerator WaitForWorldGeneration()
        {
            if (_gameStarted) yield break;
            
            yield return new WaitForSeconds(0.5f);
            
            var gm = GameManager.Instance;
            if (gm == null)
            {
                CivilizameMPPlugin.Log.LogError("[Host] GameManager no encontrado");
                yield break;
            }
            
            int timeout = 0;
            while (!gm.WorldGenerated && timeout < 600)
            {
                yield return new WaitForSeconds(0.1f);
                timeout++;
            }
            
            if (!gm.WorldGenerated)
            {
                CivilizameMPPlugin.Log.LogError("[Host] Timeout esperando generación del mundo");
                yield break;
            }
            
            yield return new WaitForSeconds(0.5f);
            
            CivilizameMPPlugin.Log.LogInfo("[Host] Mundo generado correctamente - iniciando sincronización MP");
            
            _gameStarted = true;
            _isGenerating = true;
            _clientsReady = 0;
            _readyActors.Clear();
             
            var state = MPStateManager.Instance;
            _expectedClients = GetExpectedClientCount();
            
            MPStateManager.Instance.SetState(MPGameState.PlayingHost);
            
            AssignHostPlayer();
            
            // ENVIAR CONFIGURACIÓN COMPLETA A LOS CLIENTES
            SendGameConfigToAll();
            
            MPWaitingPanel.Instance?.SetStatus("ENVIANDO MAPA", $"Enviando mundo a {_expectedClients} clientes...");
            MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
            
            SaveAndSendState();
            
            if (_expectedClients > 0)
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO CLIENTES", $"Esperando confirmación de {_expectedClients} clientes...");
                
                int confirmTimeout = 0;
                while (_clientsReady < _expectedClients && confirmTimeout < 300)
                {
                    yield return new WaitForSeconds(0.1f);
                    confirmTimeout++;
                }
            }
            
            MPWaitingPanel.Instance?.Hide();
            _isGenerating = false;
            
            var gm2 = GameManager.Instance;
            if (gm2 != null)
            {
                var currentPlayer = gm2.Jug();
                bool isHumanTurn = currentPlayer != null && currentPlayer.RealPlayer;

                if (isHumanTurn && gm2.TurnOrder == MPMatchState.MiIndiceLocal)
                {
                    CivilizameMPPlugin.Log.LogInfo("[Host] Es tu turno!");
                }
                else if (isHumanTurn)
                {
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO TURNO", $"Turno del jugador {gm2.TurnOrder + 1}");
                    MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                }
                else
                {
                    MPWaitingPanel.Instance?.Hide();
                    MPPanelManager.Instance?.HideCurrentPanel();
                    CivilizameMPPlugin.Log.LogInfo("[Host] Turno de IA/host: sin cartel de espera");
                }
            }
            
            CivilizameMPPlugin.Log.LogInfo("[Host] Partida sincronizada correctamente");
        }

        private void SendGameConfigToAll()
        {
            var state = MPStateManager.Instance;
            var gm = GameManager.Instance;
            
            if (gm == null || gm.jugadores == null) return;
            
            int seed = MPGameSettingsHelper.GetSeed();
            int mapSize = MPGameSettingsHelper.GetMapSize();
            int mapType = MPGameSettingsHelper.GetMapType();
            int difficulty = MPGameSettingsHelper.GetDifficulty();
            
            var config = new GameConfigMessage
            {
                Seed = seed,
                MapSize = mapSize,
                MapType = mapType,
                Difficulty = difficulty,
                TotalPlayers = state.ConnectedPlayerCount,
                HumanPlayers = state.ConnectedPlayerCount,
                HostName = state.LocalPlayerName,
                PlayerSlots = new List<PlayerSlotConfig>()
            };
            
            // Recorrer los jugadores reales del GameManager
            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                var jug = gm.jugadores[i];
                if (jug == null) continue;
                
                // Buscar el slot correspondiente por nombre
                PlayerSlotConfig slot = null;
                foreach (var s in state.PlayerSlots)
                {
                    if (s.IsConnected && s.PlayerName == jug.Nombre)
                    {
                        slot = s;
                        break;
                    }
                }
                
                if (slot == null)
                {
                    // Si no se encuentra, crear uno basado en el jugador real
                    slot = new PlayerSlotConfig
                    {
                        SlotIndex = i,
                        ActorNumber = i + 1,
                        PlayerName = jug.Nombre,
                        IsHuman = jug.RealPlayer,
                        IsHost = i == 0,
                        IsConnected = jug.RealPlayer,
                        IsReady = true,
                        LeaderIndex = (int)jug.lider,
                        PrimaryColor = jug.color1,
                        SecondaryColor = jug.color2
                    };
                }
                else
                {
                    // Actualizar con datos del GameManager
                    slot.LeaderIndex = (int)jug.lider;
                    slot.PrimaryColor = jug.color1;
                    slot.SecondaryColor = jug.color2;
                }
                
                config.PlayerSlots.Add(slot);
            }
            
            // Si no hay slots, agregar al menos los conectados
            if (config.PlayerSlots.Count == 0)
            {
                foreach (var slot in state.PlayerSlots)
                {
                    if (slot.IsConnected)
                    {
                        config.PlayerSlots.Add(slot);
                    }
                }
            }
            
            string json = JsonUtility.ToJson(config);
            PhotonManager.Instance.SendConfigToAll(json);
            state.PendingGameConfig = config;
            
            CivilizameMPPlugin.Log.LogInfo($"[Host] Config enviada con {config.PlayerSlots.Count} jugadores");
        }

        private void AssignHostPlayer()
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null) return;
            
            var state = MPStateManager.Instance;
            var localPlayer = state.GetPlayerSlot(state.LocalActorNumber);
            if (localPlayer == null) return;
            
            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                var jug = gm.jugadores[i];
                if (jug != null && jug.RealPlayer)
                {
                    MPMatchState.SetLocalIndex(i);
                    jug.Nombre = localPlayer.PlayerName;
                    CivilizameMPPlugin.Log.LogInfo($"[Host] Host asignado como jugador {i}: {jug.Nombre}");
                    return;
                }
            }
        }

        private void SaveAndSendState()
        {
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
                    PhotonManager.Instance.SendState(stateData);
                    CivilizameMPPlugin.Log.LogInfo($"[Host] Estado inicial enviado: {stateData.Length} bytes");
                }
                else
                {
                    CivilizameMPPlugin.Log.LogError("[Host] No se pudo guardar el estado inicial");
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Host] Error en SaveAndSendState: {ex}");
            }
        }

        public void OnClientReady(int actorNumber, bool ready)
        {
            if (!MPStateManager.Instance.IsHost) return;
            if (!ready) return;
            if (_isGenerating) return;
            if (actorNumber <= 0) return;
            if (!_readyActors.Add(actorNumber)) return;
            if (_expectedClients <= 0) _expectedClients = GetExpectedClientCount();
            
            _clientsReady++;
            CivilizameMPPlugin.Log.LogInfo($"[Host] Cliente listo ({_clientsReady}/{_expectedClients})");
            MPWaitingPanel.Instance?.SetStatus("ESPERANDO CLIENTES", $"Clientes listos: {_clientsReady}/{_expectedClients}");
        }

        private void OnStateReceived(byte[] stateData)
        {
            if (!MPStateManager.Instance.IsHost) return;
            if (stateData == null || stateData.Length == 0) return;
            if (_isGenerating) return;

            try
            {
                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                File.WriteAllBytes(path, stateData);

                var gm = GameManager.Instance;
                if (gm == null) return;

                gm.LoadGuardadoSeguridad();

                var currentPlayer = gm.Jug();
                bool isHumanTurn = currentPlayer != null && currentPlayer.RealPlayer;

                if (isHumanTurn && gm.TurnOrder == MPMatchState.MiIndiceLocal)
                {
                   MPWaitingPanel.Instance?.Hide();
                   MPPanelManager.Instance?.HideCurrentPanel();
                }
                else if (isHumanTurn)
                {
                   MPWaitingPanel.Instance?.SetStatus("ESPERANDO TU TURNO", $"Turno del jugador {gm.TurnOrder + 1}");
                   MPPanelManager.Instance?.HideCurrentPanel();
                   MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                }
                else
                {
                   MPWaitingPanel.Instance?.Hide();
                   MPPanelManager.Instance?.HideCurrentPanel();
                   CivilizameMPPlugin.Log.LogInfo("[Host] Turno de IA/host: sin cartel de espera");
                }

                CivilizameMPPlugin.Log.LogInfo($"[Host] Estado recibido y aplicado: {stateData.Length} bytes");
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Host] Error aplicando estado recibido: {ex}");
            }
        }

        private void OnConfigReceived(string json)
        {
            if (!MPStateManager.Instance.IsHost) return;
            if (_isGenerating) return;
            
            try
            {
                if (json.Contains("\"ready\":true"))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Host] Error en OnConfigReceived: {ex}");
            }
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnConfigReceived -= OnConfigReceived;
                PhotonManager.Instance.OnPlayerLeftEvent -= OnPlayerLeft;
                PhotonManager.Instance.OnStateReceived -= OnStateReceived;
            }
        }
    }
}