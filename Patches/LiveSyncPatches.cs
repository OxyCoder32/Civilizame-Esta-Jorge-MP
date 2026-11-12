using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    /// <summary>
    /// Parches emisores del canal LIVE (ver Network/LiveSync.cs): enganchan los
    /// métodos de juego que "cuentan la partida" y narran a las demás PCs lo
    /// que el jugador activo está haciendo, en tiempo real:
    ///
    ///  - Unidad.Move / MoveAfterConquer            → UNIT_MOVE.
    ///  - Unidad.CheckIfDead                         → UNIT_DIE.
    ///  - RelationshipManager.Attack / AttackCiudad  → ATTACK_FX + UNIT_HP / CITY_HP.
    ///  - UnitGenerator.CreateUnit/SpawnCaminanteBlanco → UNIT_SPAWN.
    ///  - CityGenerator.NewCity                      → CITY_FOUNDED.
    ///  - TribuManajer.DeleteTribu                   → TRIBU.
    ///  - Jugador.Explore                            → EXPLORE (niebla en vivo).
    ///  - Jugador.StartTurn / SkipTurn               → TURN_START + PLAYER_RES
    ///    (totales de recursos: la PC que corre el turno los reconcilia en las
    ///    demás sin recargar estado).
    ///  - RelationshipManager (diplomacia): ProposeAliance, DisolveAliance,
    ///    BreakAliance, AddPacto, DeclareSurpriseWar, DeclareFormalWar,
    ///    ProposePeace, SetPeace, LaunchPropaganda, AllianceResponseNotif
    ///    → EV_DIPLO. Los targets -1 se RESUELVEN aquí (selLid/realPosLids de
    ///    la PC que actúa) y el applier replaya el método vanilla con el
    ///    TurnOrder del actor. Los avisos siguen siendo de uno o más turnos.
    ///  - RelationshipManager.FormalWarChecker → DIP_FORMAL_WAR_DONE cuando un
    ///    contador de guerra formal llega a 0 (vanilla lo muta sin evento).
    ///  - Jugador.DeleteUnit → UNIT_DIE para bajas NO combativas (Colono al
    ///    fundar, Trabajador al agotarse): antes quedaban unidades fantasma.
    ///  - Unidad.BuildThing → TILE_MEJORA (granja/mina/carretera/...): el
    ///    trabajador escribe la mejora directo en el tablero.
    ///  - TribuManajer.AddTribu → TRIBU_ADD (beneficio ya sorteado).
    ///  - Ciudad.CheckIfConquered → CITY_CONQUERED / CITY_RAZED.
    ///  - Ciudad.AddToRel / InquisicionReligions / ManageReligions → CITY_RELS
    ///    (religión de ciudades ajenas a mitad del turno del misionero).
    ///  - Unidad.Convert → UNIT_REL por unidad convertida (diff 3x3).
    ///  - Ciudad.StartTurn → CITY_TICK: snapshot del estado persistente de la
    ///    ciudad (producción, crecimiento, edificios, maravillas, religiones)
    ///    que vanilla solo muta en la PC del dueño.
    ///  - Jugador.StartTurn → JUG_TICK: progreso/completado de techs, época
    ///    dorada, recursos sueltos, techs gratis.
    ///  - GameManager.DeletePlayer → PLAYER_DEAD (jugador eliminado).
    ///
    /// Todas las emisiones pasan por LiveSync.MayEmit(): quien narra es la PC
    /// que ACTÚA (host para sus turnos/IA; cliente durante su turno). Los
    /// appliers de LiveSync ponen LiveSync.Applying=true para que estas
    /// emisiones no repliquen lo que solo se está pintando (eco).
    /// </summary>
    [HarmonyPatch]
    public static class LiveSyncPatches
    {
        private static int OwnerOf(Unidad u)
        {
            var gm = GameManager.Instance;
            if (u == null || u.myPlayer == null || gm == null || gm.jugadores == null) return -1;
            for (int i = 0; i < gm.jugadores.Length; i++)
                if (gm.jugadores[i] == u.myPlayer) return i;
            return -1;
        }

        // Codifica (x,y) en un int para estados de Harmony: x*100000 + y
        private const int NO_STATE = -1000;
        private const ulong NO_STATE_UL = ulong.MaxValue;
        // Empaqueta (guardAntes, actor, target) de los métodos de alianza.
        // guardAntes + 5000 evita negativos (TurnsTryAlliance arranca en -1).
        private const int GUARD_OFFSET = 5000;

        private static ulong PackAlliance(int guardBefore, int act, int target)
            => ((ulong)(uint)(guardBefore + GUARD_OFFSET) << 32)
             | ((ulong)(uint)act << 16)
             | (ulong)(uint)target;

        private static void UnpackAlliance(ulong state, out int guardBefore, out int act, out int target)
        {
            guardBefore = (int)(state >> 32) - GUARD_OFFSET;
            act = (int)((state >> 16) & 0xFFFF);
            target = (int)(state & 0xFFFF);
        }

        // ---------------- Unidad.Move / MoveAfterConquer ----------------

        [HarmonyPatch(typeof(Unidad), "Move")]
        [HarmonyPrefix]
        public static void MovePrefix(Unidad __instance, out int __state)
        {
            __state = NO_STATE;
            if (!LiveSync.MayEmit()) return;
            __state = __instance.posX * 100000 + __instance.posY;
        }

        [HarmonyPatch(typeof(Unidad), "Move")]
        [HarmonyPostfix]
        public static void MovePostfix(Unidad __instance, int __state)
        {
            if (__state == NO_STATE) return;
            if (!LiveSync.MayEmit()) return;
            int fx = __state / 100000;
            int fy = __state - fx * 100000;
            if (fx == __instance.posX && fy == __instance.posY) return; // no se movió
            LiveSync.SendUnitMove(__instance, fx, fy);
        }

        [HarmonyPatch(typeof(Unidad), "MoveAfterConquer")]
        [HarmonyPrefix]
        public static void MoveAfterConquerPrefix(Unidad __instance, out int __state)
        {
            __state = NO_STATE;
            if (!LiveSync.MayEmit()) return;
            __state = __instance.posX * 100000 + __instance.posY;
        }

        [HarmonyPatch(typeof(Unidad), "MoveAfterConquer")]
        [HarmonyPostfix]
        public static void MoveAfterConquerPostfix(Unidad __instance, int __state)
        {
            if (__state == NO_STATE) return;
            if (!LiveSync.MayEmit()) return;
            int fx = __state / 100000;
            int fy = __state - fx * 100000;
            if (fx == __instance.posX && fy == __instance.posY) return;
            LiveSync.SendUnitMove(__instance, fx, fy);
        }

        // ---------------- Unidad.CheckIfDead ----------------

        [HarmonyPatch(typeof(Unidad), "CheckIfDead")]
        [HarmonyPrefix]
        public static void CheckIfDeadPrefix(Unidad __instance, out ulong __state)
        {
            __state = NO_STATE_UL;
            if (!LiveSync.MayEmit()) return;
            if (__instance.VidaLeft <= 0f)
            {
                int owner = OwnerOf(__instance);
                if (owner >= 0)
                    __state = ((ulong)owner << 40) | ((ulong)(uint)__instance.posX << 20) | (ulong)(uint)__instance.posY;
            }
        }

        [HarmonyPatch(typeof(Unidad), "CheckIfDead")]
        [HarmonyPostfix]
        public static void CheckIfDeadPostfix(Unidad __instance, ulong __state)
        {
            if (__state == NO_STATE_UL) return;
            if (!LiveSync.MayEmit()) return;
            int owner = (int)(__state >> 40);
            int x = (int)((__state >> 20) & 0xFFFFF);
            int y = (int)(__state & 0xFFFFF);

            // Las resurrecciones vanilla (NightKing reviviendo slow/convertido,
            // Moctezuma) dejan la unidad viva o con otro dueño: NO es muerte.
            if (__instance.VidaLeft > 0f) return;
            if (OwnerOf(__instance) != owner) return;
            LiveSync.SendUnitDie(__instance, owner, x, y);
        }

        // ---------------- Jugador.DeleteUnit (bajas NO combativas) ------------

        // Vanilla borra unidades con DeleteUnit + Object.Destroy DIRECTO en
        // varios sitios y NINGUNO pasaba por CheckIfDead: el Colono al fundar
        // ciudad (Unidad.Build), el Trabajador al agotar sus BuildThings
        // (Unidad.BuildThing)… En las demás copias la unidad quedaba fantasma
        // para siempre. CheckIfDead cubre las muertes por COMBATE (emite él);
        // aquí solo emitimos bajas con vida intacta para no duplicar.
        [HarmonyPatch(typeof(Jugador), "DeleteUnit")]
        [HarmonyPostfix]
        public static void DeleteUnitPostfix(Jugador __instance, Unidad unit)
        {
            if (unit == null) return;
            if (unit.VidaLeft <= 0f) return; // combate → CheckIfDead ya emite
            if (!LiveSync.MayEmit()) return;
            int owner = OwnerOf(unit);
            if (owner < 0) return;
            LiveSync.SendUnitDie(unit, owner, unit.posX, unit.posY);
        }

        // ---------------- RelationshipManager.Attack / AttackCiudad ----------------

        private static GameObject AttackFxPrefab(Unidad att)
        {
            var field = AccessTools.Field(typeof(Unidad), "AttackAnim");
            var go = field?.GetValue(att) as GameObject;
            return go;
        }

        [HarmonyPatch(typeof(RelationshipManager), "Attack")]
        [HarmonyPostfix]
        public static void AttackPostfix(Unidad Att, Unidad Def)
        {
            if (!LiveSync.MayEmit()) return;
            if (Att == null || Def == null) return;
            var fx = AttackFxPrefab(Att);
            if (fx != null)
                LiveSync.SendAttackFx(fx, Def.posX, Def.posY);
            LiveSync.SendUnitHp(Def);
            LiveSync.SendUnitHp(Att);
        }

        [HarmonyPatch(typeof(RelationshipManager), "AttackCiudad")]
        [HarmonyPostfix]
        public static void AttackCiudadPostfix(Unidad Att, Ciudad Def)
        {
            if (!LiveSync.MayEmit()) return;
            if (Att == null || Def == null) return;
            var fx = AttackFxPrefab(Att);
            if (fx != null)
                LiveSync.SendAttackFx(fx, Def.posX, Def.posY);
            LiveSync.SendUnitHp(Att);
            LiveSync.SendCityHp(Def, Def.posX, Def.posY);
        }

        // ---------------- Jugador.StartTurn (TURN_START / recursos en vivo) --

        [HarmonyPatch(typeof(Jugador), "StartTurn")]
        [HarmonyPostfix]
        public static void StartTurnPostfix()
        {
            // Emite el arranque de cada turno que ESTA PC dirige (host: host+IA;
            // cliente: su turno) y los totales de recursos del jugador activo
            // (los ingresos del StartTurn convergen en las demás copias).
            var gm = GameManager.Instance;
            if (gm == null) return;
            TurnController.OnStartTurnRan(gm.TurnOrder);
            LiveSync.SendTurnStart(gm.TurnOrder);
            var jug = gm.Jug();
            if (jug != null) LiveSync.SendRes(jug);
        }

        // ---------------- Jugador.SkipTurn (recursos al cerrar turno) --------

        [HarmonyPatch(typeof(Jugador), "SkipTurn")]
        [HarmonyPostfix]
        public static void SkipTurnPostfix(Jugador __instance)
        {
            // Totales al cerrar el turno (compras, ediciones y demás cambios
            // hechos durante el turno) para que el resto de copias converja.
            LiveSync.SendRes(__instance);
        }

        // ---------------- RelationshipManager: diplomacia en vivo ------------

        private static FieldInfo _mUIField;

        /// <summary>Resuelve el target -1 de los métodos de alianza con el UI
        /// de la PC que ACTÚA (el receptor no tiene ese UI seleccionado).</summary>
        private static int ResolveTarget(RelationshipManager rm, int i)
        {
            if (i != -1) return i;
            if (_mUIField == null)
                _mUIField = AccessTools.Field(typeof(RelationshipManager), "mUI");
            var ui = _mUIField?.GetValue(rm) as RelationshipUI;
            if (ui == null || ui.realPosLids == null) return -1;
            if (ui.selLid < 0 || ui.selLid >= ui.realPosLids.Length) return -1;
            return ui.realPosLids[ui.selLid];
        }

        private static bool ValidJug(int j)
        {
            var gm = GameManager.Instance;
            return gm != null && gm.jugadores != null && j >= 0 && j < gm.jugadores.Length;
        }

        // ProposeAliance(i): vanilla usa TurnOrder como ACTOR. Capturamos el
        // target resuelto y el valor del guard para emitir SOLO si el método
        // realmente hizo algo (el applier replaya el mismo método vanilla).
        [HarmonyPatch(typeof(RelationshipManager), "ProposeAliance")]
        [HarmonyPrefix]
        public static void ProposeAliancePrefix(RelationshipManager __instance, int i, out ulong __state)
        {
            __state = NO_STATE_UL;
            if (!LiveSync.MayEmit()) return;
            var gm = GameManager.Instance;
            if (gm == null) return;
            int target = ResolveTarget(__instance, i);
            if (!ValidJug(target)) return;
            __state = PackAlliance(__instance.TurnsTryAlliance[gm.TurnOrder, target], gm.TurnOrder, target);
        }

        [HarmonyPatch(typeof(RelationshipManager), "ProposeAliance")]
        [HarmonyPostfix]
        public static void ProposeAliancePostfix(RelationshipManager __instance, ulong __state)
        {
            if (__state == NO_STATE_UL) return;
            if (!LiveSync.MayEmit()) return;
            int before, act, target;
            UnpackAlliance(__state, out before, out act, out target);
            if (__instance.TurnsTryAlliance[act, target] == before) return; // vanilla no hizo nada
            LiveSync.SendDiploma(LiveSync.DIP_PROPOSE_ALLIANCE, act, target, 0, false);
        }

        [HarmonyPatch(typeof(RelationshipManager), "DisolveAliance")]
        [HarmonyPrefix]
        public static void DisolveAliancePrefix(RelationshipManager __instance, int i, out ulong __state)
        {
            __state = NO_STATE_UL;
            if (!LiveSync.MayEmit()) return;
            var gm = GameManager.Instance;
            if (gm == null) return;
            int target = ResolveTarget(__instance, i);
            if (!ValidJug(target)) return;
            __state = PackAlliance(__instance.TurnsTryAlliance[gm.TurnOrder, target], gm.TurnOrder, target);
        }

        [HarmonyPatch(typeof(RelationshipManager), "DisolveAliance")]
        [HarmonyPostfix]
        public static void DisolveAliancePostfix(RelationshipManager __instance, ulong __state)
        {
            if (__state == NO_STATE_UL) return;
            if (!LiveSync.MayEmit()) return;
            int before, act, target;
            UnpackAlliance(__state, out before, out act, out target);
            if (__instance.TurnsTryAlliance[act, target] == before) return;
            LiveSync.SendDiploma(LiveSync.DIP_DISOLVE_ALLIANCE, act, target, 0, false);
        }

        [HarmonyPatch(typeof(RelationshipManager), "BreakAliance")]
        [HarmonyPrefix]
        public static void BreakAliancePrefix(RelationshipManager __instance, int i, out ulong __state)
        {
            __state = NO_STATE_UL;
            if (!LiveSync.MayEmit()) return;
            var gm = GameManager.Instance;
            if (gm == null) return;
            int target = ResolveTarget(__instance, i);
            if (!ValidJug(target)) return;
            __state = PackAlliance(__instance.TurnsTryAlliance[gm.TurnOrder, target], gm.TurnOrder, target);
        }

        [HarmonyPatch(typeof(RelationshipManager), "BreakAliance")]
        [HarmonyPostfix]
        public static void BreakAliancePostfix(RelationshipManager __instance, ulong __state)
        {
            if (__state == NO_STATE_UL) return;
            if (!LiveSync.MayEmit()) return;
            int before, act, target;
            UnpackAlliance(__state, out before, out act, out target);
            if (__instance.TurnsTryAlliance[act, target] == before) return;
            LiveSync.SendDiploma(LiveSync.DIP_BREAK_ALLIANCE, act, target, 0, false);
        }

        // AddPacto: sin guard vanilla: siempre emite (el applier replaya igual).
        [HarmonyPatch(typeof(RelationshipManager), "AddPacto")]
        [HarmonyPostfix]
        public static void AddPactoPostfix(int j1, int j2, int p, bool accept)
        {
            if (!LiveSync.MayEmit()) return;
            if (!ValidJug(j1) || !ValidJug(j2)) return;
            LiveSync.SendDiploma(LiveSync.DIP_ADD_PACTO, j1, j2, p, accept);
        }

        [HarmonyPatch(typeof(RelationshipManager), "DeclareSurpriseWar")]
        [HarmonyPostfix]
        public static void DeclareSurpriseWarPostfix(int jug1, int jug2)
        {
            if (!LiveSync.MayEmit()) return;
            if (!ValidJug(jug1) || !ValidJug(jug2)) return;
            LiveSync.SendDiploma(LiveSync.DIP_SURPRISE_WAR, jug1, jug2, 0, false);
        }

        [HarmonyPatch(typeof(RelationshipManager), "DeclareFormalWar")]
        [HarmonyPostfix]
        public static void DeclareFormalWarPostfix(int jug1, int jug2)
        {
            if (!LiveSync.MayEmit()) return;
            if (!ValidJug(jug1) || !ValidJug(jug2)) return;
            LiveSync.SendDiploma(LiveSync.DIP_FORMAL_WAR, jug1, jug2, 0, false);
        }

        // FormalWarChecker: la guerra formal se CONCRETA cuando su contador
        // (YearsToStartWar) llega a 0 — vanilla lo hace dentro del StartTurn de
        // CADA PC y muta PlayersRelations a Guerra SIN evento: el resto de
        // copias nunca se enteraba ("las guerras no se sincronizan"). Capturamos
        // la fila del contador antes y emitimos cada casilla que pasó a -1.
        [HarmonyPatch(typeof(RelationshipManager), "FormalWarChecker")]
        [HarmonyPrefix]
        public static void FormalWarCheckerPrefix(RelationshipManager __instance, int jug, out int[] __state)
        {
            __state = null;
            if (!ValidJug(jug)) return;
            var yrs = __instance.YearsToStartWar;
            if (yrs == null) return;
            int n = yrs.GetLength(0);
            var fila = new int[n];
            for (int i = 0; i < n; i++) fila[i] = yrs[jug, i];
            __state = fila;
        }

        [HarmonyPatch(typeof(RelationshipManager), "FormalWarChecker")]
        [HarmonyPostfix]
        public static void FormalWarCheckerPostfix(RelationshipManager __instance, int jug, int[] __state)
        {
            if (__state == null || !LiveSync.MayEmit()) return;
            var yrs = __instance.YearsToStartWar;
            if (yrs == null) return;
            for (int i = 0; i < __state.Length; i++)
            {
                // Pasó de "contando" a "concretada" (o arrastrada por alianza):
                if (__state[i] != -1 && ValidJug(i) && yrs[jug, i] == -1)
                    LiveSync.SendDiploma(LiveSync.DIP_FORMAL_WAR_DONE, jug, i, 0, false);
            }
        }

        // ProposePeace tiene guard (NightKing / propuesta ya enviada): capturamos
        // ProposePeaceNotif[j2, jug1] para emitir solo si cambió.
        [HarmonyPatch(typeof(RelationshipManager), "ProposePeace")]
        [HarmonyPrefix]
        public static void ProposePeacePrefix(RelationshipManager __instance, int jug1, int jug2, out bool __state)
        {
            __state = false;
            if (!LiveSync.MayEmit()) return;
            if (!ValidJug(jug1) || !ValidJug(jug2)) return;
            __state = __instance.ProposePeaceNotif[jug2, jug1];
        }

        [HarmonyPatch(typeof(RelationshipManager), "ProposePeace")]
        [HarmonyPostfix]
        public static void ProposePeacePostfix(RelationshipManager __instance, int jug1, int jug2, bool __state)
        {
            if (!LiveSync.MayEmit()) return;
            if (!ValidJug(jug1) || !ValidJug(jug2)) return;
            if (__instance.ProposePeaceNotif[jug2, jug1] == __state) return;
            LiveSync.SendDiploma(LiveSync.DIP_PROPOSE_PEACE, jug1, jug2, 0, false);
        }

        [HarmonyPatch(typeof(RelationshipManager), "SetPeace")]
        [HarmonyPostfix]
        public static void SetPeacePostfix(int j1, int j2)
        {
            if (!LiveSync.MayEmit()) return;
            if (!ValidJug(j1) || !ValidJug(j2)) return;
            LiveSync.SendDiploma(LiveSync.DIP_SET_PEACE, j1, j2, 0, false);
        }

        [HarmonyPatch(typeof(RelationshipManager), "LaunchPropaganda")]
        [HarmonyPostfix]
        public static void LaunchPropagandaPostfix(int j1, int j2)
        {
            if (!LiveSync.MayEmit()) return;
            if (!ValidJug(j1) || !ValidJug(j2)) return;
            LiveSync.SendDiploma(LiveSync.DIP_PROPAGANDA, j1, j2, 0, false);
        }

        [HarmonyPatch(typeof(RelationshipManager), "AllianceResponseNotif")]
        [HarmonyPostfix]
        public static void AllianceResponseNotifPostfix(int a, int b, bool aux)
        {
            if (!LiveSync.MayEmit()) return;
            if (!ValidJug(a) || !ValidJug(b)) return;
            LiveSync.SendDiploma(LiveSync.DIP_ALLIANCE_RESPONSE, a, b, 0, aux);
        }

        // ---------------- Jugador.Explore (niebla en vivo) ----------------

        [HarmonyPatch(typeof(Jugador), "Explore")]
        [HarmonyPostfix]
        public static void ExplorePostfix(Jugador __instance, int x, int y, int vision, bool loading)
        {
            // La Visión del jugador es estado real (viaja en el .jue): con este
            // evento el mapa descubierto evoluciona en directo entre turnos. El
            // reload de carga (loading=true) lo lleva el handoff completo.
            if (loading) return;
            LiveSync.SendExplore(__instance, x, y, vision);
        }

        // ---------------- Unidad.Heal (HP al cerrar turno) ----------------

        [HarmonyPatch(typeof(Unidad), "Heal")]
        [HarmonyPostfix]
        public static void HealPostfix(Unidad __instance)
        {
            // La curación de fin de turno ocurre en la PC que cierra el turno
            // (Jugador.SkipTurn) y no la ve nadie más: sin este evento el HP
            // curado se revertiría con el estado del servidor en la ronda
            // siguiente.
            if (!LiveSync.MayEmit()) return;
            LiveSync.SendUnitHp(__instance);
        }

        // ---------------- UnitGenerator.CreateUnit / SpawnCaminanteBlanco ----------------

        private static void EmitSpawnFromAuxiliar(UnitGenerator __instance)
        {
            if (!LiveSync.MayEmit()) return;
            var auxField = AccessTools.Field(typeof(UnitGenerator), "Auxiliar");
            var go = auxField?.GetValue(__instance) as GameObject;
            if (go == null) return;
            var u = go.GetComponent<Unidad>();
            if (u == null) return;
            LiveSync.SendUnitSpawn(u);
        }

        [HarmonyPatch(typeof(UnitGenerator), "CreateUnit")]
        [HarmonyPostfix]
        public static void CreateUnitPostfix(UnitGenerator __instance)
        {
            EmitSpawnFromAuxiliar(__instance);
        }

        [HarmonyPatch(typeof(UnitGenerator), "SpawnCaminanteBlanco")]
        [HarmonyPostfix]
        public static void SpawnCaminanteBlancoPostfix(UnitGenerator __instance)
        {
            EmitSpawnFromAuxiliar(__instance);
        }

        // ---------------- CityGenerator.NewCity ----------------

        [HarmonyPatch(typeof(CityGenerator), "NewCity")]
        [HarmonyPostfix]
        public static void NewCityPostfix(Jugador player, int x, int y, string name, bool capital, int rel)
        {
            if (!LiveSync.MayEmit()) return;
            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null || player == null) return;
            int owner = -1;
            for (int i = 0; i < gm.jugadores.Length; i++)
                if (gm.jugadores[i] == player) { owner = i; break; }
            if (owner < 0) return;
            LiveSync.SendCityFounded(owner, x, y, name, capital, rel);
        }

        // ---------------- TribuManajer.DeleteTribu ----------------

        [HarmonyPatch(typeof(TribuManajer), "DeleteTribu")]
        [HarmonyPrefix]
        public static void DeleteTribuPrefix(TribuManajer __instance, int pos, out int __state)
        {
            __state = NO_STATE;
            if (!LiveSync.MayEmit()) return;
            if (pos < 0 || pos >= __instance.tribus.Length) return;
            int x = __instance.tribus[pos].posX;
            int y = __instance.tribus[pos].posY;
            if (x >= 0) __state = x * 100000 + y;
        }

        [HarmonyPatch(typeof(TribuManajer), "DeleteTribu")]
        [HarmonyPostfix]
        public static void DeleteTribuPostfix(int __state)
        {
            if (__state == NO_STATE) return;
            if (!LiveSync.MayEmit()) return;
            int x = __state / 100000;
            int y = __state - x * 100000;
            LiveSync.SendTribuTaken(x, y);
        }

        // ---------------- TribuManajer.AddTribu (spawn de tribus) --------------

        // Las tribus aparecen en runtime (regeneración bárbara): solo la PC que
        // la crea la tenía. El beneficio se sortea ALLÍ (tipe=99 → Random) así
        // que enviamos el valor ya sorteado, no el parámetro.
        [HarmonyPatch(typeof(TribuManajer), "AddTribu")]
        [HarmonyPrefix]
        public static void AddTribuPrefix(TribuManajer __instance, out int __state)
        {
            var f = AccessTools.Field(typeof(TribuManajer), "maxTribus");
            __state = f != null ? (int)(f.GetValue(__instance) ?? 0) : 0;
        }

        [HarmonyPatch(typeof(TribuManajer), "AddTribu")]
        [HarmonyPostfix]
        public static void AddTribuPostfix(TribuManajer __instance, int x, int y, int __state)
        {
            var f = AccessTools.Field(typeof(TribuManajer), "maxTribus");
            int maxAhora = f != null ? (int)(f.GetValue(__instance) ?? 0) : 0;
            if (__state == maxAhora) return; // no se añadió nada
            if (!LiveSync.MayEmit()) return;
            int last = maxAhora - 1;
            if (last < 0 || last >= __instance.tribus.Length) return;
            int benef = (int)__instance.tribus[last].Benef;
            LiveSync.SendTribuAdd(x, y, benef);
        }

        // ---------------- Unidad.BuildThing (mejoras de casilla) ---------------

        // El trabajador escribe _mejora/Carretera DIRECTO en el tablero (granja,
        // mina, aserradero, carretera...): en las demás copias la casilla quedaba
        // sin mejora para siempre. Solo este método toca el tablero en runtime.
        [HarmonyPatch(typeof(Unidad), "BuildThing")]
        [HarmonyPrefix]
        public static void BuildThingPrefix(Unidad __instance, out ulong __state)
        {
            __state = NO_STATE_UL;
            if (!LiveSync.MayEmit()) return;
            int x = __instance.posX, y = __instance.posY;
            var board = Tablero.Instance.tablero;
            if (board == null || x < 0 || y < 0 || x >= board.GetLength(0) || y >= board.GetLength(1)) return;
            ulong mej = ((ulong)(uint)board[x, y].posX << 40)
                      | ((ulong)(uint)board[x, y].posY << 20)
                      | (ulong)(uint)(int)board[x, y]._mejora;
            if (board[x, y].Carretera) mej |= 1UL << 62;
            __state = mej;
        }

        [HarmonyPatch(typeof(Unidad), "BuildThing")]
        [HarmonyPostfix]
        public static void BuildThingPostfix(Unidad __instance, ulong __state)
        {
            if (__state == NO_STATE_UL) return;
            if (!LiveSync.MayEmit()) return;
            int x = (int)((__state >> 40) & 0xFFFFF);
            int y = (int)((__state >> 20) & 0xFFFFF);
            var board = Tablero.Instance.tablero;
            if (board == null || x < 0 || y < 0 || x >= board.GetLength(0) || y >= board.GetLength(1)) return;
            bool carreteraAntes = (__state & (1UL << 62)) != 0;
            var mejoraAntes = (Tablero.Mejora)(int)(__state & 0xFFFFF);
            if (board[x, y]._mejora == mejoraAntes && board[x, y].Carretera == carreteraAntes)
                return; // no construyó nada
            LiveSync.SendTileMejora(x, y, board[x, y]._mejora, board[x, y].Carretera);
        }

        // ---------------- Ciudad.StartTurn (tick de ciudad) --------------------

        // Producción, crecimiento, edificios, maravillas, religión: TODO eso se
        // decide en el StartTurn de la ciudad y solo en la PC del dueño. Un
        // snapshot compacto por turno converge las copias (el snapshot es
        // completo, no diff: ~100 B por ciudad comprimidos).
        [HarmonyPatch(typeof(Ciudad), "StartTurn")]
        [HarmonyPostfix]
        public static void CityStartTurnPostfix(Ciudad __instance)
        {
            if (!LiveSync.MayEmit()) return;
            LiveSync.SendCityTick(__instance);
        }

        // ---------------- Jugador.StartTurn (tick de jugador) ------------------

        // Progreso de techs, techs completadas, época dorada, recursos sueltos.
        // (SendRes ya envía los totales de oro/cultura/etc. en el mismo postfix.)
        [HarmonyPatch(typeof(Jugador), "StartTurn")]
        [HarmonyPostfix]
        public static void JugStartTurnPostfix(Jugador __instance)
        {
            if (!LiveSync.MayEmit()) return;
            LiveSync.SendJugTick(__instance);
        }

        // ---------------- Ciudad.CheckIfConquered (conquista/arrasado) ----------

        [HarmonyPatch(typeof(Ciudad), "CheckIfConquered")]
        [HarmonyPrefix]
        public static void CheckIfConqueredPrefix(Ciudad __instance, Unidad a, out int[] __state)
        {
            __state = null;
            if (!LiveSync.MayEmit()) return;
            if (a == null || a.myPlayer == null) return;
            int raider = -1;
            var gm = GameManager.Instance;
            if (gm?.jugadores != null)
                for (int i = 0; i < gm.jugadores.Length; i++)
                    if (gm.jugadores[i] == a.myPlayer) { raider = i; break; }
            if (raider < 0) return;
            __state = new int[] { raider, __instance.posX, __instance.posY, __instance.Ciudadanos };
        }

        [HarmonyPatch(typeof(Ciudad), "CheckIfConquered")]
        [HarmonyPostfix]
        public static void CheckIfConqueredPostfix(Ciudad __instance, int[] __state)
        {
            if (__state == null || !LiveSync.MayEmit()) return;
            int raider = __state[0], x = __state[1], y = __state[2];
            var board = Tablero.Instance.ciudades;
            if (board == null || x < 0 || y < 0 || x >= board.GetLength(0) || y >= board.GetLength(1)) return;
            if (!board[x, y])
            {
                // Arrasada (Atila/NightKing): la ciudad ya no existe en el
                // tablero del emisor.
                LiveSync.SendCityRazed(x, y, raider, __state[3]);
            }
            else
            {
                LiveSync.SendCityConquered(x, y, raider, __instance.Ciudadanos);
            }
        }

        // ---------------- Ciudad: religiones (AddToRel / Inquisición) -----------

        // Misioneros/profetas/inquisidores mutan religiones de ciudades AJENAS
        // a mitad de SU turno: un hook por método con snapshot completo es más
        // robusto que replicar el cálculo de cantidades.
        [HarmonyPatch(typeof(Ciudad), "AddToRel")]
        [HarmonyPostfix]
        public static void AddToRelPostfix(Ciudad __instance, int jug, int cant)
        {
            if (!LiveSync.MayEmit()) return;
            LiveSync.SendCityRels(__instance);
        }

        [HarmonyPatch(typeof(Ciudad), "InquisicionReligions")]
        [HarmonyPostfix]
        public static void InquisicionReligionsPostfix(Ciudad __instance, int rel, float percentage)
        {
            if (!LiveSync.MayEmit()) return;
            LiveSync.SendCityRels(__instance);
        }

        [HarmonyPatch(typeof(Ciudad), "ManageReligions")]
        [HarmonyPostfix]
        public static void ManageReligionsPostfix(Ciudad __instance)
        {
            if (!LiveSync.MayEmit()) return;
            LiveSync.SendCityRels(__instance);
        }

        // ---------------- Unidad.Convert (conversión de unidades) --------------

        // El misionero/profeta cambia myRel de las unidades vecinas: diff del
        // bloque 3x3 antes/después.
        [HarmonyPatch(typeof(Unidad), "Convert")]
        [HarmonyPrefix]
        public static void ConvertPrefix(Unidad __instance, out List<int[]> __state)
        {
            __state = null;
            if (!LiveSync.MayEmit()) return;
            __state = SnapshotRels(__instance);
        }

        [HarmonyPatch(typeof(Unidad), "Convert")]
        [HarmonyPostfix]
        public static void ConvertPostfix(Unidad __instance, List<int[]> __state)
        {
            if (__state == null || !LiveSync.MayEmit()) return;
            var ahora = SnapshotRels(__instance);
            foreach (var pre in __state)
            {
                foreach (var post in ahora)
                {
                    if (pre[0] == post[0] && pre[1] == post[1] && pre[2] == post[2]
                        && pre[3] != post[3])
                        LiveSync.SendUnitRel(pre[0], pre[1], pre[2], post[3]);
                }
            }
        }

        private static List<int[]> SnapshotRels(Unidad centro)
        {
            var list = new List<int[]>();
            var board = Tablero.Instance.tablero;
            var units = Tablero.Instance.unidades;
            if (board == null || units == null) return list;
            int cx = centro.posX, cy = centro.posY;
            int w = board.GetLength(0), h = board.GetLength(1);
            for (int i = -1; i <= 1; i++)
            {
                for (int j = -1; j <= 1; j++)
                {
                    if (i == 0 && j == 0) continue;
                    int x = cx + i, y = cy + j;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    if (!units[x, y]) continue;
                    var u = Tablero.Instance.GetUnit(x, y);
                    if (u == null || u.myPlayer == null) continue;
                    int owner = OwnerOf(u);
                    if (owner < 0) continue;
                    list.Add(new int[] { owner, x, y, u.myRel });
                }
            }
            return list;
        }

        // ---------------- Compras instantáneas (TryBuy / Monopoly) -------------

        // Comprar con oro completa unidades (CreateUnit ✓) o AÑADE EDIFICIOS
        // inline: sin evento, la copia ajena no veía el edificio hasta el
        // siguiente tick. Un snapshot de ciudad al momento lo converge.
        [HarmonyPatch(typeof(Ciudad), "TryBuy")]
        [HarmonyPostfix]
        public static void TryBuyPostfix(Ciudad __instance)
        {
            if (!LiveSync.MayEmit()) return;
            LiveSync.SendCityTick(__instance);
        }

        [HarmonyPatch(typeof(Ciudad), "MonopolyTryBuy")]
        [HarmonyPostfix]
        public static void MonopolyTryBuyPostfix(Ciudad __instance)
        {
            if (!LiveSync.MayEmit()) return;
            LiveSync.SendCityTick(__instance);
        }

        // ---------------- GameManager.DeletePlayer (jugador eliminado) ----------

        [HarmonyPatch(typeof(GameManager), "DeletePlayer")]
        [HarmonyPrefix]
        public static void DeletePlayerPrefix(GameManager __instance, Jugador jug, out int __state)
        {
            __state = -1;
            if (!LiveSync.MayEmit()) return;
            if (jug == null || __instance.jugadores == null) return;
            for (int i = 0; i < __instance.jugadores.Length; i++)
                if (__instance.jugadores[i] == jug) { __state = i; break; }
        }

        [HarmonyPatch(typeof(GameManager), "DeletePlayer")]
        [HarmonyPostfix]
        public static void DeletePlayerPostfix(int __state)
        {
            if (__state < 0 || !LiveSync.MayEmit()) return;
            LiveSync.SendPlayerDead(__state);
        }
    }
}
