using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.Shared.Enums;
using ImGuiNET;

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

    /// <summary>
    /// BFS from the completed frontier to PathEnabled reward cells and draw paths.
    /// Traverses every cell with a real connection edge (corridor pass-throughs included);
    /// only real reward nodes are targets.
    /// </summary>
    private void DrawRewardPaths(List<DelveCellReader.Cell> cells, Vector2 mapMin, Vector2 mapMax)
    {
        if (cells.Count == 0) return;

        // Build address -> cell lookup and per-cell adjacency.
        var byAddr = new Dictionary<long, DelveCellReader.Cell>();
        foreach (var c in cells) byAddr[c.Address] = c;

        // A cell is a valid reward TARGET if it's a real node (not fogged/empty). Completed
        // cells are never targets (you can't redo them). NOTE: traversal is NOT gated on this
        // — "Nothing" cells that carry a real NeighborConnected edge are corridor pass-throughs
        // between nodes and must be crossable; truly fogged cells have companion==0 (no edge)
        // so they're already unreachable. This distinction is what allows A -> empty tile -> B.
        Func<DelveCellReader.Cell, bool> targetable = c => !c.IsNothing;

        // Frontier = completed cells with at least one real connection edge (companion != 0)
        // to an uncompleted neighbor. Connectivity is authoritative from NeighborConnected.
        var frontier = new List<DelveCellReader.Cell>();
        foreach (var c in cells)
        {
            if (!c.Completed) continue;
            bool hasOut = false;
            for (int i = 0; i < c.Neighbors.Length; i++)
            {
                if (!c.NeighborConnected[i]) continue;
                var nbAddr = c.Neighbors[i] + DelveOffsets.ElementToCell;
                if (byAddr.TryGetValue(nbAddr, out var nb) && !nb.Completed)
                {
                    hasOut = true;
                    break;
                }
            }
            if (hasOut) frontier.Add(c);
        }
        if (frontier.Count == 0) return;

        // BFS from all frontier cells simultaneously. dist/prev per cell address.
        // Traverse every cell with a real connection edge (including completed cells and
        // "Nothing" corridor cells) so paths can route back through the done line and across
        // empty corridor tiles to reach deeper branches; only real nodes are targets.
        var dist = new Dictionary<long, int>();
        var prev = new Dictionary<long, DelveCellReader.Cell>();
        var queue = new Queue<DelveCellReader.Cell>();
        foreach (var f in frontier) { dist[f.Address] = 0; queue.Enqueue(f); }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            int curDist = dist[cur.Address];
            for (int i = 0; i < cur.Neighbors.Length; i++)
            {
                if (!cur.NeighborConnected[i]) continue;
                var nbAddr = cur.Neighbors[i] + DelveOffsets.ElementToCell;
                if (!byAddr.TryGetValue(nbAddr, out var nb)) continue;
                if (dist.ContainsKey(nb.Address)) continue;
                dist[nb.Address] = curDist + 1;
                prev[nb.Address] = cur;
                queue.Enqueue(nb);
            }
        }

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

        var pc = Settings.PathColor;
        var pathColorSd = new SharpDX.Color(pc.X, pc.Y, pc.Z, pc.W);
        float th = Math.Max(1f, Settings.PathThickness);

        // Build the ordered draw list. Ordering uses value x closeness: score = weight /
        // (hops + 1), so a moderately valuable reward 2 hops away beats a trash reward 1 hop
        // away. All reachable PathEnabled targets draw (capped by MaxPaths).
        float Score(DelveCellReader.Cell cell, int hops)
        {
            var w = 0f;
            if (Settings.RewardFilters.TryGetValue(cell.Reward ?? "", out var srf)) w = srf.Weight;
            return w / (hops + 1);
        }

        var drawList = targetList
            .OrderByDescending(kv => Score(kv.Value, kv.Key))
            .ThenBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value, true, Score(kv.Value, kv.Key))).ToList();

        int maxPaths = Math.Max(1, Settings.MaxPaths);
        int drawn = 0;
        foreach (var (hops, target, primary, score) in drawList)
        {
            if (drawn >= maxPaths) break;
            drawn++;

            // Walk prev chain back to the frontier to get the polyline points.
            var pts = new List<Vector2>();
            var cur = target;
            while (cur != null)
            {
                pts.Add(cur.Center);
                if (!prev.TryGetValue(cur.Address, out cur)) break;
            }
            pts.Reverse();

            for (int i = 0; i + 1 < pts.Count; i++)
                Graphics.DrawLine(pts[i], pts[i + 1], th, pathColorSd);

            var tCenter = target.Center;
            var pulseColor = pathColorSd;
            if (Settings.PathPulse)
            {
                var t = (float)(DateTime.UtcNow.TimeOfDay.TotalSeconds * Settings.PathPulseSpeed % 1f);
                var tri = t < 0.5f ? t * 2f : 1f - (t - 0.5f) * 2f;
                pulseColor = new SharpDX.Color(pathColorSd.R, pathColorSd.G, pathColorSd.B, 0.4f + 0.6f * tri);
            }
            var tFrame = target.FrameTopLeft(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
            var tFrameEnd = target.FrameBottomRight(Settings.FrameInset, Settings.OffsetX, Settings.OffsetY);
            Graphics.DrawFrame(tFrame, tFrameEnd, pulseColor, 3);

            var hopText = $"{hops} hop{(hops == 1 ? "" : "s")}";
            var ts = MeasureTextCached(hopText);
            var hp = new Vector2(tCenter.X - ts.X / 2f, tFrameEnd.Y + 2f);
            if (hp.X >= mapMin.X && hp.X + ts.X <= mapMax.X && hp.Y >= mapMin.Y && hp.Y + ts.Y <= mapMax.Y)
                Graphics.DrawTextWithBackground(hopText, hp, pathColorSd, FontAlign.Center,
                    new SharpDX.Color(0f, 0f, 0f, 0.8f));
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

        // Node counts by reward type (only for enabled rewards, sorted by count desc).
        if (ImGui.CollapsingHeader("Nodes by type", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var byReward = cells
                .Where(c => !c.IsNothing && !c.Completed)
                .GroupBy(c => c.Reward ?? "?")
                .OrderByDescending(g => g.Count());
            var dl = ImGui.GetWindowDrawList();
            foreach (var g in byReward)
            {
                var rf = Settings.RewardFilters.TryGetValue(g.Key, out var r) ? r : null;
                if (rf == null || !rf.Enabled) continue;
                var color = ApplyWeight(NodeBaseColor(g.Key, rf), rf.Weight);
                var start = ImGui.GetCursorScreenPos();
                dl.AddRectFilled(new Vector2(start.X, start.Y + 1),
                    new Vector2(start.X + 10, start.Y + 11), ToU32(color));
                ImGui.SetCursorScreenPos(new Vector2(start.X + 15, start.Y));
                ImGui.TextUnformatted($"{g.Key} x {g.Count()}");
            }
        }

        // Node stats: counts by state.
        if (ImGui.CollapsingHeader("Nodes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int completed = 0, open = 0, hidden2 = 0, hidden0 = 0, nothing = 0;
            foreach (var c in cells)
            {
                if (c.Completed) { completed++; continue; }
                if (c.IsNothing) { nothing++; continue; }
                int conns = 0;
                for (int i = 0; i < c.NeighborConnected.Length; i++)
                    if (c.NeighborConnected[i]) conns++;
                if (conns == 2) hidden2++;
                else if (conns == 0) hidden0++;
                else open++;
            }
            ImGui.TextUnformatted($"Completed (done line): {completed}");
            ImGui.TextUnformatted($"Open: {open}");
            ImGui.TextUnformatted($"Hidden-path likely (2 links): {hidden2}");
            ImGui.TextUnformatted($"Isolated hidden nodes: {hidden0}");
            ImGui.TextUnformatted($"Empty/fogged: {nothing}");
        }

        // Biome summary: which biomes are present and what fossils they offer.
        if (ImGui.CollapsingHeader("Biomes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var byBiome = cells
                .Where(c => !c.IsNothing && !string.IsNullOrEmpty(c.Biome))
                .GroupBy(c => c.Biome)
                .OrderByDescending(g => g.Count());
            foreach (var g in byBiome)
            {
                ImGui.TextUnformatted($"{g.Key}: {g.Count()}");
                var pool = NodeBackbone.FossilPoolOf(g.Key);
                if (pool.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(string.Join(", ", pool));
                }
            }
        }

        ImGui.End();
    }

    /// <summary>
    /// Draw every real connection edge. GREEN = both endpoints are real nodes; YELLOW =
    /// corridor passing through a "Nothing" tile (still travelable); GRAY = everything else.
    /// Reveals the full mine structure, including connections behind the fog.
    /// </summary>
    private void DrawMineLayout(List<DelveCellReader.Cell> cells, Vector2 mapMin, Vector2 mapMax)
    {
        if (cells.Count == 0) return;
        var byAddr = new Dictionary<long, DelveCellReader.Cell>();
        foreach (var c in cells) byAddr[c.Address] = c;

        Func<DelveCellReader.Cell, bool> realNode = c => !c.IsNothing;

        var green = new SharpDX.Color(0.2f, 0.9f, 0.3f, 0.55f);
        var yellow = new SharpDX.Color(1f, 0.85f, 0.2f, 0.5f);
        var edgeThickness = Math.Max(1f, Settings.LayoutThickness);
        var seen = new HashSet<(long, long)>();
        foreach (var c in cells)
        {
            var cCenter = c.Center;
            for (int i = 0; i < c.Neighbors.Length; i++)
            {
                if (!c.NeighborConnected[i]) continue;
                if (c.Neighbors[i] == 0) continue;
                var nbAddr = c.Neighbors[i] + DelveOffsets.ElementToCell;
                if (!byAddr.TryGetValue(nbAddr, out var nb)) continue;
                long a = c.Address, b = nb.Address;
                var key = a < b ? (a, b) : (b, a);
                if (!seen.Add(key)) continue;
                var nbCenter = nb.Center;
                var bothReal = realNode(c) && realNode(nb);
                Graphics.DrawLine(cCenter, nbCenter, edgeThickness, bothReal ? green : yellow);
            }
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
        ImGui.BeginChild("##nfLeft", new Vector2(200f, 0f), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
        DrawRewardList();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##nfRight", new Vector2(0f, 0f), ImGuiChildFlags.Border);
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
            if (ImGui.Checkbox("##en", ref enabled)) rf.Enabled = enabled;

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
        if (ImGui.Checkbox("Enabled", ref enabled)) rf.Enabled = enabled;

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

        var dir = @"F:\PoE\PoE-DEV\DelveMap\dumps";
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
            X = c.Rect.X,
            Y = c.Rect.Y,
            W = c.Rect.Z,
            H = c.Rect.W,
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

    /// <summary>Effective color: the filter's custom color if set, else the auto reward color.</summary>
    private static SharpDX.Color NodeBaseColor(string reward, NodeFilter rf)
    {
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
