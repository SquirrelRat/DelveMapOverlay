using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.Shared.Enums;

namespace DelveMapOverlay;

public class DelveMapOverlayPlugin : BaseSettingsPlugin<DelveMapOverlaySettings>
{
    private readonly object _sync = new();
    private List<DelveCellReader.Cell> _cells = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private Task _readTask;
    private bool _chartOpen;
    private string _selectedReward;
    private string _searchText = "";
    private float _cachedFontScale = -1f;

    // Text measurement cache (keyed by string). MeasureText is comparatively expensive,
    // and node names/labels are stable per snapshot. Cleared when FontScale changes.
    private readonly Dictionary<string, Vector2> _textSizeCache = new();

    // Topology cache: BFS distances/parents, layout edges and stats rows depend only
    // on the walked mine data, not on screen geometry, so they are rebuilt when the
    // walked set changes (fingerprint) or every 500 ms (filter/weight edits), while
    // rects/centers keep updating every frame for smooth panning.
    private long _topoFingerprint;
    private long _topoBuiltAtMs;
    private readonly Dictionary<long, int> _distCache = new();
    private readonly Dictionary<long, long> _prevCache = new();
    private readonly List<(long A, long B)> _layoutEdges = new();
    private readonly Dictionary<long, DelveCellReader.Cell> _frameByAddr = new();
    private readonly List<StatsRewardRow> _statsRewardRows = new();
    private readonly List<StatsBiomeRow> _statsBiomeRows = new();

    private sealed class StatsRewardRow { public string Text; public string BaseReward; }
    private sealed class StatsBiomeRow { public string Text; public string Pool; }

    public override bool Initialise()
    {
        NodeBackbone.Load(System.IO.Path.GetDirectoryName(GetType().Assembly.Location));
        try { NodeBackbone.BuildRewardData(GameController.Files.DelveFeatures.EntriesList); }
        catch (Exception ex) { LogError($"DelveMapOverlay reward data build failed: {ex.Message}"); }
        EnsureFiltersSeeded();
        LogMessage($"DelveMapOverlay loaded. Backbone: {NodeBackbone.Nodes.Count} nodes ({(NodeBackbone.Loaded ? NodeBackbone.Path : "MISSING")}).", 2f);
        return true;
    }

    private void EnsureFiltersSeeded()
    {
        var rewards = NodeBackbone.AllRewards.Count > 0 ? NodeBackbone.AllRewards : NodeBackbone.DefaultRewards;
        foreach (var reward in rewards)
        {
            if (!Settings.RewardFilters.ContainsKey(reward))
                Settings.RewardFilters[reward] = NewSeededFilter(reward);
        }
    }

    /// <summary>Fresh reward filter with chase-default enable/weight and default tiers.</summary>
    private static NodeFilter NewSeededFilter(string reward)
    {
        var nf = new NodeFilter();
        ApplyDefaults(nf, reward);
        return nf;
    }

    /// <summary>Restores a reward filter's default enable/weight/tier state.</summary>
    private static void ApplyDefaults(NodeFilter nf, string reward)
    {
        nf.Enabled = NodeBackbone.IsChaseReward(reward);
        nf.Weight = NodeBackbone.DefaultWeight(reward);
        nf.CustomColor = null;
        nf.SelectedTiers.Clear();
        nf.SelectedTiers.AddRange(NodeBackbone.AllTiersOf(reward));
        nf.TierWeights = NodeBackbone.DefaultTierWeights(reward);
        nf.PathEnabled = NodeBackbone.IsChaseReward(reward);
    }

    public override Job Tick()
    {
        var window = GameController.IngameState.IngameUi.DelveWindow;
        _chartOpen = window != null && window.IsVisible;

        if (Settings.Enable && _chartOpen &&
            (DateTime.Now - _lastRefresh).TotalMilliseconds >= Settings.RefreshMs &&
            (_readTask == null || _readTask.IsCompleted))
        {
            _lastRefresh = DateTime.Now;
            _readTask = Task.Run(() =>
            {
                try
                {
                    var read = DelveCellReader.ReadCells(GameController);
                    lock (_sync) { _cells = read; }
                }
                catch (Exception ex)
                {
                    LogError($"DelveMapOverlay read error: {ex.Message}");
                }
            });
        }

        return null;
    }

    public override void Render()
    {
        if (!Settings.Enable || !_chartOpen) return;

        List<DelveCellReader.Cell> cells;
        lock (_sync) { cells = _cells; }
        if (cells.Count == 0) return;

        var window = GameController.IngameState.IngameUi.DelveWindow;

        // Clip to the DelveWindow's own client rect: nothing may ever render outside it,
        // even when zoomed in (the inner canvas is larger than the window and scrolls).
        var map = window.GetClientRect();
        if (map.Width <= 0 || map.Height <= 0)
            map = window.Children[0].Children[0].GetClientRect();

        var mapMin = new Vector2(map.X, map.Y);
        var mapMax = new Vector2(map.X + map.Width, map.Y + map.Height);

        if (Settings.FontScale != _cachedFontScale)
        {
            _textSizeCache.Clear();
            _cachedFontScale = Settings.FontScale;
        }

        // Live lookup for this frame's geometry plus fingerprint-gated topology.
        _frameByAddr.Clear();
        foreach (var c in cells) _frameByAddr[c.Address] = c;
        EnsureTopology(cells);

        using (Graphics.BeginRectClip(new SharpDX.RectangleF(map.X, map.Y, map.Width, map.Height)))
        using (Graphics.SetTextScale(Settings.FontScale))
        {
            foreach (var c in cells)
            {
                // Completed ("done line") cells: draw a dim outline so the explored path
                // reads clearly, then skip the normal filter/label rendering.
                if (c.Completed && Settings.HideCompleted.Value)
                {
                    var cFrame = c.FrameTopLeft(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
                    var cFrameEnd = c.FrameBottomRight(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
                    if (cFrameEnd.X >= mapMin.X && cFrame.X <= mapMax.X &&
                        cFrameEnd.Y >= mapMin.Y && cFrame.Y <= mapMax.Y)
                    {
                        Graphics.DrawFrame(cFrame, cFrameEnd, new SharpDX.Color(0.35f, 0.35f, 0.35f, 0.5f), 2);
                    }
                    continue;
                }

                NodeFilter rf = null;
                Settings.RewardFilters.TryGetValue(c.Reward ?? "", out rf);
                if (rf != null && !rf.Enabled) continue;

                var isEmpty = c.IsNothing;
                if (isEmpty && !Settings.ShowEmpty.Value) continue;

                // Tier filter: rewards with real tiering (Azurite/Chambers) only show
                // when at least one tier is selected AND this cell's tier is in it.
                // Unchecking every tier hides the reward entirely.
                if (c.Tier > 0 && rf != null &&
                    (!rf.SelectedTiers.Contains(c.Tier)))
                    continue;

                // Per-tier weight overrides the reward weight when the filter defines one
                // for this tier (e.g. Azurite T1 low / T2 4 / T3 8).
                var weight = rf?.Weight ?? 0f;
                if (c.Tier > 0 && rf != null && rf.TierWeights.TryGetValue(c.Tier, out var tw))
                    weight = tw;

                var frame = c.FrameTopLeft(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
                var frameEnd = c.FrameBottomRight(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);

                // Cull frames fully off the map surface.
                if (frameEnd.X < mapMin.X || frame.X > mapMax.X ||
                    frameEnd.Y < mapMin.Y || frame.Y > mapMax.Y)
                    continue;

                var color = ApplyWeightWithOpacity(NodeBaseColor(c.Reward, rf), weight, Settings.WeightOpacityStrength);

                Graphics.DrawFrame(frame, frameEnd, color, 2);

                // Awareness effect: high-weight rewards get a travelling white snake
                // comet around the frame.
                if (weight >= Settings.SnakeThreshold && Settings.SnakeSpeed > 0f)
                {
                    DrawSnakeEffect(frame, frameEnd,
                        Settings.SnakeSpeed, Settings.SnakeThickness, Settings.SnakeOpacity);
                }

                // Hidden passageways: for Obstruction (fractured wall) cells, draw gold DOTTED
                // paths from the wall center to each VALID neighbor's frame-edge port (real
                // nodes, not fogged "Nothing" cells).
                if (Settings.ShowHiddenPaths.Value &&
                    c.FeatureId != null &&
                    c.FeatureId.StartsWith("Obstruction", StringComparison.OrdinalIgnoreCase))
                {
                    var pathColor = Settings.HiddenPathColor;
                    var pathColorSd = new SharpDX.Color(pathColor.X, pathColor.Y, pathColor.Z, pathColor.W);
                    var pathThickness = Settings.HiddenPathThickness;
                    var dashLen = Settings.HiddenPathDash;
                    var gapLen = Settings.HiddenPathGap;
                    var wallCenter = new Vector2(
                        (frame.X + frameEnd.X) / 2f,
                        (frame.Y + frameEnd.Y) / 2f);

                    for (int i = 0; i < c.NeighborRects.Length; i++)
                    {
                        var nr = c.NeighborRects[i];
                        if (nr.Z <= 0f || nr.W <= 0f) continue;

                        // Skip fogged/unexplored neighbors - the wall only links to real nodes.
                        var nfName = c.NeighborFeatures[i];
                        if (string.IsNullOrEmpty(nfName) || nfName == "Nothing") continue;
                        var nbCenter = new Vector2(nr.X + nr.Z / 2f, nr.Y + nr.W / 2f);
                        var dx = nbCenter.X - wallCenter.X;
                        var dy = nbCenter.Y - wallCenter.Y;

                        // Port on the wall frame edge in the neighbor's dominant direction.
                        Vector2 port;
                        if (Math.Abs(dx) >= Math.Abs(dy))
                            port = new Vector2(dx >= 0f ? frameEnd.X : frame.X, wallCenter.Y);
                        else
                            port = new Vector2(wallCenter.X, dy >= 0f ? frameEnd.Y : frame.Y);

                        // Draw the center->port segment as dashes.
                        var seg = port - wallCenter;
                        var len = seg.Length();
                        if (len <= 0.001f) continue;
                        var dir = seg / len;
                        var d = 0f;
                        while (d < len)
                        {
                            var s = Math.Min(d + dashLen, len);
                            Graphics.DrawLine(wallCenter + dir * d, wallCenter + dir * s, pathThickness, pathColorSd);
                            d = s + gapLen;
                        }
                    }
                }

                if (Settings.DrawNames && !c.IsNothing)
                {
                    var labelPos = new Vector2(
                        (frame.X + frameEnd.X) / 2f,
                        frameEnd.Y + 2f);

                    // Weight affects text + background opacity too, matching the frames.
                    var fade = OpacityForWeight(weight, Settings.WeightOpacityStrength);

                    // Skip the labels entirely if they would extend outside the map rect,
                    // rather than letting the clip cut them off.
                    var size = MeasureTextCached(c.Feature);
                    var halfW = size.X / 2f;
                    var lineH = size.Y;
                    var rewardLine = c.Reward.Length > 0;
                    var totalH = lineH + (rewardLine ? lineH + 2f : 0f);
                    if (labelPos.X - halfW < mapMin.X || labelPos.X + halfW > mapMax.X ||
                        labelPos.Y < mapMin.Y || labelPos.Y + totalH > mapMax.Y)
                        continue;

                    Graphics.DrawTextWithBackground(c.Feature, labelPos,
                        new SharpDX.Color(1f, 1f, 1f, fade), FontAlign.Center,
                        new SharpDX.Color(0f, 0f, 0f, 0.75f * fade));

                    if (rewardLine)
                    {
                        // For the 6 exclusive-fossil nodes, show the specific fossil name
                        // (e.g. "Hollow", "Faceted") instead of the generic "Fossils".
                        var rewardText = c.Reward;
                        if (c.Reward == "Fossils")
                        {
                            var exFossil = NodeBackbone.ExclusiveFossilOf(c.Feature);
                            if (exFossil != null) rewardText = exFossil;
                        }
                        Graphics.DrawTextWithBackground(rewardText,
                            labelPos + new Vector2(0f, lineH + 2f),
                            new SharpDX.Color(1f, 0.84f, 0f, fade), FontAlign.Center,
                            new SharpDX.Color(0f, 0f, 0f, 0.75f * fade));
                    }
                }

                // Tier marker drawn inside the tile's top-left corner (e.g. "T1").
                if (c.Tier > 0)
                {
                    var tierFade = OpacityForWeight(weight, Settings.WeightOpacityStrength);
                    Graphics.DrawTextWithBackground($"T{c.Tier}",
                        new Vector2(frame.X + 2f, frame.Y + 2f),
                        new SharpDX.Color(1f, 0.84f, 0f, tierFade), FontAlign.Left,
                        new SharpDX.Color(0f, 0f, 0f, 0.75f * tierFade));
                }
            }

            if (Settings.ShowLayout.Value)
                DrawMineLayout(cells, mapMin, mapMax);
            if (Settings.ShowPaths.Value)
                DrawRewardPaths(cells, mapMin, mapMax);
        }

        if (Settings.ShowStatsPanel.Value)
            DrawStatsPanel(cells, map);
    }

    /// <summary>Port where the ray from inside point C along direction D exits a rect.</summary>
    private static Vector2 RectEdgePort(Vector2 c, float left, float top, float right, float bottom, Vector2 d)    {
        float tx = float.MaxValue, ty = float.MaxValue;
        if (d.X > 1e-6f) tx = (right - c.X) / d.X;
        else if (d.X < -1e-6f) tx = (left - c.X) / d.X;
        if (d.Y > 1e-6f) ty = (bottom - c.Y) / d.Y;
        else if (d.Y < -1e-6f) ty = (top - c.Y) / d.Y;
        var t = Math.Min(tx, ty);
        if (!float.IsFinite(t) || t < 0f) return c;
        return c + d * t;
    }

    /// <summary>
    /// Whether a cell draws its feature/reward labels in the render loop. Paths use
    /// this to reserve the label zones so hop counts never print over node text.
    /// </summary>
    private bool NodeLabelsDrawn(DelveCellReader.Cell c)
    {
        if (!Settings.DrawNames.Value || c.IsNothing) return false;
        if (c.Completed && Settings.HideCompleted.Value) return false;
        NodeFilter rf = null;
        Settings.RewardFilters.TryGetValue(c.Reward ?? "", out rf);
        if (rf != null && !rf.Enabled) return false;
        if (c.IsNothing && !Settings.ShowEmpty.Value) return false;
        if (c.Tier > 0 && rf != null && !rf.SelectedTiers.Contains(c.Tier)) return false;
        return true;
    }

    private static bool RectsOverlap(Vector4 a, Vector4 b, float pad)
    {
        return a.X < b.X + b.Z + pad && a.X + a.Z + pad > b.X &&
               a.Y < b.Y + b.W + pad && a.Y + a.W + pad > b.Y;
    }
    /// <summary>
    /// Whether a cell draws a frame in the render loop (dim done-line outline or a
    /// normal frame). Path lines dock at the frame edge of these cells and pass
    /// through the centers of the rest (fogged corridor tiles with no visible box).
    /// </summary>
    private bool DrawsPathFrame(DelveCellReader.Cell c)
    {
        if (c.Completed && Settings.HideCompleted.Value) return true;
        NodeFilter rf = null;
        Settings.RewardFilters.TryGetValue(c.Reward ?? "", out rf);
        if (rf != null && !rf.Enabled) return false;
        if (c.IsNothing && !Settings.ShowEmpty.Value) return false;
        if (c.Tier > 0 && rf != null && !rf.SelectedTiers.Contains(c.Tier)) return false;
        return true;
    }

    /// <summary>
    /// Rebuilds the topology cache when the walked mine data changed (address set or
    /// completion flags) or 500 ms elapsed (filter/weight edits). Screen geometry is
    /// never cached: rects keep flowing every frame so panning stays live.
    /// </summary>
    private void EnsureTopology(List<DelveCellReader.Cell> cells)
    {
        long fp = cells.Count;
        foreach (var c in cells)
        {
            fp = fp * 31 + c.Address;
            if (c.Completed) fp += 7919;
        }
        long now = System.Environment.TickCount64;
        if (fp == _topoFingerprint && now - _topoBuiltAtMs < 500) return;
        _topoFingerprint = fp;
        _topoBuiltAtMs = now;

        var byAddr = _frameByAddr;
        _distCache.Clear();
        _prevCache.Clear();

        // Frontier = completed cells with a real edge to an uncompleted neighbor.
        var frontier = new List<DelveCellReader.Cell>();
        foreach (var c in cells)
        {
            if (!c.Completed) continue;
            for (int i = 0; i < c.Neighbors.Length; i++)
            {
                if (!c.NeighborConnected[i]) continue;
                var nbAddr = c.Neighbors[i] + DelveCellReader.ElementToCell;
                if (byAddr.TryGetValue(nbAddr, out var nb) && !nb.Completed)
                {
                    frontier.Add(c);
                    break;
                }
            }
        }

        // BFS from all frontier cells simultaneously.
        var dist = _distCache;
        var prev = _prevCache;
        var queue = new Queue<long>();
        foreach (var f in frontier) { dist[f.Address] = 0; queue.Enqueue(f.Address); }
        while (queue.Count > 0)
        {
            var curAddr = queue.Dequeue();
            if (!byAddr.TryGetValue(curAddr, out var cur)) continue;
            int curDist = dist[curAddr];
            for (int i = 0; i < cur.Neighbors.Length; i++)
            {
                if (!cur.NeighborConnected[i]) continue;
                var nbAddr = cur.Neighbors[i] + DelveCellReader.ElementToCell;
                if (!byAddr.ContainsKey(nbAddr)) continue;
                if (dist.ContainsKey(nbAddr)) continue;
                dist[nbAddr] = curDist + 1;
                prev[nbAddr] = curAddr;
                queue.Enqueue(nbAddr);
            }
        }

        // Layout edges, deduplicated.
        _layoutEdges.Clear();
        var seen = new HashSet<(long, long)>();
        foreach (var c in cells)
        {
            for (int i = 0; i < c.Neighbors.Length; i++)
            {
                if (!c.NeighborConnected[i] || c.Neighbors[i] == 0) continue;
                var nbAddr = c.Neighbors[i] + DelveCellReader.ElementToCell;
                if (!byAddr.ContainsKey(nbAddr)) continue;
                long a = c.Address, b = nbAddr;
                var key = a < b ? (a, b) : (b, a);
                if (seen.Add(key)) _layoutEdges.Add(key);
            }
        }

        // Stats rows (counts frozen here; enabled/colors resolve per frame).
        _statsRewardRows.Clear();
        foreach (var g in cells
            .Where(c => !c.IsNothing && !c.Completed)
            .Where(c => {
                if (c.Tier <= 0) return true;
                var rf = Settings.RewardFilters.TryGetValue(c.Reward ?? "", out var r) ? r : null;
                return rf != null && rf.SelectedTiers.Contains(c.Tier);
            })
            .GroupBy(StatsRewardName)
            .OrderByDescending(g => g.Count()))
        {
            _statsRewardRows.Add(new StatsRewardRow
            {
                Text = $"{g.Key} x {g.Count()}",
                BaseReward = g.First().Reward ?? "",
            });
        }

        _statsBiomeRows.Clear();
        foreach (var g in cells
            .Where(c => !c.IsNothing && !c.Completed && !string.IsNullOrEmpty(c.Biome))
            .Where(c => {
                if (c.Tier <= 0) return true;
                var rf = Settings.RewardFilters.TryGetValue(c.Reward ?? "", out var r) ? r : null;
                return rf != null && rf.SelectedTiers.Contains(c.Tier);
            })
            .GroupBy(c => c.Biome)
            .OrderByDescending(g => g.Count()))
        {
            var pool = NodeBackbone.FossilPoolOf(g.Key);
            _statsBiomeRows.Add(new StatsBiomeRow
            {
                Text = $"{g.Key}: {g.Count()}",
                Pool = pool.Count > 0 ? string.Join(", ", pool) : "",
            });
        }
    }

    /// <summary>
    /// BFS from the completed frontier to PathEnabled reward cells and draw paths.
    /// Traverses every cell with a real connection edge (corridor pass-throughs included);
    /// only real reward nodes are targets.
    /// </summary>
    private void DrawRewardPaths(List<DelveCellReader.Cell> cells, Vector2 mapMin, Vector2 mapMax)
    {
        if (cells.Count == 0) return;

        // A cell is a valid reward TARGET if it's a real node (not fogged/empty). Completed
        // cells are never targets (you can't redo them). NOTE: traversal is NOT gated on this
        // — "Nothing" cells that carry a real NeighborConnected edge are corridor pass-throughs
        // between nodes and must be crossable; truly fogged cells have companion==0 (no edge)
        // so they're already unreachable. This distinction is what allows A -> empty tile -> B.
        Func<DelveCellReader.Cell, bool> targetable = c => !c.IsNothing;

        // Distances/parents come from the topology cache (rebuilt when the walked set
        // changes); targets resolve against this frame's cells so filters apply instantly.
        var dist = _distCache;
        var prev = _prevCache;
        var byAddr = _frameByAddr;
        if (dist.Count == 0) return;

        // Targets = ALL uncompleted real reward cells with pathfinding enabled for that
        // reward (per-reward toggle) that are reachable from the frontier, sorted by hop
        // count ascending and capped at MaxPaths. Drawing every reachable target (not just
        // the nearest per reward) is what lets the user see paths to rewards several hops
        // away, not only the closest one.
        var targetList = new List<KeyValuePair<int, DelveCellReader.Cell>>();
        foreach (var c in cells)
        {
            if (!targetable(c) || c.Completed) continue;
            if (!dist.ContainsKey(c.Address)) continue; // unreachable
            NodeFilter rf = null;
            Settings.RewardFilters.TryGetValue(c.Reward ?? "", out rf);
            if (rf == null || !rf.PathEnabled) continue;
            if (c.Tier > 0 && !rf.SelectedTiers.Contains(c.Tier)) continue;
            targetList.Add(new KeyValuePair<int, DelveCellReader.Cell>(dist[c.Address], c));
        }
        if (targetList.Count == 0) return;

        // Reserve every node label zone plus already-drawn hop counts so hop counts
        // never print over other text. Frames are deliberately NOT reserved: cells
        // tile edge-to-edge, so any pad around frames would disqualify every slot.
        // A hop may overlap a box edge (its background keeps it readable), never text.
        // Draw order is score order, so the most valuable route keeps its label.
        var occupied = new List<Vector4>();
        foreach (var c in cells)
        {
            if (!NodeLabelsDrawn(c)) continue;
            var cf = c.FrameTopLeft(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
            var ce = c.FrameBottomRight(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
            var cx = (cf.X + ce.X) / 2f;
            var fw = MeasureTextCached(c.Feature);
            float lw = fw.X, lh = fw.Y;
            if (c.Reward.Length > 0)
            {
                var rt = c.Reward;
                if (c.Reward == "Fossils")
                {
                    var ex = NodeBackbone.ExclusiveFossilOf(c.Feature);
                    if (ex != null) rt = ex;
                }
                var rw = MeasureTextCached(rt);
                lw = Math.Max(lw, rw.X);
                lh += rw.Y + 2f;
            }
            occupied.Add(new Vector4(cx - lw / 2f, ce.Y + 2f, lw, lh));
        }

        float th = Math.Max(1f, Settings.PathThickness);

        // Build the ordered draw list. Ordering uses value x closeness: score = weight /
        // (hops + 1), so a moderately valuable reward 2 hops away beats a trash reward 1 hop
        // away. All reachable PathEnabled targets draw (capped by MaxPaths).
        float Score(DelveCellReader.Cell cell, int hops)
        {
            var w = 0f;
            if (Settings.RewardFilters.TryGetValue(cell.Reward ?? "", out var srf))
            {
                w = srf.Weight;
                if (cell.Tier > 0 && srf.TierWeights.TryGetValue(cell.Tier, out var stw))
                    w = stw;
            }
            return w / (hops + 1);
        }

        var drawList = targetList
            .Select(kv => (kv.Key, kv.Value, Score(kv.Value, kv.Key)))
            .OrderByDescending(t => t.Item3)
            .ThenBy(t => t.Key)
            .ToList();

        int maxPaths = Math.Max(1, Settings.MaxPaths);
        int drawn = 0;
        foreach (var (hops, target, _) in drawList)
        {
            if (drawn >= maxPaths) break;
            drawn++;

            // Line and box share the target's own reward color (same as its
            // node frame), so the eye matches path to node.
            Settings.RewardFilters.TryGetValue(target.Reward ?? "", out var trf);
            var tweight = trf?.Weight ?? 0f;
            if (target.Tier > 0 && trf != null && trf.TierWeights.TryGetValue(target.Tier, out var ttw))
                tweight = ttw;
            var boxColor = ApplyWeightWithOpacity(NodeBaseColor(target.Reward, trf), tweight, Settings.WeightOpacityStrength);

            // Walk prev chain back to the frontier, resolving against this frame's
            // cells (a cell scrolled out of the walk truncates the drawn part).
            var chain = new List<DelveCellReader.Cell>();
            long a = target.Address;
            while (true)
            {
                if (!byAddr.TryGetValue(a, out var cell)) break;
                chain.Add(cell);
                if (!prev.TryGetValue(a, out a)) break;
            }
            chain.Reverse();

            for (int i = 0; i + 1 < chain.Count; i++)
            {
                var ca = chain[i];
                var cb = chain[i + 1];
                var pa = ca.Center;
                var pb = cb.Center;
                var dd = pb - pa;
                if (dd.LengthSquared() < 0.001f) continue;
                dd = Vector2.Normalize(dd);
                Vector2 start = pa, end = pb;
                if (DrawsPathFrame(ca))
                {
                    var fa = ca.FrameTopLeft(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
                    var fae = ca.FrameBottomRight(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
                    start = RectEdgePort(pa, fa.X, fa.Y, fae.X, fae.Y, dd);
                }
                if (DrawsPathFrame(cb))
                {
                    var fb = cb.FrameTopLeft(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
                    var fbe = cb.FrameBottomRight(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
                    end = RectEdgePort(pb, fb.X, fb.Y, fbe.X, fbe.Y, -dd);
                }
                // Touching boxes already imply the connection; only draw real spans.
                if ((end - start).LengthSquared() < 1f) continue;
                Graphics.DrawLine(start, end, Math.Min(th, 2f), boxColor);
            }

            var tCenter = target.Center;
            var pulseColor = boxColor;
            if (Settings.PathPulse)
            {
                var t = (float)(DateTime.UtcNow.TimeOfDay.TotalSeconds * Settings.PathPulseSpeed % 1f);
                var tri = t < 0.5f ? t * 2f : 1f - (t - 0.5f) * 2f;
                // Rebuild from 0-1 floats: the float ctor clamps >1 to 255, so raw
                // bytes (e.g. 200) would all saturate to white.
                var bc = boxColor.ToColor4();
                pulseColor = new SharpDX.Color(bc.Red, bc.Green, bc.Blue, bc.Alpha * (0.4f + 0.6f * tri));
            }
            var tFrame = target.FrameTopLeft(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
            var tFrameEnd = target.FrameBottomRight(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
            Graphics.DrawFrame(tFrame, tFrameEnd, pulseColor, 3);

            var hopText = $"{hops} hop{(hops == 1 ? "" : "s")}";
            var ts = MeasureTextCached(hopText);
            // Above the target frame: the feature/reward labels live below it,
            // so the hop count gets its own line with room for all three.
            var hp = new Vector2(tCenter.X, tFrame.Y - ts.Y - 2f);
            var hr = new Vector4(hp.X - ts.X / 2f, hp.Y, ts.X, ts.Y);
            bool hopFits = hp.Y >= mapMin.Y && hr.X >= mapMin.X && hr.X + hr.Z <= mapMax.X && hr.Y + hr.W <= mapMax.Y;
            if (hopFits)
            {
                foreach (var o in occupied)
                {
                    if (RectsOverlap(hr, o, 2f)) { hopFits = false; break; }
                }
            }
            if (hopFits)
            {
                occupied.Add(hr);
                Graphics.DrawTextWithBackground(hopText, hp, boxColor, FontAlign.Center,
                    new SharpDX.Color(0f, 0f, 0f, 0.8f));
            }
        }
    }

    private Vector2 MeasureTextCached(string text)
    {
        if (_textSizeCache.TryGetValue(text, out var sz)) return sz;
        sz = Graphics.MeasureText(text);
        _textSizeCache[text] = sz;
        return sz;
    }

    /// <summary>
    /// Stats panel docked to the left edge of the chart. Shows a color legend, the biome
    /// summary with fossil pools, and counts of the current mine's nodes.
    /// </summary>
    private void DrawStatsPanel(List<DelveCellReader.Cell> cells, SharpDX.RectangleF map)
    {
        const float width = 260f;
        var pos = new Vector2(map.X - width - 8f, map.Y);
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(new Vector2(width, 0f));
        ImGui.Begin("Delve Stats", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize);

        // Word-wrap all text at the window's content edge so nothing is cut off.
        // GetWindowContentRegionMax is gone from the current ImGui, so the edge is the
        // cursor position plus the space left in the content region.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

        // Node counts by reward type (only for enabled rewards, sorted by count desc).
        if (ImGui.CollapsingHeader("Nodes by type", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var dl = ImGui.GetWindowDrawList();
            foreach (var row in _statsRewardRows)
            {
                // Exclusive-fossil rows share the Fossils filter (color/weight/enabled).
                var rf = Settings.RewardFilters.TryGetValue(row.BaseReward, out var r) ? r : null;
                if (rf == null || !rf.Enabled) continue;
                var color = ApplyWeight(NodeBaseColor(row.BaseReward, rf), rf.Weight);
                var start = ImGui.GetCursorScreenPos();
                dl.AddRectFilled(new Vector2(start.X, start.Y + 1),
                    new Vector2(start.X + 10, start.Y + 11), ToU32(color));
                ImGui.SetCursorScreenPos(new Vector2(start.X + 15, start.Y));
                ImGui.TextUnformatted(row.Text);
            }
        }

        // Node stats: counts by state.
        if (ImGui.CollapsingHeader("Nodes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int completed = 0, open = 0, likelyHidden = 0, isolatedHidden = 0, nothing = 0;
            foreach (var c in cells)
            {
                if (c.Completed) { completed++; continue; }
                if (c.IsNothing) { nothing++; continue; }
                int conns = 0;
                for (int i = 0; i < c.NeighborConnected.Length; i++)
                    if (c.NeighborConnected[i]) conns++;
                if (conns == 2) likelyHidden++;
                else if (conns == 0) isolatedHidden++;
                else open++;
            }
            ImGui.TextUnformatted($"Completed (done line): {completed}");
            ImGui.TextUnformatted($"Open: {open}");
            ImGui.TextUnformatted($"Hidden-path likely (2 links): {likelyHidden}");
            ImGui.TextUnformatted($"Isolated hidden nodes: {isolatedHidden}");
            ImGui.TextUnformatted($"Empty/fogged: {nothing}");
        }

        // Biome summary: which biomes are present and what fossils they offer.
        if (ImGui.CollapsingHeader("Biomes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var row in _statsBiomeRows)
            {
                ImGui.TextUnformatted(row.Text);
                if (row.Pool.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(row.Pool);
                }
            }
        }

        ImGui.PopTextWrapPos();

        ImGui.End();
    }

    /// <summary>
    /// Draw every real connection edge. GREEN = both endpoints are real nodes; YELLOW =
    /// corridor passing through a "Nothing" tile (still travelable); GRAY = everything else.
    /// Reveals the full mine structure, including connections behind the fog.
    /// </summary>
    private void DrawMineLayout(List<DelveCellReader.Cell> cells, Vector2 mapMin, Vector2 mapMax)
    {
        if (cells.Count == 0 || _layoutEdges.Count == 0) return;

        var green = new SharpDX.Color(0.2f, 0.9f, 0.3f, 0.55f);
        var yellow = new SharpDX.Color(1f, 0.85f, 0.2f, 0.5f);
        var edgeThickness = Math.Max(1f, Settings.LayoutThickness);
        var byAddr = _frameByAddr;
        foreach (var (ea, eb) in _layoutEdges)
        {
            if (!byAddr.TryGetValue(ea, out var ca) || !byAddr.TryGetValue(eb, out var cb)) continue;
            var bothReal = !ca.IsNothing && !cb.IsNothing;
            Graphics.DrawLine(ca.Center, cb.Center, edgeThickness, bothReal ? green : yellow);
        }
    }

    // ---------------------------------------------------------------- settings UI

    public override void DrawSettings()
    {
        EnsureFiltersSeeded();

        if (ImGui.BeginTabBar("##delveTabs"))
        {
            if (ImGui.BeginTabItem("Rewards"))
            {
                DrawRewardsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Map Display"))
            {
                DrawMapDisplayTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug"))
            {
                DrawDebugTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    var enabled = Settings.Enable.Value;
                    if (ImGui.Checkbox("Plugin enabled", ref enabled)) Settings.Enable.Value = enabled;

                    var refresh = Settings.RefreshMs.Value;
                    if (ImGui.SliderInt("Refresh rate (ms)", ref refresh, Settings.RefreshMs.Min, Settings.RefreshMs.Max))
                        Settings.RefreshMs.Value = refresh;
                }

                ImGui.Spacing();
                ImGui.TextDisabled("Filters are keyed by reward (from live feature Ids), not area name.");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawRewardsTab()
    {
        // Left: search + reward list. Right: selected reward detail.
        ImGui.BeginChild("##nfLeft", new Vector2(200f, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX);
        DrawRewardList();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##nfRight", new Vector2(0f, 0f), ImGuiChildFlags.Borders);
        DrawRewardDetail();
        ImGui.EndChild();
    }

    private void DrawRewardList()
    {
        if (ImGui.InputText("Search", ref _searchText, 64))
        {
            if (_selectedReward != null && !_selectedReward.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                _selectedReward = null;
        }

        ImGui.Spacing();

        var dl = ImGui.GetWindowDrawList();

        var rewards = NodeBackbone.AllRewards.Count > 0 ? NodeBackbone.AllRewards : NodeBackbone.DefaultRewards;
        var rows = rewards
            .Where(r => string.IsNullOrEmpty(_searchText) ||
                        r.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var reward in rows)
        {
            var rf = Settings.RewardFilters[reward];
            var start = ImGui.GetCursorScreenPos();

            ImGui.SetCursorScreenPos(new Vector2(start.X + 17, start.Y));
            ImGui.PushID(reward);

            bool enabled = rf.Enabled;
            if (ImGui.Checkbox("##en", ref enabled))
                SetRewardEnabled(rf, enabled);

            ImGui.SameLine();
            if (ImGui.Selectable(reward, _selectedReward == reward)) _selectedReward = reward;

            ImGui.PopID();

            var textH = ImGui.GetTextLineHeight();
            var textY = (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) / 2f - textH / 2f;

            var baseColor = NodeBaseColor(reward, rf);
            var swatchColor = ApplyWeight(baseColor, rf.Weight);
            var swatchY = textY + textH / 2f - 5.5f;
            dl.AddRectFilled(new Vector2(start.X + 2, swatchY),
                new Vector2(start.X + 13, swatchY + 11),
                ToU32(swatchColor));

            var readout = NodeBackbone.RewardTierRange(reward);
            var tw = readout.Length == 0 ? 0f : ImGui.CalcTextSize(readout).X;
            var rightEdge = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - 12f;
            dl.AddText(new Vector2(rightEdge - tw, textY),
                ToU32(new SharpDX.Color(0.75f, 0.75f, 0.75f, 1f)), readout);

            ImGui.Spacing();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset all filters"))
        {
            foreach (var kv in Settings.RewardFilters)
                ApplyDefaults(kv.Value, kv.Key);
        }
    }

    /// <summary>Reward on/off: disabling a reward also disables its pathfinding.</summary>
    private static void SetRewardEnabled(NodeFilter rf, bool enabled)
    {
        rf.Enabled = enabled;
        if (!enabled) rf.PathEnabled = false;
    }

    private void DrawRewardDetail()
    {
        if (string.IsNullOrEmpty(_selectedReward))
        {
            ImGui.TextDisabled("Select a reward on the left.");
            return;
        }

        if (!Settings.RewardFilters.TryGetValue(_selectedReward, out var rf))
        {
            rf = NewSeededFilter(_selectedReward);
            Settings.RewardFilters[_selectedReward] = rf;
        }

        ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.5f, 1f), _selectedReward);
        ImGui.Spacing();

        bool enabled = rf.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
            SetRewardEnabled(rf, enabled);

        bool pathOn = rf.PathEnabled;
        if (ImGui.Checkbox("Pathfind to this reward", ref pathOn)) rf.PathEnabled = pathOn;

        // Rewards with real tiering (Azurite/Chambers) use per-tier weights, so the
        // global weight slider is redundant and hidden.
        bool hasTiers = NodeBackbone.RewardTiers.TryGetValue(_selectedReward, out var tierList) && tierList.Count > 0;
        var weight = rf.Weight;
        if (!hasTiers)
        {
            if (ImGui.SliderFloat("Weight", ref weight, -10f, 10f)) rf.Weight = weight;

            ImGui.TextUnformatted(
                weight <= -9.5f ? "Fully desaturated (barely visible)" :
                weight >= 9.5f ? "Full saturation (brightest)" :
                weight < -0.5f ? "Desaturated" :
                weight > 0.5f ? "Enhanced" : "Neutral");
        }

        var autoC4 = RewardColor(_selectedReward).ToColor4();
        var cc = rf.CustomColor ?? new Vector4(autoC4.Red, autoC4.Green, autoC4.Blue, autoC4.Alpha);
        if (ImGui.ColorEdit4("Color (auto if unset)", ref cc))
            rf.CustomColor = cc;
        if (rf.CustomColor.HasValue && ImGui.SmallButton("Use auto color"))
            rf.CustomColor = null;

        // Tier selection - only meaningful for rewards with real tiering (Azurite, Chambers).
        if (hasTiers)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted("Show tiers (none selected = hidden):");
            foreach (var t in tierList)
            {
                bool sel = rf.SelectedTiers.Contains(t);
                if (ImGui.Checkbox($"T{t} ", ref sel))
                {
                    if (sel) rf.SelectedTiers.Add(t);
                    else rf.SelectedTiers.Remove(t);
                }
                ImGui.SameLine();

                var tw = rf.TierWeights.TryGetValue(t, out var existing) ? existing : rf.Weight;
                var twLabel = $"weight##t{t}";
                if (ImGui.SliderFloat(twLabel, ref tw, -10f, 10f))
                    rf.TierWeights[t] = tw;
            }
            ImGui.TextDisabled("Per-tier weight overrides the reward weight for that tier.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (NodeBackbone.RewardNodes.TryGetValue(_selectedReward, out var nodes) && nodes.Count > 0)
        {
            ImGui.TextUnformatted($"Nodes ({nodes.Count}): {string.Join(", ", nodes)}");
        }
    }

    private void DrawMapDisplayTab()
    {
        if (ImGui.CollapsingHeader("Display", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var names = Settings.DrawNames.Value;
            if (ImGui.Checkbox("Draw names", ref names)) Settings.DrawNames.Value = names;

            var showEmpty = Settings.ShowEmpty.Value;
            if (ImGui.Checkbox("Show empty grid cells", ref showEmpty)) Settings.ShowEmpty.Value = showEmpty;

            var hideCompleted = Settings.HideCompleted.Value;
            if (ImGui.Checkbox("Hide completed (dim done nodes)", ref hideCompleted)) Settings.HideCompleted.Value = hideCompleted;
        }

        if (ImGui.CollapsingHeader("Pathfinding", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var showRewardPaths = Settings.ShowPaths.Value;
            if (ImGui.Checkbox("Show reward paths (BFS from done line)", ref showRewardPaths)) Settings.ShowPaths.Value = showRewardPaths;

            if (Settings.ShowPaths.Value)
            {
                var pathTh = Settings.PathThickness;
                if (ImGui.SliderFloat("Path thickness", ref pathTh, 1f, 8f)) Settings.PathThickness = pathTh;

                var maxP = Settings.MaxPaths;
                if (ImGui.SliderInt("Max paths drawn", ref maxP, 1, 30)) Settings.MaxPaths = maxP;

                var col = Settings.PathColor;
                if (ImGui.ColorEdit4("Path color", ref col))
                    Settings.PathColor = new System.Numerics.Vector4(col.X, col.Y, col.Z, col.W);

                var pulse = Settings.PathPulse;
                if (ImGui.Checkbox("Pulse target", ref pulse)) Settings.PathPulse = pulse;
                if (Settings.PathPulse)
                {
                    var pulseSpeed = Settings.PathPulseSpeed;
                    if (ImGui.SliderFloat("Pulse speed", ref pulseSpeed, 0.5f, 6f)) Settings.PathPulseSpeed = pulseSpeed;
                }

                ImGui.TextDisabled("Paths draw for rewards with \"Pathfind\" enabled in the Rewards tab.");
            }

            ImGui.Spacing();

            var showLayout = Settings.ShowLayout.Value;
            if (ImGui.Checkbox("Show mine layout (all connections, incl. behind fog)", ref showLayout)) Settings.ShowLayout.Value = showLayout;
            if (Settings.ShowLayout.Value)
            {
                var layoutTh = Settings.LayoutThickness;
                if (ImGui.SliderFloat("Layout line thickness", ref layoutTh, 1f, 4f)) Settings.LayoutThickness = layoutTh;
            }
        }

        if (ImGui.CollapsingHeader("Hidden paths (fractured walls)"))
        {
            var showPaths = Settings.ShowHiddenPaths.Value;
            if (ImGui.Checkbox("Show hidden paths (walls)", ref showPaths)) Settings.ShowHiddenPaths.Value = showPaths;

            if (Settings.ShowHiddenPaths.Value)
            {
                var col = Settings.HiddenPathColor;
                if (ImGui.ColorEdit4("Hidden path color", ref col))
                    Settings.HiddenPathColor = new System.Numerics.Vector4(col.X, col.Y, col.Z, col.W);

                var thick = Settings.HiddenPathThickness;
                if (ImGui.SliderFloat("Hidden path thickness", ref thick, 1f, 12f)) Settings.HiddenPathThickness = thick;

                var dash = Settings.HiddenPathDash;
                if (ImGui.SliderFloat("Hidden path dash", ref dash, 2f, 24f)) Settings.HiddenPathDash = dash;

                var gap = Settings.HiddenPathGap;
                if (ImGui.SliderFloat("Hidden path gap", ref gap, 1f, 20f)) Settings.HiddenPathGap = gap;
            }
        }

        if (ImGui.CollapsingHeader("Layout & alignment"))
        {
            var inset = Settings.FrameInset;
            if (ImGui.SliderFloat("Frame inset", ref inset, 0f, 30f)) Settings.FrameInset = inset;

            var font = Settings.FontScale;
            if (ImGui.SliderFloat("Font scale", ref font, 0.4f, 3f)) Settings.FontScale = font;

            ImGui.Spacing();
            var ox = Settings.OffsetX;
            if (ImGui.SliderFloat("Align offset X", ref ox, -40f, 40f)) Settings.OffsetX = ox;
            var oy = Settings.OffsetY;
            if (ImGui.SliderFloat("Align offset Y", ref oy, -40f, 40f)) Settings.OffsetY = oy;
        }

        if (ImGui.CollapsingHeader("Stats panel"))
        {
            var showStats = Settings.ShowStatsPanel.Value;
            if (ImGui.Checkbox("Show stats panel (legend, biomes, node counts)", ref showStats)) Settings.ShowStatsPanel.Value = showStats;
        }

        if (ImGui.CollapsingHeader("Weight visuals"))
        {
            var wos = Settings.WeightOpacityStrength;
            if (ImGui.SliderFloat("Weight affects opacity", ref wos, 0f, 1f)) Settings.WeightOpacityStrength = wos;
            ImGui.TextDisabled("Weight also controls saturation of node colors.");
        }

        if (ImGui.CollapsingHeader("Awareness (snake comet)"))
        {
            var st = Settings.SnakeThreshold;
            if (ImGui.SliderFloat("Snake threshold (weight)", ref st, 5f, 10f)) Settings.SnakeThreshold = st;
            var ss = Settings.SnakeSpeed;
            if (ImGui.SliderFloat("Snake speed", ref ss, 0f, 3f)) Settings.SnakeSpeed = ss;
            var sk = Settings.SnakeThickness;
            if (ImGui.SliderFloat("Snake thickness (px)", ref sk, 1f, 6f)) Settings.SnakeThickness = sk;
            var so = Settings.SnakeOpacity;
            if (ImGui.SliderFloat("Snake opacity", ref so, 0f, 1f)) Settings.SnakeOpacity = so;
            ImGui.TextDisabled("A white comet circles the frame of high-weight nodes.");
        }
    }

    private void DrawDebugTab()
    {
        if (ImGui.CollapsingHeader("Status", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextUnformatted($"Cells: {_cells.Count}");
            ImGui.TextUnformatted($"Chart open: {_chartOpen}");
            ImGui.TextUnformatted(DelveCellReader.Diagnostics);
            if (ImGui.Button("Re-detect cell offset"))
                DelveCellReader.ForceRedetect();
            ImGui.TextDisabled("Diagnostic info. Not needed for normal use.");
        }

        if (ImGui.CollapsingHeader("Dump", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.Button("Dump current mine to file"))
            {
                try { DumpMine(); }
                catch (Exception ex) { LogError($"Dump failed: {ex.Message}"); }
            }
            ImGui.TextDisabled("Writes the current mine (nodes, rewards, positions, connections) to a JSON file.");
        }
    }

    private void DumpMine()
    {
        List<DelveCellReader.Cell> cells;
        lock (_sync) { cells = _cells; }

        var asmDir = System.IO.Path.GetDirectoryName(GetType().Assembly.Location) ?? ".";
        var dir = System.IO.Path.Combine(asmDir, "dumps");
        System.IO.Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var path = System.IO.Path.Combine(dir, $"mine_{stamp}.json");

        var rows = cells.Select(c => new
        {
            Feature = c.Feature,
            FeatureId = c.FeatureId,
            Biome = c.Biome,
            Reward = c.Reward,
            Tier = c.Tier,
            Completed = c.Completed,
            Neighbors = Enumerable.Range(0, 4)
                .Where(i => c.NeighborConnected[i])
                .Select(i => c.NeighborFeatures[i])
                .ToArray(),
        }).ToList();

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(rows, Newtonsoft.Json.Formatting.Indented);
        System.IO.File.WriteAllText(path, json);
        LogMessage($"Dumped {rows.Count} cells to {path}", 4f);
    }

    // ---------------------------------------------------------------- color helpers

    /// <summary>Stats display name: exclusive-fossil nodes show the fossil name, not generic "Fossils".</summary>
    private static string StatsRewardName(DelveCellReader.Cell c)
    {
        if (c.Reward == "Fossils")
        {
            var ex = NodeBackbone.ExclusiveFossilOf(c.Feature);
            if (!string.IsNullOrEmpty(ex)) return ex;
        }
        return c.Reward ?? "?";
    }

    /// <summary>Effective color: the filter's custom color if set, else the auto reward color.</summary>
    private static SharpDX.Color NodeBaseColor(string reward, NodeFilter rf)    {
        if (rf?.CustomColor is { } cc)
            return new SharpDX.Color(cc.X, cc.Y, cc.Z, cc.W);
        return RewardColor(reward);
    }

    /// <summary>Auto color per reward label.</summary>
    private static SharpDX.Color RewardColor(string reward)
    {
        switch (reward ?? "")
        {
            case "Azurite": return new SharpDX.Color(0.2f, 0.9f, 0.3f, 1f);
            case "Hidden Path": return new SharpDX.Color(1f, 0.15f, 0.15f, 1f);
            case "Boss": return new SharpDX.Color(1f, 0.2f, 0.3f, 1f);
            case "Chamber": return new SharpDX.Color(0.8f, 0.2f, 0.9f, 1f);
            case "Fossils": return new SharpDX.Color(0.9f, 0.5f, 0.2f, 1f);
            case "Maps": return new SharpDX.Color(0.3f, 0.7f, 1f, 1f);
            case "Strongboxes": return new SharpDX.Color(0.6f, 0.4f, 0.2f, 1f);
            case "Loot": return new SharpDX.Color(0.9f, 0.8f, 0.3f, 1f);
            case "Fire": return new SharpDX.Color(1f, 0.4f, 0.2f, 1f);
            case "Cold": return new SharpDX.Color(0.4f, 0.7f, 1f, 1f);
            case "Lightning": return new SharpDX.Color(0.95f, 0.9f, 0.3f, 1f);
            case "Minion/Aura": return new SharpDX.Color(0.8f, 0.3f, 0.8f, 1f);
            case "Chaos": return new SharpDX.Color(0.6f, 0.3f, 0.9f, 1f);
            case "Animalistic": return new SharpDX.Color(0.6f, 0.8f, 0.4f, 1f);
            case "Physical": return new SharpDX.Color(0.7f, 0.6f, 0.4f, 1f);
            case "Talismans": return new SharpDX.Color(0.8f, 0.5f, 0.4f, 1f);
            case "Abyss": return new SharpDX.Color(0.3f, 0.3f, 0.8f, 1f);
            case "Mana/Curse": return new SharpDX.Color(0.7f, 0.4f, 0.9f, 1f);
            case "Essences": return new SharpDX.Color(0.9f, 0.7f, 0.4f, 1f);
            case "Bestiary": return new SharpDX.Color(0.4f, 0.8f, 0.6f, 1f);
            case "Legion": return new SharpDX.Color(0.9f, 0.5f, 0.3f, 1f);
            case "Breach": return new SharpDX.Color(0.9f, 0.3f, 0.6f, 1f);
            case "Beyond": return new SharpDX.Color(0.5f, 0.3f, 0.9f, 1f);
            case "Harbinger": return new SharpDX.Color(0.3f, 0.9f, 0.9f, 1f);
            case "Armour":
            case "Weapons":
            case "Gems":
            case "Currency":
            case "Jewellery":
                return new SharpDX.Color(0.55f, 0.65f, 0.9f, 1f);
            default:
                if (string.IsNullOrEmpty(reward))
                    return new SharpDX.Color(0.4f, 0.4f, 0.4f, 0.6f);
                return new SharpDX.Color(0.4f, 0.5f, 0.9f, 1f);
        }
    }

    /// <summary>Weight -10..10 maps to saturation 0..1 (-10 = gray, +10 = full color).</summary>
    private static SharpDX.Color ApplyWeight(SharpDX.Color c, float weight)
    {
        var s = Math.Clamp((weight + 10f) / 20f, 0f, 1f);
        var c4 = c.ToColor4();
        var lum = 0.2126f * c4.Red + 0.7152f * c4.Green + 0.0722f * c4.Blue;
        var r = lum + (c4.Red - lum) * s;
        var g = lum + (c4.Green - lum) * s;
        var b = lum + (c4.Blue - lum) * s;
        return new SharpDX.Color(r, g, b, c4.Alpha);
    }

    /// <summary>
    /// Opacity multiplier for a weight: -10 fades toward 0.4 alpha, +10 full, scaled by
    /// <paramref name="opacityStrength"/> (0 = no opacity effect). Shared by frames, labels
    /// and their backgrounds so low-weight rewards fade together.
    /// </summary>
    private static float OpacityForWeight(float weight, float opacityStrength)
    {
        var s = Math.Clamp((weight + 10f) / 20f, 0f, 1f);
        const float minAlpha = 0.4f;
        var alpha = minAlpha + (1f - minAlpha) * s;
        return 1f + (alpha - 1f) * Math.Clamp(opacityStrength, 0f, 1f);
    }

    /// <summary>Weight applied as saturation (via ApplyWeight) AND opacity.</summary>
    private static SharpDX.Color ApplyWeightWithOpacity(SharpDX.Color c, float weight, float opacityStrength)
    {
        var faded = ApplyWeight(c, weight);
        var c4 = faded.ToColor4();
        return new SharpDX.Color(c4.Red, c4.Green, c4.Blue, c4.Alpha * OpacityForWeight(weight, opacityStrength));
    }

    /// <summary>
    /// "Snake" awareness effect for high-weight rewards (adapted from MercScanner):
    /// a comet of filled segments travels clockwise around the frame perimeter. The head
    /// is bright and the tail fades out, so the eye tracks a pulse around the node.
    /// White, opacity-controlled; speed sets how fast the comet moves.
    /// </summary>
    private void DrawSnakeEffect(Vector2 tl, Vector2 br, float speed, float thickness, float opacity)
    {
        var lineThickness = Math.Max(1f, thickness);
        var padding = 0f; // 0 = snake rides exactly on the frame line itself.
        const int snakeLength = 60;

        var snakePosition = (float)(DateTime.UtcNow.TimeOfDay.TotalSeconds * 100f * speed);

        var pathWidth = (br.X - tl.X) + padding * 2f;
        var pathHeight = (br.Y - tl.Y) + padding * 2f;
        var perimeter = (pathWidth + pathHeight) * 2f;
        var startX = tl.X - padding;
        var startY = tl.Y - padding;

        for (int i = 0; i < snakeLength; i++)
        {
            var segmentOffset = (snakePosition - i) % perimeter;
            if (segmentOffset < 0) segmentOffset += perimeter;

            var fade = 1f - (i / (float)snakeLength);
            var alpha = Math.Max(0.02f, fade) * opacity;

            var brightness = 0.5f + fade * 0.5f;
            var white = Math.Min(1f, brightness);
            var segmentColor = new SharpDX.Color(white, white, white, alpha);

            float sx, sy;
            if (segmentOffset < pathWidth)
            {
                sx = startX + segmentOffset;
                sy = startY;
            }
            else if (segmentOffset < pathWidth + pathHeight)
            {
                sx = startX + pathWidth;
                sy = startY + (segmentOffset - pathWidth);
            }
            else if (segmentOffset < pathWidth * 2f + pathHeight)
            {
                sx = startX + pathWidth - (segmentOffset - (pathWidth + pathHeight));
                sy = startY + pathHeight;
            }
            else
            {
                sx = startX;
                sy = startY + pathHeight - (segmentOffset - (pathWidth * 2f + pathHeight));
            }

            var p1 = new Vector2(sx - lineThickness / 2f, sy - lineThickness / 2f);
            var p2 = new Vector2(sx + lineThickness / 2f, sy + lineThickness / 2f);
            Graphics.DrawBox(p1, p2, segmentColor, 0f);
        }
    }

    private static uint ToU32(SharpDX.Color c)
    {
        var c4 = c.ToColor4();
        return ImGui.GetColorU32(new Vector4(c4.Red, c4.Green, c4.Blue, c4.Alpha));
    }
}
