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
        if (room.Players[room.TurnSlot].ConnectionId != Context.ConnectionId) return;

        // RACE GUARD: If the player already has dice, they already rolled
        // (or got a bonus from their own roll). Let them make their own moves.
        if (room.DicePool.Count > 0) return;

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

        // CRITICAL FIX: After auto-play ends, the turn may have advanced to a bot
        // via EndTurn. If so, trigger bot processing. Without this, bot jams.
        if (room.Phase == GamePhase.Play)
            await CheckAndAutoPlay(room);
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
                    var (r, extra2, pp2, toast2) = rooms.RollDice(room);
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
        int hitCount = 0;
        if (isHit)
        {
            foreach (var p in players)
            {
                if (p.Id == myId || p.PartnerId == myId || !p.IsActive) continue;
                foreach (var pc in p.Pieces)
                    if (pc.State == PieceState.Board && pc.Pos == targetPos)
                        hitCount++;
            }
        }

        // Block
        int sameColorAtTarget = (targetState == PieceState.Board || targetState == PieceState.HomeStretch)
            ? players[myId].Pieces.Count(p => p != move.Piece && p.State == targetState && p.Pos == targetPos)
            : 0;
        bool formingBlock = sameColorAtTarget == 1;
        bool joiningBlock = sameColorAtTarget >= 1; // Joining existing block

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

        // Check if piece is AT the second stop (the border block position)
        bool pieceAtSecondStop = currentState == PieceState.Board && currentPos == mySecondStop;

        // Check if moving this piece from second stop will enter home stretch or home
        bool movingFromSecondStopIntoHome = pieceAtSecondStop && (targetState == PieceState.HomeStretch || targetState == PieceState.Home);

        int piecesAtSecondStop = players[myId].Pieces.Count(p => p.State == PieceState.Board && p.Pos == mySecondStop);
        int piecesAtFirstStop  = players[myId].Pieces.Count(p => p.State == PieceState.Board && p.Pos == myFirstStop);

        // Count pieces NOT at second stop and not past it (these are the ones that need attention)
        int piecesNeedingHelp = players[myId].Pieces.Count(p =>
        {
            if (p.State == PieceState.Home || p.State == PieceState.HomeStretch) return false;
            if (p.State == PieceState.Board)
            {
                int distFromStop = (p.Pos - mySecondStop + 52) % 52;
                if (distFromStop <= 3) return false; // At or past second stop
            }
            return true;
        });

        int opponentPiecesOut = 0;
        foreach (var p in players)
            if (p.Id != myId && p.PartnerId != myId && p.IsActive)
                opponentPiecesOut += p.Pieces.Count(pc => pc.State != PieceState.Yard);

        // Danger / chasing scan
        bool currentlyInDanger = false, targetInDanger = false, targetIsChasing = false;
        int minThreatDist = 7;
        int threatCountNearTarget = 0;
        int chaseCountFromTarget = 0;
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
                        if (dt > 0 && dt <= 6) { targetInDanger = true; threatCountNearTarget++; }
                        int da = (pc.Pos - targetPos + 52) % 52;
                        if (da > 0 && da <= 6) { targetIsChasing = true; chaseCountFromTarget++; }
                    }
            }

        // ── Scoring — exact mirror of Flutter ──

        // CRITICAL: Apply isPastSecondStop multiplier EARLY before strategic bonuses
        if (isPastSecondStop && targetState != PieceState.Home)
        {
            int distToHomeEntry = 3 - ((currentPos - mySecondStop + 52) % 52);
            if (distToHomeEntry <= 2) score = (int)(score * 0.3);
            else score = (int)(score * 0.5);
        }

        // Pieces behind second stop that CAN land on it = high priority
        if (isBehindSecondStop && !willPassSecondStop) {
            if (distToSecondStop == move.DieValue && targetState == PieceState.Board && !isHit)
                score += 250000;
        }

        // HUGE penalty for passing over second stop without landing
        if (willPassSecondStop) score -= 5000000;

        // Forming the 2-piece block on second stop = TOP priority
        if (formingChokePoint) score += 2000000;

        // Landing ON second stop (even without forming block yet)
        if (isTargetSecondSafe && targetState == PieceState.Board) {
            if (sameColorAtTarget == 0) score += 800000;
        }

        // Breaking second stop block = effectively disabled
        if (breakingChokePoint) score -= 2000000;
        if (isCurrentSecondSafe)
        {
            if (targetState != PieceState.Home)
            {
                score += piecesAtSecondStop >= 2 ? -1000000 : -2000000;
            }
            else
            {
                if (piecesNeedingHelp > 0) score -= 500000;
            }
        }

        // 🔥 ENHANCED: Prevent moving from second stop into home stretch/home
        if (movingFromSecondStopIntoHome)
            score -= 200000;

        // 🔥 ENHANCED: Strong penalty for moving ANY piece at second stop
        if (pieceAtSecondStop && targetState != PieceState.Home)
        {
            if (piecesNeedingHelp > 0)
                score -= 120000 + (piecesNeedingHelp * 10000);
        }

        // 🔥 ENHANCED: Boost for freeing pieces that need help when we have pieces at second stop
        if (piecesNeedingHelp > 0)
        {
            bool moveHelpsNeedy = !pieceAtSecondStop && !isPastSecondStop;
            if (moveHelpsNeedy)
            {
                score += 50000;
                if (currentlyInDanger) score += 30000;
            }
        }

        // ========== FIX 2: PRISONER RELEASE = CRITICAL WHEN 6 IN POOL ==========
        bool hasSixInPool = currentPool.Contains(6);
        
        // CRITICAL FIX: Prisoner -> Yard (first step: escape prison with 6)
        if (currentState == PieceState.Prison && targetState == PieceState.Yard)
        {
            score += 1000000; // INCREASED
            if (hasSixInPool) score += 500000;

            int piecesOnBoard = players[myId].Pieces.Count(p => p.State==PieceState.Board || p.State==PieceState.HomeStretch);
            int piecesInYard = players[myId].Pieces.Count(p => p.State == PieceState.Yard);

            // Rule A: only pieces at yard, 0 on board, has prisoners -> Prioritize Prisoners
            if (piecesOnBoard == 0 && piecesInYard == 1 && prisoners > 0)
            {
                score += 800000;
            }

            if (piecesOnBoard <= 2) score += 200000;
            int outDiff = opponentPiecesOut - piecesOut;
            if (outDiff > 0) score += 50000 * (outDiff + 1);
        }
        
        // Yard -> Board: high priority, but defers to prisoners based on specific rules
        if (currentState == PieceState.Yard && targetState == PieceState.Board)
        {
            int piecesOnBoard = players[myId].Pieces.Count(p => p.State==PieceState.Board || p.State==PieceState.HomeStretch);
            int piecesInYard = players[myId].Pieces.Count(p => p.State == PieceState.Yard);
            bool shouldPrioritizeYard = false;

            // Rule C: 2+ yard pieces, 0 on board -> Yard Release
            if (piecesOnBoard == 0 && piecesInYard >= 2)
            {
                shouldPrioritizeYard = true;
            }
            // Rule B: 1-2 board pieces, 1 yard piece -> Yard Release
            else if ((piecesOnBoard == 1 || piecesOnBoard == 2) && piecesInYard == 1)
            {
                shouldPrioritizeYard = true;
            }

            if (prisoners > 0 && !shouldPrioritizeYard)
            {
                score -= 500000; // STRONG defer to prisoners
            }
            else
            {
                score += 500000;
                if (hasSixInPool) score += 200000;

                if (shouldPrioritizeYard) score += 400000;

                // Task 7b & 7c logic refinement
                bool enemyNearHome = false;
                int myHomeStop = BoardConstants.MyStops[myId][0];
                foreach (var p in players)
                {
                    if (p.Id == myId || p.PartnerId == myId || !p.IsActive) continue;
                    foreach (var pc in p.Pieces)
                    {
                        if (pc.State == PieceState.Board)
                        {
                            int distToHome = (myHomeStop - pc.Pos + 52) % 52;
                            if (distToHome >= 0 && distToHome <= 4) enemyNearHome = true;
                        }
                    }
                }

                if (enemyNearHome && piecesInYard == 1 && hasSixInPool && currentPool.Count(d => d == 6) == 1)
                {
                    if (!shouldPrioritizeYard) score -= 400000;
                }

                // Task 6 Ambush/Opportunity from yard
                if (isHit) score += 600000;
            }
        }

        int otherPiecesHome = players[myId].Pieces.Count(p => p!=move.Piece && p.State==PieceState.Home);
        bool isFinishing = targetState==PieceState.Home && otherPiecesHome==3;
        if (isFinishing) { score+=150000; if(remainingDice.Contains(6)) score+=20000; }

        if (formingBlock && isTargetFirstSafe) score += 40000;

        if (isHit)
        {
            // 🔥 FIX 3: Scale score by number of opponents hit (multi-hit is stronger)
            score += 60000 + (hitCount * 30000);
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

        // 🔥 FIX 3: Bonus for forming/joining a block on opponent's colored squares
        if (joiningBlock && !IsOwnColoredSafe(myId, targetPos) && safeZones.Contains(targetPos))
        {
            bool isOpponentColored = false;
            foreach (var p in players)
                if (p.Id != myId && p.PartnerId != myId && p.IsActive)
                    if (IsOwnColoredSafe(p.Id, targetPos)) { isOpponentColored = true; break; }
            if (isOpponentColored) score += 70000;
            else score += 25000;
        }

        // 🔥 FIX 3: Strong penalty for moving to a position where MULTIPLE opponents threaten
        if (threatCountNearTarget >= 2 && !formingBlock && !isHit)
            score -= 30000 * threatCountNearTarget;
        else if (threatCountNearTarget >= 1 && !formingBlock && !isHit)
            score -= 8000;

        // 🔥 FIX 3: Bonus for tailing multiple opponents (chase potential)
        if (chaseCountFromTarget > 1)
            score += 30000;

        if (targetState == PieceState.Home)
        {
            score += 12000;
            int notHome = players[myId].Pieces.Count(p => p.State!=PieceState.Home);
            if (notHome > 1) score -= 5000;
            // 🔥 FIX 4: Gently prioritize/preserve home entry
            if (notHome <= 2) score += 15000;
            else if (notHome >= 3 && piecesNeedingHelp > 1) score -= 40000;
        }

        if (formingBlock && IsOwnColoredSafe(myId,targetPos) && !isTargetFirstSafe && !isTargetSecondSafe) score+=5000;
        if (IsOwnColoredSafe(myId,targetPos) && !formingBlock) score+=8000;
        if (formingBlock && !IsOwnColoredSafe(myId,targetPos)) score-=100000;

        if (currentlyInDanger && !targetInDanger) score += 6000+(7-minThreatDist)*1000;
        else if (currentlyInDanger && targetInDanger) score += 1000;

        if (targetState==PieceState.Board && safeZones.Contains(targetPos) && !formingBlock) score+=3000;
        if (targetIsChasing && !targetInDanger) score+=4000;

        // Task 6: Ambush - reward staying in hit range of an opponent if on a safe zone
        if (currentState == PieceState.Board && IsOwnColoredSafe(myId, currentPos))
        {
            bool isAmbushing = false;
            foreach (var p in players)
            {
                if (p.Id == myId || p.PartnerId == myId || !p.IsActive) continue;
                foreach (var pc in p.Pieces)
                {
                    if (pc.State == PieceState.Board)
                    {
                        int d = (pc.Pos - currentPos + 52) % 52;
                        if (d > 0 && d <= 6) isAmbushing = true;
                    }
                }
            }
            if (isAmbushing && !isHit && !isTargetSecondSafe && !isTargetFirstSafe)
                score -= 15000;
        }

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

        if (isCurrentFirstSafe)
        {
            if (piecesAtFirstStop >= 3)
            {
                score += 100000; // Increased bonus for moving 3rd piece
            }
            else
            {
                score -= 1000000; // Strict maintain block
            }
        }
        if (targetInDanger && !isHit && !formingBlock) score-=4000 * (threatCountNearTarget + 1);
        if (leavingVulnerable) score-=5000;
        if (currentState==PieceState.Board && IsOwnColoredSafe(myId,currentPos) && sameColorAtCurrent>0
            && !isHit && !breakingChokePoint && !isCurrentFirstSafe && !isCurrentSecondSafe) score-=2000;

        return score;
    }

    static bool IsOwnColoredSafe(int playerId, int pos) =>
        BoardConstants.MyStops.TryGetValue(playerId, out var stops) && stops.Contains(pos);

    async Task BroadcastLobby() =>
        await Clients.All.SendAsync("LobbyList", new LobbyListMsg(rooms.GetLobbyList()));
}
