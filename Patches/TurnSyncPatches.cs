using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;
using CivilizameMP.UI;
using System.IO;
using UnityEngine;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(Jugador))]
    public class TurnSyncPatches
    {
        [HarmonyPatch("SkipTurn")]
        [HarmonyPostfix]
        public static void SkipTurnPostfix(Jugador __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return;
            if (!__instance.RealPlayer) return;
            
            var gm = GameManager.Instance;
            if (gm == null) return;
            if (gm.TurnOrder != MPMatchState.MiIndiceLocal) return;
            
            try
            {
                var infoToFile = gm.GetComponent<InformationToFile>();
                if (infoToFile != null)
                {
                    infoToFile.GuardadoSeguridad();
                }
                if (Tablero.Instance != null)
                {
                    Tablero.Instance.GuardadoSeg();
                }
                
                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                if (!File.Exists(path)) return;
                
                byte[] worldData = File.ReadAllBytes(path);
                PhotonManager.Instance.SendState(worldData);
                
                MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                MPWaitingPanel.Instance?.SetStatus("TURNO ENVIADO", "Esperando procesamiento del servidor...");
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[TurnSync] Error en SkipTurnPostfix: {ex}");
            }
        }
    }
}