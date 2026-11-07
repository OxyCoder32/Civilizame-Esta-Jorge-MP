using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;
using CivilizameMP.Core;

namespace CivilizameMP.Network
{
    /// <summary>
    /// Guarda/carga el estado completo del juego reutilizando el sistema vanilla
    /// de "GuardadoSeguridad" (GuardadoSeguridad.jue + MapaGuardadoSeguridad.txt),
    /// para pasar turnos entre dispositivos.
    ///
    /// Regla clave del etiquetado: el TurnOrder grabado dentro del save debe ser el
    /// turno que va a ARRANCAR (no el que acaba de terminar), porque el juego, al
    /// cargar, hace TurnOrder = save.TurnOrder - 1 y luego NextTurn() lo avanza de
    /// nuevo hasta ese turno. Así el ingreso del turno se aplica una sola vez.
    /// </summary>
    public static class StateHandoff
    {
        /// <summary>True mientras se restaura un estado (evita capturas duplicadas).</summary>
        public static bool InsideRestore;
        /// <summary>Habilita temporalmente los guardados vanilla (lo pide nuestro código).</summary>
        public static bool VanillaAutosaveAllowed;
        /// <summary>Etiqueta de TurnOrder que debe llevar el GameSave que se está creando.</summary>
        public static int PendingSaveLabel = -1;

        /// <summary>Magic + versión del payload binario del handoff.</summary>
        public const int PAYLOAD_MAGIC = 0x4D505348;   // "HSPM" (Handoff State PayLoad Magic)
        public const int PAYLOAD_VERSION = 1;

        /// <summary>Bloquea el autosave vanilla mientras hay una partida MP en curso,
        /// para que no pise los archivos que estamos sincronizando.</summary>
        public static bool BlockVanillaAutosave
        {
            get { return !VanillaAutosaveAllowed && IsInGame(); }
        }

        public static bool IsInGame()
        {
            var st = MPStateManager.Instance;
            return st != null && (st.CurrentState == MPGameState.PlayingHost ||
                                  st.CurrentState == MPGameState.PlayingClient);
        }

        public static string SavePath
        {
            get { return Application.persistentDataPath + "/GuardadoSeguridad.jue"; }
        }

        public static string MapPath
        {
            get { return Application.persistentDataPath + "/MapaGuardadoSeguridad.txt"; }
        }

        /// <summary>
        /// Guarda el estado actual etiquetado con el turno que va a arrancar.
        /// Devuelve los bytes del .jue (o null si falla) y escribe el mapa en 'mapBytes'.
        /// </summary>
        public static byte[] SaveStateFiles(int labelTraffic, out byte[] mapBytes)
        {
            mapBytes = null;
            VanillaAutosaveAllowed = true;
            PendingSaveLabel = labelTraffic;
            try
            {
                var gm = GameManager.Instance;
                if (gm == null)
                {
                    CivilizameMPPlugin.Log.LogError("[StateHandoff] GameManager null al guardar estado");
                    return null;
                }

                var info = gm.GetComponent<InformationToFile>();
                if (info == null)
                {
                    CivilizameMPPlugin.Log.LogError("[StateHandoff] InformationToFile no encontrado");
                    return null;
                }

                info.GuardadoSeguridad();
                if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();

                byte[] jue = File.ReadAllBytes(SavePath);
                mapBytes = File.Exists(MapPath) ? File.ReadAllBytes(MapPath) : new byte[0];
                CivilizameMPPlugin.Log.LogInfo($"[StateHandoff] Estado guardado: {jue.Length} B + mapa {mapBytes.Length} B (etiqueta turno {labelTraffic})");
                return jue;
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[StateHandoff] Error guardando estado: {ex}");
                return null;
            }
            finally
            {
                PendingSaveLabel = -1;
                VanillaAutosaveAllowed = false;
            }
        }

        /// <summary>Escribe los archivos de estado recibidos por red en el disco.</summary>
        public static bool WriteStateFiles(byte[] jueBytes, byte[] mapBytes)
        {
            try
            {
                if (jueBytes == null || jueBytes.Length == 0)
                {
                    CivilizameMPPlugin.Log.LogError("[StateHandoff] .jue vacío al escribir estado");
                    return false;
                }

                File.WriteAllBytes(SavePath, jueBytes);
                File.WriteAllBytes(MapPath, mapBytes ?? new byte[0]);
                CivilizameMPPlugin.Log.LogInfo($"[StateHandoff] Estado escrito: {jueBytes.Length} B + mapa {(mapBytes == null ? 0 : mapBytes.Length)} B");
                return true;
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[StateHandoff] Error escribiendo estado: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Escribe el NewGameData.gen en modo "Cargar=true, posCargado=-1" para que
        /// GenerationManager cargue GuardadoSeguridad.jue (idéntico a CargarPartida.LoadGame(-1)).
        /// </summary>
        public static void WriteLoadMarker()
        {
            try
            {
                var formatter = new BinaryFormatter();
                string path = Application.persistentDataPath + "/NewGameData.gen";
                var data = new NewWorldData(0, false, 1f, 0, null, null, null, null,
                    true, false, false, -1, false, false, -1);
                using (var stream = new FileStream(path, FileMode.Create))
                    formatter.Serialize(stream, data);
                CivilizameMPPlugin.Log.LogInfo("[StateHandoff] NewGameData.gen escrito (modo carga)");
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[StateHandoff] Error escribiendo NewGameData.gen: {ex}");
            }
        }

        /// <summary>
        /// Siguiente turno que va a EMPEZAR partiendo del TurnOrder ACTUAL —
        /// replica el incremento vanilla de GameManager.NextTurn (`TurnOrder == -1
        /// || == ultimo -> 0; else ++`) saltando los jugadores eliminados (null).
        /// </summary>
        public static int NextPlayableSlot(int fromTurnOrder)
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.jugadores == null || gm.jugadores.Length == 0) return -1;

            int len = gm.jugadores.Length;
            int cur = (fromTurnOrder == len - 1 || fromTurnOrder == -1) ? 0 : fromTurnOrder + 1;
            for (int k = 0; k < len; k++)
            {
                if (gm.jugadores[cur] != null) return cur;
                cur = (cur == len - 1) ? 0 : cur + 1;
            }
            return -1;
        }
    }
}
