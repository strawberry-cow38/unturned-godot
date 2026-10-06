using Godot;

namespace UnturnedGodot
{
    // How a held barricade mounts to the surface under the reticle. Faithful to UseableBarricade's per-EBuild
    // branches (SDK UseableBarricade.cs): different barricade families accept different surfaces and orient
    // differently. The port's ground DeployablePlacer only ever did Floor (and rejected everything else,
    // DeployablePlacer.cs:61) — Wall + Sticky are the gap for "barricades on structures".
    public enum BarricadeMount
    {
        Floor,   // generic barricades (crate/generator/sign-post): upward surfaces only (normal.y >= 0.01), upright, free player yaw (UseableBarricade.cs:805)
        Wall,    // wall-mount (storage/sign/cage/torch): near-vertical surfaces only (|normal.y| < 0.1), UPRIGHT + yaw snapped to face out of the wall (UseableBarricade.cs:1397,1400)
        Sticky,  // charge/note/clock: ANY surface, lies fully flush — all 3 axes follow the normal (UseableBarricade.cs:1483-1494)
        Ceiling, // hanging fixtures (the pendant lamps): DOWNWARD-facing surfaces only (normal.y <= -0.01), the exact
                 // mirror of Floor. The models are authored hanging from z=0 into negative Z, so they need no flip --
                 // they need the surface test inverted and the standoff hug that Wall already has.
        Window,  // window barricade: snaps INTO a building-editor window opening (UV-projected onto the wall plane, NOT
                 // raycast -- the hole has no collider), one per inside/outside face, sized to the opening; ONLY placeable
                 // when the reticle is on a window opening. No retail analogue -- master 2026-08-31.
        Container, // v56 Storage Adapter: snaps flush onto a STORAGE CONTAINER's side face, back to the box, and is red
                   // anywhere else (strawberry 2026-10-06: "the adapter should snap to the storage container. has to snap
                   // or it wont place -red."). Seated like Wall -- a horizontal normal recoverable from the yaw -- so the
                   // replica re-seats it from (pos, yaw) alone. APPENDED: nothing persists this enum, but order is cheap.
    }

    // The placement ghost for a held BARRICADE — a deployable that mounts on a STRUCTURE surface (wall / floor /
    // ceiling), not just flat ground. The ground DeployablePlacer bails on any surface with normal.y < 0.01 and
    // always stands the model bolt-upright with free yaw; this honours the three real mount families above.
    //
    // Orientation is built on DeployableDef.StandBasis — the src stand-up + yaw, itself a mirror of
    // BarricadeManager.getRotation = Euler(0,angle_y,0) * Euler(angle_x-90,0,0) * Euler(0,0,angle_z)
    // (DeployableDef.cs:383-386; getRotation BarricadeManager.cs:1715-1719). Wall just feeds it a yaw that faces out
    // of the wall; Sticky tips the whole stand-up so the mount axis follows the normal (ceilings included).
    //
    // Valid on the ground OR anything tinyclaw's StructureManager parents into the "structures" group. Attachment is
    // a delegate (CanAttach) so this branch compiles WITHOUT feat/structures: at merge it's pointed at
    // StructureManager.Instance.CanAttach(world, normal); until then the default accepts a hit on a structure/terrain.
    public partial class BarricadePlacer : Node3D
    {
        public DeployableDef Def { get; private set; }
        public BarricadeMount Mount = BarricadeMount.Wall;   // the mount family (from the barricade's build type); Wall = the new capability
        public bool Valid { get; private set; }
        /// <summary>WHY the ghost is red, or null when it is not (strawberry 2026-09-10: polish the deployable
        /// UX). There are five distinct ways a placement fails and the player was shown one undifferentiated red
        /// ghost for all of them -- so "why can't I put it here" had no answer, and the fix was to wave the
        /// cursor about until it turned blue. Kept short enough to read at a glance under the crosshair.</summary>
        public string Reason { get; private set; }
        public Vector3 Point { get; private set; }              // surface contact (raycast hit position)
        public Vector3 Normal { get; private set; } = Vector3.Up;  // the hit surface normal
        public float Yaw { get; private set; }                 // the final yaw fed to StandBasis (player aim for Floor; wall-facing for Wall)
        public float YawOffset;   // R accumulates here: manual spin about the mount axis (src input_y, UseableBarricade.cs:2041)

        // Window mount state: the opening the ghost is snapped to (SnappedOpening = -1 when not on a window). SnappedFace
        // is +1 (the wall's +Z face) or -1 (the -Z face) -- whichever side the camera is on -- so one barricade goes on
        // each side. _windowScale fits the panel to the opening. The spawn (Barricade.PlaceInWindow) reads these back.
        public WallSurface SnappedWall { get; private set; }
        public int SnappedOpening { get; private set; } = -1;
        public WindowOpeningMarker SnappedMarker { get; private set; }   // baked-prop case: the marker the ghost is on (SnappedWall null then)
        public int SnappedFace { get; private set; }
        Vector3 _windowScale = Vector3.One;
        public Vector3 WindowScale => _windowScale;   // the per-opening panel scale, frozen with the placement (PlayerController)
        /// <summary>Container mount: the crate NetId the ghost is snapped to (0 = none, or a container with no server
        /// id -- the direct path). Sent with the place so the server binds THAT box (PlaceDeployableCommand.TargetId).</summary>
        public uint SnappedCrateId { get; private set; }
        /// <summary>v56: the adapter snapped to the container's TOP face (sent as PlaceDeployableCommand.MountUp).</summary>
        public bool SnappedTop { get; private set; }
        public StorageCrate SnappedCrate { get; private set; }

        // Attachment predicate (world point, surface normal, hit collider) -> can a barricade mount here? At merge:
        //   placer.CanAttach = StructureManager.BarricadeAttachHook;   // NOT Instance.CanAttach -- see that method
        // Kept as a hook so feat/barricades builds standalone; null falls back to DefaultAttachable below.
        // The hook ABSTAINS (returns true) off-structure rather than refusing -- a false is read as "may not build
        // here", so wiring CanAttach = "is there a structure face" would brick every ground deployable on open terrain.
        // Carries the COLLIDER as well as the point/normal: the structure gate has to know WHICH piece was hit,
        // and no radius around the point can tell it that (a wall's origin is its base, metres below the hit).
        public System.Func<Vector3, Vector3, Node, bool> CanAttach;

        MeshInstance3D _ghost;
        Aabb _localAabb;
        StandardMaterial3D _arrowMat;

        public void SetDef(DeployableDef def)
        {
            Def = def;
            Mount = def.Mount;   // the barricade def carries its own mount family (Floor/Wall/Sticky); caller may still override
            _ghost?.QueueFree();
            _ghost = Deployable.BuildMesh(def, out _localAabb);
            _ghost.MaterialOverride = DeployablePlacer.InvalidMat;
            AddChild(_ghost);
            _arrowMat = ConnectionPort.ArrowMaterial(ConnectionPort.ArrowRed);
            foreach (var p in def.Ports)
                _ghost.AddChild(ConnectionPort.MakeArrow(p, _arrowMat, p.Pos));
        }

        public void SetGhostVisible(bool v) { if (_ghost != null) _ghost.Visible = v; }

        // Shortest-arc rotation taking world-up to the surface normal (Sticky mounts). Identity on flat ground; a
        // clean 180 on a dead-flat ceiling (the cross product degenerates there, so pick a fixed horizontal flip axis).
        public static Basis AlignUpTo(Vector3 n)
        {
            n = n.Normalized();
            float d = Vector3.Up.Dot(n);
            if (d > 0.99999f) return Basis.Identity;                       // n == +Y (floor / ground)
            if (d < -0.99999f) return new Basis(Vector3.Right, Mathf.Pi);  // n == -Y (ceiling): mount axis -> -Y
            return new Basis(Vector3.Up.Cross(n).Normalized(), Mathf.Acos(Mathf.Clamp(d, -1f, 1f)));
        }

        // The yaw (about world-up) that faces a StandBasis'd barricade OUT of a wall along the horizontal normal.
        // StandBasis(0) faces +Z: the flat-frame "+Y front" maps to +Z under the +90 X stand-up (StandRotX=90), so
        // facing(yaw) = (sin yaw, 0, cos yaw); solving facing == n gives atan2(n.x, n.z).
        public static float YawFacing(Vector3 n) => Mathf.RadToDeg(Mathf.Atan2(n.X, n.Z));

        // The world basis for a given mount family. Floor/Wall keep the model's ground orientation -- StandBasis for a
        // flat-authored mesh, or a bare yaw for a model authored already-vertical (Upright, e.g. the wind turbine;
        // matches Deployable.Spawn so its ghost doesn't lie on its side). Sticky tips that whole thing so the mount
        // axis follows the normal.
        public static Basis MountBasis(BarricadeMount mount, Vector3 n, float yaw, bool upright = false)
        {
            var ground = upright ? new Basis(Vector3.Up, Mathf.DegToRad(yaw)) : DeployableDef.StandBasis(yaw);
            if (mount == BarricadeMount.Container && n.Y > 0.5f)
            {
                // ON TOP of a container: the side-mount pose (front face out along the yaw) tipped back 90 degrees
                // about the horizontal axis, so the front face -- and its pipe sockets -- point straight up and the
                // back lies flat on the lid. Rotating f about (f x up) by +90 takes f to up.
                float r = Mathf.DegToRad(yaw);
                var f = new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
                return new Basis(f.Cross(Vector3.Up).Normalized(), Mathf.Pi * 0.5f) * ground;
            }
            return mount == BarricadeMount.Sticky ? AlignUpTo(n) * ground : ground;
        }

        // Which surfaces this mount family accepts (src per-EBuild normal.y gates).
        static bool SurfaceOk(BarricadeMount mount, Vector3 n) => mount switch
        {
            BarricadeMount.Floor => n.Y >= 0.01f,          // upward-facing only (UseableBarricade.cs:805)
            BarricadeMount.Wall => Mathf.Abs(n.Y) < 0.1f,  // near-vertical only (UseableBarricade.cs:1397)
            BarricadeMount.Ceiling => n.Y <= -0.01f,        // downward-facing only -- the mirror of Floor. A pendant
                                                            // on the ground is the failure this exists to refuse.
            _ => true,                                      // Sticky: anything (UseableBarricade.cs:1483)
        };

        // The final yaw fed to StandBasis for this mount + normal. Floor = the player's aim yaw; Sticky = manual spin
        // only; Wall = the wall's outward facing (src angle_y = LookRotation(normal).eulerAngles.y) and NOTHING ELSE.
        //
        // A wall mount does not take the manual spin, and that is src, not a simplification: UseableBarricade.cs:1595
        // returns false from startSecondary for the whole wall family -- TORCH, CAGE, STORAGE_WALL, SIGN_WALL,
        // BARRICADE_WALL, and the doors -- so R never reaches rotate_y for any of them. The wall normal already fully
        // determines the yaw, and adding to it aims the fixture off its own wall. Nothing observable changed for the
        // metal barricade (the only other Wall def, and a symmetric ProcBox); the cage light is the first asset with
        // a front and a back, where spinning it 90 degrees points the cage into the brickwork.
        float ResolveYaw(float aimYaw, Vector3 n) => Mount switch
        {
            BarricadeMount.Wall => YawFacing(n),
            BarricadeMount.Sticky => YawOffset,
            _ => aimYaw,   // Floor: aim yaw + R (aimYaw already includes YawOffset, see Aim)
        };

        // Standoff off a wall/ceiling along the normal (src point = hit + normal*offset, UseableBarricade.cs:817). The
        // port's DeployableDef.Offset is authored as a GROUND vertical clearance (~mesh half-height); as a wall
        // standoff that floats the piece a metre off the surface, so clamp it to a small hug for Wall/Sticky. A real
        // barricade asset's small Offset passes through unchanged.
        float WallStandoff => Def == null ? 0f : Standoff(Mount, Def);

        /// <summary>The hug off a wall/ceiling along the surface normal, shared with Barricade.PlaceOnSurface so the
        /// ghost and the placed object cannot disagree about where the thing sits.
        ///
        /// A CEILING plate is flush: a canopy is screwed to the slab. Offset's clamped 0.05 hung the pendants a
        /// visible 5 cm below the ceiling, which reads as a floating fixture. 0.005 is a z-fight gap, not a design
        /// number. Offset keeps its OTHER job unchanged -- it also centres the placement clearance sphere (Aim), and
        /// shrinking that for a 0.30-radius dome would put the probe inside the slab it is mounting to.</summary>
        public static float Standoff(BarricadeMount mount, DeployableDef def) =>
            def == null ? 0f : mount == BarricadeMount.Ceiling ? 0.005f
            // A CONTAINER mount is centred on its own body, so the back face is half the depth behind the origin:
            // half-depth + a 1 cm hair puts the back flush on the box. The Wall clamp (0.1) would sink a 0.24-deep
            // adapter 2 cm into the container.
            : mount == BarricadeMount.Container ? def.Size.Y * 0.5f + 0.01f
            : Mathf.Min(def.Offset, 0.1f);

        // Lift so the mesh seats on the surface: Floor stands its base on the point (GroundLift along up); Wall/Sticky
        // hug the surface by WallStandoff along the normal.
        // ⚠ Ceiling deliberately falls into the SECOND branch with Wall/Sticky: it hugs the surface along the
        // normal. Giving it the Floor branch's GroundLift would push a pendant UP through the ceiling slab by its
        // own half-height, which is the same arithmetic that seats a crate on the ground and is exactly wrong here.
        Vector3 MountOrigin() => Mount == BarricadeMount.Floor
            ? Point + Vector3.Up * (Def != null && Def.Upright ? -_localAabb.Position.Y : DeployableDef.GroundLift(_localAabb))
            : Point + Normal * WallStandoff;

        // Run the aim -> point / normal / valid check this frame and move the ghost to match. Returns Valid.
        public bool Aim(Camera3D cam)
        {
            if (Def == null || cam == null) return false;
            if (Mount == BarricadeMount.Window) return AimWindow(cam);   // window barricade UV-projects onto openings (the hole has no collider to raycast)
            if (Mount == BarricadeMount.Container) return AimContainer(cam);   // storage adapter: snaps onto a container face or stays red
            float aimYaw = Mathf.RadToDeg(cam.GlobalRotation.Y) + YawOffset;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = cam.GlobalPosition, dir = -cam.GlobalTransform.Basis.Z;
            var rq = PhysicsRayQueryParameters3D.Create(from, from + dir * Def.Range);
            rq.CollisionMask = 1u << 0;                   // ground / structures / vehicles (src BARRICADE_INTERACT)
            var hit = space.IntersectRay(rq);
            if (hit.Count == 0)                           // aiming at nothing within range -> invalid
            {
                Valid = false; Reason = "Too far away"; Normal = Vector3.Up; Point = from + dir * Def.Range; Yaw = aimYaw; Apply(); return false;
            }
            Vector3 hp = (Vector3)hit["position"], n = ((Vector3)hit["normal"]).Normalized();
            var collider = hit["collider"].As<Node>();
            Rid hitRid = (Rid)hit["rid"];
            Point = hp; Normal = n; Yaw = ResolveYaw(aimYaw, n);
            // valid = this mount family accepts the surface, the clearance sphere is free (excluding the mount surface),
            // and the spot is attachable (a structure/terrain, or StructureManager.CanAttach when wired).
            bool surf = SurfaceOk(Mount, n);
            bool clear = !Overlap(space, hp + n * Def.Offset, Def.Radius, hitRid);   // src OverlapSphere(point, radius, BLOCK_BARRICADE), UseableBarricade.cs:883
            // BOTH gates, not either/or. This was `CanAttach != null ? CanAttach(...) : DefaultAttachable(...)`, so
            // supplying the structure hook silently DROPPED the no-stacking rule and you could plant a barricade on
            // the face of another barricade. The two answer different questions -- "is this surface a legal thing to
            // build on at all" and "does the structure under it agree" -- and both must hold.
            bool attach = DefaultAttachable(collider) && (CanAttach == null || CanAttach(hp, n, collider));
            Valid = surf && clear && attach;
            // Most specific first: an obstructed spot on a legal surface should say "not enough room", not repeat
            // the surface rule that is actually satisfied.
            Reason = surf ? (clear ? (attach ? null : "Can't build on that") : "Not enough room") : SurfaceReason(Mount);
            // submersible device (fluid inlet) parity: valid only on submerged seabed within its water-depth band
            if (Valid && Def.WaterDepthMin >= 0f)
            {
                float depth = DeployableDef.SeaLevel - hp.Y;
                Valid = depth >= Def.WaterDepthMin && depth <= Def.WaterDepthMax;
                if (!Valid) Reason = depth < Def.WaterDepthMin ? "Needs deeper water" : "Water too deep";
            }
            if (Valid) Reason = null;
            Apply();
            return Valid;
        }

        /// <summary>The surface rule for this mount family, phrased as what to DO rather than what is wrong --
        /// "needs flat ground" tells you where to look, "invalid surface" does not.</summary>
        static string SurfaceReason(BarricadeMount m) => m switch
        {
            BarricadeMount.Wall => "Needs a wall",
            BarricadeMount.Window => "Needs a window opening",
            BarricadeMount.Sticky => "Needs a surface",
            BarricadeMount.Container => "Needs a storage container",
            _ => "Needs flat ground",
        };

        // ---- CONTAINER mount (v56 Storage Adapter) ----------------------------------------------------------------

        /// <summary>The storage container a collider belongs to: a StorageCrate up the parent chain (a map shelf's
        /// trimesh body sits two levels under it, a fridge's one), or the crate grid a placed device carries as a
        /// child (the campfire's DeployableCrate). Null for anything else -- which is what turns the ghost red.</summary>
        public static StorageCrate ContainerOf(Node n)
        {
            for (var c = n; c != null; c = c.GetParent())
            {
                if (c is StorageCrate sc) return sc;
                if (c is Deployable d)
                {
                    foreach (var ch in d.GetChildren()) if (ch is StorageCrate dc) return dc;
                    return null;   // a deployable with no grid is not a container, and nothing above it is either
                }
            }
            return null;
        }

        /// <summary>The node whose frame the container's box lives in: the StorageCrate itself, or for a device-borne
        /// grid (DeployableCrate) the device body that actually has the collider.</summary>
        public static Node3D ContainerBody(StorageCrate c) => c is DeployableCrate && c.GetParent() is Node3D p ? p : c;

        static readonly System.Collections.Generic.Dictionary<ulong, Aabb> _containerBounds = new();

        /// <summary>The container's box in its OWN frame: the union of its world-layer collision shapes. Cached per
        /// node -- a store shelf's trimesh is thousands of faces and the ghost asks every frame -- and only the
        /// world layer (bit 0) counts, so the shelf's display-item hitboxes (layer 11) do not swell it.</summary>
        public static bool ContainerBounds(Node3D body, out Aabb box)
        {
            ulong key = body.GetInstanceId();
            if (_containerBounds.TryGetValue(key, out box)) return box.Size != Vector3.Zero;
            bool any = false;
            var inv = body.GlobalTransform.AffineInverse();
            var stack = new System.Collections.Generic.Stack<Node>();
            stack.Push(body);
            var acc = new Aabb();
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                foreach (var ch in n.GetChildren()) stack.Push(ch);
                if (n is not CollisionShape3D cs || cs.Shape == null || cs.GetParent() is not CollisionObject3D co || (co.CollisionLayer & 1u) == 0) continue;
                Aabb a;
                if (cs.Shape is BoxShape3D b) a = new Aabb(-b.Size * 0.5f, b.Size);
                else if (cs.Shape is ConcavePolygonShape3D cp) a = PointsAabb(cp.GetFaces());
                else if (cs.Shape is ConvexPolygonShape3D cv) a = PointsAabb(cv.Points);
                else continue;
                a = (inv * cs.GlobalTransform) * a;
                acc = any ? acc.Merge(a) : a; any = true;
            }
            box = any ? acc : new Aabb();
            _containerBounds[key] = box;
            return any;
        }

        static Aabb PointsAabb(Vector3[] pts)
        {
            if (pts == null || pts.Length == 0) return new Aabb();
            Vector3 mn = pts[0], mx = pts[0];
            foreach (var p in pts) { mn = mn.Min(p); mx = mx.Max(p); }
            return new Aabb(mn, mx - mn);
        }

        // ---- which faces carry a DOOR (strawberry 2026-10-06: "doors on smart storage containers dont count as solid
        // for the placement tools. prevent placing adapters on the doors of storages") ----

        static int FaceBit(int axis, float sign) => 1 << (axis * 2 + (sign > 0f ? 1 : 0));
        static readonly System.Collections.Generic.Dictionary<ulong, int> _doorFaces = new();

        /// <summary>The faces of this container's box that carry a door, as FaceBit flags. A face carries one when a
        /// door's SHUT middle lies within DoorSlack of that face's plane AND inside the face's rectangle -- so a door
        /// beside the box (a doorway next to a shelf) is not mistaken for its own. Doors come from two places: prop
        /// leaves (ObjectDoor, found through its group -- they are siblings of the container, not children) and a
        /// container that declares its own (the placed fridge has a front, but no separate leaf). Cached per body:
        /// neither containers nor hinges move, and the ghost asks every frame.</summary>
        public static int DoorFaces(StorageCrate crate, Node3D body, Aabb box)
        {
            ulong key = body.GetInstanceId();
            if (_doorFaces.TryGetValue(key, out int mask)) return mask;
            mask = 0;
            var inv = body.GlobalTransform.AffineInverse();
            var points = new System.Collections.Generic.List<Vector3>();
            if (body.IsInsideTree())
                foreach (var n in body.GetTree().GetNodesInGroup(ObjectDoor.Group))
                    if (n is ObjectDoor d && IsInstanceValid(d)) points.Add(d.ClosedLeafCentreWorld);
            points.AddRange(crate.DoorPointsWorld);
            foreach (var w in points)
            {
                Vector3 c = inv * w;
                float best = float.MaxValue; int bit = 0;
                for (int axis = 0; axis < 3; axis++)
                    foreach (float sign in new[] { -1f, 1f })
                    {
                        float dist = Mathf.Abs(c[axis] - (sign > 0f ? box.End[axis] : box.Position[axis]));
                        if (dist > DoorSlack || dist >= best) continue;
                        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                        if (c[a1] < box.Position[a1] || c[a1] > box.End[a1] || c[a2] < box.Position[a2] || c[a2] > box.End[a2]) continue;
                        best = dist; bit = FaceBit(axis, sign);
                    }
                mask |= bit;
            }
            _doorFaces[key] = mask;
            return mask;
        }

        /// <summary>How far a door's shut middle may sit from its face plane: half a leaf's thickness plus however
        /// proud of the carcass it hangs. Measured off the props, not chosen: the deepest is a wardrobe leaf ~0.1 m
        /// out; 0.2 m keeps a margin without reaching a doorway beside the box.</summary>
        const float DoorSlack = 0.2f;

        static bool IsDoor(Node n)
        {
            for (var c = n; c != null; c = c.GetParent()) if (c is ObjectDoor) return true;
            return false;
        }

        /// <summary>Snap onto the container face nearest the aim point, back face against the box (strawberry:
        /// "the adapter should snap to the storage container. has to snap or it wont place -red."). The four SIDES
        /// and, since 2026-10-06, the TOP ("allow placing them on top of storages too"); never the bottom, and
        /// never a face with a DOOR on it -- a door leaf now stops the aim ray (it is on the small-prop layer the
        /// world-only mask used to look straight through), and aiming at it, or at a face that carries one, is red.
        ///
        /// SIDE: the yaw faces straight out of the face, so the replica recovers the normal from (pos, yaw) alone,
        /// exactly as for a wall mount. TOP: a yaw cannot say "up", so SnappedTop rides the place command and the
        /// entity (MountUp); the yaw is snapped to the container's own axes, turned so the adapter's top edge points
        /// AWAY from the player -- it reads like a label lying on a table.</summary>
        bool AimContainer(Camera3D cam)
        {
            SnappedCrate = null; SnappedCrateId = 0; SnappedTop = false;
            float aimYaw = Mathf.RadToDeg(cam.GlobalRotation.Y) + YawOffset;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = cam.GlobalPosition, dir = -cam.GlobalTransform.Basis.Z;
            var rq = PhysicsRayQueryParameters3D.Create(from, from + dir * Def.Range);
            rq.CollisionMask = (1u << 0) | (1u << 6);   // world + small props: a door leaf lives on 6, and must STOP the ray
            var hit = space.IntersectRay(rq);
            Node hitNode = hit.Count > 0 ? hit["collider"].As<Node>() : null;
            if (hitNode != null && IsDoor(hitNode))
            {
                Valid = false; Reason = "Not on a door"; Normal = Vector3.Up; Yaw = aimYaw; Point = (Vector3)hit["position"];
                Apply(); return false;
            }
            StorageCrate crate = hitNode != null ? ContainerOf(hitNode) : null;
            Node3D body = crate != null ? ContainerBody(crate) : null;
            if (crate == null || body == null || !ContainerBounds(body, out var box))
            {
                Valid = false; Reason = "Needs a storage container"; Normal = Vector3.Up; Yaw = aimYaw;
                Point = hit.Count > 0 ? (Vector3)hit["position"] : from + dir * Def.Range;
                Apply(); return false;
            }
            int doors = DoorFaces(crate, body, box);
            // THE FACE THE RAY ENTERS THE BOX THROUGH -- not the face nearest wherever it stopped. A fridge stood
            // open, or a shelf's open front, lets the ray in to hit the back wall or a board inside, and "nearest
            // face to that" is the BACK: the adapter would jump to the far side of the box. The entry face is the
            // one the player is looking at, whatever the ray met after it.
            var inv = body.GlobalTransform.AffineInverse();
            Vector3 o = inv * from, d = inv.Basis * dir;
            int bestAxis = -1; float bestSign = 1f, tEnter = float.MinValue;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(d[axis]) < 1e-9f) continue;   // parallel to this slab: it cannot be the entry face
                float t1 = (box.Position[axis] - o[axis]) / d[axis], t2 = (box.End[axis] - o[axis]) / d[axis];
                float tn = Mathf.Min(t1, t2);
                if (tn > tEnter) { tEnter = tn; bestAxis = axis; bestSign = d[axis] > 0f ? -1f : 1f; }
            }
            Vector3 local = inv * (Vector3)hit["position"];
            if (bestAxis < 0 || tEnter <= 0f)
            {
                // the camera is INSIDE the box (or degenerate): fall back to the face nearest the hit
                float nb = float.MaxValue;
                for (int axis = 0; axis < 3; axis++)
                    foreach (float sign in new[] { -1f, 1f })
                    {
                        float dist = Mathf.Abs(local[axis] - (sign > 0f ? box.End[axis] : box.Position[axis]));
                        if (dist < nb) { nb = dist; bestAxis = axis; bestSign = sign; }
                    }
            }
            else local = o + d * tEnter;   // where the ray crosses that face
            var nFace = (body.GlobalBasis * (AxisDir(bestAxis) * bestSign)).Normalized();
            bool bestTop = nFace.Y > 0.94f;   // within ~20 degrees of up
            if ((doors & FaceBit(bestAxis, bestSign)) != 0)
            {
                Valid = false; Reason = "Not on a door"; Normal = Vector3.Up; Yaw = aimYaw; Point = (Vector3)hit["position"];
                Apply(); return false;
            }
            if (!bestTop && Mathf.Abs(nFace.Y) >= 0.5f)
            {
                // the bottom, or a "side" of a tipped prop that faces up/down
                Valid = false; Reason = "Needs a side or the top"; Normal = Vector3.Up; Yaw = aimYaw; Point = (Vector3)hit["position"];
                Apply(); return false;
            }

            Vector3 p = local;
            p[bestAxis] = bestSign > 0f ? box.End[bestAxis] : box.Position[bestAxis];
            int a1 = (bestAxis + 1) % 3, a2 = (bestAxis + 2) % 3;
            if (!bestTop)
            {
                // the in-face axis nearer world-vertical carries the adapter's HEIGHT, the other its width
                bool a1Up = Mathf.Abs((body.GlobalBasis * AxisDir(a1)).Normalized().Y) >= Mathf.Abs((body.GlobalBasis * AxisDir(a2)).Normalized().Y);
                int up = a1Up ? a1 : a2, across = a1Up ? a2 : a1;
                p[across] = ClampInside(p[across], box.Position[across], box.End[across], Def.Size.X * 0.5f);
                p[up] = ClampInside(p[up], box.Position[up], box.End[up], Def.Size.Z * 0.5f);
                Vector3 nWorld = body.GlobalBasis * (AxisDir(bestAxis) * bestSign);
                nWorld = new Vector3(nWorld.X, 0f, nWorld.Z).Normalized();
                Normal = nWorld;
                Yaw = YawFacing(nWorld);
            }
            else
            {
                // f = the container axis nearest the direction TOWARD the player; the tipped plate's top edge ends up
                // along -f, i.e. pointing away. Snapped to the box so it sits square on the lid.
                Vector3 toward = new Vector3(-dir.X, 0f, -dir.Z);
                if (toward.LengthSquared() < 1e-6f) toward = -cam.GlobalTransform.Basis.Y with { Y = 0f };   // looking straight down
                int fAxis = a1; float fSign = 1f, fBest = float.MinValue;
                foreach (int ax in new[] { a1, a2 })
                    foreach (float sg in new[] { -1f, 1f })
                    {
                        var w = body.GlobalBasis * (AxisDir(ax) * sg);
                        float dot = new Vector3(w.X, 0f, w.Z).Normalized().Dot(toward.Normalized());
                        if (dot > fBest) { fBest = dot; fAxis = ax; fSign = sg; }
                    }
                int across = fAxis == a1 ? a2 : a1;
                p[fAxis] = ClampInside(p[fAxis], box.Position[fAxis], box.End[fAxis], Def.Size.Z * 0.5f);     // height runs along f once tipped
                p[across] = ClampInside(p[across], box.Position[across], box.End[across], Def.Size.X * 0.5f);
                Vector3 f = body.GlobalBasis * (AxisDir(fAxis) * fSign);
                Normal = Vector3.Up;
                Yaw = YawFacing(new Vector3(f.X, 0f, f.Z).Normalized());
                SnappedTop = true;
            }
            Point = body.GlobalTransform * p;
            SnappedCrate = crate; SnappedCrateId = crate.NetId;
            Valid = true; Reason = null;
            Apply();
            return true;
        }

        static Vector3 AxisDir(int axis) => axis == 0 ? Vector3.Right : axis == 1 ? Vector3.Up : Vector3.Back;

        static float ClampInside(float v, float lo, float hi, float margin)
            => hi - lo <= margin * 2f ? (lo + hi) * 0.5f : Mathf.Clamp(v, lo + margin, hi - margin);

        // The ghost/placed transform for the current mount. Window = stood-up + faced like a Wall mount, but scaled to
        // fit the opening and seated at the opening centre + face standoff (Point), not a raycast hit + MountOrigin lift.
        // ⚠ THE GHOST *IS* THE MESH, so it has to carry the model fixup itself. A PLACED deployable is two nodes --
        // the body holding the placement basis, the MeshInstance holding Def.MeshBasis() -- so its world basis is
        // placement × meshFixup. The ghost is the MeshInstance returned by BuildMesh, and assigning GlobalTransform
        // here OVERWRITES the `mi.Basis = mrot` BuildMesh just set. Any def with a non-identity MeshEuler therefore
        // previewed WITHOUT its fixup while placing WITH it: the standing lamp's ghost hung shade-down with its base
        // and port arrows in the air (master 2026-09-07), and the battery's ghost has been 180 off since it got one.
        // Appended, not prepended, to match the parent×child order the real object composes in.
        Transform3D GhostTransform() => Mount == BarricadeMount.Window
            ? new Transform3D(DeployableDef.StandBasis(Yaw) * Basis.FromScale(_windowScale) * MeshFix, Point)
            : new Transform3D(MountBasis(Mount, Normal, Yaw, Def != null && Def.Upright) * MeshFix, MountOrigin());

        Basis MeshFix => Def != null ? Def.MeshBasis() : Basis.Identity;

        // Window mount aim -- a window HOLE has no collider, so a raycast sails straight through it. Enumerate the live
        // WallSurface nodes ("walls" group), UV-project the camera ray onto each wall plane, and find the OPENING the
        // ray lands in. Snap the panel flush into that opening on the face the camera is on, sized to the opening;
        // valid only if it's really a window (sill'd, not a door) and that inside/outside slot is still empty.
        bool AimWindow(Camera3D cam)
        {
            SnappedWall = null; SnappedOpening = -1; SnappedMarker = null;
            Vector3 from = cam.GlobalPosition, dir = -cam.GlobalTransform.Basis.Z;
            float best = float.MaxValue;
            Vector3 bC = default, bN = default; float bHW = 0f, bHH = 0f, bHT = 0f;   // best opening: centre, normal, half-width/height/thickness

            // LIVE walls (editor / play-mode): UV-project onto each WallSurface, find the window opening the ray lands in.
            foreach (var node in GetTree().GetNodesInGroup("walls"))
            {
                if (node is not WallSurface wall) continue;
                if (!wall.RayToUVInside(from, dir, out float u, out float v)) continue;
                int oi = wall.OpeningAt(u, v); if (oi < 0) continue;
                var o = wall.Openings[oi];
                // ANY opening is barricadable (master 2026-09-01): windows, empty doorways, AND doored openings.
                // A barricade over a doored opening blocks the door's interaction ("Door is barricaded") -- see PlayerController.
                Vector3 c = wall.UVToWorld(o.U + o.Width * 0.5f, o.V + o.Height * 0.5f);
                float d = from.DistanceTo(c);
                if (d <= Def.Range && d < best)
                {
                    best = d; SnappedWall = wall; SnappedOpening = oi; SnappedMarker = null;
                    bC = c; bN = wall.GlobalTransform.Basis.Z.Normalized();
                    bHW = o.Width * 0.5f; bHH = o.Height * 0.5f; bHT = wall.Thickness * 0.5f;
                }
            }
            // BAKED props (real map): no WallSurface -> ray ∩ each WindowOpeningMarker's plane, within its extents.
            foreach (var node in GetTree().GetNodesInGroup("window_openings"))
            {
                if (node is not WindowOpeningMarker m) continue;
                Vector3 c = m.WorldCentre, n = m.WorldNormal;
                float denom = n.Dot(dir); if (Mathf.Abs(denom) < 1e-6f) continue;
                float t = n.Dot(c - from) / denom; if (t < 0f) continue;
                Vector3 rel = from + dir * t - c;
                if (Mathf.Abs(rel.Dot(m.WorldWidthAxis)) > m.HalfWidth || Mathf.Abs(rel.Dot(m.WorldHeightAxis)) > m.HalfHeight) continue;
                float d = from.DistanceTo(c);
                if (d <= Def.Range && d < best)
                {
                    best = d; SnappedMarker = m; SnappedWall = null; SnappedOpening = -1;
                    bC = c; bN = n; bHW = m.HalfWidth; bHH = m.HalfHeight; bHT = m.HalfThickness;
                }
            }

            if (SnappedWall == null && SnappedMarker == null)   // not on a window -> invalid; park the ghost out along the ray
            {
                Valid = false; Reason = "Needs a window opening"; Normal = Vector3.Up; _windowScale = Vector3.One;
                Point = from + dir * Def.Range; Yaw = 0f; Apply(); return false;
            }
            SnappedFace = (from - bC).Dot(bN) >= 0f ? 1 : -1;              // the face the camera is on
            Normal = bN * SnappedFace;
            Yaw = YawFacing(Normal);
            Point = bC + Normal * (bHT + Def.Size.Y * 0.5f + 0.005f);      // seat the panel flat ON the aimed face (half-wall + half-panel + hair)
            _windowScale = new Vector3(bHW * 2f / Def.Size.X, 1f, bHH * 2f / Def.Size.Z);   // fit the flat frame (X=width, Z=height) to the opening
            Valid = !SlotTaken();                                         // one barricade per face
            Reason = Valid ? null : "That opening is taken";
            Apply();
            return Valid;
        }

        // The one-per-face slot check over whichever host the snap came from (a live wall, or a baked marker's prop).
        bool SlotTaken() => SnappedWall != null ? SlotFilled(SnappedWall, SnappedOpening, SnappedFace)
                          : SnappedMarker != null && MarkerSlotFilled(SnappedMarker, SnappedFace);

        // Baked case: a placed window barricade is a child of the marker's PROP root, stamped with the marker's id + face.
        public static bool MarkerSlotFilled(WindowOpeningMarker marker, int face)
        {
            var host = marker.GetParent();
            if (host == null) return false;
            long id = (long)marker.GetInstanceId();
            foreach (var c in host.GetChildren())
                if (c.HasMeta("ug_wb_marker") && (long)c.GetMeta("ug_wb_marker") == id
                    && c.HasMeta("ug_wb_face") && (int)c.GetMeta("ug_wb_face") == face)
                    return true;
            return false;
        }

        // One window barricade per opening face. A placed panel is parented onto its WallSurface + stamped with the
        // opening index + face (see Barricade.PlaceInWindow), so scan the wall's children for a match -- the two faces
        // sit ~10 cm apart, too close for a clearance sphere to tell them apart.
        public static bool SlotFilled(WallSurface wall, int opening, int face)
        {
            foreach (var c in wall.GetChildren())
                if (c.HasMeta("ug_wb_opening") && (int)c.GetMeta("ug_wb_opening") == opening
                    && c.HasMeta("ug_wb_face") && (int)c.GetMeta("ug_wb_face") == face)
                    return true;
            return false;
        }

        // Without StructureManager wired, accept a hit on a structure piece or terrain/ground, but never stack a
        // barricade straight onto another barricade/deployable. CanAttach (StructureManager.CanAttach) supersedes this.
        static bool DefaultAttachable(Node collider)
        {
            // the raycast mask (1<<0) already limits hits to ground / structures / vehicles; accept any of those, and
            // just never stack a barricade straight onto another barricade/deployable. CanAttach (StructureManager)
            // supersedes this for the structure-specific edge/pillar rules at merge.
            if (collider == null) return true;
            // A VEHICLE surface is refused, deliberately, until planting exists. Retail parents a barricade to
            // the vehicle it is dropped on (SDK dropPlantedBarricade); nothing here does, so the old code let
            // the ghost go BLUE on a car roof and then left the generator hanging in mid-air the moment the car
            // drove away. A ghost that says no is honest; a ghost that says yes and then loses your item is not.
            // Swap this for real planting when the parented path lands.
            for (var n = collider; n != null; n = n.GetParent())
                if (n.IsInGroup("vehicles")) return false;
            return !collider.IsInGroup("deployables") && !collider.IsInGroup("barricades");
        }

        // clearance sphere at the standoff point (src OverlapSphere(point, radius, BLOCK_BARRICADE)); excludes the mount
        // surface (the wall/floor we're placing on). The src BLOCK_BARRICADE mask does NOT include GROUND, so the terrain
        // a barricade sits against never counts as a blocking obstacle -- otherwise a wall barricade near the floor (its
        // sphere dipping into the ground) would falsely read blocked. We can't mask that out by layer (ground/structures
        // share layer 0), so filter the ground/terrain hits out in code.
        static bool Overlap(PhysicsDirectSpaceState3D space, Vector3 p, float r, Rid exclude)
        {
            var pq = new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = r },
                Transform = new Transform3D(Basis.Identity, p),
                CollisionMask = 1u << 0,
                Exclude = new Godot.Collections.Array<Rid> { exclude },
            };
            foreach (var h in space.IntersectShape(pq, 8))
            {
                var c = h["collider"].As<Node>();
                if (c == null || c.IsInGroup("terrain") || c.IsInGroup("ground") || c.IsInGroup("structures")) continue;   // ground + the structure lattice (the mount SURFACE) don't block, else the floor slab bricks the bottom of every wall
                return true;   // a real obstacle: another structure / barricade / vehicle / prop
            }
            return false;
        }

        void Apply()
        {
            if (_ghost == null) return;
            _ghost.GlobalTransform = GhostTransform();
            _ghost.MaterialOverride = Valid ? DeployablePlacer.ValidMat : DeployablePlacer.InvalidMat;
            if (_arrowMat != null) { var c = Valid ? ConnectionPort.ArrowBlue : ConnectionPort.ArrowRed; c.A = 0.92f; _arrowMat.AlbedoColor = c; }
        }

        // DeployablePlacer-compatible overload: freeze with just point + yaw (normal = up, i.e. a Floor barricade).
        // Lets the in-game place flow swap DeployablePlacer -> BarricadePlacer with no signature change -- BarricadePlacer
        // is an API superset (SetDef/Aim/Point/Yaw/YawOffset/SetGhostVisible all match), so the swap plus a spawn branch
        // to Barricade.PlaceOnSurface is the whole integration.
        public void Freeze(Vector3 point, float yaw) => Freeze(point, Vector3.Up, yaw);

        // Pin the ghost at a committed point/normal/yaw (blue) while the place gesture plays -- ignores aim.
        public void Freeze(Vector3 point, Vector3 normal, float yaw)
        {
            Valid = true; Point = point; Normal = normal.Normalized(); Yaw = yaw;
            if (_ghost == null) return;
            _ghost.Visible = true;
            _ghost.GlobalTransform = GhostTransform();
            _ghost.MaterialOverride = DeployablePlacer.ValidMat;
            if (_arrowMat != null) { var c = ConnectionPort.ArrowBlue; c.A = 0.92f; _arrowMat.AlbedoColor = c; }
        }
    }
}
