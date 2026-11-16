using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using CivilizameMP.UI;

namespace CivilizameMP.Core
{
    /// <summary>
    /// Actualizador del mod. Al cargar el menú pide Compiled/version.txt
    /// (petición única y diminuta) y si la versión remota es mayor que la
    /// local, muestra el cartel. Al aceptar descarga la DLL y la instala;
    /// el juego NO se reinicia solo: el cartel pide reiniciar manualmente.
    /// </summary>
    public class ModUpdater : MonoBehaviour
    {
        public static ModUpdater Instance { get; private set; }

        private enum State { Idle, Checking, Offering, Downloading, Failed }
        private volatile State _state = State.Idle;

        private string _remoteVersion;
        private readonly string _localVersion = ModVersion.Current;

        private volatile bool _checkDone;
        private volatile bool _installDone;
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
            yield return new WaitForSeconds(0.5f);
            StartCheck();
        }

        // ------------------------------------------------------------------
        //  Chequeo: una petición a version.txt y comparar. Nada más.
        // ------------------------------------------------------------------

        public void StartCheck()
        {
            if (_state != State.Idle) return;
            string versionUrl = MPConfig.UpdateVersionUrl != null ? MPConfig.UpdateVersionUrl.Value : null;
            if (string.IsNullOrEmpty(versionUrl)) return;

            _state = State.Checking;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string remote = CleanVersion(Encoding.UTF8.GetString(Download(versionUrl)));
                    if (remote == null)
                        throw new Exception("version.txt ilegible");

                    lock (Instance) Instance._remoteVersion = remote;
                    _checkDone = true; // se evalúa en el hilo principal
                }
                catch (Exception ex)
                {
                    CivilizameMPPlugin.Log.LogWarning($"[Updater] Chequeo falló (se sigue sin actualizar): {ex.Message}");
                    _state = State.Failed;
                }
            });
        }

        private static string CleanVersion(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string line = raw.Trim();
            int nl = line.IndexOfAny(new[] { '\r', '\n' });
            if (nl > 0) line = line.Substring(0, nl);
            line = line.Trim();
            Version v;
            return Version.TryParse(line, out v) ? line : null;
        }

        private static byte[] Download(string url)
        {
            // Cache-buster: el CDN de GitHub cachea por ruta.
            url = url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.UserAgent = "CivilizameMP-Updater";
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var ms = new MemoryStream())
            {
                resp.GetResponseStream().CopyTo(ms);
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------------
        //  Máquina de estados (hilo principal)
        // ------------------------------------------------------------------

        private void Update()
        {
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

            Version local = ParseVersion(_localVersion);
            Version parsed = ParseVersion(remote);
            bool hayUpdate = parsed != null && (local == null || parsed > local);
            CivilizameMPPlugin.Log.LogInfo($"[Updater] Chequeo: local {_localVersion}, remota {remote}, update={hayUpdate}");
            if (!hayUpdate) return;

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
            panel.ResetView();
            panel.SetBody(
                "Hay una versión nueva del mod disponible.\n\n" +
                $"Tu versión: {_localVersion}\n" +
                $"Versión nueva: {remote}\n\n" +
                "Se descargará e instalará. Al terminar, reinicia el juego para aplicarla.");
            panel.OnAccept = AcceptUpdate;
            panel.OnDecline = DeclineUpdate;
            panel.OnOk = CloseDonePanel;
            CivilizameMPPlugin.Log.LogInfo($"[Updater] Update disponible: {remote} (local {_localVersion})");
        }

        // ------------------------------------------------------------------
        //  Acciones del cartel
        // ------------------------------------------------------------------

        private void AcceptUpdate()
        {
            if (_state == State.Downloading) return;
            lock (Instance)
            {
                if (_remoteVersion == null)
                {
                    _state = State.Idle;
                    MPPanelManager.Instance?.CloseUpdatePanel();
                    return;
                }
            }

            _state = State.Downloading;
            var panel = MPPanelManager.Instance != null
                ? MPPanelManager.Instance.GetPanel(MPPanelType.Update) as MPUpdatePanel : null;
            if (panel != null)
            {
                panel.SetBusy(true);
                panel.SetBody("Descargando e instalando...");
            }

            string esperada;
            lock (Instance) esperada = _remoteVersion;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string url = MPConfig.UpdateUrl != null ? MPConfig.UpdateUrl.Value : null;
                    if (string.IsNullOrEmpty(url))
                        throw new Exception("URL de actualización no configurada");

                    byte[] data = Download(url);
                    if (data == null || data.Length < 1024)
                        throw new Exception("DLL remota vacía o demasiado pequeña");

                    // Verificación: que la DLL descargada sea la versión de version.txt.
                    string real = ModVersion.ReadFromDllBytes(data);
                    if (real != esperada)
                        throw new Exception($"La DLL del repo es v{real} pero version.txt dice v{esperada}");

                    InstallInPlace(data);
                    _installError = null;
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
            var panel = MPPanelManager.Instance != null
                ? MPPanelManager.Instance.GetPanel(MPPanelType.Update) as MPUpdatePanel : null;

            if (_installError != null)
            {
                _state = State.Idle;
                CivilizameMPPlugin.Log.LogError($"[Updater] Error instalando la actualización: {_installError}");
                if (panel != null)
                {
                    panel.ResetView();
                    panel.SetBody("No se pudo instalar la actualización:\n" + _installError +
                        "\n\nPuedes actualizarla manualmente desde GitHub.");
                }
                return;
            }

            // Instalada: reinicio manual por el usuario.
            _state = State.Idle;
            CivilizameMPPlugin.Log.LogInfo("[Updater] Instalación completada; se aplicará al reiniciar el juego");
            panel?.ShowDone("Actualización Descargada.\n\nReinicia el juego para aplicar.");
        }

        private void CloseDonePanel()
        {
            _state = State.Idle;
            MPPanelManager.Instance?.CloseUpdatePanel();
        }

        // ------------------------------------------------------------------
        //  Instalación: renombrar la DLL en uso a .old y escribir la nueva.
        // ------------------------------------------------------------------

        private static void InstallInPlace(byte[] data)
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string target = Path.Combine(pluginDir ?? ".", "CivilizameMP.dll");
            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir))
                throw new Exception("No se encontró la carpeta del mod en BepInEx/plugins");

            string old = target + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }

            bool moved = false;
            try
            {
                File.Move(target, old);
                moved = true;
                File.WriteAllBytes(target, data);
            }
            catch
            {
                if (moved) { try { File.Move(old, target); } catch { } }
                throw;
            }
            try { File.Delete(old); } catch { }
        }
    }
}
