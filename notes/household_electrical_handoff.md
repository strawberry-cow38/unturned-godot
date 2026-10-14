# Household switch/outlet — frame-matched, seated-rocker revision

Owner feedback: `1559810962108977213` and `1559811297565220946`, October 14, 2026. Art-only revision of the already supplied two fixture assets. **Keep their GUIDs/registrations. No extra catalog fixtures, no gameplay code changes here.**

## Art changes

- **Power_Outlet_0**: rectangular backplate with square corners instead of the clipped/rounded eight-sided outline. Existing 0.32 m width, 0.54 m height, 0.054 m projection and both recessed three-hole receptacle faces remain. Now 87 geometric vertices / 162 triangles (previously 103 / 194).
- Both fixtures use the actual screenshot border/frame swatch: **House_09 Reveal, texel 2, sRGB `#ababab`**, from `game/content/wall_palettes.tsv`. `WallSurface.TrimTint` uses `WallMaterials.At(MaterialId).Reveal`; the screenshot's material browser reads `33 House_09`. This matches the source albedo rather than baking the warm lighting/shadow colour sampled from a screenshot into a texture. Body front and sides use the same swatch. Dark recesses remain charcoal.
- **Light_Switch_0**: rocker depth increased from 0.022 m to **0.040 m**, pivot moved inward from 0.055 m to **0.046 m**. The rocker now extends inside the 0.040 m-deep housing instead of sitting largely in front of it. The blind cavity back is at native Y=0.004 m; the rocker clears it in either pose. 28 geometric vertices / 46 triangles unchanged; overall depth approximately 0.085638 m.
- **Bottom pressed = ON, top pressed = OFF**. Revised static main OBJ/GLB is staged ON. Local-X rotations are now **ON −8°, OFF +8°** in native coordinates and in the standard Godot conversion. These signs supersede the previous handoff.

## Mandatory component integration update

| Main fixture | Preserved GUID |
|---|---|
| Light_Switch_0 | `397a9d3cc7eb4a4291dc41ae40a8d12f` |
| Power_Outlet_0 | `6bc5342b8fe5438b98ad49f4b28ccc5c` |

For the movable rocker, use the revised **Base + Rocker instead of the complete static OBJ**, not over it:

- New pivot native **(0, 0.046, 0)** m; Godot **(0, 0, −0.046)** m.
- Rocker OBJ is local about its pivot. Do not translate its mesh vertices a second time.
- ON local X **−8°**, OFF **+8°**; bottom/top refer to the pressed-in edge, not the protruding edge.
- Component palette aliases are updated alongside both main palettes.
- GLB named child remains `Light_Switch_0_Rocker` with its new pivot/default pose.
- No new GUIDs for Base/Rocker helper meshes.

Main native models remain metres, +Z-up, +Y-front, back Y=0, centred in X/Z. No geometry behind the wall. Standard Godot conversion is Y-up/front −Z.

## Behavior / placement handoff to Tinyclaw

Tiny reported working room-light switching, animated rocker and 1500 W outlet nodes in `1559809954263859250`. This artifact supplies revised art, **not an independent gameplay/network/save verification**.

- Support two switches controlling **one shared room-light circuit**. Tiny owns the interaction and synchronized/persisted control semantics; the art does not choose circuit logic.
- Raise switch placement slightly. Manifest suggests **1.55 m** rather than the old 1.4 m; Tiny chooses the final height with the current room/door clearance rules.
- Owner asks for wire-I/O on **both visible receptacles**. Tiny acknowledged in `1559811522354487379` that only the upper currently has a port and that the lower is being added. **Both share one outlet's 1500 W total**, not 1500 W each.
- Socket connection positions unchanged: native `(0, 0.054, ±0.13)` m; Godot `(0, ±0.13, −0.054)` m.
- Keep window/fireplace/door-sweep/furniture access exclusions. This art revision changes no solver, room identity, networking, save format or placement code.

## Review and delivery

Ten actual standalone Godot captures: both old fixtures front/angle, both revised fixtures front/angle, plus revised switch's other front/angle poses. Four final fixture views and all four revised rocker pose views were visually inspected through the real private Blender viewport. The comparison strip beside each fixture has the same `#ababab` source albedo as the screenshot's window/door frame.

**No test suites run.** Art generation, engine renders and visual inspection only. The source was restored after viewing temporary image cards: `Household_Electrical_Fixtures_Revision.blend` contains just one switch root, Base, Rocker and outlet, with two packed palettes, and no review cards.

Bundle branch `Staging-Astra-Household-Electrical-Revision` contains one art revision commit based on `862306c7131ab2a71efd7b8cc45cf4f8fed5249f`. Only the original eight model/texture files plus the two original notes/manifest files change. Tinyclaw owns publication/cherry-picking and integration; this handoff does not claim a remote push.