using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace CivilizameMP.UI
{
    /// <summary>Cartel de actualización detectada: cuerpo informativo, casilla
    /// "No volver a preguntar por esta versión" y botones ACTUALIZAR / NO.
    /// Las acciones las conecta ModUpdater (descarga + reinicio del juego).</summary>
    public class MPUpdatePanel : MPPanelBase
    {
        public Action OnAccept;
        public Action<bool> OnDecline; // bool = "no volver a preguntar" marcado

        private TextMeshProUGUI _body;
        private Toggle _dontAsk;
        private Button _yesButton;
        private Button _noButton;

        protected override void BuildUI()
        {
            CreateBackground();
            CreateLabel("ACTUALIZACIÓN DETECTADA", transform, new Vector2(0, 150), 32);
            _body = CreateLabel("", transform, new Vector2(0, 30), 22);
            _body.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            _body.rectTransform.sizeDelta = new Vector2(660, 220);

            _dontAsk = CreateToggle("No volver a preguntar por esta versión",
                transform, new Vector2(-150, -95));

            _yesButton = CreateButton("ACTUALIZAR", transform, new Vector2(-115, -160), new Vector2(220, 55));
            _yesButton.onClick.AddListener(() => OnAccept?.Invoke());
            StyleYes(_yesButton);

            _noButton = CreateButton("NO", transform, new Vector2(115, -160), new Vector2(160, 55));
            _noButton.onClick.AddListener(() => OnDecline?.Invoke(_dontAsk != null && _dontAsk.isOn));
            StyleNo(_noButton);
        }

        /// <summary>Texto del cuerpo (versiones, explicación, estado de descarga).</summary>
        public void SetBody(string text)
        {
            if (_body != null) _body.text = text;
        }

        /// <summary>Bloquea los botones durante la descarga/instalación.</summary>
        public void SetBusy(bool busy)
        {
            if (_yesButton != null) _yesButton.interactable = !busy;
            if (_noButton != null) _noButton.interactable = !busy;
            if (_dontAsk != null) _dontAsk.interactable = !busy;
        }

        private void StyleYes(Button button)
        {
            var colors = button.colors;
            colors.normalColor = new Color(0.15f, 0.35f, 0.15f, 1f);
            colors.highlightedColor = new Color(0.2f, 0.5f, 0.2f, 1f);
            colors.pressedColor = new Color(0.1f, 0.25f, 0.1f, 1f);
            button.colors = colors;
        }

        private void StyleNo(Button button)
        {
            var colors = button.colors;
            colors.normalColor = new Color(0.25f, 0.25f, 0.25f, 1f);
            colors.highlightedColor = new Color(0.35f, 0.35f, 0.35f, 1f);
            colors.pressedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            button.colors = colors;
        }
    }
}
