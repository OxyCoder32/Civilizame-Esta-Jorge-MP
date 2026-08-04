using UnityEngine;
using CivilizameMP.Core;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Collections;

namespace CivilizameMP.Network
{
    public class HostManager : MonoBehaviour
    {
        public static HostManager Instance { get; private set; }

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void StartGame(GameConfigMessage config)
        {
            if (!MPStateManager.Instance.IsHost) return;
            MPStateManager.Instance.SetState(MPGameState.PlayingHost);
            WriteNewGameData(config);
            StartCoroutine(StartGameCoroutine(config.NumPlayers));
        }

        private IEnumerator StartGameCoroutine(int numPlayers)
        {
            yield return new WaitForSeconds(2.0f);

            var genManager = GenerationManager.Instance;
            if (genManager == null)
                genManager = FindObjectOfType<GenerationManager>();
            if (genManager == null)
            {
                var genObj = GameObject.Find("GenerationManager");
                if (genObj != null)
                    genManager = genObj.GetComponent<GenerationManager>();
            }

            if (genManager != null)
            {
                CivilizameMPPlugin.Log.LogInfo($"GenerationManager encontrado, iniciando partida con {numPlayers} jugadores...");
                genManager.SendMessage("Start", SendMessageOptions.DontRequireReceiver);
                genManager.Invoke("Start", 0f);
                yield return new WaitForSeconds(0.5f);
            }
            else
            {
                CivilizameMPPlugin.Log.LogError("[Host] GenerationManager no encontrado");
                GameManager.Instance?.Comienzo();
            }

            var gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                CivilizameMPPlugin.Log.LogInfo("GameManager encontrado, esperando generación...");
                yield return new WaitForSeconds(1.0f);
                
                int timeout = 0;
                while (!gameManager.WorldGenerated && timeout < 50)
                {
                    yield return new WaitForSeconds(0.1f);
                    timeout++;
                }
            }

            yield return new WaitForSeconds(1.0f);
            StateSyncManager.Instance.SaveCurrentState();
            StateSyncManager.Instance.SendState();
        }

        void WriteNewGameData(GameConfigMessage config)
        {
            int numPlayers = Mathf.Max(2, config.NumPlayers);
            var lideres = new GameSettings.Lideres[numPlayers];
            lideres[0] = (GameSettings.Lideres)config.HostLeader;

            for (int i = 1; i < numPlayers; i++)
                lideres[i] = (GameSettings.Lideres)0;

            var colors = new Color[numPlayers, 2];
            colors[0, 0] = Color.red;
            colors[0, 1] = new Color(1, 0.5f, 0);
            colors[1, 0] = Color.blue;
            colors[1, 1] = new Color(0, 0.5f, 1);

            for (int i = 2; i < numPlayers; i++)
            {
                colors[i, 0] = Color.gray;
                colors[i, 1] = Color.white;
            }

            var nombres = new string[numPlayers];
            nombres[0] = config.HostName;
            nombres[1] = string.IsNullOrEmpty(MPStateManager.Instance.RemotePlayerName) ? "Jugador 2" : MPStateManager.Instance.RemotePlayerName;

            for (int i = 2; i < numPlayers; i++)
                nombres[i] = "IA " + (i - 1);

            var realPlayers = new bool[numPlayers];
            realPlayers[0] = true;
            realPlayers[1] = true;

            for (int i = 2; i < numPlayers; i++)
                realPlayers[i] = false;

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
                -1
            );

            string path = Application.persistentDataPath + "/NewGameData.gen";
            var formatter = new BinaryFormatter();
            using (var stream = new FileStream(path, FileMode.Create))
            {
                formatter.Serialize(stream, data);
            }
        }


        public void ProcessClientState(byte[] state)
        {
            if (!MPStateManager.Instance.IsHost) return;

            StateSyncManager.Instance.DecompressAndLoad(state);
            StateSyncManager.Instance.WriteLoadDataForSync();

            var genManager = GenerationManager.Instance;
            if (genManager != null)
                genManager.SendMessage("Start", SendMessageOptions.DontRequireReceiver);
            else
                GameManager.Instance?.Comienzo();

            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.jugadores == null) return;

            int next = gameManager.TurnOrder;
            if (next >= 2 && next < gameManager.jugadores.Length && gameManager.jugadores[next] != null && !gameManager.jugadores[next].RealPlayer)
            {
                var aiManager = MainAIManager.Instance;
                if (aiManager != null)
                    aiManager.Invoke("PlayTurn", 0.5f);
            }
        }
    }
}