using Godot;

namespace UnturnedGodot
{
    /// <summary>ONE quad that spins about a SINGLE axis to face the camera -- master 2026-10-06: "the power io
    /// arrows that show input vs output shouldnt be crossing billboards. should be one arrow billboard that spins
    /// on one axis following the camera."
    ///
    /// ⭐ WHY NOT `BillboardMode.FixedY`. Godot's built-in fixed-axis billboard spins about an axis the material
    /// picks, not about the arrow's own FLOW axis -- and these arrows point along whatever direction their port
    /// faces, which is usually horizontal. The axis that has to stay pinned is the one the arrow points down, so
    /// the rotation is done here where that axis is known. Being explicit also means the behaviour does not depend
    /// on which Godot version's billboard convention is in play, which is not a thing worth guessing at.
    ///
    /// The node's own +Y is the spin axis (`ConnectionPort.RotateYTo(flow)` already aims it down the flow), so all
    /// this does is yaw the child quad about local Y until its +Z face looks at the camera. The arrow keeps
    /// pointing exactly where it pointed; only the flat side turns toward you.
    ///
    /// ⚠ Hub-ticked, not `_Process`. A port arrow is a per-DEPLOYABLE node and a base can hold hundreds; a
    /// per-node engine callback each is the tax [[reference_unturned_profiling]] is about. It also skips entirely
    /// while hidden, which is most of the time.</summary>
    public partial class AxisBillboard : Node3D
    {
        MeshInstance3D _quad;

        /// <summary>A billboarded quad whose +Y (the arrow's direction) is this node's local +Y.</summary>
        public static AxisBillboard Make(Vector2 size, Material mat)
        {
            var b = new AxisBillboard();
            b._quad = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = size },
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            b.AddChild(b._quad);
            return b;
        }

        public override void _Ready() { TickHub.AddProcess(this, Tick); SetProcess(false); }
        public override void _ExitTree() { TickHub.RemoveProcess(this); }

        void Tick(double delta)
        {
            if (_quad == null || !IsInstanceValid(_quad) || !IsVisibleInTree()) return;
            var cam = GetViewport()?.GetCamera3D();
            if (cam == null || !IsInstanceValid(cam)) return;
            // The camera in THIS node's frame. Local +Y is the pinned axis, so only the XZ bearing matters: yaw the
            // quad about Y until its +Z normal points at the viewer. A camera dead on the axis leaves the bearing
            // undefined, and holding the last angle is right -- the quad is edge-on there and the turn is invisible.
            Vector3 l = ToLocal(cam.GlobalPosition);
            if (l.X * l.X + l.Z * l.Z < 1e-8f) return;
            _quad.Basis = new Basis(Vector3.Up, Mathf.Atan2(l.X, l.Z));
        }
    }
}
