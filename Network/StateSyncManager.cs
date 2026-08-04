using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;
using CivilizameMP.Network;

namespace CivilizameMP.Core
{
    public class StateSyncManager : MonoBehaviour
    {
        private static StateSyncManager _instance;
        public static StateSyncManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("StateSyncManager");
                    _instance = go.AddComponent<StateSyncManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private byte[] _currentState;
        private bool _isStateReady;
        private GameManager _gameManager;

        public void DecompressAndLoad(byte[] stateData)
        {
            if (stateData == null || stateData.Length == 0)
            {
                CivilizameMPPlugin.Log.LogWarning("[StateSync] Estado vacío para descomprimir");
                return;
            }

            try
            {
                CivilizameMPPlugin.Log.LogInfo($"[StateSync] Descomprimiendo {stateData.Length} bytes");
                _currentState = stateData;
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[StateSync] Error descomprimiendo: {ex}");
            }
        }

        public void WriteLoadDataForSync()
        {
            try
            {
                CivilizameMPPlugin.Log.LogInfo("[StateSync] Escribiendo datos de carga para sincronización");
                if (_currentState != null && _currentState.Length > 0)
                {
                    string path = Application.persistentDataPath + "/MP_Sync.sync";
                    File.WriteAllBytes(path, _currentState);
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[StateSync] Error en WriteLoadDataForSync: {ex}");
            }
        }

        public void SaveCurrentState()
        {
            try
            {
                if (_gameManager == null)
                    _gameManager = GameManager.Instance;

                if (_gameManager == null)
                {
                    CivilizameMPPlugin.Log.LogWarning("[StateSync] GameManager no disponible");
                    return;
                }

                var state = new GameStateData
                {
                    TurnOrder = _gameManager.TurnOrder,
                    WorldGenerated = _gameManager.WorldGenerated
                };

                using (var ms = new MemoryStream())
                {
                    var formatter = new BinaryFormatter();
                    formatter.Serialize(ms, state);
                    _currentState = ms.ToArray();
                }

                _isStateReady = true;
                CivilizameMPPlugin.Log.LogInfo($"[StateSync] Estado guardado: {_currentState.Length} bytes");
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[StateSync] Error guardando estado: {ex}");
            }
        }

        public byte[] CompressState()
        {
            if (!_isStateReady || _currentState == null)
            {
                CivilizameMPPlugin.Log.LogWarning("[StateSync] No hay estado listo para comprimir");
                return null;
            }
            return _currentState;
        }

        public void SendState()
        {
            if (!_isStateReady || _currentState == null)
            {
                CivilizameMPPlugin.Log.LogWarning("[StateSync] No hay estado para enviar");
                return;
            }

            var state = CompressState();
            if (state != null && state.Length > 0)
            {
                PhotonManager.Instance?.SendState(state);
                CivilizameMPPlugin.Log.LogInfo($"[StateSync] Estado enviado: {state.Length} bytes");
            }
        }

        public void ReceiveState(byte[] stateData)
        {
            if (stateData == null || stateData.Length == 0)
            {
                CivilizameMPPlugin.Log.LogWarning("[StateSync] Estado recibido vacío");
                return;
            }

            CivilizameMPPlugin.Log.LogInfo($"[StateSync] Estado recibido: {stateData.Length} bytes");
            DecompressAndLoad(stateData);
        }
    }

    [Serializable]
    public class GameStateData
    {
        public int TurnOrder;
        public bool WorldGenerated;
    }
}