using BepInEx.Configuration;

namespace CivilizameMP.Core
{
    public static class MPConfig
    {
        public static ConfigEntry<string> PhotonAppID { get; private set; }
        public static ConfigEntry<string> DefaultPlayerName { get; private set; }
        public static ConfigEntry<bool> EnableLogging { get; private set; }
        public static ConfigEntry<string> UpdateUrl { get; private set; }
        public static ConfigEntry<string> UpdateVersionUrl { get; private set; }
        public static ConfigEntry<string> IgnoredUpdateVersion { get; private set; }

        /// <summary>Fuente original de la configuración (para guardar de
        /// inmediato al marcar "no volver a preguntar" en el actualizador).</summary>
        public static ConfigFile Source { get; private set; }
        
        public static void Load(ConfigFile config)
        {
            PhotonAppID = config.Bind(
                "Network", 
                "PhotonAppID", 
                "YOUR_PHOTON_APPID_HERE", 
                "AppID de Photon Cloud. Obtén uno gratis en https://dashboard.photonengine.com\n" +
                "1. Ve a https://dashboard.photonengine.com\n" +
                "2. Crea una cuenta\n" +
                "3. Crea una aplicación F2P (Free)\n" +
                "4. Copia el AppID de la sección 'App ID'"
            );
            
            DefaultPlayerName = config.Bind(
                "General", 
                "DefaultPlayerName", 
                "Jugador", 
                "Nombre por defecto del jugador"
            );
            
            EnableLogging = config.Bind(
                "Debug", 
                "EnableLogging", 
                true, 
                "Activar logging detallado en BepInEx"
            );

            Source = config;

            UpdateUrl = config.Bind(
                "Updater",
                "UpdateUrl",
                "https://raw.githubusercontent.com/OxyCoder32/Civilizame-Esta-Jorge-MP/main/Compiled/CivilizameMP.dll",
                "URL raw de la DLL oficial del mod en GitHub. Se descarga SOLO al\n" +
                "aceptar instalar la actualización."
            );

            UpdateVersionUrl = config.Bind(
                "Updater",
                "UpdateVersionUrl",
                "https://raw.githubusercontent.com/OxyCoder32/Civilizame-Esta-Jorge-MP/main/Compiled/version.txt",
                "URL raw de Compiled/version.txt: un archivo de texto con SOLO el\n" +
                "número de versión publicada (p.ej. 1.0.1). Es lo único que se\n" +
                "consulta en cada arranque. IMPORTANTE: sube este archivo a la\n" +
                "vez que cada DLL nueva."
            );

            IgnoredUpdateVersion = config.Bind(
                "Updater",
                "IgnoredUpdateVersion",
                "",
                "Versión que NO se volverá a ofrecer (se rellena al marcar\n" +
                "'No volver a preguntar por esta versión' en el cartel de actualización)."
            );
            
            // Verificar AppID
            if (PhotonAppID.Value == "YOUR_PHOTON_APPID_HERE")
            {
                CivilizameMPPlugin.Log.LogWarning("⚠️  PHOTON APPID NO CONFIGURADO");
            }
            else
            {
                CivilizameMPPlugin.Log.LogInfo($"✅ Photon AppID configurado: {PhotonAppID.Value.Substring(0, 4)}...");
            }
            
            CivilizameMPPlugin.Log.LogInfo("Configuración cargada");
        }
    }
}