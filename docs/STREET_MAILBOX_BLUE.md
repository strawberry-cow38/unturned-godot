# Approved blue street mailbox — integration handoff
Owner1558688527003422721 approved ONLY the latest player-scaled mailbox, both newspaper machines scrapped.

Prop key: **Street_Mailbox_Blue_0**.
Files: `game/content/objects/Street_Mailbox_Blue_0.obj`, `_lod1.obj`, `_tex.png`.
Flat PROP naming, not part-indexed resource/canopy/trunk naming. NativeZ-up/metres/XYcentre/baseZ0/front+Y; existing EditorObjects.Upright(-90degreeX). Scale already baked; do NOT apply the15% increase again.

Full:74v110tri,1.5065m. Same approved shape/texture byte-for-byte: first-version envelope, higher hood-conforming slot, four short legs. LOD:62v80tri. Body, legs, hood slot and three envelope FRONT faces exactly match full; only tiny envelope badge/fold back+side faces removed. No large silhouette change.

Palette opaque4x2. No newspaper model/texture files, no player reference meshes, no generated placements. No GUID/catalog change by this art commit: **Cow tools owns registration/collider/surface/LOD integration**. Do not claim placeable until that follow-up is verified.

Standalone real-loader check: `tools/mailbox_verify/`, builds ONLY its tiny verifier and links existing game/ObjMesh.cs. No fullgamebuild or test suite.
