using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace CivilizameMP.UI
{
    public class MPConnectionIndicator : MonoBehaviour
    {
        public static MPConnectionIndicator Instance { get; private set; }
        
        [SerializeField] private Image _statusDot;
        [SerializeField] private TextMeshProUGUI _statusText;
        private GameObject _container;
        private bool _created;
        
        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            CreateIndicator();
        }

        private void CreateIndicator()
        {
            if (_created) return;
            
            Canvas canvas = null;
            var canvases = Object.FindObjectsOfType<Canvas>();
            foreach (var c in canvases)
            {
                if (c.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    canvas = c;
                    break;
                }
            }
            
            if (canvas == null)
            {
                var canvasObj = new GameObject("MPIndicatorCanvas");
                canvas = canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 99;
                canvasObj.AddComponent<CanvasScaler>();
                canvasObj.AddComponent<GraphicRaycaster>();
            }
            
            _container = new GameObject("ConnectionIndicator");
            _container.transform.SetParent(canvas.transform, false);
            
            var containerRect = _container.AddComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(1, 1);
            containerRect.anchorMax = new Vector2(1, 1);
            containerRect.anchoredPosition = new Vector2(-150, -40);
            containerRect.sizeDelta = new Vector2(200, 30);
            
            var dotObj = new GameObject("StatusDot");
            dotObj.transform.SetParent(_container.transform, false);
            _statusDot = dotObj.AddComponent<Image>();
            _statusDot.color = Color.red;
            
            var dotRect = dotObj.GetComponent<RectTransform>();
            dotRect.anchorMin = new Vector2(0, 0.5f);
            dotRect.anchorMax = new Vector2(0, 0.5f);
            dotRect.anchoredPosition = new Vector2(15, 0);
            dotRect.sizeDelta = new Vector2(16, 16);
            
            var textObj = new GameObject("StatusText");
            textObj.transform.SetParent(_container.transform, false);
            _statusText = textObj.AddComponent<TextMeshProUGUI>();
            _statusText.text = "● Offline";
            _statusText.fontSize = 16;
            _statusText.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            _statusText.alignment = TextAlignmentOptions.Left;
            
            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0, 0);
            textRect.anchorMax = new Vector2(1, 1);
            textRect.offsetMin = new Vector2(35, 0);
            textRect.offsetMax = new Vector2(0, 0);
            
            _container.SetActive(false);
            _created = true;
        }

        public void Show() => _container?.SetActive(true);
        public void Hide() => _container?.SetActive(false);

        public void SetStatus(ConnectionStatus status, string ping = "")
        {
            if (_statusDot == null || _statusText == null) return;
            
            switch (status)
            {
                case ConnectionStatus.Connected:
                    _statusDot.color = Color.green;
                    _statusText.text = $"● Online {ping}";
                    break;
                case ConnectionStatus.Connecting:
                    _statusDot.color = Color.yellow;
                    _statusText.text = "● ...";
                    break;
                case ConnectionStatus.Disconnected:
                    _statusDot.color = Color.red;
                    _statusText.text = "● Offline";
                    break;
                case ConnectionStatus.Error:
                    _statusDot.color = Color.red;
                    _statusText.text = "● Error";
                    break;
            }
        }

        public enum ConnectionStatus
        {
            Disconnected,
            Connecting,
            Connected,
            Error
        }
    }
}