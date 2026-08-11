using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;
using CivilizameMP.UI;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(GameManager), "NextTurn")]
    public class NextTurnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameManager __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return;
            if (__instance == null) return;
            if (__instance.TurnOrder < 0) return;

            UpdateTurnUI(__instance);

            // Solo el host envía estado después de NextTurn cuando es su PROPIO turno el que terminó
            // (es decir, cuando el host presionó SkipButton y el juego llamó NextTurn)
            if (!MPStateManager.Instance.IsHost) return;
            if (HostManager.Instance != null && HostManager.Instance.IsGeneratingWorld) return;

            // Si es turno de IA, HostStateProcessor se encarga (no debería pasar por aquí
            // porque el host no llama NextTurn para IA, la IA se ejecuta sola)
            if (MPMatchState.IsAITurn(__instance))
                return;

            // Es turno humano. Guardar y enviar.
            HostManager.Instance?.SaveAndSendState();
        }

        private static void UpdateTurnUI(GameManager gm)
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
                return;
            }

            if (MPMatchState.IsRemoteHumanTurn(gm))
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO TU TURNO", $"Turno del jugador {gm.TurnOrder + 1}");
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
            }
        }
    }
}