using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Photon.Realtime;
using CivilizameMP.Core;
using CivilizameMP.Network;

namespace CivilizameMP.UI
{
    public class MPJoinPanel : MPPanelBase
    {
        private TMP_InputField _nameInput;
        private TMP_InputField _roomInput;
        private Button _joinButton;
        private Button _backButton;
        private TextMeshProUGUI _statusLabel;
        private bool _subscribedConnect;
        private bool _subscribedJoin;

        protected override void BuildUI()
        {
            CreateBackground();
            CreateLabel("UNIRSE A PARTIDA", transform, new Vector2(0, 220), 42);
            CreateLabel("Tu nombre:", transform, new Vector2(0, 140), 22);
            _nameInput = CreateInputField("Introduce tu nombre...", transform, new Vector2(0, 100), new Vector2(350, 45));
            _nameInput.text = MPStateManager.Instance.LocalPlayerName;
            CreateLabel("Código de sala:", transform, new Vector2(0, 20), 22);
            _roomInput = CreateInputField("Ej: 1234", transform, new Vector2(0, -20), new Vector2(350, 45));
            _joinButton = CreateButton("UNIRSE", transform, new Vector2(0, -120), new Vector2(280, 55));
            _joinButton.onClick.AddListener(OnJoinClick);
            _backButton = CreateButton("VOLVER", transform, new Vector2(0, -200), new Vector2(200, 50));
            _backButton.onClick.AddListener(OnBackClick);
            _statusLabel = CreateLabel("Introduce el código de la sala", transform, new Vector2(0, -280), 20);
            _statusLabel.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            StyleButton(_joinButton);
            StyleButton(_backButton);
        }

        private bool IsValidRoomCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            code = code.Trim().ToUpper();
            if (code.StartsWith(MPConstants.ROOM_PREFIX))
                code = code.Substring(MPConstants.ROOM_PREFIX.Length);
            if (code.Length < 4) return false;
            foreach (char c in code)
                if (!char.IsDigit(c)) return false;
            return true;
        }

        private string BuildRoomName(string code)
        {
            code = code.Trim().ToUpper();
            if (code.StartsWith(MPConstants.ROOM_PREFIX))
                return code;
            return MPConstants.ROOM_PREFIX + code;
        }

        private void OnJoinClick()
        {
            string name = string.IsNullOrWhiteSpace(_nameInput.text) 
                ? MPConfig.DefaultPlayerName.Value 
                : _nameInput.text.Trim();
            MPStateManager.Instance.LocalPlayerName = name;
            
            string code = _roomInput.text?.Trim() ?? "";

            if (string.IsNullOrEmpty(MPConfig.PhotonAppID.Value) || MPConfig.PhotonAppID.Value == "YOUR_PHOTON_APPID_HERE")
            {
                _statusLabel.text = "Error: AppID no configurado";
                _statusLabel.color = Color.red;
                return;
            }

            if (string.IsNullOrEmpty(code))
            {
                _statusLabel.text = "Introduce un código de sala";
                _statusLabel.color = Color.red;
                return;
            }

            if (!IsValidRoomCode(code))
            {
                _statusLabel.text = "Código inválido (4+ dígitos)";
                _statusLabel.color = Color.red;
                return;
            }

            string roomName = BuildRoomName(code);
            _joinButton.interactable = false;
            _statusLabel.text = "Conectando...";
            _statusLabel.color = Color.yellow;

            if (!PhotonManager.Instance.IsConnected)
            {
                SubscribeConnectEvents();
                PhotonManager.Instance.Connect(MPConfig.PhotonAppID.Value);
            }
            else
            {
                TryJoinRoom(roomName);
            }
        }

        private void SubscribeConnectEvents()
        {
            if (_subscribedConnect) return;
            PhotonManager.Instance.OnConnectedToMasterEvent += OnConnectSuccess;
            PhotonManager.Instance.OnDisconnectedEvent += OnConnectFailed;
            _subscribedConnect = true;
        }

        private void UnsubscribeConnectEvents()
        {
            if (!_subscribedConnect) return;
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnConnectedToMasterEvent -= OnConnectSuccess;
                PhotonManager.Instance.OnDisconnectedEvent -= OnConnectFailed;
            }
            _subscribedConnect = false;
        }

        private void OnConnectSuccess()
        {
            UnsubscribeConnectEvents();
            string roomName = BuildRoomName(_roomInput.text?.Trim() ?? "");
            TryJoinRoom(roomName);
        }

        private void TryJoinRoom(string roomName)
        {
            if (_subscribedJoin) return;
            PhotonManager.Instance.OnJoinFailedEvent += OnJoinFailed;
            PhotonManager.Instance.OnJoinedRoomEvent += OnJoinSuccess;
            _subscribedJoin = true;
            PhotonManager.Instance.JoinRoom(roomName);
        }

        private void UnsubscribeJoinEvents()
        {
            if (!_subscribedJoin) return;
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnJoinFailedEvent -= OnJoinFailed;
                PhotonManager.Instance.OnJoinedRoomEvent -= OnJoinSuccess;
            }
            _subscribedJoin = false;
        }

        private void OnJoinFailed(short code, string message)
        {
            UnsubscribeJoinEvents();
            _statusLabel.text = "Sala no encontrada";
            _statusLabel.color = Color.red;
            _joinButton.interactable = true;
            CivilizameMPPlugin.Log.LogError($"[Join] Sala no existe: {message}");
        }

        private void OnJoinSuccess()
        {
            UnsubscribeJoinEvents();
            MPStateManager.Instance.SetState(MPGameState.InLobby);
            MPPanelManager.Instance.ShowPanel(MPPanelType.Lobby);
            _joinButton.interactable = true;
            CivilizameMPPlugin.Log.LogInfo("Cliente unido a la sala exitosamente");
        }

        private void OnConnectFailed()
        {
            UnsubscribeConnectEvents();
            _statusLabel.text = "Error de conexión";
            _statusLabel.color = Color.red;
            _joinButton.interactable = true;
        }

        private void OnBackClick()
        {
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
            UnsubscribeConnectEvents();
            UnsubscribeJoinEvents();
        }
    }
}