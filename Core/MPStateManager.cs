using UnityEngine;
using System.Collections.Generic;
using Photon.Realtime;

namespace CivilizameMP.Core
{
    public enum MPGameState
    {
        None,
        InMenu,
        InHostPanel,
        InJoinPanel,
        InLobby,
        LoadingGame,
        PlayingHost,
        PlayingClient,
        WaitingTurn,
        Disconnected
    }

    public class MPStateManager : MonoBehaviour
    {
        public static MPStateManager Instance { get; private set; }
        
        public MPGameState CurrentState { get; private set; } = MPGameState.None;
        public bool IsMultiplayerActive => CurrentState != MPGameState.None && CurrentState != MPGameState.Disconnected;
        public bool IsHost { get; set; }
        public bool IsClient { get; set; }
        public string LastError { get; set; }
        public GameConfigMessage PendingConfig { get; set; }
        
        public string LocalPlayerName { get; set; }
        public int LocalPlayerLeaderIndex { get; set; }
        public Color LocalPlayerColor { get; set; }
        
        public string RemotePlayerName { get; set; }
        public int RemotePlayerLeaderIndex { get; set; }

        public List<string> PlayerNames { get; set; } = new List<string>();
        public Dictionary<int, string> PlayerList { get; set; } = new Dictionary<int, string>();

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            LocalPlayerName = MPConfig.DefaultPlayerName?.Value ?? "Jugador";
            DontDestroyOnLoad(gameObject);
        }

        public void SetState(MPGameState newState)
        {
            if (CurrentState == newState) return;
            CivilizameMPPlugin.Log.LogInfo($"Estado: {CurrentState} -> {newState}");
            CurrentState = newState;
        }

        public void Reset()
        {
            CurrentState = MPGameState.None;
            IsHost = false;
            IsClient = false;
            RemotePlayerName = null;
            PendingConfig = null;
            LastError = null;
            PlayerNames.Clear();
            PlayerList.Clear();
        }

        public void UpdatePlayerList(Player[] players)
        {
            PlayerList.Clear();
            PlayerNames.Clear();
            
            foreach (var player in players)
            {
                PlayerList[player.ActorNumber] = player.NickName;
                PlayerNames.Add(player.NickName);
            }
        }
    }

    [System.Serializable]
    public class GameConfigMessage
    {
        public int Seed;
        public int MapSize;
        public int MapType;
        public int Difficulty;
        public int NumPlayers;
        public string HostName;
        public int HostLeader;
    }
}