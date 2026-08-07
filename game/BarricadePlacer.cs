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

        // Attachment predicate (world point, surface normal) -> can a barricade mount here? At merge:
        //   placer.CanAttach = (p, n) => StructureManager.Instance.CanAttach(p, n);
        // Kept as a hook so feat/barricades builds standalone; null falls back to DefaultAttachable below.
        public System.Func<Vector3, Vector3, bool> CanAttach;

        MeshInstance3D _ghost;
        Aabb _localAabb;
        StandardMaterial3D _arrowMat;

        public void SetDef(DeployableDef def)
        {
            Def = def;
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

        // The world basis for a given mount family. Floor/Wall stay upright (StandBasis); Sticky lies flush.
        public static Basis MountBasis(BarricadeMount mount, Vector3 n, float yaw) => mount switch
        {
            BarricadeMount.Sticky => AlignUpTo(n) * DeployableDef.StandBasis(yaw),   // whole stand-up tipped so the mount axis follows the normal
            _ => DeployableDef.StandBasis(yaw),                                       // Floor + Wall: upright; the yaw already encodes wall-facing for Wall
        };

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
            bool attach = CanAttach != null ? CanAttach(hp, n) : DefaultAttachable(collider);
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

        // Without StructureManager wired, accept a hit on a structure piece or terrain/ground, but never stack a
        // barricade straight onto another barricade/deployable. CanAttach (StructureManager.CanAttach) supersedes this.
        static bool DefaultAttachable(Node collider)
        {
            // the raycast mask (1<<0) already limits hits to ground / structures / vehicles; accept any of those, and
            // just never stack a barricade straight onto another barricade/deployable. CanAttach (StructureManager)
            // supersedes this for the structure-specific edge/pillar rules at merge.
            return collider == null || (!collider.IsInGroup("deployables") && !collider.IsInGroup("barricades"));
        }

        // clearance sphere at the standoff point (src OverlapSphere(point, radius, BLOCK_BARRICADE)); excludes the
        // mount surface so contact with the wall/floor we're placing on doesn't count as a blocking obstacle.
        static bool Overlap(PhysicsDirectSpaceState3D space, Vector3 p, float r, Rid exclude)
        {
            var pq = new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = r },
                Transform = new Transform3D(Basis.Identity, p),
                CollisionMask = 1u << 0,
                Exclude = new Godot.Collections.Array<Rid> { exclude },
            };
            return space.IntersectShape(pq, 1).Count > 0;
        }

        void Apply()
        {
            if (_ghost == null) return;
            _ghost.GlobalTransform = new Transform3D(MountBasis(Mount, Normal, Yaw), MountOrigin());
            _ghost.MaterialOverride = Valid ? DeployablePlacer.ValidMat : DeployablePlacer.InvalidMat;
            if (_arrowMat != null) { var c = Valid ? ConnectionPort.ArrowBlue : ConnectionPort.ArrowRed; c.A = 0.92f; _arrowMat.AlbedoColor = c; }
        }

        // Pin the ghost at a committed point/normal/yaw (blue) while the place gesture plays -- ignores aim.
        public void Freeze(Vector3 point, Vector3 normal, float yaw)
        {
            Valid = true; Point = point; Normal = normal.Normalized(); Yaw = yaw;
            if (_ghost == null) return;
            _ghost.Visible = true;
            _ghost.GlobalTransform = new Transform3D(MountBasis(Mount, Normal, Yaw), MountOrigin());
            _ghost.MaterialOverride = DeployablePlacer.ValidMat;
            if (_arrowMat != null) { var c = ConnectionPort.ArrowBlue; c.A = 0.92f; _arrowMat.AlbedoColor = c; }
        }
    }
}
