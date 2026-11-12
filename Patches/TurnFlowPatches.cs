using System.Reflection;
using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    /// <summary>
    /// Parches que cuelgan el protocolo de turnos ONLINE (host = servidor) al
    /// bucle vanilla del juego:
    ///  - GameManager.NextTurn: gate de avance. En partida se BLOQUEA el
    ///    NextTurn vanilla (que es lo que cambia de jugador/POV/UI); el ciclo
    ///    lo lleva la máquina de estados del TurnController y cada PC se queda
    ///    con SU jugador durante toda la partida.
    ///  - GameManager.MundoCargado: partida nueva (host: primer turno propio
    ///    directo, sin cadena vanilla de slots) y restauración del estado
    ///    recibido en dos modos: ESPEJO (cliente: fija SU slot, sin StartTurn;
    ///    el resto llega por eventos) y CLÁSICO (host tras migración).
    ///  - GameManager.SkipButton: solo el jugador local puede saltar turno.
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
            //  - i == 0: la llamada recursiva interna avanza con i+1.
            //  - cargado: ruta vanilla de carga (la interceptamos en el parche
            //    de MundoCargado, que decide cómo reanudar).
            if (i != 0 || cargado) return true;

            // El gate central decide: avanzar vanilla, correr el turno que
            // toque en su PC, o bloquear (sin cambiar de jugador NUNCA).
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
        /// Dos caminos, según Cargado:
        ///
        ///  - PARTIDA NUEVA (Cargado=false, host): setup vanilla (FOW, render,
        ///    cultura) y primer turno PROPIO directo. NO la cadena vanilla de
        ///    slots: esa correría StartTurn de otros jugadores en esta PC y
        ///    cambiaría de dueño (lo que el online no debe hacer).
        ///
        ///  - RESTORE de un estado recibido (Cargado=true, ruta vanilla de
        ///    GuardadoSeguridad):
        ///      · MODO ESPEJO (cliente, estado inicial o resync): TurnOrder =
        ///        SU slot y NINGÚN StartTurn: la copia local queda lista para
        ///        replicar eventos; su turno llegará por EV_SLOT.
        ///      · RESTORE CLÁSICO (host tras migración): TurnOrder = label - 1
        ///        y NextTurn dentro de InsideRestore, de modo que el StartTurn
        ///        del slot etiquetado corra aquí y el ciclo siga con el gate
        ///        del nuevo host.
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "MundoCargado")]
        [HarmonyPrefix]
        public static bool MundoCargadoPrefix(GameManager __instance, bool Cargado = false)
        {
            var state = MPStateManager.Instance;
            var pm = PhotonManager.Instance;

            if (!StateHandoff.IsInGame())
                return true; // fuera del multijugador: vanilla

            if (!Cargado)
            {
                // ---------------- PARTIDA NUEVA (host) -----------------------
                // Es la partida que el host crea con los menús vanilla. NO
                // exigimos hm.MatchStarted: vanilla llama a MundoCargado justo
                // tras generar el mundo, ANTES de que el host difunda la
                // config de slots (MatchStarted aún false). Si no se intercepta
                // aquí, vanilla arranca el turno del slot 0 al instante y el
                // boot nuestro lo repite después (doble StartTurn).
                if (state == null || !state.IsHost || pm == null || !pm.IsInRoom)
                    return true; // no es el host de una sala MP: vanilla

                CivilizameMPPlugin.Log.LogInfo("[TurnFlow] Partida nueva (host): setup y arranca por barrera de carga");

                __instance.FOW.Inicialize(__instance.jugadores.Length);
                Tablero.Instance.Render(false);
                Tablero.Instance.ResetIcons();

                typeof(GameManager)
                    .GetField("playing", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(__instance, true);

                __instance.getCultMan().init(__instance.jugadores.Length, false);

                if (MPMatchState.IsInitialized)
                {
                    __instance.TurnOrder = MPMatchState.MiIndiceLocal;
                    __instance.FOW.ChangeFOW(__instance.TurnOrder);
                    TurnController.BootHostGame(__instance);
                }
                else
                {
                    // La config de slots llega ~2s después (asignación por
                    // llegada). NO arrancar la cadena vanilla: dejar TurnOrder
                    // en -1 y que el arranque por respaldo del Tick lleve el
                    // boot (despacho inicial + barrera de carga) en cuanto
                    // llegue la config. El input queda bloqueado mientras
                    // (GameControlPatches: sin config = bloqueado).
                    __instance.TurnOrder = -1;
                    CivilizameMPPlugin.Log.LogWarning("[TurnFlow] Boot sin config de slots aún; el arranque por respaldo lo llevará");
                }

                TurnController.OnTurnAdvanced(__instance, false);
                return false;
            }

            // ---------------- RESTORE de un estado recibido (Cargado=true) ----
            // En el cliente MirrorLoad lo fija ReceiveOwnTurn; en el restore
            // clásico (migración) PendingTurnOrderAfterLoad >= 0. NO depender
            // de hm.MatchStarted: en el cliente es false y haría correr la
            // ruta vanilla (StartTurn del slot etiquetado en ESTA PC).
            if (!StateHandoff.MirrorLoad && StateHandoff.PendingTurnOrderAfterLoad < 0)
                return true;

            CivilizameMPPlugin.Log.LogInfo($"[TurnFlow] Restaurando estado (MirrorLoad={StateHandoff.MirrorLoad}, PendingTurnOrderAfterLoad={StateHandoff.PendingTurnOrderAfterLoad}, TurnOrder tras cargar={GameManager.Instance?.TurnOrder})");

            __instance.FOW.Inicialize(__instance.jugadores.Length);
            Tablero.Instance.Render(false);
            Tablero.Instance.ResetIcons();

            typeof(GameManager)
                .GetField("playing", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(__instance, true);

            __instance.getCultMan().init(__instance.jugadores.Length, true);

            if (StateHandoff.MirrorLoad)
            {
                // ------------------ MODO ESPEJO (cliente) --------------------
                // El modo espejo se decide por MirrorLoad (fijado por
                // ReceiveOwnTurn), NO por la etiqueta: el despacho inicial
                // puede llevar label -1 (boot con TurnOrder aún sin fijar) y
                // eso no convierte la carga en un restore clásico.
                int label = StateHandoff.PendingTurnOrderAfterLoad;
                StateHandoff.PendingTurnOrderAfterLoad = -1;
                StateHandoff.MirrorLoad = false;

                // SIN CAMBIO DE JUGADOR: esta PC se queda con SU slot. Su
                // turno llegará por EV_SLOT (el StartTurn propio corre allí).
                if (MPMatchState.IsInitialized && MPMatchState.MiIndiceLocal >= 0
                    && MPMatchState.MiIndiceLocal < __instance.jugadores.Length)
                {
                    __instance.TurnOrder = MPMatchState.MiIndiceLocal;
                    CivilizameMPPlugin.Log.LogInfo(
                        $"[TurnFlow] Carga en espejo: TurnOrder fijado a MI slot {MPMatchState.MiIndiceLocal} (estado del host etiquetado como {label}); SIN StartTurn");
                }
                else
                {
                    __instance.TurnOrder = label;
                    CivilizameMPPlugin.Log.LogInfo($"[TurnFlow] Carga en espejo sin config: TurnOrder = etiqueta {label}");
                }
                __instance.FOW.ChangeFOW(__instance.TurnOrder);

                // RECONSTRUCCIÓN DE NIEBLA PROPIA: FOW.Inicialize re-creó TODAS
                // las casillas de niebla y, al NO correr StartTurn (espejo),
                // nadie destruía las que ya estaban descubiertas → tras cada
                // carga el mapa propio reaparecía oscurecido y solo se aclaraba
                // donde las unidades volvieran a explorar (por eso solo se veían
                // unidades cerca de ciudades/edificios). Vanilla lo consigue de
                // rebote porque el restore SIEMPRE corre el StartTurn del slot
                // etiquetado. Aquí despejamos MI capa con la Visión restaurada
                // (loading=true: sin notificaciones de "conocer jugadores").
                if (MPMatchState.IsInitialized && MPMatchState.MiIndiceLocal >= 0
                    && MPMatchState.MiIndiceLocal < __instance.jugadores.Length)
                {
                    var miJug = __instance.jugadores[MPMatchState.MiIndiceLocal];
                    if (miJug != null && miJug.Visión != null)
                        __instance.FOW.Check(MPMatchState.MiIndiceLocal, miJug.Visión, loading: true);
                }

                // La cola de eventos puede empezar a reproducirse (el mapa ya
                // está); el filtro de seq descarta lo anterior al snapshot.
                LiveSync.LoadPending = false;

                // Modo espectador hasta que un EV_SLOT arme NUESTRO turno.
                LiveSync.Spectating = true;
                // Cartel vanilla de espera: aún no es nuestro turno (o la
                // partida está arrancando); se oculta al llegar nuestro slot.
                TurnController.ShowVanillaWaiting(true);
                CivilizameMPPlugin.Log.LogInfo("[TurnFlow] Modo ESPECTADOR en MI jugador: el ciclo llega por eventos en vivo");

                // Sincroniza la UI de espera según el turno tras la carga.
                TurnController.OnTurnAdvanced(__instance, false);
                return false;
            }

            // ------------------ RESTORE CLÁSICO (migración de host) ---------
            // TurnOrder restaurado: viene como label - 1 (>= 0), de modo que el
            // NextTurn de abajo retome el ciclo exactamente como quedaría tras
            // terminar el turno anterior (el turno a ARRANCAR es 'label').
            if (StateHandoff.PendingTurnOrderAfterLoad >= 0)
            {
                __instance.TurnOrder = StateHandoff.PendingTurnOrderAfterLoad - 1;
                CivilizameMPPlugin.Log.LogInfo($"[TurnFlow] TurnOrder restaurado a {__instance.TurnOrder} (label - 1)");
                StateHandoff.PendingTurnOrderAfterLoad = -1;
            }
            StateHandoff.InsideRestore = true;
            // La cola de eventos se libera al terminar el restore.
            LiveSync.LoadPending = false;
            LiveSync.Spectating = true;
            int landed;
            try
            {
                // Ciclo normal de turnos desde el estado restaurado. Con
                // cargado=false el StartTurn aplica ingresos y resetea el
                // movimiento (turno fresco), como haría la partida original.
                __instance.NextTurn(false, 0, false);
                landed = __instance.TurnOrder;
            }
            finally
            {
                StateHandoff.InsideRestore = false;
            }

            // La máquina de estados del NUEVO host adopta el slot que arrancó.
            TurnController.SyncPhaseAfterRestore(__instance, landed);

            // Sincroniza la UI de espera según el turno tras el restore.
            TurnController.OnTurnAdvanced(__instance, false);

            return false;
        }

        // ---------------- GameManager.SkipButton ----------------

        /// <summary>
        /// El botón de saltar turno solo actúa cuando es el turno del jugador
        /// local y no está en modo espectador. El cierre del turno lo
        /// intercepta el NextTurn (así SkipTurn da tiempo a curar unidades y
        /// dejar el turno "limpio").
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "SkipButton")]
        [HarmonyPrefix]
        public static bool SkipButtonPrefix(GameManager __instance)
        {
            if (!StateHandoff.IsInGame()) return true;
            if (!MPMatchState.IsInitialized) return false;
            if (LiveSync.Spectating) return false;

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
