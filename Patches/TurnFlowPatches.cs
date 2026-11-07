using System.Reflection;
using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    /// <summary>
    /// Parches que cuelgan el protocólo de turnos al bucle vanilla del juego:
    ///  - GameManager.NextTurn: captura/envío de estados en cada cambio de turno.
    ///  - GameManager.MundoCargado: al restaurar un estado recibido, retomar el
    ///    turno como si fuera fresco (ingreso aplicado una vez, movimiento reseteado).
    ///  - GameManager.SkipButton: solo el jugador al que le toca puede saltar turno.
    /// </summary>
    [HarmonyPatch]
    public static class TurnFlowPatches
    {
        // ---------------- GameManager.NextTurn ----------------

        [HarmonyPatch(typeof(GameManager), "NextTurn")]
        [HarmonyPrefix]
        public static bool NextTurnPrefix(GameManager __instance, bool aux, int i, bool cargado)
        {
            // Solo llamadas "reales" de avance de turno:
            //  - i == 0: la llamada recursiva de NextTurn avanza con i+1.
            //  - cargado == false: la llamada de resume de guardado usa cargado = true.
            //  - InsideRestore: la restauración ya hizo su captura propia.
            if (i != 0 || cargado || StateHandoff.InsideRestore) return true;

            // Puede devolver FALSE para BLOQUEAR el avance:
            //  - El host pide antes enviar el estado al cliente que le toca.
            //  - El cliente congelado tras enviar su turno NO simula nada en local.
            return TurnController.OnTurnAdvancing(__instance);
        }

        [HarmonyPatch(typeof(GameManager), "NextTurn")]
        [HarmonyPostfix]
        public static void NextTurnPostfix(GameManager __instance, bool cargado)
        {
            TurnController.OnTurnAdvanced(__instance, cargado);
        }

        // ---------------- GameManager.MundoCargado ----------------

        /// <summary>
        /// Al cargar un guardado en multijugador, retomar el turno como si fuera
        /// fresco: NextTurn(false, 0, false) en vez de (false, 0, true). Así el
        /// ingreso del turno se aplica una sola vez y las unidades resetean el
        /// movimiento. Fuera de MP el comportamiento vanilla se mantiene intacto.
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "MundoCargado")]
        [HarmonyPrefix]
        public static bool MundoCargadoPrefix(GameManager __instance, bool Cargado = false)
        {
            if (!StateHandoff.IsInGame() || !Cargado) return true;

            CivilizameMPPlugin.Log.LogInfo($"[TurnFlow] Retomando turno {GameManager.Instance?.TurnOrder} desde guardado (como turno fresco)");

            __instance.FOW.Inicialize(__instance.jugadores.Length);
            Tablero.Instance.Render(false);
            Tablero.Instance.ResetIcons();

            typeof(GameManager)
                .GetField("playing", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(__instance, true);

            __instance.getCultMan().init(__instance.jugadores.Length, true);

            StateHandoff.InsideRestore = true;
            try
            {
                // El turno que va a arrancar es el del estado restaurado; si pertenece
                // a un cliente humano, el host le envía aquí el estado (antes de que
                // el juego aplique el turno en su copia local).
                TurnController.OnPreResumeAdvance(__instance);

                __instance.NextTurn(false, 0, false);
            }
            finally
            {
                StateHandoff.InsideRestore = false;
            }
            return false;
        }

        // ---------------- GameManager.SkipButton ----------------

        /// <summary>
        /// El botón de saltar turno solo actúa cuando es el turno del jugador local.
        /// El envío del estado no se hace aquí: lo intercepta el NextTurn anterior
        /// (dando tiempo a que SkipTurn cure unidades y deje el turno "limpio").
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "SkipButton")]
        [HarmonyPrefix]
        public static bool SkipButtonPrefix(GameManager __instance)
        {
            if (!StateHandoff.IsInGame()) return true;
            if (!MPMatchState.IsInitialized) return false;

            if (__instance.TurnOrder != MPMatchState.MiIndiceLocal)
            {
                CivilizameMPPlugin.Log.LogInfo(
                    $"[SkipButton] Bloqueado (TurnOrder={__instance.TurnOrder}, mi indice={MPMatchState.MiIndiceLocal})");
                return false;
            }
            return true;
        }
    }
}
