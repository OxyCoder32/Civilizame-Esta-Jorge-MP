using System;
using System.Collections.Generic;
using UnityEngine;

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
        Disconnected
    }
    
    [Serializable]
    public class PlayerSlotConfig
    {
        public int SlotIndex;
        public int ActorNumber;
        public string PlayerName;
        public bool IsHuman;
        public bool IsHost;
        public bool IsReady;
        public bool IsConnected;
        public int LeaderIndex;
        public Color PrimaryColor;
        public Color SecondaryColor;
    }
    
    public class MPStateManager : MonoBehaviour
    {
        public static MPStateManager Instance { get; private set; }
        
        [Header("Estado Local")]
        public int LocalActorNumber = -1;
        public string LocalPlayerName;
        public bool IsHost;
        public bool IsClient;
        public int LocalSlotIndex = -1;
        public MPGameState CurrentState = MPGameState.None;
        
        [Header("Jugadores en Sala")]
        public Dictionary<int, PlayerSlotConfig> ConnectedPlayers = new Dictionary<int, PlayerSlotConfig>();
        public List<PlayerSlotConfig> PlayerSlots = new List<PlayerSlotConfig>();
        
        [Header("Configuración de Partida")]
        public GameConfigMessage PendingGameConfig;
        public string LastError;
        
        public bool IsMultiplayerActive => CurrentState == MPGameState.PlayingHost || 
                                           CurrentState == MPGameState.PlayingClient ||
                                           CurrentState == MPGameState.InLobby;
        
        public int ConnectedPlayerCount => ConnectedPlayers.Count;
        public int MaxPlayers => 20;
        
        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            
            for (int i = 0; i < MaxPlayers; i++)
            {
                PlayerSlots.Add(new PlayerSlotConfig 
                { 
                    SlotIndex = i, 
                    PlayerName = $"Slot {i+1}",
                    IsConnected = false,
                    IsHuman = false,
                    IsReady = false,
                    ActorNumber = -1
                });
            }
        }
        
        public void SetState(MPGameState newState)
        {
            CurrentState = newState;
            CivilizameMPPlugin.Log.LogInfo($"[MPState] Estado cambiado a: {newState}");
        }
        
        public void RegisterPlayer(int actorNumber, string playerName, bool isHost)
        {
            if (ConnectedPlayers.ContainsKey(actorNumber))
            {
                ConnectedPlayers[actorNumber].PlayerName = playerName;
                ConnectedPlayers[actorNumber].IsConnected = true;
                return;
            }
            
            int slotIndex = -1;
            for (int i = 0; i < MaxPlayers; i++)
            {
                if (PlayerSlots[i].ActorNumber == -1)
                {
                    slotIndex = i;
                    break;
                }
            }
            
            if (slotIndex == -1)
            {
                CivilizameMPPlugin.Log.LogError("[MPState] No hay slots disponibles!");
                return;
            }
            
            var config = new PlayerSlotConfig
            {
                SlotIndex = slotIndex,
                ActorNumber = actorNumber,
                PlayerName = playerName,
                IsHuman = true,
                IsHost = isHost,
                IsConnected = true,
                IsReady = false
            };
            
            ConnectedPlayers[actorNumber] = config;
            PlayerSlots[slotIndex] = config;
            
            if (actorNumber == LocalActorNumber)
            {
                IsHost = isHost;
                IsClient = !isHost;
                LocalSlotIndex = slotIndex;
                LocalPlayerName = playerName;
            }
            
            CivilizameMPPlugin.Log.LogInfo($"[MPState] Jugador registrado: {playerName} (Actor: {actorNumber}, Slot {slotIndex}, Host: {isHost})");
        }
        
        public void UnregisterPlayer(int actorNumber)
        {
            if (!ConnectedPlayers.ContainsKey(actorNumber)) return;
            
            var config = ConnectedPlayers[actorNumber];
            int slotIndex = config.SlotIndex;
            
            ConnectedPlayers.Remove(actorNumber);
            
            if (slotIndex >= 0 && slotIndex < PlayerSlots.Count)
            {
                PlayerSlots[slotIndex] = new PlayerSlotConfig 
                { 
                    SlotIndex = slotIndex, 
                    PlayerName = $"Slot {slotIndex+1}",
                    IsConnected = false,
                    IsHuman = false,
                    IsReady = false,
                    ActorNumber = -1
                };
            }
            
            if (actorNumber == LocalActorNumber)
            {
                IsHost = false;
                IsClient = false;
                LocalSlotIndex = -1;
                LocalActorNumber = -1;
            }
            
            CivilizameMPPlugin.Log.LogInfo($"[MPState] Jugador desregistrado: {config.PlayerName}");
        }
        
        // ELIMINAR ESTE MÉTODO DUPLICADO (el que está en la línea 200)
        // public PlayerSlotConfig GetPlayerSlot(int actorNumber)
        // {
        //     return ConnectedPlayers.TryGetValue(actorNumber, out var config) ? config : null;
        // }
        
        public PlayerSlotConfig GetPlayerSlot(int actorNumber)
        {
            return ConnectedPlayers.TryGetValue(actorNumber, out var config) ? config : null;
        }
        
        public PlayerSlotConfig GetPlayerSlotByIndex(int index)
        {
            if (index < 0 || index >= PlayerSlots.Count) return null;
            return PlayerSlots[index];
        }
        
        public void SetPlayerReady(int actorNumber, bool ready)
        {
            if (ConnectedPlayers.TryGetValue(actorNumber, out var config))
            {
                config.IsReady = ready;
                CivilizameMPPlugin.Log.LogInfo($"[MPState] {config.PlayerName} ready: {ready}");
            }
        }
        
        public bool AreAllPlayersReady()
        {
            foreach (var player in ConnectedPlayers.Values)
            {
                if (!player.IsReady) return false;
            }
            return ConnectedPlayers.Count > 0;
        }

        public void Reset()
        {
            ConnectedPlayers.Clear();
            for (int i = 0; i < MaxPlayers; i++)
            {
                PlayerSlots[i] = new PlayerSlotConfig 
                { 
                    SlotIndex = i, 
                    PlayerName = $"Slot {i+1}",
                    IsConnected = false,
                    IsHuman = false,
                    IsReady = false,
                    ActorNumber = -1
                };
            }
            IsHost = false;
            IsClient = false;
            LocalSlotIndex = -1;
            LocalActorNumber = -1;
            PendingGameConfig = null;
            LastError = null;
            SetState(MPGameState.None);
        }
    }
}