using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using CivilizameMP.UI;

namespace CivilizameMP.Core
{
    /// <summary>
    /// Actualizador automático del mod.
    ///
    /// 1. Al cargar la escena del menú (una vez por sesión, con un pequeño
    ///    retardo) descarga en segundo plano la DLL oficial de GitHub
    ///    (MPConfig.UpdateUrl) y compara:
    ///      - versión del ensamblado remoto (leída SIN cargarlo, desde %TEMP%)
    ///      - versión local (atributo [BepInPlugin] => ModVersion.Current)
    ///    Solo se ofrece actualizar si la remota es MÁS NUEVA que la local.
    ///
    /// 2. Si hay update muestra el cartel MPUpdatePanel:
    ///      ACTUALIZAR  -> escribe la DLL en %TEMP%, genera un .bat que
    ///                     espera a que este proceso termine, copia la DLL
    ///                     sobre BepInEx/plugins/CivilizameMP y vuelve a
    ///                     abrir el juego; luego Application.Quit().
    ///      NO          -> sigue normal; si marcó "No volver a preguntar
    ///                     por esta versión" se guarda en la config
    ///                     (IgnoredUpdateVersion) y no vuelve a ofrecerla.
    ///
    /// Todo el trabajo de red/archivos va en hilos de fondo; la UI y
    /// Application.Quit() solo en el hilo principal (máquina de estados).
    /// </summary>
    public class ModUpdater : MonoBehaviour
    {
        public static ModUpdater Instance { get; private set; }

        private enum State { Idle, Checking, Offering, Downloading, Failed }
        private volatile State _state = State.Idle;

        private byte[] _remoteBytes;      // DLL descargada (hilos de fondo)
        private string _remoteVersion;    // "1.2.3"
        private readonly string _localVersion = ModVersion.Current;

        private volatile bool _checkDone;
        private volatile bool _installDone;
        private volatile bool _quitPending;
        private string _installError;
        private bool _checkedThisSession;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_checkedThisSession) return;
            if (scene.name != MPConstants.MENU_SCENE) return;
            _checkedThisSession = true;
            StartCoroutine(CheckWhenSettled());
        }

        private IEnumerator CheckWhenSettled()
        {
            // Dejar que el menú termine de cargar y que cualquier connexión
            // pendiente arranque antes de abrir el cartel.
            yield return new WaitForSeconds(6f);
            StartCheck();
        }

        // ------------------------------------------------------------------
        //  Chequeo (hilo de fondo)
        // ------------------------------------------------------------------

        public void StartCheck()
        {
            if (_state != State.Idle) return;
            string url = MPConfig.UpdateUrl != null ? MPConfig.UpdateUrl.Value : null;
            if (string.IsNullOrEmpty(url))
            {
                CivilizameMPPlugin.Log.LogInfo("[Updater] Sin URL de actualización; desactivado");
                return;
            }
            _state = State.Checking;
            CivilizameMPPlugin.Log.LogInfo($"[Updater] Buscando actualizaciones en {url}");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    byte[] data = Download(url);
                    if (data == null || data.Length < 1024)
                        throw new Exception("DLL remota vacía o demasiado pequeña");

                    string remoteVersion = ReadDllVersion(data);
                    if (remoteVersion == null)
                        throw new Exception("No se pudo leer la versión de la DLL remota");

                    lock (Instance) { Instance._remoteBytes = data; Instance._remoteVersion = remoteVersion; }
                    CivilizameMPPlugin.Log.LogInfo(
                        $"[Updater] DLL remota leída: versión {remoteVersion} ({data.Length} B); local {_localVersion}");
                    _checkDone = true; // lo evalúa Update() en el hilo principal
                }
                catch (Exception ex)
                {
                    CivilizameMPPlugin.Log.LogWarning($"[Updater] Chequeo falló (se sigue sin actualizar): {ex.Message}");
                    _state = State.Failed;
                }
            });
        }

        private static byte[] Download(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.UserAgent = "CivilizameMP-Updater";
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var ms = new MemoryStream())
            {
                resp.GetResponseStream().CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Versión de la DLL remota, leída del atributo [BepInPlugin]
        /// SIN ejecutar el mod (misma fuente que la copia local).</summary>
        private static string ReadDllVersion(byte[] data)
        {
            return ModVersion.ReadFromDllBytes(data);
        }

        // ------------------------------------------------------------------
        //  Máquina de estados (hilo principal)
        // ------------------------------------------------------------------

        private void Update()
        {
            if (_quitPending)
            {
                _quitPending = false;
                Application.Quit();
                return;
            }

            if (_checkDone)
            {
                _checkDone = false;
                EvaluateCheck();
            }

            if (_installDone)
            {
                _installDone = false;
                OnInstallFinished();
            }
        }

        private void EvaluateCheck()
        {
            string remote;
            lock (Instance) remote = _remoteVersion;
            _state = State.Idle;

            var local = ParseVersion(_localVersion);
            var parsed = ParseVersion(remote);

            bool hayUpdate =
                parsed != null &&
                (local == null || parsed > local); // solo si la remota es MÁS NUEVA

            if (!hayUpdate)
            {
                CivilizameMPPlugin.Log.LogInfo(
                    $"[Updater] Sin actualización (local {_localVersion}, remota {remote})");
                return;
            }

            if (MPConfig.IgnoredUpdateVersion != null &&
                !string.IsNullOrEmpty(MPConfig.IgnoredUpdateVersion.Value) &&
                MPConfig.IgnoredUpdateVersion.Value == remote)
            {
                CivilizameMPPlugin.Log.LogInfo($"[Updater] Versión {remote} ignorada por el usuario");
                return;
            }

            ShowOffer(remote);
        }

        private static Version ParseVersion(string s)
        {
            Version v;
            return Version.TryParse(s, out v) ? v : null;
        }

        private void ShowOffer(string remote)
        {
            var pm = MPPanelManager.Instance;
            var panel = pm != null ? pm.ShowUpdatePanel() : null;
            if (panel == null)
            {
                CivilizameMPPlugin.Log.LogWarning("[Updater] Sin UI disponible para el cartel de update");
                return;
            }
            _state = State.Offering;
            panel.SetBusy(false);
            panel.SetBody(
                "Hay una versión nueva del mod disponible.\n\n" +
                $"Tu versión: {_localVersion}\n" +
                $"Versión nueva: {remote}\n\n" +
                "Se descargará y el juego se REINICIARÁ para aplicarla.");
            panel.OnAccept = AcceptUpdate;
            panel.OnDecline = DeclineUpdate;
            CivilizameMPPlugin.Log.LogInfo($"[Updater] Update disponible: {remote} (local {_localVersion})");
        }

        // ------------------------------------------------------------------
        //  Acciones del cartel
        // ------------------------------------------------------------------

        private void AcceptUpdate()
        {
            if (_state == State.Downloading) return;
            byte[] data;
            lock (Instance) data = _remoteBytes;
            if (data == null)
            {
                _state = State.Idle;
                MPPanelManager.Instance?.CloseUpdatePanel();
                return;
            }

            _state = State.Downloading;
            var panel = MPPanelManager.Instance != null
                ? MPPanelManager.Instance.GetPanel(MPPanelType.Update) as MPUpdatePanel : null;
            if (panel != null)
            {
                panel.SetBusy(true);
                panel.SetBody("Descargando e instalando...\nEl juego se reiniciará en unos segundos.");
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    InstallAndRestart(data);
                    _installError = null;
                    _quitPending = true; // Application.Quit() en el hilo principal
                }
                catch (Exception ex)
                {
                    _installError = ex.Message;
                }
                _installDone = true;
            });
        }

        private void DeclineUpdate(bool dontAskAgain)
        {
            if (dontAskAgain && _remoteVersion != null && MPConfig.IgnoredUpdateVersion != null)
            {
                MPConfig.IgnoredUpdateVersion.Value = _remoteVersion;
                try { MPConfig.Source?.Save(); } catch { }
                CivilizameMPPlugin.Log.LogInfo($"[Updater] Versión {_remoteVersion} marcada como 'no volver a preguntar'");
            }
            _state = State.Idle;
            MPPanelManager.Instance?.CloseUpdatePanel();
        }

        private void OnInstallFinished()
        {
            if (_installError == null) return; // el reinicio está en camino
            _state = State.Idle;
            CivilizameMPPlugin.Log.LogError($"[Updater] Error instalando la actualización: {_installError}");
            var panel = MPPanelManager.Instance != null
                ? MPPanelManager.Instance.GetPanel(MPPanelType.Update) as MPUpdatePanel : null;
            if (panel != null)
            {
                panel.SetBusy(false);
                panel.SetBody("No se pudo instalar la actualización:\n" + _installError +
                    "\n\nPuedes actualizarla manualmente desde GitHub.");
            }
        }

        // ------------------------------------------------------------------
        //  Instalación: %TEMP% + .bat que espera al cierre y reinicia el juego
        // ------------------------------------------------------------------

        private void InstallAndRestart(byte[] data)
        {
            int pid = Process.GetCurrentProcess().Id;

            string tempDll = Path.Combine(Path.GetTempPath(), $"CivilizameMP.{pid}.update.dll");
            File.WriteAllBytes(tempDll, data);

            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string target = Path.Combine(pluginDir ?? ".", "CivilizameMP.dll");
            if (string.IsNullOrEmpty(target) || !Directory.Exists(Path.GetDirectoryName(target)))
                throw new Exception("No se encontró la carpeta del mod en BepInEx/plugins");

            string exe = Process.GetCurrentProcess().MainModule != null
                ? Process.GetCurrentProcess().MainModule.FileName : null;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new Exception("No se encontró el ejecutable del juego");

            // El .bat se escribe en ASCII puro; las rutas van como ARGUMENTOS
            // (cmd las recibe por la línea de comandos, que sí es Unicode).
            string bat = Path.Combine(Path.GetTempPath(), $"CivilizameMP.{pid}.update.bat");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine(":waitloop");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find /I \"{pid}\" >NUL");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  ping -n 2 127.0.0.1 >NUL");
            sb.AppendLine("  goto waitloop");
            sb.AppendLine(")");
            sb.AppendLine("copy /Y \"%~1\" \"%~2\" >NUL");
            sb.AppendLine("del \"%~1\" 2>NUL");
            sb.AppendLine("start \"\" \"%~3\"");
            sb.AppendLine("del \"%~f0\" 2>NUL");
            sb.AppendLine("exit");
            File.WriteAllText(bat, sb.ToString(), new ASCIIEncoding());

            CivilizameMPPlugin.Log.LogInfo($"[Updater] Instalando: {tempDll} -> {target}; reiniciando {exe}");

            var psi = new ProcessStartInfo
            {
                FileName = bat,
                Arguments = $"\"{tempDll}\" \"{target}\" \"{exe}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            // Application.Quit() lo lanza el hilo principal al ver _quitPending.
        }
    }
}
