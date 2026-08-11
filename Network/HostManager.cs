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
    public class HostManager : MonoBehaviour
    {
        public static HostManager Instance { get; private set; }
        public bool IsGeneratingWorld => _isGenerating;
        
        private bool _isGenerating;
        private int _clientsReady;
        private int _expectedClients;
        private bool _gameStarted;
        private bool _initialized;
        private bool _waitingForClientConfirmation;
        private readonly HashSet<int> _readyActors = new HashSet<int>();
        private bool _worldSent;
        private bool _waitingForWorldGen;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            
            PhotonManager.Instance.OnConfigReceived += OnConfigReceived;
            PhotonManager.Instance.OnPlayerLeftEvent += OnPlayerLeft;
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
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "Juego" && MPStateManager.Instance.IsHost && !_worldSent)
            {
                StartCoroutine(WaitForWorldGeneration());
            }
        }

        private IEnumerator WaitForWorldGeneration()
        {
            if (_gameStarted) yield break;
            
            _isGenerating = true;
            _waitingForWorldGen = true;
            
            yield return new WaitForSeconds(1f);
            
            var gm = GameManager.Instance;
            if (gm == null)
            {
                _isGenerating = false;
                _waitingForWorldGen = false;
                yield break;
            }
            
            // Esperar a que el jugador host genere el mundo manualmente
            int timeout = 0;
            while (!gm.WorldGenerated && timeout < 60000)
            {
                yield return new WaitForSeconds(0.5f);
                timeout++;
            }
            
            if (!gm.WorldGenerated)
            {
                _isGenerating = false;
                _waitingForWorldGen = false;
                yield break;
            }
            
            yield return new WaitForSeconds(1f);
            
            _gameStarted = true;
            _clientsReady = 0;
            _readyActors.Clear();
            _expectedClients = GetExpectedClientCount();
            _worldSent = true;
            
            MPStateManager.Instance.SetState(MPGameState.PlayingHost);
            
            AssignHostPlayer();
            SendGameConfigToAll();
            
            // Esperar a que todos los clientes confirmen ready post-config
            if (_expectedClients > 0)
            {
                _waitingForClientConfirmation = true;
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO CLIENTES", $"Esperando confirmacion de {_expectedClients} clientes...");
                
                int confirmTimeout = 0;
                while (_clientsReady < _expectedClients && confirmTimeout < 300)
                {
                    yield return new WaitForSeconds(0.1f);
                    confirmTimeout++;
                }
                _waitingForClientConfirmation = false;
            }
            
            // Todos listos, enviar estado del mundo UNA VEZ
            SaveAndSendState();
            
            MPWaitingPanel.Instance?.Hide();
            _isGenerating = false;
            _waitingForWorldGen = false;
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
            
            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                var jug = gm.jugadores[i];
                if (jug == null) continue;
                
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
                    slot.LeaderIndex = (int)jug.lider;
                    slot.PrimaryColor = jug.color1;
                    slot.SecondaryColor = jug.color2;
                }
                
                config.PlayerSlots.Add(slot);
            }
            
            if (config.PlayerSlots.Count == 0)
            {
                foreach (var slot in state.PlayerSlots)
                {
                    if (slot.IsConnected)
                        config.PlayerSlots.Add(slot);
                }
            }
            
            string json = JsonUtility.ToJson(config);
            PhotonManager.Instance.SendConfigToAll(json);
            state.PendingGameConfig = config;
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
                    return;
                }
            }
        }

        private float _lastSendTime;
        private const float SEND_DEBOUNCE = 0.3f;

        public void SaveAndSendState()
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
                    PhotonManager.Instance.SendStateToAll(stateData);
                    CivilizameMPPlugin.Log.LogInfo($"[Host] Estado enviado: {stateData.Length} bytes, turno {gm.TurnOrder}");
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
            if (actorNumber <= 0) return;
            if (!_readyActors.Add(actorNumber)) return;
            if (_expectedClients <= 0) _expectedClients = GetExpectedClientCount();
            
            _clientsReady++;
            CivilizameMPPlugin.Log.LogInfo($"[Host] Cliente ready: {_clientsReady}/{_expectedClients}");
        }

        private void OnConfigReceived(string json)
        {
            if (!MPStateManager.Instance.IsHost) return;
            if (_isGenerating) return;
            
            try
            {
                if (json.Contains("\"ready\":true"))
                    return;
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
            }
        }
    }
}