# Switch seating correction — current binding contract

Owner requests1559816740542742579 and1559816755126476881: switch rocker still looks slightly floating. This supersedes the rocker seating/pose convention in art commit979c422c.

## Revised switch only
- Fixture GUID stays397a9d3cc7eb4a4291dc41ae40a8d12f; .32m width x .54m height; 28 geometric vertices /46triangles. Frame-matched #ababab palette unchanged.
- Housing opening narrowed from .18x.30 to .166x.292m (side clearance3mm). Rim deepened from nativeY=.040 to .050m.
- Rocker pivot moved further inward to native(0,.040,0), Godot(0,0,-.040)m. Rocker body remains .16x.040x.285m.
- ON bottom pressed = localX -5degrees. OFF top pressed = +5degrees. This replaces ALL earlier +/-8degree settings and .046/.055m pivots. Update runtime placement/rotation of the child, not just the main static OBJ.
- Entire rocker rear stays inside the housing in BOTH positions (nativeY approximately .007657.. .032496m; blind cavity backY=.004m). Pressed front edge lies slightly below the rim, not floating in front.
- Overall maximum projection now .072344m. Main OBJ/GLB staged ON. Base+Rocker used INSTEAD OF complete main OBJ when animated; helper meshes are not separate registered fixtures.
- No geometry behind backY=0; standard nativeZ-up,+Y-front convention unchanged.

## Outlet and gameplay
Power_Outlet_0 remains EXACTLY the previous square-corner outlet geometry and finish, GUID6bc5342b8fe5438b98ad49f4b28ccc5c; no outlet changes in this delta. Tinyclaw1559822305356414987 reports Staging-Tinyclaw168c7c5b0 has room switches at1.6m, two-door rooms with two switches controlling that room, and both receptacle wire ports sharing one1500W fixture rating; deeper rocker was explicitly pending this delivery. The metadata height now reflects that reported1.6m. Game behavior/network/save changes are NOT part of this art delta.

## Visual review
Actual Godot before/after front and angle plus revised ON/OFF front+angle captures; all inspected through the private Blender viewport. The art-review model is now mounted back-flush to wallZ=0, without the previous preview-only6mm stand-off. Both before and after use that same flush mount. No test suites run.

Editable source: Household_Electrical_Seated_Switch_Revision.blend; just switch root/Base/Rocker and unchanged outlet, two packed palettes, no temporary image cards. Recipe replaces current scene: rebuild only in an empty separate art scene.

Art-only delta based979c422c6a74a450fecb1dca3358ec1557c1b371, branchStaging-Astra-Switch-Seating. Bundle includes the previous art revision too and requires published862306c7131ab2a71efd7b8cc45cf4f8fed5249f. Tiny owns publication; no remote push is claimed here.
