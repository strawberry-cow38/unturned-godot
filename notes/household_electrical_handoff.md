# Household electrical fixtures — model and interaction handoff

Owner request `1559691911042957396`: switches for a room's ceiling lights; outlets acting as 1500 W power-I/O fixtures. Tinyclaw accepted **placement, PowerSolver, room-light interaction, networking and saved state** in `1559692182938587189`.

**This delta supplies the art and binding metadata, not that game-behaviour implementation.** The previews are actual engine-rendered models/poses, not a completed power or multiplayer test. Both main fixtures are review-pending/unregistered as of this art handoff.

## Main assets — preserve these two GUIDs

| Asset | GUID | Native W × H × projection | Triangles |
|---|---|---|---:|
| Light_Switch_0 | `397a9d3cc7eb4a4291dc41ae40a8d12f` | 0.32 × 0.54 × 0.085725 m | 46 |
| Power_Outlet_0 | `6bc5342b8fe5438b98ad49f4b28ccc5c` | 0.32 × 0.54 × 0.054 m | 194 |

Simple pale wall plates, flat shading and exact stock white/grey/charcoal swatches from Clock_0, Chair_Metal_0 and Counter_2. No lettering, tiny screws, photo textures or realistic micro-detail. Outlet openings are actual blind recesses, not overlapping black decals.

Both main native OBJ models: metres, +Z up, front **+Y**, back at **Y=0**, origin at the centre of the back face (X/Z centred). No geometry behind the wall. The standard GLB conversion is Godot Y-up/front −Z. The switch's reserved envelope is the same in both rocker poses.

## Requested behaviour — not passive wall décor

**Power_Outlet_0**
- Bind to the existing **PowerSolver/power-I/O** system with **1500 W per placed fixture**.
- The art has two visible receptacle faces. Do not silently create two extra 1500 W nodes from the faces; the requested model-instance rating is in `power_io_watts_per_fixture`.
- A power outlet is not a generator. Use the existing solver's supply/connection rules.
- Socket visual points: native `(0, 0.054, +0.13)` and `(0, 0.054, -0.13)` m. Godot equivalents are in the manifest.

**Light_Switch_0**
- A real interactable, bound to the fixture's **own room** and controlling that room's **ceiling-light fixtures** on/off.
- Do not use this as a whole-house toggle or include other rooms/table/floor lamps by accident.
- Tinyclaw owns the room key, interaction, power/light state coupling, networking and persistence.
- Synchronise the rocker pose with the requested switch state if using the separate component. The art does not decide the room's power-availability rules.

## Optional independently movable rocker

The complete `Light_Switch_0.obj` already contains its rocker in the default ON art pose. Use it as one mesh if a static visual is enough.

For a moving button, use **Light_Switch_0_Base.obj + Light_Switch_0_Rocker.obj**, **instead of** rendering a second rocker over the complete OBJ:
- Base uses the normal fixture origin/back Y=0.
- Rocker mesh is local around its own centre/pivot; it is **not a standalone wall-mount prop**.
- Put its pivot at native `(0, 0.055, 0)` m, or Godot `(0, 0, -0.055)` m.
- Local X rotation: ON **+8°**, OFF **−8°**. Source is staged ON.
- Both components use the main switch palette. Matching `_Base_tex.png`/`_Rocker_tex.png` aliases are also supplied for automatic filename-based texture lookup.
- GLB already contains the named child `Light_Switch_0_Rocker` at the correct pivot.
- **Do not mint GUIDs/register the component meshes as extra fixtures.** There are only two catalog props in this handoff.

## Placement metadata

The manifest includes mount, room rule, near/avoid, front access, clearances, suggested heights, interaction role and integration ownership.
- Switch: accessible wall by the room door, suggested origin height **1.4 m**. Clear door swings/windows/fireplace walls/stairs and tall furniture.
- Outlet: accessible low wall, suggested origin **0.4 m**. Near a desk/bedside/counter work area rather than hidden behind a wardrobe or fridge.
- Above-counter alternative: **1.67 m** origin, subject to the actual window/appliance clearance checks. This is a placement suggestion, not a forced height or exemption from the owner's window rules.
- Suggested unobstructed front access **0.8 m**, local edge margin **0.05 m**. Keep the current generator's stronger exclusions where applicable.
- These fixtures are **excluded from the parallel bulk passive-doodad catalog**, as agreed in Tinyclaw message `1559694571821203536`.

## Review/source

Five actual Godot 4.6 captures are supplied: switch front ON, switch angled OFF, switch front OFF at the same camera, and outlet front/angle. All final views were visually inspected. The preview lighting was reduced after the initial pass clipped the pale rocker to white; final captures retain its shading. There is no game-behaviour QA claim in this artifact.

The editable `Household_Electrical_Fixtures_First_Review.blend` contains exactly the switch root/base/rocker and the outlet, with two packed palettes. Temporary review cards were not saved. Display staging is separate from the zero-origin exports.

The standalone art branch `Staging-Astra-Household-Electrical` is based on the published `09255d473ab87a55d17713d870c5e365ae351048`. It changes only these model/texture files and notes/manifest, not catalog/core/networking code or other art handoffs. The recipe/config/references are provided for rebuilding in a **separate empty art scene**; the recipe replaces that scene's current objects.