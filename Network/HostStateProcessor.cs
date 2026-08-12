using UnityEngine;
using CivilizameMP.Core;
using CivilizameMP.Network;
using CivilizameMP.UI;
using System.IO;
using System.Reflection;
using System.Collections;

namespace CivilizameMP.Network
{
    public class HostStateProcessor : MonoBehaviour
    {
        private static HostStateProcessor _instance;
        public static HostStateProcessor Instance => _instance;
        
        private bool _isProcessing;

        void Awake()
        {
            if (_instance != null && _instance != this) 
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            
            if (PhotonManager.Instance != null)
                PhotonManager.Instance.OnStateReceived += OnStateReceived;
        }

        private void OnStateReceived(byte[] stateData, int sequence, string hash, int senderId)
        {
            if (!MPStateManager.Instance.IsHost) return;
            if (_isProcessing) return;
            if (stateData == null || stateData.Length == 0) return;
            if (senderId == MPStateManager.Instance.LocalActorNumber) return;
            
            _isProcessing = true;
            
            try
            {
                string computedHash = PhotonManager.ComputeHash(stateData);
                if (computedHash != hash)
                {
                    _isProcessing = false;
                    return;
                }
                
                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                File.WriteAllBytes(path, stateData);
                
                var gm = GameManager.Instance;
                if (gm == null) 
                { 
                    _isProcessing = false; 
                    return; 
                }
                
                int turnoAntes = gm.TurnOrder;
                
                gm.LoadGuardadoSeguridad();
                
                int turnoDespues = gm.TurnOrder;
                CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] Estado cargado de cliente {senderId}. Turno: {turnoAntes} -> {turnoDespues}");
                
                if (turnoDespues == turnoAntes && !MPMatchState.IsLocalTurn(gm) && !MPMatchState.IsAITurn(gm))
                {
                    CivilizameMPPlugin.Log.LogInfo("[HostStateProcessor] Turno es de cliente remoto, avanzando...");
                    
                    var nextTurnMethod = typeof(GameManager).GetMethod("NextTurn", 
                        BindingFlags.Public | BindingFlags.Instance);
                    if (nextTurnMethod != null)
                        nextTurnMethod.Invoke(gm, new object[] { false, 0, false });
                    
                    turnoDespues = gm.TurnOrder;
                    CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] NextTurn ejecutado. Nuevo turno: {turnoDespues}");
                }
                
                UpdateTurnUI(gm);

                if (MPMatchState.IsAITurn(gm))
                {
                    StartCoroutine(ProcessAIAndContinue(gm));
                    return;
                }
                
                HostManager.Instance?.SaveAndSendState();
                _isProcessing = false;
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[HostStateProcessor] Error: {ex}");
                _isProcessing = false;
            }
        }

        private IEnumerator ProcessAIAndContinue(GameManager gm)
        {
            CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] Procesando IA para jugador {gm.TurnOrder}");
            
            float startTime = Time.time;
            while (Time.time - startTime < 60f)
            {
                if (gm == null || gm.TurnOrder < 0 || gm.jugadores == null || gm.TurnOrder >= gm.jugadores.Length)
                    break;

                var jug = gm.jugadores[gm.TurnOrder];
                if (jug == null || jug.Turno == false)
                    break;

                yield return new WaitForSeconds(0.2f);
            }
            
            if (gm == null)
            {
                _isProcessing = false;
                yield break;
            }
            
            CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] IA terminó para jugador {gm.TurnOrder}");
            
            // Guardar estado post-IA
            var infoToFile = gm.GetComponent<InformationToFile>();
            if (infoToFile != null) infoToFile.GuardadoSeguridad();
            if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();

            // Avanzar al siguiente turno
            var nextTurnMethod = typeof(GameManager).GetMethod("NextTurn", 
                BindingFlags.Public | BindingFlags.Instance);
            if (nextTurnMethod != null)
                nextTurnMethod.Invoke(gm, new object[] { false, 0, false });
            
            CivilizameMPPlugin.Log.LogInfo($"[HostStateProcessor] NextTurn ejecutado. Nuevo turno: {gm.TurnOrder}");
            
            UpdateTurnUI(gm);
            
            if (MPMatchState.IsAITurn(gm))
            {
                StartCoroutine(ProcessAIAndContinue(gm));
                yield break;
            }
            
            HostManager.Instance?.SaveAndSendState();
            _isProcessing = false;
        }

        private void UpdateTurnUI(GameManager gm)
        {
            if (gm == null || MPMatchState.MiIndiceLocal < 0) return;

            if (MPMatchState.IsAITurn(gm))
            {
                MPWaitingPanel.Instance?.SetStatus("TURNO DE IA", $"IA jugando... (Jugador {gm.TurnOrder + 1})");
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                return;
            }

            if (MPMatchState.IsLocalTurn(gm))
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo($"[Host] >>> ES TU TURNO <<< (Jugador {gm.TurnOrder + 1})");
                return;
            }

            if (MPMatchState.IsRemoteHumanTurn(gm))
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO TU TURNO", $"Turno del jugador {gm.TurnOrder + 1}");
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
            }
        }

        void OnDestroy()
        {
            if (PhotonManager.Instance != null)
                PhotonManager.Instance.OnStateReceived -= OnStateReceived;
        }
    }
}