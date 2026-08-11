using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using Photon.Realtime;
using CivilizameMP.Core;
using CivilizameMP.Network;
using System.Collections.Generic;

namespace CivilizameMP.UI
{
    public class MPLobbyPanel : MPPanelBase
    {
        private TextMeshProUGUI _roomNameLabel;
        private Button _copyButton;
        private Toggle _readyToggle;
        private Button _startButton;
        private Button _leaveButton;
        private TextMeshProUGUI _statusLabel;
        private bool _localReady;
        private bool _remoteReady;
        private bool _eventsSubscribed;
        private List<GameObject> _slotUIs = new List<GameObject>();
        private bool _gameStarting;

        protected override void BuildUI()
        {
            CreateBackground();
            CreateLabel("SALA DE ESPERA", transform, new Vector2(0, 220), 42);
            
            var roomBg = new GameObject("RoomBg");
            roomBg.transform.SetParent(transform, false);
            var roomBgImg = roomBg.AddComponent<Image>();
            roomBgImg.color = new Color(0.1f, 0.1f, 0.15f, 0.9f);
            roomBgImg.raycastTarget = false;
            var roomBgRect = roomBg.GetComponent<RectTransform>();
            roomBgRect.anchorMin = new Vector2(0.5f, 0.5f);
            roomBgRect.anchorMax = new Vector2(0.5f, 0.5f);
            roomBgRect.anchoredPosition = new Vector2(0, 170);
            roomBgRect.sizeDelta = new Vector2(400, 40);
            
            _roomNameLabel = CreateLabel("Sala: ---", transform, new Vector2(0, 170), 22);
            _roomNameLabel.color = new Color(0.4f, 0.8f, 1f, 1f);
            
            _copyButton = CreateButton("COPIAR", transform, new Vector2(180, 170), new Vector2(90, 36));
            _copyButton.onClick.AddListener(OnCopyClick);
            var copyColors = _copyButton.colors;
            copyColors.normalColor = new Color(0.2f, 0.5f, 0.8f, 1f);
            _copyButton.colors = copyColors;
            
            CreatePlayerListUI();
            
            var toggleObj = new GameObject("ReadyToggle");
            toggleObj.transform.SetParent(transform, false);
            _readyToggle = toggleObj.AddComponent<Toggle>();
            var toggleRect = toggleObj.GetComponent<RectTransform>();
            toggleRect.anchorMin = new Vector2(0.5f, 0.5f);
            toggleRect.anchorMax = new Vector2(0.5f, 0.5f);
            toggleRect.anchoredPosition = new Vector2(-80, -80);
            toggleRect.sizeDelta = new Vector2(40, 40);
            var toggleBg = toggleObj.AddComponent<Image>();
            toggleBg.color = new Color(0.3f, 0.3f, 0.3f, 1f);
            toggleBg.raycastTarget = true;
            _readyToggle.targetGraphic = toggleBg;
            
            var checkObj = new GameObject("Checkmark");
            checkObj.transform.SetParent(toggleObj.transform, false);
            var checkImage = checkObj.AddComponent<Image>();
            checkImage.color = Color.green;
            checkImage.raycastTarget = false;
            _readyToggle.graphic = checkImage;
            var checkRect = checkObj.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.1f, 0.1f);
            checkRect.anchorMax = new Vector2(0.9f, 0.9f);
            checkRect.sizeDelta = Vector2.zero;
            
            var readyLabel = CreateLabel("Listo", transform, new Vector2(40, -80), 22);
            readyLabel.alignment = TextAlignmentOptions.Left;
            _readyToggle.onValueChanged.AddListener(OnReadyChanged);
            
            _startButton = CreateButton("INICIAR PARTIDA", transform, new Vector2(0, -160), new Vector2(280, 55));
            _startButton.onClick.AddListener(OnStartClick);
            _startButton.interactable = false;
            
            _leaveButton = CreateButton("ABANDONAR SALA", transform, new Vector2(0, -230), new Vector2(250, 50));
            _leaveButton.onClick.AddListener(OnLeaveClick);
            
            _statusLabel = CreateLabel("Esperando jugadores...", transform, new Vector2(0, -290), 20);
            _statusLabel.color = Color.yellow;
            
            StyleButton(_startButton);
            StyleButton(_leaveButton);
        }

        private void CreatePlayerListUI()
        {
            var listContainer = new GameObject("PlayerListContainer");
            listContainer.transform.SetParent(transform, false);
            var listRect = listContainer.AddComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0.5f, 0.5f);
            listRect.anchorMax = new Vector2(0.5f, 0.5f);
            listRect.anchoredPosition = new Vector2(0, 20);
            listRect.sizeDelta = new Vector2(500, 250);
            
            for (int i = 0; i < MPConstants.MAX_PLAYERS; i++)
            {
                var slotObj = new GameObject($"Slot_{i}");
                slotObj.transform.SetParent(listContainer.transform, false);
                
                var slotRect = slotObj.AddComponent<RectTransform>();
                slotRect.anchorMin = new Vector2(0, 1);
                slotRect.anchorMax = new Vector2(1, 1);
                slotRect.pivot = new Vector2(0.5f, 1);
                slotRect.anchoredPosition = new Vector2(0, -i * 22);
                slotRect.sizeDelta = new Vector2(0, 20);
                
                var labelObj = new GameObject("Label");
                labelObj.transform.SetParent(slotObj.transform, false);
                var label = labelObj.AddComponent<TextMeshProUGUI>();
                label.text = $"Slot {i+1}: Vacio";
                label.fontSize = 14;
                label.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                label.alignment = TextAlignmentOptions.Left;
                label.raycastTarget = false;
                
                var labelRect = labelObj.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(5, 0);
                labelRect.offsetMax = new Vector2(-5, 0);
                
                slotObj.SetActive(false);
                _slotUIs.Add(slotObj);
            }
        }

        void OnEnable()
        {
            if (_eventsSubscribed) return;
            PhotonManager.Instance.OnPlayerJoinedEvent += OnPlayerJoined;
            PhotonManager.Instance.OnPlayerLeftEvent += OnPlayerLeft;
            PhotonManager.Instance.OnJoinedRoomEvent += OnJoinedRoomHandler;
            PhotonManager.Instance.OnRemoteReadyEvent += OnRemoteReadyChanged;
            _eventsSubscribed = true;
            
            UpdateAllUI();
        }

        void OnDisable()
        {
            if (!_eventsSubscribed) return;
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnPlayerJoinedEvent -= OnPlayerJoined;
                PhotonManager.Instance.OnPlayerLeftEvent -= OnPlayerLeft;
                PhotonManager.Instance.OnJoinedRoomEvent -= OnJoinedRoomHandler;
                PhotonManager.Instance.OnRemoteReadyEvent -= OnRemoteReadyChanged;
            }
            _eventsSubscribed = false;
        }

        private void OnJoinedRoomHandler()
        {
            var state = MPStateManager.Instance;
            state.IsHost = PhotonManager.Instance.IsMasterClient;
            state.IsClient = !state.IsHost;
            
            UpdateAllUI();
            CivilizameMPPlugin.Log.LogInfo($"[Lobby] Unido a sala. Host: {state.IsHost}");
        }

        public override void Show()
        {
            base.Show();
            _gameStarting = false;
            UpdateAllUI();
        }

        private void OnCopyClick()
        {
            string roomName = PhotonManager.Instance.RoomName;
            if (string.IsNullOrEmpty(roomName)) return;
            GUIUtility.systemCopyBuffer = roomName;
            _statusLabel.text = "¡Copiado al portapapeles!";
            _statusLabel.color = Color.cyan;
        }

        private void OnPlayerJoined(Player player)
        {
            UpdateAllUI();
            CivilizameMPPlugin.Log.LogInfo($"Jugador entró: {player.NickName}");
        }

        private void OnPlayerLeft(Player player)
        {
            _remoteReady = false;
            UpdateAllUI();
            _startButton.interactable = false;
            _statusLabel.text = "Jugador desconectado";
            _statusLabel.color = Color.red;
            CivilizameMPPlugin.Log.LogWarning($"Jugador salió: {player?.NickName}");
        }

        private void OnReadyChanged(bool isReady)
        {
            _localReady = isReady;
            PhotonManager.Instance.SendReady(isReady);
            
            var state = MPStateManager.Instance;
            for (int i = 0; i < state.PlayerSlots.Count; i++) 
            {
                var slot = state.PlayerSlots[i];
                if (slot != null && slot.ActorNumber == state.LocalActorNumber)
                {
                    slot.IsReady = isReady;
                    break;
                }
            }
            
            UpdateAllUI();
        }

        private void OnRemoteReadyChanged(int actorNumber, bool ready)
        {
            var state = MPStateManager.Instance;
            if (state.ConnectedPlayers.TryGetValue(actorNumber, out var slot) && slot.IsConnected)
            {
                slot.IsReady = ready;
                if (actorNumber != state.LocalActorNumber)
                    _remoteReady = ready;
                CivilizameMPPlugin.Log.LogInfo($"[UI] {slot.PlayerName} listo = {ready}");
            }
            UpdateAllUI();
        }

        private void UpdateAllUI()
        {
            UpdateRoomLabel();
            UpdatePlayerList();
            CheckStartReady();
        }

        private void UpdateRoomLabel()
        {
            var state = MPStateManager.Instance;
            
            if (state.IsHost && PhotonManager.Instance.IsInRoom)
            {
                _roomNameLabel.text = $"Sala: {PhotonManager.Instance.RoomName}";
                _roomNameLabel.gameObject.SetActive(true);
                _copyButton.gameObject.SetActive(true);
            }
            else
            {
                _roomNameLabel.text = "Conectado como Cliente";
                _roomNameLabel.gameObject.SetActive(true);
                _copyButton.gameObject.SetActive(false);
            }
        }

        public void UpdatePlayerList()
        {
            var state = MPStateManager.Instance;
            
            for (int i = 0; i < MPConstants.MAX_PLAYERS && i < _slotUIs.Count; i++)
            {
                var slot = state.PlayerSlots[i];
                var slotUI = _slotUIs[i];
                
                if (slot != null && slot.IsConnected && slot.ActorNumber >= 0)
                {
                    slotUI.SetActive(true);
                    var label = slotUI.GetComponentInChildren<TextMeshProUGUI>();
                    if (label != null)
                    {
                        string status = slot.IsReady ? " [✓]" : " [...]";
                        string hostTag = slot.IsHost ? "[HOST] " : "";
                        string selfTag = slot.ActorNumber == state.LocalActorNumber ? " (TU)" : "";
                        label.text = $"{hostTag}J{i+1}: {slot.PlayerName}{selfTag}{status}";
                        label.color = slot.IsReady ? Color.green : Color.yellow;
                    }
                }
                else
                {
                    slotUI.SetActive(false);
                }
            }
        }

        private void CheckStartReady()
        {
            var state = MPStateManager.Instance;
            
            bool allReady = state.ConnectedPlayerCount >= 2 && state.AreAllPlayersReady();
            
            _startButton.interactable = allReady && state.IsHost;
            
            var colors = _startButton.colors;
            colors.normalColor = allReady && state.IsHost ? new Color(0.2f, 0.7f, 0.2f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f);
            _startButton.colors = colors;
            
            if (_statusLabel != null && !_gameStarting)
            {
                if (state.ConnectedPlayerCount < 2)
                {
                    _statusLabel.text = $"Esperando jugadores... ({state.ConnectedPlayerCount}/2)";
                    _statusLabel.color = Color.yellow;
                }
                else if (!state.AreAllPlayersReady())
                {
                    _statusLabel.text = $"{state.ConnectedPlayerCount} jugadores - Esperando que todos marquen 'Listo'";
                    _statusLabel.color = Color.yellow;
                }
                else
                {
                    _statusLabel.text = $"¡{state.ConnectedPlayerCount} jugadores listos!";
                    _statusLabel.color = Color.green;
                }
            }
        }

        private void OnStartClick()
        {
            if (!MPStateManager.Instance.IsHost) return;
            
            var state = MPStateManager.Instance;
            if (state.ConnectedPlayerCount < 2) 
            {
                _statusLabel.text = "Se necesitan al menos 2 jugadores";
                return;
            }
            
            _gameStarting = true;
            CivilizameMPPlugin.Log.LogInfo("[Host] Iniciando partida");
            
            PhotonManager.Instance.SendConfigToAll("{\"hostStarting\":true}");
            
            MPPanelManager.Instance.HideCurrentPanel();
            MPStateManager.Instance.SetState(MPGameState.PlayingHost);
        }

        private Color GetColorForSlot(int index)
        {
            Color[] colors = new Color[]
            {
                Color.red, Color.blue, Color.green, Color.yellow,
                Color.cyan, Color.magenta, new Color(1, 0.5f, 0), Color.white
            };
            return colors[index % colors.Length];
        }

        private void OnLeaveClick()
        {
            CivilizameMPPlugin.Log.LogInfo("Abandonando sala");
            PhotonManager.Instance.LeaveRoom();
            MPStateManager.Instance.Reset();
            if (_readyToggle != null) _readyToggle.isOn = false;
            _localReady = false;
            _remoteReady = false;
            MPPanelManager.Instance.ShowPanel(MPPanelType.MainMenu);
        }

        private void StyleButton(Button button)
        {
            var colors = button.colors;
            colors.normalColor = new Color(0.25f, 0.25f, 0.25f, 1f);
            colors.highlightedColor = new Color(0.35f, 0.35f, 0.35f, 1f);
            colors.pressedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            button.colors = colors;
        }

        void OnDestroy()
        {
            OnDisable();
        }
    }
}