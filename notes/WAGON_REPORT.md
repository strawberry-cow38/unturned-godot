# Wagon revision 3 — lamps, bumpers, seating and exhaust

Implemented on `wagon-strip` from `18e58ac0`, after reading that commit's collider correction. Units are metres; +Y is up and forward is −Z. Measurements below were independently read from `game/content/*_body.txt`, the steering/seat/lamp meshes, and the literals in `game/Vehicle.cs` using `tools/measure_vehicles.py`. No appearance judgment is claimed.

The collider remains **BoxSize (2.5, 0.98, 5.52), BoxCenter (0, 0.59, −0.061)**: a fitted lower shell whose top is Y 1.080, below the 1.10 beltline. The separate `RoofBox("Station Wagon")` remains size (2.52, 0.25, 3.36), centre (0, 2.045, 0.88). The existing verifier assertions and runtime sedan control are retained. The previous report's full-body enclosure description was wrong and is superseded here.

## Sedan lamps at both ends

The pre-Golf generator (`f2ff4f3c^:tools/build_wagon.py`) confirms exactly **+0.150 Z for sedan headlights and +0.012 Z for sedan taillights**. Every saved wagon triangle position was compared against the pre-Golf wagon lamps and matches exactly. The new generator copies sedan records and translates only vertices; donor UVs, explicit normals and topology are retained.

| Part | Sedan dimensions X × Y × Z | Translation | Triangles | Wagon light anchors (X,Y,Z) |
| --- | --- | --- | ---: | --- |
| Headlights | 2.048475 × 0.254715 × 0.125399 | (0,0,+0.150) | 20 | Spots (±0.765,0.708,−2.819); omni (0,0.841,−2.795) |
| Taillights | 2.226929 × 0.329078 × 0.189916 | (0,0,+0.012) | 20 | Tails (±0.979,0.688,2.853) |

`VehicleWagonTests` retains both lamp-count assertions, now at the sedan's 20 triangles each. Static checks compare every donor corner and UV, the exact translations and the matching emitters. Outward lens corners clear the fascia by at least 0.014028 and the rear panel by 0.120002; these are geometric clearances, not render results.

## Bumpers lowered 0.035

The supplied six-car statistic is correct to the stated precision. “Nose” here means body vertices forward of the front axle; the minima are on the donor bumper bands.

| Body | Body Y minimum | Nose Y minimum | Nose minus floor |
| --- | ---: | ---: | ---: |
| sedan | −0.273431 | −0.158648 | 0.114783 |
| hatchback | −0.273431 | −0.158648 | 0.114783 |
| golf | −0.273431 | −0.158647 | 0.114784 |
| police | −0.273431 | −0.158648 | 0.114783 |
| van | −0.273432 | −0.158647 | 0.114785 |
| humvee | −0.273431 | −0.158647 | 0.114784 |

Thus +0.115 is the rounded fleet constant. Subtracting the separately rounded −0.273 and −0.159 would give 0.114; the unrounded coordinates explain the apparent discrepancy. The prescribed 0.035 drop is retained, rather than replacing it with a micrometre adjustment.

Both finished hatchback-derived bumper parts move by **(0,−0.035,0)**, from lip Y −0.120 to **−0.155**, exactly **0.115 above wagon floor −0.270**. Every vertex was checked against `18e58ac0`; X/Z, faces, normals and UVs are unchanged. Each remains 44 triangles, with zero boundary edges.

| Bumper | Final Y extent | Unchanged Z extent |
| --- | --- | --- |
| Front | −0.155000..0.104451 | −2.949005..−2.791821 |
| Rear | −0.155000..0.104450 | 2.680000..2.826938 |

The generator lowers the already fitted/capped parts. Re-fitting them against the sloped fascia at the new Y would move Z, contrary to the request. Consequently the front attachment cap now clears that plane by approximately **0.001383**; the rear attachment plane stays at Z 2.680.

**Qualification to the brief:** the wagon's existing flat floor extends to its nose at Y −0.270. Its bumper lip therefore is not the lowest point of the wagon nose, even after lowering. Achieving that separate donor property would require changing the closed body floor. The requested lip-to-floor offset is satisfied and the body is unchanged.

## Steering wheel and front seats

The supplied cabin-front and wheel-front figures match the files when rounded to three decimals. Export jitter across the donor cabin-front corners is at most 8 micrometres.

| Car | Cabin-front Z, approximately | Wheel front Z | Forward poke-through |
| --- | ---: | ---: | ---: |
| sedan | −1.461164 | −1.572887 | 0.111723 |
| hatchback | −1.128024 | −1.246196 | 0.118172 |
| golf | −1.211164 | −1.335727 | 0.124563 |
| wagon before | −1.250000 | −1.572887 | 0.322887 |
| wagon revision 3 | −1.250000 | −1.367887 | **0.117887** |

`wagon_steer.txt` is the sedan's 122-triangle wheel translated **+0.205 Z**, now **Z −1.367887..−1.056433**. `SteerPivot` follows from (−0.464,0.894,−1.416) to **(−0.464,0.894,−1.211)**; the steering axis is unchanged. The wagon uses its own wheel asset so the sedan is unaffected.

Reach is measured from the steering mesh's **Z-bounds centre** to the driver seat-table Z, which reproduces the figures in the brief. It is not measured from the rounded `SteerPivot` literal.

| Vehicle | Wheel-to-driver-seat reach |
| --- | ---: |
| sedan / police | 0.792160 |
| roadster | 0.792094 |
| van | 0.792042 |
| hatchback | 0.791469 |
| humvee | 0.791169 |
| jeep | 0.806760 |
| golf | 0.830000 |
| wagon before / after | **0.792160 / 0.792160** |
| wagon with wheel moved alone | 0.587160 |

All supplied reach figures agree at three decimals. The front table positions are now **(±0.500,−0.079,−0.420)**; rear positions remain **(±0.500,−0.079,+0.772)**. Row spacing is **1.192**, compared with the verified Golf spacing **1.122**.

The new **`wagon_seats.txt`** derives from `sedan_seats.txt`: only the two front seats move +0.205 Z; the rear row is untouched. The sedan mesh has four disconnected geometric components, each with 16 welded positions; its front components have only negative Z and its rear components only positive Z. No triangle crosses that row split. All 96 triangles, UVs and donor normals survive. The verifier compares each vertex's translation against its corresponding driver/passenger/rear seat-table delta, and rejects a table-only or mesh-only move.

There is also an absolute `SeatOf` anchor used for the driver body and camera. The wagon's anchor moves from **(−0.50,−0.04,−0.566)** to **(−0.50,−0.04,−0.361)**, preserving the sedan's body-to-seat offset on server and replica. Without this change, the visible player and camera would remain forward despite the corrected table. Rear passenger body positions remain unchanged.

| Saved seat geometry | Z extent / clearance |
| --- | --- |
| Moved front row, both seats | −0.828915..0.166275 |
| Unchanged rear row | 0.362069..1.357307 |
| Clear separation between rows | **0.195794** |
| Front seatback to load-floor step at 1.360 | **1.193725** |
| Rear seatback to step | **0.002693** |

Disjoint Z bounds prove the front seats intersect neither the rear seats nor the step; the rear row also remains before the step. The narrow existing rear clearance is reported rather than hidden by rounding.

## Exhaust pipe and emitter

The formula in `Vehicle.cs` and the corrected box yield exactly **(0.95,0.28,2.649)**, as supplied. That point is **31 mm inside** the closed rear panel, whose outer face is Z 2.680. A pipe ending there would be buried.

**I extended the outlet past the rear valance. The actual tip and smoke origin are (0.95,0.28,2.730)**, **50 mm outside the panel and 81 mm rearward of the formula point**. This is the explicit departure from the requested formula endpoint needed for an exposed outlet. The new nullable `Spec.ExhaustPos` is set only for the wagon; every other vehicle retains the existing formula. No collider or body surface was moved or opened. Runtime tests include a sedan control for the unchanged default formula.

`wagon_exhaust.txt` is a short six-sided pipe: **28 triangles, 18 positions**, Z **2.550..2.730**, 0.120 across X, with a mouth recessed 0.040 from the tip. It uses the fleet's ordinary solid-part material in dark metal RGB **(0.16,0.17,0.18)** and is registered in `Parts`. The radius, length, recess depth and 50 mm projection are explicit construction choices, not claimed fleet constants: the existing road specs contain no separate pipe part to measure. This is a pipe end without a modelled muffler.

Five static probes through the outlet and rearward smoke path clear both the pipe surface and the closed body. This proves the mouth is geometrically exposed; it does not establish how smoke or materials look in game.

## Verification

- `python3 tools/verify_wagon.py`: **PASS**, all **16 OBJ assets**. Triangles only; positive indices; explicit unit normals; V-flipped UVs; no duplicate or degenerate triangles. Authored faces have normals agreeing with winding. The translated sedan seats and wheel preserve the donor's smooth normals exactly, so those two use donor-normal equality plus outward-orientation checks rather than falsely requiring flat shading.
- Body: **unchanged bytes**, **101 positions, 226 triangles, 0/339 boundary edges**; one connected outward shell, no overlapping coplanar triangles or triangle piercings. All eight glass panes and palette are unchanged. The original straight walls, flat underside, roof rakes and open window/cargo probes remain checked.
- Network registration: all **32 preceding `SpecNames` entries retain their ordering; wagon remains TypeId 32**. Lower-shell and separate roof collider checks, including the sedan non-enclosure control, remain intact.
- `dotnet build game/UnturnedGodot.csproj`: **succeeded, 0 errors**, 54.66 s. The compile emitted **22 warnings from unchanged code/declarations**, so this was not a warning-free compile. No unrelated warning fixes were made.
- `./test.sh --l1 --only 'vehicle.wagon*'`: **PASS, 1 test, 34 checks, 0 failures**, 0.51 s in engine. Its build also succeeded with 0 errors and the same 22 warnings. Checks include server/replica seat, wheel and pipe loading; wheel pivot offsets; driver and rear passenger body anchors; both lamp counts; bumper heights; pipe/smoke alignment; and the retained collider controls.
- Regeneration: **all 17 assets (16 OBJ plus palette) reproduce identical bytes**. No existing sedan or other fleet donor asset changed. `git diff --check` passes.

**I tried reverting each new placement check's input**, ran the complete verifier, required a nonzero exit with the relevant assertion, and restored the saved file in `finally` before the next trial:

| Temporary regression | Observed failure |
| --- | --- |
| Front bumper restored from `18e58ac0` | `bumper lip must be floor +0.115`, Y −0.120 |
| Rear bumper restored separately | Same height assertion, rear Y −0.120 |
| Steering mesh restored to sedan placement | `wheel poke-through outside fleet 0.11..0.13`, 0.322887 |
| Front seat table restored to −0.625 only | `wheel-to-driver-seat reach outside fleet 0.79..0.83`, 0.587160 |
| Seat mesh restored to sedan placement only | `seat mesh disagrees with seat table`, missing +0.205 driver translation |
| Front passenger table restored alone | `seat mesh disagrees with seat table`, passenger delta mismatch |
| Driver body/camera anchor restored | `driver body pose did not follow front row` |
| Smoke override restored to formula point | `smoke must leave pipe tip`, 2.730 versus 2.649 |

The complete verifier passes again after restoring the final files.

**Not verified:** visual appearance, night lighting, smoke animation, actual seated-player rendering, an actual network session, driving, loaded suspension clearance, impacts or the full regression suites. The targeted L1 test builds a server vehicle and local replica; it is not an end-to-end network test. No new visual captures were made, and the existing wagon PNGs show earlier revisions. I do not claim the revised car looks right.

Current coordinates are also recorded in [wagon_dimensions.md](wagon_dimensions.md). No push is performed.
