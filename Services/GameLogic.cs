using LuduBackend.Models;

namespace LuduBackend.Services;

public class MoveDestination
{
    public bool Valid { get; set; }
    public PieceState TargetState { get; set; }
    public int TargetPos { get; set; }
}

public class AiMove
{
    public Piece Piece { get; set; } = null!;
    public MoveDestination Target { get; set; } = null!;
    public List<int> DieIndices { get; set; } = [];
    public int DieValue { get; set; }
}

/// Exact port of game_logic.dart
public static class GameLogic
{
    public static List<int> EffectiveSafeZones(GameSettings s) =>
        s.SafeZonesEnabled ? BoardConstants.SafeZones : [];

    public static bool IsCellBlocked(int cellIdx, int movingColor, List<Player> players)
    {
        var counts = new Dictionary<int, int> { {0,0},{1,0},{2,0},{3,0} };
        foreach (var p in players)
            foreach (var pc in p.Pieces)
                if (pc.State == PieceState.Board && pc.Pos == cellIdx)
                    counts[p.Id]++;
        int partnerId = (movingColor + 2) % 4;
        for (int i = 0; i < 4; i++)
            if (i != movingColor && i != partnerId && counts.GetValueOrDefault(i) >= 2)
                return true;
        return false;
    }

    public static bool HasOpponent(int pos, int myColor, List<Player> players)
    {
        int partnerId = (myColor + 2) % 4;
        foreach (var p in players)
            if (p.Id != myColor && p.Id != partnerId)
                if (p.Pieces.Any(pc => pc.State == PieceState.Board && pc.Pos == pos))
                    return true;
        return false;
    }

    static bool HasSameColorPiece(int pos, int myColor, List<Player> players) =>
        players.First(p => p.Id == myColor).Pieces.Any(pc => pc.State == PieceState.Board && pc.Pos == pos);

    /// Exact port of calculateDestSimple
    public static MoveDestination? CalcDestSimple(Player player, Piece piece, int moveVal, List<Player> players, GameSettings settings, List<int> pool)
    {
        var safeZones = EffectiveSafeZones(settings);

        if (piece.State == PieceState.Yard)
        {
            if (moveVal == 6 && pool.Count > 0)
                return new() { Valid=true, TargetState=PieceState.Board, TargetPos=BoardConstants.StartCells[player.Id] };
            return null;
        }
        if (piece.State == PieceState.Prison)
        {
            if (!settings.PrisonRule) return null;
            if (moveVal == 6 && pool.Count > 0)
                return new() { Valid=true, TargetState=PieceState.Yard, TargetPos=-1 };
            return null;
        }
        if (piece.State == PieceState.Home) return null;

        if (piece.State == PieceState.HomeStretch)
        {
            int remaining = 5 - piece.Pos;
            if (moveVal == remaining) return new() { Valid=true, TargetState=PieceState.Home, TargetPos=999 };
            if (moveVal < remaining) return new() { Valid=true, TargetState=PieceState.HomeStretch, TargetPos=piece.Pos+moveVal };
            return null;
        }

        if (piece.State == PieceState.Board)
        {
            int curr = piece.Pos;
            for (int step = 1; step <= moveVal; step++)
            {
                if (curr == BoardConstants.WhiteSquares[player.Id])
                {
                    bool canEnter = !settings.KillToEnter || player.HasKilled;
                    if (!canEnter) return null;
                    int remaining = moveVal - step;
                    if (remaining == 5) return new() { Valid=true, TargetState=PieceState.Home, TargetPos=999 };
                    if (remaining < 5) return new() { Valid=true, TargetState=PieceState.HomeStretch, TargetPos=remaining };
                    return null;
                }
                curr = (curr + 1) % 52;

                if (safeZones.Contains(curr))
                {
                    int? owner = null;
                    foreach (var kv in BoardConstants.MyStops)
                        if (kv.Value.Contains(curr)) { owner = kv.Key; break; }
                    int partnerId = (player.Id + 2) % 4;
                    if (owner.HasValue && owner != player.Id && owner != partnerId)
                    {
                        int ownPieces = players[owner.Value].Pieces.Count(pc => pc.State == PieceState.Board && pc.Pos == curr);
                        if (ownPieces >= 2) return null;
                    }
                }
            }
            return new() { Valid=true, TargetState=PieceState.Board, TargetPos=curr };
        }
        return null;
    }

    public static bool HasAnyPossibleMove(Player player, List<Player> allPlayers, GameSettings settings)
    {
        if (player.Finished && !player.IsHelper && settings.TeamPlay)
        {
            return !allPlayers[player.PartnerId].Finished;
        }

        int ctrlId = (player.Finished && player.IsHelper && settings.TeamPlay)
            ? player.PartnerId
            : player.Id;
        var pCtrl = allPlayers[ctrlId];

        if (pCtrl.Finished) return false;

        bool allHome = pCtrl.Pieces.All(pc => pc.State == PieceState.Home);
        if (allHome) return player.Id == ctrlId;

        for (int d = 1; d <= 6; d++)
        {
            foreach (var pc in pCtrl.Pieces)
            {
                if (pc.State == PieceState.Home || pc.HasKilledThisTurn) continue;
                var dest = CalculateDestination(pCtrl, pc, d, allPlayers, [d], 0, settings);
                if (dest != null) return true;
            }
        }
        return false;
    }

    /// Exact port of calculateDestination
    public static MoveDestination? CalculateDestination(Player player, Piece piece, int moveVal, List<Player> players,
        List<int> pool, int dieIndex, GameSettings settings)
    {
        var dest = CalcDestSimple(player, piece, moveVal, players, settings, pool);
        if (dest == null) return null;
        var safeZones = EffectiveSafeZones(settings);

        // Block hitting opponent if another move exists
        if (dest.TargetState == PieceState.Board && !safeZones.Contains(dest.TargetPos) &&
            HasOpponent(dest.TargetPos, player.Id, players))
        {
            if (pool.Count > 1 && dieIndex != -1)
            {
                var rem = pool.ToList(); rem.RemoveAt(dieIndex);
                bool hasOther = false;
                foreach (var rVal in rem)
                    foreach (var p in player.Pieces)
                    {
                        if (p.Id == piece.Id || p.State == PieceState.Home || p.HasKilledThisTurn) continue;
                        if (CalcDestSimple(player, p, rVal, players, settings, rem) != null)
                        { hasOther = true; goto doneOuter1; }
                    }
                doneOuter1:
                if (!hasOther) return null;
            }
        }

        // Avoid stacking on own white square if possible
        if (dest.TargetState == PieceState.Board &&
            dest.TargetPos == BoardConstants.WhiteSquares[player.Id] &&
            HasSameColorPiece(dest.TargetPos, player.Id, players))
        {
            if (pool.Count > 1 && dieIndex != -1)
            {
                var rem = pool.ToList(); rem.RemoveAt(dieIndex);
                bool hasOther = false;
                foreach (var rVal in rem)
                    foreach (var p in player.Pieces)
                    {
                        if (p.Id == piece.Id || p.State == PieceState.Home || p.HasKilledThisTurn) continue;
                        if (CalcDestSimple(player, p, rVal, players, settings, rem) != null)
                        { hasOther = true; goto doneOuter2; }
                    }
                doneOuter2:
                if (hasOther) return null;
            }
        }
        return dest;
    }

    /// Exact port of getAllValidMoves
    public static List<AiMove> GetAllValidMoves(Player player, List<int> pool, List<Player> players, GameSettings settings)
    {
        var moves = new List<AiMove>();
        // Single die moves
        for (int i = 0; i < pool.Count; i++)
            foreach (var pc in player.Pieces)
            {
                if (pc.State == PieceState.Home || pc.HasKilledThisTurn) continue;
                var dest = CalculateDestination(player, pc, pool[i], players, pool, i, settings);
                if (dest != null) moves.Add(new() { Piece=pc, Target=dest, DieIndices=[i], DieValue=pool[i] });
            }

        // 🔥 ENHANCED: Combined moves ALWAYS generated alongside singles
        var combinedMoves = new List<AiMove>();
        if (pool.Count >= 2)
            for (int i = 0; i < pool.Count; i++)
                for (int j = i+1; j < pool.Count; j++)
                {
                    int sum = pool[i]+pool[j];
                    foreach (var pc in player.Pieces)
                    {
                        if (pc.State == PieceState.Home || pc.HasKilledThisTurn) continue;
                        var dest = CalculateDestination(player, pc, sum, players, [], -1, settings);
                        if (dest != null) combinedMoves.Add(new() { Piece=pc, Target=dest, DieIndices=[i,j], DieValue=sum });
                    }
                }
        // If no single moves, use combined; else include both
        if (moves.Count == 0) return combinedMoves;
        else { moves.AddRange(combinedMoves); return moves; }
    }
}