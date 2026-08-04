using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CivilizameMP.Core;

namespace CivilizameMP.UI
{
    public abstract class MPPanelBase : MonoBehaviour
    {
        protected RectTransform _rectTransform;
        protected CanvasGroup _canvasGroup;
        
        public bool IsVisible => _canvasGroup != null && _canvasGroup.alpha > 0.01f;
        
        public virtual void Initialize()
        {
            _rectTransform = GetComponent<RectTransform>();
            if (_rectTransform == null)
                _rectTransform = gameObject.AddComponent<RectTransform>();
            
            _rectTransform.anchorMin = Vector2.zero;
            _rectTransform.anchorMax = Vector2.one;
            _rectTransform.sizeDelta = Vector2.zero;
            _rectTransform.anchoredPosition = Vector2.zero;
            
            _canvasGroup = gameObject.GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            
            _canvasGroup.alpha = 0;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            
            BuildUI();
        }

        protected abstract void BuildUI();
        
        public virtual void Show()
        {
            gameObject.SetActive(true);
            StopAllCoroutines();
            StartCoroutine(FadeIn());
        }
        
        public virtual void Hide()
        {
            StopAllCoroutines();
            StartCoroutine(FadeOut());
        }

        private System.Collections.IEnumerator FadeIn()
        {
            _canvasGroup.interactable = true;
            _canvasGroup.blocksRaycasts = true;
            float elapsed = 0;
            while (elapsed < MPConstants.UI_TRANSITION_TIME)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(0, 1, elapsed / MPConstants.UI_TRANSITION_TIME);
                yield return null;
            }
            _canvasGroup.alpha = 1;
        }
        
        private System.Collections.IEnumerator FadeOut()
        {
            _canvasGroup.interactable = false;
            float elapsed = 0;
            while (elapsed < MPConstants.UI_TRANSITION_TIME)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(1, 0, elapsed / MPConstants.UI_TRANSITION_TIME);
                yield return null;
            }
            _canvasGroup.alpha = 0;
            _canvasGroup.blocksRaycasts = false;
            gameObject.SetActive(false);
        }

        protected GameObject CreateBackground()
        {
            var bg = new GameObject("Background");
            bg.transform.SetParent(transform, false);
            var image = bg.AddComponent<Image>();
            image.color = new Color(0.05f, 0.05f, 0.05f, 0.92f);
            var rect = bg.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            return bg;
        }

        protected GameObject CreatePanel(string name, Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            var panel = new GameObject(name);
            panel.transform.SetParent(transform, false);
            var image = panel.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.sizeDelta = Vector2.zero;
            return panel;
        }
        
        protected Button CreateButton(string text, Transform parent, Vector2 position, Vector2 size)
        {
            var buttonObj = new GameObject(text + "Button");
            buttonObj.transform.SetParent(parent, false);
            var image = buttonObj.AddComponent<Image>();
            image.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            image.raycastTarget = true;
            var rect = buttonObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var button = buttonObj.AddComponent<Button>();
            button.targetGraphic = image;
            
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(buttonObj.transform, false);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 24;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            
            return button;
        }
        
        protected TMP_InputField CreateInputField(string placeholder, Transform parent, Vector2 position, Vector2 size)
        {
            var inputObj = new GameObject(placeholder + "Input");
            inputObj.transform.SetParent(parent, false);
            var image = inputObj.AddComponent<Image>();
            image.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            image.raycastTarget = true;
            var rect = inputObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            
            var input = inputObj.AddComponent<TMP_InputField>();
            input.targetGraphic = image;
            
            var placeholderObj = new GameObject("Placeholder");
            placeholderObj.transform.SetParent(inputObj.transform, false);
            var placeholderText = placeholderObj.AddComponent<TextMeshProUGUI>();
            placeholderText.text = placeholder;
            placeholderText.fontSize = 20;
            placeholderText.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            placeholderText.alignment = TextAlignmentOptions.Left;
            placeholderText.raycastTarget = false;
            var phRect = placeholderObj.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = new Vector2(10, 0);
            phRect.offsetMax = new Vector2(-10, 0);
            
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(inputObj.transform, false);
            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.fontSize = 20;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Left;
            text.raycastTarget = false;
            text.richText = false;
            var tRect = textObj.GetComponent<RectTransform>();
            tRect.anchorMin = Vector2.zero;
            tRect.anchorMax = Vector2.one;
            tRect.offsetMin = new Vector2(10, 0);
            tRect.offsetMax = new Vector2(-10, 0);
            
            input.textComponent = text;
            input.placeholder = placeholderText;
            input.text = "";
            input.interactable = true;
            
            return input;
        }
        
        protected TextMeshProUGUI CreateLabel(string text, Transform parent, Vector2 position, float fontSize = 28)
        {
            var labelObj = new GameObject(text + "Label");
            labelObj.transform.SetParent(parent, false);
            var tmp = labelObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            var rect = labelObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(400, 50);
            return tmp;
        }
    }
}