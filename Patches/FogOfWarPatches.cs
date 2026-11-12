using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    /// <summary>
    /// Asegura el POV durante las restauraciones de estado en multijugador.
    ///
    /// FogOfWarManager.ChangeFOW(i) activa la niebla VISIBLE del jugador i.
    /// Vanilla la llama con TurnOrder (el slot que está ARRANCANDO o el del
    /// estado restaurado). En MP, restaurar un estado cuyo próximo turno
    /// pertenece a OTRO jugador haría que esta PC mirara temporalmente con la
    /// niebla del vecino (fuga de información cuando el host queda bloqueado
    /// esperando el estado de un cliente).
    ///
    /// El prefix fija el índice a NUESTRO slot local: cada máquina ve siempre
    /// su propia niebla aunque el estado cargado sea el de otra persona
    /// (los turnos IA no tocan el fog porque el guard vanilla ya lo evita).
    /// </summary>
    [HarmonyPatch(typeof(FogOfWarManager), "ChangeFOW")]
    public static class FogOfWarPatches
    {
        [HarmonyPrefix]
        public static void ChangeFOWPrefix(ref int i)
        {
            if (!StateHandoff.IsInGame() || !MPMatchState.IsInitialized) return;
            if (i == MPMatchState.MiIndiceLocal) return;
            CivilizameMPPlugin.Log.LogInfo($"[FOW] ChangeFOW({i}) → fijo POV local ({MPMatchState.MiIndiceLocal})");
            i = MPMatchState.MiIndiceLocal;
        }

        /// <summary>
        /// Vanilla resuelve la capa de niebla con GameManager.TurnOrder:
        /// Check(TurnOrder, ...). Con el TurnOrder FIJADO al slot local eso
        /// destruye la niebla de la capa EQUIVOCADA (p. ej. el cliente aplica
        /// la exploración del host y borra tile de SU propia capa → "FOW
        /// compartido"). La capa correcta es la del DUEÑO de la Visión.
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "CheckFogOfWar")]
        [HarmonyPrefix]
        public static bool CheckFogOfWarPrefix(GameManager __instance, Jugador player, bool loading)
        {
            if (!StateHandoff.IsInGame() || !MPMatchState.IsInitialized) return true;
            var jugs = __instance.jugadores;
            if (jugs == null) return true;
            for (int i = 0; i < jugs.Length; i++)
            {
                if (jugs[i] == player)
                {
                    // La capa de niebla es la del DUEÑO de la visión, no el
                    // TurnOrder local (que está fijado y no cambia nunca).
                    __instance.FOW.Check(i, player.Visión, loading);
                    return false;
                }
            }
            return false; // jugador no encontrado: no tocar la capa local
        }
    }
}
