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
    public List<PlayerSlotConfig> PlayerSlots = new List<PlayerSlotConfig>();

    public List<PlayerSlotConfig> GetPlayerSlots()
    {
        return PlayerSlots;
    }

    public void SetPlayerSlots(List<PlayerSlotConfig> slots)
    {
        PlayerSlots = slots;
    }
}