using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace CivilizameMP.UI
{
    public class MPErrorPanel : MPPanelBase
    {
        private TextMeshProUGUI _messageText;
        private Button _okButton;

        protected override void BuildUI()
        {
            CreateBackground();
            CreateLabel("ERROR", transform, new Vector2(0, 80), 36);
            _messageText = CreateLabel("Ha ocurrido un error.", transform, new Vector2(0, 0), 22);
            _messageText.color = new Color(0.9f, 0.3f, 0.3f, 1f);
            _okButton = CreateButton("ACEPTAR", transform, new Vector2(0, -80), new Vector2(200, 50));
            _okButton.onClick.AddListener(OnOkClick);
            StyleButton(_okButton);
        }

        public void SetMessage(string message)
        {
            if (_messageText != null)
                _messageText.text = message;
        }

        private void OnOkClick()
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
    }
}