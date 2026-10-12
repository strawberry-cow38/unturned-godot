# Approved ceiling-lamp art handoff

Owner strawberry_cow9814 approved both models in Discord message 1559158996772986934 (2026-10-12), and requested Tinyclaw apply them to layouts.

- Ceiling_Lamp_Pendant_0: 24 vertices / 36 triangles; 0.741766 m width and depth; 0.943447 m ceiling drop.
- Ceiling_Lamp_Flush_0: 16 vertices / 24 triangles; same width/depth; 0.275 m ceiling drop.
- Actual Lamp_1 standing-lamp shade dimensions and unchanged stock 2x2 grey palette. Pendant preserves shade height; flush compresses it. Closed diffuser undersides.

## Critical mounting convention

Native Z-up, metres. Origin is the CEILING PLANE at Z=0. All mesh geometry is below it in negative Z. Standard native-to-Godot prop rotation maps this to geometry beneath the mount in negative Y. Do not apply floor-grounding or a second axis rotation. Layout height should put the origin at the room's ceiling underside.

## Integration left to Tinyclaw/Cow tools

No catalog/GUID, save, collision, snapping or gameplay illumination changes are in this art commit. Use/register the OBJ+PNG pairs and wire lamp behaviour into the generated layouts as requested. They are not already placeable.

The preview GLBs contained no light nodes and no emissive shader. Preview-scene OmniLights were only for illustration. `ceiling_lamp_mounts.json` documents mounting and suggested downward light positions (1 cm below the diffuser) to avoid placing a light inside the opaque housing. It is handoff metadata, not a claim that a loader already consumes it. Keep any chosen brightness/range controlled by existing lamp/layout behaviour.

Existing Lamp_1 art remains untouched. Toilet and pedestal sink remain WIP/paused and are deliberately absent.
