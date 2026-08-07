using Godot;

namespace UnturnedGodot
{
    // Build mode. B toggles it, C cycles the construct, V cycles the tier, LMB places.
    //
    // This used to BE the building system -- a box mesh snapped to a 3 m grid, spawning a loose StaticBody with
    // no health, no slot bookkeeping and no persistence. It is now just the input + preview layer over
    // StructureManager, which owns all of that. The split matters because placement rules are the part worth
    // testing, and they are untestable while they live inside a node that needs a camera and a viewport.
    //
    // The ghost is honest about refusal: it turns RED and carries the manager's reason ("occupied" / "no
    // support") rather than silently declining to place. A ghost that looks placeable but is not is the
    // frustrating version of this feature.
    public partial class BuildTool : Node3D
    {
        public Camera3D Cam;
        public bool Active;
        public int Type;            // index into StructureCatalog.Buildable
        public int Tier;            // index into StructureCatalog.Tiers

        MeshInstance3D _ghost;
        StandardMaterial3D _mat;
        string _reason;             // why the current aim point is refused, null when placeable

        public EConstruct Construct => StructureCatalog.Buildable[Mathf.PosMod(Type, StructureCatalog.Buildable.Length)];
        public string BlockedReason => _reason;
        public bool AimValid => _reason == null;

        static readonly Color OkTint = new(0.35f, 0.75f, 1f, 0.38f);
        static readonly Color BadTint = new(1f, 0.30f, 0.25f, 0.38f);

        public override void _Ready()
        {
            _mat = new StandardMaterial3D
            {
                AlbedoColor = OkTint,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _ghost = new MeshInstance3D { Visible = false, MaterialOverride = _mat };
            AddChild(_ghost);
            RebuildGhost();
            EnsureManager();
        }

        /// <summary>Provision the StructureManager if the world has none. Deliberately done HERE rather than in
        /// PlayerController: that file is shared with the barricade branch this sprint, and a one-line
        /// instantiation there is exactly the kind of edit that turns into a merge conflict for no benefit. The
        /// manager parents to the build tool's parent, so it outlives nothing it should not and a headless test
        /// can add its own instead.</summary>
        void EnsureManager()
        {
            if (StructureManager.Instance != null) return;
            var host = GetParent() ?? (Node)this;
            var sm = new StructureManager { Name = "StructureManager" };
            host.CallDeferred(Node.MethodName.AddChild, sm);
        }

        public void Toggle() { Active = !Active; if (_ghost != null) _ghost.Visible = Active; if (Active) RebuildGhost(); }
        public void CycleType() { if (!Active) return; Type = Mathf.PosMod(Type + 1, StructureCatalog.Buildable.Length); RebuildGhost(); }
        public void CycleTier() { if (!Active) return; Tier = Mathf.PosMod(Tier + 1, StructureCatalog.TierCount); RebuildGhost(); }

        void RebuildGhost()
        {
            if (_ghost == null) return;
            _ghost.Mesh = new BoxMesh { Size = StructureCatalog.Extents(Construct) };
        }

        public override void _Process(double delta)
        {
            if (!Active || Cam == null || _ghost == null) return;
            if (!TryAim(out Vector3 world)) return;

            var c = Construct;
            var (snapped, yaw) = StructureCatalog.Snap(world, c);
            _ghost.GlobalPosition = snapped + Vector3.Up * StructureCatalog.PivotOffset(c);
            _ghost.RotationDegrees = new Vector3(0f, yaw, 0f);

            var sm = StructureManager.Instance;
            _reason = sm == null ? null : (sm.CanPlace(world, c, Tier, out var why) ? null : why);
            _mat.AlbedoColor = _reason == null ? OkTint : BadTint;
        }

        /// <summary>Aim point: the eye ray out to the placement reach. Retail bounds this at 16 m
        /// (SDK HousingConnections.cs:248 MAX_PLACEMENT_DISTANCE) -- without a cap you can place across a
        /// valley, which is how players build unreachable sky bases.</summary>
        bool TryAim(out Vector3 pos)
        {
            pos = Vector3.Zero;
            if (Cam == null) return false;
            var space = GetWorld3D()?.DirectSpaceState;
            Vector3 from = Cam.GlobalPosition, dir = -Cam.GlobalTransform.Basis.Z;
            float reach = StructureCatalog.MaxPlacementDistance;
            if (space != null)
            {
                var q = PhysicsRayQueryParameters3D.Create(from, from + dir * reach);
                q.CollisionMask = 1u << 0;                     // ground + existing structures
                var hit = space.IntersectRay(q);
                if (hit.Count > 0) { pos = (Vector3)hit["position"]; return true; }
            }
            pos = from + dir * (reach * 0.5f);                 // nothing under the cursor: place at half reach
            return true;
        }

        /// <summary>Place at the current aim. Returns the piece, or null when the manager refused it.</summary>
        public StructureManager.Piece Place()
        {
            if (!Active || Cam == null) return null;
            var sm = StructureManager.Instance;
            if (sm == null) return null;
            if (!TryAim(out Vector3 world)) return null;
            return sm.Place(world, Construct, Tier);
        }

        /// <summary>Scripted/demo placement seam, kept from the old tool so render harnesses and tests can build
        /// without a camera. Goes through the SAME manager path as a player placement -- a demo that bypassed
        /// the rules would prove nothing about whether the rules work.</summary>
        public StructureManager.Piece Spawn(Vector3 world, EConstruct c, int tier)
            => StructureManager.Instance?.Place(world, c, tier);
    }
}
