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
    ///      ACTUALIZAR  -> descarga la DLL (si el chequeo fue por version.txt),
    ///                     verifica su versión y la instala EN EL ACTO sobre
    ///                     BepInEx/plugins/CivilizameMP (renombrar la DLL en
    ///                     uso funciona en Windows; comprobado). El juego NO
    ///                     se reinicia solo: el cartel muestra "Actualización
    ///                     Descargada. Reinicia para aplicar" con botón OK.
    ///      NO          -> sigue normal; si marcó "No volver a preguntar
    ///                     por esta versión" se guarda en la config
    ///                     (IgnoredUpdateVersion) y no vuelve a ofrecerla.
    ///
    /// Todo el trabajo de red/archivos va en hilos de fondo; la UI solo en
    /// el hilo principal (máquina de estados).
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
            // Menú cargado: chequear ya (version.txt es un archivo diminuto).
            yield return new WaitForSeconds(2f);
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
                    byte[] data = null;
                    string remoteVersion = null;

                    // 1) Intento LIGERO: Compiled/version.txt (un archivo de
                    //    texto minúsculo con la versión publicada). Evita
                    //    descargar la DLL entera en cada arranque.
                    string versionUrl = MPConfig.UpdateVersionUrl != null
                        ? MPConfig.UpdateVersionUrl.Value : null;
                    if (!string.IsNullOrEmpty(versionUrl))
                    {
                        try
                        {
                            string txt = Encoding.UTF8.GetString(Download(versionUrl));
                            remoteVersion = CleanVersion(txt);
                            if (remoteVersion != null)
                                CivilizameMPPlugin.Log.LogInfo(
                                    $"[Updater] version.txt leído: {remoteVersion}");
                        }
                        catch (Exception ex)
                        {
                            CivilizameMPPlugin.Log.LogInfo(
                                $"[Updater] version.txt no disponible ({ex.Message}); leyendo la DLL completa");
                        }
                    }

                    // 2) Respaldo: descargar la DLL y leer su [BepInPlugin]
                    //    con Mono.Cecil (método antiguo, sin ejecutar nada).
                    if (remoteVersion == null)
                    {
                        data = Download(url);
                        if (data == null || data.Length < 1024)
                            throw new Exception("DLL remota vacía o demasiado pequeña");
                        remoteVersion = ModVersion.ReadFromDllBytes(data);
                        if (remoteVersion == null)
                            throw new Exception("No se pudo leer la versión de la DLL remota");
                    }

                    lock (Instance) { Instance._remoteBytes = data; Instance._remoteVersion = remoteVersion; }
                    CivilizameMPPlugin.Log.LogInfo(
                        $"[Updater] Remota: versión {remoteVersion}{(data != null ? $" ({data.Length} B)" : " (la DLL se descargará solo si aceptas)")}; local {_localVersion}");
                    _checkDone = true; // lo evalúa Update() en el hilo principal
                }
                catch (Exception ex)
                {
                    CivilizameMPPlugin.Log.LogWarning($"[Updater] Chequeo falló (se sigue sin actualizar): {ex.Message}");
                    _state = State.Failed;
                }
            });
        }

        /// <summary>Limpia el contenido de version.txt: primera línea con
        /// formato Major[.Minor[.Build[.Revision]]] sin espacios.</summary>
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
            // Cache-buster: el CDN de GitHub (raw.githubusercontent) sirve por
            // RUTA y cachea la respuesta (~5 min o más). Con un parámetro
            // único por descarga garantizamos leer la versión recién publicada.
            string sep = url.Contains("?") ? "&" : "?";
            url = url + sep + "t=" + DateTime.UtcNow.Ticks;
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
                if (_remoteBytes == null && _remoteVersion == null)
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

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    byte[] data;
                    lock (Instance) data = _remoteBytes;

                    // Chequeo por version.txt: la DLL NO estaba descargada.
                    // Se baja ahora (una sola vez) y se verifica que su
                    // versión coincide con version.txt (evita downgrades).
                    if (data == null)
                    {
                        string url = MPConfig.UpdateUrl != null ? MPConfig.UpdateUrl.Value : null;
                        if (string.IsNullOrEmpty(url))
                            throw new Exception("URL de actualización no configurada");
                        data = Download(url);
                        string real = ModVersion.ReadFromDllBytes(data);
                        string esperada;
                        lock (Instance) esperada = _remoteVersion;
                        if (real != esperada)
                            throw new Exception($"La DLL del repo es v{real} pero version.txt dice v{esperada}");
                        lock (Instance) _remoteBytes = data;
                        CivilizameMPPlugin.Log.LogInfo(
                            $"[Updater] DLL descargada al aceptar: {data.Length} B (v{real})");
                    }

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

            // Instalada: el juego NO se reinicia solo. Mensaje + botón OK,
            // el usuario cierra y reabre el juego para usar la versión nueva.
            _state = State.Idle;
            CivilizameMPPlugin.Log.LogInfo("[Updater] Instalación completada; se aplicará al reiniciar el juego");
            panel?.ShowDone("Actualización Descargada.\n\nReinicia el juego para aplicar.");
        }

        /// <summary>Botón OK del cartel "Actualización Descargada": cierra.</summary>
        private void CloseDonePanel()
        {
            _state = State.Idle;
            MPPanelManager.Instance?.CloseUpdatePanel();
        }

        // ------------------------------------------------------------------
        //  Instalación EN EL ACTO (sin .bat, sin reinicio automático)
        // ------------------------------------------------------------------

        /// <summary>Reemplaza la DLL del mod en plugins. Renombrar la DLL EN
        /// USO funciona en Windows (comprobado con el juego corriendo):
        ///  1) renombra la actual a .old (libera el nombre),
        ///  2) escribe la nueva con el nombre original,
        ///  3) borra la .old (Mono la abre con FILE_SHARE_DELETE; si no se
        ///     pudiera borrar ahora mismo, se limpiará en la próxima update).
        /// La sesión actual sigue con el código viejo en memoria; la nueva
        /// versión se carga al reiniciar el juego.</summary>
        private static void InstallInPlace(byte[] data)
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string target = Path.Combine(pluginDir ?? ".", "CivilizameMP.dll");
            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir))
                throw new Exception("No se encontró la carpeta del mod en BepInEx/plugins");

            string old = target + ".old";
            try { if (File.Exists(old)) File.Delete(old); }
            catch { /* residual de una update anterior aún mapeada */ }

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
