using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // THE AUTHORING HALF OF FoliageField.
    //
    // FoliageField was a pure LOADER -- one public method, LoadGrass(), reading baked .bin files the python
    // tools produced. There was no way to add, remove or save a single blade, so "implement foliage into the
    // map editor" (strawberry) is not wiring up an existing system, it is writing the half that never existed.
    //
    // Kept in its own file, and as a partial, so the runtime loader stays readable: the load path is hot and
    // gets read often, and interleaving edit bookkeeping through it would cost more in comprehension than the
    // extra file costs in navigation.
    //
    // WHAT THE SOURCE SAYS THIS HAS TO SUPPORT (retail FoliageEditor, read for behaviour not code):
    //   * three modes, PAINT / EXACT / BAKE, with BAKE the default
    //   * a circular brush -- radius 16 default, falloff 0.5, strength 0.05, radius clamped 0..2048
    //   * placement by raycasting DOWN from brushPos + (x, radius, y) over 2*radius against a surface mask,
    //     seating each instance on the hit point's NORMAL so foliage lies along slopes
    //   * manually placed foliage flagged clearWhenBaked = false -- "Manually placed, should not be cleared"
    //   * removal filtered by ManuallyPlaced / Baked / All
    //
    // That last pair is why this file exists before any UI does: hand-placed and generated foliage are two
    // populations that must survive each other's operations, and that is a FORMAT property. Retrofitting the
    // flag after maps are authored means re-baking all of them.
    public partial class FoliageField : Node3D
    {
        public const int FormatVersion = 2;

        sealed class Cell
        {
            public MultiMeshInstance3D Mmi;
            public Mesh Mesh;
            public Material Mat;
            public readonly List<Transform3D> Xf = new();
            public readonly List<bool> Manual = new();
        }

        sealed class TypeStore
        {
            public readonly Dictionary<(int, int), Cell> Cells = new();
        }

        readonly Dictionary<string, TypeStore> _authoring = new();

        /// <summary>Cell size must match the loader's bucketing or an edited instance lands in a MultiMesh whose
        /// visibility range is computed for somewhere else.</summary>
        public const float AuthorCell = 96f;

        static (int, int) KeyFor(Vector3 pos)
            => ((int)Mathf.Floor(pos.X / AuthorCell), (int)Mathf.Floor(pos.Z / AuthorCell));

        void RegisterAuthoringCell(string type, (int, int) key, MultiMeshInstance3D mmi, Mesh mesh, Material mat,
                                   List<Transform3D> xf, List<bool> manual)
        {
            if (!_authoring.TryGetValue(type, out var ts)) { ts = new TypeStore(); _authoring[type] = ts; }
            var c = new Cell { Mmi = mmi, Mesh = mesh, Mat = mat };
            c.Xf.AddRange(xf);
            c.Manual.AddRange(manual);
            ts.Cells[key] = c;
        }

        public IEnumerable<string> AuthoringTypes => _authoring.Keys;

        public int InstanceCount(string type)
        {
            if (!_authoring.TryGetValue(type, out var ts)) return 0;
            int n = 0;
            foreach (var c in ts.Cells.Values) n += c.Xf.Count;
            return n;
        }

        /// <summary>Test seam: every placed transform for a type, so a test can assert WHERE the brush put
        /// things rather than only how many -- a correct count at the wrong height is the failure that matters.</summary>
        public IEnumerable<Transform3D> DebugInstancesForTest(string type)
        {
            if (!_authoring.TryGetValue(type, out var ts)) yield break;
            foreach (var c in ts.Cells.Values) foreach (var x in c.Xf) yield return x;
        }

        public int ManualCount(string type)
        {
            if (!_authoring.TryGetValue(type, out var ts)) return 0;
            int n = 0;
            foreach (var c in ts.Cells.Values) foreach (var m in c.Manual) if (m) n++;
            return n;
        }

        /// <summary>Place one instance. `manual` marks it as hand-placed, which is what protects it from a
        /// later bake. Returns false only if the type was never loaded -- there is no mesh to instance.</summary>
        public bool AddInstance(string type, Transform3D xf, bool manual)
        {
            if (!_authoring.TryGetValue(type, out var ts)) return false;
            var key = KeyFor(xf.Origin);
            if (!ts.Cells.TryGetValue(key, out var cell))
            {
                // A brush stroke can reach a cell that has never held this foliage. Borrow the mesh and material
                // from any existing cell of the same type rather than re-loading the .obj: they are shared by
                // construction, and a second load would also give it a second material, breaking the
                // NearestFilter opt-out that is applied per instance.
                Cell donor = null;
                foreach (var c in ts.Cells.Values) { donor = c; break; }
                if (donor == null) return false;
                cell = new Cell { Mesh = donor.Mesh, Mat = donor.Mat };
                var mm = new MultiMesh { Mesh = donor.Mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = 0 };
                cell.Mmi = new MultiMeshInstance3D
                {
                    Multimesh = mm, MaterialOverride = donor.Mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = donor.Mmi.VisibilityRangeEnd,
                    VisibilityRangeFadeMode = donor.Mmi.VisibilityRangeFadeMode,
                };
                cell.Mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                AddChild(cell.Mmi);
                ts.Cells[key] = cell;
            }
            cell.Xf.Add(xf);
            cell.Manual.Add(manual);
            Rebuild(cell);
            return true;
        }

        /// <summary>Remove instances of `type` within `radius` of `centre`. `manual`/`baked` select which
        /// populations are eligible -- retail's ManuallyPlaced / Baked / All filter. Returns how many went.</summary>
        public int RemoveInSphere(string type, Vector3 centre, float radius, bool manual, bool baked)
        {
            if (!_authoring.TryGetValue(type, out var ts) || (!manual && !baked)) return 0;
            float r2 = radius * radius;
            int removed = 0;
            foreach (var cell in ts.Cells.Values)
            {
                bool dirty = false;
                for (int i = cell.Xf.Count - 1; i >= 0; i--)
                {
                    if (cell.Xf[i].Origin.DistanceSquaredTo(centre) > r2) continue;
                    if (cell.Manual[i] ? !manual : !baked) continue;
                    cell.Xf.RemoveAt(i); cell.Manual.RemoveAt(i);
                    removed++; dirty = true;
                }
                if (dirty) Rebuild(cell);
            }
            return removed;
        }

        static void Rebuild(Cell cell)
        {
            // MultiMesh has no per-instance visibility and no insert, so an edited cell is rebuilt whole. That is
            // fine at authoring rates -- one 96m cell, not the map -- and it keeps the runtime path (which reads
            // these buffers every frame) free of any edit-time indirection.
            var mm = cell.Mmi.Multimesh;
            mm.InstanceCount = cell.Xf.Count;
            for (int i = 0; i < cell.Xf.Count; i++) mm.SetInstanceTransform(i, cell.Xf[i]);
        }

        /// <summary>Write every loaded type back as v2. Values go out in UNITY space, the same convention the
        /// python bakers write and the loader reads, so an editor-saved file and a tool-baked one are the same
        /// format -- two conventions would be a bug generator with no upside.</summary>
        public void SaveAll(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var (type, ts) in _authoring) SaveType(dir, type, ts);
        }

        void SaveType(string dir, string type, TypeStore ts)
        {
            int total = 0;
            foreach (var c in ts.Cells.Values) total += c.Xf.Count;
            using var bw = new BinaryWriter(File.Create(Path.Combine(dir, type + ".bin")));
            bw.Write(-1);                 // sentinel: not a v1 count
            bw.Write(FormatVersion);
            bw.Write(total);
            foreach (var cell in ts.Cells.Values)
                for (int i = 0; i < cell.Xf.Count; i++)
                {
                    var xf = cell.Xf[i];
                    // The Godot->Unity conversion is the same negation the loader applies on the way in; it is
                    // its own inverse, so one expression serves both directions.
                    var b = xf.Basis;
                    bw.Write(b.X.X); bw.Write(b.X.Y); bw.Write(-b.X.Z);
                    bw.Write(b.Y.X); bw.Write(b.Y.Y); bw.Write(-b.Y.Z);
                    bw.Write(-b.Z.X); bw.Write(-b.Z.Y); bw.Write(b.Z.Z);
                    bw.Write(xf.Origin.X); bw.Write(xf.Origin.Y); bw.Write(-xf.Origin.Z);
                    bw.Write((byte)(cell.Manual[i] ? 1 : 0));
                }
        }
    }
}
