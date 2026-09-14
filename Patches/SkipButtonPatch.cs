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
            if (__instance == null) return true;

            if (__instance.TurnOrder != MPMatchState.MiIndiceLocal)
                return false;

            if (MPStateManager.Instance.IsHost)
            {
                return true;
            }
            else
            {

                ClientManager.Instance?.SendCurrentState();
                return false; // Bloquea el SkipButton original
            }
        }
    }
}