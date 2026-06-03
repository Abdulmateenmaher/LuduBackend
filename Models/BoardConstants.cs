namespace LuduBackend.Models;

// Board constants mirroring board_constants.dart
public static class BoardConstants
{
    public static readonly Dictionary<int, int> StartCells = new() { {0,0},{1,13},{2,26},{3,39} };
    public static readonly Dictionary<int, int> WhiteSquares = new() { {0,50},{1,11},{2,24},{3,37} };
    public static readonly List<int> SafeZones = [0, 8, 13, 21, 26, 34, 39, 47];
    public static readonly Dictionary<int, List<int>> MyStops = new()
    {
        {0, [0, 47]}, {1, [13, 8]}, {2, [26, 21]}, {3, [39, 34]}
    };
    public static readonly Dictionary<int, string> ColorNames = new()
    {
        {0, "Red"}, {1, "Green"}, {2, "Yellow"}, {3, "Blue"}
    };
}
