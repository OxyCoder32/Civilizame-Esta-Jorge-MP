using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using CivilizameMP.Core;
using CivilizameMP.UI;

namespace CivilizameMP.Network
{
    /// <summary>
    /// Canal LIGERO de eventos en vivo (opuesto al handoff completo de estado):
    ///
    /// Tras el handoff ÚNICO de creación de partida, el juego NO se vuelve a
    /// sincronizar por estado completo. Todo viaja por aquí:
    ///
    ///  - EV_SLOT: el host avanza el ciclo de turnos y lo anuncia. Los
    ///    clientes avanzan SU TurnOrder en espejo (sin StartTurn: los efectos
    ///    los ejecuta la PC que juega el turno); cuando el slot es el propio,
    ///    el cliente "arma" su turno y lo juega en su POV.
    ///  - Micro-eventos del turno en curso (movimientos, muertes, vida,
    ///    ataques, curaciones, exploración, unidades nuevas, ciudades,
    ///    tribus): quien ACTÚA narra, el resto pinta en su POV.
    ///  - EV_PLAYER_RES: totales de recursos del jugador activo (ingresos del
    ///    StartTurn y cambios al cerrar el turno), para que las copias de los
    ///    demás converjan sin recargar estado.
    ///  - EV_DIPLO: acciones de diplomacia (propuestas, pactos, guerras,
    ///    paz, propaganda, alianzas). Se emiten desde la PC que actúa y se
    ///    REPLAYAN en las demás llamando al método vanilla con el TurnOrder
    ///    del actor: los avisos siguen siendo de uno o más turnos (vanilla).
    ///  - EV_TURN_DONE: aviso ligero de fin de turno del cliente (el host
    ///    retoma el ciclo en el slot siguiente).
    ///  - EV_RESYNC: pedido del cliente (al reentrar en la sala) para recibir
    ///    un estado fresco con etiqueta = TurnOrder actual del host.
    ///
    /// Filosofía anti-loops: al APLICAR un evento se pone LiveSync.Applying
    /// para que los parches emisores no reenvíen lo que se está pintando.
    /// Cada payload lleva el ORIGEN (actor que actúa) y una SEQ del emisor;
    /// cada PC ignora su propio eco, y durante una carga de estado se
    /// almacenan los eventos en cola para reproducirlos en orden después.
    /// </summary>
    public static class LiveSync
    {
        // ------------------------------------------------------------------
        //  Estado global del canal
        // ------------------------------------------------------------------

        /// <summary>
        /// Espectador: el turno en curso no es propio (o ya se cerró). Los
        /// inputs quedan bloqueados (GameControlPatches) y la copia local solo
        /// cambia vía eventos. Se pone a false al armar el turno propio.
        /// </summary>
        public static bool Spectating;

        /// <summary>Turno que se está reproduciendo en vivo, según el último
        /// evento SLOT/TURN_START recibido/emite (para el HUD).</summary>
        public static int CurrentTurnLabel = -1;
        public static string CurrentTurnName;

        /// <summary>Evita que la aplicacion de eventos dispare emisiones.</summary>
        internal static bool Applying;

        /// <summary>True mientras un estado recibido está EN CAMINO (escritura
        /// + recarga de escena): los eventos en vivo se almacenan en cola.</summary>
        public static bool LoadPending;

        /// <summary>SEQ del estado que se está cargando (BaseSeq del handoff).
        /// Los eventos del host con seq <= este valor son ANTERIORES al
        /// snapshot (ya están dentro del estado) y se descartan al reproducir
        /// la cola. -1 = no hay filtro activo.</summary>
        public static int LoadSeq = -1;

        // ------------------------------------------------------------------
        //  Códigos de evento (payload binario: origen + seq + byte opcode)
        // ------------------------------------------------------------------

        public const byte EV_TURN_START = 0;
        public const byte EV_UNIT_MOVE = 1;
        public const byte EV_UNIT_DIE = 2;
        public const byte EV_UNIT_HP = 3;
        public const byte EV_CITY_HP = 4;
        public const byte EV_ATTACK_FX = 5;
        public const byte EV_UNIT_SPAWN = 6;
        public const byte EV_TRIBU = 7;
        public const byte EV_CITY_FOUNDED = 8;
        public const byte EV_TURN_DONE = 9;
        public const byte EV_EXPLORE = 10;
        public const byte EV_SLOT = 11;
        public const byte EV_RESYNC = 12;
        public const byte EV_PLAYER_RES = 13;
        public const byte EV_DIPLO = 14;
        public const byte EV_LOAD_READY = 15;
        public const byte EV_CITY_TICK = 16;
        public const byte EV_JUG_TICK = 17;
        public const byte EV_TILE_MEJORA = 18;
        public const byte EV_TRIBU_ADD = 19;
        public const byte EV_CITY_CONQUERED = 20;
        public const byte EV_CITY_RAZED = 21;
        public const byte EV_UNIT_REL = 22;
        public const byte EV_CITY_RELS = 23;
        public const byte EV_PLAYER_DEAD = 24;
        public const byte EV_LAST = EV_PLAYER_DEAD;

        /// <summary>Nombre corto del opcode para los logs de diagnóstico.</summary>
        internal static string NameOf(byte ev)
        {
            switch (ev)
            {
                case EV_TURN_START: return "TURN_START";
                case EV_UNIT_MOVE: return "UNIT_MOVE";
                case EV_UNIT_DIE: return "UNIT_DIE";
                case EV_UNIT_HP: return "UNIT_HP";
                case EV_CITY_HP: return "CITY_HP";
                case EV_ATTACK_FX: return "ATTACK_FX";
                case EV_UNIT_SPAWN: return "UNIT_SPAWN";
                case EV_TRIBU: return "TRIBU";
                case EV_CITY_FOUNDED: return "CITY_FOUNDED";
                case EV_TURN_DONE: return "TURN_DONE";
                case EV_EXPLORE: return "EXPLORE";
                case EV_SLOT: return "SLOT";
                case EV_RESYNC: return "RESYNC";
                case EV_PLAYER_RES: return "PLAYER_RES";
                case EV_DIPLO: return "DIPLO";
                case EV_LOAD_READY: return "LOAD_READY";
                case EV_CITY_TICK: return "CITY_TICK";
                case EV_JUG_TICK: return "JUG_TICK";
                case EV_TILE_MEJORA: return "TILE_MEJORA";
                case EV_TRIBU_ADD: return "TRIBU_ADD";
                case EV_CITY_CONQUERED: return "CITY_CONQUERED";
                case EV_CITY_RAZED: return "CITY_RAZED";
                case EV_UNIT_REL: return "UNIT_REL";
                case EV_CITY_RELS: return "CITY_RELS";
                case EV_PLAYER_DEAD: return "PLAYER_DEAD";
                default: return "?" + ev;
            }
        }

        /// <summary>Identificadores de los métodos de diplomacia (EV_DIPLO).</summary>
        internal const byte DIP_PROPOSE_ALLIANCE = 0;   // ProposeAliance(act, target)
        internal const byte DIP_DISOLVE_ALLIANCE = 1;   // DisolveAliance(act, target)
        internal const byte DIP_BREAK_ALLIANCE = 2;     // BreakAliance(act, target)
        internal const byte DIP_ADD_PACTO = 3;          // AddPacto(j1, j2, p, accept)
        internal const byte DIP_SURPRISE_WAR = 4;       // DeclareSurpriseWar(jug1, jug2)
        internal const byte DIP_FORMAL_WAR = 5;         // DeclareFormalWar(jug1, jug2)
        internal const byte DIP_PROPOSE_PEACE = 6;      // ProposePeace(jug1, jug2)
        internal const byte DIP_SET_PEACE = 7;          // SetPeace(j1, j2)
        internal const byte DIP_PROPAGANDA = 8;         // LaunchPropaganda(j1, j2)
        internal const byte DIP_ALLIANCE_RESPONSE = 9;  // AllianceResponseNotif(a, b, aux)
        internal const byte DIP_FORMAL_WAR_DONE = 10;   // FormalWarChecker: la guerra formal SE CONCRETA (j1, j2)

        // ------------------------------------------------------------------
        //  Runner (corutinas + Tick de cola de eventos)
        // ------------------------------------------------------------------

        private static LiveSyncRunner _runner;

        internal static LiveSyncRunner Runner
        {
            get
            {
                if (_runner == null)
                {
                    var go = new GameObject("CivilizameMP_LiveSyncRunner");
                    _runner = go.AddComponent<LiveSyncRunner>();
                    UnityEngine.Object.DontDestroyOnLoad(go);
                }
                return _runner;
            }
        }

        public class LiveSyncRunner : MonoBehaviour
        {
            private void Start()
            {
                // El runner vive desde el primer Tick: engancha ahí el canal de
                // eventos de Photon al procesador de LiveSync.
                var pm = PhotonManager.Instance;
                if (pm != null) pm.OnLiveEvent += HandleLiveEvent;
            }

            private void HandleLiveEvent(byte[] payload, int sender)
                => LiveSync.HandlePayload(payload, sender);

            private void Update()
            {
                // Orden importante: reproducir PRIMERO la cola de eventos
                // (incluye los EV_SLOT que arman el turno) y LUEGO el Tick del
                // control de turnos (que puede consumir el turno armado).
                LiveSync.TickPending();
                TurnController.Tick();
            }

            /// <summary>Corutinas usadas por los appliers (animación de movimiento).</summary>
            public void StartLiveCoroutine(IEnumerator el) { StartCoroutine(el); }
        }

        public static void Reset()
        {
            Spectating = false;
            CurrentTurnLabel = -1;
            CurrentTurnName = null;
            Applying = false;
            LoadPending = false;
            LoadSeq = -1;
            _pending.Clear();
            _warnedQueueFull = false;
            _unitScriptables = null;
            _fxCache.Clear();
            MPWaitingPanel.Instance?.HideHUD();
            EnsureRunner(); // suscribe ya el canal LIVE_EVENT de Photon
        }

        /// <summary>Crea (si falta) y engancha el runner que rutea los eventos
        /// en vivo. Se llama temprano (Reset en lobby/arranque), porque los
        /// eventos LIVE_EVENT pueden llegar antes del primer applier.</summary>
        public static void EnsureRunner()
        {
            var r = Runner;
        }

        // ------------------------------------------------------------------
        //  Guardas de emisión
        // ------------------------------------------------------------------

        private static FieldInfo _playingField;

        /// <summary>True si ESTA PC es la que debe narrar el turno actual.</summary>
        public static bool MayEmit()
        {
            if (Applying || StateHandoff.PendingTurnOrderAfterLoad >= 0)
                return false;
            var st = MPStateManager.Instance;
            if (st == null || !StateHandoff.IsInGame()) return false;
            var pm = PhotonManager.Instance;
            if (pm == null || !pm.IsInRoom) return false;
            var gm = GameManager.Instance;
            if (gm == null || Tablero.Instance == null || MPMatchState.MiIndiceLocal < 0) return false;

            if (_playingField == null)
                _playingField = typeof(GameManager).GetField("playing",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            bool playing = _playingField != null && _playingField.GetValue(gm) is bool b && b;
            if (!playing) return false;

            if (st.IsHost)
            {
                // MatchStarted solo se activa en el host; en el cliente es
                // siempre false y bloqueaba TODA su emisión (por eso el host
                // no veía los movimientos de los clientes). La señal del
                // cliente es la config aplicada + estar en la partida.
                var hm = HostManager.Instance;
                if (hm == null || !hm.MatchStarted) return false;
                return TurnController.HostMayNarrate(); // el host narra su turno y los de la IA
            }

            // Cliente: solo narra SU turno (el resto llega por eventos/watch).
            return !Spectating && gm.TurnOrder == MPMatchState.MiIndiceLocal;
        }

        // ------------------------------------------------------------------
        //  Emisión
        // ------------------------------------------------------------------

        private static int NextSeq()
        {
            var pm = PhotonManager.Instance;
            return pm != null ? pm.GetNextSequence() : 0;
        }

        /// <summary>En el host: broadcast a todos los clientes. En cliente: al master.</summary>
        public static void BroadcastToActors(byte[] payload)
        {
            var st = MPStateManager.Instance;
            if (st == null) return;
            PhotonManager.Instance?.SendLiveEvent(payload, toOthers: st.IsHost);
        }

        private static void Send(Action<BinaryWriter> build)
        {
            if (!MayEmit()) return;
            try
            {
                var st = MPStateManager.Instance;
                if (st == null) return;
                byte[] payload;
                using (var ms = new MemoryStream(64))
                {
                    using (var w = new BinaryWriter(ms))
                    {
                        // Cabecera común: ORIGEN real del evento (actor de la PC
                        // que actúa; necesario porque el host RELAYA los eventos
                        // de clientes y cada PC debe filtrar su propio eco) y
                        // SEQ monótona del emisor (filtro de la cola de carga).
                        w.Write(st.LocalActorNumber);
                        w.Write(NextSeq());
                        build(w);
                        w.Flush();
                    }
                    payload = ms.ToArray();
                }
                // Diagnóstico: qué emite esta PC y de qué tamaño.
                if (payload.Length > 8)
                    CivilizameMPPlugin.Log.LogInfo(
                        $"[LiveSync→] {NameOf(payload[8])} ({payload.Length} B) actor {st.LocalActorNumber}");
                BroadcastToActors(payload);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[LiveSync] Error emitiendo evento: {ex.Message}");
            }
        }

        private static int SlotOfPlayer(Jugador jug)
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null || jug == null) return -1;
            for (int i = 0; i < gm.jugadores.Length; i++)
                if (gm.jugadores[i] == jug) return i;
            return -1;
        }

        // --- EV_SLOT: avance del ciclo de turnos en espejo (solo host) -------

        /// <summary>
        /// Lo llama el gate del host (TurnController.OnTurnAdvancing) por CADA
        /// slot del ciclo: los clientes avanzan su TurnOrder en espejo o arman
        /// su propio turno. No pasa por MayEmit (es un evento del CICLO, no de
        /// acción); requiere host en partida y en sala.
        /// </summary>
        public static void EmitSlot(int landing)
        {
            var st = MPStateManager.Instance;
            var pm = PhotonManager.Instance;
            if (st == null || !st.IsHost || pm == null || !pm.IsInRoom) return;
            if (!StateHandoff.IsInGame()) return;
            try
            {
                var gm = GameManager.Instance;
                string name = (gm != null && gm.jugadores != null && landing >= 0 &&
                               landing < gm.jugadores.Length && gm.jugadores[landing] != null)
                    ? gm.jugadores[landing].Nombre : ("Jugador " + (landing + 1));

                byte[] payload;
                using (var ms = new MemoryStream(32))
                {
                    using (var w = new BinaryWriter(ms))
                    {
                        w.Write(st.LocalActorNumber);
                        w.Write(NextSeq());
                        w.Write(EV_SLOT);
                        w.Write(landing);
                        w.Write(name ?? "");
                        w.Flush();
                    }
                    payload = ms.ToArray();
                }
                CurrentTurnLabel = landing;
                CurrentTurnName = name;
                CivilizameMPPlugin.Log.LogInfo($"[LiveSync→] SLOT ({landing}, {name}) broadcast del host");
                pm.SendLiveEvent(payload, toOthers: true);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[LiveSync] Error emitiendo EV_SLOT: {ex.Message}");
            }
        }

        // --- emisores públicos (llamados por los parches de LiveSyncPatches) ---

        public static void SendTurnStart(int slot)
        {
            var gm = GameManager.Instance;
            string name = (gm != null && gm.jugadores != null && slot >= 0 && slot < gm.jugadores.Length
                           && gm.jugadores[slot] != null)
                ? gm.jugadores[slot].Nombre : ("Jugador " + (slot + 1));
            Send(w =>
            {
                w.Write(EV_TURN_START);
                w.Write(slot);
                w.Write(name ?? "");
            });
        }

        /// <summary>Totales de recursos del jugador activo: la PC que corre el
        /// StartTurn (host en sus turnos y IA; cliente en el propio) los envía
        /// para que las copias de los demás converjan sin recargar estado.</summary>
        public static void SendRes(Jugador jug)
        {
            if (jug == null) return;
            int slot = SlotOfPlayer(jug);
            if (slot < 0) return;
            Send(w =>
            {
                w.Write(EV_PLAYER_RES);
                w.Write(slot);
                w.Write(jug.OroTotal);
                w.Write(jug.CulturaTotal);
                w.Write(jug.TurismoTotal);
                w.Write(jug.FeTotal);
                w.Write(jug.InfluenciaTotal);
                w.Write(jug.FelicidadTotal);
            });
        }

        // ------------------------------------------------------------------
        //  Tick de estado (StartTurn replica): edificios, maravillas,
        //  crecimiento, techs, época dorada... corren SOLO en la PC del dueño.
        //  Un snapshot compacto por turno converge las copias sin recargar.
        // ------------------------------------------------------------------

        /// <summary>Estado persistente de una ciudad tras SU StartTurn.</summary>
        public static void SendCityTick(Ciudad c)
        {
            if (c == null) return;
            int owner = SlotOfPlayer(c.myPlayer);
            if (owner < 0) return;
            Send(w =>
            {
                w.Write(EV_CITY_TICK);
                w.Write(owner);
                w.Write(c.posX); w.Write(c.posY);
                w.Write(c.Ciudadanos);
                w.Write(c.ComidaAlmacenada);
                w.Write(c.Vida);
                w.Write(c.ProducciónAlmacenada);
                w.Write(c.ProducciónObjetivo);
                w.Write(c.FelicidadTotal);
                w.Write(c.CulturaBase); w.Write(c.Cultura);
                w.Write(c.ProducciónBase); w.Write(c.Producción);
                w.Write(c.FeBase); w.Write(c.Fe);
                w.Write(c.ComidaBase); w.Write(c.Comida);
                w.Write(c.CienciaBase); w.Write(c.Ciencia);
                w.Write(c.DineroBase); w.Write(c.Dinero);
                w.Write(c.InfluenciaBase); w.Write(c.Influencia);
                w.Write(c.Turismo);
                w.Write(c.Defense);
                w.Write(c.TienePuerto); w.Write(c.TieneAeropuerto); w.Write(c.Delegacion);
                w.Write(c.modeResources);
                w.Write(c.myReligionThisTurn);
                // Cola de construcción (resuelta por nombre en el receptor)
                w.Write(c.UnitConstruyendo != null && c.UnitConstruyendo.Prefab != null
                    ? c.UnitConstruyendo.Prefab.name : "");
                w.Write(c.BuildingConstruyendo != null ? c.BuildingConstruyendo.Nombre : "");
                w.Write(c.MaravConstruyendo != null ? c.MaravConstruyendo.Nombre : "");
                // Religiones
                w.Write(c.maxRel);
                for (int i = 0; i < c.maxRel; i++)
                {
                    w.Write(c.religiones[i].jug);
                    w.Write(c.religiones[i].cant);
                }
                // Edificios y maravillas (por nombre)
                w.Write(c.EdifMax);
                for (int i = 0; i < c.EdifMax; i++)
                    w.Write(c.Edificios[i] != null ? c.Edificios[i].Nombre : "");
                w.Write(c.MaravMax);
                for (int i = 0; i < c.MaravMax; i++)
                    w.Write(c.Maravillas[i] != null ? c.Maravillas[i].Nombre : "");
            });
        }

        /// <summary>Estado de ciencia/época dorada de un jugador tras SU
        /// StartTurn (progreso de techs, techs completadas, banderas).</summary>
        public static void SendJugTick(Jugador jug)
        {
            if (jug == null || jug.Tecs == null) return;
            int slot = SlotOfPlayer(jug);
            if (slot < 0) return;
            var gm = GameManager.Instance;
            Send(w =>
            {
                w.Write(EV_JUG_TICK);
                w.Write(slot);
                // Progreso de techs
                w.Write(jug.Tecs.Length);
                for (int i = 0; i < jug.Tecs.Length; i++)
                    w.Write(jug.Tecs[i].done);
                w.Write(jug.researching != null ? jug.researching.Nombre : "");
                w.Write(jug.maxRes);
                for (int i = 0; i < jug.maxRes; i++)
                    w.Write(jug.Researched[i] != null ? jug.Researched[i].Nombre : "");
                // Banderas derivadas de techs
                w.Write(jug.veHierro); w.Write(jug.veCarbon);
                w.Write(jug.vePetroleo); w.Write(jug.veUranio);
                w.Write(jug.canMina); w.Write(jug.canAserr);
                w.Write(jug.canPastiz); w.Write(jug.canRefin);
                w.Write(jug.canCarre);
                w.Write(jug.navegar);
                w.Write(jug.FinTecnologias);
                w.Write(jug.Bancarrota);
                w.Write(jug.maxUIUnidad);
                // Recursos sueltos (CheckExtraResources)
                w.Write(jug.MiHierro); w.Write(jug.MisCaballos);
                w.Write(jug.MiCarbon); w.Write(jug.MiPetroleo); w.Write(jug.MiUranio);
                // Época dorada + Saiyan
                w.Write(jug.TurnLeftEpocaDorada);
                w.Write(jug.NivelEpocaDorada);
                w.Write(jug.GoldenAgeMultiplier);
                w.Write(jug.TurnosSuperSaiyan);
                // Techs/cultura gratis globales del GameManager
                if (gm != null) { w.Write(gm.FreeTechs); w.Write(gm.FreeCulture); }
                else { w.Write(0); w.Write(0); }
            });
        }

        /// <summary>Mejora de casilla (granja/mina/carretera/...): el trabajador
        /// la escribe en SU tablero y en las demás copias no existía.</summary>
        public static void SendTileMejora(int x, int y, Tablero.Mejora mejora, bool carretera)
        {
            Send(w =>
            {
                w.Write(EV_TILE_MEJORA);
                w.Write(x); w.Write(y);
                w.Write((int)mejora);
                w.Write(carretera);
            });
        }

        /// <summary>Aparición de una tribu (campamento bárbaro) con su
        /// beneficio YA sorteado en la PC emisaria.</summary>
        public static void SendTribuAdd(int x, int y, int benef)
        {
            Send(w =>
            {
                w.Write(EV_TRIBU_ADD);
                w.Write(x); w.Write(y);
                w.Write(benef);
            });
        }

        /// <summary>Ciudad conquistada: cambio de dueño (la unidad que la
        /// toma ya viaja por UNIT_MOVE).</summary>
        public static void SendCityConquered(int x, int y, int newOwner, int ciudadanos)
        {
            Send(w =>
            {
                w.Write(EV_CITY_CONQUERED);
                w.Write(x); w.Write(y);
                w.Write(newOwner);
                w.Write(ciudadanos);
            });
        }

        /// <summary>Ciudad DESTRUIDA (Atila/NightKing la arrasan).</summary>
        public static void SendCityRazed(int x, int y, int raider, int ciudadanos)
        {
            Send(w =>
            {
                w.Write(EV_CITY_RAZED);
                w.Write(x); w.Write(y);
                w.Write(raider);
                w.Write(ciudadanos);
            });
        }

        /// <summary>Conversión religiosa de UNIDADES (misionero/profeta):
        /// cambia myRel de las unidades vecinas.</summary>
        public static void SendUnitRel(int owner, int x, int y, int rel)
        {
            Send(w =>
            {
                w.Write(EV_UNIT_REL);
                w.Write(owner);
                w.Write(x); w.Write(y);
                w.Write(rel);
            });
        }

        /// <summary>Estado completo de las religiones de una ciudad
        /// (AddToRel/Inquisición/ManageReligions mutan por caminos varios).</summary>
        public static void SendCityRels(Ciudad c)
        {
            if (c == null || c.religiones == null) return;
            Send(w =>
            {
                w.Write(EV_CITY_RELS);
                w.Write(c.posX); w.Write(c.posY);
                w.Write(c.maxRel);
                for (int i = 0; i < c.maxRel; i++)
                {
                    w.Write(c.religiones[i].jug);
                    w.Write(c.religiones[i].cant);
                }
                w.Write(c.myReligionThisTurn);
            });
        }

        /// <summary>Jugador ELIMINADO (perdió ciudades y unidades).</summary>
        public static void SendPlayerDead(int slot)
        {
            Send(w =>
            {
                w.Write(EV_PLAYER_DEAD);
                w.Write(slot);
            });
        }

        /// <summary>Acción de diplomacia (ver ids DIP_*). a/b/c son los
        /// argumentos RESUELTOS en la PC que actúa (targets -1 ya resueltos);
        /// el applier replaya el método vanilla con el TurnOrder del actor.</summary>
        public static void SendDiploma(byte methodId, int a, int b, int c, bool flag)
        {
            Send(w =>
            {
                w.Write(EV_DIPLO);
                w.Write(methodId);
                w.Write(a);
                w.Write(b);
                w.Write(c);
                w.Write(flag);
            });
        }

        public static void SendUnitMove(Unidad u, int fromX, int fromY)
        {
            if (u == null) return;
            int owner = SlotOfPlayer(u.myPlayer);
            if (owner < 0) return;
            Send(w =>
            {
                w.Write(EV_UNIT_MOVE);
                w.Write(owner);
                w.Write(fromX); w.Write(fromY);
                w.Write(u.posX); w.Write(u.posY);
            });
        }

        public static void SendUnitDie(Unidad u, int owner, int x, int y)
        {
            Send(w =>
            {
                w.Write(EV_UNIT_DIE);
                w.Write(owner);
                w.Write(x); w.Write(y);
            });
        }

        public static void SendUnitHp(Unidad u)
        {
            if (u == null) return;
            int owner = SlotOfPlayer(u.myPlayer);
            if (owner < 0) return;
            Send(w =>
            {
                w.Write(EV_UNIT_HP);
                w.Write(owner);
                w.Write(u.posX); w.Write(u.posY);
                w.Write(u.VidaLeft);
            });
        }

        public static void SendCityHp(Ciudad c, int x, int y)
        {
            if (c == null) return;
            Send(w =>
            {
                w.Write(EV_CITY_HP);
                w.Write(x); w.Write(y);
                w.Write(c.Vida);
            });
        }

        public static void SendAttackFx(GameObject fxPrefab, int x, int y)
        {
            string fxName = fxPrefab != null ? fxPrefab.name : null;
            if (string.IsNullOrEmpty(fxName)) return;
            Send(w =>
            {
                w.Write(EV_ATTACK_FX);
                w.Write(fxName);
                w.Write(x); w.Write(y);
            });
        }

        public static void SendUnitSpawn(Unidad u)
        {
            if (u == null) return;
            int owner = SlotOfPlayer(u.myPlayer);
            if (owner < 0) return;
            string prefabName = u.gameObject != null ? u.gameObject.name : null;
            if (string.IsNullOrEmpty(prefabName)) return;
            Send(w =>
            {
                w.Write(EV_UNIT_SPAWN);
                w.Write(owner);
                w.Write(u.posX); w.Write(u.posY);
                w.Write(u.FuerzaIni);
                w.Write(u.MovimientoMax);
                w.Write(u.MovLeft);
                w.Write(u.VidaLeft);
                w.Write(u.Turno);
                w.Write(u.BuildThings);
                w.Write(u.PactFuerza);
                w.Write(u.PactBuildThings);
                w.Write(u.myRel);
                w.Write((int)u.Tipo); // desambigua el prefab en el receptor
                w.Write(prefabName);
            });
        }

        public static void SendTribuTaken(int x, int y)
        {
            Send(w =>
            {
                w.Write(EV_TRIBU);
                w.Write(x); w.Write(y);
            });
        }

        public static void SendCityFounded(int owner, int x, int y, string name, bool capital, int rel)
        {
            Send(w =>
            {
                w.Write(EV_CITY_FOUNDED);
                w.Write(owner);
                w.Write(x); w.Write(y);
                w.Write(name ?? "");
                w.Write(capital);
                w.Write(rel);
            });
        }

        /// <summary>Casillas desveladas por una unidad (la niebla de cada POV
        /// evoluciona en directo; el array Visión del dueño es estado real).</summary>
        public static void SendExplore(Jugador jug, int x, int y, int vision)
        {
            int owner = SlotOfPlayer(jug);
            if (owner < 0 || x < 0 || y < 0) return;
            Send(w =>
            {
                w.Write(EV_EXPLORE);
                w.Write(owner);
                w.Write(x); w.Write(y);
                w.Write(vision);
            });
        }

        // ------------------------------------------------------------------
        //  Aviso ligero de fin de turno (EV_TURN_DONE) — cliente → host
        // ------------------------------------------------------------------

        /// <summary>
        /// El cliente NO sube su estado al terminar: solo avisa al host. No
        /// pasa por MayEmit (es una frontera de turno). El seq del cuerpo es
        /// monótono y lo deduplica el host (permite reintentos del
        /// ClientManager).
        /// </summary>
        public static bool SendTurnDone(int slot, int seq)
        {
            var st = MPStateManager.Instance;
            if (st == null || st.IsHost) return false;
            var pm = PhotonManager.Instance;
            if (pm == null || !pm.IsInRoom) return false; // sin sala: el Tick reintenta al reconectar
            try
            {
                byte[] payload;
                using (var ms = new MemoryStream(24))
                {
                    using (var w = new BinaryWriter(ms))
                    {
                        w.Write(st.LocalActorNumber);
                        w.Write(NextSeq());
                        w.Write(EV_TURN_DONE);
                        w.Write(slot);
                        w.Write(seq);
                        w.Flush();
                    }
                    payload = ms.ToArray();
                }
                return pm.SendLiveEvent(payload, toOthers: false);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[LiveSync] Error enviando TURN_DONE: {ex.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  Ping de carga (EV_LOAD_READY) — cliente → host
        // ------------------------------------------------------------------

        /// <summary>
        /// El cliente confirma que YA CARGÓ la partida (aplicó el estado en
        /// la escena Juego). El host lo acumula en la barrera de carga: el
        /// ciclo de turnos no arranca hasta que todos los humanos conectados
        /// hayan enviado este ping. No pasa por MayEmit.
        /// </summary>
        public static bool SendLoadReady(int slot)
        {
            var st = MPStateManager.Instance;
            if (st == null || st.IsHost) return false;
            var pm = PhotonManager.Instance;
            if (pm == null || !pm.IsInRoom) return false;
            try
            {
                byte[] payload;
                using (var ms = new MemoryStream(16))
                {
                    using (var w = new BinaryWriter(ms))
                    {
                        w.Write(st.LocalActorNumber);
                        w.Write(NextSeq());
                        w.Write(EV_LOAD_READY);
                        w.Write(slot);
                        w.Flush();
                    }
                    payload = ms.ToArray();
                }
                return pm.SendLiveEvent(payload, toOthers: false);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[LiveSync] Error enviando LOAD_READY: {ex.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  Pedido de resincronización (EV_RESYNC) — cliente → host
        // ------------------------------------------------------------------

        /// <summary>
        /// El cliente pide un estado fresco al (re)entrar en la sala durante
        /// una partida. No pasa por MayEmit. Si no hay sala devuelve false y
        /// el Tick del ClientManager reintenta.
        /// </summary>
        public static bool SendResyncRequest()
        {
            var st = MPStateManager.Instance;
            if (st == null || st.IsHost) return false;
            var pm = PhotonManager.Instance;
            if (pm == null || !pm.IsInRoom) return false;
            try
            {
                byte[] payload;
                using (var ms = new MemoryStream(16))
                {
                    using (var w = new BinaryWriter(ms))
                    {
                        w.Write(st.LocalActorNumber);
                        w.Write(NextSeq());
                        w.Write(EV_RESYNC);
                        w.Flush();
                    }
                    payload = ms.ToArray();
                }
                return pm.SendLiveEvent(payload, toOthers: false);
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[LiveSync] Error enviando RESYNC: {ex.Message}");
                return false;
            }
        }

        private static float _lastAutoResync = -300f;

        /// <summary>Auto-reparación ante divergencia (unidad no encontrada al
        /// aplicar un evento): el cliente pide un estado fresco al host. Solo
        /// en espectador (nunca a mitad del turno propio) y con cooldown para
        /// no lanzar recargas en bucle.</summary>
        private static void RequestResyncIfStale()
        {
            var st = MPStateManager.Instance;
            if (st == null || st.IsHost) return;
            if (Applying || !Spectating) return; // jugando nuestro turno: no interrumpir
            if (Time.time - _lastAutoResync < 30f) return;
            _lastAutoResync = Time.time;
            CivilizameMPPlugin.Log.LogWarning(
                "[LiveSync] Divergencia detectada: pidiendo estado fresco al host (auto-resync)");
            SendResyncRequest();
        }

        // ------------------------------------------------------------------
        //  Recepción + cola de carga
        // ------------------------------------------------------------------

        /// <summary>Eventos en cola mientras el mapa no está listo o hay una
        /// carga de estado en camino (se reproducen EN ORDEN al terminar).</summary>
        private static readonly List<byte[]> _pending = new List<byte[]>();
        private static bool _warnedQueueFull;
        private const int MAX_PENDING = 800;

        /// <summary>Eventos en cola pendientes de reproducir.</summary>
        public static int PendingCount => _pending.Count;

        private static bool MapReady()
        {
            var gm = GameManager.Instance;
            return SceneManager.GetActiveScene().name == MPConstants.GAME_SCENE
                   && gm != null && Tablero.Instance != null && gm.jugadores != null;
        }

        /// <summary>Actor del host (para el filtro de seq de la cola de carga).</summary>
        private static int HostActorNumber()
        {
            var st = MPStateManager.Instance;
            if (st == null) return -1;
            foreach (var s in st.PlayerSlots)
                if (s != null && s.IsHost && s.ActorNumber > 0)
                    return s.ActorNumber;
            return -1;
        }

        public static void HandlePayload(byte[] payload, int sender)
        {
            if (payload == null || payload.Length <= 8) return; // cabecera mínima: origin + seq + opcode

            var st = MPStateManager.Instance;
            if (st == null) return;
            if (sender <= 0 || sender == st.LocalActorNumber) return; // eco propio

            // SIN CAMBIO DE JUGADOR NI RECARGAS: no se puede DESCARTAR un
            // evento aunque esta PC aún no esté "en partida" (lobby/menú
            // mientras el snapshot viaja). Se ENCOLA: al recibir el estado
            // (LoadSeq = BaseSeq) el filtro de seq descarta los anteriores
            // al snapshot y el resto se aplica al terminar la carga.
            if (!StateHandoff.IsInGame())
            {
                var hmPre = HostManager.Instance;
                var pmPre = PhotonManager.Instance;
                if (hmPre == null || !hmPre.MatchStarted || pmPre == null || !pmPre.IsInRoom)
                    return; // sin partida activa en la sala: basura fuera de contexto
                if (_pending.Count < MAX_PENDING) _pending.Add(payload);
                return;
            }

            int origin, seq;
            byte ev;
            if (!ParseHeader(payload, out origin, out seq, out ev)) return;

            CivilizameMPPlugin.Log.LogInfo(
                $"[LiveSync←] {NameOf(ev)} de actor {origin} (seq {seq}, host={st.IsHost})");

            // El host RELAYA los eventos de clientes al resto (Receivers=Others
            // evita eco). El origen no cambia: en el payload va solo quien actúa.
            // La petición de resync y el ping de carga son punto a punto:
            // no se retransmiten.
            if (st.IsHost && ev != EV_RESYNC && ev != EV_LOAD_READY)
                PhotonManager.Instance?.SendLiveEvent(payload, toOthers: true);

            if (LoadPending || !MapReady())
            {
                // Carga de estado en camino (o mapa aún no disponible): guardar
                // el evento y reproducirlo en orden después.
                if (_pending.Count < MAX_PENDING) _pending.Add(payload);
                else if (!_warnedQueueFull)
                {
                    _warnedQueueFull = true;
                    CivilizameMPPlugin.Log.LogWarning("[LiveSync] Cola de eventos llena durante la carga; se descartan eventos nuevos");
                }
                return;
            }

            Applying = true;
            try { ApplyBody(payload, origin); }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogWarning($"[LiveSync] Error aplicando evento: {ex.Message}");
            }
            finally { Applying = false; }
        }

        private static bool ParseHeader(byte[] payload, out int origin, out int seq, out byte ev)
        {
            origin = 0; seq = 0; ev = EV_LAST + 1;
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms))
                {
                    origin = r.ReadInt32();
                    seq = r.ReadInt32();
                    ev = r.ReadByte();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Reproduce la cola de eventos almacenados durante una carga.
        /// Los del host con seq <= LoadSeq son anteriores al snapshot recibido
        /// (sus efectos ya están dentro del estado) y se descartan.</summary>
        public static void TickPending()
        {
            if (_pending.Count == 0)
            {
                if (!LoadPending && LoadSeq != -1) LoadSeq = -1; // cola vacía: quitar filtro
                return;
            }
            if (LoadPending || !MapReady()) return;

            int hostActor = HostActorNumber();
            Applying = true;
            try
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    var payload = _pending[i];
                    int origin, seq; byte ev;
                    if (!ParseHeader(payload, out origin, out seq, out ev)) continue;
                    if (origin == (MPStateManager.Instance != null ? MPStateManager.Instance.LocalActorNumber : -1))
                        continue;
                    if (origin == hostActor && LoadSeq >= 0 && seq <= LoadSeq)
                        continue; // anterior al snapshot: ya viene en el estado
                    try { ApplyBody(payload, origin); }
                    catch (Exception ex)
                    {
                        CivilizameMPPlugin.Log.LogWarning($"[LiveSync] Error aplicando evento en cola: {ex.Message}");
                    }
                }
            }
            finally
            {
                Applying = false;
                _pending.Clear();
                LoadSeq = -1;
            }
        }

        private static void ApplyBody(byte[] payload, int origin)
        {
            var st = MPStateManager.Instance;
            var gm = GameManager.Instance;
            if (st == null || gm == null || Tablero.Instance == null || gm.jugadores == null) return;

            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms))
            {
                ms.Position = 8; // saltar cabecera (origin + seq)
                byte ev = r.ReadByte();
                if (ev > EV_LAST) return;
                switch (ev)
                {
                    case EV_SLOT:
                    {
                        int landing = r.ReadInt32();
                        string name = r.ReadString();
                        ApplySlot(landing, name);
                        break;
                    }

                    case EV_RESYNC:
                    {
                        // Solo el host atiende pedidos de resync (los demás
                        // clientes ignoran este evento rebotado).
                        if (st.IsHost)
                            TurnController.HandleResyncRequest(origin);
                        break;
                    }

                    case EV_PLAYER_RES:
                    {
                        int slot = r.ReadInt32();
                        int oro = r.ReadInt32();
                        float cultura = r.ReadSingle();
                        float turismo = r.ReadSingle();
                        float fe = r.ReadSingle();
                        float influencia = r.ReadSingle();
                        float felicidad = r.ReadSingle();
                        var jug = JugFor(slot);
                        if (jug != null)
                        {
                            jug.OroTotal = oro;
                            jug.CulturaTotal = cultura;
                            jug.TurismoTotal = turismo;
                            jug.FeTotal = fe;
                            jug.InfluenciaTotal = influencia;
                            jug.FelicidadTotal = felicidad;
                        }
                        break;
                    }

                    case EV_DIPLO:
                    {
                        byte mid = r.ReadByte();
                        int a = r.ReadInt32();
                        int b = r.ReadInt32();
                        int c = r.ReadInt32();
                        bool flag = r.ReadBoolean();
                        ApplyDiploma(gm, mid, a, b, c, flag);
                        break;
                    }

                    case EV_TURN_START:
                    {
                        int slot = r.ReadInt32();
                        string name = r.ReadString();
                        CurrentTurnLabel = slot;
                        CurrentTurnName = string.IsNullOrEmpty(name) ? ("Jugador " + (slot + 1)) : name;

                        // SIN CAMBIO DE JUGADOR: el TurnOrder local no se toca.
                        // Solo replicamos el reset de movimiento que el StartTurn
                        // del dueño hizo en SU PC (así las copias convergen).
                        var jug = JugFor(slot);
                        if (jug != null && jug.Unidades != null)
                        {
                            int n = Math.Min(jug.maxUnidad, jug.Unidades.Length);
                            for (int i = 0; i < n; i++)
                            {
                                var u = jug.Unidades[i];
                                if (u == null) continue;
                                try { u.ResetMovement(); } catch { }
                                u.fortified = false;
                            }
                        }

                        if (slot == MPMatchState.MiIndiceLocal)
                        {
                            // El arranque de NUESTRO turno narrado por esta PC:
                            // fuera el cartel de espera.
                            TurnController.ShowVanillaWaiting(false);
                        }
                        else if (Spectating)
                        {
                            // Turno ajeno: SOLO cartel vanilla de espera.
                            TurnController.ShowVanillaWaiting(true);
                        }
                        break;
                    }

                    case EV_UNIT_MOVE:
                    {
                        int owner = r.ReadInt32();
                        int fx = r.ReadInt32(); int fy = r.ReadInt32();
                        int tx = r.ReadInt32(); int ty = r.ReadInt32();
                        ApplyUnitMove(owner, fx, fy, tx, ty);
                        break;
                    }

                    case EV_UNIT_DIE:
                    {
                        int owner = r.ReadInt32();
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        ApplyUnitDie(owner, x, y);
                        break;
                    }

                    case EV_UNIT_HP:
                    {
                        int owner = r.ReadInt32();
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        float vida = r.ReadSingle();
                        var u = FindUnit(owner, x, y);
                        if (u != null)
                        {
                            u.VidaLeft = vida;
                            u.ManageHealthBar();
                        }
                        break;
                    }

                    case EV_CITY_HP:
                    {
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        float vida = r.ReadSingle();
                        var c = Tablero.Instance.GetCiudad(x, y);
                        if (c != null)
                        {
                            c.Vida = vida;
                            c.ManageHealthBar();
                        }
                        break;
                    }

                    case EV_ATTACK_FX:
                    {
                        string fxName = r.ReadString();
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        ApplyAttackFx(fxName, x, y);
                        break;
                    }

                    case EV_UNIT_SPAWN:
                    {
                        int owner = r.ReadInt32();
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        int fuerza = r.ReadInt32();
                        int movMax = r.ReadInt32();
                        float movLeft = r.ReadSingle();
                        float vidaLeft = r.ReadSingle();
                        bool turno = r.ReadBoolean();
                        int buildThings = r.ReadInt32();
                        int pactFuerza = r.ReadInt32();
                        int pactBuild = r.ReadInt32();
                        int rel = r.ReadInt32();
                        int tipo = r.ReadInt32();
                        string prefabName = r.ReadString();
                        ApplyUnitSpawn(owner, x, y, fuerza, movMax, movLeft, vidaLeft,
                            turno, buildThings, pactFuerza, pactBuild, rel, tipo, prefabName);
                        break;
                    }

                    case EV_TRIBU:
                    {
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        var tm = Tablero.Instance.myTribuManajer;
                        if (tm != null)
                        {
                            int pos = tm.BuscarTribu(x, y);
                            if (pos >= 0) tm.DeleteTribu(pos);
                        }
                        break;
                    }

                    case EV_CITY_FOUNDED:
                    {
                        int owner = r.ReadInt32();
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        string name = r.ReadString();
                        bool capital = r.ReadBoolean();
                        int rel = r.ReadInt32();
                        // Idempotente: si la ciudad ya existe (eco/replay) no repetir.
                        if (Tablero.Instance.GetCiudad(x, y) != null) break;
                        var jug = owner >= 0 && owner < gm.jugadores.Length ? gm.jugadores[owner] : null;
                        if (jug != null)
                            gm.NewCity(jug, x, y, name, capital, rel);
                        break;
                    }

                    case EV_EXPLORE:
                    {
                        int owner = r.ReadInt32();
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        int vision = r.ReadInt32();
                        var jug = JugFor(owner);
                        if (jug != null) jug.Explore(x, y, vision, false);
                        break;
                    }

                    case EV_TURN_DONE:
                    {
                        int slot = r.ReadInt32();
                        int seq = r.ReadInt32();
                        if (st.IsHost)
                        {
                            TurnController.HandleTurnDone(origin, slot, seq);
                        }
                        else if (slot >= 0 && slot < gm.jugadores.Length && gm.jugadores[slot] != null)
                        {
                            // El cartel vanilla de espera ya está visible (lo
                            // puso el EV_SLOT/EV_TURN_START del turno ajeno);
                            // no superponer HUD encima.
                            break;
                        }
                        break;
                    }

                    case EV_CITY_TICK:
                    {
                        int owner = r.ReadInt32();
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        ApplyCityTick(owner, x, y, r);
                        break;
                    }

                    case EV_JUG_TICK:
                    {
                        int slot = r.ReadInt32();
                        ApplyJugTick(slot, r);
                        break;
                    }

                    case EV_TILE_MEJORA:
                    {
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        var mejora = (Tablero.Mejora)r.ReadInt32();
                        bool carretera = r.ReadBoolean();
                        var board = Tablero.Instance.tablero;
                        if (board != null && x >= 0 && y >= 0
                            && x < board.GetLength(0) && y < board.GetLength(1))
                        {
                            board[x, y]._mejora = mejora;
                            board[x, y].Carretera = carretera;
                            Tablero.Instance.ChangeValueToOneCasilla(
                                board[x, y], out board[x, y].Oro, out board[x, y].Comida,
                                out board[x, y].Produccion);
                        }
                        break;
                    }

                    case EV_TRIBU_ADD:
                    {
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        int benef = r.ReadInt32();
                        var tm = Tablero.Instance.myTribuManajer;
                        if (tm != null && tm.BuscarTribu(x, y) < 0)
                            tm.AddTribu(x, y, benef);
                        break;
                    }

                    case EV_CITY_CONQUERED:
                    {
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        int newOwner = r.ReadInt32();
                        int ciudadanos = r.ReadInt32();
                        ApplyCityConquered(x, y, newOwner, ciudadanos);
                        break;
                    }

                    case EV_CITY_RAZED:
                    {
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        int raider = r.ReadInt32();
                        int ciudadanos = r.ReadInt32();
                        ApplyCityRazed(x, y, raider, ciudadanos);
                        break;
                    }

                    case EV_UNIT_REL:
                    {
                        int owner = r.ReadInt32();
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        int rel = r.ReadInt32();
                        var u = FindUnit(owner, x, y);
                        if (u != null && u.myRel != rel)
                        {
                            u.myRel = rel;
                            u.ShowRelSimb();
                        }
                        break;
                    }

                    case EV_CITY_RELS:
                    {
                        int x = r.ReadInt32(); int y = r.ReadInt32();
                        var c = Tablero.Instance.GetCiudad(x, y);
                        if (c == null || c.religiones == null) break;
                        int maxRel = r.ReadInt32();
                        for (int i = 0; i < maxRel && i < c.religiones.Length; i++)
                        {
                            int jrel = r.ReadInt32();
                            int jcant = r.ReadInt32();
                            c.religiones[i].jug = jrel;
                            c.religiones[i].cant = jcant;
                        }
                        c.maxRel = maxRel;
                        c.myReligionThisTurn = r.ReadInt32();
                        AccessTools.Method(typeof(Ciudad), "ManageReligionSymbol")?.Invoke(c, null);
                        break;
                    }

                    case EV_PLAYER_DEAD:
                    {
                        int slot = r.ReadInt32();
                        var jug = slot >= 0 && slot < gm.jugadores.Length ? gm.jugadores[slot] : null;
                        if (jug != null)
                        {
                            CivilizameMPPlugin.Log.LogInfo(
                                $"[LiveSync] PLAYER_DEAD slot {slot}: eliminado en otra PC, replicando");
                            gm.DeletePlayer(jug);
                        }
                        break;
                    }

                    case EV_LOAD_READY:
                    {
                        int slot = r.ReadInt32();
                        TurnController.HandleLoadReady(origin, slot);
                        break;
                    }
                }
            }
        }

        private static void ApplySlot(int landing, string name)
        {
            var st = MPStateManager.Instance;
            if (st == null || st.IsHost) return; // el host lleva SU ciclo, no el espejo
            if (!MPMatchState.IsInitialized) return; // pre-config: el estado inicial fijará el TurnOrder

            CivilizameMPPlugin.Log.LogInfo(
                $"[LiveSync] SLOT aplicado en cliente: aterriza slot {landing} ({name}); mi indice={MPMatchState.MiIndiceLocal}");

            var gm = GameManager.Instance;
            if (gm == null) return;

            CurrentTurnLabel = landing;
            CurrentTurnName = string.IsNullOrEmpty(name) ? ("Jugador " + (landing + 1)) : name;

            if (landing == MPMatchState.MiIndiceLocal)
            {
                // Nuestro slot: armar el turno. El Tick correrá el StartTurn
                // PROPIO directamente (sin NextTurn: nunca se cambia de
                // jugador) y narrará en vivo desde esta PC.
                TurnController.ShowVanillaWaiting(false); // es TU turno
                TurnController.ArmMyTurn();
            }
            else
            {
                // Turno ajeno: NO se toca el TurnOrder local (seguimos en
                // nuestro jugador); SOLO cartel vanilla de espera. Los
                // efectos llegan por eventos desde la PC que actúa.
                TurnController.ShowVanillaWaiting(true);
            }
        }

        private static void ApplyDiploma(GameManager gm, byte mid, int a, int b, int c, bool flag)
        {
            var rm = gm.myRelationshipManager;
            if (rm == null) return;

            // Los métodos que usan GameManager.Instance.TurnOrder como ACTOR
            // (trío de alianzas) replayan con el TurnOrder del actor original.
            int prevTurn = gm.TurnOrder;
            try
            {
                switch (mid)
                {
                    case DIP_PROPOSE_ALLIANCE:
                        gm.TurnOrder = a; rm.ProposeAliance(b); break;
                    case DIP_DISOLVE_ALLIANCE:
                        gm.TurnOrder = a; rm.DisolveAliance(b); break;
                    case DIP_BREAK_ALLIANCE:
                        gm.TurnOrder = a; rm.BreakAliance(b); break;
                    case DIP_ADD_PACTO:
                        rm.AddPacto(a, b, c, flag); break;
                    case DIP_SURPRISE_WAR:
                        rm.DeclareSurpriseWar(a, b); break;
                    case DIP_FORMAL_WAR:
                        rm.DeclareFormalWar(a, b); break;
                    case DIP_PROPOSE_PEACE:
                        rm.ProposePeace(a, b); break;
                    case DIP_SET_PEACE:
                        rm.SetPeace(a, b); break;
                    case DIP_PROPAGANDA:
                        rm.LaunchPropaganda(a, b); break;
                    case DIP_ALLIANCE_RESPONSE:
                        rm.AllianceResponseNotif(a, b, flag); break;
                    case DIP_FORMAL_WAR_DONE:
                    {
                        // La guerra formal se CONCRETA: replicar el estado exacto
                        // que vanilla aplica al llegar a 0 el contador
                        // (FormalWarChecker) + arrastrar aliados. NADA de llamar
                        // DeclareSurpriseWar: eso añadiría notifs/contadores que
                        // vanilla no añade aquí.
                        int warBonus = rm.GetWarBonus(a, b);
                        rm.BonusBelicismoDefinitivo[a, b] = warBonus;
                        rm.BonusBelicismoDefinitivo[b, a] = 0;
                        rm.PlayersRelations[a, b] = RelationshipManager.RelationStatus.Guerra;
                        rm.PlayersRelations[b, a] = RelationshipManager.RelationStatus.Guerra;
                        rm.amountLike[a, b] = rm.MIN_LIKE;
                        rm.amountLike[b, a] = rm.MIN_LIKE;
                        rm.YearsToStartWar[a, b] = -1;
                        rm.YearsToStartWar[b, a] = -1;
                        rm.WarCheckAlliances(a, b);
                        break;
                    }
                    default:
                        CivilizameMPPlugin.Log.LogWarning($"[LiveSync] EV_DIPLO método desconocido {mid}");
                        break;
                }
            }
            finally
            {
                if (gm.TurnOrder != prevTurn) gm.TurnOrder = prevTurn;
            }
        }

        // ------------------------------------------------------------------
        //  Appliers (mutan la copia local; sin emisión: Applying=true)
        // ------------------------------------------------------------------

        private static Jugador JugFor(int owner)
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null) return null;
            if (owner < 0 || owner >= gm.jugadores.Length) return null;
            return gm.jugadores[owner];
        }

        private static Unidad FindUnit(int owner, int x, int y)
        {
            var jug = JugFor(owner);
            if (jug == null) return null;
            int n = Math.Min(jug.maxUnidad, jug.Unidades != null ? jug.Unidades.Length : 0);
            for (int i = 0; i < n; i++)
            {
                var u = jug.Unidades[i];
                if (u != null && u.posX == x && u.posY == y)
                    return u;
            }
            return null;
        }

        private static void ApplyUnitMove(int owner, int fx, int fy, int tx, int ty)
        {
            var u = FindUnit(owner, fx, fy);
            if (u == null)
            {
                // Diagnóstico: sin este log un descarte silencioso es invisible.
                CivilizameMPPlugin.Log.LogWarning(
                    $"[LiveSync] UNIT_MOVE descartado: unidad del slot {owner} no está en ({fx},{fy}) en esta copia");

                // Auto-reparación: una divergencia de posición se arrastra para
                // siempre (los movimientos siguientes de esa unidad encadenan
                // en falso). Si somos espectadores, pedir estado fresco al host.
                RequestResyncIfStale();
                return;
            }

            // Banderas de ocupación del tablero (métodos privados de Unidad).
            var gmLeft = AccessTools.Method(typeof(Unidad), "TableroBoolLeftPosition");
            var gmNew = AccessTools.Method(typeof(Unidad), "TableroBoolNewPosition");
            try { if (u.posX == fx && u.posY == fy) gmLeft?.Invoke(u, null); } catch { }
            u.posX = tx;
            u.posY = ty;
            try { gmNew?.Invoke(u, null); } catch { }

            // Animación suave idéntica a la vanilla (10 pasos de 0.01s).
            Vector2 desde = new Vector2(fx, fy);
            Vector2 hasta = new Vector2(tx, ty);
            Runner.StartLiveCoroutine(MoveAnim(u.transform, desde, hasta, u));
        }

        private static System.Collections.IEnumerator MoveAnim(
            Transform t, Vector2 desde, Vector2 hasta, Unidad u)
        {
            if (t == null) yield break;
            t.position = desde;
            for (int i = 0; i < 10; i++)
            {
                if (t == null) yield break;
                yield return new WaitForSeconds(0.01f);
                if (t == null) yield break;
                t.position += (Vector3)((hasta - desde) / 10f);
            }
            if (u != null)
            {
                try
                {
                    AccessTools.Method(typeof(Unidad), "Render")?.Invoke(u, null);
                }
                catch { }
            }
        }

        private static void ApplyUnitDie(int owner, int x, int y)
        {
            var jug = JugFor(owner);
            var u = jug != null ? FindUnit(owner, x, y) : null;
            if (u == null)
            {
                // La unidad ya no existe en esta PC: solo desocupa la casilla.
                SetTileUnit(x, y, false);
                return;
            }
            SetTileUnit(x, y, false);
            if (jug != null) jug.DeleteUnit(u);
            UnityEngine.Object.Destroy(u.gameObject);
        }

        private static void SetTileUnit(int x, int y, bool occupied)
        {
            var board = Tablero.Instance.unidades;
            if (board == null) return;
            if (x < 0 || y < 0 || x >= board.GetLength(0) || y >= board.GetLength(1)) return;
            board[x, y] = occupied;
        }

        // ------------------------------------------------------------------
        //  Appliers de ticks de estado (StartTurn del dueño replicado)
        // ------------------------------------------------------------------

        private static void ApplyCityTick(int owner, int x, int y, BinaryReader r)
        {
            var c = Tablero.Instance.GetCiudad(x, y);
            if (c == null) return; // CITY_FOUNDED llegará/ya llegó: el siguiente tick la sincroniza

            c.Ciudadanos = r.ReadInt32();
            c.ComidaAlmacenada = r.ReadInt32();
            c.Vida = r.ReadSingle();
            c.ProducciónAlmacenada = r.ReadSingle();
            c.ProducciónObjetivo = r.ReadInt32();
            c.FelicidadTotal = r.ReadInt32();
            c.CulturaBase = r.ReadSingle(); c.Cultura = r.ReadSingle();
            c.ProducciónBase = r.ReadInt32(); c.Producción = r.ReadSingle();
            c.FeBase = r.ReadSingle(); c.Fe = r.ReadSingle();
            c.ComidaBase = r.ReadInt32(); c.Comida = r.ReadInt32();
            c.CienciaBase = r.ReadSingle(); c.Ciencia = r.ReadSingle();
            c.DineroBase = r.ReadInt32(); c.Dinero = r.ReadInt32();
            c.InfluenciaBase = r.ReadInt32(); c.Influencia = r.ReadInt32();
            c.Turismo = r.ReadInt32();
            c.Defense = r.ReadInt32();
            c.TienePuerto = r.ReadBoolean(); c.TieneAeropuerto = r.ReadBoolean();
            c.Delegacion = r.ReadBoolean();
            c.modeResources = r.ReadInt32();
            c.myReligionThisTurn = r.ReadInt32();

            string unitName = r.ReadString();
            c.UnitConstruyendo = string.IsNullOrEmpty(unitName)
                ? null : FindUnitScriptableByName(unitName);
            string buildingName = r.ReadString();
            c.BuildingConstruyendo = string.IsNullOrEmpty(buildingName)
                ? null : ResolveBuilding(buildingName);
            string maravName = r.ReadString();
            c.MaravConstruyendo = string.IsNullOrEmpty(maravName)
                ? null : ResolveMaravilla(maravName);

            int maxRel = r.ReadInt32();
            if (c.religiones != null)
            {
                for (int i = 0; i < maxRel && i < c.religiones.Length; i++)
                {
                    int jrel = r.ReadInt32();
                    int jcant = r.ReadInt32();
                    c.religiones[i].jug = jrel;
                    c.religiones[i].cant = jcant;
                }
            }
            else
            {
                for (int i = 0; i < maxRel; i++) { r.ReadInt32(); r.ReadInt32(); }
            }
            c.maxRel = maxRel;

            int edifMax = r.ReadInt32();
            for (int i = 0; i < edifMax && i < c.Edificios.Length; i++)
                c.Edificios[i] = ResolveBuilding(r.ReadString());
            c.EdifMax = Math.Min(edifMax, c.Edificios.Length);

            int maravMax = r.ReadInt32();
            for (int i = 0; i < maravMax && i < c.Maravillas.Length; i++)
                c.Maravillas[i] = ResolveMaravilla(r.ReadString());
            c.MaravMax = Math.Min(maravMax, c.Maravillas.Length);

            try { c.ManageHealthBar(); } catch { }
            AccessTools.Method(typeof(Ciudad), "ManageReligionSymbol")?.Invoke(c, null);
        }

        private static void ApplyJugTick(int slot, BinaryReader r)
        {
            var jug = JugFor(slot);
            if (jug == null || jug.Tecs == null)
            {
                // Consumir el resto del payload aunque el jugador falte
                ConsumeJugTick(r);
                return;
            }
            var gm = GameManager.Instance;

            int tecsLen = r.ReadInt32();
            for (int i = 0; i < tecsLen && i < jug.Tecs.Length; i++)
            {
                float done = r.ReadSingle();
                var t = jug.Tecs[i];
                t.done = done;
                jug.Tecs[i] = t;
            }
            for (int i = tecsLen; i < jug.Tecs.Length; i++) r.ReadSingle(); // seguridad de alineación

            string researchingName = r.ReadString();
            jug.researching = string.IsNullOrEmpty(researchingName)
                ? null : ResolveTechnology(researchingName);

            int maxRes = r.ReadInt32();
            for (int i = 0; i < maxRes; i++)
            {
                string nombre = r.ReadString();
                var tec = ResolveTechnology(nombre);
                if (tec == null) continue;
                bool ya = false;
                for (int j = 0; j < jug.maxRes && j < jug.Researched.Length; j++)
                    if (jug.Researched[j] == tec ||
                        (jug.Researched[j] != null && jug.Researched[j].Nombre == nombre))
                    { ya = true; break; }
                if (ya) continue;

                // Replicación mínima de TecInvestigated (sin UI ni mensajes)
                if (jug.maxRes < jug.Researched.Length)
                    jug.Researched[jug.maxRes++] = tec;
                AccessTools.Method(typeof(Jugador), "AddUnitsAndBuildings")
                    ?.Invoke(jug, new object[] { tec });
                if (jug.Ciudades != null)
                    for (int j = 0; j < jug.Ciudades.Length; j++)
                        if (jug.Ciudades[j] != null) jug.Ciudades[j].Vida += 2f;
                if (tec.permiteEmbarcar) jug.navegar++;
                if (tec.mejora == Tablero.Mejora.Mina) jug.canMina = true;
                if (tec.mejora == Tablero.Mejora.Aserradero) jug.canAserr = true;
                if (tec.mejora == Tablero.Mejora.Pastizal) jug.canPastiz = true;
                if (tec.mejora == Tablero.Mejora.Refineria) jug.canRefin = true;
                if (tec.mejora == Tablero.Mejora.Carretera) jug.canCarre = true;
                if (tec.elemento == Tablero.ElementoExtra.Hierro) jug.veHierro = true;
                if (tec.elemento == Tablero.ElementoExtra.Carbon) jug.veCarbon = true;
                if (tec.elemento == Tablero.ElementoExtra.Petroleo) jug.vePetroleo = true;
                if (tec.elemento == Tablero.ElementoExtra.Uranio) jug.veUranio = true;
                if (tec.TecnologiaFinal) jug.FinTecnologias = true;
            }

            jug.veHierro = r.ReadBoolean(); jug.veCarbon = r.ReadBoolean();
            jug.vePetroleo = r.ReadBoolean(); jug.veUranio = r.ReadBoolean();
            jug.canMina = r.ReadBoolean(); jug.canAserr = r.ReadBoolean();
            jug.canPastiz = r.ReadBoolean(); jug.canRefin = r.ReadBoolean();
            jug.canCarre = r.ReadBoolean();
            jug.navegar = r.ReadInt32();
            jug.FinTecnologias = r.ReadBoolean();
            jug.Bancarrota = r.ReadBoolean();
            jug.maxUIUnidad = r.ReadInt32();
            jug.MiHierro = r.ReadInt32(); jug.MisCaballos = r.ReadInt32();
            jug.MiCarbon = r.ReadInt32(); jug.MiPetroleo = r.ReadInt32();
            jug.MiUranio = r.ReadInt32();
            jug.TurnLeftEpocaDorada = r.ReadInt32();
            jug.NivelEpocaDorada = r.ReadInt32();
            jug.GoldenAgeMultiplier = r.ReadSingle();
            jug.TurnosSuperSaiyan = r.ReadInt32();
            int freeTechs = r.ReadInt32();
            int freeCulture = r.ReadInt32();
            if (gm != null) { gm.FreeTechs = freeTechs; gm.FreeCulture = freeCulture; }
        }

        private static void ConsumeJugTick(BinaryReader r)
        {
            try
            {
                int tecsLen = r.ReadInt32();
                for (int i = 0; i < tecsLen; i++) r.ReadSingle();
                r.ReadString(); // researching
                int maxRes = r.ReadInt32();
                for (int i = 0; i < maxRes; i++) r.ReadString();
                for (int i = 0; i < 9; i++) r.ReadBoolean(); // flags de tech
                r.ReadInt32(); // navegar
                r.ReadBoolean(); r.ReadBoolean(); // FinTecnologias, Bancarrota
                r.ReadInt32(); // maxUIUnidad
                for (int i = 0; i < 5; i++) r.ReadInt32(); // recursos
                r.ReadInt32(); r.ReadInt32(); r.ReadSingle(); // época dorada
                r.ReadInt32(); // saiyan
                r.ReadInt32(); r.ReadInt32(); // freeTechs/FreeCulture
            }
            catch { }
        }

        private static void ApplyCityConquered(int x, int y, int newOwner, int ciudadanos)
        {
            var gm = GameManager.Instance;
            var c = Tablero.Instance.GetCiudad(x, y);
            if (c == null || gm == null) return;
            var nuevo = newOwner >= 0 && newOwner < gm.jugadores.Length ? gm.jugadores[newOwner] : null;

            // La guarnición muere igual que en vanilla (CheckIfDead no emite:
            // estamos Applying).
            var guarnicion = Tablero.Instance.GetUnit(x, y);
            if (guarnicion != null)
            {
                guarnicion.VidaLeft = 0f;
                guarnicion.CheckIfDead(null);
            }

            // La unidad conquistadora ya llegó por UNIT_MOVE (MoveAfterConquer).
            c.myPlayer?.RemoveCity(c);
            c.myPlayer = nuevo;
            nuevo?.AddCity(c);

            var fStar = AccessTools.Field(typeof(Ciudad), "CapitalStar");
            var fOg = AccessTools.Field(typeof(Ciudad), "OgPlayer");
            var star = fStar?.GetValue(c) as UnityEngine.SpriteRenderer;
            var og = fOg?.GetValue(c) as Jugador;
            if (star != null)
                star.enabled = (c.Capital && nuevo == og);

            var fBandera = AccessTools.Field(typeof(Ciudad), "Bandera");
            var fA1 = AccessTools.Field(typeof(Ciudad), "Area1");
            var fA2 = AccessTools.Field(typeof(Ciudad), "Area2");
            var fA3 = AccessTools.Field(typeof(Ciudad), "Area3");
            if (nuevo != null)
            {
                if (fBandera?.GetValue(c) is UnityEngine.SpriteRenderer b) b.color = nuevo.color2;
                if (fA1?.GetValue(c) is UnityEngine.SpriteRenderer a1) a1.color = nuevo.color2;
                if (fA2?.GetValue(c) is UnityEngine.SpriteRenderer a2) a2.color = nuevo.color2;
                if (fA3?.GetValue(c) is UnityEngine.SpriteRenderer a3) a3.color = nuevo.color2;
            }
            c.HideAreas(false);
            c.Vida = c.VidaMax();
            c.Ciudadanos = ciudadanos;
            c.ProducciónObjetivo = 0;
            c.ProducciónAlmacenada = 0f;
            c.BuildingConstruyendo = null;
            c.UnitConstruyendo = null;
            c.MaravConstruyendo = null;
            c.modeResources = 0;
            c.Render(false);
            if (nuevo != null && nuevo.lider == GameSettings.Lideres.Alejandro)
                gm.FreeTechs += 1;
            if (nuevo != null && nuevo.lider == GameSettings.Lideres.Hitler)
                c.FelicidadTotal = 0;
        }

        private static void ApplyCityRazed(int x, int y, int raider, int ciudadanos)
        {
            var gm = GameManager.Instance;
            var c = Tablero.Instance.GetCiudad(x, y);
            if (c == null || gm == null) return;
            var saqueador = raider >= 0 && raider < gm.jugadores.Length ? gm.jugadores[raider] : null;

            var guarnicion = Tablero.Instance.GetUnit(x, y);
            if (guarnicion != null)
            {
                guarnicion.VidaLeft = 0f;
                guarnicion.CheckIfDead(null);
            }
            if (saqueador != null && saqueador.lider == GameSettings.Lideres.Atila)
            {
                saqueador.OroTotal += 50 * ciudadanos;
                saqueador.CulturaTotal += 20 * ciudadanos;
                saqueador.TurismoTotal += 5 * ciudadanos;
                saqueador.InfluenciaTotal += 5 * ciudadanos;
                saqueador.FelicidadTotal += 10 * ciudadanos;
            }
            if (saqueador != null && saqueador.lider == GameSettings.Lideres.NightKing)
                gm.SpawnCaminanteBlanco(saqueador, x, y);
            c.myPlayer?.RemoveCity(c);
            var board = Tablero.Instance.ciudades;
            if (board != null && x >= 0 && y >= 0
                && x < board.GetLength(0) && y < board.GetLength(1))
                board[x, y] = false;
            UnityEngine.Object.Destroy(c.gameObject);
        }

        // ------------------------------------------------------------------
        //  Resolución de assets por nombre (Building/Maravilla/Technology)
        // ------------------------------------------------------------------

        private static Building ResolveBuilding(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return null;
            var gm = GameManager.Instance;
            if (gm?.jugadores == null) return null;
            foreach (var jug in gm.jugadores)
            {
                if (jug == null) continue;
                if (jug.MyEdifs != null)
                    foreach (var b in jug.MyEdifs)
                        if (b != null && b.Nombre == nombre) return b;
                if (jug.Tecs != null)
                    foreach (var t in jug.Tecs)
                    {
                        var tec = t.tec;
                        if (tec?.Edificios == null) continue;
                        foreach (var b in tec.Edificios)
                            if (b != null && b.Nombre == nombre) return b;
                    }
            }
            return null;
        }

        private static Maravilla ResolveMaravilla(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return null;
            var gm = GameManager.Instance;
            if (gm?.jugadores == null) return null;
            if (gm.MaravsDone != null)
                foreach (var m in gm.MaravsDone)
                    if (m != null && m.Nombre == nombre) return m;
            foreach (var jug in gm.jugadores)
            {
                if (jug == null) continue;
                if (jug.MyMaravillas != null)
                    foreach (var m in jug.MyMaravillas)
                        if (m != null && m.Nombre == nombre) return m;
                if (jug.Tecs != null)
                    foreach (var t in jug.Tecs)
                    {
                        var tec = t.tec;
                        if (tec?.Maravillas == null) continue;
                        foreach (var m in tec.Maravillas)
                            if (m != null && m.Nombre == nombre) return m;
                    }
            }
            return null;
        }

        private static Technology ResolveTechnology(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return null;
            var gm = GameManager.Instance;
            if (gm?.jugadores == null) return null;
            foreach (var jug in gm.jugadores)
            {
                if (jug == null || jug.Tecs == null) continue;
                foreach (var t in jug.Tecs)
                    if (t.tec != null && t.tec.Nombre == nombre) return t.tec;
                if (jug.Researched != null)
                    foreach (var t in jug.Researched)
                        if (t != null && t.Nombre == nombre) return t;
            }
            return null;
        }

        private static void ApplyAttackFx(string fxName, int x, int y)
        {
            var prefab = ResolveFxPrefab(fxName);
            if (prefab == null) return;
            UnityEngine.Object.Instantiate(prefab, new Vector2(x, y), Quaternion.identity);
        }

        // --- caches de resolución de prefabs --------------------------------

        private static readonly Dictionary<string, GameObject> _fxCache =
            new Dictionary<string, GameObject>();
        private static FieldInfo[] _unitScriptables;

        private static GameObject ResolveFxPrefab(string fxName)
        {
            if (string.IsNullOrEmpty(fxName)) return null;
            GameObject cached;
            if (_fxCache.TryGetValue(fxName, out cached) && cached != null) return cached;

            foreach (var u in UnityEngine.Object.FindObjectsOfType<Unidad>())
            {
                if (u == null) continue;
                var f = AccessTools.Field(typeof(Unidad), "AttackAnim");
                var g = f?.GetValue(u) as GameObject;
                if (g != null && g.name == fxName)
                {
                    _fxCache[fxName] = g;
                    return g;
                }
            }
            return null;
        }

        private static FieldInfo[] UnitScriptableFields()
        {
            if (_unitScriptables != null) return _unitScriptables;
            var gm = GameManager.Instance;
            var ug = gm != null ? gm.unitGenerator : null;
            if (ug == null) return null;
            var list = new List<FieldInfo>();
            foreach (var f in typeof(UnitGenerator).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                if (typeof(UnitScriptable).IsAssignableFrom(f.FieldType))
                    list.Add(f);
            _unitScriptables = list.ToArray();
            return _unitScriptables;
        }

        /// <summary>Resuelve un UnitScriptable por nombre de prefab (limpia
        /// el sufijo "(Clone)" de los nombres de instancia).</summary>
        private static UnitScriptable FindUnitScriptableByName(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return null;
            var fields = UnitScriptableFields();
            if (fields == null) return null;
            var gm = GameManager.Instance;
            if (gm?.unitGenerator == null) return null;
            string clean = nombre.Replace("(Clone)", "").Trim();
            foreach (var f in fields)
            {
                var sc = f.GetValue(gm.unitGenerator) as UnitScriptable;
                if (sc == null) continue;
                if (sc.Prefab != null && (sc.Prefab.name == nombre || sc.Prefab.name == clean))
                    return sc;
                if (sc.imagen != null && (sc.imagen.name == nombre || sc.imagen.name == clean))
                    return sc;
            }
            return null;
        }

        private static void ApplyUnitSpawn(int owner, int x, int y, int fuerza, int movMax,
            float movLeft, float vidaLeft, bool turno, int buildThings,
            int pactFuerza, int pactBuild, int rel, int tipo, string prefabName)
        {
            // Idempotente: si ya hay una unidad del dueño en esa casilla (eco o
            // replay tras una carga) no duplicar.
            if (FindUnit(owner, x, y) != null) return;

            var jug = JugFor(owner);
            if (jug == null) return;
            var fields = UnitScriptableFields();
            if (fields == null) return;
            var gm = GameManager.Instance;
            if (gm == null || gm.unitGenerator == null) return;

            UnitScriptable match = null;
            int nameMatches = 0;
            foreach (var f in fields)
            {
                var sc = f.GetValue(gm.unitGenerator) as UnitScriptable;
                if (sc == null) continue;
                if ((sc.Prefab != null && sc.Prefab.name == prefabName) ||
                    (sc.imagen != null && sc.imagen.name == prefabName))
                {
                    match = sc;
                    nameMatches++;
                }
            }
            if (match == null || match.Prefab == null)
            {
                // Los nombres de instancia llevan "(Clone)": quitar el sufijo
                // y reintentar antes de rendirse.
                string clean = prefabName != null ? prefabName.Replace("(Clone)", "").Trim() : null;
                if (!string.IsNullOrEmpty(clean))
                {
                    match = null; nameMatches = 0;
                    foreach (var f in fields)
                    {
                        var sc = f.GetValue(gm.unitGenerator) as UnitScriptable;
                        if (sc == null) continue;
                        if ((sc.Prefab != null && sc.Prefab.name == clean) ||
                            (sc.imagen != null && sc.imagen.name == clean))
                        {
                            match = sc;
                            nameMatches++;
                        }
                    }
                }
            }
            if (match == null || match.Prefab == null)
            {
                CivilizameMPPlugin.Log.LogWarning($"[LiveSync] UnidadSpawn: prefab '{prefabName}' no encontrado");
                return;
            }
            // Desambiguación por TIPO: varias unidades pueden compartir nombre
            // de prefab (distintas eras/variantes) y el primer match daba la
            // unidad equivocada. El prefab real lleva su Tipo en el componente.
            if (nameMatches > 1)
            {
                var want = (Unidad.TipoUnidad)tipo;
                var porTipo = match;
                foreach (var f in fields)
                {
                    var sc = f.GetValue(gm.unitGenerator) as UnitScriptable;
                    if (sc == null || sc.Prefab == null) continue;
                    bool nombreCoincide =
                        (sc.Prefab.name == prefabName || (sc.imagen != null && sc.imagen.name == prefabName));
                    if (!nombreCoincide) continue;
                    var comp = sc.Prefab.GetComponent<Unidad>();
                    if (comp != null && comp.Tipo == want) { porTipo = sc; break; }
                }
                if (porTipo != match)
                    CivilizameMPPlugin.Log.LogInfo(
                        $"[LiveSync] UnidadSpawn: '{prefabName}' desambiguado por Tipo={want}");
                match = porTipo;
            }

            gm.unitGenerator.LoadUnit(match.Prefab, x, y, jug, fuerza, movMax,
                movLeft, vidaLeft, turno, buildThings, pactFuerza, pactBuild, rel);
        }
    }
}
