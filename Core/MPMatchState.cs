using UnityEngine;

namespace CivilizameMP.Core
{
    public static class MPMatchState
    {
        public static int MiIndiceLocal = -1;
        public static bool IsInitialized => MiIndiceLocal >= 0;
        
        public static void SetLocalIndex(int index)
        {
            MiIndiceLocal = index;
            CivilizameMPPlugin.Log.LogInfo($"[MPMatchState] Indice local asignado: {index}");
        }
        
        public static bool IsLocalTurn(GameManager gm)
        {
            if (gm == null || gm.TurnOrder < 0) return false;
            if (MiIndiceLocal < 0) return false;
            bool result = gm.TurnOrder == MiIndiceLocal;
            if (result)
                CivilizameMPPlugin.Log.LogInfo($"[MPMatchState] IsLocalTurn=TRUE (TurnOrder={gm.TurnOrder}, MiIndice={MiIndiceLocal})");
            return result;
        }
        
        public static bool IsAITurn(GameManager gm)
        {
            if (gm == null || gm.TurnOrder < 0) return false;
            if (gm.jugadores == null || gm.TurnOrder >= gm.jugadores.Length) return false;
            var jug = gm.jugadores[gm.TurnOrder];
            bool result = jug != null && !jug.RealPlayer;
            if (result)
                CivilizameMPPlugin.Log.LogInfo($"[MPMatchState] IsAITurn=TRUE (TurnOrder={gm.TurnOrder})");
            return result;
        }
        
        public static bool IsRemoteHumanTurn(GameManager gm)
        {
            if (gm == null || gm.TurnOrder < 0) return false;
            if (gm.jugadores == null || gm.TurnOrder >= gm.jugadores.Length) return false;
            if (MiIndiceLocal < 0) return false;
            var jug = gm.jugadores[gm.TurnOrder];
            bool result = jug != null && jug.RealPlayer && gm.TurnOrder != MiIndiceLocal;
            if (result)
                CivilizameMPPlugin.Log.LogInfo($"[MPMatchState] IsRemoteHumanTurn=TRUE (TurnOrder={gm.TurnOrder}, MiIndice={MiIndiceLocal})");
            return result;
        }
        
        public static void Reset()
        {
            MiIndiceLocal = -1;
        }
    }
}