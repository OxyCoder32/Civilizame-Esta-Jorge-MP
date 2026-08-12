using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

namespace CivilizameMP.UI
{
    public class MPWaitingPanel : MPPanelBase
    {
        private static MPWaitingPanel _instance;
        public static MPWaitingPanel Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<MPWaitingPanel>();
                    
                    if (_instance == null)
                    {
                        var go = new GameObject("MPWaitingPanel");
                        _instance = go.AddComponent<MPWaitingPanel>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }

        private TextMeshProUGUI _statusText;
        private TextMeshProUGUI _subText;
        private Image _spinnerImage;
        private bool _isSpinning;
        private bool _isInitialized;
        private Canvas _panelCanvas;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        protected override void BuildUI()
        {
            if (_isInitialized) return;
            
            if (_rectTransform == null)
                _rectTransform = GetComponent<RectTransform>();
            
            if (_rectTransform == null)
                _rectTransform = gameObject.AddComponent<RectTransform>();
            
            _rectTransform.anchorMin = Vector2.zero;
            _rectTransform.anchorMax = Vector2.one;
            _rectTransform.sizeDelta = Vector2.zero;
            _rectTransform.anchoredPosition = Vector2.zero;

            _panelCanvas = gameObject.GetComponent<Canvas>();
            if (_panelCanvas == null)
                _panelCanvas = gameObject.AddComponent<Canvas>();
            
            _panelCanvas.overrideSorting = true;
            _panelCanvas.sortingOrder = 20;
            
            var raycaster = gameObject.GetComponent<GraphicRaycaster>();
            if (raycaster == null)
                gameObject.AddComponent<GraphicRaycaster>();

            var blocker = new GameObject("Blocker");
            blocker.transform.SetParent(transform, false);
            var blockerImg = blocker.AddComponent<Image>();
            blockerImg.color = new Color(0f, 0f, 0f, 1f);
            blockerImg.raycastTarget = true;
            
            var blockerRect = blocker.GetComponent<RectTransform>();
            blockerRect.anchorMin = Vector2.zero;
            blockerRect.anchorMax = Vector2.one;
            blockerRect.sizeDelta = Vector2.zero;
            blockerRect.anchoredPosition = Vector2.zero;

            blocker.transform.SetAsLastSibling();

            var content = new GameObject("Content");
            content.transform.SetParent(transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.5f, 0.5f);
            contentRect.anchorMax = new Vector2(0.5f, 0.5f);
            contentRect.sizeDelta = new Vector2(500, 250);

            var spinnerObj = new GameObject("Spinner");
            spinnerObj.transform.SetParent(content.transform, false);
            _spinnerImage = spinnerObj.AddComponent<Image>();
            _spinnerImage.color = new Color(0.8f, 0.8f, 0.8f, 1f);
            
            var spinnerRect = spinnerObj.GetComponent<RectTransform>();
            spinnerRect.anchorMin = new Vector2(0.5f, 0.5f);
            spinnerRect.anchorMax = new Vector2(0.5f, 0.5f);
            spinnerRect.anchoredPosition = new Vector2(0, 40);
            spinnerRect.sizeDelta = new Vector2(50, 50);
            
            _statusText = CreateLabel("ESPERANDO OPONENTE", content.transform, new Vector2(0, -20), 32);
            _statusText.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            
            _subText = CreateLabel("Sincronizando partida...", content.transform, new Vector2(0, -60), 18);
            _subText.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            
            if (_canvasGroup == null)
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            
            _canvasGroup.alpha = 0;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            
            transform.SetAsLastSibling();
            
            gameObject.SetActive(false);
            _isInitialized = true;
        }

        public override void Show()
        {
            if (!_isInitialized) BuildUI();
            
            transform.SetAsLastSibling();
            if (_panelCanvas != null)
                _panelCanvas.sortingOrder = 20;
            
            gameObject.SetActive(true);
            StopAllCoroutines();
            StartCoroutine(FadeIn());
            
            if (_spinnerImage != null)
            {
                _isSpinning = true;
                StartCoroutine(SpinSpinner());
            }
        }

        public override void Hide()
        {
            _isSpinning = false;
            StopAllCoroutines();
            
            if (_canvasGroup != null)
            {
                StartCoroutine(FadeOut());
            }
            else
            {
                gameObject.SetActive(false);
            }
        }

        public void SetStatus(string mainText, string subText = null)
        {
            if (!_isInitialized) BuildUI();
            
            if (_statusText != null) 
                _statusText.text = mainText;
            
            if (_subText != null && subText != null) 
                _subText.text = subText;
        }

        private IEnumerator SpinSpinner()
        {
            while (_isSpinning && _spinnerImage != null)
            {
                _spinnerImage.rectTransform.Rotate(0, 0, -200 * Time.deltaTime);
                yield return null;
            }
        }

        private IEnumerator FadeIn()
        {
            if (_canvasGroup == null) yield break;
            
            _canvasGroup.interactable = true;
            _canvasGroup.blocksRaycasts = true;
            float elapsed = 0;
            float duration = 0.3f;
            
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(0, 1, elapsed / duration);
                yield return null;
            }
            _canvasGroup.alpha = 1;
        }

        private IEnumerator FadeOut()
        {
            if (_canvasGroup == null) 
            {
                gameObject.SetActive(false);
                yield break;
            }
            
            _canvasGroup.interactable = false;
            float elapsed = 0;
            float duration = 0.3f;
            
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(1, 0, elapsed / duration);
                yield return null;
            }
            _canvasGroup.alpha = 0;
            _canvasGroup.blocksRaycasts = false;
            gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            _isSpinning = false;
            if (_instance == this)
                _instance = null;
        }
    }
}