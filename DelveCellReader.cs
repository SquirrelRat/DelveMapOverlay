using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;

namespace DelveMapOverlay;

/// <summary>
/// Reads the native delve chart node data directly from game memory, bypassing the corrupted
/// ExileCore DelveElement/DelveCell wrappers (their method bodies are destroyed in this build).
///
/// Key discovery: the chart renders every explored node as a real UI Element whose address is
///   nativeCell + 0x10
/// and whose GetClientRect() returns the node's exact on-screen rect (tracks zoom/scroll).
/// The native cell struct (0x800 stride, base = elementAddr - 0x10) carries feature/biome
/// pointers, grid coords, depth, flags and neighbor links. See DelveOffsets for the layout.
///
/// Names are resolved by matching the pointer address against the loaded
/// gc.Files.DelveFeatures / gc.Files.DelveBiomes entries.
/// </summary>
public static class DelveCellReader
{
    // Remembers the last valid Mine Entrance state-object across reads, so "Completed"
    // detection doesn't drop out when the entrance scrolls/zooms out of the current
    // tree-walk (see the "Mark completed" block at the end of ReadCells).
    private static long _cachedDoneState = 0;

    public sealed class Cell
    {
        public long Address;
        public string Feature = "";
        public string FeatureId = "";
        public string Biome = "";
        public string Reward = "";
        public int Tier;

        // True when the cell is part of the explored "done line". Detected by anchoring on the
        // Mine Entrance cell (always completed): its state object at +0x6B0 is shared by every
        // completed cell. Empirically verified (4/4 match against known completed nodes).
        public bool Completed;
        public long StateObj;
        public long[] Neighbors = new long[4];

        // Whether each neighbor slot is a REAL delve connection. The neighbor pointer itself
        // encodes grid adjacency (a cell physically exists there), while the packed companion
        // field at slot+8 (0x570/0x588/0x5A0/0x5B8) is nonzero only for actual connection
        // lines. Verified against Obstruction wall cells (real links have comp!=0, fogged
        // Nothing neighbors have comp==0).
        public bool[] NeighborConnected = new bool[4];

        // On-screen rects of the connected neighbors (resolved after the walk), for
        // drawing hidden-passageway connection lines from Obstruction cells.
        public Vector4[] NeighborRects = new Vector4[4];

        // Feature names of the connected neighbors, aligned with NeighborRects.
        // Used to only draw wall connection lines to VALID (non-"Nothing") neighbors.
        public string[] NeighborFeatures = new string[4];

        // Exact on-screen rect of the node icon (tracks zoom/scroll).
        public Vector4 Rect;

        public bool IsNothing => string.IsNullOrEmpty(Feature) || Feature == "Nothing";

        public System.Numerics.Vector2 Center =>
            new System.Numerics.Vector2(Rect.X + Rect.Z / 2f, Rect.Y + Rect.W / 2f);

        public System.Numerics.Vector2 FrameTopLeft(float inset, float ox, float oy) =>
            new System.Numerics.Vector2(Rect.X + inset + ox, Rect.Y + inset + oy);

        public System.Numerics.Vector2 FrameBottomRight(float inset, float ox, float oy) =>
            new System.Numerics.Vector2(Rect.X + Rect.Z - inset + ox, Rect.Y + Rect.W - inset + oy);
    }

    public static List<Cell> ReadCells(GameController gc)
    {
        if (gc == null || !gc.InGame) return new List<Cell>();

        var window = gc.IngameState.IngameUi.DelveWindow;
        if (window == null || !window.IsVisible) return new List<Cell>();

        var featMap = new Dictionary<long, string>();
        var featIdMap = new Dictionary<long, string>();
        long featMin = long.MaxValue, featMax = 0;
        foreach (var featEntry in gc.Files.DelveFeatures.EntriesList)
        {
            var addr = (long)featEntry.Address;
            featMap[addr] = !string.IsNullOrEmpty(featEntry.Name) ? featEntry.Name : featEntry.Id;
            featIdMap[addr] = featEntry.Id ?? "";
            if (addr < featMin) featMin = addr;
            if (addr > featMax) featMax = addr;
        }

        var bioMap = new Dictionary<long, string>();
        long bioMin = long.MaxValue, bioMax = 0;
        foreach (var bioEntry in gc.Files.DelveBiomes.EntriesList)
        {
            var addr = (long)bioEntry.Address;
            bioMap[addr] = !string.IsNullOrEmpty(bioEntry.Name) ? bioEntry.Name : bioEntry.Id;
            if (addr < bioMin) bioMin = addr;
            if (addr > bioMax) bioMax = addr;
        }

        if (featMap.Count == 0 || bioMap.Count == 0)
            return new List<Cell>();

        // Tree walk: explored cells with exact screen rects.
        var result = new List<Cell>();
        var stack = new Stack<ExileCore.PoEMemory.Element>();
        stack.Push(window);
        int walked = 0;
        while (stack.Count > 0 && walked < DelveOffsets.TreeWalkCap)
        {
            var elem = stack.Pop();
            walked++;
            try
            {
                long elemAddr = elem.Address;
                long cell = elemAddr + DelveOffsets.ElementToCell;
                long featPtr = gc.Memory.Read<long>(cell + DelveOffsets.CellFeature);
                if (featPtr >= featMin && featPtr <= featMax)
                {
                    long bioPtr = gc.Memory.Read<long>(cell + DelveOffsets.CellBiome);
                    if (bioPtr >= bioMin && bioPtr <= bioMax)
                    {
                        string feat;
                        if (!featMap.TryGetValue(featPtr, out feat)) feat = "";
                        string featId;
                        if (!featIdMap.TryGetValue(featPtr, out featId)) featId = "";
                        string bio;
                        if (!bioMap.TryGetValue(bioPtr, out bio)) bio = "";

                        var r = elem.GetClientRect();
                        var stateObj = gc.Memory.Read<long>(cell + DelveOffsets.CellStateObj);

                        // 4 neighbor slots at 0x18 stride. Companion field at slot+8:
                        // nonzero = real connection line.
                        var neighbors = new long[4];
                        var connected = new bool[4];
                        for (int i = 0; i < 4; i++)
                        {
                            var slot = cell + DelveOffsets.CellNeighbor0 + i * DelveOffsets.CellNeighborStride;
                            neighbors[i] = gc.Memory.Read<long>(slot);
                            connected[i] = neighbors[i] != 0 &&
                                           gc.Memory.Read<long>(slot + DelveOffsets.CellNeighborCompanionDelta) != 0;
                        }

                        result.Add(new Cell
                        {
                            Address = cell,
                            Feature = feat,
                            FeatureId = featId,
                            Biome = bio,
                            Reward = NodeBackbone.RewardOfId(featId),
                            Tier = NodeBackbone.TierOfId(featId),
                            StateObj = stateObj,
                            Neighbors = neighbors,
                            NeighborConnected = connected,
                            Rect = new Vector4(r.X, r.Y, r.Width, r.Height),
                        });
                    }
                }

                var ch = elem.Children;
                for (int i = 0; i < ch.Count; i++)
                    stack.Push(ch[i]);
            }
            catch
            {
                // skip unreadable elements
            }
        }

        // Resolve neighbor rects: neighbor pointers point to the neighbor's ELEMENT
        // (cell + 0x10), so subtract the element offset to get the native cell address.
        var byAddr = new Dictionary<long, Cell>();
        foreach (var c in result) byAddr[c.Address] = c;
        foreach (var c in result)
        {
            for (int i = 0; i < c.Neighbors.Length; i++)
            {
                if (c.Neighbors[i] == 0) continue;
                var nbCellAddr = c.Neighbors[i] + DelveOffsets.ElementToCell;
                if (byAddr.TryGetValue(nbCellAddr, out var nb))
                {
                    c.NeighborRects[i] = nb.Rect;
                    c.NeighborFeatures[i] = nb.Feature ?? "";
                }
            }
        }

        // Mark completed: anchor on the Mine Entrance (always a done node) and treat every
        // cell sharing its +0x6B0 state object as part of the explored "done line".
        long doneState = 0;
        foreach (var c in result)
            if (string.Equals(c.Feature, DelveOffsets.MineEntranceFeature, StringComparison.OrdinalIgnoreCase))
            {
                doneState = c.StateObj;
                break;
            }

        if (doneState != 0)
        {
            // Found the entrance this time: refresh the cache.
            _cachedDoneState = doneState;
        }
        else
        {
            // Entrance isn't in the current read (scrolled/zoomed out of view):
            // fall back to the last known value instead of leaving everything unmarked.
            doneState = _cachedDoneState;
        }

        if (doneState != 0)
            foreach (var c in result)
                c.Completed = c.StateObj == doneState;

        return result;

    }
}