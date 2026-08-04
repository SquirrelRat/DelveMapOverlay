# DelveMapOverlay

An ExileCore (ExileAPI) plugin for Path of Exile that overlays the Delve chart to make the mine easier to read and to plan worthwhile routes.

<img width="676" height="768" alt="image" src="https://github.com/user-attachments/assets/ac17bd5a-1c65-4e15-80ef-641b2052d45d" />

## Features

- **Reward-aware highlighting** — every explored node gets a colored frame. Per-reward *weight* (-10..10) drives both color saturation and opacity, so low-value nodes fade/dull and chase-worthy ones stay bright.
- **Reward filter tab** — searchable list of rewards with per-reward toggles for enabled, weight, custom color, tier selection, and *Pathfind to this reward*.
<img width="1086" height="553" alt="image" src="https://github.com/user-attachments/assets/95f21d7b-731c-4151-89ab-7811e20e0c47" />
<p></p>

- **Reward pathfinding (BFS)** — draws routes from your current "done line" frontier to enabled reward nodes, ordered by value-per-hop (weight / (distance+1)), with hop counts and a pulsing target frame. Caps the number of paths shown.
- **Mine layout view** — draws every real connection between explored nodes, including those behind the fog (green = node↔node, yellow = corridor through an empty tile).
- **Hidden passageways** — gold dashed lines from fractured-wall (Obstruction) cells to the valid nodes they actually connect to, revealing takeable hidden paths.
- **Tier support** — Azurite and Chambers have real tiers (e.g. Azurite T1/T2/T3) with independent show + weight per tier.
- **Exclusive fossil labels** — the 6 exclusive-fossil nodes show their specific fossil name (e.g. *Hollow*, *Faceted*) instead of generic "Fossils".
- **Stats panel** — docked panel with node-counts-by-reward, node state counts (completed / open / hidden-path-likely / isolated), and a biome summary with each biome's fossil pool.
- **Awareness comet** — a travelling white "snake" circles frames of high-weight nodes so the eye tracks them.
- **Debug dump** — exports the current mine (nodes, rewards, connections) to JSON for analysis.
