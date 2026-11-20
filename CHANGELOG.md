# Changelog

## Unreleased

- The element-to-cell offset is detected at runtime when the built-in value stops matching, so the chart no longer goes blank after a game update. The Debug tab shows the offset in use and the last scan, with a button to re-detect.
- Reward paths and the mine layout use the detected offset for neighbor lookups.
- Smuggler's Stash fossil variants count as Fossils, and Ruined Chambers show up as the Chamber reward.
- Path lines and target boxes use the target's reward color, so each route reads as one color from line to box.
- Route lines stop at node frame edges and run through fogged corridor tiles instead of crossing through boxes.
- Hop counts sit above their target frame and yield to other text instead of printing over it.
- Path ordering and stats use per-tier weights where defined; the stats panel splits exclusive fossils by fossil name.
- Path, layout, and stats math rebuilds only when the mine data changes, with positions updating every frame so panning stays smooth.
- The plugin now compiles optimized instead of debug, which speeds up all of its frame code.
- Path ordering and stats use per-tier weights where defined; the stats panel splits exclusive fossils by fossil name.
- Updated the ElementToCell offset that broke chart reads after the game update.
- The debug dump lands in a dumps/ folder next to the plugin and records node identity instead of screen coordinates.

## v1.0.0

- Builds against the current ExileCore and its ImGui library, so the plugin loads again.
- The stats window wraps text at the content edge using the current API. The old one was removed from ImGui.
- Reward list and detail panels use the renamed child border flag.
