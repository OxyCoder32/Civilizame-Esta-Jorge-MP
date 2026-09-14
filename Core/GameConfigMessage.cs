using System;
using System.Collections.Generic;
using CivilizameMP.Core;
using UnityEngine;

[System.Serializable]
public class PlayerSlotNetwork
{
    public int SlotIndex;
    public int ActorNumber;
    public string PlayerName;
    public bool IsHuman;
    public bool IsHost;
    public bool IsConnected;
    public bool IsReady;
    public int LeaderIndex;
}

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

    public PlayerSlotNetwork[] PlayerSlotsArray = new PlayerSlotNetwork[0];

    public List<PlayerSlotConfig> GetPlayerSlots()
    {
        var list = new List<PlayerSlotConfig>();
        if (PlayerSlotsArray == null) return list;

        foreach (var n in PlayerSlotsArray)
        {
            list.Add(new PlayerSlotConfig
            {
                SlotIndex = n.SlotIndex,
                ActorNumber = n.ActorNumber,
                PlayerName = n.PlayerName,
                IsHuman = n.IsHuman,
                IsHost = n.IsHost,
                IsConnected = n.IsConnected,
                IsReady = n.IsReady,
                LeaderIndex = n.LeaderIndex
            });
        }
        return list;
    }
}