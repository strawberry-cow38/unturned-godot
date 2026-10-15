# Accepted resource contacts over UDP

Protocol63 adds reliable ordered Event54 `ResourceContact`. Version62 peers are
refused at the handshake: they would silently drop the new event. No existing
payload or command changes. The existing resource bitmap still owns alive/dead
state; a contact event never damages, harvests, regrows or creates rewards.

| Field | Encoding and meaning |
|---|---|
| kind / material | bounded u8 enums: Hit/Felled and Tree/Ore |
| index | u16 authoritative resource registry index, shared with harvested/respawned facts |
| tick | u32 accepted server simulation tick |
| position | shared `NetWire` world-position grid; contact point for Hit, original root for Felled |
| Hit: health / damage | finite nonnegative source floats; zero damage is valid for ore tool refusal |
| Felled: direction | shared 12-bit-per-component direction, horizontal and unit within quantization tolerance |
| Felled: trunkLength | positive finite source float |
| Felled: debrisSeconds | exact source float; positive for a tree, zero for ore |

The source `ResourceServices` remains the only producer. A real accepted hit
publishes the fact, and death retains the original alive-bit transition, drop
timer, species rewards and regrowth behavior. Tree rewards still wait for the
original11-second debris timer; ore still accepts only the pickaxe's original
Resource_Damage. Invalid/truncated payloads are rejected before a presentation
handler runs. Resource indices and registration metadata are validated before
publication.

Both managed modes expose `resourceHit` and `resourceFelled` in the retained,
acknowledged event queue. The integrated host emits its exact source fact once
and ignores its wire echo. Remote clients add the event without local authority.
JSON includes `acceptedTick` and `ageSeconds`: the latter is elapsed source time
known from the latest received snapshot when the event arrives, clamped to zero
when reliable delivery precedes the snapshot and handling u32 wrap. It is not an
invented estimate of unobserved network transit. The outer event tick is the
accepted tick too. Local age is zero. Remote fall directions are normalized
after wire quantization.

Name strings are omitted from the wire; the native view resolves an index through
the accepted map manifest and original resource catalog. Local JSON retains its
source name; remote JSON supplies `name:null`. Unknown/unloaded resources do not
gain guessed identity or direction. Late joins and save restoration receive
lasting dead state, with no replay of historical falls.

Native adoption of accepted age and an actual remote fall render are a separate
presentation checkpoint. The existing native view already retains a fall fact
until the corresponding dead snapshot arrives.

Four new core tests cover shared-grid round trips, every truncated prefix,
invalid tags/floats/directions/lifetimes, once-only ordered delivery, persistent
bitmap independence and explicit version62 rejection. Three bridge tests use
actual UDP and the source producer: a real bullet fells a Birch, local/remote
queues each receive one hit and one fall, the original reward timer runs once,
late joins receive only the dead bit, and ore retains its pickaxe gate and zero
debris lifetime. Age tests cover snapshot lag and tick wrap.

All656 core network tests,59 managed bridge tests and121 existing Python authority
journeys pass on immutable `bevy/.local/resource-contact-59`. That integrated
artifact includes the separately owned bed-damage adapter's three tests; those
changes are outside this protocol checkpoint. An isolated negative build removed
only the bridge subscription: the real UDP test failed because the remote hit
and fall were absent, while local production still succeeded. Native GPU
evidence belongs to the presentation follow-up.
