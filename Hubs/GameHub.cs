using LuduBackend.Models;
using LuduBackend.Services;
using Microsoft.AspNetCore.SignalR;

namespace LuduBackend.Hubs;

public class GameHub(RoomService rooms) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("LobbyList", new LobbyListMsg(rooms.GetLobbyList()));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        var room = rooms.GetRoomByConn(Context.ConnectionId);
        rooms.RemoveConnection(Context.ConnectionId);
        if (room != null)
        {
            await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, "A player disconnected"));
            // Trigger AI moves if it's now an AI player's turn
            if (room.Phase == GamePhase.Play)
                await CheckAndAutoPlay(room);
        }
        await BroadcastLobby();
        await base.OnDisconnectedAsync(ex);
    }

    // ── Lobby ───────────────────────────────────────────────────────────────

    public async Task GetLobby() =>
        await Clients.Caller.SendAsync("LobbyList", new LobbyListMsg(rooms.GetLobbyList()));

    public async Task CreateRoom(string hostName, GameSettings settings)
    {
        var (room, slot) = rooms.CreateRoom(Context.ConnectionId, hostName, settings);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.RoomId);
        await Clients.Caller.SendAsync("RoomCreated", new RoomCreatedMsg(room.RoomId, slot, room));
        await BroadcastLobby();
    }

    public async Task RequestJoin(string roomId, string playerName)
    {
        var (room, error) = rooms.RequestJoin(Context.ConnectionId, roomId, playerName);
        if (error != null) { await Clients.Caller.SendAsync("Error", new RoomErrorMsg(error)); return; }
        await Clients.Client(room!.HostConnectionId).SendAsync("JoinRequest", new JoinRequestMsg(Context.ConnectionId, playerName));
        await Clients.Caller.SendAsync("JoinRequestSent", roomId);
    }

    public async Task ApproveJoin(string requesterConnId)
    {
        var (room, slot, error) = rooms.ApproveJoin(Context.ConnectionId, requesterConnId);
        if (error != null) { await Clients.Caller.SendAsync("Error", new RoomErrorMsg(error)); return; }
        await Groups.AddToGroupAsync(requesterConnId, room!.RoomId);
        await Clients.Client(requesterConnId).SendAsync("RoomJoined", new RoomJoinedMsg(room.RoomId, slot, room));
        await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, $"{room.Players[slot].Name} joined"));
        await BroadcastLobby();
    }

    public async Task DeclineJoin(string requesterConnId)
    {
        var (room, _) = rooms.DeclineJoin(Context.ConnectionId, requesterConnId);
        await Clients.Client(requesterConnId).SendAsync("JoinDeclined", "Host declined your request");
        if (room != null)
            await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, null));
    }

    public async Task StartGame()
    {
        var room = rooms.StartGame(Context.ConnectionId);
        if (room == null) { await Clients.Caller.SendAsync("Error", new RoomErrorMsg("Cannot start")); return; }
        await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, "Match Started!"));
        await BroadcastLobby();
        await CheckAndAutoPlay(room);
    }

    // ── Game actions ────────────────────────────────────────────────────────

    public async Task RollDice()
    {
        var (room, extra, prioritizePrison, toast) = rooms.RollDice(Context.ConnectionId);
        if (room == null) return;
        await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, toast));
        if (room.Phase == GamePhase.End) return;
        await CheckAndAutoPlay(room, prioritizePrison);
    }

    public async Task MovePiece(int pieceId, int pieceColor, List<int> dieIndices)
    {
        var (room, toast) = rooms.MovePiece(Context.ConnectionId, pieceId, pieceColor, dieIndices);
        if (room == null) return;
        await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, toast));
        if (room.Phase == GamePhase.End) return;
        await CheckAndAutoPlay(room);
    }

    // ── AutoPlay — called by the client when the 10-second dice timer expires ─
    // The current human player didn't roll. Server rolls on their behalf and
    // immediately plays out all available pieces using the same AI logic the
    // bot uses. After all pieces are played, turn ends (or extra-6 chains).

    public async Task AutoPlay()
    {
        var room = rooms.GetRoomByConn(Context.ConnectionId);
        if (room == null || room.Phase != GamePhase.Play) return;
        if (room.Players[room.TurnSlot].IsAI) return; // AI doesn't need it

        // Notify other players
        await Clients.Group(room.RoomId).SendAsync("GameState",
            new GameStateMsg(room, $"{room.Players[room.TurnSlot].Name} timed out — auto-play"));

        // If still in dice phase, roll on player's behalf
        if (room.CanRoll && room.DicePool.Count == 0)
        {
            var (rolledRoom, extra, prioritizePrison, _) = rooms.RollDice(Context.ConnectionId);
            if (rolledRoom != null)
            {
                await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, null));
                if (room.Phase == GamePhase.End) return;
            }
        }

        // Now consume the dice pool piece-by-piece using the AI's exact evaluator.
        // Loop because extra rolls (6, double-6, etc.) can chain.
        const int MAX_MOVE_STEPS = 32;
        int moveStep = 0;
        while (room.Phase == GamePhase.Play && room.DicePool.Count > 0
               && !room.Players[room.TurnSlot].IsAI && moveStep++ < MAX_MOVE_STEPS)
        {
            int actId = room.TurnSlot;
            var actPlayer = room.Players[actId];
            int ctrlId = actPlayer.Finished && actPlayer.IsHelper ? actPlayer.PartnerId : actId;
            var pCtrl = room.Players[ctrlId];

            // If this player is finished and not yet a helper, end turn immediately.
            if (actPlayer.Finished && !actPlayer.IsHelper)
            {
                rooms.EndTurn(room);
                await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, null));
                break;
            }

            var pool = room.DicePool.ToList();
            var allMoves = GameLogic.GetAllValidMoves(pCtrl, pool, room.Players, room.Settings);

            if (allMoves.Count == 0)
            {
                // No valid moves — end turn (the "no_move_chance" branch)
                rooms.EndTurn(room);
                await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, "No valid moves"));
                break;
            }

            // Pick best by AI scoring
            allMoves.Sort((a, b) =>
            {
                int sA = EvaluateAiMove(a, room.Players, ctrlId, false, pool, a.DieIndices, room.Settings);
                int sB = EvaluateAiMove(b, room.Players, ctrlId, false, pool, b.DieIndices, room.Settings);
                return sB.CompareTo(sA);
            });
            var best = allMoves[0];

            var (_, moveToast) = rooms.ExecuteMove(room, pCtrl, best.Piece, best.Target, best.DieIndices);
            await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, moveToast));
            if (room.Phase == GamePhase.End) return;

            // If the move granted an extra roll, keep going automatically
            if (!room.CanRoll) break;
        }
    }

    // ── _checkAndAutoPlay — iterative loop to avoid stack overflow ──────────
    // Originally a recursive method. With long AI chains (multiple extra-turn
    // 6's in a row, helper pass-throughs, etc.) the recursion could exceed
    // the .NET thread stack. Now driven by a `while` loop with a safety cap.

    private async Task CheckAndAutoPlay(GameRoom room, bool prioritizePrison = false)
    {
        if (room.Phase != GamePhase.Play) return;

        // Iterative driver — replaces deep recursion.
        const int MAX_STEPS = 64;
        int step = 0;

        while (room.Phase == GamePhase.Play && step++ < MAX_STEPS)
        {
            int actId = room.TurnSlot;
            var actPlayer = room.Players[actId];
            int ctrlId = actPlayer.Finished && actPlayer.IsHelper ? actPlayer.PartnerId : actId;
            var pCtrl = room.Players[ctrlId];
            var pool = room.DicePool.ToList();
            bool currentCanRoll = room.CanRoll;
            var allMoves = GameLogic.GetAllValidMoves(pCtrl, pool, room.Players, room.Settings);

            if (actPlayer.IsAI)
            {
                // AI: if canRoll → roll after delay
                if (currentCanRoll)
                {
                    await Task.Delay(490);
                    if (room.Phase != GamePhase.Play || room.TurnSlot != actId) return;
                    var (r, extra2, pp2, toast2) = rooms.RollDice("__ai__");
                    if (r == null) return;
                    await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, toast2));
                    if (room.Phase == GamePhase.End) return;
                    prioritizePrison = pp2;
                    continue; // loop picks up the new state
                }

                // AI: finished but not helper → end turn
                if (actPlayer.Finished && !actPlayer.IsHelper)
                {
                    await Task.Delay(350);
                    rooms.EndTurn(room);
                    await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, null));
                    if (room.Phase == GamePhase.End) return;
                    continue;
                }

                // AI: helper with no moves → end turn
                if (actPlayer.Finished && actPlayer.IsHelper && allMoves.Count == 0)
                {
                    await Task.Delay(425);
                    rooms.EndTurn(room);
                    await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, null));
                    if (room.Phase == GamePhase.End) return;
                    continue;
                }

                // AI: has moves → pick best
                if (allMoves.Count > 0)
                {
                    allMoves.Sort((a, b) =>
                    {
                        int sA = EvaluateAiMove(a, room.Players, ctrlId, prioritizePrison, pool, a.DieIndices, room.Settings);
                        int sB = EvaluateAiMove(b, room.Players, ctrlId, prioritizePrison, pool, b.DieIndices, room.Settings);
                        return sB.CompareTo(sA);
                    });
                    var best = allMoves[0];
                    await Task.Delay(500);
                    if (room.Phase != GamePhase.Play || room.TurnSlot != actId) return;
                    var (moved, toast3) = rooms.ExecuteMove(room, pCtrl, best.Piece, best.Target, best.DieIndices);
                    await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, toast3));
                    if (room.Phase == GamePhase.End) return;
                    prioritizePrison = false;
                    continue;
                }
                else
                {
                    // No moves at all
                    await Task.Delay(420);
                    rooms.EndTurn(room);
                    await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, "No valid moves"));
                    if (room.Phase == GamePhase.End) return;
                    continue;
                }
            }

            // ── Human player ──

            // Finished but not helper → auto-end
            if (actPlayer.Finished && !actPlayer.IsHelper)
            {
                await Task.Delay(280);
                rooms.EndTurn(room);
                await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, null));
                if (room.Phase == GamePhase.End) return;
                continue;
            }

            // No moves and can't roll → end turn
            if (allMoves.Count == 0 && !currentCanRoll && pool.Count > 0)
            {
                await Task.Delay(1120);
                rooms.EndTurn(room);
                await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, "No valid moves"));
                if (room.Phase == GamePhase.End) return;
                continue;
            }

            // Auto-move if exactly one valid move
            if (allMoves.Count == 1 && !currentCanRoll)
            {
                var only = allMoves[0];
                await Task.Delay(420);
                if (room.Phase != GamePhase.Play || room.TurnSlot != actId) return;
                var (_, toast4) = rooms.ExecuteMove(room, pCtrl, only.Piece, only.Target, only.DieIndices);
                await Clients.Group(room.RoomId).SendAsync("GameState", new GameStateMsg(room, toast4));
                if (room.Phase == GamePhase.End) return;
                continue;
            }

            // Human has dice and possibly multiple valid moves — wait for input.
            return;
        }
    }

    // ── _evaluateAiMove — exact port ─────────────────────────────────────────

    static int EvaluateAiMove(AiMove move, List<Player> players, int myId, bool prioritizePrison,
        List<int> currentPool, List<int> dieIndices, GameSettings settings)
    {
        var remainingDice = new List<int>();
        for (int k = 0; k < currentPool.Count; k++)
            if (!dieIndices.Contains(k)) remainingDice.Add(currentPool[k]);

        int score = move.DieValue * 10;
        int targetPos = move.Target.TargetPos;
        var targetState = move.Target.TargetState;
        int currentPos = move.Piece.Pos;
        var currentState = move.Piece.State;
        var safeZones = GameLogic.EffectiveSafeZones(settings);

        int piecesOut = players[myId].Pieces.Count(p => p.State != PieceState.Yard);
        int prisoners = players[myId].Pieces.Count(p => p.State == PieceState.Prison);

        int mySecondStop = BoardConstants.MyStops[myId][1];
        int myFirstStop  = BoardConstants.MyStops[myId][0];
        bool isTargetSecondSafe  = targetPos == mySecondStop;
        bool isCurrentSecondSafe = currentPos == mySecondStop;
        bool isTargetFirstSafe   = targetPos == myFirstStop;
        bool isCurrentFirstSafe  = currentPos == myFirstStop;

        bool isCurrentlyOnSafePlace = currentState == PieceState.Board &&
            (safeZones.Contains(currentPos) || IsOwnColoredSafe(myId, currentPos));

        if (currentState == PieceState.Board)
            score += isCurrentlyOnSafePlace ? -4000 : 4000;

        // Hit
        bool isHit = targetState == PieceState.Board && !safeZones.Contains(targetPos) &&
                     GameLogic.HasOpponent(targetPos, myId, players);

        // Block
        int sameColorAtTarget = (targetState == PieceState.Board || targetState == PieceState.HomeStretch)
            ? players[myId].Pieces.Count(p => p != move.Piece && p.State == targetState && p.Pos == targetPos)
            : 0;
        bool formingBlock = sameColorAtTarget == 1;

        int sameColorAtCurrent = currentState == PieceState.Board
            ? players[myId].Pieces.Count(p => p != move.Piece && p.State == PieceState.Board && p.Pos == currentPos)
            : 0;
        bool leavingVulnerable = sameColorAtCurrent == 1 && !safeZones.Contains(currentPos);

        bool formingChokePoint = formingBlock && isTargetSecondSafe;
        bool breakingChokePoint = sameColorAtCurrent == 1 && isCurrentSecondSafe;

        int distToSecondStop = (mySecondStop - currentPos + 52) % 52;
        bool isBehindSecondStop = distToSecondStop > 0 && distToSecondStop <= 6;
        bool willPassSecondStop = isBehindSecondStop && distToSecondStop < move.DieValue && targetState == PieceState.Board;

        bool isPastSecondStop = false;
        if (currentState == PieceState.HomeStretch) isPastSecondStop = true;
        else if (currentState == PieceState.Board)
        {
            int distFromSecondStop = (currentPos - mySecondStop + 52) % 52;
            if (distFromSecondStop > 0 && distFromSecondStop <= 3) isPastSecondStop = true;
        }

        int piecesAtSecondStop = players[myId].Pieces.Count(p => p.State == PieceState.Board && p.Pos == mySecondStop);
        int piecesAtFirstStop  = players[myId].Pieces.Count(p => p.State == PieceState.Board && p.Pos == myFirstStop);

        int opponentPiecesOut = 0;
        foreach (var p in players)
            if (p.Id != myId && p.PartnerId != myId && p.IsActive)
                opponentPiecesOut += p.Pieces.Count(pc => pc.State != PieceState.Yard);

        // Danger / chasing scan
        bool currentlyInDanger = false, targetInDanger = false, targetIsChasing = false;
        int minThreatDist = 7;
        if (currentState == PieceState.Board && !safeZones.Contains(currentPos) && sameColorAtCurrent == 0)
            foreach (var p in players)
            {
                if (p.Id == myId || p.PartnerId == myId || !p.IsActive) continue;
                foreach (var pc in p.Pieces)
                    if (pc.State == PieceState.Board)
                    {
                        int d = (currentPos - pc.Pos + 52) % 52;
                        if (d > 0 && d <= 6) { currentlyInDanger=true; if(d<minThreatDist) minThreatDist=d; }
                    }
            }
        if (targetState == PieceState.Board && !safeZones.Contains(targetPos) && !formingBlock)
            foreach (var p in players)
            {
                if (p.Id == myId || p.PartnerId == myId || !p.IsActive) continue;
                foreach (var pc in p.Pieces)
                    if (pc.State == PieceState.Board)
                    {
                        int dt = (targetPos - pc.Pos + 52) % 52;
                        if (dt > 0 && dt <= 6) targetInDanger = true;
                        int da = (pc.Pos - targetPos + 52) % 52;
                        if (da > 0 && da <= 6) targetIsChasing = true;
                    }
            }

        // ── Scoring — exact mirror of Flutter ──

        if (willPassSecondStop) score -= 100000;

        if (prioritizePrison && currentState == PieceState.Prison && targetState == PieceState.Board)
            score += 30000;

        int piecesOnBoard = players[myId].Pieces.Count(p => p.State==PieceState.Board || p.State==PieceState.HomeStretch);
        if (piecesOnBoard <= 2 && prisoners > 0 && move.DieValue == 6)
            if (currentState==PieceState.Prison && targetState==PieceState.Board)
                score += 50000;

        if (move.DieValue == 6)
        {
            int outDiff = opponentPiecesOut - piecesOut;
            if (currentState==PieceState.Prison && targetState==PieceState.Board)
            { score+=30000; score+=10000*(outDiff+1); if(piecesOnBoard<=1) score+=10000; }
            if (currentState==PieceState.Yard && targetState==PieceState.Board)
            { score+=20000; score+=8000*(outDiff+1); }
        }

        int piecesInYard = players[myId].Pieces.Count(p => p.State==PieceState.Yard);
        if (prioritizePrison && piecesInYard>=3 && prisoners>0)
            if (currentState==PieceState.Prison && targetState==PieceState.Board)
                score += 22000;

        int otherPiecesHome = players[myId].Pieces.Count(p => p!=move.Piece && p.State==PieceState.Home);
        bool isFinishing = targetState==PieceState.Home && otherPiecesHome==3;
        if (isFinishing) { score+=150000; if(remainingDice.Contains(6)) score+=20000; }

        if (formingChokePoint) score += 200000;
        if (formingBlock && isTargetFirstSafe) score += 40000;

        if (isHit)
        {
            score += 60000;
            if (remainingDice.Count > 0)
            {
                int remSum = remainingDice.Sum();
                foreach (var p in players)
                {
                    if (p.Id==myId||p.PartnerId==myId||!p.IsActive) continue;
                    foreach (var pc in p.Pieces)
                        if (pc.State==PieceState.Board)
                        {
                            int dist = (pc.Pos-targetPos+52)%52;
                            if (dist>0 && dist<=remSum) score+=10000;
                        }
                }
            }
        }

        if (targetState == PieceState.Home)
        {
            score += 12000;
            int notHome = players[myId].Pieces.Count(p => p.State!=PieceState.Home);
            if (notHome > 1) score -= 5000;
        }

        if (formingBlock && IsOwnColoredSafe(myId,targetPos) && !isTargetFirstSafe && !isTargetSecondSafe) score+=5000;
        if (IsOwnColoredSafe(myId,targetPos) && !formingBlock) score+=8000;
        if (formingBlock && !IsOwnColoredSafe(myId,targetPos)) score-=100000;

        if (currentlyInDanger && !targetInDanger) score += 6000+(7-minThreatDist)*1000;
        else if (currentlyInDanger && targetInDanger) score += 1000;

        if (targetState==PieceState.Board && safeZones.Contains(targetPos) && !formingBlock) score+=3000;
        if (targetIsChasing && !targetInDanger) score+=4000;

        if (targetState == PieceState.Board)
        {
            int futureHit=0;
            foreach (var p in players)
            {
                if (p.Id==myId||p.PartnerId==myId||!p.IsActive) continue;
                foreach (var pc in p.Pieces)
                    if (pc.State==PieceState.Board)
                    { int d=(pc.Pos-targetPos+52)%52; if(d>0&&d<=6) futureHit++; }
            }
            score += futureHit*2000;
        }

        if (currentState==PieceState.Yard)
        { score+=2000; if(piecesOut>=3) score-=1000; if(prioritizePrison&&prisoners>0) score-=10000; }
        if (targetState==PieceState.HomeStretch) score+=1500;

        if (breakingChokePoint) score-=100000;
        if (isCurrentSecondSafe) score += piecesAtSecondStop>=2 ? -30000 : -150000;
        if (isCurrentFirstSafe)  score += piecesAtFirstStop>=2  ? -5000  : -20000;
        if (targetInDanger && !isHit && !formingBlock) score-=4000;
        if (leavingVulnerable) score-=5000;
        if (currentState==PieceState.Board && IsOwnColoredSafe(myId,currentPos) && sameColorAtCurrent>0
            && !isHit && !breakingChokePoint && !isCurrentFirstSafe && !isCurrentSecondSafe) score-=2000;

        if (isPastSecondStop && targetState!=PieceState.Home) score=(int)(score*0.5);

        return score;
    }

    static bool IsOwnColoredSafe(int playerId, int pos) =>
        BoardConstants.MyStops.TryGetValue(playerId, out var stops) && stops.Contains(pos);

    async Task BroadcastLobby() =>
        await Clients.All.SendAsync("LobbyList", new LobbyListMsg(rooms.GetLobbyList()));
}
