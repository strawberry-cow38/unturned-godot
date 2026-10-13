using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // strawberry, round 12: "allow room layouts to decide door swing direction". The layout writes the choice onto the
    // opening (WallOpening.DoorHingeAtStart / DoorOpensFront); this is the check that the GAME builds it -- that the leaf
    // a player opens hangs on that jamb and swings out of that face -- read off the leaf's own geometry in the wall's
    // space, open and shut, for all four ways a door can hang. (The L0 side -- which way the generator hangs each door,
    // and that the furniture keeps out of that sweep -- is BuildingGenTests.DoorsSwingIntoTheRoomsTheyServe.)
    public class BuildToolDoorSwing : GameTest
    {
        public override string Name => "buildtool.door_swings_as_its_opening_says";

        /// <summary>The leaf's extent in the wall's own space (x along it = U, z out of its front face).</summary>
        static (float X0, float X1, float Z0, float Z1) LeafIn(WallSurface w, Node3D host)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            void Walk(Node n)
            {
                foreach (var c in n.GetChildren())
                {
                    if (c is MeshInstance3D mi && mi.Mesh != null && c.GetParent() is Node3D piv && piv.GetParent() is ObjectDoor)
                    {
                        var ab = mi.Mesh.GetAabb();
                        for (int i = 0; i < 8; i++)
                        {
                            var q = w.ToLocal(mi.GlobalTransform * ab.GetEndpoint(i));
                            x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X); z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z);
                        }
                    }
                    Walk(c);
                }
            }
            Walk(host);
            return (x0, x1, z0, z1);
        }

        public override IEnumerable<Step> Run()
        {
            var w = new WallSurface { Length = 26f, Height = WallOpenings.DoorHeight };
            World.AddChild(w);
            var ways = new[] { (Start: false, Front: false), (Start: true, Front: false), (Start: false, Front: true), (Start: true, Front: true) };
            for (int i = 0; i < ways.Length; i++)
                w.Openings.Add(new WallOpening(1.5f + i * 6f, 0f, 2.5f, 2.85f) { DoorProp = "Door_Pine", DoorHingeAtStart = ways[i].Start, DoorOpensFront = ways[i].Front });
            w.Rebuild();
            yield return Step.Ticks(2);

            var hosts = new Node3D[ways.Length];
            foreach (var c in w.GetChildren())
                if (c is Node3D h && w.OpeningIndexForDoorHost(h) is int k && k >= 0) hosts[k] = h;
            for (int i = 0; i < ways.Length; i++) T.Check($"opening {i} has its door", hosts[i] != null);
            if (System.Array.Exists(hosts, h => h == null)) yield break;

            // shut: every leaf in its hole, whichever way it hangs (a door turned half round is centred as a plain one)
            for (int i = 0; i < ways.Length; i++)
            {
                var o = w.Openings[i]; var l = LeafIn(w, hosts[i]);
                T.Check($"door {i} (hinge {(ways[i].Start ? "start" : "end")}, opens {(ways[i].Front ? "front" : "back")}) shut in its hole: x {l.X0:0.00}..{l.X1:0.00} in {o.U:0.00}..{o.U1:0.00}, z {l.Z0:0.00}..{l.Z1:0.00}",
                        l.X0 > o.U - 0.01f && l.X1 < o.U1 + 0.01f && Mathf.Abs((l.X0 + l.X1) * 0.5f - (o.U + o.Width * 0.5f)) < 0.06f && l.Z0 > -0.4f && l.Z1 < 0.4f);
            }

            // open: out of the face it says, resting against the jamb it says
            void Open(Node n) { foreach (var c in n.GetChildren()) { if (c is ObjectDoor d) d.SetInitialState(true); Open(c); } }
            foreach (var h in hosts) Open(h);
            yield return Step.Ticks(1);
            for (int i = 0; i < ways.Length; i++)
            {
                var o = w.Openings[i]; var l = LeafIn(w, hosts[i]);
                float mid = (l.X0 + l.X1) * 0.5f, jamb = ways[i].Start ? o.U : o.U1, other = ways[i].Start ? o.U1 : o.U;
                bool face = ways[i].Front ? l.Z1 > 1.5f && l.Z0 > -0.5f : l.Z0 < -1.5f && l.Z1 < 0.5f;
                bool hinge = Mathf.Abs(mid - jamb) < 0.6f && Mathf.Abs(mid - other) > 1.5f;
                T.Check($"door {i} open out of its {(ways[i].Front ? "front (+Z)" : "back (-Z)")}: z {l.Z0:0.00}..{l.Z1:0.00}", face);
                T.Check($"door {i} open against its {(ways[i].Start ? "start" : "end")} jamb (u {jamb:0.00}): leaf at x {l.X0:0.00}..{l.X1:0.00}", hinge);
                // and what stops a player is where the leaf is: the "something standing in the swing" test a wall's door makes
                // (ObjectDoor.TrySolidify, overlap-checked on this collider) is asked of the leaf as it swung, not of a side
                // assumed for every door. (DoorLogic.CanToggle's arc test belongs to the old building Door and is not on this
                // path: the server passes it no arc.)
                ObjectDoor door = null;
                void Find(Node n) { foreach (var c in n.GetChildren()) { if (c is ObjectDoor d) door ??= d; Find(c); } }
                Find(hosts[i]);
                var col = door?.DebugLeafCollider;
                var cz = col == null ? 0f : w.ToLocal(col.GlobalTransform.Origin).Z;
                T.Check($"door {i}'s leaf collider swung with it (z {cz:0.00})", col != null && (ways[i].Front ? cz > 0.6f : cz < -0.6f));
            }
        }
    }
}
