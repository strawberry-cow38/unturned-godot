using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The concrete barrier spline (strawberry 2026-10-10: "work on getting the road concrete
    /// barricade spline'd like fence road is. should be block road i think?").
    ///
    /// ⭐ THE TWO THINGS THAT ARE EASY TO GET WRONG AND INVISIBLE ON A STRAIGHT, both asserted on a CURVE
    /// with a straight run as the control:
    ///   1. the yaw's PROC-frame Z negation -- a straight run along X has dz = 0, so a mirrored run looks
    ///      perfect and only a bend reveals it. The guardrail tool shipped that bug once.
    ///   2. the chord wedge -- rigid tiles with flat ends splay at a joint, and the backoff that closes it
    ///      must be ZERO on a straight or every straight run shifts.</summary>
    public sealed class BlockRoadTool : GameTest
    {
        public override string Name => "editor.block_road_tool";

        static List<Vector3> Arc(Vector3 c, float r, float fromDeg, float toDeg, int n)
        {
            var pts = new List<Vector3>();
            for (int i = 0; i <= n; i++)
            {
                float a = Mathf.DegToRad(Mathf.Lerp(fromDeg, toDeg, i / (float)n));
                pts.Add(c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
            return pts;
        }

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            yield return Ticks(2);

            // ---- THE PROP IS WHAT THE TOOL THINKS IT IS -------------------------------------------------
            // Measured off the mesh, not taken from the name: 3.00 m long on local +Y, 0.50 across, 1.25 tall.
            var aabb = ContentProvider.ParseObj($"res://content/objects/{EditorBlockRoad.Unit}.obj")?.GetAabb();
            T.Check($"{EditorBlockRoad.Unit} loads", aabb.HasValue);
            if (aabb.HasValue)
            {
                var sz = aabb.Value.Size;
                T.Check($"...it is {EditorBlockRoad.Pitch:0.00} m long on its length axis ({sz.Y:0.00})",
                        Mathf.Abs(sz.Y - EditorBlockRoad.Pitch) < 0.01f);
                T.Check($"...and HalfWidth is half its THICKNESS, not half its length "
                      + $"({EditorBlockRoad.HalfWidth:0.00} vs {sz.X * 0.5f:0.00})",
                        Mathf.Abs(EditorBlockRoad.HalfWidth - sz.X * 0.5f) < 0.01f);
            }

            // ---- A STRAIGHT RUN: exact pitch, no backoff --------------------------------------------------
            var straight = new List<Vector3> { new(0f, 0f, 0f), new(60f, 0f, 0f) };
            int n = EditorBlockRoad.LayPath(objs, null, straight, false, null);
            T.Check($"a 60 m straight lays 20 units ({n})", n == 20);

            var centres = new List<Vector3>();
            foreach (var x in objs.PlacedOf(EditorBlockRoad.Unit)) centres.Add(x.Origin);
            T.Check($"...and they are all placed ({centres.Count})", centres.Count == n);
            centres.Sort((p, q) => p.X.CompareTo(q.X));
            float minGap = float.MaxValue, maxGap = 0f;
            for (int i = 1; i < centres.Count; i++)
            {
                float g = centres[i].DistanceTo(centres[i - 1]);
                minGap = Mathf.Min(minGap, g); maxGap = Mathf.Max(maxGap, g);
            }
            // ⭐⭐ THE CONTROL FOR THE WEDGE. OverlapFor is free on a straight, so this must be EXACTLY the
            // pitch -- if a backoff leaks onto straight runs, every straight barrier creeps short.
            T.Check($"CONTROL: a straight steps exactly {EditorBlockRoad.Pitch:0.00} m "
                  + $"({minGap:0.000}..{maxGap:0.000})",
                    Mathf.Abs(minGap - EditorBlockRoad.Pitch) < 0.002f
                    && Mathf.Abs(maxGap - EditorBlockRoad.Pitch) < 0.002f);

            // ---- THE LONG AXIS LIES ALONG THE RUN ---------------------------------------------------------
            // ⚠ This is the Z-negation catcher. Asserted on BOTH a +X and a +Z run: a mirrored yaw is correct
            // on one of them and wrong on the other, so one axis alone proves nothing.
            foreach (var (label, from, to, dir) in new (string, Vector3, Vector3, Vector3)[]
                     { ("+X", new Vector3(0f, 0f, 200f), new Vector3(60f, 0f, 200f), Vector3.Right),
                       ("+Z", new Vector3(200f, 0f, 0f), new Vector3(200f, 0f, 60f), Vector3.Back) })
            {
                var before = new List<Node3D>(objs.PlacedOfNodes(EditorBlockRoad.Unit)).Count;
                EditorBlockRoad.LayPath(objs, null, new List<Vector3> { from, to }, false, null);
                var nodes = new List<Node3D>(objs.PlacedOfNodes(EditorBlockRoad.Unit));
                float worstDot = 1f, worstTilt = 0f;
                for (int i = before; i < nodes.Count; i++)
                {
                    var lengthAxis = nodes[i].Transform.Basis.Y.Normalized();   // the prop's local +Y
                    worstDot = Mathf.Min(worstDot, Mathf.Abs(lengthAxis.Dot(dir)));
                    worstTilt = Mathf.Max(worstTilt, Mathf.Abs(lengthAxis.Y));
                }
                T.Check($"{label} run: every unit's long axis lies along the run (worst |dot| {worstDot:0.000})",
                        worstDot > 0.99f);
                T.Check($"{label} run: ...and horizontal (worst |Y| {worstTilt:0.000})", worstTilt < 0.02f);
            }

            // ---- A CURVE: the joints close up --------------------------------------------------------------
            var arcPts = Arc(new Vector3(0f, 0f, -600f), 40f, 0f, 90f, 6);
            int beforeArc = new List<Node3D>(objs.PlacedOfNodes(EditorBlockRoad.Unit)).Count;
            int an = EditorBlockRoad.LayPath(objs, null, arcPts, false, null);
            T.Check($"a 40 m-radius quarter arc lays units ({an})", an > 4);
            var arcNodes = new List<Node3D>(objs.PlacedOfNodes(EditorBlockRoad.Unit));
            float arcMax = 0f;
            for (int i = beforeArc + 1; i < arcNodes.Count; i++)
                arcMax = Mathf.Max(arcMax, arcNodes[i].Position.DistanceTo(arcNodes[i - 1].Position));
            // ⭐ ON A BEND THE STEP SHRINKS. Strictly less than the pitch is the whole wedge fix; equal to it
            // means the backoff never fired and the joints are open.
            T.Check($"...and a bend steps SHORTER than the pitch ({arcMax:0.000} < {EditorBlockRoad.Pitch:0.00})",
                    arcMax < EditorBlockRoad.Pitch - 0.001f);
            // ...but not absurdly short: OverlapFor caps at half the pitch.
            T.Check($"...and never collapses past half a unit ({arcMax:0.000})",
                    arcMax > EditorBlockRoad.Pitch * 0.5f - 0.001f);

            // ---- A PATH SHORTER THAN ONE UNIT LAYS NOTHING -------------------------------------------------
            int tiny = EditorBlockRoad.LayPath(objs, null,
                        new List<Vector3> { new(0f, 0f, 900f), new(1.5f, 0f, 900f) }, false, null);
            T.Check($"a 1.5 m path lays nothing ({tiny})", tiny == 0);

            T.Check($"the minimum bend a {EditorBlockRoad.Pitch:0.00} m unit can follow is "
                  + $"{EditorBlockRoad.MinBendRadius:0.0} m", EditorBlockRoad.MinBendRadius is > 9f and < 11f);
            objs.QueueFree(); ed.QueueFree();
        }
    }
}
