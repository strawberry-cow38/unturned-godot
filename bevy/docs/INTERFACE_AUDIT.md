# Interface parity audit

Audit of the source branch on 2026-10-14. This is a migration inventory, not a claim that the listed features have been ported.

The checkout contains 661 game C# files (208,872 lines), 187 engine-independent core files (60,586 lines), 194 engine-independent test files (45,176 lines), and 316 in-engine test files containing 651 named tests. The supplied project summary predates substantial functionality. Current wire protocol is **61**, there are **18** replication systems, and inventory contains **22** pages, including twelve extra container compartment views.

## Preserve the existing authority boundary

`core/UnturnedNet/NetWorldHost.cs` owns the server and client. Preserve its command validation, inventory, combat, save format, and wire code rather than reconstructing rules in the renderer. Singleplayer must still run a server and a client over `MemTransport`. UI actions submit intents; the UI displays the client's replicated state. Keep server simulation at 50 Hz and replication last. A local physics owner has one body; remote players have render replicas.

The bridge needs simulation/environment adapters for combat rays, ground height, zombie navigation/line-of-sight, vehicle exits, water, environmental temperature, power/fluid networks, NPC catalogs, and world-content registration. Merely constructing `NetWorldServer` does not wire those game-side services. This is a specific integration checklist, not a reason to duplicate core rules.

`WorldSave.Capture`, `ApplyWorld`, `TryApplyPlayer`, `ToJson`, and `TryParse` already preserve world and player state. The format reads versions 1/2, writes 2, keeps offline players, and refuses a mismatched map. `game/WorldSaveDriver.cs` adds a 60-second autosave, `UG_SAVE_DIR`, error reporting and clock/season/loot-seed restoration. Preserve those semantics. If moving to NativeAOT later, `WorldSave.cs:1032` currently uses reflection-based `DefaultJsonTypeInfoResolver`; source-generated metadata must retain the custom omission of unset gun fields.

## Existing interface features

| Area | Source | Required behavior |
| --- | --- | --- |
| Main menu | `MainMenu*`, `MenuNavbar`, `MenuSwoop` | Play/map selection, advanced world options, direct connection validation, server query/browser/sorting/MOTD, offline mode, profile, settings and exit |
| Inventory | `inventory/InventoryUI`, `GridPanel`, `ItemTool` | Real-size grid items; rotation; drag/drop; split/merge; hotbar 1–10; clothing; 3D paperdoll; nearby items; multiple storage compartments; hold transfer; tooltips and item actions |
| Crafting | `inventory/CraftingMenu` | Catalog-derived recipes, ingredient/tool/skill/knowledge requirements, queue/cancel, selected item “uses” and “recipes” views |
| Skills | `inventory/SkillsUI`, `SkillTree.cs` | Skill graph/pillars, authoritative XP and upgrades, no local grant |
| HUD | `HUD`, `HitmarkerHUD` | Vitals, oxygen/temperature, stance, weapon/ammo/fire mode, reticle/ADS, damage/hit/kill feedback, driving/flying instruments, interaction prompts, death/respawn |
| Weapons/devices | `AttachmentMenu`, `ItemDevicePanel`, `MechanicsPanel`, `WalkiePanel`, `FluidElectricalPanel` | Attachment mounts and magazine management, tire/carjack actions, device filters and power/fluid interactions, radio controls |
| World screens | `MapUI`, `DialogueUI`, `TradeUI`, `QuestLogPanel` | Map markers, NPC dialogue choices, wallet-based trades and quest state |
| Communication | `ChatUI`, `DevConsole` | Authoritative chat and moderation, history/completion, server console commands/results, avatars, idle fade |
| Editor | `Editor*Panel`, `NpcEditorUI` | Terrain, roads, buildings, spawns and NPC authoring; these are real existing features, not optional substitutes for gameplay |
| Pause/options | `PauseMenu`, `GraphicsPanel`, `GraphicsOptions`, `ControlsOptions`, `KeybindMenu` | Resume, save/leave, graphics configuration, mouse/FOV/inversion, independent helicopter/plane settings, key capture/conflict handling |
| Reports | `BugReport*`, `core/BugReportCore` | Screenshot/context/build capture and configured report submission |
| Launcher | `launcher/MainWindow.cs`, `AuthClient.cs` | Branch selection, update/build/run, signed Steam identity/keypair flow, offline mode, retail-data selection, report key, logs, launcher self-update |

Bindings are persisted by **action name**, not enum ordinal, and keyboard bindings are physical positions. `Keybinds.cs` defines on-foot/driving/building/bag conflict contexts, inert unbound actions, mouse-button bindings and ten contiguous hotbar slots. Runtime input gating must implement context exclusivity; metadata alone does not prevent double firing. UI consumes input before gameplay.

The existing launcher currently downloads Godot, builds `game/UnturnedGodot.sln`, imports resources, and launches the Godot binary. It needs an explicit Bevy build/runtime path before claiming launcher parity. Preserve `UG_UNTURNED_DIR`, `UG_USERNAME`, `UG_PROFILE_PNG`, `UG_AUTH_TOKEN`, `UG_AUTH_KEY`, `UG_OFFLINE`, and `UG_BUGREPORT_KEY`; do not print credentials. Do not publish a replacement launcher release as part of local migration work.

## Verification gates

`./test.sh` actually defaults to L0 only. `./test.sh --all` runs L0 and Godot L1. Source documentation contains an older contradictory description. Keep existing L0 tests, and port behavior tests to Bevy as each presentation seam lands. A passing core test does not prove the corresponding Bevy UI/physics/replica wiring exists.

High-risk acceptance cases from the existing suite:

1. `net.loopback_join_move`, `net.dropin_dropout`, `net.client_world_views`: boot, retire entities, reconnect without stale state.
2. `net.shell_pickup_item`, `net.shell_pickup_denied_stays`, `net.shell_move_item`, `net.shared_container`: accepted and rejected actions actually update the visible item; simultaneous storage viewers see the same result.
3. `net.shell_fire_zombie`, reload-refund and attachment-dupe loopback tests: weapons spend the authoritative ammo/item once, show impacts and dead entities, preserve gun state through inventory moves.
4. `net.shell_wan_*`, `net.shell_outage_recov`, `net.shell_drive_recov`, `net.shell_drive_handoff`: jitter, stair/doorway collision, owner correction, vehicle ownership/seat changes.
5. `unify.*`, door/bed/deadzone tests: identical SP/remote intent path, one host body, late-join door state, ownership, respawn teleport, environmental damage.
6. WorldSave round-trip/size tests: full world restoration, offline-player retention, save migration, malformed/mismatched save rejection, compact unset gun fields.
7. `keybind.*`, inventory drag/hold/wheel tests, `chat.*`, `menu.*`: modal input consumption, Unicode text, conflict contexts, persisted settings, scrolling, connection validation.
8. Existing reliable-channel tests: ordered delivery under loss/reordering/duplication, 100 kB fragmentation and oversized-message refusal. Preserve byte-format tests when retaining the wire protocol.

Visual verification remains manual render → inspect PNG, with no golden-image gate. Render menu, HUD, inventory with actual items/compartments, crafting, chat/console, pause/settings, death, vehicle and first/third-person views. Verify readable contrast, clipping at smaller resolutions, correct atlas UVs and cursor capture. Maintain an explicit list of pending screens until each is wired and visually reviewed.

## Implemented native interface slice

The Bevy interface now includes map selection/menu, HUD vitals/ammo/notices, the replicated 22-page inventory grid with real icons and rotation/move/drop/consume/equip intents, catalog-derived crafting, console/chat, pause/save/load, persisted mouse/FOV/audio/vsync controls and contextual key bindings. Death is a modal view driven by the replicated `player.alive`; respawn submits the existing host's authoritative respawn request and waits for the replicated revival. The source server's automatic respawn clock remains intact. This slice does **not** imply parity for the other rows above.

A visual run caught that Bevy's default camera selection includes inactive cameras: the inactive first-person camera absorbed the menu UI. A dedicated `Camera2d` with `IsDefaultUiCamera` now renders the interface after the world and gun passes; the regression test exercises Bevy's real default-camera selector. Button tests verify that actions leave replicated inventory untouched, and death tests verify that Escape cannot bypass the modal or revive the player locally.

The native Appearance panel now uses the original 32 face assets and persists a
local choice. Original `UG_USERNAME`, `UG_PROFILE_PNG` and `UG_FACE` settings flow
through the existing accepted profile command for integrated and UDP sessions;
saved Godot face preferences import once. See [PROFILE_PORT.md](PROFILE_PORT.md)
for authority, validation and remaining menu/launcher boundaries.

The later player-camera journey exposed an incorrect generic first-person `+` glyph. `ui/crosshair.rs` replaces it with the current source's four 2x9px arms and 3px center dot, each with a 1.5px black halo. Their geometry is anchored about exact screen center and only shown for an alive on-foot third-person view (including prop seats); normal first-person and vehicle views hide it. The source 12/s gap tween consumes real `Spread_Aim`, owner speed and the existing unapplied pitch recoil, without a second shot accumulator. Two focused tests cover the actual UI system's camera/seat/death transitions and the source geometry/bloom thresholds. The mounted integration now consumes the accepted operator and configured Aim input for the source cannon exception, suppressing it under the logical gunsight. Its regression covers driver first/third person, gunner priority/head-out, modal input, death and hidden-optic diagnostics. The original tank periscope and8→12→4 gunsight have six opened render fixtures; see [TANK_OPTICS_PORT.md](TANK_OPTICS_PORT.md).

NPC dialogue, vendor and touched-quest panels are now driven by the accepted
managed read model. Native page/response controls preserve original indices,
trades submit server intents, and pinning remains local presentation. Actual
keyboard/mouse captures verified conditional choices, two pages, dollar buy/sell,
independent scrolling, accepted quest grant and tracker/log/pin. Six focused
native tests and peer code/image review pass. The quest log now shares the original Information/map dashboard. See
[NPC_UI_PORT.md](NPC_UI_PORT.md) for artifacts and the source quest 9/content and
quest-persistence/progress gaps.

The native Information screen now includes the original PEI/Washington/Yukon
overviews and town positions, source map zoom/pan/markers, bounded tile residency,
accepted player roster and shared quest pin/tracker. Real listen/UDP navigation
and opened captures passed; nine map regressions cover source geometry, original
assets, input and lifecycle. Skills and dashboard blur/transition effects now have native replacements with
reviewed source renders; see [SKILLS_PORT.md](SKILLS_PORT.md),
[MODAL_BACKDROP_PORT.md](MODAL_BACKDROP_PORT.md) and
[MENU_SWOOP_PORT.md](MENU_SWOOP_PORT.md). Finite generated-island charts remain pending. See [INFORMATION_PORT.md](INFORMATION_PORT.md).

Accepted damage and blast events now drive the original local camera spring,
including the existing accepted gun impulse in third person. Actual camera/ray
ordering, collision and lifecycle regressions plus a rendered kick/recovery
sequence passed. Accepted local hurt events now also drive the source directional wedges and
pain overlay; low HP applies the original vignette/desaturation at the correct
canvas boundary. Opened playable-world captures cover healthy, hurt, low HP,
inventory/equipment, death and respawn. See
[CAMERA_FLINCH_PORT.md](CAMERA_FLINCH_PORT.md),
[HURT_FEEDBACK_PORT.md](HURT_FEEDBACK_PORT.md) and
[LOW_HEALTH_PORT.md](LOW_HEALTH_PORT.md).

The original Crafting category/grid/search/detail/accepted queue dashboard now
has native tests, eight opened Vulkan stages and a real X11 playable PEI
craft/cancel/refund journey. Search/selection survive reopen and queue refresh.
Station authority and remaining LineEdit affordances are explicit gaps in
[CRAFTING_PORT.md](CRAFTING_PORT.md); Inventory's replacement is separate.
