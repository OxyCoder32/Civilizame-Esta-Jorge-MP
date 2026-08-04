using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CivilizameMP.Core;
using CivilizameMP.UI;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(EscMenu))]
    public class EscMenuPatches
    {
        private static GameObject _mpSection;

        [HarmonyPatch("Start")]
        [HarmonyPostfix]
        public static void StartPostfix(EscMenu __instance)
        {
            if (_mpSection != null) return;
            
            try
            {
                InjectMultiplayerSection(__instance);
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"Error inyectando MP en EscMenu: {ex}");
            }
        }

        private static void InjectMultiplayerSection(EscMenu escMenu)
        {
            var botones3 = escMenu.GetEscGuardYCarg();
            if (botones3 == null)
            {
                CivilizameMPPlugin.Log.LogWarning("GetEscGuardYCarg() devolvió null");
                return;
            }

            var botones1Field = AccessTools.Field(typeof(EscGuardaoYCargadoMenu), "Botones1");
            if (botones1Field == null)
            {
                CivilizameMPPlugin.Log.LogWarning("No se encontró campo Botones1");
                return;
            }

            var botones1 = botones1Field.GetValue(botones3) as GameObject[];
            if (botones1 == null || botones1.Length == 0) return;
            
            _mpSection = new GameObject("MPSection");
            _mpSection.transform.SetParent(botones3.transform, false);
            
            var sectionRect = _mpSection.AddComponent<RectTransform>();
            sectionRect.anchorMin = new Vector2(0.5f, 0.5f);
            sectionRect.anchorMax = new Vector2(0.5f, 0.5f);
            sectionRect.pivot = new Vector2(0.5f, 0.5f);
            sectionRect.sizeDelta = new Vector2(300, 120f);
            
            var separator = new GameObject("MPSeparator");
            separator.transform.SetParent(_mpSection.transform, false);
            var sepImage = separator.AddComponent<Image>();
            sepImage.color = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            var sepRect = separator.GetComponent<RectTransform>();
            sepRect.anchorMin = new Vector2(0.5f, 1);
            sepRect.anchorMax = new Vector2(0.5f, 1);
            sepRect.pivot = new Vector2(0.5f, 1);
            sepRect.anchoredPosition = Vector2.zero;
            sepRect.sizeDelta = new Vector2(300, 2);
            
            var titleObj = new GameObject("MPTitle");
            titleObj.transform.SetParent(_mpSection.transform, false);
            var titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = "MULTIPLAYER";
            titleText.fontSize = 20;
            titleText.color = new Color(0.8f, 0.6f, 0.2f, 1f);
            titleText.alignment = TextAlignmentOptions.Center;
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 1);
            titleRect.anchorMax = new Vector2(0.5f, 1);
            titleRect.pivot = new Vector2(0.5f, 1);
            titleRect.anchoredPosition = new Vector2(0, -5);
            titleRect.sizeDelta = new Vector2(300, 30);
            
            var infoObj = new GameObject("MPInfo");
            infoObj.transform.SetParent(_mpSection.transform, false);
            var infoText = infoObj.AddComponent<TextMeshProUGUI>();
            infoText.text = "Estado: Offline";
            infoText.fontSize = 16;
            infoText.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            infoText.alignment = TextAlignmentOptions.Center;
            var infoRect = infoObj.GetComponent<RectTransform>();
            infoRect.anchorMin = new Vector2(0.5f, 1);
            infoRect.anchorMax = new Vector2(0.5f, 1);
            infoRect.pivot = new Vector2(0.5f, 1);
            infoRect.anchoredPosition = new Vector2(0, -35);
            infoRect.sizeDelta = new Vector2(300, 25);
            
            var leaveBtn = CreateMPButton("Abandonar Partida MP", _mpSection.transform);
            var leaveRect = leaveBtn.GetComponent<RectTransform>();
            leaveRect.anchorMin = new Vector2(0.5f, 1);
            leaveRect.anchorMax = new Vector2(0.5f, 1);
            leaveRect.pivot = new Vector2(0.5f, 1);
            leaveRect.anchoredPosition = new Vector2(0, -65);
            leaveRect.sizeDelta = new Vector2(280, 45);
            
            leaveBtn.GetComponent<Button>().onClick.AddListener(() => {
                CivilizameMPPlugin.Log.LogInfo("Abandonando partida MP...");
                PhotonManager.Instance?.Disconnect();
                MPStateManager.Instance.Reset();
                _mpSection.SetActive(false);
            });
            
            var lastButton = botones1[botones1.Length - 1];
            if (lastButton != null)
            {
                var lastRect = lastButton.GetComponent<RectTransform>();
                sectionRect.anchoredPosition = new Vector2(
                    lastRect.anchoredPosition.x,
                    lastRect.anchoredPosition.y - lastRect.sizeDelta.y - 30f
                );
            }
            
            _mpSection.SetActive(false);
            CivilizameMPPlugin.Log.LogInfo("Sección MP inyectada en EscMenu");
        }

        private static GameObject CreateMPButton(string text, Transform parent)
        {
            var btnObj = new GameObject(text);
            btnObj.transform.SetParent(parent, false);
            
            var image = btnObj.AddComponent<Image>();
            image.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
            
            var btn = btnObj.AddComponent<Button>();
            btn.targetGraphic = image;
            
            var rect = btnObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(280, 45);
            
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(btnObj.transform, false);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 20;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            
            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            
            return btnObj;
        }

        [HarmonyPatch("GoBack")]
        [HarmonyPostfix]
        public static void GoBackPostfix()
        {
            if (_mpSection != null)
                _mpSection.SetActive(false);
        }

        [HarmonyPatch(typeof(EscGuardaoYCargadoMenu), "ChooseAspecto")]
        [HarmonyPostfix]
        public static void ChooseAspectoPostfix(bool guardando)
        {
            if (_mpSection == null) return;
            
            bool shouldShow = MPStateManager.Instance.IsMultiplayerActive;
            _mpSection.SetActive(shouldShow);
            
            if (shouldShow)
                UpdateMPInfo();
        }

        private static void UpdateMPInfo()
        {
            var infoText = _mpSection?.transform.Find("MPInfo")?.GetComponent<TextMeshProUGUI>();
            if (infoText == null) return;
            
            var state = MPStateManager.Instance;
            string status = state.IsHost ? "Host" : "Cliente";
            string opponent = string.IsNullOrEmpty(state.RemotePlayerName) ? "???" : state.RemotePlayerName;
            infoText.text = $"Estado: {status} | Oponente: {opponent}";
        }
    }
}