# Accepted viewed-container inventory operations

Protocol 75 adds exact Empty and magazine operations for the container the owner
is currently viewing. Owned commands 79/81 and legacy 39 retain their byte layouts;
Event 60 remains the same owner-only magazine result. System 7 extends its
existing identity tail from owned pages to owned and container pages. AREA
remains excluded. Published 74 peers are
rejected during the handshake rather than silently losing the new commands.

## Source and admission

`InventoryUI.EmptyFuelSelected` edits the selected fuel container, and its timed
magazine jobs resolve selected pages through the original `MagRules`. The native
UI preserves those actions for storage, freezer and vehicle compartment pages.
It still sends an intent and displays only accepted item data.

`ScopedInventoryAddress` carries crate ID, page, root x/y, item ID and instance
token. A zero crate ID admits only the owner's pages; a nonzero ID admits only
container-view pages. AREA is excluded. The host checks the live connection owner,
alive state, accepted position, current open-crate latch, viewer membership and
`CrateEntry.CanAccess` on every operation. That reuses the existing fixed-crate
reach and vehicle-specific access rules. The projected and backing slots must
both resolve the canonical root, ID and token to the **same Item instance**.
Opening once does not grant indefinite access after walking away or losing a
vehicle seat.

| Added command | Payload excluding command byte | Result |
| --- | --- | --- |
|84 EmptyViewedFuel|17-byte scoped address|Set only that fuel instance to zero; repaint every viewer|
|85 ViewedMagazineTransfer, load|u32 request ID, magazine address, unload bit false, round address; 39 bytes padded|One original MagRules load; shared Event 60 result|
|85 ViewedMagazineTransfer, unload|u32 request ID, magazine address, unload bit true, expected loose-round ID; 24 bytes padded|Insert the round into the owner's bag before decrementing; shared Event 60 result|

At least one magazine-transfer input must be viewed. Unload has no input-round
scope or token; its unused bridge fields must be zero. Both 81 and 85 use the same
per-owner monotonic replay ledger, including retained refusals and disconnect
cleanup. Switching between owned and viewed jobs cannot spend an old request
again. The shared mutation keeps the original loose-ammunition/type/capacity
rules. Bare amount/cartridge/fluid edits explicitly repaint all viewers after
the complete mutation. Depletion uses the existing immediate container writeback,
so a zero stack disappears from the backing grid and every viewer.

The System 7 grid still defines the token order and count. Each published
container jar adds one u64 token to the same owner-only block; there is no
independent attacker-controlled count. Existing byte jar counts bound the extra
tail to at most 14×255×8 bytes. The receiver parses the complete grids and tail
before committing either, rejecting duplicate roots or identities across owned
and viewed pages. A byte-maximum final compartment, truncated tail and absent
provider are covered by core tests.

The native UDP fixture exposed the old owned-only token tail: container IDs
arrived but their tokens were zero, so native admission refused correctly.
Managed tests now assert actual received tokens and send the received address;
they no longer prove this seam with server-only token knowledge.

`inventoryView` supplies the accepted crate ID with each scoped item. Native
context dispatch and timed jobs retain it along with the exact token. The job
still waits for its correlated result and inventory tick before another round,
pauses while the dashboard is closed, and preserves request IDs across reopening.
A changed crate identity retires the old job.

## Verification

The focused managed cases cover actual UDP Empty on storage/freezer/compartments,
both-viewer updates, mixed owned/viewed magazine inputs, owner-only replies,
shared 81/85 replay, full-bag refusal, stack depletion and close/reopen cleanup.
Rejections cover dead owners, stale/foreign identities, changed/closed/removed
views, reach and current access revocation. Core tests cover truncation and
invalid scoped shapes atomically plus backing-reference and root-cell admission.
A published 74 peer is explicitly rejected.

Three private negative controls remove current access admission, replay admission,
and all-viewer dirty publication separately. Each corresponding real UDP test
fails at the intended access, amount or stale-view assertion. Native tests cover exact context dispatch, source stack order, shared
request sequence, result-before-echo waiting, view retirement and the real UDP
production timer unloading a magazine in a police trunk. The final immutable bridge is
`bevy/.local/viewed-inventory-v75-tokens-final`, built from the clean
`a25da4ad` source archive plus this checkpoint's 13 managed/core/test paths in
`/tmp/unturned-viewed75-final-source`. Its bridge DLL SHA-256 is
`4ed36db8162c6b627bfb7a44f798c3443ef2df90344be47bedd9ddf0c3ed03f0`;
`UnturnedNet.dll` is
`5b72c51cc407153a3f033c0f1a3c701a5b9aeba08a30b9d51a36a2f3e2029058`.
This isolated scope excludes the later power/building presentation adapters.
`native-viewed-inventory-v75-final` runs the native tests and render against that
DLL. All 44 Inventory tests pass, including real UDP trunk unloading; the Vulkan
fixture passes and all four images were opened and independently reviewed.
`bevy-inventory-viewed-trunk-{0,1,2,3}.png` shows a 30-round trunk magazine, source
Take/Unload context, accepted 27+3 split, then the scoped unload wheel and loose
round in Hands. The JSON sidecar records accepted amount 26 and exact crate/token
request. A fourth negative control swaps back the development DLL with the old
owned-only tail; the received-token test rejects its zero token.
Final broad results are recorded in PROGRESS.md.

## Remaining external-page work

Nearby now has its own owner-only full-item projection (protocol 76) and the
ground Split/Empty and magazine operations (protocol 77); see
NEARBY_PORT.md. A ground magazine job can take its rounds from, or put them into,
the viewed container scopes described here.

Viewed Salvage is not offered, matching the source. The original
`InventoryUI.OpenSelection` gates its Salvage button on `_selPage < OWNPAGES`
("OWN PAGES ONLY": crafting counts and spends owned pages, so a crated or ground
copy would either do nothing or consume the owned copy instead). An earlier
revision of this note said the source offered it; that was incorrect. No
viewed or ground salvage transaction exists to port.
