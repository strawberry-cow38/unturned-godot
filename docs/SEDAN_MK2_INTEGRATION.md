# Sedan Mk II — V13 cleanup, separate new vehicle

## Player access

Open the F1 dev console and run `vehicle sedan_mk2`. Display name: **Sedan Mk II**.
The key is appended at TypeId **39**. Original sedan stays at TypeId **3**; all prior
keys, source files, prefab seat data and original sedan handling remain untouched.
No existing natural sedan spawns were replaced. This is an additional vehicle.

## Model and runtime

28 new prefixed assets are byte-identical copies of the V13 refinement exports. The
stock 4x2 palette is retained, including paintable index 0 and fixed charcoal index 5.
The fixed frame, four doors, hood/trunk, moving door panes, static fittings and
four interior pieces are installed separately. Lid undersides are body-painted.
The complete internal wheel housings are already in the approved frame; no diagnostic
or duplicate housing meshes are installed. Total: 2,478 triangles excluding tyres.

An opt-in `VehiclePanelRig` is shared by real cars and replica puppets. It uses body-space
pivots, follows existing glass/collider nodes, shares the body paint and adds no moving
rigid-body chassis walls. Board/exit pulses only the corresponding door, opening to
55 degrees and closing after 2.5 seconds. Internal seat switches do not pulse doors.
Hood/trunk open to 50 degrees with the existing mechanics/storage UI and close on
UI close/teardown. Runtime tick occurs before parked/NetHeld/distance early returns.

Body/stock fittings are 106% of original, tyre radius stays 0.6 m, track 2.18 m,
axles -1.9292/+1.949 m. Wheel mount Y=0.25 m matches the original sedan and
puts the nominal tyre centre at body Y=0 after the 0.25 m suspension drop.
Both native cars settle on the same flat floor: stock root Y=0.55353 m, Mk II
Y=0.55473 m in the focused regression run (1.2 mm difference). The separate
shared-world camera capture settled at stock Y=0.5518 m / Mk II Y=0.5547 m
(2.9 mm difference); neither car is repositioned vertically for the image. All four wheels
are in contact and both ride heights remain stable over the final 20 ticks.
Loaded tyre meshes clear all faceted housing planes with zero measured overrun.

V13 uses a single inverted-U outline (eight upper semicircle facets and straight
lower legs) for the body openings, complete backed housings and liner reliefs.
The inner housing radius is 0.70 m, outer radius 0.75 m. Its nominal centre is
body Y=0, coherent with the native suspension. Original exterior slope planes,
1.06 scale, wheelbase, stock fittings and hinge pivots are unchanged.

The cabin subtraction removes the thick hidden roof/cowl/rear-deck masses; slim
painted jamb returns remain instead of broad interior blocks. Engine/trunk liners
retain thin floors and end bulkheads, without the former full-length side blocks.
The level floor, low tunnel, dashboard and steering support remain. Lower door
metal is about 0.18 m thick (previously about 0.086 m); hood/trunk panels are
about 0.12 m thick (previously about 0.064 m). All lid surfaces remain body paint.
Free-edge underside relief keeps the thicker lids clear during opening, without
changing the closed exterior top edge. Seat geometry is the clean original shape,
not boolean-notched: front seats move another 0.12 m back, rear another 0.08 m.
The rear bench is narrowed to 82% width to clear the solid inboard housing backs.

Four explicit seat origins follow the approved shifted seat model. The visible body
keeps the original sedan's tuned displacement coherently scaled and shifted. Real
first-person camera follows the seated skull as the existing game does; DriverEyeLocal
is a fallback, not an assertion that overrides the animated seated head.

## Networking boundary

Wire format remains **v55**, unchanged. Replicas use the new spec/paint/parts/glass.
Door pulses derive from existing occupant identity snapshots; driver twin-to-puppet
handoff preserves the local exit pulse. Unchanged occupancy does not allocate or
rebuild sets every frame, and closed panels do not rewrite node transforms.

**Hood/trunk UI state is local, not replicated.** This addition does not add remote
compartment interactions, synchronized lid targets, or new vehicle storage/mechanics
protocols. Existing multiplayer glass-break propagation is likewise not expanded.
Server and clients still need this new vehicle's build/assets to spawn/display it.
No main merge or server deployment is performed by the staging commit.

## Verification

- Build succeeded (existing warnings only).
- L0: 2,364 passed / 0 failed.
- Focused vehicle regression L1: 12 tests, 550 checks, all passed. Includes 272 new
  model/rig checks and 26 real-runtime checks for native tyre contact/clearance,
  driver boarding/camera, safe exit, selective doors, UI closure, storage identity,
  teardown, driving and held braking.
- V13 authoring audits: four doors sampled through the full 55-degree swing;
  hood/trunk sampled every 0.5 degrees from 0 to 50. No fixed-frame/liner/seat
  penetration in those sampled poses. These are discrete geometry audits, not
  a proof of arbitrary runtime collision behaviour.
- Existing access, door exits, glass, lamps, hit meshes, paint, puppet glass/solidity,
  tyres and wagon tests stayed green. This is not a claim of a full L1 sweep.
- `--sedan-mk2-showcase=DIR` captures the actual game builders, native suspension,
  real PlayerController first-person camera and runtime rig. It does not reload
  the private art-preview scene. Output is explicitly a DIRECTORY, and PNG errors
  abort the capture. Dry rain shader globals are initialized before materials link.
  `UG_MK2_REMAINING=1` resumes just the driver/open-panel views;
  `UG_MK2_HEIGHT_ONLY=1` uses ONE fixed world camera/floor after native suspension
  settles, with no per-car camera recentering or height adjustment.
- Captures: original/closed/open side/front/rear, real driving 1P, open cabin, engine bay
  and trunk. Opening endpoints are inspection poses through the runtime rig;
  mechanics/storage input lifecycle is independently tested through the real UI methods.

## Portable asset audit

`python3 tools/install_sedan_mk2.py --check` verifies installed files and original
asset hashes through `docs/SEDAN_MK2_ASSETS.json`, even without the private art workspace.
Reinstallation requires the V13 refinement workspace via `--art` and frozen palette via
`--frozen`; the installer is a byte copier, not a geometry-authoring recipe.

## SHA-256: approved source geometry and installed bytes

Every row below is byte-identical source -> installed asset. Manifest SHA-256:
`b452b2abf752e618a015ccf4f7ba3f8993446e3a8dc8641c646df64c18874bfc`.

| Source file | Installed file | SHA-256 |
|---|---|---|
| `sedan_frame.obj` | `sedan_mk2_frame.txt` | `65af06bc22f8b19fc67f9797dd1d22ffef88009d103d85aead133b68864cf733` |
| `sedan_front_door_left.obj` | `sedan_mk2_front_door_left.txt` | `c9402a8a818aebb461f66700acd298cc60ab3e7392df1c9614d0160b11cbf37a` |
| `sedan_rear_door_left.obj` | `sedan_mk2_rear_door_left.txt` | `9ff7d919879c555f4c764d73b6df44713843735cab8d2753b8e3578eaf881bc3` |
| `sedan_front_door_right.obj` | `sedan_mk2_front_door_right.txt` | `024e2db1f00683c7a2d27ea3630382c63e591d15db8a16d20ff6485da71813ec` |
| `sedan_rear_door_right.obj` | `sedan_mk2_rear_door_right.txt` | `d0d97e2d93758ad6cc58cf98dfaeb0f051aa0bd0c714a48815237bef00ec5a59` |
| `sedan_hood.obj` | `sedan_mk2_hood.txt` | `5554595b874aa754d99e48aedec07ecca1aca7c59b668c7b61098940fe5a0adf` |
| `sedan_trunk_lid.obj` | `sedan_mk2_trunk_lid.txt` | `46f8652cbed8233a1ce99db83c8d93b94b429356c9e2f870c3f492c76b412d12` |
| `sedan_glass_l_front.obj` | `sedan_mk2_glass_l_front.txt` | `87b737aca4d53436118fe8e599b3e725218701623b0f8b7a51d9040b76a5ea4b` |
| `sedan_glass_l_rear.obj` | `sedan_mk2_glass_l_rear.txt` | `af0aa8497e54e6d023c2a7110cbcfb7c14f05cd1eea3b4c10a9d176a9bc9a23a` |
| `sedan_glass_r_front.obj` | `sedan_mk2_glass_r_front.txt` | `9acb14687ddd1ff270397c3d2f1d304c4c22a953b05a2c260eabb42b3928e4ab` |
| `sedan_glass_r_rear.obj` | `sedan_mk2_glass_r_rear.txt` | `b91f02595ac84e649d5d75836092098b0fb1a411f4a2a8a3b4e1dc35a19c865d` |
| `sedan_glass_rear.obj` | `sedan_mk2_glass_rear.txt` | `4c374c404d1fceb2aac8cebfcdd4c77fdad61feb80a140ac6dd360f2e238f7db` |
| `sedan_glass_windshield.obj` | `sedan_mk2_glass_windshield.txt` | `e3bf87ee902c80d900bc931d1545b1ab76fabbce52f5078622c52cfd75ba8f2f` |
| `sedan_stock_front_bumper.obj` | `sedan_mk2_stock_front_bumper.txt` | `30ced7ffce1dcf3dd631bcda9dca60242b4d548bc6affe215f259e45295efac7` |
| `sedan_stock_rear_bumper.obj` | `sedan_mk2_stock_rear_bumper.txt` | `cf8c07161f34ee5e6874bde043a8228a884564a624f62bffdba284bd933ebea0` |
| `sedan_stock_exhaust.obj` | `sedan_mk2_stock_exhaust.txt` | `892409df2dfc5aad899599d9ad5404743684915e7b6b054bb1224c627f49ca65` |
| `sedan_stock_headlights.obj` | `sedan_mk2_stock_headlights.txt` | `51bb3b2b658ce3e90e58e73de531aa52fcb84b7ded87e7bc46c012ca368829a0` |
| `sedan_stock_taillights.obj` | `sedan_mk2_stock_taillights.txt` | `f96953110910401f91bd39b7d66f7e92d4d896ec7edb60277624c0259a077a5b` |
| `sedan_stock_hitch.obj` | `sedan_mk2_stock_hitch.txt` | `adff7e44427813c2349b45cb7710829046cb65f6372f60424f77edc2c2433588` |
| `sedan_stock_seats.obj` | `sedan_mk2_stock_seats.txt` | `236513ff339a71837c447b36a5bdc5ef75f9250e48a821eef8fb918c4aea867c` |
| `sedan_stock_steer.obj` | `sedan_mk2_stock_steer.txt` | `22cfbf7e6cee4da838b2298ffa8c1a0902f4acf61b0e0c3139eb60911f7fd328` |
| `sedan_dashboard.obj` | `sedan_mk2_dashboard.txt` | `3b56a23003d9900e1a0139881c4c61979f9b8bebf6c1abac90d2976353502401` |
| `sedan_steering_column.obj` | `sedan_mk2_column_support.txt` | `e664f3d8313b6b545f13deb2eac46837d0d367c9aae88428b3019b835a357171` |
| `sedan_cabin_floor.obj` | `sedan_mk2_cabin_floor.txt` | `e6b0e0a87e41a1d770bfb9605bd1a0c659574d8bf393556d44becd84e94e2164` |
| `sedan_floor_tunnel.obj` | `sedan_mk2_floor_tunnel.txt` | `18611b196ef035e87f4a56589ee2d1b34ff0902edda92865469a3370a77c53be` |
| `sedan_engine_bay_liner.obj` | `sedan_mk2_engine_bay_liner.txt` | `09d712bd433b36948070b29cfd9c25bd565aba9ad99b138a21657c1fb167f388` |
| `sedan_trunk_liner.obj` | `sedan_mk2_trunk_liner.txt` | `0e6fe924343cd743940e9b161589571d8598f8c2a259cf8fa1f53a5020cc0223` |
| `sedan_palette.png` | `sedan_mk2_palette.png` | `df13338fc3cd5ac95c0de41b7273ae3d3f9a4c4a815f3113ee898bc3637b276b` |

### Frozen original sedan sources (unchanged)

| File | SHA-256 |
|---|---|
| `jeep_wheel_albedo.png` | `7acdadfdb30488d32b32458359470d6ee88957e7b0aec3b02b71372c55534c90` |
| `sedan_body.txt` | `6682845c86063fc8e48e4d59a4b2ad485ef2d95e46c0584e99ff430a4792b2b7` |
| `sedan_glass_l_front.txt` | `813cc5929dfa8bbcc9097738a5b6097c7ba781c716d5b4fb7b21907a78bb1e85` |
| `sedan_glass_l_rear.txt` | `c4de9c451e89131b9b29f96dfcdd83f5f8a6297b43fbc3455c528d210202b2c5` |
| `sedan_glass_r_front.txt` | `f16a0dbbf0ba7be9925984596f9cd4c27be635ab8e7b5add66b4d7102d0f6173` |
| `sedan_glass_r_rear.txt` | `ac26ac3182248212b36c0d528e0171035447b4fffb5f6dd63dbcb733e1314526` |
| `sedan_glass_rear.txt` | `bf2e5c04d326d4af1389cc7837b6d5c8a6c014f2a3fbf73d6fd92029122abff3` |
| `sedan_glass_windshield.txt` | `7466ef6015e552ed9e40e57c7c3cda6d268f8a8fef394ab7ff04e6125b834132` |
| `sedan_headlights.txt` | `513c6cfa3867d57f3f7dedef1bde3d786e6f1642164b8f79951980af6233fbdb` |
| `sedan_hitch.txt` | `7c61fd2d3db8add6f04043223e65b91468e449d183dffab8d4616f5b6d44b263` |
| `sedan_palette.png` | `df13338fc3cd5ac95c0de41b7273ae3d3f9a4c4a815f3113ee898bc3637b276b` |
| `sedan_seats.txt` | `223feb7f6345720608a81920e15ab30f1d61d8ed5d50cfafc3b8bcb1b2c459bf` |
| `sedan_steer.txt` | `52e096bcab44c183d9345aab9d804f82052fc38abad7248993706eefb677a064` |
| `sedan_taillights.txt` | `47488dc09847d2e8c0b7ead2627015dcbf7a8bb8c331b2afc86d93d8828fac75` |
| `sedan_wheel.txt` | `0f69e9fc1e9d4f12cae59bf83dca7e2a540dc1eb1018722d360f3e5c0a261e73` |
