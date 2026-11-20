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
    // Remembers the last valid Mine Entrance state-object across reads (single-threaded
    // via Tick's _readTask gate), so "Completed" detection doesn't drop out when the
    // entrance scrolls/zooms out of the current tree-walk.
    private static long _lastEntranceDoneState = 0;

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

    // Element -> native cell offset currently in use. Starts at the known value and is
    // re-detected at runtime when it stops matching (it moves with game updates).
    private static long _elementToCell = DelveOffsets.ElementToCell;
    private static string _offsetSource = "default";
    private static string _lastScanInfo = "Last scan: none";
    private static DateTime _lastScan = DateTime.MinValue;
    private static volatile bool _forceRedetect;

    /// <summary>
    /// Element -> cell offset of the last read. Neighbor pointers point to the neighbor's
    /// element, so add this to them to get the neighbor's cell address.
    /// </summary>
    public static long ElementToCell => _elementToCell;

    /// <summary>Human-readable read status for the Debug tab.</summary>
    public static string Diagnostics { get; private set; } = "No read yet (open the Delve chart).";

    /// <summary>Requests a fresh offset scan on the next read.</summary>
    public static void ForceRedetect() => _forceRedetect = true;

    private static string FormatOffset(long value) =>
        value < 0 ? $"-0x{-value:X}" : $"0x{value:X}";

    private static bool IsCellAt(GameController gc, long elemAddr, long elementToCell,
        Dictionary<long, string> featMap, Dictionary<long, string> bioMap)
    {
        long cell = elemAddr + elementToCell;
        if (!featMap.ContainsKey(gc.Memory.Read<long>(cell + DelveOffsets.CellFeature))) return false;
        return bioMap.ContainsKey(gc.Memory.Read<long>(cell + DelveOffsets.CellBiome));
    }

    /// <summary>
    /// Returns the element -> cell offset to use for this read. Keeps the current offset as
    /// long as it finds cells; otherwise scans a window of candidate offsets once (throttled)
    /// and picks the one where most elements carry a valid feature AND biome pointer.
    /// </summary>
    private static long ResolveElementToCell(GameController gc, List<ExileCore.PoEMemory.Element> elements,
        Dictionary<long, string> featMap, Dictionary<long, string> bioMap)
    {
        if (!_forceRedetect)
        {
            int found = 0;
            foreach (var elem in elements)
            {
                try
                {
                    if (IsCellAt(gc, elem.Address, _elementToCell, featMap, bioMap) && ++found >= 3)
                        return _elementToCell;
                }
                catch
                {
                    // skip unreadable elements
                }
            }

            if ((DateTime.Now - _lastScan).TotalSeconds < 3)
                return _elementToCell;
        }

        _forceRedetect = false;
        _lastScan = DateTime.Now;

        // Read one block per element that covers the feature and biome pointers for every
        // candidate offset, then test the candidates in-process instead of per memory read.
        long readStart = DelveOffsets.ScanMin + DelveOffsets.CellFeature;
        int readLength = (int)(DelveOffsets.ScanMax + DelveOffsets.CellBiome + 8 - readStart);
        int candidateCount = (int)((DelveOffsets.ScanMax - DelveOffsets.ScanMin) / 8) + 1;
        var hits = new int[candidateCount];
        int scanned = 0;

        foreach (var elem in elements)
        {
            if (scanned >= DelveOffsets.ScanElementCap) break;
            byte[] buffer;
            try
            {
                buffer = gc.Memory.ReadBytes(elem.Address + readStart, readLength);
            }
            catch
            {
                continue;
            }
            if (buffer == null || buffer.Length < readLength) continue;
            scanned++;

            for (int k = 0; k < candidateCount; k++)
            {
                long offset = DelveOffsets.ScanMin + k * 8L;
                int featIndex = (int)(offset + DelveOffsets.CellFeature - readStart);
                int bioIndex = (int)(offset + DelveOffsets.CellBiome - readStart);
                if (featMap.ContainsKey(BitConverter.ToInt64(buffer, featIndex)) &&
                    bioMap.ContainsKey(BitConverter.ToInt64(buffer, bioIndex)))
                {
                    hits[k]++;
                }
            }
        }

        int best = -1;
        for (int k = 0; k < candidateCount; k++)
            if (best < 0 || hits[k] > hits[best]) best = k;

        if (best >= 0 && hits[best] >= 3)
        {
            long detected = DelveOffsets.ScanMin + best * 8L;
            _offsetSource = detected == DelveOffsets.ElementToCell ? "default, verified" : "auto-detected";
            _lastScanInfo = $"Last scan: {FormatOffset(detected)} matched {hits[best]} of {scanned} elements";
            _elementToCell = detected;
        }
        else
        {
            _offsetSource = "no match";
            _lastScanInfo = $"Last scan: no offset in {FormatOffset(DelveOffsets.ScanMin)}..{FormatOffset(DelveOffsets.ScanMax)} matched ({scanned} elements)";
        }

        return _elementToCell;
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

        // Tree walk: collect every element of the chart first, then resolve which ones are
        // cells. The element -> native cell offset moves with game updates, so it is
        // verified against the walked elements and re-detected when it no longer matches.
        var elements = new List<ExileCore.PoEMemory.Element>();
        var stack = new Stack<ExileCore.PoEMemory.Element>();
        stack.Push(window);
        while (stack.Count > 0 && elements.Count < DelveOffsets.TreeWalkCap)
        {
            var elem = stack.Pop();
            elements.Add(elem);
            try
            {
                var ch = elem.Children;
                for (int i = 0; i < ch.Count; i++)
                    stack.Push(ch[i]);
            }
            catch
            {
                // skip unreadable elements
            }
        }

        long elementToCell = ResolveElementToCell(gc, elements, featMap, bioMap);
        Diagnostics = $"Features: {featMap.Count}  Biomes: {bioMap.Count}  Elements walked: {elements.Count}\n" +
                      $"ElementToCell: {FormatOffset(elementToCell)} ({_offsetSource})\n" +
                      _lastScanInfo;

        var result = new List<Cell>();
        foreach (var elem in elements)
        {
            try
            {
                long elemAddr = elem.Address;
                long cell = elemAddr + elementToCell;
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
                var nbCellAddr = c.Neighbors[i] + elementToCell;
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
            _lastEntranceDoneState = doneState;
        }
        else
        {
            // Entrance isn't in the current read (scrolled/zoomed out of view):
            // fall back to the last known value instead of leaving everything unmarked.
            doneState = _lastEntranceDoneState;
        }

        if (doneState != 0)
            foreach (var c in result)
                c.Completed = c.StateObj == doneState;

        return result;

    }
}