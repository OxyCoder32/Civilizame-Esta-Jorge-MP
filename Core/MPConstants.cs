namespace CivilizameMP.Core
{
    public static class MPConstants
    {
        // Slots de guardado
        public const int SYNC_SLOT = 99;
        public const string SYNC_FILENAME = "MP_Sync";
        public const string ROOM_PREFIX = "CZMP_";
        public const string GAME_VERSION = "1.0";
        
        // Escenas
        public const string MENU_SCENE = "Menu";
        public const string GAME_SCENE = "Game";
        
        // UI
        public const string MULTIPLAYER_BUTTON_TEXT = "MULTIPLAYER";
        public const float UI_TRANSITION_TIME = 0.3f;
        
        // Timing
        public const float SYNC_TIMEOUT = 30f;
        public const float RECONNECT_TIMEOUT = 60f;
    }
}