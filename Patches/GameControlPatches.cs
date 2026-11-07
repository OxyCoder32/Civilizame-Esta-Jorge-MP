using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(GameManager))]
    public class GameControlPatches
    {
        private static bool ShouldBlockControl()
        {
            if (!StateHandoff.IsInGame()) return false;
            if (!MPMatchState.IsInitialized) return true;
            var gm = GameManager.Instance;
            if (gm == null) return false;
            return gm.TurnOrder != MPMatchState.MiIndiceLocal;
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