using HarmonyLib;
using CivilizameMP.Core;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(GameManager))]
    public class GameControlPatches
    {
        private static bool ShouldBlockControl()
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return false;
            if (!MPMatchState.IsInitialized) return true;
            return GameManager.Instance?.TurnOrder != MPMatchState.MiIndiceLocal;
        }
        
        [HarmonyPatch("Move")]
        [HarmonyPrefix]
        public static bool MovePrefix() => !ShouldBlockControl();
        
        [HarmonyPatch("Build")]
        [HarmonyPrefix]
        public static bool BuildPrefix() => !ShouldBlockControl();
        
        [HarmonyPatch("BuildThing")]
        [HarmonyPrefix]
        public static bool BuildThingPrefix() => !ShouldBlockControl();
        
        [HarmonyPatch("Convert")]
        [HarmonyPrefix]
        public static bool ConvertPrefix() => !ShouldBlockControl();
        
        [HarmonyPatch("Inquisition")]
        [HarmonyPrefix]
        public static bool InquisitionPrefix() => !ShouldBlockControl();
        
        [HarmonyPatch("ScienceTree")]
        [HarmonyPrefix]
        public static bool ScienceTreePrefix() => !ShouldBlockControl();
        
        [HarmonyPatch("CultureTree")]
        [HarmonyPrefix]
        public static bool CultureTreePrefix() => !ShouldBlockControl();
    }
}