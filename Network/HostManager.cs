using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Photon.Realtime;
using CivilizameMP.Core;
using CivilizameMP.UI;

namespace CivilizameMP.Network
{
    /// <summary>
    /// Lado host del flujo de turnos. El host es al mismo tiempo "servidor" y
    /// jugador normal:
    ///  - Prepara y envía la config de slots cuando su partida vanilla arranca.
    ///  - Cuando un cliente devuelve su turno terminado, TurnController valida el
    ///    turno/seq y este restaura el estado con la recarga vanilla; el juego
    ///    avanza solo (IA vanilla incluida) y la cadena de turnos sigue.
    /// </summary>
    public class HostManager : MonoBehaviour
    {
        public static HostManager Instance { get; private set; }
        public bool IsGeneratingWorld => _isGenerating;
        /// <summary>True cuando la partida ya existe y la config de asientos fue enviada.</summary>
        public bool MatchStarted => _gameStarted;

        private bool _isGenerating;
        private bool _gameStarted;
        private bool _initialized;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // NOTA: los handoff entrantes NO se enrutan desde aquí. ClientManager ya
            // está suscrito y llama a TurnController.HandleIncomingHandoff, que
            // reparte según IsHost/IsClient. Duplicar la suscripción procesaba cada
            // estado DOS VECES en el host (doble restauración => el estado no se
            // aplicaba y el host se quedaba colgado).
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

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (PhotonManager.Instance != null)
                PhotonManager.Instance.OnPlayerLeftEvent -= OnPlayerLeft;
        }
        // ------------------------------------------------------------------
        //  Desconexiones
        // ------------------------------------------------------------------

        private void OnPlayerLeft(Photon.Realtime.Player player)
        {
            var state = MPStateManager.Instance;
            if (!state.IsHost) return;
            TurnController.OnClientDisconnected(player.ActorNumber);
            CivilizameMPPlugin.Log.LogWarning($"[Host] Jugador desconectado: {player?.NickName}. Su slot quedará gestionado por el estado del juego.");
        }

        // ------------------------------------------------------------------
        //  Partida vanilla del host: broadcast de la config de slots
        // ------------------------------------------------------------------

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != MPConstants.GAME_SCENE)
            {
                // Vuelve al menú: permitir una nueva partida en la siguiente sesión.
                if (scene.name == MPConstants.MENU_SCENE)
                    _gameStarted = false;
                return;
            }
            if (!StateHandoff.IsInGame()) return;
            if (!MPStateManager.Instance.IsHost) return;
            if (_gameStarted) return; // durante la partida solo entran por recargas de estado

            StartCoroutine(WaitForWorldAndBroadcast());
        }

        private IEnumerator WaitForWorldAndBroadcast()
        {
            _isGenerating = true;

            yield return new WaitForSeconds(1f);

            var gm = GameManager.Instance;
            int guard = 0;
            while (gm == null && guard < 120)
            {
                yield return new WaitForSeconds(0.5f);
                gm = GameManager.Instance;
                guard++;
            }
            if (gm == null)
            {
                _isGenerating = false;
                yield break;
            }

            guard = 0;
            while (!gm.WorldGenerated && guard < 240)
            {
                yield return new WaitForSeconds(0.5f);
                guard++;
            }
            yield return new WaitForSeconds(0.5f);

            _isGenerating = false;
            _gameStarted = true;

            MPStateManager.Instance.SetState(MPGameState.PlayingHost);

            // Los asientos ya no salen del lobby: se empareja cada humano (orden de
            // llegada a la sala) con los slots que el host marcó como reales en su
            // panel vanilla (RealPlayer de la partida recién creada).
            AssignHumanSlotsByArrival();
            SendGameConfigToAll();

            CivilizameMPPlugin.Log.LogInfo("[Host] Config de partida enviada. La cadena de turnos la lleva TurnController.");

            // Ya no toca esperar la creación manual: el juego arranca.
            MPWaitingPanel.Instance?.Hide();
            MPPanelManager.Instance?.HideCurrentPanel();
        }

        private void AssignHumanSlotsByArrival()
        {
            var state = MPStateManager.Instance;
            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null)
            {
                CivilizameMPPlugin.Log.LogError("[Host] GameManager o jugadores null al asignar slots");
                return;
            }

            // Slots vanilla marcados como "reales" por el host en su panel
            var realSlots = new System.Collections.Generic.List<int>();
            for (int i = 0; i < gm.jugadores.Length; i++)
                if (gm.jugadores[i] != null && gm.jugadores[i].RealPlayer)
                    realSlots.Add(i);

            // Humanos por orden de llegada (así los asignó el lobby: primer slot libre)
            var humans = new System.Collections.Generic.List<PlayerSlotConfig>();
            foreach (var s in state.PlayerSlots)
                if (s != null && s.ActorNumber > 0)
                    humans.Add(s);

            int paired = Math.Min(realSlots.Count, humans.Count);
            if (realSlots.Count != humans.Count)
                CivilizameMPPlugin.Log.LogWarning(
                    $"[Host] {humans.Count} humanos en la sala vs {realSlots.Count} jugadores reales en la partida. Se emparejan los primeros {paired}.");

            // Alinear el estado local con los slots del juego (por si hay IA en la sala vanilla)
            state.PlayerSlots.Clear();
            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                state.PlayerSlots.Add(new PlayerSlotConfig
                {
                    SlotIndex = i,
                    PlayerName = gm.jugadores[i] != null ? (gm.jugadores[i].Nombre ?? $"Jugador {i + 1}") : $"Jugador {i + 1}",
                    IsHuman = false,
                    ActorNumber = -1
                });
            }
            state.ConnectedPlayers.Clear();

            int localSlot = -1;
            for (int k = 0; k < paired; k++)
            {
                int gameSlot = realSlots[k];
                var human = humans[k];
                var jug = gm.jugadores[gameSlot];

                var slot = new PlayerSlotConfig
                {
                    SlotIndex = gameSlot,
                    ActorNumber = human.ActorNumber,
                    PlayerName = jug.Nombre ?? human.PlayerName,
                    IsHuman = true,
                    IsHost = k == 0,
                    IsConnected = true,
                    IsReady = true,
                    LeaderIndex = (int)jug.lider
                };
                state.PlayerSlots[gameSlot] = slot;
                state.ConnectedPlayers[human.ActorNumber] = slot;

                if (human.ActorNumber == state.LocalActorNumber)
                    localSlot = gameSlot;

                CivilizameMPPlugin.Log.LogInfo(
                    $"[Host] Llegada {k + 1}: {human.PlayerName} (actor {human.ActorNumber}) -> slot del juego {gameSlot} ({slot.PlayerName}, lider {(GameSettings.Lideres)jug.lider})");
            }

            // Un slot vanilla "Real" sin humano asignado bloquearía el ciclo de turnos
            // esperando su entrada: se juega como IA.
            foreach (int i in realSlots)
            {
                if (state.PlayerSlots[i].ActorNumber <= 0 && gm.jugadores[i].RealPlayer)
                {
                    gm.jugadores[i].RealPlayer = false;
                    CivilizameMPPlugin.Log.LogWarning($"[Host] Slot {i} era RealPlayer sin humano asignado -> se juega como IA");
                }
            }

            if (localSlot < 0)
            {
                CivilizameMPPlugin.Log.LogError("[Host] El host no obtuvo slot en el juego");
                return;
            }

            state.LocalSlotIndex = localSlot;
            state.IsHost = true;
            state.IsClient = false;
            MPMatchState.SetLocalIndex(localSlot);
            CivilizameMPPlugin.Log.LogInfo($"[Host] Slot del host: {localSlot}");
        }

        private void SendGameConfigToAll()
        {
            var state = MPStateManager.Instance;
            var gm = GameManager.Instance;

            if (gm == null || gm.jugadores == null)
            {
                CivilizameMPPlugin.Log.LogError("[Host] GameManager o jugadores es null al enviar config");
                return;
            }

            // Los asientos (tamaño/tipo/dificultad) vienen del panel vanilla del host
            // (SetupSync); el mundo ya existe porque el host lo creó con sus menús.
            int mapSize = SetupSync.MapSize > 0 ? SetupSync.MapSize : 0;
            int mapType = SetupSync.MapType;
            int difficulty = SetupSync.Dificultad >= 0 ? SetupSync.Dificultad : 0;
            int seed = 0; // el cliente no genera mundo: solo juega el estado recibido

            var slotsList = new System.Collections.Generic.List<PlayerSlotNetwork>();

            for (int i = 0; i < gm.jugadores.Length; i++)
            {
                var jug = gm.jugadores[i];
                if (jug == null) continue;

                PlayerSlotConfig photonSlot = state.PlayerSlots.Count > i ? state.PlayerSlots[i] : null;
                bool esHumano = photonSlot != null && photonSlot.ActorNumber > 0;

                if (esHumano)
                {
                    // Nombres vanilla del panel del host (nada de sobrescribir)
                    slotsList.Add(new PlayerSlotNetwork
                    {
                        SlotIndex = i,
                        ActorNumber = photonSlot.ActorNumber,
                        PlayerName = jug.Nombre ?? photonSlot.PlayerName,
                        IsHuman = true,
                        IsHost = photonSlot.ActorNumber == state.LocalActorNumber,
                        IsConnected = true,
                        IsReady = true,
                        LeaderIndex = (int)jug.lider
                    });
                }
                else
                {
                    slotsList.Add(new PlayerSlotNetwork
                    {
                        SlotIndex = i,
                        ActorNumber = -1,
                        PlayerName = jug.Nombre ?? $"IA {i + 1}",
                        IsHuman = false,
                        IsHost = false,
                        IsConnected = false,
                        IsReady = true,
                        LeaderIndex = (int)jug.lider
                    });
                }
            }

            var config = new GameConfigMessage
            {
                Seed = seed,
                MapSize = mapSize,
                MapType = mapType,
                Difficulty = difficulty,
                TotalPlayers = gm.jugadores.Length,
                HumanPlayers = state.ConnectedPlayerCount,
                HostName = state.LocalPlayerName,
                HostLeader = slotsList.Count > 0 ? slotsList[0].LeaderIndex : 0,
                PlayerSlotsArray = slotsList.ToArray()
            };

            CivilizameMPPlugin.Log.LogInfo($"[Host] Enviando config con {config.PlayerSlotsArray.Length} slots");
            string json = BuildConfigJson(config);
            PhotonManager.Instance.SendConfigToAll(json);
            state.PendingGameConfig = config;
        }

        private string BuildConfigJson(GameConfigMessage config)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{");
            sb.Append($"\"Seed\":{config.Seed},");
            sb.Append($"\"MapSize\":{config.MapSize},");
            sb.Append($"\"MapType\":{config.MapType},");
            sb.Append($"\"Difficulty\":{config.Difficulty},");
            sb.Append($"\"TotalPlayers\":{config.TotalPlayers},");
            sb.Append($"\"HumanPlayers\":{config.HumanPlayers},");
            sb.Append($"\"HostName\":\"{config.HostName}\",");
            sb.Append($"\"HostLeader\":{config.HostLeader},");

            sb.Append("\"PlayerSlotsArray\":[");
            if (config.PlayerSlotsArray != null && config.PlayerSlotsArray.Length > 0)
            {
                for (int i = 0; i < config.PlayerSlotsArray.Length; i++)
                {
                    var slot = config.PlayerSlotsArray[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append($"\"SlotIndex\":{slot.SlotIndex},");
                    sb.Append($"\"ActorNumber\":{slot.ActorNumber},");
                    sb.Append($"\"PlayerName\":\"{slot.PlayerName}\",");
                    sb.Append($"\"IsHuman\":{slot.IsHuman.ToString().ToLower()},");
                    sb.Append($"\"IsHost\":{slot.IsHost.ToString().ToLower()},");
                    sb.Append($"\"IsConnected\":{slot.IsConnected.ToString().ToLower()},");
                    sb.Append($"\"IsReady\":{slot.IsReady.ToString().ToLower()},");
                    sb.Append($"\"LeaderIndex\":{slot.LeaderIndex}");
                    sb.Append("}");
                }
            }
            sb.Append("]");
            sb.Append("}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Recepción de estados: la realiza ClientManager (TurnController).
        //  Este manager solo gestiona config/deconexiones para evitar duplicar
        //  el procesado de handoffs en el host.
        // ------------------------------------------------------------------
    }
}
