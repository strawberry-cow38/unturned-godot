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
        Window,  // window barricade: snaps INTO a building-editor window opening (UV-projected onto the wall plane, NOT
                 // raycast -- the hole has no collider), one per inside/outside face, sized to the opening; ONLY placeable
                 // when the reticle is on a window opening. No retail analogue -- master 2026-08-31.
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
        public Vector3 Point { get; private set; }              // surface contact (raycast hit position)
        public Vector3 Normal { get; private set; } = Vector3.Up;  // the hit surface normal
        public float Yaw { get; private set; }                 // the final yaw fed to StandBasis (player aim for Floor; wall-facing for Wall)
        public float YawOffset;   // R accumulates here: manual spin about the mount axis (src input_y, UseableBarricade.cs:2041)

        // Window mount state: the opening the ghost is snapped to (SnappedOpening = -1 when not on a window). SnappedFace
        // is +1 (the wall's +Z face) or -1 (the -Z face) -- whichever side the camera is on -- so one barricade goes on
        // each side. _windowScale fits the panel to the opening. The spawn (Barricade.PlaceInWindow) reads these back.
        public WallSurface SnappedWall { get; private set; }
        public int SnappedOpening { get; private set; } = -1;
        public int SnappedFace { get; private set; }
        Vector3 _windowScale = Vector3.One;

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
            return mount == BarricadeMount.Sticky ? AlignUpTo(n) * ground : ground;
        }

        // Which surfaces this mount family accepts (src per-EBuild normal.y gates).
        static bool SurfaceOk(BarricadeMount mount, Vector3 n) => mount switch
        {
            BarricadeMount.Floor => n.Y >= 0.01f,          // upward-facing only (UseableBarricade.cs:805)
            BarricadeMount.Wall => Mathf.Abs(n.Y) < 0.1f,  // near-vertical only (UseableBarricade.cs:1397)
            _ => true,                                      // Sticky: anything (UseableBarricade.cs:1483)
        };

        // The final yaw fed to StandBasis for this mount + normal. Floor = the player's aim yaw; Wall = the wall's
        // outward facing (src angle_y = LookRotation(normal).eulerAngles.y) + manual spin; Sticky = manual spin only.
        float ResolveYaw(float aimYaw, Vector3 n) => Mount switch
        {
            BarricadeMount.Wall => YawFacing(n) + YawOffset,
            BarricadeMount.Sticky => YawOffset,
            _ => aimYaw,   // Floor: aim yaw + R (aimYaw already includes YawOffset, see Aim)
        };

        // Standoff off a wall/ceiling along the normal (src point = hit + normal*offset, UseableBarricade.cs:817). The
        // port's DeployableDef.Offset is authored as a GROUND vertical clearance (~mesh half-height); as a wall
        // standoff that floats the piece a metre off the surface, so clamp it to a small hug for Wall/Sticky. A real
        // barricade asset's small Offset passes through unchanged.
        float WallStandoff => Def == null ? 0f : Mathf.Min(Def.Offset, 0.1f);

        // Lift so the mesh seats on the surface: Floor stands its base on the point (GroundLift along up); Wall/Sticky
        // hug the surface by WallStandoff along the normal.
        Vector3 MountOrigin() => Mount == BarricadeMount.Floor
            ? Point + Vector3.Up * (Def != null && Def.Upright ? -_localAabb.Position.Y : DeployableDef.GroundLift(_localAabb))
            : Point + Normal * WallStandoff;

        // Run the aim -> point / normal / valid check this frame and move the ghost to match. Returns Valid.
        public bool Aim(Camera3D cam)
        {
            if (Def == null || cam == null) return false;
            if (Mount == BarricadeMount.Window) return AimWindow(cam);   // window barricade UV-projects onto openings (the hole has no collider to raycast)
            float aimYaw = Mathf.RadToDeg(cam.GlobalRotation.Y) + YawOffset;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = cam.GlobalPosition, dir = -cam.GlobalTransform.Basis.Z;
            var rq = PhysicsRayQueryParameters3D.Create(from, from + dir * Def.Range);
            rq.CollisionMask = 1u << 0;                   // ground / structures / vehicles (src BARRICADE_INTERACT)
            var hit = space.IntersectRay(rq);
            if (hit.Count == 0)                           // aiming at nothing within range -> invalid
            {
                Valid = false; Normal = Vector3.Up; Point = from + dir * Def.Range; Yaw = aimYaw; Apply(); return false;
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
            // submersible device (fluid inlet) parity: valid only on submerged seabed within its water-depth band
            if (Valid && Def.WaterDepthMin >= 0f)
            {
                float depth = DeployableDef.SeaLevel - hp.Y;
                Valid = depth >= Def.WaterDepthMin && depth <= Def.WaterDepthMax;
            }
            Apply();
            return Valid;
        }

        // The ghost/placed transform for the current mount. Window = stood-up + faced like a Wall mount, but scaled to
        // fit the opening and seated at the opening centre + face standoff (Point), not a raycast hit + MountOrigin lift.
        Transform3D GhostTransform() => Mount == BarricadeMount.Window
            ? new Transform3D(DeployableDef.StandBasis(Yaw) * Basis.FromScale(_windowScale), Point)
            : new Transform3D(MountBasis(Mount, Normal, Yaw, Def != null && Def.Upright), MountOrigin());

        // Window mount aim -- a window HOLE has no collider, so a raycast sails straight through it. Enumerate the live
        // WallSurface nodes ("walls" group), UV-project the camera ray onto each wall plane, and find the OPENING the
        // ray lands in. Snap the panel flush into that opening on the face the camera is on, sized to the opening;
        // valid only if it's really a window (sill'd, not a door) and that inside/outside slot is still empty.
        bool AimWindow(Camera3D cam)
        {
            SnappedWall = null; SnappedOpening = -1;
            Vector3 from = cam.GlobalPosition, dir = -cam.GlobalTransform.Basis.Z;
            float best = float.MaxValue;
            foreach (var node in GetTree().GetNodesInGroup("walls"))
            {
                if (node is not WallSurface wall) continue;
                if (!wall.RayToUVInside(from, dir, out float u, out float v)) continue;
                int oi = wall.OpeningAt(u, v);
                if (oi < 0) continue;
                var o = wall.Openings[oi];
                if (o.V <= UnturnedSim.WallOpenings.Eps || o.HasDoor) continue;   // window = sill'd + not a door (doors are floor-pinned V~0 or carry a leaf)
                Vector3 c = wall.UVToWorld(o.U + o.Width * 0.5f, o.V + o.Height * 0.5f);
                float d = from.DistanceTo(c);
                if (d <= Def.Range && d < best) { best = d; SnappedWall = wall; SnappedOpening = oi; }
            }
            if (SnappedWall == null)   // not on a window opening -> invalid; park the ghost out along the ray
            {
                Valid = false; Normal = Vector3.Up; _windowScale = Vector3.One;
                Point = from + dir * Def.Range; Yaw = 0f; Apply(); return false;
            }
            var op = SnappedWall.Openings[SnappedOpening];
            Vector3 wn = SnappedWall.GlobalTransform.Basis.Z.Normalized();
            Vector3 center = SnappedWall.UVToWorld(op.U + op.Width * 0.5f, op.V + op.Height * 0.5f);
            SnappedFace = (from - center).Dot(wn) >= 0f ? 1 : -1;          // the face the camera is on
            Normal = wn * SnappedFace;
            Yaw = YawFacing(Normal);
            Point = center + Normal * (SnappedWall.Thickness * 0.5f + Def.Size.Y * 0.5f + 0.005f);   // seat the panel flat ON the aimed WALL FACE (half-wall + half-panel + hair), not embedded in the wall's thickness
            _windowScale = new Vector3(op.Width / Def.Size.X, 1f, op.Height / Def.Size.Z);   // fit the flat frame (X=width, Z=height) to the opening
            Valid = !SlotFilled(SnappedWall, SnappedOpening, SnappedFace);  // one barricade per face
            Apply();
            return Valid;
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
