using UnityEngine;
using UnityEngine.UI;
using CivilizameMP.Core;

namespace CivilizameMP.UI
{
    public class MPMainMenuPanel : MPPanelBase
    {
        private Button _hostButton;
        private Button _joinButton;
        private Button _backButton;

        protected override void BuildUI()
        {
            CreateBackground();
            CreateLabel("MULTIPLAYER", transform, new Vector2(0, 180), 48);
            _hostButton = CreateButton("CREAR PARTIDA", transform, new Vector2(0, 50), new Vector2(300, 60));
            _hostButton.onClick.AddListener(OnHostClick);
            _joinButton = CreateButton("UNIRSE A PARTIDA", transform, new Vector2(0, -40), new Vector2(300, 60));
            _joinButton.onClick.AddListener(OnJoinClick);
            _backButton = CreateButton("VOLVER", transform, new Vector2(0, -150), new Vector2(200, 50));
            _backButton.onClick.AddListener(OnBackClick);
            StyleButton(_hostButton);
            StyleButton(_joinButton);
            StyleButton(_backButton);
        }

        private void StyleButton(Button button)
        {
            var colors = button.colors;
            colors.normalColor = new Color(0.25f, 0.25f, 0.25f, 1f);
            colors.highlightedColor = new Color(0.35f, 0.35f, 0.35f, 1f);
            colors.pressedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            colors.selectedColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            button.colors = colors;
        }

        private void OnHostClick()
        {
            CivilizameMPPlugin.Log.LogInfo("Abriendo panel Host");
            MPStateManager.Instance.SetState(MPGameState.InHostPanel);
            MPPanelManager.Instance.ShowPanel(MPPanelType.Host);
        }

        private void OnJoinClick()
        {
            CivilizameMPPlugin.Log.LogInfo("Abriendo panel Join");
            MPStateManager.Instance.SetState(MPGameState.InJoinPanel);
            MPPanelManager.Instance.ShowPanel(MPPanelType.Join);
        }

        private void OnBackClick()
        {
            CivilizameMPPlugin.Log.LogInfo("Volviendo al menú principal");
            MPStateManager.Instance.SetState(MPGameState.None);
            MPPanelManager.Instance.HideCurrentPanel();
        }
    }
}