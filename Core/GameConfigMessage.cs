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
    public string PlayerSlotsJson;
    public string AIConfigsJson;

    private List<PlayerSlotConfig> _playerSlots;

    public List<PlayerSlotConfig> GetPlayerSlots()
    {
        if (_playerSlots == null)
        {
            _playerSlots = new List<PlayerSlotConfig>();
            if (!string.IsNullOrEmpty(PlayerSlotsJson))
            {
                try
                {
                    var wrapper = JsonUtility.FromJson<PlayerSlotWrapper>(PlayerSlotsJson);
                    if (wrapper != null)
                        _playerSlots = wrapper.slots ?? new List<PlayerSlotConfig>();
                }
                catch { _playerSlots = new List<PlayerSlotConfig>(); }
            }
        }
        return _playerSlots;
    }

    public void SetPlayerSlots(List<PlayerSlotConfig> slots)
    {
        _playerSlots = slots;
        var wrapper = new PlayerSlotWrapper { slots = slots };
        PlayerSlotsJson = JsonUtility.ToJson(wrapper);
    }
}

[System.Serializable]
public class PlayerSlotWrapper
{
    public List<PlayerSlotConfig> slots = new List<PlayerSlotConfig>();
}