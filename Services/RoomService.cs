using LuduBackend.Models;

namespace LuduBackend.Services;

/// Exact port of GameNotifier game logic (no AI evaluation — that stays in hub)
public class RoomService
{
    private readonly Dictionary<string, GameRoom> _rooms = new();
    private readonly Dictionary<string, string> _connRooms = new();
    private readonly Random _rng = new();
    private int _roomCounter = 1000;

    // ── Lookups ─────────────────────────────────────────────────────────────

    public GameRoom? GetRoomByConn(string connId) =>
        _connRooms.TryGetValue(connId, out var id) ? _rooms.GetValueOrDefault(id) : null;

    public List<RoomSummary> GetLobbyList() =>
        _rooms.Values
            .Where(r => r.Phase == GamePhase.Waiting && r.FilledSlots < r.MaxSlots)
            .Select(r => new RoomSummary(r.RoomId, r.HostName, r.FilledSlots, r.MaxSlots, $"{r.Settings.PlayerCount}P"))
            .ToList();

    // ── Room lifecycle ───────────────────────────────────────────────────────

    public (GameRoom room, int slot) CreateRoom(string connId, string hostName, GameSettings settings)
    {
        var roomId = (++_roomCounter).ToString();
        var room = new GameRoom { RoomId=roomId, HostConnectionId=connId, HostName=hostName, Settings=settings };
        var activeIds = settings.PlayerCount switch { 2=>new[]{1,3}, 3=>new[]{0,1,3}, _=>new[]{0,1,2,3} };

        for (int i = 0; i < 4; i++)
            room.Players.Add(new Player
            {
                Id=i, IsAI=false, IsActive=activeIds.Contains(i), PartnerId=(i+2)%4,
                Pieces=Enumerable.Range(0,4).Select(p=>new Piece{Id=p,Color=i}).ToList()
            });

        int hostSlot = activeIds[0];
        room.Players[hostSlot].Name = hostName;
        room.Players[hostSlot].ConnectionId = connId;
        room.SlotConnections[hostSlot] = connId;
        room.TurnSlot = activeIds[0];
        _rooms[roomId] = room;
        _connRooms[connId] = roomId;
        return (room, hostSlot);
    }

    public (GameRoom? room, string? error) RequestJoin(string connId, string roomId, string name)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return (null,"Room not found");
        if (room.Phase != GamePhase.Waiting) return (null,"Game already started");
        if (room.FilledSlots >= room.MaxSlots) return (null,"Room is full");
        if (room.JoinRequests.ContainsKey(connId)) return (null,"Request already pending");
        room.JoinRequests[connId] = name;
        return (room, null);
    }

    public (GameRoom? room, int slot, string? error) ApproveJoin(string hostConnId, string requesterConnId)
    {
        var room = GetRoomByConn(hostConnId);
        if (room == null || room.HostConnectionId != hostConnId) return (null,-1,"Not host");
        if (!room.JoinRequests.TryGetValue(requesterConnId, out var name)) return (null,-1,"No such request");
        room.JoinRequests.Remove(requesterConnId);
        var freeSlot = room.Players.Where(p=>p.IsActive&&p.ConnectionId==null).Select(p=>p.Id).Cast<int?>().FirstOrDefault();
        if (freeSlot == null) return (null,-1,"Room full");
        int slot = freeSlot.Value;
        room.Players[slot].Name = name;
        room.Players[slot].ConnectionId = requesterConnId;
        room.SlotConnections[slot] = requesterConnId;
        _connRooms[requesterConnId] = room.RoomId;
        return (room, slot, null);
    }

    public (GameRoom? room, string? error) DeclineJoin(string hostConnId, string requesterConnId)
    {
        var room = GetRoomByConn(hostConnId);
        if (room == null) return (null,"Not host");
        room.JoinRequests.Remove(requesterConnId);
        return (room, null);
    }

    public GameRoom? StartGame(string connId)
    {
        var room = GetRoomByConn(connId);
        if (room==null||room.HostConnectionId!=connId||room.Phase!=GamePhase.Waiting) return null;
        foreach (var p in room.Players.Where(p=>p.IsActive&&p.ConnectionId==null))
        { p.IsAI=true; p.Name=$"Bot {BoardConstants.ColorNames[p.Id]}"; }
        if (room.Settings.PlayerCount < 4) room.Settings.TeamPlay = false;
        room.Phase=GamePhase.Play; room.CanRoll=true;
        room.MatchFirstSixRolled=false; room.DicePool=[]; room.ConsecutiveExtra=0;

        // Find first valid player
        while (!room.Players[room.TurnSlot].IsActive ||
               (room.Players[room.TurnSlot].Finished && !room.Settings.TeamPlay) ||
               !GameLogic.HasAnyPossibleMove(room.Players[room.TurnSlot], room.Players, room.Settings))
        {
            room.TurnSlot = (room.TurnSlot + 1) % 4;
        }

        return room;
    }

    public void RemoveConnection(string connId)
    {
        if (!_connRooms.TryGetValue(connId, out var roomId)) return;
        _connRooms.Remove(connId);
        if (!_rooms.TryGetValue(roomId, out var room)) return;
        foreach (var p in room.Players.Where(p=>p.ConnectionId==connId))
        {
            p.ConnectionId=null;
            if(p.IsActive&&!p.Finished) {
                p.IsAI=true;
                p.Name = $"Bot {BoardConstants.ColorNames[p.Id]}";
            }
        }
        room.JoinRequests.Remove(connId);
        if (room.HostConnectionId==connId&&room.Phase==GamePhase.Waiting) _rooms.Remove(roomId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    int GetCtrlId(GameRoom room)
    {
        var act = room.Players[room.TurnSlot];
        return act.Finished && act.IsHelper ? act.PartnerId : act.Id;
    }

    bool IsAiCall(string connId) => connId == "__ai__";

    GameRoom? FindRoom(string connId) =>
        IsAiCall(connId)
            ? _rooms.Values.FirstOrDefault(r => r.Phase==GamePhase.Play && r.Players[r.TurnSlot].IsAI)
            : GetRoomByConn(connId);

    bool ValidateTurn(GameRoom room, string connId)
    {
        if (IsAiCall(connId)) return true;
        var act = room.Players[room.TurnSlot];
        if (act.IsAI) return true;
        return room.SlotConnections.TryGetValue(room.TurnSlot, out var owner) && owner == connId;
    }

    public int? AutoSelectDie(GameRoom room)
    {
        if (room.DicePool.Count == 0) return null;
        int ctrlId = GetCtrlId(room);
        var pCtrl = room.Players[ctrlId];
        if (!pCtrl.IsAI)
        {
            for (int i = 0; i < room.DicePool.Count; i++)
            {
                foreach (var pc in pCtrl.Pieces)
                {
                    if (pc.State == PieceState.Home || pc.HasKilledThisTurn) continue;
                    var dest = GameLogic.CalculateDestination(pCtrl, pc, room.DicePool[i], room.Players, room.DicePool, i, room.Settings);
                    if (dest != null) return i;
                }
            }
        }
        return 0;
    }

    // ── RollDice — exact port ──────────────────────────────────────────────
    // Overload that takes a room directly — used by CheckAndAutoPlay for AI rolls.
    // Avoids the FindRoom("__ai__") global search which is fragile in multi-game.

    public (GameRoom? room, bool extra, bool prioritizePrison, string? toast) RollDice(GameRoom room)
    {
        if (room.Phase != GamePhase.Play || !room.CanRoll) return (null,false,false,null);

        room.CanRoll = false;
        int actId = room.TurnSlot;
        var actPlayer = room.Players[actId];

        int d1 = _rng.Next(1,7), d2 = _rng.Next(1,7);

        // Team game-over check: helper wins if partner finished
        if (room.Settings.TeamPlay && actPlayer.Finished && actPlayer.IsHelper)
        {
            int partnerId = actPlayer.PartnerId;
            var partner = room.Players[partnerId];
            if (partner.Finished)
            {
                if (CheckGameOver(room)) return (room, false, false, null);
                EndTurn(room);
                return (room, false, false, null);
            }
        }

        // Dice show on all-home roll
        bool allHome = actPlayer.Pieces.All(pc => pc.State == PieceState.Home);
        if (allHome && !actPlayer.Finished)
        {
            room.DicePool = [d1, d2];
            bool winningRoll = d1==6||d2==6||(d1==1&&d2==1)||(d1==6&&d2==6);
            if (winningRoll)
            {
                actPlayer.Finished = true;
                if (room.Settings.TeamPlay) actPlayer.IsHelper = true;
            }
            if (CheckGameOver(room)) return (room, false, false, null);
            EndTurn(room);
            return (room, false, false, null);
        }

        bool prioritizePrison = d1==6||d2==6||(d1==1&&d2==1);
        bool isQualifyingRoll = d1==6||d2==6||(d1==1&&d2==1)||(d1==6&&d2==6);

        // Finished but not yet helper
        if (actPlayer.Finished && !actPlayer.IsHelper)
        {
            if (isQualifyingRoll)
            {
                actPlayer.IsHelper = true;
                room.DicePool = [];
                room.CanRoll = false;
            }
            EndTurn(room);
            return (room, false, false, null);
        }

        bool extra = false;
        var newPool = room.DicePool.ToList();
        bool isDoubleSix = d1==6&&d2==6, isDoubleOne = d1==1&&d2==1;
        bool isSixOrDoubleOne = d1==6||d2==6||isDoubleOne;
        bool isGlobalFirstSix = !room.MatchFirstSixRolled && isSixOrDoubleOne;

        if (isGlobalFirstSix)
        {
            room.MatchFirstSixRolled = true;
            bool hasFour = (d1==6&&d2==4)||(d2==6&&d1==4);
            newPool.Clear();
            if (hasFour)       { newPool.AddRange([6,6,6,6,4]); }
            else if (isDoubleSix) { newPool.AddRange([6,6,6]); extra=true; }
            else if (isDoubleOne) { newPool.AddRange([6,6,6]); extra=true; }
            else                  { newPool.AddRange([d1,d2,6]); }
        }
        else
        {
            if (room.Settings.DoubleSixBonus && (isDoubleSix||isDoubleOne))
            { extra = room.ConsecutiveExtra<3; newPool.AddRange([6,6]); }
            else newPool.AddRange([d1,d2]);
            if (d1==d2 && (d1==6||d1==1)) extra = true;
        }

        newPool.Sort((a,b) => b.CompareTo(a));
        room.DicePool = newPool;
        room.ConsecutiveExtra = extra ? room.ConsecutiveExtra+1 : 0;
        room.CanRoll = extra;
        room.SelectedDieIndex = AutoSelectDie(room);

        return (room, extra, prioritizePrison, null);
    }

    // Connection-based overload — delegates to room-based overload after lookup+validation.
    public (GameRoom? room, bool extra, bool prioritizePrison, string? toast) RollDice(string connId)
    {
        var room = FindRoom(connId);
        if (room==null) return (null,false,false,null);
        if (!ValidateTurn(room, connId)) return (null,false,false,null);
        return RollDice(room);
    }

    // ── MovePiece — exact port ─────────────────────────────────────────────

    public (GameRoom? room, string? toast) MovePiece(string connId, int pieceId, int pieceColor, List<int> dieIndices)
    {
        var room = FindRoom(connId);
        if (room==null||room.Phase!=GamePhase.Play) return (null,null);
        if (!ValidateTurn(room, connId)) return (null,null);

        int ctrlId = GetCtrlId(room);
        var pCtrl = room.Players[ctrlId];
        var piece = pCtrl.Pieces.FirstOrDefault(p => p.Id==pieceId && p.Color==pieceColor);
        if (piece==null) return (null,null);
        if (room.DicePool.Count==0) return (null,null);

        MoveDestination? dest;
        if (dieIndices.Count==2 && room.DicePool.Count>=2)
        {
            int sum = dieIndices.Select(i=>room.DicePool[i]).Sum();
            dest = GameLogic.CalculateDestination(pCtrl, piece, sum, room.Players, [], -1, room.Settings);
        }
        else if (dieIndices.Count==1)
        {
            int idx = dieIndices[0];
            if (idx>=room.DicePool.Count) return (null,null);
            dest = GameLogic.CalculateDestination(pCtrl, piece, room.DicePool[idx], room.Players, room.DicePool, idx, room.Settings);
        }
        else return (null,null);

        if (dest==null) return (null,null);

        return ExecuteMove(room, pCtrl, piece, dest, dieIndices);
    }

    // ── ExecuteMove — exact port ───────────────────────────────────────────

    public (GameRoom room, string? toast) ExecuteMove(GameRoom room, Player pCtrl, Piece piece, MoveDestination target, List<int> dieIndicesUsed)
    {
        piece.State = target.TargetState;
        piece.Pos = target.TargetPos;

        bool rewardTurn = room.CanRoll;
        if (target.TargetState == PieceState.Home) rewardTurn = true;

        var newPool = room.DicePool.ToList();
        foreach (var idx in dieIndicesUsed.OrderByDescending(x=>x)) newPool.RemoveAt(idx);

        var safeZones = GameLogic.EffectiveSafeZones(room.Settings);
        if (target.TargetState == PieceState.Board && !safeZones.Contains(target.TargetPos))
        {
            foreach (var op in room.Players)
            {
                if (op.Id==pCtrl.Id||op.Id==pCtrl.PartnerId||!op.IsActive) continue;
                foreach (var opc in op.Pieces.Where(x=>x.State==PieceState.Board&&x.Pos==target.TargetPos).ToList())
                {
                    op.TimesHit++;
                    if (room.Settings.PrisonRule) { opc.State=PieceState.Prison; opc.Pos=-2; opc.PrisonerOf=pCtrl.Id; }
                    else { opc.State=PieceState.Yard; opc.Pos=-1; opc.PrisonerOf=null; }
                    pCtrl.HasKilled = true;
                    piece.HasKilledThisTurn = true;
                }
            }
        }

        if (!room.Settings.KillToEnter) pCtrl.HasKilled = true;

        room.DicePool = newPool;
        room.CanRoll = rewardTurn;
        room.SelectedDieIndex = AutoSelectDie(room);

        if (CheckGameOver(room)) return (room, null);

        if (newPool.Count==0 && !rewardTurn)
            EndTurn(room);

        return (room, null);
    }

    // ── CheckGameOver — exact port ─────────────────────────────────────────

    public bool CheckGameOver(GameRoom room)
    {
        bool team1 = room.Players[0].Finished && room.Players[2].Finished;
        bool team2 = room.Players[1].Finished && room.Players[3].Finished;
        bool solo  = room.Players.Any(p => p.IsActive && p.Finished);
        bool over  = (room.Settings.TeamPlay&&(team1||team2)) || (!room.Settings.TeamPlay&&solo);
        if (over) room.Phase = GamePhase.End;
        return over;
    }

    // ── EndTurn ─────────────────────────────────────────────────────────────

    public void EndTurn(GameRoom room)
    {
        foreach (var p in room.Players)
            foreach (var pc in p.Pieces)
                pc.HasKilledThisTurn = false;

        int safety = 0;
        do
        {
            room.TurnSlot = (room.TurnSlot + 1) % 4;
            safety++;
            if (safety > 8)
            {
                room.Phase = GamePhase.End;
                return;
            }
        }
        while (!room.Players[room.TurnSlot].IsActive ||
               (room.Players[room.TurnSlot].Finished && !room.Settings.TeamPlay) ||
               !GameLogic.HasAnyPossibleMove(room.Players[room.TurnSlot], room.Players, room.Settings));

        room.DicePool = [];
        room.ConsecutiveExtra = 0;
        room.CanRoll = true;
        room.SelectedDieIndex = null;
    }

    // ── GetAllValidMoves for current turn ────────────────────────────────────

    public List<AiMove> GetCurrentMoves(GameRoom room)
    {
        int ctrlId = GetCtrlId(room);
        return GameLogic.GetAllValidMoves(room.Players[ctrlId], room.DicePool, room.Players, room.Settings);
    }
}