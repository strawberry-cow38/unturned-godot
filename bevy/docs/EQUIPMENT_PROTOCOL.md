# Authoritative owner equipment, protocol 62

The Bevy host now owns the exact held `Item` instance and all original attachment,
magazine, shell and firemode transactions for remote clients. A client sends an
instance reference and intent. It never uploads replacement ammunition, attachment
state or a locally mutated inventory. Integrated play uses the same source
`WeaponState`/`AttachmentTransaction` implementation.

## Framing and compatibility

Protocol 61 clients are explicitly refused with the original `VersionMismatch`
reason and server version 62. IDs are appended; existing IDs keep their values.

| Plane | ID | Payload |
|---|---:|---|
| Inventory system | 7 | Existing owner grid and worn state, then optional identity tail |
| Owner equipment system | 19 | Selected identity/address, selection version and actual weapon state |
| SelectHeld command | 71 | Owned address, token, whether to move into the original preferred weapon slot |
| WeaponIntent command | 72 | Action/slot tags, expected gun token/version, optional incoming item token/address, shell ID or firemode |
| AttachmentOptions command | 73 | Request serial |
| AttachmentOptions event | 52 | Matching serial, source menu capabilities and actual owned choices |
| EquipmentFact event | 53 | Owner-only accepted change, original weapon animation timing, or refusal |

The inventory tail has a presence bit and one nonzero unsigned 64-bit token for
each jar in the seven owned pages, in the existing page/item order. Storage views
and worn items do not acquire actionable tokens. The complete grid, worn fields
and token tail are parsed into temporary data and adopted together. Truncation,
duplicate tokens or duplicate cells leave the previous accepted view intact.
Tokens identify server `Item` object identity, not an asset ID or a cell. Moving
an item preserves its token; cloning/replacing it changes the token. Tokens are
session references and are not written into the original saved Item format.

System 19 is sent only to its owner and is excluded from global sync hashes. Its
state includes token/address/item ID, selection version, reloading, unloading,
rechambering, broken/canFire, ammo/capacity, chambered, firemode, magazine ID,
selected shell ID and suppression. Selection version changes when the actual
held instance changes, including removal and an identical replacement. The gun
token also rejects stale requests across save/load where version numbers can
restart. The remote view resolves attachment fields only when the selected token
matches the atomically accepted inventory.

Command and reply tags are checked before adapters run. There are five distinct
menu slots, at most `OWNPAGES * byte.MaxValue` choices/shell entries per list,
192-character source names/reasons, finite colour channels and nonnegative finite
animation durations. Invalid or truncated messages cannot partially replace an
accepted menu/state. Connection identity supplies the owner; no payload can
select another player's inventory.

## Source transaction path

`EquipmentServices` adapts the typed commands to the existing `QueueAttachment`
preflight and original mutation helpers. Selection validates actual ownership,
hand kind and source slot admission; equipping uses the same original inventory
move as the old equip command. Installing, removing, selecting loose ammunition,
unloading loose ammunition and racking reuse the original exact-instance,
capacity, chamber, busy-state and timing rules. Firemode selection accepts only
the gun's actual supported modes and changes no ammunition. It applies during
command dispatch, before combat, so a later shot in that same tick cannot spend
a round after safety was accepted. The regression fails with the deferred path.

When `WeaponServices` is installed, `ServerTransactions.AuthoritativeEquipment`
rejects the older GunState, WeaponUse, FitAttachment, ReloadSwap and GunUnload
commands. In particular a raw partial unload cannot bypass source weapon clocks
or pick an arbitrary compatible loose-round ID. Magazine removal returns the
actual loaded magazine and preserves the chambered round; the source rack or
loose-shell action handles its own ammunition. The flag/delegates default off/null
for engine-free and old source hosts, preserving their existing transaction
behavior. A remote native client needs a host with this equipment adapter for
exact-instance selection; it does not guess an equivalent same-ID item.

Accepted source `attachmentChanged`, `attachmentRejected`, reload/rechamber,
reload completion/cancellation, equip refusal, dry-fire and broken-bottle facts
are unicast to the owner. Facts retain their authority tick, timing fields and
broken barrel ID. Integrated hosts retain their original local facts without
also replaying the wire copy. Existing shot/impact enrichment is a separate
protocol follow-up and is not implied by this owner-equipment channel.

## Native JSON seam

Existing `holdItem`, `equipAndHold`, `installAttachment`, `removeAttachment`,
`chooseShellType`, `unloadShells`, `rackGun` and `setFiremode` action names remain.
A remote queued selection leaves the old held item visible until accepted owner
state arrives. `attachmentOptions` is asynchronous remotely: `{pending:true}`
means no matching reply is ready. The eventual reply retains the integrated
menu's `gunToken`, `selectionVersion`, `slots`, actual `instanceToken` choices,
source colours, availability and shell counts. Inventory, selection or weapon-clock changes
refresh that owner-only query. Stale choices are refused server-side even while
an older menu is still visible.

## Verification

Six new core protocol tests exercise atomic identity parsing, duplicate/truncated
frames, owner-only state/options, connection identity, bounded malformed tags,
fact metadata and explicit 61/62 mismatch. All 651 core networking tests pass.
Eight new OS-UDP bridge tests exercise real equip/fire, exact attachment spending,
same-address replacement, movement and swaps of identical items, another owner's
token, selected-gun replacement epochs, replay refusal, blocked legacy ammunition
assertions, canonical magazine removal, same-tick safety ordering, menu-clock invalidation
and supported firemode selection. All 41
managed UDP/status/profile/equipment tests pass; the 121 existing Python authority
journeys also pass. These are authority/transport checks. The native menu's remote
pending state and interactive multiplayer fitting journey require their own
native tests and live rendering checkpoint.
