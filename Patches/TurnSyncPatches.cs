using HarmonyLib;
using UnityEngine;
using CivilizameMP.Core;
using CivilizameMP.Network;
using CivilizameMP.UI;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(GameManager))]
    public class TurnSyncPatches
    {
        [HarmonyPatch("NextTurn")]
        [HarmonyPostfix]
        public static void NextTurnPostfix(GameManager __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return;

            int currentTurn = __instance.TurnOrder;
            bool isHost = MPStateManager.Instance.IsHost;
            bool isClient = MPStateManager.Instance.IsClient;

            if (isClient && currentTurn == 1)
            {
                MPWaitingPanel.Instance?.Hide();
            }

            if (isHost && currentTurn == 0)
            {
                MPWaitingPanel.Instance?.Hide();
            }
        }

        [HarmonyPatch("SkipButton")]
        [HarmonyPrefix]
        public static bool SkipButtonPrefix(GameManager __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return true;

            int currentTurn = __instance.TurnOrder;
            bool isHost = MPStateManager.Instance.IsHost;
            bool isClient = MPStateManager.Instance.IsClient;

            bool isMyTurn = (isHost && currentTurn == 0) || (isClient && currentTurn == 1);
            if (!isMyTurn) return false;

                if (MPStateManager.Instance.IsHost)
                {
                    StateSyncManager.Instance.SaveCurrentState();
                    byte[] state = StateSyncManager.Instance.CompressState();
                    if (state != null)
                    {
                        PhotonManager.Instance.SendState(state);
                    }
                }

                MPPanelManager.Instance.ShowPanel(MPPanelType.Waiting);
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO OPONENTE", "Sincronizando partida...");

            return true;
        }

        [HarmonyPatch("SkipButton")]
        [HarmonyPostfix]
        public static void SkipButtonPostfix(GameManager __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return;

            int currentTurn = __instance.TurnOrder;
            bool isHost = MPStateManager.Instance.IsHost;

            if (isHost && currentTurn >= 2 && currentTurn < __instance.jugadores.Length)
            {
                if (!__instance.jugadores[currentTurn].RealPlayer && MainAIManager.Instance != null)
                {
                    MainAIManager.Instance.Invoke("PlayTurn", 0.1f);
                }
            }
        }
    }
}