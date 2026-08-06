using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using CivilizameMP.Core;

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
            if (__instance.TurnOrder != MPMatchState.MiIndiceLocal) return false;
            return true;
        }
    }
}