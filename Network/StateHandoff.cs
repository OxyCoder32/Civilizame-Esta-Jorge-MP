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
        /// <summary>TurnOrder que debe restaurarse tras recargar la escena (supervive al reset de MundoCargado(false) en GameManager.Awake).</summary>
        public static int PendingTurnOrderAfterLoad = -1;
        /// <summary>Carga en MODO ESPEJO (cliente): TurnOrder = etiqueta tal cual
        /// (turno ya en curso en la PC que actúa) y SIN NextTurn: los efectos
        /// del turno activo llegan por eventos en vivo. La pone ClientManager
        /// al aceptar un estado y la consume el parche de MundoCargado.</summary>
        public static bool MirrorLoad;

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

                // COMPRESIÓN GZip: el estado va por red y las conexiones malas
                // sufren; el binario serializado y el mapa de texto se comprimen
                // ~70-80% (el receptor lo detecta por la cabecera 1F 8B).
                byte[] jue = GzipMaybe(File.ReadAllBytes(SavePath));
                mapBytes = File.Exists(MapPath)
                    ? GzipMaybe(File.ReadAllBytes(MapPath)) : new byte[0];
                CivilizameMPPlugin.Log.LogInfo($"[StateHandoff] Estado guardado (GZip): {jue.Length} B + mapa {mapBytes.Length} B (etiqueta turno {labelTraffic})");
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

        /// <summary>Comprime con GZip (deja el array tal cual si no reduce).</summary>
        private static byte[] GzipMaybe(byte[] raw)
        {
            if (raw == null || raw.Length < 64) return raw;
            try
            {
                using (var src = new MemoryStream(raw))
                using (var dst = new MemoryStream(raw.Length))
                {
                    using (var gz = new System.IO.Compression.GZipStream(dst, System.IO.Compression.CompressionLevel.Fastest))
                        src.CopyTo(gz);
                    byte[] outb = dst.ToArray();
                    return outb.Length < raw.Length ? outb : raw;
                }
            }
            catch { return raw; }
        }

        /// <summary>Descomprime si el array tiene cabecera GZip (1F 8B).</summary>
        private static byte[] GzipUnwrap(byte[] data)
        {
            if (data == null || data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B)
                return data;
            try
            {
                using (var src = new MemoryStream(data))
                using (var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress))
                using (var dst = new MemoryStream())
                {
                    gz.CopyTo(dst);
                    return dst.ToArray();
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[StateHandoff] Error descomprimiendo GZip: {ex.Message}");
                return null;
            }
        }

        /// <summary>Escribe los archivos de estado recibidos por red en el disco
        /// (descomprimiendo si llegaron con GZip).</summary>
        public static bool WriteStateFiles(byte[] jueBytes, byte[] mapBytes)
        {
            try
            {
                jueBytes = GzipUnwrap(jueBytes);
                mapBytes = GzipUnwrap(mapBytes);
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
