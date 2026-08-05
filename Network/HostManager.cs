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
    public class HostManager : MonoBehaviour
    {
        public static HostManager Instance { get; private set; }
        
        private bool _isGenerating;
        private int _clientsReady;
        private int _expectedClients;
        private bool _waitingForScene;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            
            // Suscribirse a eventos de confirmación de clientes
            PhotonManager.Instance.OnConfigReceived += OnConfigReceived;
        }

        public void StartGame(GameConfigMessage config)
        {
            if (!MPStateManager.Instance.IsHost) 
            {
                CivilizameMPPlugin.Log.LogWarning("[Host] No soy host, no puedo iniciar partida");
                return;
            }
            
            if (_isGenerating) 
            {
                CivilizameMPPlugin.Log.LogWarning("[Host] Ya se está generando una partida");
                return;
            }
            
            _isGenerating = true;
            _clientsReady = 0;
            _expectedClients = MPStateManager.Instance.ConnectedPlayerCount - 1;
            
            CivilizameMPPlugin.Log.LogInfo($"[Host] Iniciando partida con {_expectedClients} clientes");
            
            MPStateManager.Instance.SetState(MPGameState.PlayingHost);
            
            // Configurar slots
            var state = MPStateManager.Instance;
            var slots = config.GetPlayerSlots();
            
            // Asegurar que el host está en los slots
            foreach (var player in state.ConnectedPlayers.Values)
            {
                if (player.ActorNumber == state.LocalActorNumber)
                {
                    player.PlayerName = state.LocalPlayerName;
                }
            }
            config.SetPlayerSlots(slots);
            state.PendingGameConfig = config;
            
            // Enviar configuración a todos
            PhotonManager.Instance.SendConfigToAll(JsonUtility.ToJson(config));
            
            // Mostrar panel de espera
            MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
            MPWaitingPanel.Instance?.SetStatus("GENERANDO MAPA", "Creando mundo...");
            
            // Iniciar el juego
            var gameSettings = FindObjectOfType<GameSettings>();
            if (gameSettings != null)
            {
                gameSettings.PlayGame();
            }
            else
            {
                WriteNewGameData(config);
                SceneManager.LoadScene("Juego");
            }
            
            _waitingForScene = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_waitingForScene && scene.name == "Juego")
            {
                _waitingForScene = false;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                StartCoroutine(WaitForWorldGeneration());
            }
        }

        private IEnumerator WaitForWorldGeneration()
        {
            yield return new WaitForSeconds(0.5f);
            
            var gm = GameManager.Instance;
            if (gm == null)
            {
                CivilizameMPPlugin.Log.LogError("[Host] GameManager no encontrado");
                _isGenerating = false;
                yield break;
            }
            
            // Esperar a que el mundo se genere
            int timeout = 0;
            while (!gm.WorldGenerated && timeout < 200)
            {
                yield return new WaitForSeconds(0.1f);
                timeout++;
            }
            
            if (!gm.WorldGenerated)
            {
                CivilizameMPPlugin.Log.LogWarning("[Host] Timeout esperando generación del mundo");
            }
            
            yield return new WaitForSeconds(0.5f);
            
            // Asignar el índice local del host
            AssignHostPlayer();
            
            // Guardar y enviar el estado inicial
            MPWaitingPanel.Instance?.SetStatus("ENVIANDO MAPA", $"Enviando mundo a {_expectedClients} clientes...");
            
            SaveAndSendState();
            
            // Esperar confirmación de clientes
            if (_expectedClients > 0)
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO CLIENTES", $"Esperando confirmación de {_expectedClients} clientes...");
                
                int confirmTimeout = 0;
                while (_clientsReady < _expectedClients && confirmTimeout < 300)
                {
                    yield return new WaitForSeconds(0.1f);
                    confirmTimeout++;
                }
                
                if (_clientsReady >= _expectedClients)
                {
                    CivilizameMPPlugin.Log.LogInfo("[Host] Todos los clientes confirmaron");
                }
                else
                {
                    CivilizameMPPlugin.Log.LogWarning($"[Host] Timeout esperando clientes ({_clientsReady}/{_expectedClients})");
                }
            }
            
            // Ocultar panel de espera
            MPWaitingPanel.Instance?.Hide();
            _isGenerating = false;
            
            CivilizameMPPlugin.Log.LogInfo("[Host] Partida iniciada correctamente");
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
                    // El host siempre es el primer jugador real
                    MPMatchState.SetLocalIndex(i);
                    
                    jug.Nombre = localPlayer.PlayerName;
                    jug.lider = (GameSettings.Lideres)localPlayer.LeaderIndex;
                    jug.color1 = localPlayer.PrimaryColor;
                    jug.color2 = localPlayer.SecondaryColor;
                    
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
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Host] Error en SaveAndSendState: {ex}");
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
                    _clientsReady++;
                    CivilizameMPPlugin.Log.LogInfo($"[Host] Cliente listo ({_clientsReady}/{_expectedClients})");
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO CLIENTES", $"Clientes listos: {_clientsReady}/{_expectedClients}");
                }
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Host] Error en OnConfigReceived: {ex}");
            }
        }

        public void OnClientReady()
        {
            _clientsReady++;
            CivilizameMPPlugin.Log.LogInfo($"[Host] Cliente listo ({_clientsReady}/{_expectedClients})");
            if (MPWaitingPanel.Instance != null)
            {
                MPWaitingPanel.Instance.SetStatus("ESPERANDO CLIENTES", $"Clientes listos: {_clientsReady}/{_expectedClients}");
            }
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
                
                // ¡CRÍTICO! El 9º parámetro (false) genera un nuevo mapa
                var data = new NewWorldData(
                    numPlayers,
                    false,
                    config.MapSize,
                    config.MapType,
                    lideres,
                    realPlayers,
                    colors,
                    nombres,
                    false,  // <- FALSE para GENERAR nuevo mapa
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
                
                CivilizameMPPlugin.Log.LogInfo($"[Host] NewGameData.gen escrito con modo generar=true");
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Host] Error escribiendo NewGameData.gen: {ex}");
            }
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (PhotonManager.Instance != null)
                PhotonManager.Instance.OnConfigReceived -= OnConfigReceived;
        }
    }
}