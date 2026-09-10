# Horse report

The measured animal fleet has **220 / 250 / 329 vertices** and **124 / 148 / 188 triangles** (min / median / max), **7 bones and 6 skin binds in every animal**, and **four decoded texture colours in every animal**. Deer and pig use 2×2 textures; cow uses 32×32. The calibrated side renders put their withers at approximately **1.45 / 0.82 / 1.57 m**, respectively. These are pixel measurements against a drawn metre ruler, independently checked against reconstructed skinned vertices. `GetAabb()` was not used. The [measurement tables](ANIMAL_MEASUREMENTS.md) include each hierarchy, raw and posed coordinates, head/body/leg measurements, texture pixel counts, and the existing capsule mismatches.

The house geometry is a small number of solid polygonal body/head sections and one rigid bone per leg. All three share the same named hierarchy and seven clip names. Their complete clip data is species-specific: deer and cow share six of seven bone tracks in each clip; pig only shares the Skeleton track exactly with deer. There is no external shared animal animation file.

## Horse dimensions and their sources

Metres throughout. Values are rounded to centimetres when authored. Multipliers below are explicit modelling choices applied to measured fleet dimensions; they are not measurements of a biological horse. The generator uses the pixel readings in `measure_animals.render_calipers()`. Reconstructed geometry is an independent cross-check to about 0.01 m, not six-decimal visual precision. Pixel-derived generation was checked to reproduce the reviewed rig byte-for-byte (SHA-256 `bc5c0022de391ac231dfe5d82b27cfacdf362c796ee204bfad829401db43e278`).

| Horse quantity | Value | Measured source and arithmetic |
|---|---:|---|
| Withers / `bodyH` | 1.81 | Cow back render (681.5−312)/235 × 1.15 → 1.81; audited back 1.573552; 0.236448 m above cow and 0.354558 m above deer |
| Ground-to-barrel underside / exposed leg target | 1.07 | Deer leg render 168/235 × 1.50 → 1.07; audited span 0.716460; 49.3% longer, even counting deer's below-ground hoof tips |
| Clearance / withers | 0.591160 | 1.07 / 1.81; deer ground-to-belly fraction is 0.657709 / 1.455442 = approximately 0.452 |
| Full height, ear tips | 2.44 | Deer full silhouette 530/235 × 1.08 → 2.44 (geometry audit 2.261929 m); horse tips are also 0.236821 m above deer's 2.203179 m tips relative to Y=0 |
| Barrel length | 1.80 | Fleet median rendered body length 338/235 × 1.25 → 1.80 (geometry median 1.440000 m) |
| Barrel width | 0.73 | Cow width render 135/148 × 0.80 → 0.73; between deer 0.604982 and cow 0.907473 |
| Barrel depth | 0.74 | Withers 1.81 − underside 1.07 |
| Flat underside width | 0.657 | 90% of horse barrel width; fleet bodies have flat undersides across their full width. The smaller bottom bevel keeps the leg edges under the body |
| Hidden leg root Y | 1.316667 | Underside 1.07 + barrel depth 0.74 / 3; overlap closes the gaps exposed by the front view without increasing leg clearance |
| Head core length | 0.64 | Cow core head length render 126/235 × 1.20 → 0.64 (audit 0.537147 m) |
| Head core width | 0.29 | Pig core head width render 48/148 × 0.90 → 0.29 (audit 0.322197 m) |
| Head core height | 0.40 | Deer core head height render 134/235 × 0.70 → 0.40 (audit 0.570745 m) |
| Leg / hoof X thickness | 0.18 | Deer rendered body length (338/235) / 8 → 0.18 |
| Leg / hoof Z thickness | 0.15 | Deer rendered width (90/148) / 4 → 0.15 |
| Ear rise above poll | 0.19 | Pig head height render (89/235) / 2 → 0.19; poll Y = 2.44 − 0.19 = 2.25 |
| Mane width | 0.07 | Pig width render (54/148) × 0.20 → 0.07 |
| Tail drop | 0.64 | Deer leg render (168/235) × 0.90 → 0.64 |
| Eye square | 0.04 | Pig width render (54/148) × 0.12 → 0.04 |
| Health | 170 | Cow health 150 × horse/cow withers ratio (1.81 / 1.573552) = 172.54, rounded to the nearest 10 |

`tools/build_horse.py` expresses the remaining neck, head, mane, tail and hock outline positions as fractions of these dimensions. For example, legs sit at X = ±0.40 × 1.80 = ±0.72 and Z = ±0.73/3; a rear hock offsets by half the 0.18 m hoof thickness. The rear barrel top drops by depth/12 = 0.061667 m. These fractions are stated in the generator so there are no unexplained independent metre coordinates. One bone per leg is retained; no extra knee or hock bones are introduced.

| Geometry quantity | Horse | Fleet min / median / max | Fleet mean |
|---|---:|---|---:|
| Vertices | **324** | 220 / 250 / 329 | 266.333 |
| Triangles | **170** | 124 / 148 / 188 | 153.333 |
| Bones | **7** | 7 / 7 / 7 | 7 |
| Skin binds | **6** | 6 / 6 / 6 | 6 |

The extra ear, mane, tail and forehead surfaces fit inside the fleet's observed vertex and triangle ranges. Straight front-leg profiles omit redundant collinear points; bent rear profiles use ear clipping to avoid reversed and zero-area fan triangles.

| Rest and Idle t=0 mesh bounds, rig local | Value |
|---|---|
| Minimum XYZ | (−1.828000, 0.000000, −0.365000) |
| Maximum XYZ | (1.188000, 2.440000, 0.365000) |
| Complete length × width × height | **3.016000 × 0.730000 × 2.440000 m** |
| Origin fractions inside XYZ bounds | (0.606101, 0.000000, 0.500000) |
| Individual hoof minimum Y | 0 within 0.000002 m on all four legs |
| Capsule height / complete height | 1.81 / 2.44 = 0.741803; the capsule reaches the withers, excluding neck/head/ears |

Length includes the head and tail. The 1.80 m figure is the barrel alone. Mesh length is local X, width is Z; at the agent's 270° rig yaw, length runs along world Z. Horse mesh coordinates are already Y-up and its six binds are inverse global bone rests. The seven bone transforms and all seven deer animation dictionaries are retained exactly. This is still the same skinned JSON and `RiggedCharacter.Build` path, including `skin_index → skin slot → skeleton bone`, not a new loader or a static mesh substitution.

## Texture and registration

The coat is **bay**: brown body, warmer muzzle, dark mane/tail/legs, and an ivory forehead diamond. This distinguishes the horse from tan deer, pink pig and Holstein cow while using the fleet's four-colour palette approach. The PNG is **2×2**, matching the fleet's modal resolution, with exactly one texel each of **#774123, #9E6439, #231C19, #D4D4CB**, all opaque. UVs sample texel centres and the existing loader uses nearest filtering.

The free local animal id is **7**, with appended network species **3**. `AnimalField.Kinds` provides 1.81 m `bodyH` and 170 health. `AnimalCatalog` resolves id 7 to species 3 and both horse asset names. Its obsolete foot values are zeroed for the existing fleet too; current placement already used zero.

PEI's source `Fauna.dat` was parsed read-only: version 3, table `Wild` (329), tier `Passive` (chance 1.0), ids `[1,4,6]`, 60 points. `IncludeHorse` adds 7 once to runtime tables containing one of those herbivores. Both SP and dedicated spawning use this loader. The existing hash selects `[deer,pig,cow,horse]` at `[12,12,20,16]` of the 60 points before distance/terrain filtering. Retail map bytes are not edited. `EditorSpawns` deals in Fauna **table indices**, so its existing Wild points inherit this expansion; there is no separate per-animal editor id list. The procedural island spawn writer has no animal species table to update.

`AnimalReplication` already writes/reads an unrestricted species byte; no packet field or message id is added. `AnimalPuppets` now applies the same `AnimalAgent.RigYawFix` as SP, closing a pre-existing 90° orientation mismatch on the path the horse uses. Panic audio now resolves its prefix through the catalog; there is no horse recording in the repo, so horse panic is silent.

## Files added or changed

| File | Change |
|---|---|
| `game/content/horse_rig.json` | New 324-vertex, 170-triangle skinned horse; deer skeleton and clips |
| `game/content/objects/Animal_Horse_tex.png` | New four-colour 2×2 bay palette |
| `tools/build_horse.py` | Reproducible measured-dimension generator, solid geometry, palette, valid polygon triangulation |
| `tools/measure_animals.py` | Parses all three fleet rigs, resolves skin slots correctly, reconstructs posed geometry, counts palette pixels and writes the measurement tables with render evidence |
| `tools/verify_horse_rig.py` | Re-parses assets and asserts arrays, bounds, skin mapping, finite values, weights, normals, triangle validity, grounded feet and exact clip compatibility |
| `game/AnimalField.cs` | Registers horse id, capsule/health and runtime Fauna expansion; updates relevant comments |
| `game/AnimalCatalog.cs` | Appends id 7/species 3, assets, zero placement offsets and idempotent Fauna inclusion |
| `game/AnimalAgent.cs` | Shares the existing yaw constant with puppets; catalog-based audio prefix |
| `game/AnimalPuppets.cs` | Applies the shared rig yaw before rendering replicas |
| `game/AnimalTestScene.cs` | Calibrated comparison harness: side/opposite/front/rear/top/quarter, ground datum, 0.25 m ticks, 1 m +X/−Z bars, explicit frozen clip/time |
| `game/Main.cs` | Routes the existing animal harness to the partial scene and sizes single/group captures |
| `tools/shot.py` | Adds animal and group scenes using the existing xvfb/Vulkan screenshot path |
| `game/testing/tests/HorseTests.cs` | Fauna/catalog checks and horse through the real loopback materialization test |
| `game/testing/tests/UnifyTests.cs` | Reuses the cow materialization scenario for horse; checks loaded geometry, origin and facing |
| `tests/UnturnedNet.Tests/AnimalReplicationTests.cs` | Includes species 3 in lossy/reordered snapshots and late joining |
| `notes/ANIMAL_MEASUREMENTS.md` | Fleet numbers, definitions, hierarchy, texture counts, capsule flags and render calibration |
| `notes/HORSE_REPORT.md` | This report |
| `notes/horse_views/deer-side.png`, `pig-side.png`, `cow-rest-side.png`, `fleet-top.png` | Fleet measurement evidence |
| `notes/horse_views/horse-side.png`, `horse-other.png`, `horse-top.png`, `horse-quarter.png`, `horse-front.png`, `horse-rear.png`, `horse-walk.png`, `horse-eat.png` | Horse alongside cow and deer, including two posed clip samples |

## Verification and limits

The render sequence used `tools/shot.py`, xvfb, Vulkan/lavapipe and movie mode. **No render used `--headless`.** Side, opposite flank, top, three-quarter, front and rear images were opened and visually inspected. The front view exposed the leg-root gaps; they were corrected. The forehead diamond was also moved outside its supporting face with outward winding. Side rulers establish height and clearance; top reference bars establish the facing and length/width convention. All three comparison animals use the same scale and Y=0 placement.

Viewed captures: [side](horse_views/horse-side.png), [opposite flank](horse_views/horse-other.png), [top](horse_views/horse-top.png), [three-quarter](horse_views/horse-quarter.png), [front](horse_views/horse-front.png), [rear](horse_views/horse-rear.png), [Walk](horse_views/horse-walk.png), [Eat](horse_views/horse-eat.png).

Reproduce from the repository root:

```sh
python3 tools/measure_animals.py
python3 tools/build_horse.py
python3 tools/verify_horse_rig.py
dotnet build game/UnturnedGodot.csproj
ANIMAL=horse,cow,deer UG_ANIMALCAM=side python3 tools/shot.py animal
ANIMAL=horse,cow,deer UG_ANIMALCAM=top python3 tools/shot.py animal
ANIMAL=horse,cow,deer UG_ANIMALCAM=front python3 tools/shot.py animal
ANIMAL=horse,cow,deer UG_ANIMALCAM=quarter UG_ANIMALCLIP=Walk UG_ANIMALTIME=0.16 python3 tools/shot.py animal
./test.sh --l1 --only 'animal.*'
dotnet test tests/UnturnedNet.Tests/UnturnedNet.Tests.csproj --filter FullyQualifiedName~AnimalReplicationTests
```

For isolated review, the renders/tests here set `XDG_DATA_HOME` and `XDG_CONFIG_HOME` to directories inside this checkout's `.shots/`. A fresh render cache was used after mesh revisions because the current rig cache is keyed by file size. The live checkout was not read or written, and nothing was pushed.

- `dotnet build game/UnturnedGodot.csproj` succeeded with **0 errors**. A compiling build reports **22 existing warnings**, the same count as the initial baseline build; none originate in the changed files.
- The asset validator passed: every skin index is in the six-entry **skin** list, every bind resolves to a valid bone, all numeric values are finite, normals/weights are normalized, triangles have positive area and consistent winding, four hoof minima are zero, claimed bounds match within 0.000002 m, and 175 sampled poses across the seven clips remain finite.
- The final engine run passed four tests / 34 checks, including the existing cow materialization regression. `animal.*` supplied three tests / 25 checks: combat, Fauna/catalog inclusion, and actual server → replica → horse rig materialization/facing/removal. The network test passed its loss/reorder/late-join scenario with horse species 3.
- The engine tests logged unsupported keyboard-label lookups under their headless display server; their assertions passed. Screenshot runs logged the renderer's `finalize` thread error during shutdown **after saving the images**, plus the existing leak/experimental-render-thread warnings. These are not being represented as error-free engine logs.
- **I did not verify a live remote-machine multiplayer session or walk PEI in-game to observe horse streaming after terrain/water filtering.** The loopback materialization and in-memory Fauna paths were exercised instead.
- **I did not visually inspect every frame of all seven clips, nor test terrain slopes, combat against the rendered horse, or its death/ragdoll.** The inspected animation samples are Walk at 0.16 s and Eat at 1.8 s. The copied source animal rigs have no populated ragdoll definition.
- The existing animal agents and puppets select clips without advancing their rig animation players. This change preserves that behavior; frozen sampled poses in the harness verify compatibility, not continuous world animation. One bone per leg also retains the fleet's rigid leg swing.

## Second pass — closure, attachments and fleet consistency

This section supersedes the first pass's geometry counts and its claim that the old validator checked “topology.” The second pass began from `91874f30`, on `astra-horse`. **The audit was implemented and run against the original asset before the generator or horse geometry was edited.** All twelve original images were opened first, including the opposite flank, Walk, Eat, front/rear, top and fleet references.

### What the repaired instrument actually measures

`tools/verify_horse_rig.py` now calls `tools/audit_animal_geometry.py`. The audit discovers components from shared vertex positions, independently of the generator's part labels. It welds only export noise within **0.000001 m** for this analysis; hard-normal and UV duplicates remain separate render vertices. Every solid requires each edge exactly twice in opposite directions. Distinct positions within **0.0001 m** are reported by solid name as unwelded cracks. It checks that copies of a shared bind position stay together after skinning and that posed triangles retain nonzero area.

Attachment tests use the **actual triangle surfaces**, including concave outlines. A point must lie strictly inside both surfaces, with over **0.00001 m** clearance, to demonstrate positive-volume overlap. Bounding boxes only reject impossible candidates; they never establish overlap. Crossed-edge candidates also cover intersections with no contained mesh vertices. The returned clearance is a conservative witness distance, **not a measurement of the maximum joint penetration**. An additional root test requires all leg-cap corners and the low neck-root corners to remain inside the barrel; merely retaining a small overlapping sliver does not pass.

The audit samples **814 poses per animal**: rest, every source key time, midpoints between adjacent distinct key times, and 25 uniform times per clip across all seven clips. The earlier 175-pose finite-number check is retained as a separate basic check. The nine adversarial tests in `tools/test_animal_geometry_audit.py` cover an open box, a missing fan triangle, reversed/duplicate faces, a 0.05 mm crack, face-corner duplicates, touching and 1 mm floating solids, crossed solids, disjoint solids with overlapping bounds, a named horse crack, and the original moving-root failure.

**The fleet cross-check came out zero findings for deer, pig and cow.** Inspection corrected an assumption in the request: these three assets have a sewn body/head/leg component, rather than separate closed limb prisms. Each also has two open ear/antler roots buried inside that closed component, and two eye decals. Calling those four surfaces failed solids would be an instrument error. The audit explicitly reports them, requires every root rim to stay inside the closed core, and checks decal support throughout the sampled poses. The decal distance threshold is 7 mm, based on the existing cow's measured 6.804 mm maximum; deer measures up to 2.091 mm and pig 3.216 mm. The horse gets no open-root or decal exception: its eleven components are all closed solids, and its markings are part of the head surface.

| Asset | Original audit | Final audit | Construction checked at every sampled pose |
|---|---|---|---|
| Deer | 0 findings | 0 findings | 1 closed core; 2 buried antler roots; 2 supported eye decals |
| Pig | 0 findings | 0 findings | 1 closed core; 2 buried ear roots; 2 supported eye decals |
| Cow | 0 findings | 0 findings | 1 closed core; 2 buried ear roots; 2 supported eye decals |
| Horse | 3 open patches, 2 non-overlapping joints, exposed roots in several clips | **0 findings** | 11 closed solids; 10 overlapping attachments; buried leg/neck roots |

The original horse findings, by name:

- `eye_left`, `eye_right`, `blaze`: **four boundary edges each**, twelve total. These were lifted one-sided patches. The barrel and leg prisms themselves were closed; claiming missing barrel/leg triangles would be false.
- `mane -> neck` and `tail -> body`: **no volume overlap** in rest or any of the seven clips. Their coincident boundary contact was insufficient.
- `Left_Front -> body` and `Right_Front -> body`: exposed attachment roots during **Run**, despite some remaining volume overlap. The regression test reinstates the old rigid root binding and catches this at Run 0.133333 s.
- `neck -> body`: exposed low attachment roots in **Eat, Glance_0, Glance_1 and Startle**.
- No unwelded near-duplicate crack was found in the original horse or fleet. This is now tested rather than assumed.

### Geometry changes and their evidence

The opposite flank showed the fore/aft socket overhang and bent rear outline clearly. Walk separated the two rear silhouettes and made the old 0.09 m bend look like a zigzag. **That offset was along local X, not Z.** The top view confirmed the legs stayed inside the barrel footprint. Front/rear cameras look down local X and cannot diagnose lateral displacement from that bend. No leg centre was moved laterally: all remain at Z = ±0.73/3.

| Change | Measurement or finding behind it |
|---|---|
| Broader upper leg profiles and tapered barrel ends | Socket ledge in both flank views. Upper X thickness is 1.5 × the existing 0.18 m hoof length = 0.27 m; the lower barrel ends taper inward by 0.18/3 = 0.06 m. The root remains at 1.07 + 0.74/3 = 1.316667 m. |
| Buried leg tops bound to Spine | Run root failures. The hidden cap follows the barrel while the lower vertices follow the existing leg bone. Upper Z thickness rises only from 0.15 to 0.165 m; the hoof width and each leg's Z centre stay fixed. No extra bone or animation edit. |
| Bay coat over the upper legs, black lower points | Low views exposed dark overlap edges against the brown barrel. Affine vertical UVs select the existing coat texel above half the 1.316667 m root height and black below; the nearest-filtered boundary stays consistent across triangles. No geometry or palette change. |
| Small single rear hock | The doubled Walk zigzag. The outer bend along X is reduced from 0.18/2 = 0.09 m to 0.18/8 = 0.0225 m; the redundant inner bend is removed. Hoof position and ground clearance stay fixed. |
| Light belly panel in the closed barrel | The fleet side references show a light underside band absent from the horse. The lower bevel is 0.74/10 = 0.074 m high and uses the existing warmer `#9E6439` texel. Its lowest surface stays at 1.07 m. No overlay or new texture colour. |
| Tapered shoulder and anchored neck base | Hard width step in top/quarter views and posed neck-root failures. Front upper barrel width is 0.73 − 0.8 × 0.29 = 0.498 m; maximum barrel width remains 0.73 m. Neck width grows from the measured 0.29 m head reference to 0.75 × 0.73 = 0.5475 m at its buried rear base. Both basal landmarks follow Spine; the upper neck follows Skull. |
| Continuous shoulder normals | The taper makes several quads nonplanar. Corner normals follow the surface and interpolate across the triangulation, avoiding an artificial diagonal lighting crease. |
| Closed, inlaid eyes and blaze | Twelve open marking edges. The same square eyes and forehead diamond are triangulated into the head, sharing position edges with the surrounding surface and retaining separate palette UVs. |
| Overlapping mane and tail | Zero-volume joint findings. The mane enters its supporting neck by 0.07/3 m at its inner profile; its base follows the same Spine attachment. The tail's upper root extends into the rump by 0.18/3 = 0.06 m in X. |
| Five-point neck/head outlines | Removes unnecessary corners to fund the inlays and stronger leg roots. Head core extents and the overall nose/ear/tail landmarks remain unchanged. |

The final horse is **336 vertices / 188 triangles**, compared with the fleet's **220–329 vertices / 124–188 triangles** and the original horse's **324 / 170**. That is seven vertices above the fleet maximum and 24 below the requested approximate ceiling of 360; triangles equal the fleet maximum. The verifier now enforces both ceilings. There are still **7 bones and 6 skin binds**. Skeleton, all seven deer clip dictionaries, and the first-pass horse bind transforms compare exactly equal. Only mesh vertices' attachment assignments changed.

Withers **1.81 m**, clearance **1.07 m**, barrel length **1.80 m**, maximum width **0.73 m**, overall bounds **(−1.828, 0, −0.365) to (1.188, 2.44, 0.365)**, four grounded hoof minima, bay palette, id **7** and species **3** are preserved. The verifier checks the barrel landmarks separately from the full silhouette. In the regenerated group side image the datum is around row 577.8 and scale is 148.15 px/m: withers around row 310 gives approximately 1.81 m, and the belly edge around row 419 gives approximately 1.07 m (about ±0.01 m reading precision). No culling `GetAabb()` measurement was used, and length is local X / width local Z before the 270° rig yaw.

### Frozen-asset proof

Byte comparisons against `git show 91874f30:<path>` are identical for all three fleet rigs, all three fleet textures, the horse palette PNG, and `notes/ANIMAL_MEASUREMENTS.md`. The requested `git diff --stat -- game/content/deer_rig.json game/content/pig_rig.json game/content/cow_rig.json` is empty. SHA-256 values for the unchanged rigs are:

- Deer: `c63bef452352f8bc3bd5b6c594af3235ea5d89f435d1d4ac09aab528f5814a1a`
- Pig: `76bf8bf8f266bee49faf2ffb8b5b57d69ac4759680e0576c63c359280c73bd18`
- Cow: `12ced2a8cce2c018bfa5277cc96dcf665ea78c96b85a7e66b46075f7bf3a69c4`

### Second-pass validation and limits

`python3 tools/build_horse.py` and `python3 tools/verify_horse_rig.py` pass on the final asset. All four animals have zero geometry findings across their 814 sampled poses. The nine audit regression tests pass. `dotnet build game/UnturnedGodot.csproj` succeeds with 0 errors and 22 existing warnings on the compiling build. Python compilation and `git diff --check` pass.

`python3 tools/render_horse_views.py` reproduces the twelve original captures through `tools/shot.py` and adds six close low-angle captures: rest low/front-quarter and low/rear-quarter, Walk 0.16 s low, Eat 1.8 s low/rear, Run 0.133333 s low, and Startle 0.316667 s low/rear. Each invocation uses xvfb/Vulkan and a fresh per-run XDG rig cache inside this checkout; it requires a new output file before copying it to `notes/horse_views/`.

SECOND_PASS_RESULTS_PENDING

**Not verified:** continuous-time closure between the sampled instants, every possible view of every clip, a complete self-intersection analysis of every deformed triangle, continuous world animation, slopes, death/ragdoll, or a live remote multiplayer session. The audit proves closure and attachment at its sampled poses; it does not certify subjective anatomy. The fixed deer bones still impose simple leg swings and stretched triangles near the anchored roots. The neck remains visibly faceted, and overlapping solids retain some intersection/shading seams. These limitations are not being called a smooth articulated horse. No live-server checkout was read or written, and nothing was pushed.
