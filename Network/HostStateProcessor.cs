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
                
                if (!MPMatchState.IsLocalTurn(gm))
                {
                    var nextTurnMethod = typeof(GameManager).GetMethod("NextTurn", BindingFlags.Public | BindingFlags.Instance);
                    if (nextTurnMethod != null)
                    {
                        nextTurnMethod.Invoke(gm, new object[] { false, 0, false });
                    }
                }
                
                var infoToFile = gm.GetComponent<InformationToFile>();
                if (infoToFile != null) infoToFile.GuardadoSeguridad();
                if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();
                
                UpdateHostTurnUI(gm);
                
                _isProcessing = false;
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[HostStateProcessor] Error: {ex}");
                _isProcessing = false;
            }
        }

        private void UpdateHostTurnUI(GameManager gm)
        {
            if (gm == null || MPMatchState.MiIndiceLocal < 0) return;

            if (MPMatchState.IsAITurn(gm))
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo("[HostStateProcessor] Turno de IA: sin cartel de espera");
                return;
            }

            if (MPMatchState.IsLocalTurn(gm))
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo("[HostStateProcessor] ¡Es tu turno!");
                return;
            }

            if (MPMatchState.IsRemoteHumanTurn(gm))
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO TU TURNO", $"Turno del jugador {gm.TurnOrder + 1}");
                MPPanelManager.Instance?.HideCurrentPanel();
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] Turno del jugador {gm.TurnOrder}");
            }
        }

        void OnDestroy()
        {
            if (PhotonManager.Instance != null)
                PhotonManager.Instance.OnStateReceived -= OnStateReceived;
        }
    }
}