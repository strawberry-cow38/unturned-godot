# Plate-glass storefront doors

Requested by strawberry on 2026-10-19; branch-only work, not automatically merged or enabled in building generators.

## Use

The Buildings opening panel's existing DOOR dropdown now offers `plate glass — aluminium` and `plate glass — bronze`. Keep the existing floor-pinned door/storefront opening archetype. The choices are leaf styles, not new opening archetypes or decorative props.

Generator API: set `WallOpening.DoorProp = "Door_PlateGlass"` or `"Door_PlateGlass_Bronze"`. WallSurface automatically marks these openings glazed, so existing glass repair/smash/tint/HP/indestructible/save controls work. No change to BuildingGenStrip is included; its author can select these names for storefront entrances once this change is integrated. Ordinary wooden/metal doors are unchanged.

Selecting a plate door supplies its intrinsic pane. The general glazing checkbox is disabled for these leaves, with an explanation; smash/repair and glass options remain available. Removing/replacing a plate door clears its intrinsic glazing rather than leaving a stationary glass sheet across the now-open doorway. Door selection, removal and repair use the existing undo path.

`DoorDeploy.SpawnProp` also accepts these names with its existing optional `fit`, yaw and reverse arguments. No new inventory item ID, global prop registration or default generator replacement is introduced.

## Art and implementation

Both frame models were authored in the instance-private Blender GUI and visually inspected there. Their native authored frame matches the existing door kit: X width, Z height, Y thickness; DoorDeploy stands the rigid assembly up by 270 degrees about X. Native leaf size is 2.45 by 2.80, with broad square stiles/top rail, kick rail, square exterior pull, interior push bar and two hinge blocks. Silver and dark bronze flat-palette finishes; no bevels, tiny screws, painted reflections or glass-textured solid backing. Fitting scales geometry, pane and hinge together, not an ancestor node.

The actual glass is `GlassPane.Build`, exactly the class existing windows use: one-shot default health, the existing rain-on-glass material, existing break sound, existing particle shards and existing jagged remnant meshes. A pane and its remnants are attached to the actual ObjectDoor hinge. Interior/exterior shelter follows the existing wall thickness convention. The pane carries the existing objectdoor metadata so looking at the clear panel resolves the same F interaction as looking at its frame.

Frame collision is twelve separate model-component boxes. There is deliberately no full-leaf rectangular collider across the pane: a shot reaches GlassPane, and after it breaks the clear parts really are clear to subsequent rays. The solid frame/push bar remain. Both glass and frame follow the existing non-solid-mid-swing and occupant-clearance policy. ObjectDoor's existing motion, interaction and replication registration are reused. This does not introduce a new glass networking protocol or claim changes to existing window glass replication.

The opening's existing GlassBroken flag is written synchronously when its pane shatters, before GlassPane frees itself. The established WallSave format keeps the name, options and broken state. Rebuild, toggle and save/reload do not silently repair it. No rebuild occurs inside the shatter event, so the original shatter effect still has its scene and global transform.

Editable source: `tools/art/plate_glass_doors/Plate_Glass_Doors.blend` and `build_doors.py`. The .blend includes hidden, explicitly preview-only pane placeholders and an unchanged metal-door style reference; neither is part of the frame OBJ export. Runtime models, palette PNGs and component collider metadata are under game/content/objects. To rerun the authoring script in the private Blender workspace, place it under `/workspace/plate_glass_doors_20261019` and copy the existing Door_Metal.obj into that directory's reference subdirectory.

## Verification

The .NET game build succeeds. Targeted L1 run: 16 behavioural tests, 218 checks, all pass (three new plate-glass tests plus thirteen existing door/window regressions). The separate optional render test passes when an output prefix is supplied.

New checks cover both finishes; real upper-panel bullet-ray hits; actual frame collision; no duplicate fixed pane; glass attached to the moving leaf; rebuilding an open door without moving it; synchronous broken state; a genuinely clear shot-out panel; surviving frame collision; no repair on toggle/rebuild; save and reconstruction of broken glazing; explicit repair, configured HP and indestructibility; all four hinge/face combinations; existing swing/double-swing/pocket motion; non-solid glass/frame mid-swing; safe closure around an occupant; and editor selection/removal/repair/undo.

Reproduce behavioural tests with the game's usual Godot executable:

```
dotnet build game/UnturnedGodot.csproj -m:1
godot --headless --path game -- --tests='plate_glass.*,buildtool.*door*,buildtool.glass*,buildtool.broken_glass*'
```

For captures, set UG_PLATEGLASS_CAPTURE to an absolute output prefix in an existing directory, run Godot with a real display and `--tests=plate_glass.render_shopfront`. The render helper waits for real FramePostDraw events rather than assuming physics ticks imply a new picture. Attached captures are actual Godot Compatibility-renderer images, not painted previews; intact, detail and open/broken views were visually inspected. A Forward+ software-rendered preview also completed, but hit the existing separate-render-thread shutdown warning; a safe-thread Forward+ retry exceeded the local command time limit. Compatibility captures completed with no engine errors (the existing unsupported screen-space-AA warning remains). These are in-engine geometry/physics/render tests, not a claim of manual human playtesting or 4080 GPU verification.