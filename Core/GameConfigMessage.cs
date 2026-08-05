using System;
using System.Collections.Generic;
using CivilizameMP.Core;
using UnityEngine;

[System.Serializable]
public class GameConfigMessage
{
    public int Seed;
    public int MapSize;
    public int MapType;
    public int Difficulty;
    public int TotalPlayers;
    public int HumanPlayers;
    public string HostName;
    public int HostLeader;
    
    [SerializeField]
    private string _playerSlotsJson;
    
    [NonSerialized]
    private List<PlayerSlotConfig> _playerSlots;

    public List<PlayerSlotConfig> GetPlayerSlots()
    {
        if (_playerSlots == null && !string.IsNullOrEmpty(_playerSlotsJson))
        {
            try
            {
                var wrapper = JsonUtility.FromJson<PlayerSlotListWrapper>(_playerSlotsJson);
                if (wrapper != null)
                {
                    _playerSlots = wrapper.slots;
                }
            }
            catch (Exception)
            {
                // Silenciar error - no usar CivilizameMPPlugin aquí
                _playerSlots = new List<PlayerSlotConfig>();
            }
            
            if (_playerSlots == null)
                _playerSlots = new List<PlayerSlotConfig>();
        }
        return _playerSlots ?? new List<PlayerSlotConfig>();
    }

    public void SetPlayerSlots(List<PlayerSlotConfig> slots)
    {
        _playerSlots = slots;
        try
        {
            var wrapper = new PlayerSlotListWrapper { slots = slots };
            _playerSlotsJson = JsonUtility.ToJson(wrapper);
        }
        catch (Exception)
        {
            // Silenciar error - no usar CivilizameMPPlugin aquí
            _playerSlotsJson = "{}";
        }
    }
}

[System.Serializable]
public class PlayerSlotListWrapper
{
    public List<PlayerSlotConfig> slots = new List<PlayerSlotConfig>();
}