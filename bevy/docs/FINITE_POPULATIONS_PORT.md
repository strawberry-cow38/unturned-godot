# Fresh finite-island populations

The native `--new-island` entry now follows the population branch of original
`EditorPlayMode.StartPlay`: generated zombie, loot and fauna points feed the
existing authoritative field services, using PEI's original table definitions.
The original `ProcIslandSpawn.SpawnVehicles` selector is extracted with only its
vehicle constructor and scene-parent sink replaced by value records. Random call
order, road/town/water rejection, variant, yaw and the source +1.1m placement
remain unchanged. Generation stays inside the existing source scratch-state lock.

Only fresh generation returns this population handoff. `--island` opens the
existing authored files and never reconstructs missing town/route metadata or
regenerates population lists. The host installs the lists after native terrain
registration; remote clients cannot publish them. Geometry refresh does not
repeat installation or replenish picked-up loot. Source loot-condition rules are
loaded before editor containers and field rolls, including the source missing-file
reset behavior. Ending the finite session clears those rules; disposing an
unrelated remote peer leaves the active host's rules intact.

Generated loot uses the original `LoadGenerated` table parser/selector and does
not read retail `Jars.dat` positions. Saves retain every rolled generated point,
including unvisited points, plus the existing live/taken state. Loading restores
those rows without rerolling or requiring table files. Older retail loot saves
retain their existing constructor and coordinate translation. Zombie and animal
field saves continue through their existing authoritative persistence paths.

The source fields can sample terrain outside resident collision. One active
finite surface supplies height and dominant-layer queries in the current origin frame, and is
released with the session. This is separate from streamed collision/rays and does
not retain every opened map. The surface directory is preserved with existing
finite service specifications so load can restore the samplers. Original water
filtering still uses the source layer5/sand proxy, including its beach exclusion;
the port does not substitute a new sea-height rule. Exact generated kept-point
counts and both callbacks after load/rebase are asserted.

Roadside vehicle records use stable per-map keys and are saved even before their
area streams in. Native registration waits for loaded terrain, preserves exact
source XYZ/yaw/variant, and uses the existing server-owned vehicle registration
and save path. It does not apply the retail spawn-height adjustment or reroll a
variant. Rebase translates pending plans and generated loot exactly once.

Verification: the original GodotSharp differential matches all 29 complete output
files for four generation cases, including all vehicle records. Three independent
vehicle hashes now join the existing full layout/prop/surface/road regressions.
The focused managed generation/files/respawn group passes ten tests; four population
service tests cover duplicate registration, taken and unvisited loot, save/load,
rebase, complete far-terrain sampling, session reset and actual UDP pickup. The
UDP peer observes the same item ID, receives its accepted removal and is rejected
when attempting population publication. Native tests exercise the actual bridge
registration system and source vehicle-plan streaming.

The SourceLayer negative control removes only the zombie callback binding from
an isolated private build. The complete-surface test then keeps 180 points instead
of the original 175, proving the test catches the streamed-ground filtering error.
The final isolated protocol 73 candidate, `finite-population-v73-complete`, passes
all 213 managed tests, including all four population regressions and session
cleanup. The independent protocol 74 hitch checkpoint was excluded from that
candidate; this population checkpoint adds no wire fields.

Actual Vulkan used `native-pickup-main-first` with the immutable
`finite-population-v73-layers` bridge, seed 1234 and a fresh 1×1 island. Opened
`bevy-finite-population-final-initial`, `-final-roadside` and `-final-fauna-wait`
PNG/JSON show the original road truck, generated horse and zombie. The source
plan has three vehicles; streaming registers the near one first and all three
after accepted teleports. The real F key picks rope 58, removing its geometry and
prompt; actual Pause→Save world→Load world keeps it absent while the other rope,
three vehicles, four animals and ten zombies remain. Opened `-final-loot`,
`-final-picked` and `-final-reloaded` document the accepted transition. Pause→Quit
exits 0. Parent source and independent image reviews pass. These are inspected
runtime checks, not pixel goldens or a two-native-player finite-map join claim.

Remaining finite scope includes editor tools and custom sidecars, map selection
and remote finite-map transfer/negotiation, and separate BuildingGen integration.
This checkpoint does not synthesize populations for saved maps lacking source
metadata, add new wire IDs, or change the original generated population rules.
