# Player names and profile pictures

Reference: `game/Nameplate.cs`, `game/PlayerProfile.cs` and the profile adoption
block in `game/RemotePlayers.cs`. This checkpoint covers remote display; profile
configuration/sending and NPC placement remain separate integration work.

## Accepted profile adoption

The native consumer reads accepted player rows and asks the bridge to sanitise a
changed name with the original `ProfileRules`. Plain font shaping never parses
markup or fetches URLs. A picture is queried separately with the exact player ID
and 64-bit accepted hash. Pending data keeps the name alone, then the original
`AvatarArrived` event permits an immediate retry; a bounded polling fallback
handles a missed event. A stale hash cannot install an unrelated image. Removing
the avatar clears the previous picture, and disconnect/load retires the child
plate and cached handles with its body.

The managed `profileAvatar` operation has real UDP tests for late reliable bytes,
replacement, departure and disconnect. Native tests cover the returned ready,
pending and stale forms, including hashes beyond floating-point exact integers,
plus actual source sanitation, unchanged-name reuse and disconnect cleanup.

## World rendering

The shared renderer uses the original 2.05m anchor, 0.42m picture, 0.315m picture
offset, 0.16m text scale at font size64, 12-pixel black85% outline and inclusive
60m cutoff. The unused-by-remotes measured-gear helper preserves the source NPC
rule for its subsequent integration. No picture means no picture quad; non-null
PNG bytes that fail to decode or have wrong dimensions use the original 8×8
magenta/near-black checkerboard.

The source engine's actual Open Sans Semibold font is embedded, with its original
license and conversion provenance in `assets/fonts/README.md`. The source
[default-theme build](https://github.com/godotengine/godot/blob/4.6.2-stable/scene/theme/SCsub)
selects that font. Bevy's font pipeline supplies shaping and glyph atlases; a
texture bake supplies the outline and mip chain. Rasterizer anti-aliasing is an
engine adapter, not a claim of pixel-identical FreeType output.

Unlit world materials retain ordinary depth testing and cast no shadows. A
vertex shader faces the quad toward its current view without rotating the
parent body. The same shader can support another camera, but current nameplates
are not added to the water reflection list, matching the original callsite.

## Evidence

Five focused native regressions pass: real font/Unicode shaping and outlined
pixels, decoded PNG/mips versus corrupt/wrong-size images, exact distance and
gear-height rules, delayed/stale picture adoption, and source sanitisation plus
body lifecycle. `bevy-nameplates-front.png` and `bevy-nameplates-side.png` were
rendered through the production rig and nameplate systems and opened for review.
They show a real decoded picture, name-only player, corrupt-image placeholder,
and a fourth label occluded by a solid wall. Side-view billboarding and partial
occlusion remain correct. The fixture supplies fixed accepted profile values;
it does not claim a complete live profile-edit journey.

The combined native suite passes 373 tests (three opt-in render fixtures ignored)
against the immutable `profile-tests-33-final` bridge. Headless character tests
also exercise font asset initialization without replacing an existing font
store. An independent source/code and two-angle image review found no blocker.
