using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Photon.Realtime;
using CivilizameMP.Core;
using CivilizameMP.Network;
using System.Collections.Generic;
using System.Reflection;

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
        private List<GameObject> _slotUIs = new List<GameObject>();

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

            _hostSlot.gameObject.SetActive(false);
            _clientSlot.gameObject.SetActive(false);
        }

        private void CreatePlayerListUI()
        {
            var listContainer = new GameObject("PlayerListContainer");
            listContainer.transform.SetParent(transform, false);
            var listRect = listContainer.AddComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0.5f, 0.5f);
            listRect.anchorMax = new Vector2(0.5f, 0.5f);
            listRect.anchoredPosition = new Vector2(0, -30);
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
            CivilizameMPPlugin.Log.LogInfo($"[Lobby] Unido a sala. Host: {state.IsHost}, Jugadores: {state.ConnectedPlayerCount}");
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

        public override void Show()
        {
            base.Show();
            UpdateAllUI();
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
            UpdateAllUI();
            CivilizameMPPlugin.Log.LogInfo($"Jugador entro: {player.NickName}");
        }

        private void OnPlayerLeft(Player player)
        {
            _remoteReady = false;
            UpdateAllUI();
            _startButton.interactable = false;
            _statusLabel.text = "Jugador desconectado";
            _statusLabel.color = Color.red;
            CivilizameMPPlugin.Log.LogWarning($"Jugador salio: {player?.NickName}");
        }

        private void OnReadyChanged(bool isReady)
        {
            _localReady = isReady;
            PhotonManager.Instance.SendReady(isReady);
            UpdateReadyUI();
            
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

        private void UpdateReadyUI()
        {
            var toggleLabel = _readyToggle.transform.parent.Find("ReadyLabel")?.GetComponent<TextMeshProUGUI>();
            if (toggleLabel != null)
            {
                toggleLabel.text = _localReady ? "X Listo" : "Listo";
                toggleLabel.color = _localReady ? Color.green : Color.white;
            }
        }

        private void OnRemoteReadyChanged(int actorNumber, bool ready)
        {
            var state = MPStateManager.Instance;
            
            if (state.ConnectedPlayers.TryGetValue(actorNumber, out var slot) && slot.IsConnected)
            {
                slot.IsReady = ready;
                _remoteReady = ready;
                CivilizameMPPlugin.Log.LogInfo($"[UI] Slot {slot.SlotIndex+1} ({slot.PlayerName}) actualizado a listo = {ready}");
            }
            
            UpdateAllUI();
        }

        private void CheckStartReady()
        {
            var state = MPStateManager.Instance;
            
            bool hasRemote = false;
            foreach (var player in state.ConnectedPlayers.Values)
            {
                if (player.ActorNumber != state.LocalActorNumber && player.IsConnected)
                {
                    hasRemote = true;
                    break;
                }
            }
            
            bool canStart = _localReady && _remoteReady && hasRemote;
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
                if (hasRemote)
                {
                    if (_localReady && _remoteReady)
                        _statusLabel.text = "¡Ambos jugadores listos!";
                    else if (_localReady && !_remoteReady)
                        _statusLabel.text = "Esperando que el otro jugador este listo...";
                    else
                        _statusLabel.text = "Marca 'Listo' para comenzar";
                }
            }
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

        private void UpdatePlayerSlots()
        {
            var state = MPStateManager.Instance;
            
            string remoteName = null;
            foreach (var player in state.ConnectedPlayers.Values)
            {
                if (player.ActorNumber != state.LocalActorNumber && player.IsConnected)
                {
                    remoteName = player.PlayerName;
                    break;
                }
            }

            if (state.IsHost)
            {
                string hostStatus = _localReady ? " [X Listo]" : " [Esperando...]";
                _hostSlot.text = $"Host: {state.LocalPlayerName} (TU){hostStatus}";
                _hostSlot.color = _localReady ? Color.green : Color.yellow;

                if (!string.IsNullOrEmpty(remoteName))
                {
                    string clientStatus = _remoteReady ? " [X Listo]" : " [Esperando...]";
                    _clientSlot.text = $"Jugador 2: {remoteName}{clientStatus}";
                    _clientSlot.color = _remoteReady ? Color.green : Color.yellow;
                }
                else
                {
                    _clientSlot.text = "Jugador 2: Esperando...";
                    _clientSlot.color = new Color(0.6f, 0.6f, 0.6f, 1f);
                }
            }
            else
            {
                string hostStatus = _remoteReady ? " [X Listo]" : " [Esperando...]";
                _hostSlot.text = $"Host: {remoteName ?? "Host"}{hostStatus}";
                _hostSlot.color = _remoteReady ? Color.green : Color.yellow;

                string clientStatus = _localReady ? " [X Listo]" : " [Esperando...]";
                _clientSlot.text = $"Jugador 2: {state.LocalPlayerName} (TU){clientStatus}";
                _clientSlot.color = _localReady ? Color.green : Color.white; 
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
                        string status = slot.IsReady ? " [X]" : " [...]";
                        string hostTag = slot.IsHost ? "[HOST] " : "";
                        string playerType = slot.IsHuman ? "[H]" : "[AI]";
                        string selfTag = slot.ActorNumber == state.LocalActorNumber ? " (TU)" : "";
                        label.text = $"{hostTag}J{i+1}: {slot.PlayerName}{selfTag} {playerType}{status}";
                        label.color = slot.IsReady ? Color.green : Color.yellow;
                    }
                }
                else
                {
                    slotUI.SetActive(false);
                }
            }
            
            int connected = state.ConnectedPlayerCount;
            bool allReady = connected >= MPConstants.MIN_PLAYERS && state.AreAllPlayersReady();
            
            if (_startButton != null)
            {
                _startButton.interactable = allReady && state.IsHost;
                var colors = _startButton.colors;
                colors.normalColor = allReady && state.IsHost ? new Color(0.2f, 0.7f, 0.2f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f);
                _startButton.colors = colors;
            }
            
            if (_statusLabel != null)
            {
                if (connected < MPConstants.MIN_PLAYERS)
                {
                    _statusLabel.text = $"Esperando jugadores... ({connected}/{MPConstants.MIN_PLAYERS})";
                    _statusLabel.color = Color.yellow;
                }
                else if (!state.AreAllPlayersReady())
                {
                    _statusLabel.text = $"{connected} jugadores conectados - Esperando que todos marquen 'Listo'";
                    _statusLabel.color = Color.yellow;
                }
                else
                {
                    _statusLabel.text = $"¡{connected} jugadores listos!";
                    _statusLabel.color = Color.green;
                }
            }
        }

        private void OnStartClick()
        {
            if (!MPStateManager.Instance.IsHost) return;
            
            var state = MPStateManager.Instance;
            if (state.ConnectedPlayerCount < MPConstants.MIN_PLAYERS) 
            {
                _statusLabel.text = "Se necesitan al menos 2 jugadores";
                return;
            }
            if (!state.AreAllPlayersReady())
            {
                _statusLabel.text = "Todos los jugadores deben estar listos";
                return;
            }
            
            // Construir configuración
            var slots = new List<PlayerSlotConfig>();
            foreach (var player in state.ConnectedPlayers.Values)
            {
                slots.Add(player);
            }
            
            var config = new GameConfigMessage
            {
                Seed = MPGameSettingsHelper.GetSeed(),
                MapSize = MPGameSettingsHelper.GetMapSize(),
                MapType = MPGameSettingsHelper.GetMapType(),
                Difficulty = MPGameSettingsHelper.GetDifficulty(),
                TotalPlayers = state.ConnectedPlayerCount,
                HumanPlayers = state.ConnectedPlayerCount,
                HostName = state.LocalPlayerName
            };
            config.SetPlayerSlots(slots);
            
            CivilizameMPPlugin.Log.LogInfo($"[Host] Iniciando partida con {config.TotalPlayers} jugadores");
            
            // Iniciar partida
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