# MAC-10 V13 integration

October 5, 2026. Accepted MAC-10 art added as a new gun, not a replacement for the Uzi.

## Obtainable items
- `9145` / `MAC-10`: Rare secondary gun, 3×2 inventory footprint.
- `9146` / `MAC-10 Magazine`: proprietary 30-round .45 ACP magazine, group 205.
- `9147` / `MAC-10 Iron Sights`: separately modelled factory irons. The normal factory-irons rules apply; separate geometry does not imply freely removable factory sights.

The existing console name resolver can obtain the items by these names or IDs. No random loot-table entries, recipes, optic rail, stock-toggle action or live-server deployment added.

## Components
- `mac10_gun.txt`: approved body plus fixed extended stock (default), 264 triangles. No magazine or irons baked into it.
- `mac10_sight.txt`: separate combined front/rear factory-irons asset, 128 triangles, palette texture.
- `mag_mac10.txt`: separate magazine, 12 triangles, own gunmetal palette.
- `mac10_stock_extended.txt`: separate export of the approved extended stock, also included in the default gun/world body; not an implemented toggle.
- World pickup meshes/manifest entries and inventory icons for all three items.

The mounted gun totals 404 triangles. Original V13 positions/face-position indices are preserved in the shared frame. Magazine and irons are exported relative to their authored mounts. `MAC10_ASSET_LAYOUT.json` records the measured frames and hooks. The runtime gun frame is already `(-L,U,-H)`; applying another source-to-port sign flip would invert it.

`tools/models/mac10_v13.json` freezes the approved art. `python3 tools/install_mac10.py` regenerates the runtime/world meshes, palettes and visual table rows; `tools/render_mac10_icons.sh` regenerates inventory icons using Godot/Xvfb.

## Animation, audio and tuning scope
Damage/recoil/ballistics retain the provisional Bulldog/Uzi game baseline. The current .45 MAC-10 cyclic-rate target is **950 RPM**, with an opt-in fractional 50 Hz schedule shared by the shell and server. Cartridge/feed identity remains .45 ACP / 30. See `MAC10_RATE_REFERENCE.md` for the source and variation caveat.

The named animation donor is Bulldog/Uzi: equip, aim, reload, hammer, inspect, attachment-view and sprint. Own future Mac10 clips take priority. This is reuse of existing animation tracks, not custom-authored MAC-10 hand/magazine animation. The separate magazine can hide/swap normally; the borrowed reload is not a claim of a newly authored moving-magazine track.

Shoot/reload sounds reuse the Uzi; rack sound reuses the existing generic Eaglefire rack. No new recordings claimed. Stock is extended and fixed by default; no stock-toggle action or stock animation is implemented.

## Shared visual and server wiring
- Optional fifth `sights.tsv` column carries an authored iron palette. The palette survives factory-irons refitting.
- `Viewmodel.MagazineVisualFor` resolves the same mesh/mount for first person, remote/third-person, paperdoll and world pickup attachment assembly. Explicitly empty magazine slots do not draw a factory magazine back in.
- Separate magazine/iron textures are bound white, nearest-filtered, not tinted dark a second time.
- Existing per-player server gun-profile fallback was still generic. `AuthoredGunProfiles` registers only the new MAC-10 from the same `.dat` in both listen-server and dedicated startup. Ownership comes from server inventory; claimed held ID alone cannot select the profile. Existing explicit host/test overrides win, and other guns retain their former behavior.
- No protocol/wire bump: v55 remains v55. New content must still be installed on both client and server to use the new item; unchanged wire version does not put missing item data on an old server.

## Prior integration verification (before extended-default update)
- C# build: 0 errors, 23 existing warnings.
- Focused `--tests=gun.mac10_*`: six tests, 47 checks. Catalog/feed compatibility, separate viewmodel parts/materials, actual world pickup model/parts, authoritative profile selection/ownership, and real dedicated-host profile wiring.
- Negative control: remove only the MAC irons row from `sights.tsv`; the viewmodel test fails on missing separate irons. Restore the row and rerun normally.
- Actual game viewmodel captures: equip/hip, ADS and reload via `Main`'s capture path. Vulkan/mobile/Xvfb, normal movie completion, exit 0. Runtime log selects `Bulldog_Equip`, 0.8 s; reload 1.8 s; hammer 1.133 s.
- Inventory icons rendered from the installed runtime meshes, not placeholder drawings.
- No whole L0/L1 suite run. No main merge or live-server restart performed as part of staging integration.

Known baseline renderer warning: ObjectDB leaked-instance warning at normal capture exit; no new engine errors in the captures. The exported review image crops the viewmodel overscan and composites its transparent capture on a flat background; it is not a screenshot of gameplay in a map.

## Extended-default asset update
The installer now uses the approved V13 `body` + `extended_stock` for both
`mac10_gun.txt` and world pickup `items/9145.txt`. Magazine and factory irons remain
separate, unchanged V13 exports. The mounted total remains 404 triangles.

This update regenerates meshes, palettes, manifest bounds and layout metadata only;
it does not change fire cadence, `mac10.dat`, hooks or other visual-table rows.
Inventory icons have **not** been rerendered for the extended default; the existing
icons and captures above predate this change. New icon rendering and game captures,
and any cadence work, are deferred to the main worker. No build or gameplay tests
were run for this asset-only update.

## Extended stock / cyclic-rate follow-up
- Default gun/world body now uses the accepted extended stock; same body/irons/magazine shapes otherwise. Icons regenerated.
- Optional `Cyclic_Rate_RPM` defaults to absent/zero on every other gun. Existing integer `Firerate+1` behavior is unchanged for legacy-only use.
- 950 RPM is 19 shot intervals in 60 physics ticks (1.2 seconds). Fixed 3- or 4-tick intervals would be 1000 or 750 RPM, so the opt-in phase alternates 3/4-tick gaps without banking idle/reload time.
- The phase is per shell/per server player, never mutable shared gun-profile state. Switching to legacy timing cannot erase a pending cooldown.
- Selected cadence unit tests: 17 passed. Selected server rate tests: two opted-in cases, a legacy-to-RPM transition guard and the existing legacy abuse guard passed. Real-shell cadence test fires 20 shots spanning 19 intervals/60 ticks and consumes ammo. Six focused MAC tests total 47 checks. No full suite.
- Existing combat packets have no shot timestamp: receive-tick batching/jitter can still cause rejection of legitimately separated shots. This change does not promise cadence preservation across arbitrary packet batching and does not silently change the wire format.

Extended-stock follow-up rendering: regenerated inventory icons and a complete actual game hip/equip capture (48 frames), Vulkan/mobile/X11, exit 0. A longer ADS capture attempt timed out and is not counted as successful verification. The previous collapsed-stock ADS captures are not presented as new extended-stock captures.
