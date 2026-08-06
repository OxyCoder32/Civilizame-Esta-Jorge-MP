using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;
using CivilizameMP.UI;
using System.IO;
using UnityEngine;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(Jugador), "SkipTurn")]
    public class TurnSyncPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Jugador __instance)
        {
            if (!__instance.RealPlayer) return;
            if (!MPStateManager.Instance.IsMultiplayerActive) return;
            
            var gm = GameManager.Instance;
            if (gm == null) return;
            if (gm.TurnOrder != MPMatchState.MiIndiceLocal) return;
            
            try
            {
                var infoToFile = gm.GetComponent<InformationToFile>();
                if (infoToFile != null) infoToFile.GuardadoSeguridad();
                if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();

                string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                if (!File.Exists(path)) return;

                byte[] worldData = File.ReadAllBytes(path);
                PhotonManager.Instance.SendState(worldData);
                
                MPWaitingPanel.Instance?.SetStatus("TURNO ENVIADO", "Esperando al otro jugador...");
                MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Turno del jugador {MPMatchState.MiIndiceLocal} completado");
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[TurnSync] Error en SkipTurnPrefix: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), "NextTurn")]
    public class NextTurnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameManager __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return;
            if (__instance.TurnOrder == -1) return;
            
            int localIndex = MPMatchState.MiIndiceLocal;
            
            if (__instance.TurnOrder == localIndex)
            {
                MPWaitingPanel.Instance?.Hide();
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] ¡Es tu turno! (Índice: {localIndex})");
            }
            else
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO OPONENTE", $"Turno del jugador {__instance.TurnOrder + 1}");
                MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Turno del jugador {__instance.TurnOrder}");
            }
        }
    }
}