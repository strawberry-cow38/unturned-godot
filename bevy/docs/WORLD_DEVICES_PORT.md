# World devices port (ledger row 20, part 1)

Sources:
- `game/WorldBuilder.cs`: `PlaceObject`'s deployable-lamp branch (~line 999), the
  indoor `LampLight` branch (~1436), `PlaceNote` (~1955) and the device metas on
  the prop body (~1737-1754).
- `game/LampLight.cs`, `game/GridLight.cs`, `game/ObjMesh.cs` (`SplitLens`,
  `SplitByUvCentroid`, `SplitByFace`, `CapHoles`).
- `game/Deployable.cs` (`WorldScenery`, the `_fixtureLamp`, the look-at
  billboard), `game/DeployableDef.cs` (`DeskLamp` 1918, `StandingLamp` 1255,
  `PropFixture`).
- `game/NoteBody.cs` (`NoteTexts`, `NoteBody`, `NoteReader`).
- `game/PowerNet.cs` (`MainsLive`), `game/DayNightCycle.cs` (`MainsLive`,
  `DriveStreetlights`).
- `game/PlayerController.cs`: `UpdateLookFocus` (2.6 m `LookReach`, the meta
  chain) and the F chain (~8960-9060).

## Authority

None of these devices is replicated in the source. Every branch above runs only
when `mode != WorldMode.Dedicated`; there is no command, event or snapshot field
for a lamp switch or a note. Their state is per-client and the port keeps it so:
`world::devices::DeviceState` holds the manual lamp switches by placement index,
which survives a region re-stream (source nodes never stream).

The one shared input is the mains. `PowerNet.MainsLive` on the replicated path
believes the GridSource breakers when the world has any and falls back to the
default-on global flag when it has none. `deployables::mains_live` reads exactly
that from the accepted `deployables` rows (the Circuit_0 fixtures the bridge
registers through `MapServices`), and `world::devices::Mains` re-reads it at the
`DayNightCycle.MainsLive` 4 Hz throttle. The console `toggleglobalpower` therefore
reaches the lamps only through the server's accepted fixture bits.

## Lamps

What the player meets on PEI (`placements.txt`): 12 `Light_0` ceiling strips,
13 `Lamp_0` desk lamps and 27 `Lamp_1` standing lamps. 39 of the 40 desk/standing
lamps are placed upright (ex 270, ez 0); one `Lamp_1` (row 2601) is tilted.

- **Upright desk/standing lamp** (`Deployable.Spawn` with `WorldScenery`). The
  source spawns the same body a player would deploy, but with no NetId. Its
  fixture lamp is fed only by its own consumer port, which no wire can reach
  over the server (no NetId), so it is dark. Natively it is the world prop with
  the deployable look focus: the `Deployable.OutlineColor` rim (0.82/0.83/0.90)
  and the look-at billboard with the def name ("Desk Lamp"/"Lamp"), a full HP
  bar (no backing item), no fuel or load bar and no prompt (no toggle, and
  `WorldScenery && NetId == 0` offers no pickup). F does nothing, as in the
  source, where the hold is cancelled at once on a consuming client.
- **Tilted desk/standing lamp and the ceiling strips** (`LampLight`, grid-fed):
  - Kinds (`KindFor`): `Light_0` CeilingStrip, `Lamp_1` FloorShade, `Lamp_0`
    DeskBulb. Only FloorShade and DeskBulb are toggles (`IsToggle`).
  - Emitter split: CeilingStrip takes triangles whose three UVs sit in the
    u>0.5, v>0.5 quadrant and caps the lens's open boundary loops (`CapHoles`,
    keys rounded to 1e-4, ties to even, the .NET insertion order). FloorShade
    takes UV centroids u<0.5, v>0.5. DeskBulb takes faces on the head (centroid
    z>0.6) facing down and to +X. The prop keeps its body; the emitter is a child
    mesh that swaps to a lit copy of the prop material with emission
    `BulbColor x 0.9 x worn`.
  - Light: one shadowless point light, range 8, `BulbColor` (1, 0.90, 0.72),
    energy `2.2 x worn` (desk lamp: half), Godot falloff 1 (`SourceFalloff`).
    Its position is `ComputeLightLocal`: 0.9 m under the strip body's lowest
    world corner, at the shade's centre, or 0.18 m in front of the desk bulb face
    along its averaged world normal.
  - `GridLight`: `InitJitter` hashes the absolute world position for the wear
    (0.95-1.05) and the reaction fraction. A mains change animates: a reaction
    delay of `2.2 x (0.15 + 0.85 x frac)` seconds with an irregular 8-25 Hz
    stutter whose odds ramp to the new state, then a settle. The effective state
    is grid-lit AND the manual switch; the `dynlight` membership (`WorldLight`)
    follows the grid alone, as `SyncDynGroup` does.
  - Focus: a toggle lamp offers a silent candidate on the 2.6 m look ray and
    claims its whole-lamp outline in white. F flips its own switch only; grid
    power is still required to emit. The ceiling strip has no look target.
  - Ceiling-strip hum: `fluorescent_hum.wav` looping at 0.35 linear, unit size
    2.2, max distance 13, hard-muted while dark. A voice exists only within 13 m.

## Notes

`NoteTexts` (`content/note_texts.tsv`, GUID -> name and lines) turns a placement
into a `NoteBody`: its collider becomes a box padded to 0.05 m on each axis, on
the source's see-through look layer (bit 6: native GROUP4 plus the GROUP17
source-prop marker). Focus is silent with a white outline. F opens the reader:
the title drops "Note - ", the body joins the lines with newlines. F or Esc
closes it (a new `Screen::Note`, so world input is captured while it is open).

The reader is the source panel under the 2560x1440 canvas stretch: a 0.78 black
dim, the paper (0.93, 0.90, 0.82) with a 2 px (0.2, 0.18, 0.13) border and 26 px
content margins, a 560 px column 12 px apart with the 24 px centred title, the
separator, the 17 px wrapped body and the 12 px hint, at
`y = max(40, (h - panel) / 2)`. Label line height is the Open Sans ascent plus
descent plus the theme's 3 px spacing.

PEI's four note GUIDs have no extracted mesh (absent from `guid_mesh.txt`), so
the source skips them and PEI has no readable notes. Washington places eight.

## Deviations and gaps

- **Not ported (unreachable or out of scope):** `RoomLightSwitch` exists only in
  generated buildings (`EditorBuildings`), whose installed runtime is ledger
  row 19; its data is already extracted (`BuildingFurniture`). The CeilingBulb
  pendants exist only as deployables (the kind is in the table; their glass
  material is not built here). Finite maps place props through `EditorObjects`,
  which has its own path; the devices attach only to retail placements.
- **Not ported (cosmetic):** `LightShadowBudget` (lamps stay shadowless; the
  source turns shadows on for the nearest few), the brownout/blackout schedule
  (`TriggerGlobalBrownout`, `DriveBlackout`), prop destruction and the shot-out
  bulb, the CeilingStrip `CeilingSpot`/decal experiments (off by default in the
  source), `VisibilityRange` culling of the emitter, and the hum's random seek.
- **Cap winding:** the native OBJ loader orders each triangle's corners to agree
  with its authored normal, so a cap's fan may face the other way from Godot's;
  the cap only shows on the emissive lens, and prop materials are double-sided.
- **Reader panel height:** in the Godot oracle the paper keeps the taller
  minimum size of its first autowrap pass, leaving empty paper under the hint
  and placing the panel higher. The port sizes the paper to its content.
- **Godot `--lamptest` outline:** the harness shows the white silhouette itself
  (no mask pass), so it is not an outline oracle. The native rim is the shared
  `OutlineOverlay` port, verified in `OUTLINE_PORT.md`.

## Verification

- **Native (`world::devices`, 11 + 2 ignored GPU fixtures):** the kind table and
  toggles; the exact ex 270/ez 0 rule (PEI row 2601 stays a prop); the jitter;
  the real `Light_0`/`Lamp_1`/`Lamp_0` emitter splits; `CapHoles` closing an
  open box with one quad; the grid reaction delay and its re-aim; the breaker
  rule for the mains; a Rapier schedule test where the look ray picks the tilted
  lamp, F toggles only it, the accepted breakers going off darken every lamp
  after its delay and the switch cannot relight a dead grid; the upright desk
  lamp's scenery focus, billboard and inert F; and a note read on F, closed on F
  and by `close_overlay` (the path the Esc key takes; the key itself is not
  driven in the test). The full native suite passes (891, 0 failed).
- **Godot oracle (numeric):** `--lamptest UG_LAMP_POSE=1 UG_LAMP_PITCH=270`
  prints the omni pose and energy. The native `ComputeLightLocal` port matches
  all three within 1e-4 m (`omni_pose_and_energy_match_the_godot_lamptest`):
  Light_0 (0, 1.85, 0) 2.09, Lamp_1 (0, 1.988, 0) 2.09, Lamp_0
  (0.279, 0.515, 0) 1.045.
- **Negative controls, each failing as intended and restored byte-identical:**
  the upright rule removed, the mains ignored, the F focus gate removed, the
  note look box removed, F no longer closing the reader, and the strip's 0.9 m
  float removed.
- **Visual:** `render_source_lamptest_through_the_production_lamp_light`
  (`UG_LAMP`, `UG_LAMP_PITCH`, `UG_LAMP_OUTLINE`, `UG_LAMP_SHOT`) reproduces
  `Main.BuildLampTest` through `lamps::build` and `lamps::grid`. Beside the Godot
  4.6.2 `--lamptest` renders, the strip's diffuser, the standing lamp's shade and
  the desk lamp's head opening glow in the same places with the same warm pool;
  only ambient/background levels differ. The outline frame shows the white rim
  around the whole standing lamp. `render_source_note_reader_panel`
  (`UG_NOTE_SHOT`) beside the Godot oracle (`bevy/tools/NoteReaderOracle.cs`,
  the actual `NoteBody.Spawn` + `NoteReader.Show`): same paper width, wrap
  points and line pitch; see the height deviation above. Renders:
  `bevy/artifacts/{godot,bevy}-lamp-{light0,lamp1,lamp0}.png`,
  `bevy-lamp-lamp1-outline.png`, `{godot,bevy}-note-military1.png`.

## Checkpoint 2: vending machines, radios and patient monitors

Sources: `game/VendingMachine.cs`, `game/RadioDevice.cs`, `game/HeartMonitor.cs`,
`game/content/ecg.gdshader`, `game/MpLoopback.cs` (`NetVendPay`/`NetVendDrop`),
`PlayerController.VendPromptSet`/`CanVend`/`RequestVendPay`/`RequestVendDrop`,
`WeatherManager.ForecastLine`. PEI places 6 vending machines, 6 radios and 6
`Science_3` monitors.

### Authority

- **Radio and monitor:** client-local, as in the source. Their feed is
  `PowerNet.MainsLive || PlugPowered`; the plug is a `ConnectionPort` on a NetId-0
  fixture that no server wire can reach, so natively the feed is the accepted
  mains.
- **Vending:** the machine's state, shake and refusal flash are local. The
  purchase is the source's listen-host seam: `vendPay` spends one `Currency.StackId`
  dollar on the server inventory (with `ServerMarkDirty`), and `vendDrop` spawns
  the can as a replicated world item at the machine's tray when the shake ends.
  A joined client has no seam in the source (`CanVend` false, "Out of order"); the
  bridge refuses both actions for a remote session. No wire id was added. The
  bridge is stricter than the in-process seam: a drop must follow a paid vend
  and name one of the two drinks (cola 80, soda 465).

### Native

- **Vending** (`world::devices::vending`): `StateFor` order (dispensing, no power,
  out of order, no funds, ready); the wallet is the owner's own pages' 1056 amount;
  "can vend" is `owns_world`. The look focus is silent and claims the rim in the
  state colour, blinking red/white for 0.6 s after a refused press (not while
  dispensing). The look-at billboard (`VendPromptSet`) shows "Cola Machine"/"Soda
  Machine" in the livery colour and the state line at `PromptAnchor`. F on a
  ready machine asks the host to pay; then the 0.45 s decaying shudder off the
  rest pose, the `inv_heavy_01` clunk, a 1.0 s cooldown, and the can at
  `DispensePoint` on the tick the shake ends.
- **Radio** (`radio`): OFF by default, F flips it and clicks (`tv_on`/`tv_off`),
  the `tv_static` loop plays while on and fed (0.30, unit 1.2, max 6 m). The
  forecast board ("TODAY  x\nTOMORROW  y" from the accepted weather outlook,
  "Clear" unless wet) is a depth-tested camera-facing Label3D (pixel 0.0016, font
  44, outline 10, tint (0.62, 0.90, 1)) at the body centre plus
  (0, size.y / 2 + 0.22, 0) in the prop's own frame, shown only while on and fed.
  As in the source, that frame puts it in front of the set rather than above it.
- **Monitor** (`monitor`, `ecg.wesl`): the screen quad 4 mm in front of the
  modelled trace with UV (1 - u, v), the ECG shader ported line for line, the
  off panel in the prop's own (53, 53, 53). ON by default; lit while on and fed.
  One shared clock drives every monitor; alive units beep (`ecg_beep.ogg`) as the
  sweep crosses R (phase 0.42), flatlined ones hold `ecg_flat.wav`
  (-21 dB, unit 7, max 26 m). Each unit is alive with chance 0.7, rolled per client
  as the source rolls it per world build.

### Deviations and gaps

- The fixtures' power plug cubes (`ConnectionPort`) are not drawn: they are
  unwireable over the server, and natively there is no local wire graph.
- Brownout sag, shot-out screens and prop destruction are not ported (as for
  the lamps). The flatline wav's `smpl` loop region is not honoured: the whole
  file loops.
- `ServerMarkDirty` after the spend is kept for parity, but its removal did not
  fail a test: the bridge's inventory replication echoes the spend regardless.

### Verification

- **Native (`world::devices::tests::appliances`, 7):** the vending state order;
  the tray and prompt anchor equal the Godot oracle's (`VendingOracle.cs`:
  anchor (0, 2.8, -0.572), tray (0, 0.30, -1.495)) within 1e-4 m; the forecast
  line; the monitor quad; a radio that starts off, plays only while switched on
  and fed and dies with the accepted breakers; a monitor whose ECG shows while on
  and fed; and a real-bridge journey: broke, the press only flashes and the
  billboard reads "Insufficient funds -- $1 cola"; with a dollar, F shudders the
  machine, the server takes exactly one dollar, the machine lands exactly home and
  the server's cola lies at the tray. Full native suite 898/0.
- **Managed (`VendingTests`, real UDP):** the host pays exactly one dollar and its
  can replicates to the joined peer at the tray; no can without a paid vend or for
  another item; one can per dollar; the joined client is refused both actions and
  keeps its money. Full managed suite 300/0.
- **Negative controls:** native: the state order (power vs seam), the radio's
  default, the monitor's mains gate, the refusal flash, the tray's 0.35 m; managed:
  the paid-vend guard. Each failed as intended and was restored byte-identical.
  The `ServerMarkDirty` removal passed (see above).
- **Visual** (`render_source_proptest_device`, `Main.BuildPropTest`): the alive
  monitor shows the same trace, amplitude and graticule as the Godot 4.6.2
  `--proptest=Science_3 UG_LIVE=1` render; the radio board reads "TODAY  Drizzle
  Rain / TOMORROW  Drizzle Rain" at the same place and size as the Godot day-7
  render; the vending billboard matches the Godot oracle's text, colour and
  placement, and the native frame adds the red no-funds rim. Renders:
  `bevy/artifacts/{godot,bevy}-{monitor-alive,radio-7,vending}.png`.
