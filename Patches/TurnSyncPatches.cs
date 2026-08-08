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

        [HarmonyPostfix]
        public static void Postfix(GameManager __instance)
        {
            if (!MPStateManager.Instance.IsMultiplayerActive) return;
            if (__instance == null) return;
            if (__instance.TurnOrder < 0) return;

            var currentPlayer = __instance.Jug();
            bool isHumanTurn = currentPlayer != null && currentPlayer.RealPlayer;

            UpdateTurnUI(__instance, isHumanTurn);

            if (!isHumanTurn || !MPStateManager.Instance.IsHost)
            {
                return;
            }

            if (_lastBroadcastTurnOrder == __instance.TurnOrder)
            {
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Se omite reenvío para el turno {__instance.TurnOrder}");
                return;
            }

            if (MPStateManager.Instance != null)
            {
                MPStateManager.Instance.StartCoroutine(DelayAndBroadcastTurnState(__instance));
            }
        }

        private static IEnumerator DelayAndBroadcastTurnState(GameManager gm)
        {
            yield return new WaitForSeconds(0.15f);

            if (gm == null || !MPStateManager.Instance.IsHost || !MPStateManager.Instance.IsMultiplayerActive)
            {
                yield break;
            }

            var currentPlayer = gm.Jug();
            if (currentPlayer == null || !currentPlayer.RealPlayer)
            {
                yield break;
            }

            if (_lastBroadcastTurnOrder == gm.TurnOrder)
            {
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Se omite reenvío para el turno {gm.TurnOrder}");
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
                    yield break;
                }

                CivilizameMPPlugin.Log.LogWarning($"[TurnSync] Intento {attempt} fallido al guardar/capturar estado: {lastException?.Message}");
                yield return new WaitForSeconds(0.1f);
            }
        }

        private static void UpdateTurnUI(GameManager gm, bool isHumanTurn)
        {
            if (gm == null || MPMatchState.MiIndiceLocal < 0) return;

            if (!isHumanTurn)
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo("[TurnSync] Turno de IA/host: sin cartel de espera");
                return;
            }

            if (gm.TurnOrder == MPMatchState.MiIndiceLocal)
            {
                MPWaitingPanel.Instance?.Hide();
                MPPanelManager.Instance?.HideCurrentPanel();
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] ¡Es tu turno! (Índice: {MPMatchState.MiIndiceLocal})");
            }
            else
            {
                MPWaitingPanel.Instance?.SetStatus("ESPERANDO TU TURNO", $"Turno del jugador {gm.TurnOrder + 1}");
                MPPanelManager.Instance?.HideCurrentPanel();
                MPPanelManager.Instance?.ShowPanel(MPPanelType.Waiting);
                CivilizameMPPlugin.Log.LogInfo($"[TurnSync] Turno del jugador {gm.TurnOrder}");
            }
        }
    }
}