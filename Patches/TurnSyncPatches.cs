using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using CivilizameMP.Core;
using CivilizameMP.Network;
using CivilizameMP.UI;
using UnityEngine;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(GameManager), "NextTurn")]
    public class NextTurnPatch
    {
        private static int _lastBroadcastTurnOrder = -1;
        private static bool _isHostBroadcasting;
        private static bool _isClientBroadcasting;
        private static int _lastClientSentTurnOrder = -1;

        [HarmonyPostfix]
        public static void Postfix(GameManager __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return;
            if (__instance == null) return;
            if (__instance.TurnOrder < 0) return;

            UpdateTurnUI(__instance);

            if (MPStateManager.Instance.IsHost && HostManager.Instance != null && HostManager.Instance.IsGeneratingWorld)
            {
                CivilizameMPPlugin.Log.LogInfo("[TurnSync] Host generando mundo - turno no sincronizado");
                return;
            }

            if (!MPStateManager.Instance.IsHost && MPMatchState.IsInitialized)
            {
                if (__instance.TurnOrder != MPMatchState.MiIndiceLocal && _lastClientSentTurnOrder != __instance.TurnOrder)
                {
                    if (MPStateManager.Instance != null && !_isClientBroadcasting)
                    {
                        MPStateManager.Instance.StartCoroutine(SendAndBroadcastTurnState(__instance));
                    }
                }
                return;
            }

            if (!MPStateManager.Instance.IsHost) return;

            if (!MPMatchState.IsRemoteHumanTurn(__instance)) return;

            if (_lastBroadcastTurnOrder == __instance.TurnOrder)
            {
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Se omite reenvío para el turno {__instance.TurnOrder}");
                return;
            }

            if (_isHostBroadcasting) return;

            if (MPStateManager.Instance != null)
            {
                MPStateManager.Instance.StartCoroutine(DelayAndBroadcastTurnState(__instance));
            }
        }

        private static IEnumerator SendAndBroadcastTurnState(GameManager gm)
        {
            _isClientBroadcasting = true;
            yield return new WaitForSeconds(0.15f);

            if (gm == null || !MPStateManager.Instance.IsMultiplayerActive)
            {
                _isClientBroadcasting = false;
                yield break;
            }

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Exception lastException = null;
                try
                {
                    var infoToFile = gm.GetComponent<InformationToFile>();
                    if (infoToFile != null) infoToFile.GuardadoSeguridad();
                    if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();

                    string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                    if (!File.Exists(path))
                    {
                        CivilizameMPPlugin.Log.LogWarning($"[TurnSync] Intento {attempt}: no existe el save para sincronizar el turno");
                        lastException = new FileNotFoundException(path);
                    }
                    else
                    {
                        byte[] worldData = File.ReadAllBytes(path);
                        PhotonManager.Instance.SendState(worldData);
                        _lastClientSentTurnOrder = gm.TurnOrder;
                        CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Cliente envió estado al host: {worldData.Length} bytes");
                        _isClientBroadcasting = false;
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                if (attempt >= 2)
                {
                    CivilizameMPPlugin.Log.LogError($"[TurnSync] Error final al sincronizar turno: {lastException}");
                    _isClientBroadcasting = false;
                    yield break;
                }

                CivilizameMPPlugin.Log.LogWarning($"[TurnSync] Intento {attempt} fallido al guardar/capturar estado: {lastException?.Message}");
                yield return new WaitForSeconds(0.1f);
            }
            _isClientBroadcasting = false;
        }

        private static IEnumerator DelayAndBroadcastTurnState(GameManager gm)
        {
            _isHostBroadcasting = true;
            yield return new WaitForSeconds(0.15f);

            if (gm == null || !MPStateManager.Instance.IsHost || !MPStateManager.Instance.IsMultiplayerActive)
            {
                _isHostBroadcasting = false;
                yield break;
            }

            if (!MPMatchState.IsRemoteHumanTurn(gm))
            {
                _isHostBroadcasting = false;
                yield break;
            }

            if (_lastBroadcastTurnOrder == gm.TurnOrder)
            {
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Se omite reenvío para el turno {gm.TurnOrder}");
                _isHostBroadcasting = false;
                yield break;
            }

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Exception lastException = null;
                try
                {
                    var infoToFile = gm.GetComponent<InformationToFile>();
                    if (infoToFile != null) infoToFile.GuardadoSeguridad();
                    if (Tablero.Instance != null) Tablero.Instance.GuardadoSeg();

                    string path = Application.persistentDataPath + "/GuardadoSeguridad.jue";
                    if (!File.Exists(path))
                    {
                        CivilizameMPPlugin.Log.LogWarning($"[TurnSync] Intento {attempt}: no existe el save para sincronizar el turno");
                        lastException = new FileNotFoundException(path);
                    }
                    else
                    {
                        byte[] worldData = File.ReadAllBytes(path);
                        PhotonManager.Instance.SendState(worldData);
                        _lastBroadcastTurnOrder = gm.TurnOrder;

                        CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Turno cambiado a {gm.TurnOrder} - estado enviado");
                        _isHostBroadcasting = false;
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                if (attempt >= 2)
                {
                    CivilizameMPPlugin.Log.LogError($"[TurnSync] Error final al sincronizar turno: {lastException}");
                    _isHostBroadcasting = false;
                    yield break;
                }

                CivilizameMPPlugin.Log.LogWarning($"[TurnSync] Intento {attempt} fallido al guardar/capturar estado: {lastException?.Message}");
                yield return new WaitForSeconds(0.1f);
            }
            _isHostBroadcasting = false;
        }

        private static void UpdateTurnUI(GameManager gm)
        {
            if (gm == null || MPMatchState.MiIndiceLocal < 0) return;

            if (MPMatchState.IsAITurn(gm))
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo("[TurnSync] Turno de IA: sin cartel de espera");
                return;
            }

            if (MPMatchState.IsLocalTurn(gm))
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] ¡Es tu turno! (Índice: {MPMatchState.MiIndiceLocal})");
                return;
            }

            if (MPMatchState.IsRemoteHumanTurn(gm))
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO TU TURNO", $"Turno del jugador {gm.TurnOrder + 1}");
                MPPanelManager.Instance?.HideCurrentPanel();
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Turno del jugador {gm.TurnOrder}");
            }
        }
    }
}