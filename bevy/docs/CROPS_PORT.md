# Crops port

Sources:
- `game/CropNode.cs`: `CropRegistry`, the dirt base, foliage billboards and `SetGrown`.
- `game/CropReplicaView.cs`: diff-driven materialization with a tick-derived stage.
- `game/CropManager.cs`: `Plant`, `Harvest`, `NearestGrown` and `UG_FARMSPEED`.
- `game/WorldNetSync.cs` (`CropNetSync`): host mint and force-mature.
- `game/PlayerController.cs`: `RequestHarvestNearestCrop` and its place in the F chain.
- `game/DevConsole.cs`: the `plant` verb.
- `core/UnturnedNet/ServerTransactions.cs`: `PlantCrop`, `OnPlantCrop`, `OnHarvestCrop`, `CropReach`.

## Authority

Crops were already server-owned in core:
- Commands 24/25 are validated against the sender's authoritative position (`CropReach` 6 m).
- Planting spends a seed.
- Harvest requires tick-derived maturity. It spawns the `Grow` yield as a replicated world item (plus the AGRICULTURE second roll) and awards 1 XP.
- `WorldSave` keeps crops by age.

The bridge exposes the rows as `crops` (`grown = IsGrown(e, LastAppliedServerTick)`). The native `harvestCrop` and `plantCrop` actions map to `NetWorldClient.SendHarvestCrop`/`SendPlantCrop`. No wire ids or bytes change.

`bevy/bridge/CropConsole.cs` ports the console verb `plant <crop> [grown]` at `PlayerController.LookPoint`. The source resolves that point on the planting client, so the native `crops::console` intercept sends its own camera ray (2.6 m against world/vehicles/props, groups 1|4, else the ray end) as `at` with the text, for host and joined clients alike. The bridge's eye ray against its geometry is only a fallback for callers without `at`.
- **Listen host:** plants directly, as its `CropManager` + `CropNetSync` do, behind the same `AllowCheats` gate as every other host console verb in the bridge (`kill`, `sethp`, `weather`). Without the gate, a cheats-locked listen host would hand out free yields.
- **Joined client** (no `CropManager`): resolves the seed and sends `PlantCrop`, which the server refuses without a seed in the bag.
- **Messages:** taken verbatim from source, including the "E near a grown crop" wording and the bare-verb arg-guard message.

`UG_FARMSPEED` (a development aid) force-grows a host crop once its scaled age passes its growth time, as `CropNetSync` does for the host's faster `CropManager` clock.

## Native

`bevy/src/entities/crops.rs` reads `content/crops.tsv` (name, seed, dirt `_Color` with the source fallback 0.4/0.32/0.2) and keeps one entity per replicated row:
- **Dirt:** `crop_<name>_Model_0` in the dirt colour, roughness 1, double-sided.
- **Foliage:** `Foliage_0`/`Foliage_1`, alpha-scissor 0.4, double-sided, roughness 1, stood up −90° about X. Their textures use Godot `NearestWithMipmaps`: nearest texels, the source box mip chain, linear between levels (`ContentCache::nearest_mipmapped`).
- **Stage:** follows the row's `grown`. Unknown seeds are never materialized, and rows that disappear despawn their crop.

Harvest is `RequestHarvestNearestCrop`. When F is pressed it targets the nearest grown crop strictly within 3 m of the player origin.
- It has no prompt.
- It sits below every look target (seats, beds, doors, NPCs, items, vehicles) and above the shelf/crate branches. `InteractionFocus::fall_through` fills an empty focus or takes over a candidate offered with `open_storage`; the storage label stays on screen, as the source's would.
- It is skipped while UI captures input, while seated or in a vehicle, and while dead.
- ALT-look (FreeLook held) interacts with nothing, as in `PlayerController`. The generic `apply_interaction` and the inspect fallback now honour this for every target.
- The F press is only an intent: the crop leaves when the server retires it.

## Deviations

- **Planting UX:** the source has no planting UX besides the console verb, so neither does the port.
- **Look point:** the native ray filter (groups 1|4, no sensors, not the player) approximates the source mask bits 0/5/6, and may also hit other dynamic bodies.
- **Storage deployables:** a looked-at storage deployable keeps the deployable focus path (`_focusDeployable`, above harvest). Only the proximity `OpenNearestCrate` ranks below harvest and should offer with `open_storage` (ledger row 2).
- **Proximity vehicle enter on a joined client:** the source's `RequestEnterNearestPuppet` (4 m proximity enter, above harvest) has no native equivalent; native vehicle access is look-only. This is a pre-existing gap, recorded in ledger row 15.
- **`UG_FARMSPEED`:** a dev aid, force-growing at the source's 2 Hz. It accelerates save-loaded crops from their saved age, and does nothing for a speed ≤ 1.

## Verification

- **Native (`entities::crops`):**
  - crops.tsv parsing and its fallbacks;
  - the strict 3 m nearest-grown scan;
  - `fall_through` ordering (empty focus, storage taken over with its label kept, other targets win), plus a schedule test through the production `install_harvest` (a storage look target yields F, a seat keeps it);
  - the look point (first hit within 2.6 m, else the ray end);
  - an integrated test against a real local bridge. Console `plant carrot` lands at the 2.6 m look-point ray end with only its young parts visible. F next to a young crop offers nothing. `plant corn grown` shows the grown parts with no prompt. Alt+F harvests nothing. F is only an intent until the authority retires the crop, and the corn yield (335) drops. Bare/unknown verbs echo the source text.
- **Managed (`CropTests`, real UDP):** a joined client's `plant` without a seed is refused. Given a seed it plants and spends it. Unripe and out-of-reach harvests are refused, and an in-reach grown harvest retires the crop and replicates the yield. A supplied `at` is where the crop lands, and a cheats-locked listen host refuses `plant`.
- **Negative controls, each failing as intended:** storage fall-through removed, grown gate removed, young/grown visibility swapped, host and remote console wiring each removed, cheats gate removed, `at` ignored, Alt-look gate removed, and harvest scheduled before `CollectInteractions`.
- **Visual:** the ignored fixture `render_source_crop_showcase_young_left_grown_right` (`UG_CROP`, `UG_CROP_SHOT`) reproduces `Main.BuildCropTest`'s scene through `crops::sync`. Beside the Godot 4.6.2 oracle (`--croptest=carrot`), the dirt ridges, the grown billboard's shape, the alpha cutout and the orientation match; only ambient/light levels differ. The young carrot sprout (`Foliage_0`, 15 opaque texels of 256) is barely visible in both. Renders: `bevy/artifacts/{bevy,godot}-crop-showcase-carrot.png`.
