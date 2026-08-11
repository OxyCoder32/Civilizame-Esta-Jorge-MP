using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(GameManager))]
    public class SkipButtonPatch
    {
        [HarmonyPatch("SkipButton")]
        [HarmonyPrefix]
        public static bool SkipButtonPrefix(GameManager __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return true;
            
            // Solo el jugador del turno actual puede hacer skip
            if (__instance.TurnOrder != MPMatchState.MiIndiceLocal) return false;

            if (MPStateManager.Instance.IsHost)
            {
                // Host: deja que el juego haga NextTurn normalmente.
                // TurnSyncPatches.Postfix se encargará de guardar y enviar.
                return true;
            }
            else
            {
                // Cliente: llama NextTurn localmente para avanzar el turno,
                // luego guarda estado con turno YA avanzado y envía al host.
                var nextTurnMethod = typeof(GameManager).GetMethod("NextTurn", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (nextTurnMethod != null)
                    nextTurnMethod.Invoke(__instance, new object[] { false, 0, false });
                
                ClientManager.Instance?.SendCurrentState();
                return false;
            }
        }
    }
}