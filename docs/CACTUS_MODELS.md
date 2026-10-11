# Cactus model library

Fourteen original, spike-free low-poly cactus models, approved on 2026-10-09.
The low/broad round-barrel variant A (`Cactus_Barrel_1`) was rejected and is absent.
Surviving model names are not renumbered: barrel variants `_0` and `_2` remain.

## Assets

`game/content/resources/cacti.txt` is the **model inventory**, not a spawn manifest.
Each row uses the existing one-part resource convention:

```text
Cactus_Column_0 1 NONE
```

The corresponding files are `Cactus_Column_0_0.obj` and
`Cactus_Column_0_0_tex.png`. `cacti_manifest.json` records all 14 keys, triangle
counts, bounds, heights and installed file hashes. All models use the same small,
opaque foliage palette; the usual per-part texture filenames make loading simple.

| Family | Models | Variation |
|---|---:|---|
| Column | 3 | straight, stout, leaning |
| Branched | 3 | two arms, one high arm, three unequal arms |
| Barrel | 2 | round, taller offset crown |
| Paddles | 3 | five-pad fan, low spreading fan, narrow upright fan |
| Cluster | 3 | three stems, twin stems, four stems |

Models range from 120 to 360 triangles; 2,592 triangles across the whole library.
The five `_0` bodies preserve their original corner positions, normals, UVs and
palette after the spike components were removed. Variants change authored shapes,
arm/pad arrangements or clump membership, rather than just an instance scale.

## Coordinate/material contract

- **Native +Y up**, X/Z horizontal, metres at scale 1. These are already in the
  raw resource-mesh convention accepted by the default `ObjMesh.Load` path.
- Ground/root datum is **Y=0**, with a small buried root skirt (4–8 cm).
  Do not auto-recentre, floor-align or rotate by the Z-up prop convention.
- Explicit unit normals, outward CCW OBJ faces; the loader reverses triangle winding.
- One solid part/material, opaque 32x4 RGB palette, matte appearance.
- The unused pale atlas tile is not sampled. No spikes, alpha spike cards or spike texture.
- Multiple closed body components intentionally intersect at branch/pad joints;
  this is art geometry, not a boolean-unioned collision mesh.

## Deliberately not changed

No `.bin` placements, biome distribution, resource gameplay IDs, harvest/drop
definitions, collisions, wind rules or LOD/billboard assets are added here.
Existing `resources.txt` order is unchanged, so model shipping does not alter
existing load-order resource instance indices. The inventory is not currently
read by the runtime resource spawner or editor foliage picker; later placement
integration must explicitly choose to use it.

## Rebuild and verify

```sh
python3 tools/models/cacti/author_variants.py
python3 tools/models/cacti/install_cacti.py
./test.sh --l1 --only 'content.cactus_models'
```

The generator uses frozen **spike-free** base-body data and the original foliage
palette. It does not need the rejected spiky models. The installer preserves
geometry while dropping unused Wavefront material-library directives; runtime
textures are supplied by the established resource part naming convention.

The focused L1 loads every shipped model through **the actual `ObjMesh.Load`**
and decodes every texture through **`ContentProvider.LoadOk`**, checking inventory,
nonempty surfaces, complete geometry, Y-up height/root datum, explicit normals,
palette UVs and absence of placements/rejected barrel assets. It does not rely on
`ResourceField`'s missing-`.bin` skip to claim the assets load.