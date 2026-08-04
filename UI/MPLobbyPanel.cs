using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Photon.Realtime;
using CivilizameMP.Core;
using CivilizameMP.Network;

namespace CivilizameMP.UI
{
    public class MPLobbyPanel : MPPanelBase
    {
        private TextMeshProUGUI _hostSlot;
        private TextMeshProUGUI _clientSlot;
        private TextMeshProUGUI _roomNameLabel;
        private Button _copyButton;
        private Toggle _readyToggle;
        private Button _startButton;
        private Button _leaveButton;
        private TextMeshProUGUI _statusLabel;
        private bool _localReady;
        private bool _remoteReady;
        private bool _eventsSubscribed;

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
            
            _hostSlot = CreateLabel("Host: Esperando...", transform, new Vector2(0, 100), 24);
            _hostSlot.color = new Color(0.8f, 0.8f, 0.8f, 1f);
            _clientSlot = CreateLabel("Jugador 2: Esperando...", transform, new Vector2(0, 40), 24);
            _clientSlot.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            CreateSeparator(new Vector2(0, -20));
            
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

        void OnEnable()
        {
            if (_eventsSubscribed) return;
            PhotonManager.Instance.OnPlayerJoinedEvent += OnPlayerJoined;
            PhotonManager.Instance.OnPlayerLeftEvent += OnPlayerLeft;
            PhotonManager.Instance.OnJoinedRoomEvent += OnJoinedRoomHandler;
            PhotonManager.Instance.OnRemoteReadyEvent += OnRemoteReadyChanged;
            _eventsSubscribed = true;
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
            UpdatePlayerSlots();
        }

        private void CreateSeparator(Vector2 position)
        {
            var sep = new GameObject("Separator");
            sep.transform.SetParent(transform, false);
            var image = sep.AddComponent<Image>();
            image.color = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            image.raycastTarget = false;
            var rect = sep.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(400, 2);
        }

        public void UpdatePlayerSlots()
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
            
            string hostText = $"Host: {state.LocalPlayerName}";
            if (state.IsHost) hostText += " (Tú)";
            _hostSlot.text = hostText;
            _hostSlot.color = Color.green;
            
            if (!string.IsNullOrEmpty(state.RemotePlayerName))
            {
                string clientStatus = _remoteReady ? "✓ Listo" : "Esperando...";
                _clientSlot.text = $"Jugador 2: {state.RemotePlayerName} {clientStatus}";
                _clientSlot.color = _remoteReady ? Color.green : Color.yellow;
                _statusLabel.text = _localReady ? "Esperando al otro jugador..." : "Marca 'Listo' para comenzar";
                _statusLabel.color = Color.yellow;
            }
            else
            {
                _clientSlot.text = "Jugador 2: Esperando...";
                _clientSlot.color = new Color(0.6f, 0.6f, 0.6f, 1f);
                _statusLabel.text = "Esperando jugador...";
                _statusLabel.color = Color.yellow;
            }
        }

        public override void Show()
        {
            base.Show();
            UpdatePlayerSlots();
        }

        private void OnCopyClick()
        {
            string roomName = PhotonManager.Instance.RoomName;
            if (string.IsNullOrEmpty(roomName)) return;
            GUIUtility.systemCopyBuffer = roomName;
            _statusLabel.text = "¡Copiado al portapapeles!";
            _statusLabel.color = Color.cyan;
            CivilizameMPPlugin.Log.LogInfo($"Sala copiada: {roomName}");
        }

        private void OnPlayerJoined(Player player)
        {
            MPStateManager.Instance.RemotePlayerName = player.NickName;
            UpdatePlayerSlots();
            CivilizameMPPlugin.Log.LogInfo($"Jugador entró: {player.NickName}");
        }

        private void OnPlayerLeft(Player player)
        {
            MPStateManager.Instance.RemotePlayerName = null;
            _remoteReady = false;
            UpdatePlayerSlots();
            _startButton.interactable = false;
            _statusLabel.text = "Jugador desconectado";
            _statusLabel.color = Color.red;
            CivilizameMPPlugin.Log.LogWarning($"Jugador salió: {player?.NickName}");
        }

        private void OnReadyChanged(bool isReady)
        {
            _localReady = isReady;
            PhotonManager.Instance.SendReady(isReady);
            UpdateReadyUI();
            
            if (MPStateManager.Instance.IsHost)
            {
                CheckStartReady();
            }
        }

        private void UpdateReadyUI()
        {
            var toggleLabel = _readyToggle.transform.parent.Find("ReadyLabel")?.GetComponent<TextMeshProUGUI>();
            if (toggleLabel != null)
            {
                toggleLabel.text = _localReady ? "✓ Listo" : "Listo";
                toggleLabel.color = _localReady ? Color.green : Color.white;
            }
        }

        private void OnRemoteReadyChanged(bool ready)
        {
            _remoteReady = ready;
            
            if (_clientSlot != null)
            {
                string status = ready ? "✓ Listo" : "Esperando...";
                _clientSlot.text = $"Jugador 2: {MPStateManager.Instance.RemotePlayerName} {status}";
                _clientSlot.color = ready ? Color.green : Color.yellow;
            }
            
            if (MPStateManager.Instance.IsHost)
            {
                CheckStartReady();
            }
        }

        private void CheckStartReady()
        {
            bool canStart = _localReady && _remoteReady && !string.IsNullOrEmpty(MPStateManager.Instance.RemotePlayerName);
            _startButton.interactable = canStart;
            
            var image = _startButton.GetComponent<Image>();
            if (image != null)
            {
                image.color = canStart 
                    ? new Color(0.2f, 0.7f, 0.2f, 1f) 
                    : new Color(0.3f, 0.3f, 0.3f, 1f);
            }
            
            if (_statusLabel != null)
            {
                if (!string.IsNullOrEmpty(MPStateManager.Instance.RemotePlayerName))
                {
                    if (_localReady && _remoteReady)
                        _statusLabel.text = "¡Ambos jugadores listos!";
                    else if (_localReady && !_remoteReady)
                        _statusLabel.text = "Esperando que el otro jugador esté listo...";
                    else
                        _statusLabel.text = "Marca 'Listo' para comenzar";
                }
            }
        }

        private void OnStartClick()
        {
            if (!PhotonManager.Instance.IsMasterClient || !_localReady || !_remoteReady) return;
            
            var config = new GameConfigMessage
            {
                Seed = Random.Range(int.MinValue, int.MaxValue),
                MapSize = 2,
                MapType = 0,
                Difficulty = 1,
                NumPlayers = 2,
                HostName = MPStateManager.Instance.LocalPlayerName,
                HostLeader = MPStateManager.Instance.LocalPlayerLeaderIndex
            };
            
            CivilizameMPPlugin.Log.LogInfo("Host iniciando partida...");
            PhotonManager.Instance.SendConfig(JsonUtility.ToJson(config));
            HostManager.Instance.StartGame(config);
            MPPanelManager.Instance.HideCurrentPanel();
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