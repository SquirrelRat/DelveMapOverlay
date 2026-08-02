using System.Collections.Generic;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace DelveMapOverlay;

/// <summary>
/// Per-reward filter: whether nodes with this reward are drawn, its weight (-10..10),
/// an optional custom color (null = auto reward color), and the tiers to show (empty =
/// all tiers). Only Azurite and Chambers carry real tiers.
/// Weight drives color saturation AND opacity: -10 = desaturated + faint, +10 = full
/// saturation + opaque (opacity strength scales via WeightOpacityStrength).
/// </summary>
public class NodeFilter
{
    public bool Enabled { get; set; } = false;
    public float Weight { get; set; } = 0f;
    public System.Numerics.Vector4? CustomColor { get; set; }
    public System.Collections.Generic.List<int> SelectedTiers { get; set; } = new System.Collections.Generic.List<int>();
    public System.Collections.Generic.Dictionary<int, float> TierWeights { get; set; } = new System.Collections.Generic.Dictionary<int, float>();

    // Whether to draw a path from the completed frontier to this reward's nodes.
    // Default off; enable per-reward in the Rewards tab.
    public bool PathEnabled { get; set; } = false;
}

public class DelveMapOverlaySettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    // Draw a colored frame around every explored node + its name below.
    public ToggleNode DrawNames { get; set; } = new ToggleNode(true);

    // Draw gray boxes for empty/unrevealed ("Nothing") cells. Off by default.
    public ToggleNode ShowEmpty { get; set; } = new ToggleNode(false);

    // Draw elbow connection lines from Obstruction (fractured wall) cells to their
    // connected neighbors, revealing hidden passageways. On by default.
    public ToggleNode ShowHiddenPaths { get; set; } = new ToggleNode(true);

    // Hidden-path dotted line appearance (gold dashes from the wall to its real links).
    public float HiddenPathThickness { get; set; } = 4f;
    public float HiddenPathDash { get; set; } = 8f;
    public float HiddenPathGap { get; set; } = 5f;
    public System.Numerics.Vector4 HiddenPathColor { get; set; } = new System.Numerics.Vector4(1f, 0.84f, 0f, 1f); // gold

    // Completed-line display: dim the explored "done line" cells so the path you've
    // traveled reads clearly. Completed cells are never path targets.
    public ToggleNode HideCompleted { get; set; } = new ToggleNode(true);

    // Pathfinding: BFS from the completed frontier to PathEnabled reward cells, drawn as
    // polylines through the node centers with a hop count at the target. On by default;
    // per-reward on/off lives in the Rewards tab.
    public ToggleNode ShowPaths { get; set; } = new ToggleNode(true);
    public float PathThickness { get; set; } = 1.5f;
    public int MaxPaths { get; set; } = 10;
    public System.Numerics.Vector4 PathColor { get; set; } = new System.Numerics.Vector4(1f, 0.84f, 0f, 1f); // gold
    public bool PathPulse { get; set; } = true;   // pulse the target frame
    public float PathPulseSpeed { get; set; } = 0.5f;

    // When on, each enabled reward draws only its closest reachable target as the primary
    // (pulsed) path; other reachable nodes of that reward render as faint faded hints.
    // When off, all reachable targets draw at full strength (capped by MaxPaths).
    public ToggleNode NearestOnly { get; set; } = new ToggleNode(true);
    public float AlternativeFade { get; set; } = 0.12f; // alpha for faded alternative paths

    // Show the mine layout: every real connection line between nodes, including those
    // behind the fog. This is the full structure of the current mine network.
    public ToggleNode ShowLayout { get; set; } = new ToggleNode(false);
    public float LayoutThickness { get; set; } = 1.5f;

    // Frame inset in pixels (0 = tight around the cell rect).
    public float FrameInset { get; set; } = 0f;
    public float FontScale { get; set; } = 1f;

    // Fine alignment: the node icon texture sits at a sub-position within its cell slot,
    // so nudge the frame/label until they sit directly on the icon.
    public float OffsetX { get; set; } = 0f;
    public float OffsetY { get; set; } = 0f;

    // How strongly weight affects frame/label opacity in addition to saturation.
    // 0 = no opacity effect (weight only desaturates), 1 = full fade range
    // (low weight ~40% alpha, high weight ~100%).
    public float WeightOpacityStrength { get; set; } = 0.6f;

    // High-weight awareness effect: a white comet (snake) travels clockwise around the
    // frame of any node whose reward weight is >= SnakeThreshold. Speed 0 disables it.
    public float SnakeThreshold { get; set; } = 8f;
    public float SnakeSpeed { get; set; } = 1f;
    public float SnakeThickness { get; set; } = 2f;  // comet segment size in px
    public float SnakeOpacity { get; set; } = 0.9f;  // comet opacity 0..1

    // Per-reward display control, keyed by reward label (e.g. "Azurite", "Fossils").
    public Dictionary<string, NodeFilter> RewardFilters { get; set; } = new Dictionary<string, NodeFilter>();

    // Refresh the cell snapshot at most this often.
    public RangeNode<int> RefreshMs { get; set; } = new RangeNode<int>(50, 25, 2000);
}
