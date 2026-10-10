# Approved traffic markers

2026-10-10 approval: Discord1558578382403276883. **Use the FIRST cone**, not the broader/open-tip revision. Barrel is the approved first version. Six native mesh/texture files are byte-identical to the initial delivery1558575761294627046.

| Asset | Height | Full triangles | LOD1 triangles |
|---|---:|---:|---:|
| Traffic_Cone_0 |0.82m|192|136|
| Traffic_Barrel_0 |1.155m incl carry loop|312|216|

- Native PROP Z-up/metres/centredXY/pad bottom Z0, like existing roadside objects. `ObjMesh.Load` leaves CONV1 native positions; use `EditorObjects.Upright` (-90degX plus yaw) to stand them up. Not the resource/tree Y-up path.
- Opaque 2x2 `_tex.png` shared palette orange210/114/50, offwhite200/200/200, rubber42/42/42; unused dark orange170/89/43.
- White bands form part of the continuous cone/drum meshes, not coincident overlay shells.
- Barrel has a real moulded carry-loop opening, not a painted hole.
- Full/LOD native assets are in `game/content/objects/`. Manifest hashes identify exact approved files.

**Art-only staging:** no catalog/GUID, physics, destructible/debris, drops, gameplay or spawn registration added. Integration should use these exact keys and palette files. No main merge or server deployment by this change.

Editable Blender source, GLBs, recipe/snapshots, final actual-model Godot renders and 63-check focused geometry audit are in the original delivered `Traffic_Cone_and_Barrel_Models.zip` (SHA256931833baa0100e21114a7e6e9815e3be189421af2114d88c62af2d49291eb9fa). No exhaustive self-intersection audit/full game suite claim.

Standalone focused real-loader check: `tools/traffic_markers_verify/`. It links the current `game/ObjMesh.cs`, loads the four actual native OBJs and checks selected version, upright height, winding/normals, triangles, UV palette use and texture values. This builds only that verifier, not the whole game.
