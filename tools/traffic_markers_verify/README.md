# Focused real native-loader verifier
Build only this small project: `dotnet build -m:1 -p:UseSharedCompilation=false -nodeReuse:false`.
Run a Godot4.6 Mono binary: `godot --headless --path . --audio-driver Dummy`.
Expected: `TRAFFIC_MARKERS_NATIVE_LOAD_PASS checks=58`.

Links the repository's actual ObjMesh.cs, not a mocked OBJ parser. Does not compile or run the full game/test suite. Loads all four first-version full/LOD native models, checks exact approved SHA256s, standing transform/datum/heights, counts, normal/winding/UV streams, stripe presence, native collider face availability and palette pixels. Availability of collider faces is not physics registration.

Removal of Traffic_Cone_0_tex.png was rejected by the file-existence control; the original bytes were then restored and all58 checks passed. All build/cache/log files stay private/ignored. This verifier does not register models in the editor catalog or exercise a player scene.
