using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    /// <summary>
    /// Parches "de observación" del menú vanilla (los postfix no alteran el
    /// comportamiento del juego): en el HOST conectado a una sala, cualquier
    /// cambio en el panel de configuración de partida se captura y se
    /// transmite a los clientes vía SetupSync.
    /// </summary>
    [HarmonyPatch]
    public static class SetupSyncPatches
    {
        private static bool ShouldSync =>
            PhotonManager.Instance != null && PhotonManager.Instance.IsInRoom &&
            MPStateManager.Instance != null && MPStateManager.Instance.IsHost &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == MPConstants.MENU_SCENE;

        // ---------------- INICIAR PARTIDA vanilla / Cargar partida ----------------

        [HarmonyPatch(typeof(GameSettings), "PlayGame")]
        [HarmonyPrefix]
        public static void PlayGamePrefix()
        {
            if (ShouldSync)
                SetupSync.TryHostCaptureAndBroadcast(true);
        }

        [HarmonyPatch(typeof(GameSettings), "LoadGame")]
        [HarmonyPrefix]
        public static void LoadGamePrefix()
        {
            if (ShouldSync)
                SetupSync.TryHostCaptureAndBroadcast(true);
        }

        // ---------------- Cambios en vivo del panel de configuración ----------------

        [HarmonyPatch(typeof(GameSettings), "ChangeMaxPlayer")]
        [HarmonyPostfix]
        public static void ChangeMaxPlayerPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        [HarmonyPatch(typeof(GameSettings), "ChangeNumPlayers")]
        [HarmonyPostfix]
        public static void ChangeNumPlayersPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        // CPU <-> Real (toggle del panel de jugadores)
        [HarmonyPatch(typeof(GameSettings), "ChangeCPUoJug")]
        [HarmonyPostfix]
        public static void ChangeCPUoJugPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        // Elección de líder de un slot
        [HarmonyPatch(typeof(GameSettings), "AddLeader")]
        [HarmonyPostfix]
        public static void AddLeaderPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        // Nombres de jugadores (input del panel)
        [HarmonyPatch(typeof(MenuUIManager), "SetNames")]
        [HarmonyPostfix]
        public static void SetNamesPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        // Mapa: tamaño / tipo / carga
        [HarmonyPatch(typeof(GameSettings), "MapRnd")]
        [HarmonyPostfix]
        public static void MapRndPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        [HarmonyPatch(typeof(GameSettings), "MapRndTyp")]
        [HarmonyPostfix]
        public static void MapRndTypPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        [HarmonyPatch(typeof(GameSettings), "CargaMapa")]
        [HarmonyPostfix]
        public static void CargaMapaPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        // Dificultad y opciones varias
        [HarmonyPatch(typeof(GameSettings), "SetDificultad")]
        [HarmonyPostfix]
        public static void SetDificultadPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        [HarmonyPatch(typeof(GameSettings), "SwitchRndRelig")]
        [HarmonyPostfix]
        public static void SwitchRndReligPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        [HarmonyPatch(typeof(GameSettings), "SwitchTutorial")]
        [HarmonyPostfix]
        public static void SwitchTutorialPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        [HarmonyPatch(typeof(GameSettings), "SetNoLideresRepes")]
        [HarmonyPostfix]
        public static void SetNoLideresRepesPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }

        [HarmonyPatch(typeof(GameSettings), "SetGuardadoSeguridad")]
        [HarmonyPostfix]
        public static void SetGuardadoSeguridadPostfix() { if (ShouldSync) SetupSync.TryHostCaptureAndBroadcast(false); }
    }
}
