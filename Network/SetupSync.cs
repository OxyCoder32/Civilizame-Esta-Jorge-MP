using System;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.UI;

namespace CivilizameMP.Network
{
    /// <summary>
    /// Espejo del panel vanilla de configuración de partida (MenuUIManager +
    /// GameSettings). El HOST no modifica nada del juego: los parches de
    /// observación leen el panel cada vez que el host cambia algo (nº de
    /// jugadores, IA/Real, nombres, líderes, mapa, dificultad) y este módulo
    /// retransmite esa configuración a los clientes, que la muestran en su
    /// panel de espera. Orden de asientos = orden de llegada a la sala.
    /// </summary>
    public static class SetupSync
    {
        public static int NumJug = -1;
        public static int MapSize;        // tamañoMap vanilla x10 (5=Enano, 30=Gigante)
        public static int MapType;        // 0 Antiguo, 1 Continentes, 2 Archipiélago, 3 Pangea
        public static int Dificultad = -1;
        public static int[] Leaders;      // índice de GameSettings.Lideres por slot
        public static bool[] RealFlags;   // true = jugador real en ese slot
        public static string[] Names;     // nombres del panel
        public static bool IsFinal;       // true: recibido del INICIAR PARTIDA vanilla

        public static void Reset()
        {
            NumJug = -1;
            MapSize = 0;
            MapType = 0;
            Dificultad = -1;
            Leaders = null;
            RealFlags = null;
            Names = null;
            IsFinal = false;
        }

        // ------------------------------------------------------------------
        //  HOST: captura del panel vanilla (solo lectura)
        // ------------------------------------------------------------------

        public static void CaptureFromHost(GameSettings gs, MenuUIManager ui)
        {
            if (gs == null) return;
            try
            {
                NumJug = gs.NumJug;
                MapSize = Mathf.RoundToInt(Traverse.Create(gs).Field("_tamañoMap").GetValue<float>() * 10f);
                MapType = Traverse.Create(gs).Field("_typeMap").GetValue<int>();
                Dificultad = Traverse.Create(gs).Field("dificulatad").GetValue<int>();

                var jug = Traverse.Create(gs).Field("Jugadores").GetValue<GameSettings.Lideres[]>();
                if (jug != null)
                {
                    Leaders = new int[jug.Length];
                    for (int i = 0; i < jug.Length; i++) Leaders[i] = (int)jug[i];
                }

                var real = Traverse.Create(gs).Field("realLideres").GetValue<bool[]>();
                if (real != null) RealFlags = (bool[])real.Clone();

                if (ui != null && ui.namesList != null)
                    Names = (string[])ui.namesList.Clone();
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[SetupSync] Error capturando panel: {ex}");
            }
        }

        /// <summary>
        /// HOST: captura el panel y lo transmite a todos. 'final' = el host acaba
        /// de pulsar el INICIAR PARTIDA vanilla (o cargar partida).
        /// Solo actúa en el host conectado a una sala, dentro de la escena del menú.
        /// </summary>
        public static bool TryHostCaptureAndBroadcast(bool final)
        {
            var state = MPStateManager.Instance;
            if (state == null || !state.IsHost) return false;
            if (PhotonManager.Instance == null || !PhotonManager.Instance.IsInRoom) return false;
            if (SceneManager.GetActiveScene().name != MPConstants.MENU_SCENE) return false;

            CaptureFromHost(FindVanilla<GameSettings>(), FindVanilla<MenuUIManager>());
            IsFinal = final;

            if (NumJug <= 0)
            {
                CivilizameMPPlugin.Log.LogWarning("[SetupSync] Panel sin jugadores aún; no se sincroniza");
                return false;
            }

            string json = ToJson();
            PhotonManager.Instance.SendConfigToAll(json);
            CivilizameMPPlugin.Log.LogInfo($"[SetupSync] Config sincronizada (final={final}): NumJug={NumJug}");
            return true;
        }

        private static T FindVanilla<T>() where T : MonoBehaviour
        {
            return UnityEngine.Object.FindObjectOfType<T>();
        }

        // ------------------------------------------------------------------
        //  JSON (ligero, sin dependencias)
        // ------------------------------------------------------------------

        public static string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"setup\":true,\"final\":").Append(IsFinal ? "true" : "false").Append(",");
            sb.Append("\"NumJug\":").Append(NumJug).Append(",");
            sb.Append("\"Size\":").Append(MapSize).Append(",");
            sb.Append("\"Type\":").Append(MapType).Append(",");
            sb.Append("\"Dif\":").Append(Dificultad).Append(",");

            sb.Append("\"Real\":[");
            if (RealFlags != null)
                for (int i = 0; i < RealFlags.Length; i++)
                { if (i > 0) sb.Append(","); sb.Append(RealFlags[i] ? "1" : "0"); }
            sb.Append("],");

            sb.Append("\"Leaders\":[");
            if (Leaders != null)
                for (int i = 0; i < Leaders.Length; i++)
                { if (i > 0) sb.Append(","); sb.Append(Leaders[i]); }
            sb.Append("],");

            sb.Append("\"Names\":[");
            if (Names != null)
                for (int i = 0; i < Names.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(Escape(Names[i])).Append("\"");
                }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ------------------------------------------------------------------
        //  CLIENTE: aplicar el setup recibido (solo mostrar)
        // ------------------------------------------------------------------

        public static bool ApplyJson(string json)
        {
            if (json == null) return false;
            int idx = json.IndexOf("\"setup\":true", StringComparison.Ordinal);
            if (idx < 0) return false;

            try
            {
                IsFinal = ExtractBool(json, "final");
                NumJug = ExtractInt(json, "NumJug");
                MapSize = ExtractInt(json, "Size");
                MapType = ExtractInt(json, "Type");
                Dificultad = ExtractInt(json, "Dif");
                RealFlags = ExtractBoolArray(json, "Real");
                Leaders = ExtractIntArray(json, "Leaders");
                Names = ExtractStringArray(json, "Names");
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[SetupSync] Error parseando setup: {ex.Message}");
                return true;
            }

            UpdateWaitingPanel();
            CivilizameMPPlugin.Log.LogInfo($"[SetupSync] Setup recibido (final={IsFinal}): NumJug={NumJug}");
            return true;
        }

        /// <summary>Pinta la configuración de partida en el panel de espera del cliente.</summary>
        public static void UpdateWaitingPanel()
        {
            var panel = MPWaitingPanel.Instance;
            if (panel == null) return;

            var sb = new StringBuilder();
            sb.Append("Mapa: ").Append(SizeName(MapSize)).Append(" · ").Append(TypeName(MapType));
            if (Dificultad >= 0) sb.Append(" · Dif.").Append(Dificultad);
            sb.Append("\nJugadores: ").Append(Math.Max(0, NumJug));
            if (RealFlags != null)
            {
                int reales = 0;
                foreach (var f in RealFlags) if (f) reales++;
                if (reales > 0) sb.Append(" (").Append(reales).Append(" reales)");
            }

            for (int i = 0; i < Math.Max(0, NumJug); i++)
            {
                bool real = RealFlags != null && i < RealFlags.Length && RealFlags[i];
                string n = Names != null && i < Names.Length && !string.IsNullOrEmpty(Names[i])
                    ? Names[i] : "Jugador " + (i + 1);
                sb.Append("\n").Append(i + 1).Append(". ").Append(n).Append(real ? " (Real)" : " (IA)");
            }

            panel.SetStatus(IsFinal ? "PARTIDA CREADA" : "HOST CONFIGURANDO LA PARTIDA", sb.ToString());
        }

        private static string SizeName(int size)
        {
            switch (size)
            {
                case 5: return "Enano";
                case 10: return "Pequeño";
                case 15: return "Mediano";
                case 20: return "Grande";
                case 25: return "Enorme";
                case 30: return "Gigante";
                default: return "Aleatorio";
            }
        }

        private static string TypeName(int type)
        {
            switch (type)
            {
                case 0: return "Antiguo";
                case 1: return "Continentes";
                case 2: return "Archipiélago";
                case 3: return "Pangea";
                default: return "Aleatorio";
            }
        }

        // ------------------------------------------------------------------
        //  Parseo ligero (mismo estilo que ClientManager)
        // ------------------------------------------------------------------

        private static int ExtractInt(string json, string key)
        {
            string pattern = "\"" + key + "\":";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return -1;
            idx += pattern.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            int v;
            return int.TryParse(json.Substring(idx, end - idx), out v) ? v : -1;
        }

        private static bool ExtractBool(string json, string key)
        {
            string pattern = "\"" + key + "\":";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return false;
            int val = idx + pattern.Length;
            return val + 4 <= json.Length && json.Substring(val, 4) == "true";
        }

        /// <summary>Array de flags "1/0" (Real[]).</summary>
        private static bool[] ExtractBoolArray(string json, string key)
        {
            int[] ints = ExtractIntArray(json, key);
            if (ints == null) return null;
            var flags = new bool[ints.Length];
            for (int i = 0; i < ints.Length; i++) flags[i] = ints[i] != 0;
            return flags;
        }

        private static int[] ExtractIntArray(string json, string key)
        {            int start = json.IndexOf("\"" + key + "\":[", StringComparison.Ordinal);
            if (start < 0) return null;
            start = json.IndexOf('[', start) + 1;
            int end = json.IndexOf(']', start);
            if (end < 0) return null;

            string content = json.Substring(start, end - start);
            string[] parts = content.Split(',');
            var list = new System.Collections.Generic.List<int>();
            foreach (var p in parts)
            {
                int v;
                if (int.TryParse(p.Trim(), out v)) list.Add(v);
            }
            return list.ToArray();
        }

        private static string[] ExtractStringArray(string json, string key)
        {
            int start = json.IndexOf("\"" + key + "\":[", StringComparison.Ordinal);
            if (start < 0) return null;
            start = json.IndexOf('[', start) + 1;
            int end = json.IndexOf(']', start);
            if (end < 0) return null;

            string content = json.Substring(start, end - start);
            var parts = content.Split(new[] { ',' }, System.StringSplitOptions.None);
            var list = new System.Collections.Generic.List<string>();
            foreach (var p in parts)
            {
                string t = p;
                if (t.StartsWith("\"")) t = t.Substring(1);
                if (t.EndsWith("\"") && t.Length > 1) t = t.Substring(0, t.Length - 1);
                list.Add(t.Replace("\\\"", "\""));
            }
            if (parts.Length == 1 && string.IsNullOrEmpty(parts[0])) return new string[0];
            return list.ToArray();
        }
    }
}
