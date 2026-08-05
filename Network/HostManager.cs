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
        private bool _waitingForScene;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void StartGame(GameConfigMessage config)
        {
            if (!MPStateManager.Instance.IsHost) return;
            if (_isGenerating) return;
            
            _isGenerating = true;
            MPStateManager.Instance.SetState(MPGameState.PlayingHost);
            
            var state = MPStateManager.Instance;
            var slots = new List<PlayerSlotConfig>();
            
            foreach (var player in state.ConnectedPlayers.Values)
            {
                if (player.ActorNumber == state.LocalActorNumber)
                {
                    player.PlayerName = state.LocalPlayerName;
                }
                slots.Add(player);
            }
            config.SetPlayerSlots(slots);
            
            WriteNewGameData(config);
            PhotonManager.Instance.SendConfigToAll(JsonUtility.ToJson(config));
            
            MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
            MPWaitingPanel.Instance?.SetStatus("GENERANDO MAPA", $"Creando mundo para {config.TotalPlayers} jugadores...");
            
            _waitingForScene = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.LoadScene("Juego");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_waitingForScene && scene.name == "Juego")
            {
                _waitingForScene = false;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                StartCoroutine(GenerateWorldCoroutine());
            }
        }

        private IEnumerator GenerateWorldCoroutine()
        {
            yield return new WaitForSeconds(0.5f);
            
            var genManager = GenerationManager.Instance ?? FindObjectOfType<GenerationManager>();
            if (genManager == null)
            {
                var genObj = GameObject.Find("GenerationManager");
                if (genObj != null) genManager = genObj.GetComponent<GenerationManager>();
            }
            
            if (genManager != null)
            {
                CivilizameMPPlugin.Log.LogInfo($"[Host] Generando mundo...");
                genManager.SendMessage("Start", SendMessageOptions.DontRequireReceiver);
                genManager.Invoke("Start", 0f);
                
                yield return new WaitForSeconds(3.0f);
                
                var gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    int timeout = 0;
                    while (!gameManager.WorldGenerated && timeout < 100)
                    {
                        yield return new WaitForSeconds(0.1f);
                        timeout++;
                    }
                }
            }
            else
            {
                CivilizameMPPlugin.Log.LogError("[Host] GenerationManager no encontrado");
                GameManager.Instance?.Comienzo();
                yield return new WaitForSeconds(1.0f);
            }
            
            yield return new WaitForSeconds(0.5f);
            
            StateSyncManager.Instance.SaveCurrentState();
            
            yield return new WaitForSeconds(0.5f);
            
            StateSyncManager.Instance.SendState();
            
            yield return new WaitForSeconds(1.0f);
            
            MPWaitingPanel.Instance?.Hide();
            _isGenerating = false;
            CivilizameMPPlugin.Log.LogInfo("[Host] Partida iniciada correctamente");
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
            
            CivilizameMPPlugin.Log.LogInfo($"[Host] NewGameData.gen escrito: {numPlayers} jugadores, semilla {config.Seed}");
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }
}