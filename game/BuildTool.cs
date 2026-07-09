using Godot;

namespace UnturnedGodot
{
    // Bounded first pass at Unturned's building. B toggles build mode; a translucent GHOST of the current structure
    // floats at the aim point snapped to a 3 m grid (Unturned's structure tile size); C cycles Floor <-> Wall; LMB
    // places a solid Structure (mesh + collision, group "structures"). This is a stand-in: box meshes + grid snap,
    // NOT the real StructureManager (structure assets, edge/pillar snapping, health, save) -- a direction for master.
    public partial class BuildTool : Node3D
    {
        public Camera3D Cam;
        public bool Active;
        public int Type;                 // 0 = floor, 1 = wall
        const float GRID = 3f;           // structure tile size (m)

        MeshInstance3D _ghost;
        static readonly Color[] Tint = { new(0.4f, 0.7f, 1f, 0.4f), new(0.4f, 0.7f, 1f, 0.4f) };

        public override void _Ready()
        {
            _ghost = new MeshInstance3D { Visible = false };
            AddChild(_ghost);
        }

        public void Toggle() { Active = !Active; _ghost.Visible = Active; if (Active) RebuildGhost(); }
        public void CycleType() { if (!Active) return; Type = (Type + 1) % 2; RebuildGhost(); }

        Mesh MakeMesh() => Type == 0
            ? new BoxMesh { Size = new Vector3(GRID, 0.2f, GRID) }     // floor slab
            : new BoxMesh { Size = new Vector3(GRID, GRID, 0.2f) };    // wall panel

        void RebuildGhost()
        {
            _ghost.Mesh = MakeMesh();
            _ghost.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Tint[Type],
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
        }

        public override void _Process(double delta)
        {
            if (!Active || Cam == null) return;
            if (TryAim(out Vector3 snapped, out float yaw)) { _ghost.GlobalPosition = snapped; _ghost.RotationDegrees = new Vector3(0, yaw, 0); }
        }

        // raycast the camera forward to the ground, snap x/z to the grid, y to the hit; walls face the camera
        bool TryAim(out Vector3 pos, out float yaw)
        {
            pos = Vector3.Zero; yaw = 0f;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = Cam.GlobalPosition, dir = -Cam.GlobalTransform.Basis.Z;
            var q = PhysicsRayQueryParameters3D.Create(from, from + dir * 8f);
            q.CollisionMask = 1u << 0;   // ground/structures
            var hit = space.IntersectRay(q);
            Vector3 p = hit.Count > 0 ? (Vector3)hit["position"] : from + dir * 4f;
            float sx = Mathf.Round(p.X / GRID) * GRID, sz = Mathf.Round(p.Z / GRID) * GRID;
            float sy = (Type == 0) ? 0.1f : GRID * 0.5f;   // floor sits on the ground, wall stands on it
            pos = new Vector3(sx, sy, sz);
            // wall faces the camera: snap yaw to 0/90
            Vector3 flat = new Vector3(dir.X, 0, dir.Z);
            yaw = (Mathf.Abs(flat.X) > Mathf.Abs(flat.Z)) ? 90f : 0f;
            return true;
        }

        // place a solid structure at the ghost (or a given transform, for scripted demos)
        public void Place() { if (Active) Spawn(_ghost.GlobalPosition, _ghost.RotationDegrees.Y); }

        public void Spawn(Vector3 pos, float yaw)
        {
            var body = new StaticBody3D { CollisionLayer = 1 << 0 };
            var mesh = MakeMesh();
            body.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.58f, 0.5f), Roughness = 0.9f } });
            var shape = new CollisionShape3D { Shape = new BoxShape3D { Size = ((BoxMesh)mesh).Size } };
            body.AddChild(shape);
            body.AddToGroup("structures");
            GetParent().AddChild(body);
            body.GlobalPosition = pos;
            body.RotationDegrees = new Vector3(0, yaw, 0);
        }
    }
}
