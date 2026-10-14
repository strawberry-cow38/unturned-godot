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
        internal static (float X0, float X1, float Z0, float Z1) LeafIn(WallSurface w, Node3D host)
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

    // strawberry, round 12b: "doors can swing either way, some may swing both ways, swinging away from the player when they
    // open them". A double-acting door (WallOpening.DoorAction DoubleSwing) built into a wall and opened from each side, the
    // way a player's F opens it with no server attached (PlayerController.RequestToggleObjectDoor -> ObjectDoor.ToggleFrom):
    // its leaf swings out of the face AWAY from the opener, and shuts back into its hole. Both jambs. And told mid-shut to
    // open the other way (as the server's answer would), it goes back through the middle rather than jumping across.
    public class BuildToolDoubleDoor : GameTest
    {
        public override string Name => "buildtool.double_door_swings_away_from_its_opener";

        public override IEnumerable<Step> Run()
        {
            var w = new WallSurface { Length = 20f, Height = WallOpenings.DoorHeight, Thickness = WallOpenings.InteriorThickness };
            World.AddChild(w);
            var ways = new[] { (Start: false, Front: false), (Start: true, Front: true) };
            for (int i = 0; i < ways.Length; i++)
                w.Openings.Add(new WallOpening(2f + i * 9f, 0f, 2.5f, 2.85f) { DoorProp = "Door_Pine", DoorHingeAtStart = ways[i].Start, DoorOpensFront = ways[i].Front, DoorAction = DoorAction.DoubleSwing });
            w.Rebuild();
            yield return Step.Ticks(2);

            var hosts = new Node3D[ways.Length]; var doors = new ObjectDoor[ways.Length];
            foreach (var c in w.GetChildren())
                if (c is Node3D h && w.OpeningIndexForDoorHost(h) is int k && k >= 0) { hosts[k] = h; foreach (var d in h.GetChildren()) if (d is ObjectDoor od) doors[k] = od; }
            for (int i = 0; i < ways.Length; i++) T.Check($"opening {i} has its door, double-acting", doors[i] != null && doors[i].DoubleActing);
            if (System.Array.Exists(doors, d => d == null)) yield break;

            for (int i = 0; i < ways.Length; i++)
            {
                var o = w.Openings[i]; var door = doors[i];
                foreach (float side in new[] { 1f, -1f })
                {
                    var opener = w.ToGlobal(new Vector3(o.U + o.Width * 0.5f, 1f, side * 2.5f));
                    for (int k = 0; k < 240 && !door.ToggleFrom(opener); k++) yield return Step.Ticks(1);
                    yield return Until(() => door.IsOpen && door.DebugSwing >= 0.999f, 3);
                    var l = BuildToolDoorSwing.LeafIn(w, hosts[i]);
                    bool away = side > 0 ? l.Z1 < 0.5f && l.Z0 < -1.5f : l.Z0 > -0.5f && l.Z1 > 1.5f;
                    T.Check($"door {i} (built hinge {(ways[i].Start ? "start" : "end")}, {(ways[i].Front ? "front" : "back")}) opened from {(side > 0 ? "+Z" : "-Z")}: its leaf swings away, z {l.Z0:0.00}..{l.Z1:0.00}", away);
                    var col = door.DebugLeafCollider;
                    float cz = col == null ? 0f : w.ToLocal(col.GlobalTransform.Origin).Z;
                    T.Check($"door {i}'s leaf collider is on that side too (z {cz:0.00})", col != null && (side > 0 ? cz < -0.6f : cz > 0.6f));
                    for (int k = 0; k < 240 && !door.ToggleFrom(opener); k++) yield return Step.Ticks(1);
                    yield return Until(() => !door.IsOpen && door.DebugSwing <= 0.001f, 3);
                    l = BuildToolDoorSwing.LeafIn(w, hosts[i]);
                    T.Check($"door {i} shuts back into its hole: x {l.X0:0.00}..{l.X1:0.00} in {o.U:0.00}..{o.U1:0.00}, z {l.Z0:0.00}..{l.Z1:0.00}",
                            l.X0 > o.U - 0.01f && l.X1 < o.U1 + 0.01f && l.Z0 > -0.4f && l.Z1 < 0.4f);
                }
            }

            // told to open the other way while it is still out: back through the middle, never across it in a jump
            {
                var o = w.Openings[0]; var door = doors[0];
                door.ApplyReplicatedOpen(true, reverse: true);
                yield return Until(() => door.DebugSwing >= 0.999f, 3);
                door.ApplyReplicatedOpen(false, reverse: true);
                yield return Step.Ticks(8);
                float Mid() { var q = BuildToolDoorSwing.LeafIn(w, hosts[0]); return (q.Z0 + q.Z1) * 0.5f; }
                float was = Mid(), jump = 0f;
                door.ApplyReplicatedOpen(true, reverse: false);
                for (int k = 0; k < 120; k++)
                {
                    yield return Step.Ticks(1);
                    float now = Mid(); jump = Mathf.Max(jump, Mathf.Abs(now - was)); was = now;
                }
                var l = BuildToolDoorSwing.LeafIn(w, hosts[0]);
                // (door 0 is built to swing out of the back, -Z: reversed it went to +Z, re-aimed it comes back to -Z)
                T.Check($"re-aimed mid-shut, it ends out the other side (z {l.Z0:0.00}..{l.Z1:0.00})", l.Z1 < 0.5f && l.Z0 < -1.5f && !door.Reversed);
                T.Check($"...without jumping across: the most its middle moved in a tick was {jump:0.00} m", jump < 0.3f);
            }
        }
    }

    // strawberry, round 12b: "sliding pocket doors too". A pocket door (DoorAction Pocket) slides its leaf along the wall, into
    // it, past the jamb DoorHingeAtStart names -- not turning at all. Opened, the leaf is inside the wall: along it from that
    // jamb's frame to its own width further, within the wall's thickness (bar its handle's 3 mm); and the doorway is clear --
    // a ray across it at chest height meets nothing, where shut it meets the leaf. Shut, it is back in its hole. Both jambs.
    public class BuildToolPocketDoor : GameTest
    {
        public override string Name => "buildtool.pocket_door_slides_into_its_wall";

        public override IEnumerable<Step> Run()
        {
            const float Trim = WallOpenings.TrimProfile;
            var w = new WallSurface { Length = 20f, Height = WallOpenings.DoorHeight, Thickness = WallOpenings.InteriorThickness };
            World.AddChild(w);
            var starts = new[] { false, true };
            for (int i = 0; i < starts.Length; i++)
                w.Openings.Add(new WallOpening(3f + i * 9f, 0f, 2.5f, 2.85f) { DoorProp = "Door_Pine", DoorHingeAtStart = starts[i], DoorAction = DoorAction.Pocket });
            w.Rebuild();
            yield return Step.Ticks(2);

            var hosts = new Node3D[starts.Length]; var doors = new ObjectDoor[starts.Length];
            foreach (var c in w.GetChildren())
                if (c is Node3D h && w.OpeningIndexForDoorHost(h) is int k && k >= 0) { hosts[k] = h; foreach (var d in h.GetChildren()) if (d is ObjectDoor od) doors[k] = od; }
            for (int i = 0; i < starts.Length; i++) T.Check($"opening {i} has its door, sliding", doors[i] != null && doors[i].Sliding);
            if (System.Array.Exists(doors, d => d == null)) yield break;

            // a ray straight across the doorway's middle at chest height, every layer: what stands in the way
            bool Blocked(WallOpening o)
            {
                var a = w.ToGlobal(new Vector3(o.U + o.Width * 0.5f, 1.2f, -1.5f)); var b = w.ToGlobal(new Vector3(o.U + o.Width * 0.5f, 1.2f, 1.5f));
                var q = PhysicsRayQueryParameters3D.Create(a, b, 0xFFFFFFFF);
                return w.GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0;
            }
            float half = w.Thickness * 0.5f;
            for (int i = 0; i < starts.Length; i++)
            {
                var o = w.Openings[i]; var door = doors[i];
                var l = BuildToolDoorSwing.LeafIn(w, hosts[i]);
                float leafW = l.X1 - l.X0;
                T.Check($"pocket {i} shut in its hole: x {l.X0:0.00}..{l.X1:0.00} in {o.U:0.00}..{o.U1:0.00}", l.X0 > o.U - 0.01f && l.X1 < o.U1 + 0.01f && l.Z0 > -0.4f && l.Z1 < 0.4f);
                T.Check($"pocket {i} shut: the doorway is closed (a ray across it meets the leaf)", Blocked(o));

                var opener = w.ToGlobal(new Vector3(o.U + o.Width * 0.5f, 1f, 2f));
                for (int k = 0; k < 240 && !door.ToggleFrom(opener); k++) yield return Step.Ticks(1);
                yield return Until(() => door.IsOpen && door.DebugSwing >= 0.999f, 3);
                yield return Step.Ticks(2);
                l = BuildToolDoorSwing.LeafIn(w, hosts[i]);
                // into the wall past the jamb it was told: its near edge at that jamb's frame, its far edge its width on
                float near = starts[i] ? o.U + Trim : o.U1 - Trim, far = starts[i] ? near - leafW : near + leafW;
                float lo = Mathf.Min(near, far), hi = Mathf.Max(near, far);
                T.Check($"pocket {i} (into the {(starts[i] ? "start" : "end")} jamb) open: leaf x {l.X0:0.00}..{l.X1:0.00}, into the wall at {lo:0.00}..{hi:0.00}",
                        Mathf.Abs(l.X0 - lo) < 0.03f && Mathf.Abs(l.X1 - hi) < 0.03f && l.X0 >= 0f && l.X1 <= w.Length);
                T.Check($"pocket {i} open: the leaf is inside the wall's thickness, z {l.Z0:0.000}..{l.Z1:0.000} of +-{half:0.00}", l.Z0 > -half - 0.01f && l.Z1 < half + 0.01f);
                T.Check($"pocket {i} open: none of it in the clear doorway {o.U + Trim:0.00}..{o.U1 - Trim:0.00}", l.X1 <= o.U + Trim + 0.02f || l.X0 >= o.U1 - Trim - 0.02f);
                T.Check($"pocket {i} open: a ray across the doorway meets nothing", !Blocked(o));
                var col = door.DebugLeafCollider;
                float cx = col == null ? 0f : w.ToLocal(col.GlobalTransform.Origin).X;
                T.Check($"pocket {i}'s leaf collider slid with it (x {cx:0.00}, between {lo:0.00} and {hi:0.00})", col != null && cx > lo && cx < hi);

                for (int k = 0; k < 240 && !door.ToggleFrom(opener); k++) yield return Step.Ticks(1);
                yield return Until(() => !door.IsOpen && door.DebugSwing <= 0.001f, 3);
                yield return Step.Ticks(2);
                l = BuildToolDoorSwing.LeafIn(w, hosts[i]);
                T.Check($"pocket {i} shut again: x {l.X0:0.00}..{l.X1:0.00} in {o.U:0.00}..{o.U1:0.00}", l.X0 > o.U - 0.01f && l.X1 < o.U1 + 0.01f);
                T.Check($"pocket {i} shut again: the doorway is closed", Blocked(o));
            }
        }
    }
}
