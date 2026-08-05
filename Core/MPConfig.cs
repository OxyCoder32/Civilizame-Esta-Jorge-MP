using BepInEx.Configuration;

namespace CivilizameMP.Core
{
    public static class MPConfig
    {
        public static ConfigEntry<string> PhotonAppID { get; private set; }
        public static ConfigEntry<string> DefaultPlayerName { get; private set; }
        public static ConfigEntry<bool> EnableLogging { get; private set; }
        
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