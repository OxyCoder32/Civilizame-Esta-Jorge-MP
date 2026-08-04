using UnityEngine;
using CivilizameMP.Core;

namespace CivilizameMP.Network
{
    public class ClientManager : MonoBehaviour
    {
        public static ClientManager Instance { get; private set; }
        private bool _isLoading;

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
        }

        void OnConfigReceived(string json)
        {
            if (MPStateManager.Instance.IsHost) return;
            MPStateManager.Instance.PendingConfig = JsonUtility.FromJson<GameConfigMessage>(json);
            CivilizameMPPlugin.Log.LogInfo("[Client] Config recibida del host");
        }

        void OnStateReceived(byte[] state)
        {
            if (MPStateManager.Instance.IsHost || _isLoading) return;
            
            var currentState = MPStateManager.Instance.CurrentState;
            if (currentState == MPGameState.PlayingClient || currentState == MPGameState.LoadingGame)
                return;
            
            _isLoading = true;
            MPStateManager.Instance.SetState(MPGameState.LoadingGame);
            
            StateSyncManager.Instance.DecompressAndLoad(state);
            StateSyncManager.Instance.WriteLoadDataForSync();
            
            if (GenerationManager.Instance != null)
                GenerationManager.Instance.Invoke("Start", 0f);
            else
                CivilizameMPPlugin.Log.LogError("[Client] GenerationManager no disponible");
            
            MPStateManager.Instance.SetState(MPGameState.PlayingClient);
            _isLoading = false;
            
            CivilizameMPPlugin.Log.LogInfo("[Client] Partida cargada desde estado del host");
        }

        void OnDestroy()
        {
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnConfigReceived -= OnConfigReceived;
                PhotonManager.Instance.OnStateReceived -= OnStateReceived;
            }
        }
    }
}