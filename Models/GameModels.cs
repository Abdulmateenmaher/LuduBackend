namespace LuduBackend.Models;

public enum PieceState { Yard, Board, HomeStretch, Home, Prison }

public class Piece
{
    public int Id { get; set; }
    public int Color { get; set; }
    public PieceState State { get; set; } = PieceState.Yard;
    public int Pos { get; set; } = -1;
    public int? PrisonerOf { get; set; }
    public bool HasKilledThisTurn { get; set; }
}

public class Player
{
    public int Id { get; set; }
    public bool IsAI { get; set; }
    public bool IsActive { get; set; }
    public int PartnerId { get; set; }
    public string Name { get; set; } = "";
    public bool HasKilled { get; set; }
    public bool Finished { get; set; }
    public bool IsHelper { get; set; }
    public bool HasRolledFirstSix { get; set; }
    public int TimesHit { get; set; }
    public List<Piece> Pieces { get; set; } = [];
    public string? ConnectionId { get; set; }
}

public class GameSettings
{
    public bool DoubleSixBonus { get; set; } = true;
    public bool KillToEnter { get; set; } = true;
    public bool SafeZonesEnabled { get; set; } = true;
    public bool AutoMoveUnambiguous { get; set; } = true;
    public bool PrisonRule { get; set; } = true;
    public bool TeamPlay { get; set; } = true;
    public int PlayerCount { get; set; } = 4;
}

public class GameRoom
{
    public string RoomId { get; set; } = "";
    public string HostConnectionId { get; set; } = "";
    public string HostName { get; set; } = "";
    public GameSettings Settings { get; set; } = new();
    public List<Player> Players { get; set; } = [];
    public GamePhase Phase { get; set; } = GamePhase.Waiting;
    public int TurnSlot { get; set; }
    public List<int> DicePool { get; set; } = [];
    public int ConsecutiveExtra { get; set; }
    public bool CanRoll { get; set; }
    public bool MatchFirstSixRolled { get; set; }
    public Dictionary<int, string> SlotConnections { get; set; } = [];
    // Pending join requests: connectionId -> playerName
    public Dictionary<string, string> JoinRequests { get; set; } = [];
    public int FilledSlots => Players.Count(p => p.IsActive && p.ConnectionId != null);
    public int MaxSlots => Settings.PlayerCount;
    public int? SelectedDieIndex { get; set; }
}

public enum GamePhase { Waiting, Play, End }

// Lobby summary shown to browsing players
public record RoomSummary(string RoomId, string HostName, int FilledSlots, int MaxSlots, string Settings);

// Messages
public record RoomCreatedMsg(string RoomId, int YourSlot, GameRoom Room);
public record RoomJoinedMsg(string RoomId, int YourSlot, GameRoom Room);
public record GameStateMsg(GameRoom Room, string? Toast);
public record RoomErrorMsg(string Error);
public record LobbyListMsg(List<RoomSummary> Rooms);
public record JoinRequestMsg(string RequesterConnId, string RequesterName);
