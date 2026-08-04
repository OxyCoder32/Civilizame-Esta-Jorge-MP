using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CivilizameMP.UI;
using CivilizameMP.Core;

namespace CivilizameMP.Patches
{
    [HarmonyPatch(typeof(MenuUIManager))]
    public class MenuUIManagerPatches
    {
        private static GameObject _mpButton;
        private static bool _injected;

        [HarmonyPatch("Start")]
        [HarmonyPostfix]
        public static void StartPostfix(MenuUIManager __instance)
        {
            if (!_injected)
            {
                InjectMultiplayerButton(__instance);
                _injected = true;
            }
            SyncVisibility(__instance);
        }

        private static void InjectMultiplayerButton(MenuUIManager menuUI)
        {
            var startButton = FindStartButton(menuUI.transform);
            if (startButton == null)
            {
                CivilizameMPPlugin.Log.LogWarning("No se encontró botón Start para clonar");
                return;
            }

            try
            {
                _mpButton = Object.Instantiate(startButton.gameObject, startButton.parent);
                _mpButton.name = "MPButton";
                
                var refRect = startButton.GetComponent<RectTransform>();
                var mpRect = _mpButton.GetComponent<RectTransform>();
                
                mpRect.SetSiblingIndex(refRect.GetSiblingIndex() + 1);
                mpRect.anchoredPosition = new Vector2(
                    refRect.anchoredPosition.x,
                    refRect.anchoredPosition.y - refRect.sizeDelta.y - 75f
                );
                
                var textComponent = _mpButton.GetComponentInChildren<TMP_Text>();
                if (textComponent != null)
                    textComponent.text = MPConstants.MULTIPLAYER_BUTTON_TEXT;
                
                var button = _mpButton.GetComponent<Button>();
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(OnMultiplayerButtonClick);
                
                var refAnimator = startButton.GetComponent<Animator>();
                if (refAnimator != null)
                {
                    var mpAnimator = _mpButton.GetComponent<Animator>();
                    if (mpAnimator != null)
                        mpAnimator.runtimeAnimatorController = refAnimator.runtimeAnimatorController;
                }
                
                CivilizameMPPlugin.Log.LogInfo("Botón Multiplayer inyectado");
            }
            catch (System.Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"Error inyectando botón MP: {ex}");
            }
        }

        private static void SyncVisibility(MenuUIManager menuUI)
        {
            if (_mpButton == null) return;
            
            var startButton = FindStartButton(menuUI.transform);
            bool startActive = startButton != null && startButton.gameObject.activeInHierarchy;
            _mpButton.SetActive(startActive);
        }

        private static void OnMultiplayerButtonClick()
        {
            CivilizameMPPlugin.Log.LogInfo("Botón Multiplayer pulsado");
            MPStateManager.Instance.SetState(MPGameState.InMenu);
            MPPanelManager.Instance.ShowPanel(MPPanelType.MainMenu);
        }

        private static Transform FindStartButton(Transform parent)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == "Start" && child.GetComponent<Button>() != null)
                    return child;
            }
            return null;
        }
    }
}