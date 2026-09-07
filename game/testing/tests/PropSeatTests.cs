using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Sitting on furniture (master 2026-09-07: "wire up sitting on couches, chairs, benches, etc").
    ///
    /// The dangerous failure here is not "F does nothing" -- it is standing up. SitDown disables the player's
    /// collision shapes, exactly as getting into a car does, and a StandUp that fails to put them back leaves
    /// the player falling through the map with no way to notice until they are under the terrain. So the
    /// collider is checked on BOTH sides, and it is checked after an unusual exit (dying in the chair) as well
    /// as after the normal one.
    ///
    /// The second thing worth pinning is the geometry, because it is DERIVED rather than tuned. rig.json puts
    /// the Spine 0.735 above the Skeleton root and Idle_Sit rotates bones without moving any of them, so the
    /// player's origin has to go one hip-height BELOW the cushion for the pelvis to land on it. Get the sign or
    /// the magnitude wrong and the character sinks into the couch or hovers over it -- which no assertion about
    /// "is the player sitting" would catch, since the flag would be true either way.
    ///
    /// WHAT THIS DOES NOT PROVE, said plainly: it builds its seats through PropSeat.Spawn and the catalog, not
    /// by running WorldBuilder.PlaceObject over a real world, so a chair placed on PEI having a seat at all is
    /// checked at the source level (the branch and its meta tag exist) rather than by looking at one. That is
    /// the same gap the ladder tests had, and it is called out here rather than papered over.</summary>
    public sealed class PropSeatTests : GameTest
    {
        public override string Name => "prop.sitting";
        public override double TimeoutSimSeconds => 40;

        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");

        static string ReadText(string resPath)
        {
            try { string p = ProjectSettings.GlobalizePath(resPath); return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : ""; }
            catch { return ""; }
        }

        static bool ColliderOn(Node p)
        {
            foreach (var c in p.FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs && !cs.Disabled) return true;
            return false;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            // ---- THE CATALOG IS REAL DATA, not a fixture. A seats.txt that failed to generate, or that
            // generated anchors floating outside the prop, is the single most likely way this feature breaks
            // silently: every seat would still exist and sit you somewhere wrong.
            var cat = WorldBuilder.LoadSeatCatalog(Dir);
            T.Check($"seats.txt loaded ({cat.Count} props)", cat.Count >= 10);
            T.Check("...including a chair, a couch and a bench",
                    cat.ContainsKey("Chair_Wood_0") && cat.ContainsKey("Couch_0") && cat.ContainsKey("Bench_Wood_0"));
            int seats = 0; foreach (var kv in cat) seats += kv.Value.Count;
            T.Check($"...{seats} seats over them, and a couch seats more than one",
                    seats >= 20 && cat["Couch_0"].Count >= 2);

            // Every anchor must be INSIDE its own prop's mesh footprint. A sign flip or a swapped axis in the
            // extractor would leave the seat count right and put the sitter beside the furniture.
            int outside = 0; string worst = "";
            foreach (var kv in cat)
            {
                var mesh = ObjMesh.Load(Dir + kv.Key + ".obj");
                if (mesh == null) { T.Fail($"{kv.Key}.obj loads"); continue; }
                var aabb = mesh.GetAabb().Grow(0.05f);
                foreach (var (pos, face) in kv.Value)
                    if (!aabb.HasPoint(pos)) { outside++; worst = $"{kv.Key} {pos}"; }
            }
            T.Check($"every seat anchor sits inside its own prop's bounds ({outside} outside{(outside > 0 ? ", e.g. " + worst : "")})", outside == 0);

            // ---- A SEAT, BUILT THE WAY PlaceObject BUILDS ONE: the prop's placement transform, the catalog's
            // raw obj-space anchor, and the same standing-up basis the interior props are placed with.
            var standUp = new Basis(Vector3.Right, Mathf.DegToRad(270f));
            var propXf = new Transform3D(standUp, new Vector3(0f, 0f, 0f));
            var defs = cat["Couch_0"];
            var made = new List<PropSeat>();
            foreach (var (pos, face) in defs)
            {
                var ps = PropSeat.Spawn(World, propXf, pos, face);
                ps.GroundY = propXf.Origin.Y;
                made.Add(ps);
            }
            T.Check($"a couch spawned {made.Count} seats", made.Count == defs.Count && made.Count >= 2);
            T.Check($"...level with each other ({made[0].Anchor.Origin.Y:0.000} vs {made[1].Anchor.Origin.Y:0.000})",
                    Mathf.IsEqualApprox(made[0].Anchor.Origin.Y, made[1].Anchor.Origin.Y, 0.001f));
            T.Check($"...and apart, not stacked ({made[0].Anchor.Origin.DistanceTo(made[1].Anchor.Origin):0.00} m)",
                    made[0].Anchor.Origin.DistanceTo(made[1].Anchor.Origin) > 0.3f);
            // The facing is flattened at spawn: a seat on a slope must not tip its sitter into the hillside.
            T.Check($"the seat faces the horizon, not the ground (fwd.y {(-made[0].Anchor.Basis.Z).Y:0.000})",
                    Mathf.Abs((-made[0].Anchor.Basis.Z).Y) < 0.01f);

            var p = Rigs.Player(World, new Vector3(6f, 1f, 6f));
            yield return Ticks(2);
            T.Check("the player starts on foot with a live collider", !p.IsSeatedOnProp && ColliderOn(p));

            // ---- SIT. The hips must land ON the cushion: origin = cushion - HipRest.
            var seat = made[0];
            p.SitDown(seat);
            yield return Ticks(2);
            T.Check("the player is seated", p.IsSeatedOnProp && p.DebugSitting == seat);
            T.Check("...the seat knows who is in it", !seat.Free);
            float hips = p.GlobalPosition.Y + PropSeat.HipRest;
            T.Check($"...hips land on the cushion (hips {hips:0.000} vs seat {seat.Anchor.Origin.Y:0.000})",
                    Mathf.Abs(hips - seat.Anchor.Origin.Y) < 0.01f);
            // ...which is a DIFFERENT claim from "the player is at the cushion". Assert the origin is genuinely
            // below it, or a version that skipped the offset entirely would pass the check above by accident.
            T.Check($"...so the origin is a hip-height BELOW it ({p.GlobalPosition.Y:0.000})",
                    seat.Anchor.Origin.Y - p.GlobalPosition.Y > 0.5f);
            T.Check($"...facing the way the seat faces",
                    Mathf.Abs(Mathf.AngleDifference(p.Rotation.Y, seat.Anchor.Basis.GetEuler().Y)) < 0.02f);
            T.Check("...the collider is off while seated (or the capsule fights the couch)", !ColliderOn(p));
            T.Check("...and the stance says SITTING", p.Stance == EPlayerStance.SITTING);

            // A frozen player STAYS put -- the check that the movement gate actually returns early. Without it
            // gravity walks the player down through the floor while the collider is off.
            var was = p.GlobalPosition;
            for (int i = 0; i < 50; i++) yield return Ticks(1);
            T.Check($"...and does not drift or fall ({(p.GlobalPosition - was).Length():0.000} m over a second)",
                    (p.GlobalPosition - was).Length() < 0.01f);

            // THE SEAT HOLDS AGAINST ANYTHING THAT MOVES YOU. This is the regression for master's report
            // ("chairs are sitting you down where you interacted with them"): the position was written ONCE
            // at SitDown, and the render interpolation ran a frame later and lerped the player back to where
            // they had been standing. Any test that only sits and looks passes against that bug -- including
            // the ones above -- because the thing that moved them was a different subsystem a frame later.
            // So this displaces the player the way that bug did and requires the seat to take them back.
            p.GlobalPosition = was + new Vector3(3f, 0f, 3f);
            yield return Ticks(2);
            T.Check($"a seated player DRAGGED off the chair is put back ({p.GlobalPosition.DistanceTo(was):0.000} m off)",
                    p.GlobalPosition.DistanceTo(was) < 0.01f);

            // An occupied seat is not offered to anyone else.
            T.Check("an occupied seat is not free", !seat.Free);
            var other = made[1];
            T.Check("...but the seat beside it still is", other.Free);

            // ---- STAND. The collider MUST come back.
            p.StandUp();
            yield return Ticks(2);
            T.Check("standing up leaves the seat", !p.IsSeatedOnProp && seat.Free);
            T.Check("...and RESTORES the collider", ColliderOn(p));
            T.Check($"...putting the player on the prop's floor, not on the cushion ({p.GlobalPosition.Y:0.00})",
                    p.GlobalPosition.Y < seat.Anchor.Origin.Y);
            T.Check("...and back to standing", p.Stance != EPlayerStance.SITTING);

            // ---- STANDING UP TWICE IS NOT A CRASH, and does not un-disable anything twice.
            p.StandUp();
            yield return Ticks(1);
            T.Check("standing up when not seated is a no-op", !p.IsSeatedOnProp && ColliderOn(p));

            // ---- SITTING IN A TAKEN SEAT IS REFUSED. Two players, one chair.
            var q = Rigs.Player(World, new Vector3(9f, 1f, 9f));
            yield return Ticks(2);
            q.SitDown(other);
            yield return Ticks(1);
            T.Check("the second player takes the free seat", q.IsSeatedOnProp);
            p.SitDown(other);
            yield return Ticks(1);
            T.Check("...and a third cannot sit on top of them", !p.IsSeatedOnProp && other.Occupant == q);

            // ---- NEAREST SEAT WINS. On a picnic table the seats are metres apart; "the prop's first free
            // seat" would put you across the table from the one you looked at.
            var bench = new List<PropSeat>();
            foreach (var (pos, face) in cat["Bench_Wood_1"])
            {
                var ps = PropSeat.Spawn(World, new Transform3D(standUp, new Vector3(40f, 0f, 40f)), pos, face);
                ps.GroundY = 0f; bench.Add(ps);
            }
            T.Check($"a picnic table has seats down both sides ({bench.Count})", bench.Count >= 4);
            var far = bench[bench.Count - 1];
            var body = new StaticBody3D();
            World.AddChild(body);
            var arr = new Godot.Collections.Array();
            foreach (var ps in bench) arr.Add(ps);
            body.SetMeta(PropSeat.HitMeta, arr);
            var picked = PlayerController.DebugNearestFreeSeat(body, far.Anchor.Origin + new Vector3(0.1f, 0f, 0.1f));
            T.Check($"aiming at one end picks THAT seat, not the first in the list", picked == far);
            far.Occupant = q;   // ...and an occupied one is skipped rather than returned and refused later
            var picked2 = PlayerController.DebugNearestFreeSeat(body, far.Anchor.Origin);
            T.Check("...and an occupied seat is skipped for the next nearest", picked2 != null && picked2 != far);
            far.Occupant = null;

            // ---- DYING IN THE CHAIR. The seat has to be released and the collider restored, or the corpse
            // owns the couch forever and the respawned player falls through the world. This is the same leak
            // the vehicle seats had before EjectFromVehicleOnDeath freed OccupiedSeats.
            q.StandUp();
            yield return Ticks(1);
            p.SitDown(other);
            yield return Ticks(2);
            T.Check("seated again, ready to die in it", p.IsSeatedOnProp && !other.Free);
            p.TakeDamage(10000f);   // the real damage path, so Die() -> EjectFromVehicleOnDeath runs exactly as it does in play
            yield return Ticks(3);
            T.Check("dying in a seat frees it", other.Free && !p.IsSeatedOnProp);
            T.Check("...and gives the collider back", ColliderOn(p));

            // ---- THE WIRING THIS TEST CANNOT DRIVE. A world build is far too heavy for L1, so assert the
            // branch exists at the source level rather than pretending it is covered -- the same thing the
            // mono-TV suite does for its shader branch.
            string wb = ReadText("res://../WorldBuilder.cs");
            if (wb.Length == 0) wb = System.IO.File.Exists(ProjectSettings.GlobalizePath("res://WorldBuilder.cs"))
                ? System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://WorldBuilder.cs")) : "";
            T.Check($"WorldBuilder.cs was readable ({wb.Length} chars)", wb.Length > 500);
            T.Check("...PlaceObject spawns seats from the catalog", wb.Contains("PropSeat.Spawn"));
            T.Check("...and tags them onto the prop body so the look ray can find them",
                    wb.Contains("PropSeat.HitMeta"));

            // ---- LYING ON A BED (master 2026-09-07: "add the same seat idea to beds. im not sure theres a
            // lay down animation. so use the standing pose, layed down").
            //
            // She is right that there is no lay-down clip, and it was worth checking rather than assuming:
            // the rig HAS Idle_Prone, but posed out it is a forward-leaning crawl with the hips still at 0.74
            // and the head only 0.09 above them -- not a body lying flat. So the standing pose pitched back is
            // the honest answer, and the check that MATTERS here is that it really ends up horizontal.
            Bed.DebugResetAll();
            var bed = Bed.Spawn(World, new Vector3(20f, 0f, 20f), 0f);
            yield return Ticks(2);
            T.Check("a bed carries a seat", bed.Seat != null && GodotObject.IsInstanceValid(bed.Seat));
            if (bed.Seat == null) yield break;
            T.Check("...and it is a RECLINING one, not a chair", bed.Seat.Recline);
            T.Check($"...at mattress height ({bed.Seat.Anchor.Origin.Y - bed.GlobalPosition.Y:0.00} above the bed)",
                    Mathf.Abs((bed.Seat.Anchor.Origin.Y - bed.GlobalPosition.Y) - 0.45f) < 0.01f);

            // THE LOAD-BEARING ONE. Take the point where Idle_Stand puts the skull -- 1.32 up the body, off
            // rig.json -- and push it through the lying transform. Standing on the bed it would end up 1.32 in
            // the AIR; laid down it has to end up 1.32 along the bed instead, still at mattress height.
            var headStanding = new Vector3(0f, 1.32f, 0f);
            var headLying = bed.Seat.LieTransform * headStanding;
            T.Check($"the head lands at mattress height, not in the air (y {headLying.Y:0.00} vs seat {bed.Seat.Anchor.Origin.Y:0.00})",
                    Mathf.Abs(headLying.Y - bed.Seat.Anchor.Origin.Y) < 0.05f);
            float alongBed = (headLying - bed.Seat.Anchor.Origin).Dot(-bed.Seat.Anchor.Basis.Z);
            T.Check($"...and 1.3 m UP the bed toward the head end ({alongBed:0.00} m)", alongBed > 1.2f);
            T.Check("...on the bed's centre line, not off the side",
                    Mathf.Abs((headLying - bed.Seat.Anchor.Origin).Dot(bed.Seat.Anchor.Basis.X)) < 0.05f);
            // ...and the feet stay put, which is what makes the anchor the FOOT of the bed rather than a guess.
            T.Check("the feet stay at the anchor", (bed.Seat.LieTransform * Vector3.Zero).DistanceTo(bed.Seat.Anchor.Origin) < 0.001f);

            // A reclining seat does NOT drop the origin a hip-height: the body pivots instead of sinking.
            var r = Rigs.Player(World, new Vector3(20f, 1f, 24f));
            yield return Ticks(2);
            r.SitDown(bed.Seat);
            yield return Ticks(2);
            T.Check("lying down takes the seat", r.IsSeatedOnProp && !bed.Seat.Free);
            T.Check($"...with the origin ON the mattress, not a hip below it ({r.GlobalPosition.Y:0.00} vs {bed.Seat.Anchor.Origin.Y:0.00})",
                    Mathf.Abs(r.GlobalPosition.Y - bed.Seat.Anchor.Origin.Y) < 0.01f);
            T.Check("...and the collider off, same as a chair", !ColliderOn(r));
            r.StandUp();
            yield return Ticks(2);
            T.Check("getting up off a bed frees it and restores the collider", bed.Seat.Free && ColliderOn(r));

            r.QueueFree(); bed.QueueFree();
            body.QueueFree(); p.QueueFree(); q.QueueFree();
            foreach (var ps in made) ps.QueueFree();
            foreach (var ps in bench) ps.QueueFree();
        }
    }
}
