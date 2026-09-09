# Wagon strip and fleet refit

Removed the grille material patch, baked front and rear bumpers, trunk handle, both underside bevels, front lower lip, and sedan-derived lamp generation. The fascia is now continuously painted and coplanar down to the floor; the tailgate is a closed painted panel down to the floor. All geometry changes are in `tools/build_wagon.py`; the OBJ files were regenerated.

**Identification audit:** every identification in the request matches the shipped generator at `1d8bbd94`. Nothing remains unidentified. In particular, the follow-up about a completely flat underside identifies the full-length bevel from |X| = 0.99, Y = −0.270 to |X| = 1.26, Y = −0.120. The old front lip's actual Y stations were −0.158648 and −0.125000 (the request rounded the first to −0.159); neither exists in the regenerated body. The grille really was a UV/material patch, and the handle really was a protruding box. Neither required leaving an opening.

## Donors and refitting

All dimensions below come from meshes in `game/content/`, in metres. Width includes the complete left/right pair. Shape descriptions describe source geometry, not an in-game appearance judgment.

**Golf lamps selected as requested.** `golf_headlights.txt` has horizontally pointed hexagonal outlines: **2.111922 × 0.413821 × 0.125399**, **40 triangles**. `golf_taillights.txt` has wide rectangular outlines: **2.048478 × 0.254716 × 0.125398**, **20 triangles**. They replace the sedan's 20-triangle headlights (2.048 × 0.255) and taillights (2.227 × 0.329). Every donor triangle and UV is retained; only Z is translated and flat normals are recalculated from the saved float32 positions.

| Part | Golf-to-wagon translation | Final emitter positions (X,Y,Z) |
| --- | --- | --- |
| Headlights | (0,0,**−0.215180**) | Spots (±0.765,0.708,−2.803180); omni (0,0.841,−2.779180) |
| Taillights | (0,0,**+0.332254**) | Tails (±0.765,0.787,2.756254) |

The generator derives these deltas from the donor outward faces and wagon mounting planes, giving every outward lens corner at least 2 mm clearance. The spec uses the **Golf's** original emitter anchors plus exactly those deltas. The lens backs enter the closed body, as mounting geometry; the Golf taillights retain their donor's open backs. The body itself remains closed.

- **Humvee/jeep/offroad/truck/van alternative:** vertically pointed hexagonal, round-looking headlights, **2.309 × 0.405, 32 triangles**, wider-spaced than the Golf's; their **2.227 × 0.329, 20-triangle** near-square tails would largely restore the old rear shape (van tails also sit about 0.100 m higher).
- **Ambulance alternative:** **2.048 × 0.255, 20-triangle** rectangular headlights and **2.227 × 0.329, 20-triangle** near-square tails; essentially the old sedan lamp silhouettes at different donor coordinates, with less headlight detail than the Golf. Both alternatives would need their own mounting translations and matching emitter updates.

**Hatchback bumpers selected after measuring all three requested candidates.** The offroader spec uses `offroad_body.txt`; there is no `offroader_body.txt`. The bumper bands are geometrically tied to export precision, so there is no honest claim that van or offroader would require more shape distortion. Hatchback requires the least translation and is a civilian-car donor.

| Donor | Front band Z extent | Rear band Z extent | Width × height × depth, front / rear | Front / rear Z translation for this fit |
| --- | --- | --- | --- | --- |
| **Hatchback** | −2.724326..−2.309333 | 2.302789..2.717784 | 2.521863 × 0.259451 × 0.414993 / 2.521864 × 0.259450 × 0.414995 | **−0.224679 / +0.109154** |
| Van | −2.517115..−2.102122 | 2.102120..2.517116 | 2.521863 × 0.259450 × 0.414993 / 2.521864 × 0.259450 × 0.414996 | −0.431890 / +0.309822 |
| Offroader | −2.517115..−2.102122 | 2.102120..2.517116 | Same as van to the displayed precision | −0.431890 / +0.309822 |

Each source bumper has **28 triangles, 16 positions and 0/42 boundary edges**. Extraction selects triangles at the two measured Y levels (−0.158647 and +0.100803, within 3 μm export jitter) and at the appropriate end beyond |Z| > 2.1. It excludes valance, grille and exhaust geometry; no complete body is copied. The verifier independently identifies hatchback source faces 290–317 and 318–345, zero-based.

The selected bands are centered in X and reduced to **X = ±1.26** using scales **0.999261260 front / 0.999260864 rear**: a **0.0739% reduction**, not a stretch across an inset gap. Outer-tip jitter below 3 μm is snapped to the wall planes. They move up **0.038648 / 0.038646 m**, putting their bottom faces at the old sill Y = −0.120 without changing donor height. Van/offroader would cost the same width reduction, height and attachment trimming, plus **0.207211 m more forward translation and 0.200668 m more rearward translation**.

The outward donor contours sit at least **0.080 m** beyond their mounting planes. Buried backs are clipped at the fascia/tailgate planes to avoid overlapping exterior side walls, then closed with attachment caps. Those hidden caps are the only newly authored bumper surfaces. All six outward donor triangles per bumper survive intact within export precision; all other exposed triangles remain on donor surfaces. Final parts have **24 positions and 44 triangles each**, including the trimmed/capped attachment, and are listed in `Parts` as `wagon_bumper_front.txt` and `wagon_bumper_rear.txt`. They use dark solid colour RGB 58/255.

## Flat underside and bounds

The external floor is one plane at **Y = −0.270**, spanning **X = −1.26..1.26**, from the fascia's floor intersection to **Z = 2.68**. Its area is **13.829750 m²**. Both side walls drop vertically onto it. There are no sloping lower bevels or intermediate sub-sill body stations. The internal passenger floor and raised cargo deck are retained.

This preserves the body's lowest Y and its geometric ground clearance. Flattening at **Y = −0.120** instead would raise the underside **0.150 m**, increasing body clearance by that amount. Relative to the unchanged authored wheel-bottom plane (0.25 − 0.60 = −0.35), the nominal body clearance remains **0.080 m**, rather than **0.230 m**; these are geometry measurements, not loaded suspension measurements.

The unchanged upper fascia stations give:

`Z(Y) = −2.792392 + (−2.757828 + 2.792392) × (Y − 0.125) / (0.999953 − 0.125)`.

At Y = −0.270 this is **Z = −2.807996 m** after serialization. Thus the new body AABB is **(−1.26,−0.27,−2.807996)..(1.26,2.17,2.68)**, or **2.520 × 2.440 × 5.487996 m** (X/Y/Z). The separate bumper tips extend the assembled envelope to **Z = −2.949005..2.826938**, total length **5.775943 m**.

`BoxSize = (2.52,2.44,5.776)` and `BoxCenter = (0,0.95,−0.061034)` enclose the full body, lamps and bumpers; Z is rounded outward. The old `(2.5,0.98,5.52)` lower-body box did not enclose even the shipped body. Roof/cabin registration remains intact. The larger full-body box's effects on driving and collisions have not been assessed.

## Rear roof derivation

The D-pillar's exterior rear edge is **(Y,Z) = (1.10,2.68) → (1.92,2.56)**. Its slope is:

`dZ/dY = (2.56 − 2.68) / (1.92 − 1.10) = −0.1463414634`.

Continuing to Y = 2.17 gives:

`roof_rear_z = 2.56 + (2.17 − 1.92) × (2.56 − 2.68) / (1.92 − 1.10) = 2.523414634`.

The generator reads those endpoints from `posts[3]` and derives the value, just as the front does. The serialized top rear edge is **Z = 2.523415**, **36.585 mm forward** of the former vertical cap. The roof top, both side edges and rear cap meet there. D-pillar endpoints, rear glass and the correct A-pillar roof continuation remain unchanged.

## Verification and limits

| Check | Result |
| --- | --- |
| `python3 tools/verify_wagon.py` | **PASS** for all **13 OBJ assets**: three corners per face, positive indices in range, explicit unit normals agreeing with winding, V-flipped UVs, no degenerate or duplicate triangles. Also checks removals, full-width floor, both roof rakes, donor provenance, emitters, enclosure, retained hood, glass, seats, cargo and registrations. |
| Body topology under float32 / ParseObj rules | **101 positions, 226 triangles; 0/339 boundary edges (0.0%)**, every edge used twice with opposite winding; one connected outward shell; no coplanar overlaps or triangle piercings across 3,288 candidate pairs. Signed volume 12.785812 m³. |
| Bumper topology | Each has **0/66 boundary edges (0.0%)**, one connected outward shell, no overlaps or piercings. |
| `dotnet build game/UnturnedGodot.csproj` | **Succeeded, 0 errors**, 57.97 s. First compile emitted **22 warnings from unchanged declarations/call sites** (including generated code); this was not a warning-free fresh compile. No unrelated warning fixes were made. |
| `./test.sh --l1 --only 'vehicle.wagon*'` | **PASS: 1 test, 19 checks, 0 failures**, 0.98 s. Includes server/replica donor bumper loading and hull enclosure. Its incremental build reported 0 warnings and 0 errors. The headless engine logged its experimental separate-rendering-thread warning. |
| Reproducibility | **PASS:** all **14 generated assets** (13 OBJ + palette) produce identical bytes on regeneration. Eight glass panes and the palette remain byte-identical to `1d8bbd94`. |
| Regression sensitivity | The new floor/removal check rejects the shipped body's bevel/lip; the rear-roof check independently rejects its vertical rear cap. |
| Registration | `BuildWagon`, `BuildByName`, `SpecFor`, seat tables and eight glass panes retained. All **32 preceding `SpecNames` entries** retain their exact ordering; wagon remains **TypeId 32**. |

**Not verified:** visual appearance, night lighting, an actual network session, driving, loaded suspension clearance, impacts or full regression suites. The targeted L1 test creates a replica locally; it is not a network-session test. No new visual captures were made, and the existing wagon PNGs document earlier revisions. I do not claim the revised car looks right in game. The body is closed independently of the separate parts; the original Golf taillight backs remain open inside it.

Current coordinates are also recorded in [wagon_dimensions.md](wagon_dimensions.md). Build artifacts are excluded from the commit. Work is committed on `wagon-strip`; nothing is pushed.
