using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using CivilizameMP.Core;
using CivilizameMP.UI;

namespace CivilizameMP.Network
{
    /// <summary>
    /// Orquestador de la cadena de turnos:
    ///
    ///  - HOST: cuando en su partida vanilla un turno va a empezar en el slot de un
    ///    cliente humano, captura el estado y se lo envía. Cuando el cliente le
    ///    devuelve su turno terminado, valida (turno = su slot, y que se base en el
    ///    último estado enviado) y restaura ese estado con la recarga vanilla de
    ///    guardado. Tras la restauración el propio juego avanza: la IA se juega sus
    ///    turnos vanilla, y cuando el siguiente turno es de otro humano se repite el
    ///    envío; si el turno es del propio host, juega como cliente normal.
    ///
    ///  - CLIENTE: recibe el estado que corresponde a SU slot, lo carga y juega su
    ///    turno. Al terminar (salto de turno o fin normal) captura su estado y lo
    ///    envía al host. El paso intermedio avanza vanilla en local ("modo espejo").
    /// </summary>
    public static class TurnController
    {
        /// <summary>Última seq de estado enviada a cada cliente (control de estados).</summary>
        private static readonly Dictionary<int, int> _lastSeqSentTo = new Dictionary<int, int>();
        /// <summary>Último estado enviado a cada cliente (para reenviar si manda algo antiguo).</summary>
        private static readonly Dictionary<int, HandoffMessage> _lastPayloadSentTo = new Dictionary<int, HandoffMessage>();
        /// <summary>Firma del última handoff procesado por emisor (anti-duplicados de Photon).</summary>
        private static readonly Dictionary<int, string> _lastProcessedFrom = new Dictionary<int, string>();

        /// <summary>
        /// CLIENTE: true tras enviar su turno terminado. Su copia local queda
        /// congelada (cero simulación: ni IA ni turnos intermedios) hasta que el
        /// host le envíe el siguiente estado. La IA corre SOLO en el host.
        /// </summary>
        public static bool ClientFrozen;

        public static void Reset()
        {
            _lastSeqSentTo.Clear();
            _lastPayloadSentTo.Clear();
            _lastProcessedFrom.Clear();
            ClientFrozen = false;
            StateHandoff.PendingSaveLabel = -1;
            StateHandoff.InsideRestore = false;
            StateHandoff.VanillaAutosaveAllowed = false;
        }

        public static void OnClientDisconnected(int actorNumber)
        {
            _lastSeqSentTo.Remove(actorNumber);
            _lastPayloadSentTo.Remove(actorNumber);
            _lastProcessedFrom.Remove(actorNumber);
        }

        // ------------------------------------------------------------------
        //  GAME START / RESUME / AVANZAR TURNO (llamado desde los parches)
        // ------------------------------------------------------------------

        /// <summary>
        /// Prefijo de GameManager.NextTurn (llamada externa, no recursiva).
        /// Devuelve FALSE para BLOQUEAR el avance vanilla: es lo que mantiene al
        /// cliente congelado tras enviar su turno (sin IA ni turnos intermedios).
        /// </summary>
        public static bool OnTurnAdvancing(GameManager gm)
        {
            if (!StateHandoff.IsInGame() || gm == null || gm.jugadores == null || gm.jugadores.Length == 0)
                return true;

            var state = MPStateManager.Instance;

            if (state.IsHost)
            {
                // Nunca sincronizar antes de que el host haya creado la partida y
                // mandado la config de asientos (los save vanilla aún no son válidos).
                if (HostManager.Instance == null || !HostManager.Instance.MatchStarted)
                    return true;

                // Si el turno que va a empezar pertenece a un cliente humano, enviarle el estado
                // guardado justo ahora (contenido = fin del turno anterior).
                int next = StateHandoff.NextPlayableSlot(gm.TurnOrder);
                if (next >= 0)
                    TrySendTurnToSlot(gm, next);

                // El host simula TODO localmente (IA incluida): el avance vanilla sigue.
                return true;
            }

            if (state.IsClient)
            {
                // Congelado: ya enviamos nuestro turno (el TurnOrder local no se
                // mueve del nuestro porque el avance está bloqueado). Todo NextTurn
                // que llegue aquí se bloquea — la simulación intermedia corre solo
                // en el host. El desbloqueo ocurre al recibir el siguiente estado.
                if (ClientFrozen)
                {
                    CivilizameMPPlugin.Log.LogInfo("[Turn] CLIENTE: NextTurn bloqueado (congelado, esperando estado del host)");
                    return false;
                }

                // Es el fin de NUESTRO turno: capturar el estado y enviarlo al host.
                if (MPMatchState.MiIndiceLocal >= 0 && gm.TurnOrder == MPMatchState.MiIndiceLocal)
                {
                    ClientManager.Instance?.SendTurnEnd(gm);
                    ClientFrozen = true;
                    CivilizameMPPlugin.Log.LogInfo("[Turn] CLIENTE: turno enviado; simulación local congelada hasta el siguiente estado del host");
                    return false; // el turno siguiente no avanza aquí
                }

                return true;
            }

            return true;
        }

        /// <summary>
        /// Llamado desde el parche de MundoCargado justo antes de retomar el turno
        /// tras restaurar un estado recibido. En el host, si el turno que retoma
        /// pertenece a un cliente, se lo envía (es el estado recién recibido).
        /// </summary>
        public static void OnPreResumeAdvance(GameManager gm)
        {
            if (!StateHandoff.IsInGame() || gm == null)
                return;
            if (!MPStateManager.Instance.IsHost)
                return;
            if (HostManager.Instance == null || !HostManager.Instance.MatchStarted)
                return;

            int next = StateHandoff.NextPlayableSlot(gm.TurnOrder);
            if (next < 0) return;
            TrySendTurnToSlot(gm, next);
        }

        /// <summary>Postfijo de GameManager.NextTurn: centraliza la UI del flujo de turnos.
        /// En cualquier turno que no sea el nuestro se mantiene el cartel de "espera"
        /// para que nadie vea el POV de otro jugador (el FOW cambia con cada turno
        /// vanilla, pero el overlay lo tapa).</summary>
        public static void OnTurnAdvanced(GameManager gm, bool cargado)
        {
            if (!StateHandoff.IsInGame() || gm == null || gm.TurnOrder < 0)
                return;
            if (gm.jugadores == null || gm.TurnOrder >= gm.jugadores.Length)
                return;

            var state = MPStateManager.Instance;

            // CLIENTE congelado: el postfix corre aunque el prefix haya bloqueado el
            // avance original; TurnOrder sigue en nuestro slot. Mantener el cartel
            // para que no se vea el POV (ni se oculte por el IsLocalTurn de abajo).
            if (state.IsClient && ClientFrozen)
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO AL HOST",
                    "Tu turno fue enviado al servidor.\nEsperando el siguiente estado de la partida...");
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                return;
            }

            if (MPMatchState.IsLocalTurn(gm))
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                return;
            }

            // Turno de otro: mismo cartel que el del cliente jugador normal
            if (MPMatchState.IsRemoteHumanTurn(gm))
            {
                MPWaitingPanel.Instance?.SetStatus("TURNO DEL JUGADOR " + (gm.TurnOrder + 1),
                    $"El jugador {gm.TurnOrder + 1} está jugando su turno...");
            }
            else
            {
                MPWaitingPanel.Instance?.SetStatus("TURNO DE LA IA",
                    $"La IA del jugador {gm.TurnOrder + 1} está jugando...");
            }
            MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
        }

        // ------------------------------------------------------------------
        //  HOST: envío a clientes
        // ------------------------------------------------------------------

        private static void TrySendTurnToSlot(GameManager gm, int slot)
        {
            var state = MPStateManager.Instance;
            if (!state.IsHost) return;

            int actor = ActorForSlot(slot);
            if (actor <= 0 || actor == state.LocalActorNumber)
                return; // IA o el propio host: el juego lo maneja vanilla/localmente

            byte[] map;
            byte[] jue = StateHandoff.SaveStateFiles(slot, out map);
            if (jue == null) return;

            var msg = new HandoffMessage
            {
                FromTurn = gm.TurnOrder,
                LabelTurn = slot,
                BaseSeq = PhotonManager.Instance.GetNextSequence(),
                JueBytes = jue,
                MapBytes = map
            };

            int sendResult = PhotonManager.Instance.SendHandoff(msg, actor);
            if (sendResult < 0 && sendResult != PhotonManager.SEND_QUEUED)
            {
                CivilizameMPPlugin.Log.LogError($"[Turn] HOST: fallo al enviar estado del slot {slot} a actor {actor}");
                return;
            }
            if (sendResult == PhotonManager.SEND_QUEUED)
                CivilizameMPPlugin.Log.LogWarning($"[Turn] HOST: sin sala; estado del slot {slot} quedó en cola (se enviará al reconectar)");

            _lastSeqSentTo[actor] = msg.BaseSeq;
            _lastPayloadSentTo[actor] = msg;

            CivilizameMPPlugin.Log.LogInfo(sendResult == PhotonManager.SEND_QUEUED
                ? $"[Turn] HOST: estado EN COLA para el slot {slot} (actor {actor}); termina turno {gm.TurnOrder}, baseSeq {msg.BaseSeq}"
                : $"[Turn] HOST: estado enviado al slot {slot} (actor {actor}); termina turno {gm.TurnOrder}, baseSeq {msg.BaseSeq}");
            MPWaitingPanel.Instance?.SetStatus("TURNO DEL JUGADOR " + (slot + 1),
                $"El jugador {slot + 1} está jugando su turno...");
            MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
        }

        // ------------------------------------------------------------------
        //  RECEPCIÓN (host y cliente enrutados desde PhotonManager)
        // ------------------------------------------------------------------

        public static void HandleIncomingHandoff(HandoffMessage msg, int senderId)
        {
            if (msg == null) return;
            var state = MPStateManager.Instance;
            if (state == null) return;

            if (senderId <= 0 || senderId == state.LocalActorNumber)
                return; // no puede ser nuestro ni del sistema

            // NOTA clave: en el CLIENTE el estado de su turno puede llegar mientras
            // aún está en el lobby/espera (el host lo envía en cuanto termina su
            // turno, antes de que el cliente cargue la escena "Juego"), por lo que
            // NO exigimos IsInGame() en el cliente.
            if (state.IsHost)
            {
                if (!StateHandoff.IsInGame()) return; // la partida del host aún no arrancó
                HandleHandoffAsHost(msg, senderId);
            }
            else if (state.IsClient)
            {
                HandleHandoffAsClient(msg, senderId);
            }
        }

        private static void HandleHandoffAsHost(HandoffMessage msg, int senderId)
        {
            int slot = SlotOfActor(senderId);
            if (slot < 0)
            {
                CivilizameMPPlugin.Log.LogWarning($"[Turn] HOST: state de actor desconocido {senderId}, descartado");
                return;
            }

            // Anti-duplicados: HandoffMessage puede llegar dos veces por la red fiable
            // de Photon. Procesar dos veces el mismo estado provocaba un doble
            // WriteStateFiles + doble recarga de escena, y el estado no se aplicaba.
            string sig = $"{msg.FromTurn}:{msg.BaseSeq}:{msg.JueBytes?.Length ?? 0}:{msg.MapBytes?.Length ?? 0}";
            if (_lastProcessedFrom.TryGetValue(senderId, out var lastSig) && lastSig == sig)
            {
                CivilizameMPPlugin.Log.LogInfo($"[Turn] HOST: handoff duplicado de actor {senderId} (turno {msg.FromTurn}, seq {msg.BaseSeq}) ignorado");
                return;
            }

            int expectedSeq;
            bool hasSeq = _lastSeqSentTo.TryGetValue(senderId, out expectedSeq);

            bool turnoOk = msg.FromTurn == slot;             // debe terminar SU turno
            bool seqOk = !hasSeq || msg.BaseSeq == expectedSeq; // debe basarse en el último estado enviado

            if (!turnoOk || !seqOk)
            {
                CivilizameMPPlugin.Log.LogWarning(
                    $"[Turn] HOST: estado inválido de actor {senderId} (turno={msg.FromTurn}, esperado={slot}; seq={msg.BaseSeq}, esperada={expectedSeq}). Reenviando estado válido...");

                HandoffMessage resend;
                if (_lastPayloadSentTo.TryGetValue(senderId, out resend) && resend != null)
                    PhotonManager.Instance.SendHandoff(resend, senderId);
                return;
            }

            if (_lastPayloadSentTo.ContainsKey(senderId))
                _lastPayloadSentTo.Remove(senderId);
            _lastSeqSentTo.Remove(senderId);
            _lastProcessedFrom[senderId] = sig;   // procesado: bloquear duplicados

            CivilizameMPPlugin.Log.LogInfo($"[Turn] HOST: cliente {senderId} terminó turno {msg.FromTurn} (state {msg.JueBytes?.Length} B). Restaurando...");
            MPWaitingPanel.Instance?.SetStatus("APLICANDO TURNO", "El jugador " + (slot + 1) + " terminó su turno...");

            if (!StateHandoff.WriteStateFiles(msg.JueBytes, msg.MapBytes))
            {
                CivilizameMPPlugin.Log.LogError("[Turn] HOST: no se pudo escribir el estado recibido");
                return;
            }

            // Recarga la escena con la ruta vanilla de guardado; el parche de
            // MundoCargado retomará el siguiente turno como fresco y ya enviará
            // el estado al turno humano que corresponda.
            ReloadFromSave();
        }

        private static void HandleHandoffAsClient(HandoffMessage msg, int senderId)
        {
            // Con la config aún sin llegar (MiIndiceLocal = -1) no podemos filtrar por
            // LabelTurn: se le pasa a ClientManager, que la almacena y la aplica en
            // cuanto llegue la config de asientos.
            if (MPMatchState.MiIndiceLocal >= 0 && msg.LabelTurn != MPMatchState.MiIndiceLocal)
            {
                CivilizameMPPlugin.Log.LogInfo(
                    $"[Turn] CLIENTE: estado ignorado (es el turno del jugador {msg.LabelTurn + 1}, no el mío {MPMatchState.MiIndiceLocal})");
                return;
            }

            ClientManager.Instance?.ReceiveOwnTurn(msg);
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

            MPWaitingPanel.Instance?.SetStatus("CARGANDO TURNO", "Aplicando el estado recibido...");
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

        private static int GetDictValue(Dictionary<int, int> dict, int key, int fallback)
        {
            int v;
            return dict.TryGetValue(key, out v) ? v : fallback;
        }
    }
}
