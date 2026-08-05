using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;
using CivilizameMP.UI;
using System.IO;
using UnityEngine;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(GameManager))]
    public class SkipButtonPatch
    {
        [HarmonyPatch("SkipButton")]
        [HarmonyPrefix]
        public static bool SkipButtonPrefix(GameManager __instance)
        {
            // Solo intervenir en modo multijugador
            if (!MPStateManager.Instance.IsMultiplayerActive) return true;
            
            // Si no es mi turno, no hacer nada
            if (__instance.TurnOrder != MPMatchState.MiIndiceLocal) 
            {
                CivilizameMPPlugin.Log.LogWarning("[SkipButton] No es mi turno, ignorando SkipButton");
                return false;
            }
            
            var jug = __instance.Jug();
            if (jug == null) return true;
            
            // Verificar que sea un jugador real
            if (!jug.RealPlayer) return true;
            
            try
            {
                CivilizameMPPlugin.Log.LogInfo($"[SkipButton] Procesando fin de turno para jugador {__instance.TurnOrder}");
                
                // 1. Forzar guardado completo
                var infoToFile = __instance.GetComponent<InformationToFile>();
                if (infoToFile != null)
                {
                    infoToFile.GuardadoSeguridad();
                    CivilizameMPPlugin.Log.LogInfo("[SkipButton] Guardado de seguridad completado");
                }
                
                if (Tablero.Instance != null)
                {
                    Tablero.Instance.GuardadoSeg();
                    CivilizameMPPlugin.Log.LogInfo("[SkipButton] Guardado de tablero completado");
                }
                
                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                if (!File.Exists(path))
                {
                    CivilizameMPPlugin.Log.LogError("[SkipButton] Archivo de guardado no encontrado");
                    return true;
                }
                
                byte[] worldData = File.ReadAllBytes(path);
                CivilizameMPPlugin.Log.LogInfo($"[SkipButton] Estado leído: {worldData.Length} bytes");
                
                PhotonManager.Instance.SendState(worldData);
                CivilizameMPPlugin.Log.LogInfo("[SkipButton] Estado enviado a la red");
                
                MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                MPWaitingPanel.Instance?.SetStatus("TURNO ENVIADO", "Esperando al otro jugador...");
                
                return false;
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[SkipButton] Error: {ex}");
                return true;
            }
        }
    }
}