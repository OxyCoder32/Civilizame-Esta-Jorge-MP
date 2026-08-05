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
    }
}