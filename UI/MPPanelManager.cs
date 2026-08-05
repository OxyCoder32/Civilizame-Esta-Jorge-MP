using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using CivilizameMP.Core;

namespace CivilizameMP.UI
{
    public enum MPPanelType
    {
        None,
        MainMenu,
        Host,
        Join,
        Lobby,
        Waiting,
        Error
    }

    public class MPPanelManager : MonoBehaviour
    {
        public static MPPanelManager Instance { get; private set; }
        
        private Canvas _mainCanvas;
        private Dictionary<MPPanelType, MPPanelBase> _panels = new();
        private MPPanelType _currentPanel = MPPanelType.None;
        
        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            CreateCanvas();
        }

        private void CreateCanvas()
        {
            var canvasObj = new GameObject("MPCanvas");
            _mainCanvas = canvasObj.AddComponent<Canvas>();
            _mainCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _mainCanvas.sortingOrder = 100;
            
            var scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            
            canvasObj.AddComponent<GraphicRaycaster>();
            DontDestroyOnLoad(canvasObj);
        }

        public void ShowPanel(MPPanelType panelType)
        {
            if (_currentPanel == panelType) return;
            
            if (_currentPanel != MPPanelType.None && _panels.TryGetValue(_currentPanel, out var current))
            {
                if (current != null) current.Hide();
            }
            
            if (!_panels.TryGetValue(panelType, out var panel) || panel == null)
            {
                CreatePanel(panelType);
                _panels.TryGetValue(panelType, out panel);
            }
            
            if (panel != null)
            {
                panel.Show();
                _currentPanel = panelType;
            }
        }

        public void HideCurrentPanel()
        {
            if (_currentPanel != MPPanelType.None && _panels.TryGetValue(_currentPanel, out var panel))
            {
                if (panel != null) panel.Hide();
                _currentPanel = MPPanelType.None;
            }
        }

        public MPPanelBase GetPanel(MPPanelType type)
        {
            if (!_panels.TryGetValue(type, out var panel) || panel == null)
            {
                CreatePanel(type);
                _panels.TryGetValue(type, out panel);
            }
            return panel;
        }

        private void CreatePanel(MPPanelType panelType)
        {
            GameObject panelObj = null;
            MPPanelBase panel = null;
            
            switch (panelType)
            {
                case MPPanelType.MainMenu:
                    panelObj = new GameObject("MPMainMenuPanel");
                    panel = panelObj.AddComponent<MPMainMenuPanel>();
                    break;
                case MPPanelType.Host:
                    panelObj = new GameObject("MPHostPanel");
                    panel = panelObj.AddComponent<MPHostPanel>();
                    break;
                case MPPanelType.Join:
                    panelObj = new GameObject("MPJoinPanel");
                    panel = panelObj.AddComponent<MPJoinPanel>();
                    break;
                case MPPanelType.Lobby:
                    panelObj = new GameObject("MPLobbyPanel");
                    panel = panelObj.AddComponent<MPLobbyPanel>();
                    break;
                case MPPanelType.Waiting:
                    panelObj = new GameObject("MPWaitingPanel");
                    panel = panelObj.AddComponent<MPWaitingPanel>();
                    break;
                case MPPanelType.Error:
                    panelObj = new GameObject("MPErrorPanel");
                    panel = panelObj.AddComponent<MPErrorPanel>();
                    break;
            }
            
            if (panelObj != null && panel != null)
            {
                panelObj.transform.SetParent(_mainCanvas.transform, false);
                panel.Initialize();
                _panels[panelType] = panel;
            }
        }

        public void ShowError(string message)
        {
            if (!_panels.TryGetValue(MPPanelType.Error, out var panel) || panel == null)
                CreatePanel(MPPanelType.Error);
            
            if (_panels.TryGetValue(MPPanelType.Error, out var errorPanel) && errorPanel is MPErrorPanel ep)
            {
                ep.SetMessage(message);
                ShowPanel(MPPanelType.Error);
            }
        }

        public void DestroyAllPanels()
        {
            foreach (var kvp in _panels)
            {
                if (kvp.Value != null) 
                    Destroy(kvp.Value.gameObject);
            }
            _panels.Clear();
            _currentPanel = MPPanelType.None;
        }
    }
}