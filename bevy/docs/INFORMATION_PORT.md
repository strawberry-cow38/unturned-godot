# Information dashboard

The native Information screen ports `game/MapUI.cs`, with the accepted quest panel from `QuestLogPanel` and the original `MenuNavbar` labels. The map, local annotations and roster are presentation only. Opening the screen sends no world mutation, and quest pinning does not change the accepted quest state.

## Data and interaction

PEI, Washington and Yukon use their original extracted overview images and town TSVs. All three maps cover 1920 metres, including Washington despite its 4096 metre landscape. TSV positions already use engine coordinates; native projection retains Z. PEI uses its baked overview when available; `UG_MAPRETAIL=1` selects the extracted retail overview. No image is generated or substituted for missing content.

M opens/closes Information. Inventory and Craft can switch directly to it; its navbar uses live binding labels. Escape closes it. The Skills tab is visibly disabled until the original skills screen is ported. Inventory/Crafting still retain their earlier layouts; the unified four-tab styling and transition animation are not yet applied to every dashboard.

The map preserves source 1–8x cursor-centered wheel zoom, LMB drag, bounded pan and RMB marker placement/removal within 14 screen pixels. Markers retain source normalized coordinates and serial names across close/reopen, then clear on session/map replacement. Source cyan arrow/gold pin polygons and three-pixel centroid-grown halos keep their screen size under zoom. The actual camera forward vector determines map facing, including free-look and seats.

At 2x and above, original 4x4 baked JPEG tiles load only for the visible window plus a quarter-tile margin. Leaving that window drops their strong handles; zooming below 2x drops all tiles. The overview remains underneath asynchronous loads. Tile mipmaps reuse the existing runtime RGBA box-chain helper.

Labels use the shared original OpenSans font and measured native text extents. Placement is one greedy pass in original priority order: user markers, live players, then towns. Labels flip left at the right edge and collide with a three-pixel margin; their pips remain visible when labels are suppressed. Text outline rendering is still a visual fidelity gap; the source dark pill is present.

The roster refreshes every 0.4 seconds from accepted sanitized profile names and actual local/remote rendered body positions, with source X/Z and distance formats. It does not create remote players or supply authority metadata. The shared NPC read model supplies original quest names, statuses, objective text, undiscovered counts and pin/tracker behavior. No second Rust quest-rule implementation exists.

`InfiniteWorld.cs:94` explicitly omits MapUI because PEI is not its world. The native Procedural/infinite mode preserves that boundary. The separate finite generated-island overview is pending its original generator, not represented by the PEI chart.

## Validation

Nine focused map tests pass. They cover actual retail node counts/coordinates, camera-facing projection, cursor-preserving zoom and clamps, marker hit distance, bounded tile residency, label priority/edge placement, dashboard key navigation, real UI close/reopen/session teardown and in-place map replacement. An actual AssetServer decodes the original overview/JPEG tiles, generates mipmaps and releases tile handles. The shared quest observer has an Information-screen pin regression proving no accepted quest mutation or outgoing command. The broader UI suite passed 38 tests before the final two focused regressions.

A real PEI listen session and separate UDP client used immutable `npc-view-53` on both sides. The final Vulkan/lavapipe captures were opened: overview and 15placed labels, 5.23x detailed tiles, a normalized M1 pin, panning and exact close/reopen retention, accepted two-player roster with measured 5 m separation, Research Recovery 124 from Medic, local quest pin/tracker, marker removal, and zoom-out dropping all six resident tiles. Actual mouse/navbar and G/M keys navigated Craft/Inventory/Information; Pause/Quit exited 0. The sidecars record viewport, pan, norms, loaded/resident tiles and accepted roster.

Artifacts under `bevy/artifacts/` include `bevy-information-{overview,zoom-pin,panned,reopened,quest,quest-pinned,marker-removed,zoom-reset,to-craft,to-inventory,navigation-return,pinned-tracker}.png` and matching JSON. Independent source review and the opened overview/zoom images found no blocker. These are rendered functional checks, not pixel goldens.

The first render caught two real defects: absolute paths were rejected by the configured AssetServer, and the original58% quest split hid roster rows behind the native HUD at 720p. Both were corrected and regression-tested before the final capture. A subsequent real X11 wheel event had magnitude 2; source Godot responds to wheel direction only. Native now uses exactly 1.18 per event regardless line/pixel magnitude, guarded by the actual input system regression. The frozen rendered binary predates that final one-line wheel gain correction; its screenshots validate the resulting geometry and interaction states rather than per-notch gain.

## Remaining presentation

The original backdrop blur and MenuSwoop transitions are integrated through the shared adapters documented in `MODAL_BACKDROP_PORT.md` and `MENU_SWOOP_PORT.md`. The navigation capture also exposes pre-existing blank names in some crafting catalog rows; that separate crafting issue is not corrected by the map port. The first live 1280x720 capture exposed the source 58% split placing the roster behind the seven-row vitals. Native now caps that split only when necessary to retain two roster rows above the shared HUD boundary; normal 1080p source geometry remains unchanged and both cases have regressions. Shared navbar styling for Inventory/Craft, the Skills screen and finite-island map generation remain separate work. None of these gaps changes source world authority.
