# Original Skills graph and accepted progression

The source is `game/SkillTree.cs` and `game/inventory/SkillsUI.cs`, rather than the
older numeric skill list. The current graph has fifteen nodes in three main and
three advanced pillars. Multi-node prerequisites and pillar openers remain the
unchanged source `SkillProgress.CanTake/Take` rules. Grant descriptions are
presented as authored; the original graph has no consumers that apply those
grants to gameplay, so this port does not invent bonuses or map graph nodes onto
the separate legacy skills.

The original Godot graph spends XP locally and does not persist learned nodes.
The native port deliberately closes that authority gap: protocol 70 appends
`CommandLearnSkillNode = 77`, carrying only one bounded node ID. The authenticated
sender selects the owner. On the authority tick, the exact source rule checks
the live XP pool and prerequisites, then takes the node and spends the returned
cost. Unknown, duplicate, locked and unaffordable requests leave both unchanged.

The existing owner-only Skills system (ID 5) carries XP, legacy levels and a sorted
learned-node tail in one block. The reader validates the complete block before
updating any part of it. Node IDs are 1–63 lowercase ASCII letters, digits or
underscores; the tail is an unsigned byte count capped at 64, followed by the
six-bit-length strings. Duplicates, unsorted or unknown IDs and truncated data
are rejected without partially replacing XP or levels. Older protocol peers are
refused by the version handshake. There is no additional event or public graph
replica.

Player saves contain sorted `LearnedSkillNodes`. Older saves default to empty;
unknown saved IDs are ignored. Restoration adopts the known set without spending
XP again or replaying grant descriptions. The original `SkillTree.cs` is compiled
once into UnturnedSim and excluded from the Godot project's duplicate glob.

`BridgeHost.ReadSkillTree` evaluates the source rules over the accepted owner's
replica. It reports graph metadata, source grant descriptions and precise
prerequisite/XP verdicts, cached by the existing owner state hash. Rust need not
duplicate those rules, and sending a Learn intent cannot repaint a node as
learned before an accepted snapshot arrives.

Authority validation: all 701 core tests and 136 managed bridge tests pass. The
focused bridge pair additionally passes real UDP save, disconnect, host load and
same-profile rejoin. Tests cover owner privacy, live XP, repeated and insufficient
spending, cross-column prerequisites, malformed commands and atomic owner tails,
old saves and legacy skill preservation. A private negative-control assembly
removing only command 77 registration fails the actual UDP learn assertion. The
Godot C# project also builds with the shared source/exclusion.

The immutable candidate is `bevy/.local/skills-v70-rejoin-final`. A broad JSONL
run on the earlier `skills-v70-first` candidate passed 124 of 127 tests; the three
failures are the independently added world-round slug/condition/falloff tests,
which predate that candidate's separate damage correction.

The native dashboard now presents the accepted graph through `ui::skills`.
J (or its rebound Skills key), the Skills tab, and the existing dashboard
buttons open it. Original node/pillar dimensions, main/advanced bands, every
prerequisite edge, source theme, detail text and disabled Learn verdict are
retained. Selection and drag offset survive ordinary XP/learned refreshes;
geometry changes and window resizing perform the source layout/clamp. A closing
or dead screen cannot submit stale Learn actions. The shared original navbar is
also used by Information; Crafting and Inventory retain their current native
panel shells while exposing Skills navigation.

Input accounts for the current 85 ms swoop translation when testing node versus
background drags. Refreshing accepted XP does not recenter a user-panned graph.
The first rendered fixture also exposed a shared transition bug: a zero-delta
opening step stopped at t=0. The source stops only at the destination endpoint;
the corrected guard covers both opening and closing with a regression.

The original Open Sans font lacks the prerequisite check/cross glyphs. Querying
the running Godot 4.6.2 TextServer confirmed it resolves both to DejaVu Sans.
A licensed 3.9 KB subset supplies those two unchanged glyphs after Open Sans in
the native graph font list, without requiring installed system fonts. See the
font provenance and hashes in `bevy/assets/fonts/README.md`.

Native validation includes five focused Skills tests and seven shared Swoop
tests, rerun successfully with the final symbol fallback. The full UI suite
before that fallback passes 88 tests with
nine renderer tests ignored, excluding the independently diagnosed mouse picker
tests. The Vulkan fixture uses real bridge snapshots and production widget
activation: 500 XP -> Heavy Lifter (480) -> Steady Hands (440) -> Zeroing (380),
then a refused Improvised Armour request because Sharpening is missing. It pans
to the advanced band, resizes from 1600x1000 to 1024x768 and closes to zero roots
with input released. Captures `bevy-skills-0.png` through `-7.png` and
`bevy-skills.json` were opened and inspected; image comparisons guard against
invisible widgets as well as accepted-data assertions. The final eight-stage
Vulkan run also asserts the actual prerequisite text glyph layout, and both
check/cross symbols are visible in the blocked-node and resized captures.
Independent code and final image review passed. The source reference is
`godot-skills-reference.png` from the original `--skillsui` harness. That harness
stretches its 2560-wide logical canvas into a 720x760 window; it is a source
layout/theme reference, not a pixel golden for the native logical viewport.

This fixture validates the production graph over an integrated authority; the
separate managed tests validate UDP/save/rejoin. A real X11 playable PEI journey now also opens Skills, selects Heavy Lifter,
activates Learn and observes accepted XP 500 -> 480, the learned node and newly
available dependents. `bevy-crafting-pei-skills-open.png` and
`bevy-crafting-pei-skills-learned.png` were opened and inspected. Returning to
Crafting preserves the search. Advanced-pillar navigation still has the separate
fixture evidence above. Grant descriptions still have no original gameplay effect
consumers and remain descriptive as above.
