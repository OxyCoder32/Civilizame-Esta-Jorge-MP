using UnityEngine;
using System.Reflection;

namespace CivilizameMP.Core
{
    public static class MPGameSettingsHelper
    {
        private static GameSettings _gameSettings;
        private static FieldInfo _seedField;
        private static FieldInfo _mapSizeField;
        private static FieldInfo _mapTypeField;
        private static FieldInfo _difficultyField;
        private static bool _initialized;
        
        private static void Initialize()
        {
            if (_initialized) return;
            
            _gameSettings = Object.FindObjectOfType<GameSettings>();
            if (_gameSettings == null) return;
            
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _seedField = typeof(GameSettings).GetField("Seed", flags);
            _mapSizeField = typeof(GameSettings).GetField("_tamañoMap", flags);
            _mapTypeField = typeof(GameSettings).GetField("_typeMap", flags);
            _difficultyField = typeof(GameSettings).GetField("dificulatad", flags);
            
            _initialized = true;
        }
        
        public static int GetSeed()
        {
            Initialize();
            if (_gameSettings == null || _seedField == null) return Random.Range(0, 1000000);
            return (int)_seedField.GetValue(_gameSettings);
        }
        
        public static int GetMapSize()
        {
            Initialize();
            if (_gameSettings == null || _mapSizeField == null) return 1;
            return (int)(float)_mapSizeField.GetValue(_gameSettings);
        }
        
        public static int GetMapType()
        {
            Initialize();
            if (_gameSettings == null || _mapTypeField == null) return 0;
            return (int)_mapTypeField.GetValue(_gameSettings);
        }
        
        public static int GetDifficulty()
        {
            Initialize();
            if (_gameSettings == null || _difficultyField == null) return -1;
            return (int)_difficultyField.GetValue(_gameSettings);
        }
    }
}