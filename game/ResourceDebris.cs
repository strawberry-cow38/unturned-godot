using Godot;

namespace UnturnedGodot
{
    // A felled tree, mid-topple. Retail spawns the tree's own model as a Rigidbody, shoves it, and
    // `Destroy(gib, 8f)` -- no settle check, no pooling, it just goes. The fade in the last second is
    // ours: 8 s of physics ending in a tree vanishing mid-frame is worse than 8 s ending in one that
    // dissolves, and a settled trunk sitting in the grass forever is worse than both.
    //
    // The material is SHARED with the standing MultiMesh for this type, so fading it would fade every
    // other tree of the same species on the map. Hence the per-body duplicate.
    public partial class ResourceDebris : RigidBody3D
    {
        public float Life = 8f;
        readonly System.Collections.Generic.List<StandardMaterial3D> _fadeMats = new();
        bool _fading;

        /// <summary>The shove, applied on the body's FIRST integration step rather than at AddChild time.
        /// Applying it straight after AddChild put linear velocity on the body but no angular velocity at
        /// all -- an off-centre impulse 6 m up the trunk still left the rotation at exactly zero, which is
        /// not something rigid-body dynamics does. The body is not fully in the physics server yet at that
        /// point; _IntegrateForces is the first moment it provably is.</summary>
        public Vector3 PendingImpulse;
        public Vector3 PendingOffset;
        bool _shoved;

        public override void _IntegrateForces(PhysicsDirectBodyState3D state)
        {
            if (_shoved) return;
            _shoved = true;
            if (PendingImpulse == Vector3.Zero) return;
            state.ApplyImpulse(PendingImpulse, PendingOffset);
        }

        public override void _Process(double delta)
        {
            Life -= (float)delta;
            if (Life <= 1f && !_fading) { _fading = true; BeginFade(); }
            if (_fading)
            {
                float a = Mathf.Clamp(Life, 0f, 1f);
                foreach (var m in _fadeMats) { var c = m.AlbedoColor; c.A = a; m.AlbedoColor = c; }
            }
            if (Life <= 0f) QueueFree();
        }

        void BeginFade()
        {
            // One duplicate PER PART. A tree is bark + leaves with different textures, so a single shared
            // fade material would repaint the canopy with the trunk's bark on the way out.
            foreach (var child in GetChildren())
            {
                if (child is not MeshInstance3D mi || mi.MaterialOverride is not StandardMaterial3D src) continue;
                var fade = (StandardMaterial3D)src.Duplicate();   // never mutate the shared type material
                fade.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;   // scissor cannot blend; the leaf texture's own alpha still cuts the quads
                mi.MaterialOverride = fade;
                _fadeMats.Add(fade);
            }
        }
    }
}
