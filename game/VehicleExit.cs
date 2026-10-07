using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // ---- GETTING OUT AT YOUR OWN DOOR (strawberry 2026-10-04: "rework vehicles to kick you out in the spot where the
    // respective door is, instead of just 'wherevers free'. if your 'door' is blocked and you try to exit, refuse and
    // give feedback to the user") ----
    //
    // WHAT THIS REPLACED. Every seat -- driver, back seat, bus row five -- left by ONE point: 2.4 m off the vehicle's
    // RIGHT flank at its origin. So the driver of a left-hand-drive car got out of the passenger side, and when that
    // point was blocked PlayerController.ClearExitSpot fanned round the car until anything at all was free: a car parked
    // flush to a wall put you out by the bonnet, the boot or the far side, without a word. The server used the same
    // point with no fan at all, so a remote player could be put into the wall the local one walked round.
    //
    // NOW a seat leaves by its door, and the door is DERIVED the same way BuildAccessZones derives the zone you aim at to
    // get in: the seat pushed outboard, on the side it sits, until it is clear of the hull. Getting out is the mirror of
    // getting in, so a vehicle whose seats are right cannot have exits that are wrong.
    //  - A seat off the centreline has ONE door: its own side.
    //  - A seat ON it (quad, tractor, tank driver, the tandem cockpits) has no door to be loyal to, so you step off either
    //    side -- retail's order, even seats left first and odd seats right (InteractableVehicle.tryGetExit:
    //    `seat % 2 == 0 ? -center.right : center.right`).
    //  - A vehicle with a real door (the bus's bi-fold) is left through THAT doorway, from every seat.
    //  - A helm (the ship) is left where the helmsman stands: you let go of the wheel and you are on the deck.
    //
    // BLOCKED IS REFUSED, and the caller says so on screen; the player stays seated. "Blocked" is two questions, because
    // each one alone has a hole the other covers:
    //  1. THE WAY OUT -- a ray at chest height from the seat to the door spot. A wall pressed flat against the door, and
    //     a thin fence between the door and a clear patch beyond it (the patch fits a person; the path to it does not).
    //     Players are not walls: the ray ignores them and they block at (2).
    //  2. ROOM TO STAND -- the player's own capsule at the door spot. A post, a parked car, a person at the door.
    // THE VEHICLE ITSELF IS IGNORED BY BOTH, as retail ignores it (raycastIgnoringVehicleAndChildren). The spot is placed
    // clear of the hull by construction, so letting the hull vote could only ever add a false refusal -- a seat nobody
    // can leave.
    //
    // FORCED exits (dying, a blast, the vehicle despawning) never refuse. They take the door when it is clear and fall
    // back otherwise: being trapped in a burning car because a lamp post stands by the door is not the feature.
    //
    // ONE implementation for SP and MP. The local player's direct exit (PlayerController.TryExitVehicle) and the server's
    // answer to a remote player's request (VehicleNetSync -> ServerVehicles.ResolveExit) both call ResolveDoorExit on
    // the real node, so a door cannot be open for the host and shut for the joiner.
    public partial class Vehicle
    {
        public enum ExitVerdict : byte { Clear = 0, DoorBlocked = 1, NoRoom = 2 }

        /// <summary>What the player is told. One line for both failures: which of the two probes said no is a
        /// detail for the log, not for someone who just wants out.</summary>
        public const string ExitRefusedText = "Your door is blocked";

        /// <summary>A seat within this of the centreline has no side of its own -- the BuildAccessZones threshold, so a
        /// seat you enter from either side is a seat you can leave from either side.</summary>
        public const float ExitCentrelineX = 0.15f;
        /// <summary>Air left between the hull and the player's capsule at the door spot.</summary>
        public const float ExitHullClearance = 0.15f;
        /// <summary>Chest height above the seated origin: where the way-out ray runs. High enough to clear a kerb,
        /// low enough to meet a waist-high wall.</summary>
        public const float ExitChestRise = 0.9f;
        /// <summary>The player's body -- PlayerController's capsule (HEIGHT_STAND x 0.35) and its CollisionMask. The
        /// server has no PlayerController for a remote rider, so the numbers live here; the controller passes its own.</summary>
        public const float ExitPlayerRadius = 0.35f, ExitPlayerHeight = 2.0f;
        public const uint ExitPlayerMask = (1u << 0) | (1u << 6) | RemotePlayers.RemotePlayerLayer | HitMeshBit;
        /// <summary>The bottom of the capsule left out of the room test, so the ground at the door is not the thing
        /// blocking it (PlayerController.CapsuleFits' foot leniency, same number).</summary>
        const float ExitFoot = 0.25f;
        /// <summary>How far below the door spot the ground is looked for. Further than this and you step off and
        /// fall, as retail does -- the spot is never DROPPED onto a seabed or the bottom of a ravine.</summary>
        const float ExitGroundReach = 1.2f;

        /// <summary>Where `seat` gets out, vehicle-local at the seated origin's height, in the order the doors are
        /// tried. Never empty.</summary>
        public List<Vector3> DoorExitsLocal(int seat)
        {
            var doors = new List<Vector3>(3);
            var st = SeatLocal(seat);
            if (AccessRequired) { doors.Add(st); return doors; }   // the helm: stand up where you steered from

            foreach (var z in AccessZones)
                if (z.Kind == AccessKind.BiFold)
                {
                    // The doorway, at the seat's own height so every seat of the bus steps out at the same level.
                    doors.Add(Outboard(new Vector3(z.Center.X, st.Y, z.Center.Z), Mathf.Sign(z.Center.X)));
                    return doors;
                }

            float own = Mathf.Abs(st.X) > ExitCentrelineX ? Mathf.Sign(st.X) : (seat % 2 == 0 ? -1f : 1f);
            doors.Add(Outboard(st, own));
            if (Mathf.Abs(st.X) <= ExitCentrelineX) doors.Add(Outboard(st, -own));
            // ON ITS SIDE, the door facing the ground is not "blocked" by anything that could ever move: it is the car
            // lying on it, and nothing rights an OCCUPIED vehicle (carjack refuses one). Refusing there traps the rider
            // for good, so a rolled vehicle may also be left by the side facing the sky -- your own door still first.
            else if (GlobalTransform.Basis.Y.Normalized().Dot(Vector3.Up) < ExitRolledCos) doors.Add(Outboard(st, -own));
            return doors;
        }
        /// <summary>cos(60 deg): past this much roll or pitch the vehicle counts as lying on its side.</summary>
        const float ExitRolledCos = 0.5f;

        Vector3 Outboard(Vector3 at, float side)
            => new Vector3(side * (HullEdgeAt(at, side) + ExitPlayerRadius + ExitHullClearance), at.Y, at.Z);

        /// <summary>How far out the hull reaches on `side` (local |x|), measured only over the slab a person stepping
        /// out at `at` would occupy -- feet to head, one body-width either side of the door.
        ///
        /// From the COLLISION, not the spec box: the box is retail's main BoxCollider and the car you bump into is the
        /// 1:1 hitbox (HitMesh, the body mesh as a trimesh), which carries the mirrors, arches and running boards the box
        /// does not. Read per door, so a wing mirror at the front does not push the back seat's door a foot further out.
        /// The box still sets a floor under it, for a hull whose shapes have not landed yet (VHACD runs async).</summary>
        float HullEdgeAt(Vector3 at, float side)
        {
            float halfZ = ExitPlayerRadius + ExitHullClearance;
            float y0 = at.Y - 0.2f, y1 = at.Y + ExitPlayerHeight, z0 = at.Z - halfZ, z1 = at.Z + halfZ;
            float edge = Mathf.Max(Mathf.Abs(at.X), side * AccessBoxCenter.X + HullSize.X * 0.5f);

            bool InSlab(Vector3 a, Vector3 b, Vector3 c)
            {
                float lo = Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y)), hi = Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y));
                if (hi < y0 || lo > y1) return false;
                lo = Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)); hi = Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z));
                return hi >= z0 && lo <= z1;
            }
            void Take(Vector3 p) { float o = side * p.X; if (o > edge) edge = o; }

            foreach (var (shape, xf) in OwnShapes())
            {
                if (shape is ConcavePolygonShape3D tri)
                {
                    var f = tri.GetFaces();
                    for (int i = 0; i + 2 < f.Length; i += 3)
                    {
                        Vector3 a = xf * f[i], b = xf * f[i + 1], c = xf * f[i + 2];
                        if (InSlab(a, b, c)) { Take(a); Take(b); Take(c); }
                    }
                    continue;
                }
                // Anything else by its box. Conservative -- a convex piece is no wider than its own AABB -- and these
                // pieces sit INSIDE the trimesh anyway, so the trimesh above is what normally sets the edge.
                var box = xf * ShapeAabb(shape);
                if (box.End.Y < y0 || box.Position.Y > y1 || box.End.Z < z0 || box.Position.Z > z1) continue;
                Take(box.Position); Take(box.End);
            }
            return edge;
        }

        /// <summary>Every enabled collision shape that belongs to THIS vehicle's hull, with its transform into
        /// vehicle-local space: the body's own shapes and those on the static hitbox bodies it carries (HitMesh, the
        /// ship's HullMesh). Skipped: the TURRET's body -- it swings, and a barrel trained abeam would push the
        /// driver's door five metres out -- and any rigid body (a wheel blown off is a child lying in a field).</summary>
        IEnumerable<(Shape3D shape, Transform3D xf)> OwnShapes()
        {
            foreach (var n in GetChildren())
            {
                if (n is CollisionShape3D cs) { if (!cs.Disabled && cs.Shape != null) yield return (cs.Shape, cs.Transform); continue; }
                if (n is not StaticBody3D sb || sb == _turretHit) continue;
                foreach (var m in sb.GetChildren())
                    if (m is CollisionShape3D c && !c.Disabled && c.Shape != null) yield return (c.Shape, sb.Transform * c.Transform);
            }
        }

        static Aabb ShapeAabb(Shape3D s)
        {
            switch (s)
            {
                case BoxShape3D b: return new Aabb(-b.Size * 0.5f, b.Size);
                case SphereShape3D sp: return new Aabb(-Vector3.One * sp.Radius, Vector3.One * sp.Radius * 2f);
                case CapsuleShape3D cp: return new Aabb(new Vector3(-cp.Radius, -cp.Height * 0.5f, -cp.Radius), new Vector3(cp.Radius * 2f, cp.Height, cp.Radius * 2f));
                case CylinderShape3D cy: return new Aabb(new Vector3(-cy.Radius, -cy.Height * 0.5f, -cy.Radius), new Vector3(cy.Radius * 2f, cy.Height, cy.Radius * 2f));
                case ConvexPolygonShape3D cv:
                {
                    var pts = cv.Points;
                    if (pts.Length == 0) return new Aabb();
                    var bb = new Aabb(pts[0], Vector3.Zero);
                    foreach (var p in pts) bb = bb.Expand(p);
                    return bb;
                }
                default: return s.GetDebugMesh()?.GetAabb() ?? new Aabb();
            }
        }

        /// <summary>This vehicle and every physics body hanging off it (hitbox, deckhouse, turret, wheels), appended to
        /// `into`: what the exit probes look straight through.</summary>
        public void AddOwnBodyRids(Godot.Collections.Array<Rid> into)
        {
            into.Add(GetRid());
            foreach (var n in FindChildren("*", "CollisionObject3D", true, false))
                if (n is CollisionObject3D co) into.Add(co.GetRid());
        }
        Godot.Collections.Array<Rid> OwnBodiesPlus(Godot.Collections.Array<Rid> extra)
        {
            var ex = new Godot.Collections.Array<Rid>();
            AddOwnBodyRids(ex);
            if (extra != null) foreach (var r in extra) ex.Add(r);
            return ex;
        }

        /// <summary>A joined client's PRIVATE copy of the car it is driving (ClientWorldSession.BuildLocalVehicle). It is
        /// not in the server's world -- except in a shared-tree L1 host, where client and server are one scene, and the
        /// copy stands exactly where the server's car does. Its hitbox would then wall in every seat from the inside,
        /// so the server's door probe looks through anything in this group. Never populated on a real server.</summary>
        public const string ClientTwinGroup = "net_client_twin";

        /// <summary>Can `seat` get out through its door, and where (`spot`, world, feet). Tries the seat's doors in
        /// order and returns at the first clear one; when none is, `spot` is the first door's unprobed spot (what a
        /// forced exit starts its search from) and the verdict is the FIRST door's -- your own door is the one you
        /// are being told about. `exclude` is the rider's own body, when there is one in this world.
        ///
        /// No physics world to ask (a test sandbox, a node outside the tree) -> Clear at the first door: an exit is
        /// never refused on the word of a world that is not there, the CapsuleFits rule.</summary>
        public ExitVerdict ResolveDoorExit(int seat, Godot.Collections.Array<Rid> exclude, out Vector3 spot,
                                           uint mask = ExitPlayerMask, float radius = ExitPlayerRadius, float height = ExitPlayerHeight)
        {
            var doors = DoorExitsLocal(seat);
            spot = ToGlobal(doors[0]);
            var space = IsInsideTree() ? GetWorld3D()?.DirectSpaceState : null;
            if (space == null) return ExitVerdict.Clear;

            var ex = OwnBodiesPlus(exclude);
            var chest = Vector3.Up * ExitChestRise;   // WORLD up, not the hull's: on a car lying on its side the hull's up is sideways
            var from = ToGlobal(SeatLocal(seat)) + chest;
            var first = ExitVerdict.Clear;
            foreach (var d in doors)
            {
                var at = ToGlobal(d);
                var v = ProbeDoor(space, from, at, ex, exclude, mask, radius, height, out var stand);
                LastExitProbe = $"seat {seat} door {d} -> {v}";
                if (v == ExitVerdict.Clear) { spot = stand; return v; }
                if (first == ExitVerdict.Clear) first = v;
            }
            return first;
        }
        /// <summary>The last door probe, as a line -- for the log and the tests, so a refusal names which probe said no.</summary>
        public string LastExitProbe = "";

        ExitVerdict ProbeDoor(PhysicsDirectSpaceState3D space, Vector3 fromChest, Vector3 at, Godot.Collections.Array<Rid> ex,
                              Godot.Collections.Array<Rid> self, uint mask, float radius, float height, out Vector3 stand)
        {
            stand = at;
            // 1. THE WAY OUT: seat to door at chest height. Players are excluded from the mask -- a person beside the
            // door is a question of room (2), and someone standing in the car with you is not a wall.
            var path = PhysicsRayQueryParameters3D.Create(fromChest, at + Vector3.Up * ExitChestRise, mask & ~RemotePlayers.RemotePlayerLayer, ex);
            if (space.IntersectRay(path).Count > 0) return ExitVerdict.DoorBlocked;

            // 2a. FIND THE GROUND under the door: a spot below it is lifted onto it, a spot within reach above it is set
            // down on it, and further than that you step off and fall. The VEHICLE COUNTS as ground here -- it is the
            // ship's deck, and the flank of a car lying on its side is what you climb out onto.
            var down = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 1.0f, at - Vector3.Up * ExitGroundReach, mask & ~RemotePlayers.RemotePlayerLayer, self ?? new Godot.Collections.Array<Rid>());
            var g = space.IntersectRay(down);
            if (g.Count > 0) stand = new Vector3(at.X, g["position"].AsVector3().Y + 0.05f, at.Z);

            // 2b. ROOM TO STAND: the player's own capsule there, the floor under its feet left out.
            float h = Mathf.Max(0.1f, height - ExitFoot);
            var q = new PhysicsShapeQueryParameters3D
            {
                Shape = new CapsuleShape3D { Height = h, Radius = radius },
                Transform = new Transform3D(Basis.Identity, stand + Vector3.Up * (ExitFoot + h * 0.5f)),
                CollisionMask = mask,
                Exclude = ex,
            };
            return space.IntersectShape(q, 1).Count > 0 ? ExitVerdict.NoRoom : ExitVerdict.Clear;
        }
    }
}
