using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using CivilizameMP.Core;
using CivilizameMP.UI;

namespace CivilizameMP.Network
{
    /// <summary>
    /// Flujo de turnos ONLINE estilo Civilization (host = servidor):
    ///
    ///  - UN SOLO estado completo: al crear la partida (y en el resync de una
    ///    reconexión). Después NO hay recargas NUNCA: la partida se replica
    ///    con eventos en vivo (LiveSync) sobre la copia de cada PC.
    ///  - Turnos SECUENCIALES vanilla: el host recorre el ciclo de slots con
    ///    su máquina de estados y emite EV_SLOT por cada turno que empieza.
    ///      · Slot propio → StartTurn vanilla en su PC (es un jugador normal).
    ///      · Slot de IA → StartTurn vanilla en el host (la IA corre allí y se
    ///        ve en directo en todas las PCs).
    ///      · Slot humano remoto → el dueño corre SU StartTurn en SU PC y
    ///        narra; el host espera su EV_TURN_DONE.
    ///  - SIN CAMBIO DE JUGADOR LOCAL: vanilla, al cerrar un turno, avanza
    ///    TurnOrder y mueve la UI/POV al jugador siguiente (NextTurn →
    ///    ChangeUIPlayer → FOW). Aquí eso NO pasa: el NextTurn de cada PC se
    ///    BLOQUEA y cada máquina mantiene TurnOrder = su propio slot durante
    ///    TODA la partida (su UI, su POV y sus recursos nunca cambian de dueño;
    ///    solo se regulan por LiveSync.Spectating / AwaitingClientTurn).
    ///  - El host solo pone TurnOrder en el slot de la IA MIENTRAS corre su
    ///    StartTurn (la lógica vanilla de IA lee TurnOrder) y lo devuelve a su
    ///    slot al terminar.
    /// </summary>
    public static class TurnController
    {
        private enum Phase
        {
            /// <summary>Esperando el arranque (MundoCargado de partida nueva).</summary>
            Idle,
            /// <summary>Estado inicial enviado: esperando el ping de carga de
            /// TODOS los clientes conectados antes de arrancar el ciclo.</summary>
            WaitLoad,
            /// <summary>El host está jugando su propio turno.</summary>
            HostTurn,
            /// <summary>Esperando el EV_TURN_DONE de un humano remoto.</summary>
            AwaitRemoteHuman,
            /// <summary>Un slot de IA está corriendo su StartTurn en el host.</summary>
            AwaitAI
        }

        /// <summary>Colchón antes del despacho inicial (el StartTurn propio del
        /// host ya corrió; los clientes cargan mientras el host juega).</summary>
        private const float InitialDispatchDelay = 20f;
        /// <summary>Segundos que puede estar ausente de la sala el jugador
        /// esperado antes de saltar su turno (el ciclo no se congela).</summary>
        private const float AwaitGoneGrace = 12f;
        /// <summary>Backstop: si un turno de IA nunca termina (crash del
        /// coroutine), el ciclo sigue solo tras este tiempo.</summary>
        private const float AITimeout = 90f;
        /// <summary>Backstop: si algún cliente no confirma la carga, el ciclo
        /// arranca igual (el cliente resincronizará al volver).</summary>
        private const float LoadWaitTimeout = 120f;

        /// <summary>HOST: ventana de acción de OTRO (humano remoto o IA).
        /// GameControlPatches bloquea el input del host mientras tanto.</summary>
        public static bool AwaitingClientTurn;

        /// <summary>HOST: true mientras se espera la confirmación de carga de
        /// los clientes tras el despacho inicial (el ciclo aún no arrancó;
        /// el input local queda bloqueado para que nadie mueva nada antes).</summary>
        public static bool IsWaitingClients => _phase == Phase.WaitLoad;

        // ------------------------------------------------------------------
        //  Overlay vanilla de "esperando" (el reloj de arena que vanilla
        //  muestra mientras corre el turno de la IA): UIPlayerManager.
        //  ManageWaitingForPlayers(b) activa el cartel cuando b == false.
        // ------------------------------------------------------------------

        /// <summary>Muestra/oculta el cartel vanilla de espera. Se usa cuando
        /// esta PC espera el turno de OTRO (humano remoto o IA): el jugador ve
        /// claramente que toca esperar, como en el juego original.</summary>
        public static void ShowVanillaWaiting(bool show)
        {
            var gm = GameManager.Instance;
            if (gm == null) return;
            try
            {
                if (_uiPlayerField == null)
                    _uiPlayerField = AccessTools.Field(typeof(GameManager), "UIplayer");
                var ui = _uiPlayerField?.GetValue(gm) as UIPlayerManager;
                if (ui != null) ui.ManageWaitingForPlayers(!show);
                // Regla de UI: mientras el cartel vanilla de espera está
                // visible, NO va nuestro HUD encima (texto superpuesto mal
                // visible). El cartel vanilla ya dice "esperando a jugadores".
                if (show) MPWaitingPanel.Instance?.HideHUD();
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[Turn] Overlay de espera: {ex.Message}");
            }
        }

        private static System.Reflection.FieldInfo _uiPlayerField;

        private static Phase _phase = Phase.Idle;
        private static float _phaseSince;
        private static int _awaitSlot = -1;   // slot del turno en espera (humano o IA)
        private static int _awaitActor = -1;  // actor del humano esperado (-1 = IA)
        private static float _awaitGoneSince;
        private static float _resumeAt;
        private static bool _hostTurnStarted;
        private static bool _initialDispatchDone;
        private static readonly Dictionary<int, int> _lastDoneSeq = new Dictionary<int, int>();
        private static readonly HashSet<int> _dispatchedTo = new HashSet<int>();
        private static readonly HashSet<int> _loadReadyAcks = new HashSet<int>();
        private static float _resetAt;
        private static float _loadWaitSince;

        // CLIENTE: EV_SLOT marcó nuestro slot → Tick corre el StartTurn propio
        // DIRECTAMENTE (sin NextTurn: no se cambia de jugador).
        private static bool _myTurnArmed;
        private static bool _myTurnPlaying;

        public static void Reset()
        {
            AwaitingClientTurn = false;
            _phase = Phase.Idle;
            _phaseSince = 0f;
            _awaitSlot = -1;
            _awaitActor = -1;
            _awaitGoneSince = 0f;
            _resumeAt = 0f;
            _hostTurnStarted = false;
            _initialDispatchDone = false;
            _lastDoneSeq.Clear();
            _dispatchedTo.Clear();
            _loadReadyAcks.Clear();
            _loadWaitSince = 0f;
            _resetAt = Time.time;
            _myTurnArmed = false;
            _myTurnPlaying = false;
            StateHandoff.PendingSaveLabel = -1;
            StateHandoff.PendingTurnOrderAfterLoad = -1;
            StateHandoff.InsideRestore = false;
            StateHandoff.VanillaAutosaveAllowed = false;
            StateHandoff.MirrorLoad = false;
            LiveSync.Reset();
        }

        // ------------------------------------------------------------------
        //  Tick (cada frame desde LiveSyncRunner)
        // ------------------------------------------------------------------

        public static void Tick()
        {
            // Reintentos del cliente (TURN_DONE + pedido de resync).
            ClientManager.Instance?.Tick();

            var state = MPStateManager.Instance;
            if (state == null) return;

            if (!state.IsHost)
            {
                TickClientArmed(state);
                return;
            }

            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null || !StateHandoff.IsInGame()) return;
            var hm = HostManager.Instance;
            if (hm == null || !hm.MatchStarted) return;

            // Arranque por respaldo: si la partida empezó sin pasar por el
            // MundoCargado parcheado (p. ej. config llegó tarde), el arranque
            // normal (despacho + barrera de carga) lo lleva igual.
            if (_phase == Phase.Idle)
                TryHostFallbackBoot(gm);

            // BARRERA DE CARGA: el estado inicial ya salió; el ciclo NO
            // arranca hasta que TODOS los clientes conectados hayan cargado
            // la partida y confirmado (ping EV_LOAD_READY).
            if (_phase == Phase.WaitLoad)
            {
                TickWaitLoad(gm);
                return;
            }

            if (_resumeAt > 0f && Time.time >= _resumeAt)
            {
                _resumeAt = 0f;
                if (_phase == Phase.AwaitRemoteHuman)
                {
                    CivilizameMPPlugin.Log.LogInfo(
                        $"[Turn] HOST: TURN_DONE del slot {_awaitSlot} → el ciclo continúa");
                    AdvanceCycleFrom(gm, _awaitSlot);
                }
                return;
            }

            if (_phase == Phase.AwaitRemoteHuman)
                TrySkipAbsentAwaited(gm);
            else if (_phase == Phase.AwaitAI
                     && Time.unscaledTime - _phaseSince > AITimeout)
            {
                CivilizameMPPlugin.Log.LogWarning(
                    $"[Turn] HOST: el turno de IA del slot {_awaitSlot} excedió {AITimeout}s; se continúa el ciclo");
                EndAITurn(gm);
            }
        }

        /// <summary>Arranque por respaldo del host: si el MundoCargado
        /// parcheado no llegó a ejecutarse, hace el arranque normal aquí.</summary>
        private static void TryHostFallbackBoot(GameManager gm)
        {
            if (!MPMatchState.IsInitialized || MPMatchState.MiIndiceLocal < 0) return;
            if (_hostTurnStarted) return;
            if (SceneManager.GetActiveScene().name != MPConstants.GAME_SCENE) return;
            if (Tablero.Instance == null) return;
            if (Time.time - _resetAt < 1f) return; // dejar asentar la escena

            CivilizameMPPlugin.Log.LogInfo("[Turn] HOST: arranque por respaldo");
            BootHostGame(gm);
        }

        /// <summary>HOST: arranque de partida NUEVA (llamado desde el parche
        /// de MundoCargado(false)): fija el dueño local y su POV de FOW ANTES
        /// de despachar (la etiqueta del snapshot DEBE ser un slot válido,
        /// nunca -1, o el cliente lo interpreta mal), envía el estado inicial
        /// a los clientes y queda ESPERANDO su confirmación de carga. El
        /// primer turno (y todo el ciclo) arranca cuando todos hayan
        /// cargado la partida.</summary>
        internal static void BootHostGame(GameManager gm)
        {
            if (!MPMatchState.IsInitialized || MPMatchState.MiIndiceLocal < 0)
            {
                CivilizameMPPlugin.Log.LogWarning("[Turn] HOST: boot sin config de slots; el arranque por respaldo lo llevará");
                return;
            }

            // Fijar el dueño local ANTES del despacho: la etiqueta del
            // snapshot debe ser el slot propio (>= 0). Y activar YA el POV
            // del FOW: si la config llegó tarde, sin esto el host se queda
            // en niebla total hasta el primer StartTurn.
            int slot = MPMatchState.MiIndiceLocal;
            gm.TurnOrder = slot;
            gm.FOW.ChangeFOW(slot);

            _resetAt = Time.time;
            _phase = Phase.WaitLoad;
            _loadWaitSince = Time.unscaledTime;
            _loadReadyAcks.Clear();

            CivilizameMPPlugin.Log.LogInfo($"[Turn] HOST: partida nueva (slot {slot}) → enviando estado inicial y esperando la carga de los clientes");
            MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
            MPWaitingPanel.Instance?.SetStatus("SINCRONIZANDO PARTIDA",
                "Enviando la partida a los jugadores...\nEl primer turno arranca cuando todos hayan cargado.");

            DispatchInitialState(gm);
        }

        /// <summary>Espera de la barrera de carga: arranca el ciclo cuando
        /// todos los humanos conectados confirmaron, o tras el backstop.</summary>
        private static void TickWaitLoad(GameManager gm)
        {
            var st = MPStateManager.Instance;
            var pm = PhotonManager.Instance;
            if (st == null || pm == null || !pm.IsInRoom)
                return; // sin sala: nada que esperar (se reevalúa al reconectar)

            int waiting = 0;
            foreach (var s in st.PlayerSlots)
            {
                if (s == null || !s.IsHuman) continue;
                if (s.ActorNumber <= 0 || s.ActorNumber == st.LocalActorNumber) continue;
                if (!RoomHasActor(pm, s.ActorNumber)) continue; // ausente: no bloquea
                if (!_loadReadyAcks.Contains(s.ActorNumber)) waiting++;
            }

            if (waiting > 0 && Time.unscaledTime - _loadWaitSince < LoadWaitTimeout)
                return;

            if (waiting > 0)
                CivilizameMPPlugin.Log.LogWarning(
                    $"[Turn] HOST: {waiting} cliente(s) sin confirmar la carga tras {LoadWaitTimeout}s; arrancando igual");
            else
                CivilizameMPPlugin.Log.LogInfo("[Turn] HOST: todos los clientes confirmaron la carga → arranca el ciclo");

            StartFirstCycle(gm);
        }

        /// <summary>Primer ciclo tras la barrera: empieza en el slot 0 como
        /// la partida original (el StartTurn corre en la PC del dueño).</summary>
        private static void StartFirstCycle(GameManager gm)
        {
            _phase = Phase.Idle;
            MPWaitingPanel.Instance?.Hide();
            MPPanelManager.Instance?.HideCurrentPanel();
            int landing = StateHandoff.NextPlayableSlot(-1);
            if (landing < 0)
            {
                CivilizameMPPlugin.Log.LogWarning("[Turn] HOST: sin slot inicial; ciclo detenido");
                return;
            }
            RunSlot(gm, landing);
        }

        /// <summary>HOST: ping de carga recibido de un cliente (EV_LOAD_READY).
        /// Se acumula en la barrera; el Tick arranca el ciclo cuando completen
        /// todos los humanos conectados.</summary>
        public static void HandleLoadReady(int actor, int slot)
        {
            var state = MPStateManager.Instance;
            if (state == null || !state.IsHost || !StateHandoff.IsInGame()) return;
            if (_loadReadyAcks.Add(actor))
                CivilizameMPPlugin.Log.LogInfo(
                    $"[Turn] HOST: carga confirmada por actor {actor} (slot {slot}); acks {_loadReadyAcks.Count}");
        }

        // ------------------------------------------------------------------
        //  Ciclo del host: avanze de slots
        // ------------------------------------------------------------------

        /// <summary>Avanza el ciclo al slot siguiente al que TERMINÓ.</summary>
        private static void AdvanceCycleFrom(GameManager gm, int finishedSlot)
        {
            _phase = Phase.Idle;
            AwaitingClientTurn = false;

            // Primer ciclo: asegurar que los clientes ya tienen el estado.
            if (!_initialDispatchDone)
                DispatchInitialState(gm);

            int landing = StateHandoff.NextPlayableSlot(finishedSlot);
            if (landing < 0)
            {
                CivilizameMPPlugin.Log.LogWarning("[Turn] HOST: sin slot siguiente; ciclo detenido");
                return;
            }
            RunSlot(gm, landing);
        }

        /// <summary>Ejecuta el turno del slot dado, donde toque:</summary>
        private static void RunSlot(GameManager gm, int slot)
        {
            if (slot < 0 || gm.jugadores == null || slot >= gm.jugadores.Length) return;
            var jug = gm.jugadores[slot];

            // ---- Slot propio del host: juega como un jugador más ------------
            if (slot == MPMatchState.MiIndiceLocal)
            {
                RunOwnStartTurn(gm);
                return;
            }

            LiveSync.EmitSlot(slot);

            // ---- Slot de IA: StartTurn vanilla aquí; sus efectos se narran --
            // en vivo y su coroutine final llama a NextTurn (gate) al acabar.
            if (jug != null && !jug.RealPlayer)
            {
                gm.TurnOrder = slot; // la lógica vanilla de IA lee TurnOrder
                _phase = Phase.AwaitAI;
                _phaseSince = Time.unscaledTime;
                _awaitSlot = slot;
                _awaitActor = -1;
                AwaitingClientTurn = true;
                CivilizameMPPlugin.Log.LogInfo($"[Turn] HOST: turno {slot} (IA) corriendo en el servidor");
                // El cartel vanilla de espera lo activa el propio StartTurn
                // (RealPlayer=false); lo forzamos por si acaso.
                ShowVanillaWaiting(true);
                MPWaitingPanel.Instance?.HideHUD();
                try { jug.StartTurn(false, false); }
                catch (Exception ex)
                {
                    CivilizameMPPlugin.Log.LogError($"[Turn] Error en el StartTurn de IA (slot {slot}): {ex}");
                }
                TurnSlotHousekeeping(gm);
                return;
            }

            // ---- Slot humano remoto: SU PC corre el turno; aquí solo esperar -
            _awaitSlot = slot;
            _awaitActor = ActorForSlot(slot);
            _awaitGoneSince = 0f;
            _phase = Phase.AwaitRemoteHuman;
            _phaseSince = Time.unscaledTime;
            AwaitingClientTurn = true;
            string nombre = jug != null && !string.IsNullOrEmpty(jug.Nombre)
                ? jug.Nombre : ("Jugador " + (slot + 1));
            CivilizameMPPlugin.Log.LogInfo(
                $"[Turn] HOST: turno {slot} ({nombre}, actor {_awaitActor}) → esperando su TURN_DONE");
            ShowVanillaWaiting(true); // SOLO el cartel vanilla: sin HUD encima
        }

        /// <summary>StartTurn PROPIO del host, directo y sin NextTurn (la UI y
        /// el POV siguen en el jugador local; nunca se cambia de dueño).</summary>
        private static void RunOwnStartTurn(GameManager gm)
        {
            int slot = MPMatchState.MiIndiceLocal;
            if (slot < 0 || slot >= gm.jugadores.Length || gm.jugadores[slot] == null) return;

            gm.TurnOrder = slot; // fijar el dueño local (por si acaso)
            try { gm.FOW.ChangeFOW(slot); } catch { } // POV local garantizada
            _phase = Phase.HostTurn;
            _phaseSince = Time.unscaledTime;
            AwaitingClientTurn = false;
            _hostTurnStarted = true;

            ShowVanillaWaiting(false); // es TU turno: fuera el cartel de espera
            LiveSync.EmitSlot(slot);
            CivilizameMPPlugin.Log.LogInfo($"[Turn] HOST: mi turno (slot {slot}) arranca");
            MPWaitingPanel.Instance?.HideHUD();
            try { gm.jugadores[slot].StartTurn(false, false); }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Turn] Error en el StartTurn propio: {ex}");
            }
            TurnSlotHousekeeping(gm);
        }

        /// <summary>Fin del turno de IA: devolver TurnOrder y UI al dueño local
        /// y continuar el ciclo desde el slot de la IA que terminó.</summary>
        private static void EndAITurn(GameManager gm)
        {
            int aiSlot = _awaitSlot;
            _phase = Phase.Idle;
            ShowVanillaWaiting(false); // el StartTurn de la IA lo dejó activo
            if (MPMatchState.MiIndiceLocal >= 0)
            {
                gm.TurnOrder = MPMatchState.MiIndiceLocal;
                var own = gm.jugadores[MPMatchState.MiIndiceLocal];
                try { gm.ChangeUIPlayer(own); } catch { }
            }
            AwaitingClientTurn = false;
            CivilizameMPPlugin.Log.LogInfo($"[Turn] HOST: turno de IA del slot {aiSlot} terminado");
            AdvanceCycleFrom(gm, aiSlot);
        }

        /// <summary>Housekeeping vanilla que NextTurn hace tras cada StartTurn
        /// (lo replicamos porque NextTurn está bloqueado en partida).</summary>
        internal static void TurnSlotHousekeeping(GameManager gm)
        {
            try
            {
                gm.myRelationshipManager.resetProposals();
                gm.getCultMan().ManageCultureBar();
                // scienceManager y mGoldenAgeManager son privados en vanilla.
                var scienceMan = AccessTools.Field(typeof(GameManager), "scienceManager")
                    ?.GetValue(gm) as ScienceManager;
                scienceMan?.ManageScienceBar(gm.Jug());
                var goldenMan = AccessTools.Field(typeof(GameManager), "mGoldenAgeManager")
                    ?.GetValue(gm) as GoldenAgeManager;
                goldenMan?.ManageGoldenAgeBar(gm.Jug());
                ReligionManager.Instance.ManageReligionBar();
                ReligionManager.Instance.CheckIfSendVictoryMessage();
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[Turn] Housekeeping de turno: {ex.Message}");
            }
        }

        /// <summary>CLIENTE: consume el turno armado (EV_SLOT propio) corriendo
        /// el StartTurn PROPIO directamente: sin NextTurn, sin cambio de
        /// jugador. Se espera a que la cola de la carga esté vacía.</summary>
        private static void TickClientArmed(MPStateManager state)
        {
            if (!_myTurnArmed) return;
            if (LiveSync.LoadPending || LiveSync.PendingCount > 0) return;
            if (!StateHandoff.IsInGame() || !MPMatchState.IsInitialized) return;
            var gm = GameManager.Instance;
            if (gm == null || Tablero.Instance == null) return;
            int slot = MPMatchState.MiIndiceLocal;
            if (slot < 0 || gm.jugadores == null || slot >= gm.jugadores.Length
                || gm.jugadores[slot] == null) return;

            _myTurnArmed = false;
            _myTurnPlaying = true;
            LiveSync.Spectating = false;
            gm.TurnOrder = slot; // fijar el dueño local (nunca cambia a otro)
            CivilizameMPPlugin.Log.LogInfo($"[Turn] CLIENTE: mi turno (slot {slot}) arranca");
            ShowVanillaWaiting(false); // es TU turno: fuera el cartel de espera
            MPWaitingPanel.Instance?.HideHUD();
            try { gm.jugadores[slot].StartTurn(false, false); }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Turn] Error en el StartTurn del cliente: {ex}");
            }
            TurnSlotHousekeeping(gm);
        }

        /// <summary>Si el jugador esperado lleva fuera de la sala más del
        /// grace, se salta su turno (podrá resincronizarse al volver).</summary>
        private static void TrySkipAbsentAwaited(GameManager gm)
        {
            var pm = PhotonManager.Instance;
            if (pm == null || !pm.IsInRoom)
            {
                _awaitGoneSince = 0f; // sin sala (reconectando): no saltar
                return;
            }
            // Presente (con actor conocido): reiniciar el grace.
            if (_awaitActor > 0 && RoomHasActor(pm, _awaitActor))
            {
                _awaitGoneSince = 0f;
                return;
            }
            if (_awaitGoneSince <= 0f)
            {
                _awaitGoneSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _awaitGoneSince > AwaitGoneGrace)
            {
                CivilizameMPPlugin.Log.LogWarning(
                    $"[Turn] HOST: el actor {_awaitActor} (slot {_awaitSlot}) no está en la sala; se salta su turno");
                _phase = Phase.Idle;
                _awaitGoneSince = 0f;
                AdvanceCycleFrom(gm, _awaitSlot);
            }
        }

        private static bool RoomHasActor(PhotonManager pm, int actor)
        {
            if (actor <= 0) return false;
            foreach (var p in pm.GetPlayerList())
                if (p != null && p.ActorNumber == actor) return true;
            return false;
        }

        // ------------------------------------------------------------------
        //  GATE de NextTurn (prefix de GameManager.NextTurn)
        // ------------------------------------------------------------------

        public static bool OnTurnAdvancing(GameManager gm)
        {
            if (!StateHandoff.IsInGame() || gm == null || gm.jugadores == null || gm.jugadores.Length == 0)
                return true;

            // Restore en curso (aplicando un .jue): el avance DEBE pasar para
            // que el StartTurn del slot etiquetado corra (migración de host).
            if (StateHandoff.InsideRestore)
                return true;

            var state = MPStateManager.Instance;

            if (state.IsHost)
            {
                var hm = HostManager.Instance;
                if (hm == null || !hm.MatchStarted)
                    return true; // fuera de partida: vanilla libre

                switch (_phase)
                {
                    case Phase.AwaitAI:
                        // El NextTurn viene del AIEnd/AIUnCockBlocker: fin del
                        // turno de IA → devolver el control y continuar.
                        EndAITurn(gm);
                        return false;

                    case Phase.HostTurn:
                        if (gm.TurnOrder == MPMatchState.MiIndiceLocal)
                        {
                            CivilizameMPPlugin.Log.LogInfo("[Turn] HOST: mi turno terminó → ciclo continúa");
                            AdvanceCycleFrom(gm, MPMatchState.MiIndiceLocal);
                        }
                        return false; // sin cambio de jugador NUNCA

                    default:
                        // AwaitRemoteHuman/Idle: NextTurn ajenos bloqueados
                        // (el ciclo lo lleva la máquina de estados).
                        return false;
                }
            }

            if (state.IsClient)
            {
                if (!MPMatchState.IsInitialized)
                    return true; // pre-config: vanilla libre

                // SIN CAMBIO DE JUGADOR: NextTurn vanilla bloqueado siempre.
                // Si es el cierre de NUESTRO turno → TURN_DONE + espectador.
                if (_myTurnPlaying)
                {
                    _myTurnPlaying = false;
                    LiveSync.Spectating = true;
                    ClientManager.Instance?.SendTurnDone();
                    CivilizameMPPlugin.Log.LogInfo(
                        "[Turn] CLIENTE: mi turno terminó → TURN_DONE; sigo en MI jugador viendo en directo");
                }
                return false;
            }

            return true;
        }

        /// <summary>CLIENTE: un EV_SLOT marcó nuestro slot (desde el applier
        /// de LiveSync). Arma el turno: el Tick corre el StartTurn propio.</summary>
        public static void ArmMyTurn()
        {
            if (!StateHandoff.IsInGame()) return;
            var state = MPStateManager.Instance;
            if (state == null || state.IsHost) return;
            if (!MPMatchState.IsInitialized) return;
            if (_myTurnPlaying || _myTurnArmed) return; // ya activo

            _myTurnArmed = true;
            LiveSync.Spectating = false;
            ClientManager.Instance?.OnTurnArmed();
            CivilizameMPPlugin.Log.LogInfo("[Turn] CLIENTE: mi turno armado (llega por EV_SLOT)");
        }

        /// <summary>HOST: postfix de Jugador.StartTurn. Marca el arranque del
        /// primer turno propio (el despacho inicial espera a eso).</summary>
        public static void OnStartTurnRan(int slot)
        {
            var state = MPStateManager.Instance;
            if (state == null || !state.IsHost) return;
            if (slot == MPMatchState.MiIndiceLocal)
                _hostTurnStarted = true;
        }

        /// <summary>El host puede narrar (emisión libre) mientras juega su
        /// turno o mientras corre una IA. Durante el turno de un humano remoto
        /// el host es espectador: solo aplica y retransmite.</summary>
        public static bool HostMayNarrate()
        {
            return _phase == Phase.HostTurn || _phase == Phase.AwaitAI;
        }

        // ------------------------------------------------------------------
        //  HUD tras un cambio de turno que llegue por vanilla (restore)
        // ------------------------------------------------------------------

        public static void OnTurnAdvanced(GameManager gm, bool cargado)
        {
            if (!StateHandoff.IsInGame() || gm == null) return;
            if (MPMatchState.IsLocalTurn(gm))
            {
                MPWaitingPanel.Instance?.Hide();
                MPWaitingPanel.Instance?.HideHUD();
                MPPanelManager.Instance?.HideCurrentPanel();
            }
        }

        // ------------------------------------------------------------------
        //  TURN_DONE (aviso ligero de fin de turno de un humano remoto)
        // ------------------------------------------------------------------

        public static void HandleTurnDone(int actor, int slot, int seq)
        {
            var state = MPStateManager.Instance;
            if (state == null || !state.IsHost || !StateHandoff.IsInGame()) return;

            // Deduplicación: seq monótona por actor (reintentos repiten seq).
            int last;
            if (_lastDoneSeq.TryGetValue(actor, out last) && seq <= last)
            {
                CivilizameMPPlugin.Log.LogInfo(
                    $"[Turn] HOST: TURN_DONE duplicado del actor {actor} (seq {seq} <= {last}); ignorado");
                return;
            }
            _lastDoneSeq[actor] = seq;

            if (_phase != Phase.AwaitRemoteHuman || actor != _awaitActor)
            {
                CivilizameMPPlugin.Log.LogInfo(
                    $"[Turn] HOST: TURN_DONE del actor {actor} fuera de espera; ignorado");
                return;
            }

            // El ciclo se retoma por el Tick (fuera del Apply del evento).
            _resumeAt = Time.time + 0.15f;
            CivilizameMPPlugin.Log.LogInfo($"[Turn] HOST: jugador {slot + 1} terminó su turno");
        }

        // ------------------------------------------------------------------
        //  Resync (estado para reconexiones / despistes)
        // ------------------------------------------------------------------

        /// <summary>HOST: el cliente pide un estado fresco. Se le envía el
        /// snapshot ACTUAL (su PC lo carga fijando SU slot, sin arrancar nada)
        /// y, si estábamos esperando SU turno, se re-anuncia el EV_SLOT para
        /// que arme su turno al terminar de cargar.</summary>
        public static void HandleResyncRequest(int actor)
        {
            var state = MPStateManager.Instance;
            if (state == null || !state.IsHost || !StateHandoff.IsInGame()) return;
            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null) return;

            int slot = SlotOfActor(actor);
            if (slot < 0 || slot >= gm.jugadores.Length || gm.jugadores[slot] == null)
            {
                CivilizameMPPlugin.Log.LogWarning($"[Turn] HOST: resync del actor {actor} sin slot humano; ignorado");
                return;
            }
            if (StateHandoff.InsideRestore)
            {
                CivilizameMPPlugin.Log.LogWarning("[Turn] HOST: resync ignorado (restore en curso); el cliente reintentará");
                return;
            }

            SendStateToSlot(actor, "resync");

            // Si su turno está en espera, re-anunciarlo: al cargar, el EV_SLOT
            // (en cola durante la carga) le arma su turno.
            if (_phase == Phase.AwaitRemoteHuman && _awaitActor == actor)
                LiveSync.EmitSlot(_awaitSlot);
        }

        private static void DispatchInitialState(GameManager gm)
        {
            _initialDispatchDone = true;
            var state = MPStateManager.Instance;
            var pm = PhotonManager.Instance;
            if (pm == null || !pm.IsInRoom) return;

            foreach (var s in state.PlayerSlots)
            {
                if (s == null || s.ActorNumber <= 0 || s.ActorNumber == state.LocalActorNumber) continue;
                if (!RoomHasActor(pm, s.ActorNumber)) continue; // los ausentes resincronizan al volver
                _dispatchedTo.Add(s.ActorNumber);
                SendStateToSlot(s.ActorNumber, "inicial");
            }
        }

        /// <summary>Envía el snapshot actual a un actor concreto. La etiqueta
        /// es el TurnOrder actual del host (informativa): el cliente carga
        /// fijando SU slot. Sin reintentos del host: el cliente repite su
        /// pedido por Tick si no llega.</summary>
        private static void SendStateToSlot(int actor, string reason)
        {
            var pm = PhotonManager.Instance;
            var gm = GameManager.Instance;
            if (pm == null || gm == null) return;

            byte[] map;
            byte[] jue = StateHandoff.SaveStateFiles(gm.TurnOrder, out map);
            if (jue == null)
            {
                CivilizameMPPlugin.Log.LogError($"[Turn] HOST: no se pudo guardar el estado ({reason}) para el actor {actor}");
                return;
            }

            var msg = new HandoffMessage
            {
                FromTurn = gm.TurnOrder,
                LabelTurn = gm.TurnOrder,
                BaseSeq = pm.GetNextSequence(),
                JueBytes = jue,
                MapBytes = map
            };

            int sendResult = pm.SendHandoff(msg, actor);
            if (sendResult >= 0 || sendResult == PhotonManager.SEND_QUEUED)
            {
                CivilizameMPPlugin.Log.LogInfo(
                    $"[Turn] HOST: estado {reason} → actor {actor} (label {msg.LabelTurn}, baseSeq {msg.BaseSeq}, {jue.Length} B)");
            }
            else
            {
                CivilizameMPPlugin.Log.LogError($"[Turn] HOST: fallo al enviar el estado {reason} al actor {actor}; el cliente repetirá su pedido");
            }
        }

        // ------------------------------------------------------------------
        //  Desconexiones de clientes (host)
        // ------------------------------------------------------------------

        public static void OnClientDisconnected(int actorNumber)
        {
            // El slot NO se convierte a IA: se conserva para el jugador y su
            // resync al volver. Si esperábamos SU turno, el Tick lo salta
            // tras el grace (TrySkipAbsentAwaited).
            _lastDoneSeq.Remove(actorNumber);
        }

        /// <summary>Solo desactiva la espera (sin retomar el ciclo): se usa
        /// cuando esta PC DEJA de ser host (master migró a otro actor).</summary>
        public static void CancelAwaitingNoResume()
        {
            CivilizameMPPlugin.Log.LogWarning("[Turn] Dejamos de ser host: cancelando la espera de turno (sin retomar ciclo)");
            _phase = Phase.Idle;
            AwaitingClientTurn = false;
            _awaitActor = -1;
            _awaitSlot = -1;
            _resumeAt = 0f;
        }

        // ------------------------------------------------------------------
        //  RECEPCIÓN de handoff (estado inicial / resync del cliente)
        // ------------------------------------------------------------------

        public static void HandleIncomingHandoff(HandoffMessage msg, int senderId)
        {
            if (msg == null) return;
            var state = MPStateManager.Instance;
            if (state == null) return;

            if (senderId <= 0 || senderId == state.LocalActorNumber)
                return;

            if (state.IsClient)
            {
                // Todo estado del host durante la partida se acepta: es el
                // inicial o el de un resync. El orden lo lleva BaseSeq.
                ClientManager.Instance?.ReceiveOwnTurn(msg);
                return;
            }

            // El host no recibe estados de los clientes (el aviso de fin de
            // turno es ligero, EV_TURN_DONE).
            CivilizameMPPlugin.Log.LogWarning(
                $"[Turn] HOST: estado completo entrante del actor {senderId} ignorado (protocolo de eventos)");
        }

        // ------------------------------------------------------------------
        //  Restaura el estado en disco con la ruta vanilla (recarga escena)
        // ------------------------------------------------------------------

        public static void ReloadFromSave()
        {
            var gm = GameManager.Instance;
            bool inGame = gm != null && SceneManager.GetActiveScene().name == MPConstants.GAME_SCENE;

            CivilizameMPPlugin.Log.LogInfo(
                $"[Turn] Restaurando estado desde escena '{SceneManager.GetActiveScene().name}' " +
                (inGame ? "(en partida: recarga vanilla de GuardadoSeguridad)" : "(en menú: marcando carga y abriendo la escena Juego)"));

            MPWaitingPanel.Instance?.SetStatus("CARGANDO", "Aplicando el estado recibido...");
            MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);

            if (inGame)
            {
                // Ruta vanilla: escribe NewGameData.gen y recarga la escena "Juego".
                gm.LoadGuardadoSeguridad();
            }
            else
            {
                // Desde el menú: marcar modo carga y abrir la escena del juego.
                StateHandoff.WriteLoadMarker();
                SceneManager.LoadScene(MPConstants.GAME_SCENE);
            }
        }

        // ------------------------------------------------------------------
        //  Host Migration: el nuevo host asume el control de la partida
        // ------------------------------------------------------------------

        public static void OnBecameHost()
        {
            if (!MPStateManager.Instance.IsHost) return;

            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null || gm.jugadores.Length == 0)
            {
                CivilizameMPPlugin.Log.LogError("[HostMigration] GameManager no disponible; no se puede asumir el host.");
                return;
            }

            LiveSync.Spectating = false;
            _phase = Phase.Idle;
            AwaitingClientTurn = false;
            _awaitActor = -1;
            _awaitSlot = -1;
            _awaitGoneSince = 0f;
            _resumeAt = 0f;

            bool hasSavedState = System.IO.File.Exists(StateHandoff.SavePath);

            if (hasSavedState)
            {
                // El estado en disco es el ÚLTIMO que este PC escribió. La
                // recarga en la ruta vanilla (restore clásico) reanuda el
                // ciclo: el StartTurn del slot etiquetado corre aquí y la
                // máquina de estados retoma el ciclo con el gate del nuevo
                // host (SyncPhaseAfterRestore).
                CivilizameMPPlugin.Log.LogWarning(
                    "[HostMigration] Somos el nuevo host. Recargando el último estado en disco (TurnOrder actual: " + gm.TurnOrder + ")...");
                ReloadFromSave();
            }
            else
            {
                CivilizameMPPlugin.Log.LogError(
                    "[HostMigration] Somos el nuevo host pero no hay estado guardado en disco; no se puede retomar la partida.");
            }
        }

        /// <summary>Tras el restore clásico de una migración, sincroniza la
        /// máquina de estados con el slot que acaba de arrancar.</summary>
        public static void SyncPhaseAfterRestore(GameManager gm, int landedSlot)
        {
            if (!MPStateManager.Instance.IsHost) return;
            _hostTurnStarted = true;
            _resetAt = Time.time;
            LiveSync.EmitSlot(landedSlot); // que los clientes sepan de quién es el turno

            if (landedSlot == MPMatchState.MiIndiceLocal)
            {
                _phase = Phase.HostTurn;
                AwaitingClientTurn = false;
                LiveSync.Spectating = false; // el restore clásico nos puso en espectador
                ShowVanillaWaiting(false);
            }
            else if (landedSlot >= 0 && landedSlot < gm.jugadores.Length
                     && gm.jugadores[landedSlot] != null
                     && !gm.jugadores[landedSlot].RealPlayer)
            {
                _phase = Phase.AwaitAI;
                _phaseSince = Time.unscaledTime;
                _awaitSlot = landedSlot;
                _awaitActor = -1;
                AwaitingClientTurn = true;
                ShowVanillaWaiting(true);
            }
            else
            {
                _phase = Phase.AwaitRemoteHuman;
                _phaseSince = Time.unscaledTime;
                _awaitSlot = landedSlot;
                _awaitActor = ActorForSlot(landedSlot);
                _awaitGoneSince = 0f;
                AwaitingClientTurn = true;
                ShowVanillaWaiting(true);
            }
            CivilizameMPPlugin.Log.LogInfo($"[Turn] HOST (migración): fase {_phase} tras restore (slot {landedSlot})");
        }

        // ------------------------------------------------------------------
        //  Utilidades de mapeo slot <-> actor
        // ------------------------------------------------------------------

        private static int ActorForSlot(int slot)
        {
            var st = MPStateManager.Instance;
            if (st == null || slot < 0 || slot >= st.PlayerSlots.Count) return -1;
            var s = st.PlayerSlots[slot];
            return s != null && s.ActorNumber > 0 ? s.ActorNumber : -1;
        }

        private static int SlotOfActor(int actorNumber)
        {
            var st = MPStateManager.Instance;
            if (st == null) return -1;
            foreach (var s in st.PlayerSlots)
                if (s != null && s.ActorNumber == actorNumber)
                    return s.SlotIndex;
            return -1;
        }
    }
}
