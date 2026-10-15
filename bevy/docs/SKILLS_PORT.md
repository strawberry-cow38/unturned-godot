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
which predate that candidate's separate damage correction. Native graph UI and
rendered validation are the next checkpoint; no UI completion is claimed here.
