namespace DelveMapOverlay;

/// <summary>
/// Reverse-engineered native offsets for the delve chart cell data.
/// </summary>
public static class DelveOffsets
{
    /// <summary>Offset from a rendered node element back to the native cell base.</summary>
    public const long ElementToCell = -0x10;

    /// <summary>First neighbor link slot (pointer to another cell; bidirectional).</summary>
    public const long CellNeighbor0 = 0x568;

    /// <summary>
    /// Neighbor slot stride (0x18 between slots 0x568/0x580/0x598/0x5B0) and the packed
    /// companion field at slot+8 (0x570/0x588/0x5A0/0x5B8). A nonzero companion means the
    /// neighbor is a REAL delve connection line; a zero companion means only grid adjacency
    /// (the cell physically exists there but there is no path between them).
    /// </summary>
    public const long CellNeighborStride = 0x18;
    public const long CellNeighborCompanionDelta = 8;

    /// <summary>
    /// Per-cell state object pointer. All completed ("done line") cells share the same
    /// state object; anchoring on the Mine Entrance's value identifies the completed set.
    /// </summary>
    public const long CellStateObj = 0x6B0;

    /// <summary>Feature pointer -> DelveFeatures table entry.</summary>
    public const long CellFeature = 0x728;

    /// <summary>Biome pointer -> DelveBiomes table entry.</summary>
    public const long CellBiome = 0x738;

    /// <summary>Cap on the number of elements walked per tree-walk pass.</summary>
    public const int TreeWalkCap = 20000;

    /// <summary>Feature display name of the mine entrance cell (always completed; anchors
    /// the completed-set detection via its state object).</summary>
    public const string MineEntranceFeature = "Mine Entrance";
}
