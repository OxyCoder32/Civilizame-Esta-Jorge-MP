using UnityEngine;
using CivilizameMP.Core;
using CivilizameMP.UI;
using CivilizameMP.Network;
using System.IO;
using System.Collections;
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
        }

        private void OnStateReceived(byte[] stateData)
        {
            // Solo el host procesa estados
            if (!MPStateManager.Instance.IsHost) return;
            if (_isProcessing) return;
            if (stateData == null || stateData.Length == 0) return;
            
            // Verificar que estamos en una partida activa
            if (MPStateManager.Instance.CurrentState != MPGameState.PlayingHost) return;
            
            _isProcessing = true;
            
            try
            {
                CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] Procesando estado de {stateData.Length} bytes");
                
                // 1. Escribir el estado recibido en disco
                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                File.WriteAllBytes(path, stateData);
                
                // 2. Cargar el estado en el juego
                var gm = GameManager.Instance;
                if (gm == null)
                {
                    CivilizameMPPlugin.Log.LogError("[HostStateProcessor] GameManager no encontrado");
                    _isProcessing = false;
                    return;
                }
                
                gm.LoadGuardadoSeguridad();
                
                // 3. Avanzar el turno nativamente
                CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] Avanzando turno desde {gm.TurnOrder}");
                
                // Usar NextTurn para avanzar correctamente
                var nextTurnMethod = typeof(GameManager).GetMethod("NextTurn", BindingFlags.Public | BindingFlags.Instance);
                if (nextTurnMethod != null)
                {
                    nextTurnMethod.Invoke(gm, new object[] { false, 0, false });
                }
                else
                {
                    // Fallback: usar SkipTurn del jugador actual
                    var jug = gm.Jug();
                    if (jug != null)
                    {
                        jug.SkipTurn();
                    }
                    else
                    {
                        CivilizameMPPlugin.Log.LogError("[HostStateProcessor] No se pudo avanzar el turno");
                        _isProcessing = false;
                        return;
                    }
                }
                
                CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] Turno avanzado a {gm.TurnOrder}");
                
                // 4. Guardar el nuevo estado
                var infoToFile = gm.GetComponent<InformationToFile>();
                if (infoToFile != null) infoToFile.GuardadoSeguridad();
                if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();
                
                // 5. Leer y transmitir el nuevo estado
                if (File.Exists(path))
                {
                    byte[] updatedState = File.ReadAllBytes(path);
                    PhotonManager.Instance.SendState(updatedState);
                    CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] Nuevo estado transmitido: {updatedState.Length} bytes");
                }
                else
                {
                    CivilizameMPPlugin.Log.LogError("[HostStateProcessor] No se pudo guardar el estado actualizado");
                }
                
                // 6. Mostrar estado en UI
                if (gm.TurnOrder == MPMatchState.MiIndiceLocal)
                {
                    MPWaitingPanel.Instance?.Hide();
                    CivilizameMPPlugin.Log.LogInfo("[HostStateProcessor] Es tu turno!");
                }
                else
                {
                    MPWaitingPanel.Instance?.SetStatus("ESPERANDO OPONENTE", "Turno del otro jugador...");
                    CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] Turno del jugador {gm.TurnOrder}");
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