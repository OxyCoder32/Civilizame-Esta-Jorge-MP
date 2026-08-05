using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CivilizameMP.Core;
using CivilizameMP.Network;
using System.Collections; 

namespace CivilizameMP.UI
{
    public class MPHostPanel : MPPanelBase
    {
        private TMP_InputField _nameInput;
        private Button _createButton;
        private Button _backButton;
        private TextMeshProUGUI _statusLabel;
        private bool _connecting;
        private bool _subscribed;

        protected override void BuildUI()
        {
            CreateBackground();
            CreateLabel("CREAR PARTIDA", transform, new Vector2(0, 200), 42);
            CreateLabel("Tu nombre:", transform, new Vector2(0, 100), 22);
            _nameInput = CreateInputField("Introduce tu nombre...", transform, new Vector2(0, 60), new Vector2(350, 45));
            _nameInput.text = MPStateManager.Instance.LocalPlayerName;
            _statusLabel = CreateLabel("Estado: Desconectado", transform, new Vector2(0, -20), 20);
            _statusLabel.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            _createButton = CreateButton("CREAR SALA", transform, new Vector2(0, -100), new Vector2(280, 55));
            _createButton.onClick.AddListener(OnCreateClick);
            _backButton = CreateButton("VOLVER", transform, new Vector2(0, -180), new Vector2(200, 50));
            _backButton.onClick.AddListener(OnBackClick);
            StyleButton(_createButton);
            StyleButton(_backButton);
        }

        private void SubscribeEvents()
        {
            if (_subscribed) return;
            PhotonManager.Instance.OnConnectedToMasterEvent += OnConnected;
            PhotonManager.Instance.OnDisconnectedEvent += OnConnectFailed;
            _subscribed = true;
        }

        private void UnsubscribeEvents()
        {
            if (!_subscribed) return;
            if (PhotonManager.Instance != null)
            {
                PhotonManager.Instance.OnConnectedToMasterEvent -= OnConnected;
                PhotonManager.Instance.OnDisconnectedEvent -= OnConnectFailed;
            }
            _subscribed = false;
        }

        private void OnCreateClick()
        {
            if (_connecting) return;
            
            string name = string.IsNullOrWhiteSpace(_nameInput.text) 
                ? MPConfig.DefaultPlayerName.Value 
                : _nameInput.text.Trim();
            MPStateManager.Instance.LocalPlayerName = name;
            
            if (string.IsNullOrEmpty(MPConfig.PhotonAppID.Value) || MPConfig.PhotonAppID.Value == "YOUR_PHOTON_APPID_HERE")
            {
                _statusLabel.text = "Error: AppID no configurado";
                _statusLabel.color = Color.red;
                return;
            }
            
            _connecting = true;
            _createButton.interactable = false;
            _statusLabel.text = "Conectando a Photon...";
            _statusLabel.color = Color.yellow;
            
            if (!PhotonManager.Instance.IsConnected)
            {
                SubscribeEvents();
                PhotonManager.Instance.Connect();
            }
            else
            {
                OnConnected();
            }
        }

        private void OnConnected()
        {
            UnsubscribeEvents();
            
            // Generar nombre de sala único
            string roomCode = Random.Range(1000, 9999).ToString();
            string roomName = MPConstants.ROOM_PREFIX + roomCode;
            
            CivilizameMPPlugin.Log.LogInfo($"[Host] Creando sala: {roomName}");
            PhotonManager.Instance.CreateRoom(roomName);
            
            // Esperar a que se cree la sala
            StartCoroutine(WaitForRoomCreation(roomName));
        }

        private IEnumerator WaitForRoomCreation(string roomName)
        {
            int timeout = 0;
            while (!PhotonManager.Instance.IsInRoom && timeout < 100)
            {
                yield return new WaitForSeconds(0.1f);
                timeout++;
            }
            
            if (PhotonManager.Instance.IsInRoom)
            {
                MPStateManager.Instance.SetState(MPGameState.InLobby);
                MPPanelManager.Instance.ShowPanel(MPPanelType.Lobby);
                _connecting = false;
                CivilizameMPPlugin.Log.LogInfo($"[Host] Sala creada: {roomName}");
            }
            else
            {
                _statusLabel.text = "Error al crear sala";
                _statusLabel.color = Color.red;
                _createButton.interactable = true;
                _connecting = false;
            }
        }

        private void OnConnectFailed()
        {
            UnsubscribeEvents();
            _statusLabel.text = "Error de conexión";
            _statusLabel.color = Color.red;
            _createButton.interactable = true;
            _connecting = false;
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
            UnsubscribeEvents();
        }
    }
}