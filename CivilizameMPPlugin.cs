using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using CivilizameMP.Core;
using CivilizameMP.Network;
using CivilizameMP.UI;

namespace CivilizameMP
{
    [BepInPlugin("com.civilizamemp.mod", "Civilízame Multiplayer", "1.0.0")]
    public class CivilizameMPPlugin : BaseUnityPlugin
    {
        public static CivilizameMPPlugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }
        
        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            
            Logger.LogInfo("=== CivilizameMP Inicializando ===");
            
            var stateManager = new GameObject("MPStateManager");
            stateManager.AddComponent<MPStateManager>();
            DontDestroyOnLoad(stateManager);
            
            MPConfig.Load(Config);
            
            _harmony = new Harmony("com.civilizamemp.mod");
            _harmony.PatchAll();
            
            Logger.LogInfo("Parches Harmony registrados correctamente");

            var photonObj = new GameObject("PhotonManager");
            photonObj.AddComponent<PhotonManager>();
            DontDestroyOnLoad(photonObj);

            var syncObj = new GameObject("StateSyncManager");
            syncObj.AddComponent<StateSyncManager>();
            DontDestroyOnLoad(syncObj);

            var hostObj = new GameObject("HostManager");
            hostObj.AddComponent<HostManager>();
            DontDestroyOnLoad(hostObj);

            var clientObj = new GameObject("ClientManager");
            clientObj.AddComponent<ClientManager>();
            DontDestroyOnLoad(clientObj);

            var panelObj = new GameObject("MPPanelManager");
            panelObj.AddComponent<MPPanelManager>();
            DontDestroyOnLoad(panelObj);

            Logger.LogInfo("CivilizameMP inicializado correctamente");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            Logger.LogInfo("CivilizameMP desinstalado");
        }
    }
}