using UnityEngine;
using CivilizameMP.Core;
using CivilizameMP.UI;
using CivilizameMP.Network;
using System.IO;
using System.Reflection;

namespace CivilizameMP.Network
{
    public class HostStateProcessor : MonoBehaviour
    {
        private static HostStateProcessor _instance;
        
        public static HostStateProcessor Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("HostStateProcessor");
                    _instance = go.AddComponent<HostStateProcessor>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }
        
        private bool _isProcessing;
        private bool _initialized;

        void Awake()
        {
            if (_instance != null && _instance != this) 
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            Initialize();
        }

        private void Initialize()
        {
            if (_initialized) return;
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnStateReceived += OnStateReceived;
                _initialized = true;
                CivilizameMPPlugin.Log.LogInfo("[HostStateProcessor] Inicializado");
            }
            else
            {
                StartCoroutine(WaitForPhotonManager());
            }
        }

        private System.Collections.IEnumerator WaitForPhotonManager()
        {
            int attempts = 0;
            while (PhotonManager.Instance == null && attempts < 50)
            {
                yield return new WaitForSeconds(0.1f);
                attempts++;
            }
            
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnStateReceived += OnStateReceived;
                _initialized = true;
                CivilizameMPPlugin.Log.LogInfo("[HostStateProcessor] Inicializado (tardío)");
            }
            else
            {
                CivilizameMPPlugin.Log.LogError("[HostStateProcessor] No se pudo inicializar - PhotonManager no encontrado");
            }
        }

        private void OnStateReceived(byte[] stateData)
        {
            if (!MPStateManager.Instance.IsHost) return;
            if (_isProcessing) return;
            if (stateData == null || stateData.Length == 0) return;
            
            _isProcessing = true;
            
            try
            {
                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                File.WriteAllBytes(path, stateData);
                
                var gm = GameManager.Instance;
                if (gm == null) 
                { 
                    _isProcessing = false; 
                    return; 
                }
                
                gm.LoadGuardadoSeguridad();
                
                var nextTurnMethod = typeof(GameManager).GetMethod("NextTurn", BindingFlags.Public | BindingFlags.Instance);
                if (nextTurnMethod != null)
                {
                    nextTurnMethod.Invoke(gm, new object[] { false, 0, false });
                }
                else
                {
                    _isProcessing = false;
                    return;
                }
                
                var infoToFile = gm.GetComponent<InformationToFile>();
                if (infoToFile != null) infoToFile.GuardadoSeguridad();
                if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();
                
                if (File.Exists(path))
                {
                    byte[] updatedState = File.ReadAllBytes(path);
                    PhotonManager.Instance.SendStateToAll(updatedState);
                }
                
                if (gm.TurnOrder == MPMatchState.MiIndiceLocal)
                {
                    MPWaitingPanel.Instance?.Hide();
                }
                else
                {
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO OPONENTE", $"Turno del jugador {gm.TurnOrder + 1}");
                    MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                }
                
                _isProcessing = false;
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[HostStateProcessor] Error: {ex}");
                _isProcessing = false;
            }
        }

        void OnDestroy()
        {
            if (PhotonManager.Instance != null)
                PhotonManager.Instance.OnStateReceived -= OnStateReceived;
        }
    }
}