using System;

namespace CivilizameMP.Core
{
    public static class MPMatchState
    {
        public static int MiIndiceLocal = -1;
        public static bool IsInitialized => MiIndiceLocal >= 0;
        public static bool IsMyTurn => IsInitialized && GameManager.Instance != null && GameManager.Instance.TurnOrder == MiIndiceLocal;
        
        public static void Reset()
        {
            MiIndiceLocal = -1;
        }
        
        public static void SetLocalIndex(int index)
        {
            if (index < 0) 
            {
                CivilizameMPPlugin.Log.LogWarning($"[MPMatchState] Intentando asignar índice inválido: {index}");
                return;
            }
            MiIndiceLocal = index;
            CivilizameMPPlugin.Log.LogInfo($"[MPMatchState] Índice local asignado: {MiIndiceLocal}");
        }

        public static bool IsRemoteHumanTurn(GameManager gm)
        {
            if (gm == null || gm.jugadores == null) return false;
            if (gm.TurnOrder < 0 || gm.TurnOrder >= gm.jugadores.Length) return false;
            if (gm.TurnOrder == MiIndiceLocal) return false;

            var jug = gm.jugadores[gm.TurnOrder];
            if (jug == null) return false;

            return jug.RealPlayer;
        }

        public static bool IsAITurn(GameManager gm)
        {
            if (gm == null || gm.jugadores == null) return false;
            if (gm.TurnOrder < 0 || gm.TurnOrder >= gm.jugadores.Length) return false;

            var jug = gm.jugadores[gm.TurnOrder];
            if (jug == null) return false;

            return !jug.RealPlayer;
        }

        public static bool IsLocalTurn(GameManager gm)
        {
            if (gm == null) return false;
            if (!IsInitialized) return false;
            return gm.TurnOrder == MiIndiceLocal;
        }
    }
}