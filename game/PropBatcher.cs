using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Batches identical map props into MultiMeshes so the map costs draws per PROP TYPE instead of per PLACEMENT.
    //
    // WHY. PEI places 4329 objects across 372 types, and every one was its own MeshInstance3D -- 4329 potential
    // draw calls for 372 distinct things. Batched, the floor is 372. Measured on the real placements.txt; the
    // top four types alone are Fence_Wood_0 x317, Fence_Metal_0 x206, Crop_0 x157, Power_Line_0 x131.
    //
    // WHY NOT ONE MULTIMESH PER TYPE. A MultiMeshInstance3D culls as ONE unit, so a map-wide batch has a
    // map-wide AABB and never frustum-culls and never distance-culls -- you would trade 317 cheap culled draws
    // for one uncullable draw of 317 fences and lose. ResourceField hit this exact wall with trees and solved
    // it by bucketing into 64 m CELLS; this is the same fix, so the two systems stay recognisably one pattern.
    //
    // WHY THE LOD LEVEL IS PART OF THE KEY. Props LOD by distance band via VisibilityRangeBegin/End, which on a
    // MultiMeshInstance3D applies to the whole batch. Keying the batch by (type, LOD level, cell) means every
    // instance in a batch shares a band, so the range still means something -- the granularity drops from
    // per-instance to per-64m-cell, which is the deliberate trade. Bands come from the prop's GUID, so all
    // members of a group agree on them by construction.
    //
    // THE ALIVE/DEAD PAIR. A destructible prop's visual lives in an ALIVE batch and its debris in a DEAD batch,
    // and breaking it moves the transform from one to the other (zero-scale is how a MultiMesh hides an
    // instance -- it has no per-instance visibility flag, same trick as ResourceField.SetAlive). So the debris
    // is free: 317 broken fences cost the same one draw as 317 intact ones.
    public sealed class PropBatcher
    {
        /// <summary>Cell edge in metres. Small enough that a cell's props are at a similar distance from the
        /// camera (so a shared LOD band and a shared cull are honest), large enough not to shatter a common prop
        /// into single-instance batches. 64 m matches ResourceField's tree cells.</summary>
        public static readonly float Cell = ParseCell();
        static float ParseCell()
        {
            // UG_BATCHCELL overrides the cell edge. Not a tuning knob for players -- it is the instrument for
            // measuring what batching costs visually. A MultiMeshInstance3D evaluates its visibility range and
            // frustum test ONCE for the whole batch, so a cell is the granularity at which props cull and swap
            // LOD; shrinking it toward zero converges on the old per-prop behaviour, which is how you separate
            // "batching moved a pixel" from "batching broke something".
            var s = System.Environment.GetEnvironmentVariable("UG_BATCHCELL");
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f ? v : 64f;
        }

        /// <summary>Where one instance ended up. Handed back at Add time and only FILLED IN by Flush -- the
        /// MultiMesh cannot exist until the group's size is known, so nothing may read Mm/Index before then.</summary>
        public sealed class Slot
        {
            public MultiMesh Mm;
            public int Index = -1;
            public Transform3D Xf;          // the placed transform, so a hidden instance can be restored exactly
            public bool Placed;             // false until Flush wired it up

            /// <summary>What SetVisible last decided for this instance.
            ///
            /// It exists because a MultiMesh CANNOT BE READ BACK IN A HEADLESS BOOT: the instance transforms
            /// live in the RenderingServer, which is a stub when there is no renderer, so both
            /// GetInstanceTransform and Buffer come back empty (measured -- a bare set(5,6,7)/get round-trip
            /// returns (0,0,0) with a zero-length buffer under L1). An L1 test asserting on the MultiMesh
            /// would therefore fail identically whether the code worked or not.
            ///
            /// So this records the DECISION, not the render. It is enough to prove the routing -- which
            /// instance in which batch got flipped, and that a neighbour sharing the batch did not -- which is
            /// where index bugs live. It cannot prove anything about pixels; that needs the L2 visual tier or
            /// a real boot.</summary>
            public bool Visible { get; private set; } = true;

            /// <summary>Show or hide this one instance. Hiding scales it to nothing and parks it far below the
            /// map: a zero basis alone still leaves a degenerate point at the origin of the batch.</summary>
            public void SetVisible(bool on)
            {
                if (!Placed || Mm == null) return;
                Visible = on;
                Mm.SetInstanceTransform(Index, on
                    ? Xf
                    : new Transform3D(new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), new Vector3(0f, -10000f, 0f)));
            }
        }

        sealed class Group
        {
            public Mesh Mesh;
            public Material Mat;
            public float Begin, End;
            public GeometryInstance3D.ShadowCastingSetting Shadow;
            public bool StartHidden;                    // dead/debris batches start empty-looking
            public readonly List<Slot> Slots = new();
        }

        readonly Dictionary<(string Guid, string Mat, int Lod, bool Dead, int Cx, int Cz), Group> _groups = new();

        public int GroupCount { get; private set; }
        public int InstanceCount { get; private set; }
        public int Batched { get; private set; }        // instances that actually went into a batch

        /// <summary>Queue one instance. `guid` keys the prop TYPE (not the mesh name -- two GUIDs can share a
        /// mesh but carry different LOD tables), `matKey` separates the per-instance material palette variants
        /// that gen_placements rolls, and `dead` selects the debris batch. Returns the slot to fill in later.</summary>
        public Slot Add(string guid, string matKey, int lod, bool dead, Mesh mesh, Material mat,
                        float begin, float end, GeometryInstance3D.ShadowCastingSetting shadow, Transform3D xf)
        {
            if (mesh == null) return null;
            var key = (guid, matKey ?? "", lod, dead,
                       (int)Mathf.Floor(xf.Origin.X / Cell), (int)Mathf.Floor(xf.Origin.Z / Cell));
            if (!_groups.TryGetValue(key, out var g))
            {
                g = new Group { Mesh = mesh, Mat = mat, Begin = begin, End = end, Shadow = shadow, StartHidden = dead };
                _groups[key] = g;
            }
            // A missing LOD .obj hands its band back to the level before it (WorldBuilder extends that level's
            // End). Every member of a group shares a GUID and therefore shares that table, so taking the widest
            // End any member asked for keeps the batch's band identical to what the individual props had.
            if (end > g.End) g.End = end;
            var s = new Slot { Xf = xf };
            g.Slots.Add(s);
            InstanceCount++;
            return s;
        }

        /// <summary>Fold groups too small to justify a draw call of their own into coarser cells.
        ///
        /// The batcher's own numbers said it was not working: 6,498 prop visuals became 2,605 MultiMesh nodes --
        /// 2.5 instances per batch. That is why the A/B measured a LOSS and why batching was defaulted off. A
        /// batch of 2.5 props costs a full draw call and, being a MultiMesh, culls as one unit with a bigger AABB
        /// than the props it replaced -- so it submits MORE geometry than the individual meshes did. Measured:
        /// draws went UP, 5,377 -> 5,675, with batching ON.
        ///
        /// ResourceField had the identical disease (3,126 batches for 3,921 trees) and the identical cause: a
        /// type spread thinly over the map gets one or two instances per 64 m cell. Collapsing the thin groups
        /// there took it to 37 batches for the same 3,921 trees and measured +6% fps. This is that fix, for props.
        ///
        /// Merging only ever happens WITHIN a (guid, material, lod, dead) family, so every member already shares
        /// the mesh, the material and the LOD band -- the merge cannot change what is drawn, only how many draw
        /// calls draw it. End takes the widest and Shadow takes the most conservative, so nothing loses a shadow
        /// or a visibility band it had before.</summary>
        void Coalesce()
        {
            // ⚠⚠ SAME TRAP AS ResourceField, AND FOR THE SAME REASON -- OFF BY DEFAULT (2026-09-12).
            //
            // Collapsing a type into one map-wide batch broke every tree in the game when I did it in
            // ResourceField: a MultiMesh culls as a SINGLE UNIT on the distance to its bounding box, so a
            // map-wide box is always "far" and the visibility range stops meaning anything. Props here carry
            // VisibilityRangeBegin/End as their LOD BANDS, so the identical merge would put every prop in the
            // wrong LOD rather than the wrong existence -- quieter, same cause. Batching is opt-in (UG_BATCH)
            // so this was never live, but a trap that only fires for the next person is still a trap.
            //
            // Opt in with UG_PMERGE=1 alongside UG_BATCH=1. Anything that merges these MUST keep each batch's
            // AABB local, and must be proved with a RENDER -- fps and draw counts both IMPROVED while the world
            // was visibly wrong, so neither can see this class of bug.
            if (System.Environment.GetEnvironmentVariable("UG_PMERGE") != "1") return;
            int singleMax = 512, minPerBatch = 64, coarseMul = 4;
            int.TryParse(System.Environment.GetEnvironmentVariable("UG_PSINGLEMAX"), out singleMax);
            int.TryParse(System.Environment.GetEnvironmentVariable("UG_PMINBATCH"), out minPerBatch);
            if (singleMax <= 0) singleMax = 512;
            if (minPerBatch <= 0) minPerBatch = 64;

            var family = new Dictionary<(string, string, int, bool), int>();
            foreach (var kv in _groups)
            {
                var f = (kv.Key.Guid, kv.Key.Mat, kv.Key.Lod, kv.Key.Dead);
                family.TryGetValue(f, out int n); family[f] = n + kv.Value.Slots.Count;
            }

            var rebuilt = new Dictionary<(string Guid, string Mat, int Lod, bool Dead, int Cx, int Cz), Group>();
            foreach (var kv in _groups)
            {
                var k = kv.Key;
                var f = (k.Guid, k.Mat, k.Lod, k.Dead);
                int total = family[f];
                int cx, cz;
                if (total <= singleMax) { cx = 0; cz = 0; }                                  // whole map, one batch
                else if (kv.Value.Slots.Count >= minPerBatch) { cx = k.Cx; cz = k.Cz; }      // dense: keep its own cell
                else                                                                          // thin: coarser grid, bounded AABB
                {
                    cx = (1 << 20) + (int)Mathf.Floor(k.Cx / (float)coarseMul);
                    cz = (int)Mathf.Floor(k.Cz / (float)coarseMul);
                }
                var nk = (k.Guid, k.Mat, k.Lod, k.Dead, cx, cz);
                if (!rebuilt.TryGetValue(nk, out var g)) { rebuilt[nk] = kv.Value; continue; }
                if (ReferenceEquals(g, kv.Value)) continue;
                g.Slots.AddRange(kv.Value.Slots);
                if (kv.Value.End > g.End) g.End = kv.Value.End;
                if (kv.Value.Begin < g.Begin) g.Begin = kv.Value.Begin;
                if (kv.Value.Shadow == GeometryInstance3D.ShadowCastingSetting.On) g.Shadow = GeometryInstance3D.ShadowCastingSetting.On;
            }
            _groups.Clear();
            foreach (var kv in rebuilt) _groups[kv.Key] = kv.Value;
        }

        /// <summary>Build the MultiMeshes and hang them under `root`. Call once, after every placement is queued.</summary>
        public void Flush(Node root)
        {
            Coalesce();
            foreach (var kv in _groups)
            {
                var g = kv.Value;
                if (g.Slots.Count == 0) continue;
                var mm = new MultiMesh
                {
                    Mesh = g.Mesh,
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    InstanceCount = g.Slots.Count,
                };
                for (int i = 0; i < g.Slots.Count; i++)
                {
                    var s = g.Slots[i];
                    s.Mm = mm; s.Index = i; s.Placed = true;
                    s.SetVisible(!g.StartHidden);
                }
                var mmi = new MultiMeshInstance3D
                {
                    Multimesh = mm,
                    MaterialOverride = g.Mat,
                    CastShadow = g.Shadow,
                    VisibilityRangeBegin = g.Begin,
                    VisibilityRangeEnd = g.End,
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
                };
                // The scene-wide nearest-neighbour sweep would otherwise stamp over the material these props
                // were built with -- same reason ResourceField's chunks join this group.
                mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                root.AddChild(mmi);
                GroupCount++;
                Batched += g.Slots.Count;
            }
            _groups.Clear();
        }
    }
}
