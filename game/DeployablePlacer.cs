using Godot;

namespace UnturnedGodot
{
    // The placement ghost for a held deployable, ported from UseableBarricade.checkSpace + HighlighterTool:
    // raycast from the eye out to the asset's Range; place the ghost at hit + normal*offset (or up*offset on a
    // slope); reject walls/ceilings (normal.y < 0.01) and any clearance overlap (a sphere of the asset's Radius).
    // The ghost is BLUE when placement is valid, RED when not (src = a two-material swap on the preview renderer).
    public partial class DeployablePlacer : Node3D
    {
        public DeployableDef Def { get; private set; }
        public bool Valid { get; private set; }
        public Vector3 Point { get; private set; }
        public float Yaw { get; private set; }

        MeshInstance3D _ghost;
        Aabb _localAabb;

        public static readonly StandardMaterial3D ValidMat = Ghost(new Color(0.30f, 0.62f, 1f, 0.45f));   // blue
        public static readonly StandardMaterial3D InvalidMat = Ghost(new Color(1f, 0.28f, 0.28f, 0.45f)); // red

        static StandardMaterial3D Ghost(Color c) => new()
        {
            AlbedoColor = c,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        public void SetDef(DeployableDef def)
        {
            Def = def;
            _ghost?.QueueFree();
            _ghost = Deployable.BuildMesh(def, out _localAabb);
            _ghost.MaterialOverride = InvalidMat;
            AddChild(_ghost);
        }

        public void SetGhostVisible(bool v) { if (_ghost != null) _ghost.Visible = v; }

        // Run the aim -> point/valid check for this frame and move the ghost to match. Returns Valid.
        public bool Aim(Camera3D cam)
        {
            if (Def == null || cam == null) return false;
            Yaw = Mathf.RadToDeg(cam.GlobalRotation.Y);   // ghost yaw follows aim yaw (src angle_y = look.yaw)
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = cam.GlobalPosition, dir = -cam.GlobalTransform.Basis.Z;
            var rq = PhysicsRayQueryParameters3D.Create(from, from + dir * Def.Range);
            rq.CollisionMask = 1u << 0;                   // ground / structures / vehicles
            var hit = space.IntersectRay(rq);
            if (hit.Count == 0)                           // aiming at nothing within range -> invalid
            {
                Valid = false; Point = from + dir * Def.Range; Apply(); return false;
            }
            Vector3 hp = (Vector3)hit["position"], n = (Vector3)hit["normal"];
            bool wall = n.Y < 0.01f;                      // vertical wall / ceiling -> blocked
            Point = hp;                                   // the surface contact; the base sits here (ghost is lifted in Apply)
            Valid = !wall && !Overlap(space, hp + Vector3.Up * Def.Offset, Def.Radius);   // clearance sphere at the src offset height
            Apply();
            return Valid;
        }

        // clearance sphere at the placement point (src OverlapSphere(point, radius, BLOCK_BARRICADE)); the offset
        // lifts the sphere clear of flat ground, so a hit here means a real obstacle (vehicle/deployable/wall).
        static bool Overlap(PhysicsDirectSpaceState3D space, Vector3 p, float r)
        {
            var pq = new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = r },
                Transform = new Transform3D(Basis.Identity, p),
                CollisionMask = 1u << 0,
            };
            return space.IntersectShape(pq, 1).Count > 0;
        }

        void Apply()
        {
            if (_ghost == null) return;
            _ghost.GlobalTransform = new Transform3D(DeployableDef.StandBasis(Yaw),
                Point + Vector3.Up * DeployableDef.GroundLift(_localAabb));   // base sits on the surface point
            _ghost.MaterialOverride = Valid ? ValidMat : InvalidMat;
        }
    }
}
