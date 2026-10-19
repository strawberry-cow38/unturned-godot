using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    public partial class ObjectDoor
    {
        List<(CollisionShape3D Shape, Vector3 Centre)> _plateFrame;
        Vector3 _plateCentre;
        Vector2 _plateSize;
        GlassPane _platePane;
        CollisionShape3D _platePaneCollider;
        Node3D _plateShards;
        (Color Tint, float Hp, bool Indestructible, bool Broken, bool Covered)? _plateOptions;
        public event System.Action PlateGlassShattered;
        public bool HasPlateGlassFrame => _plateFrame != null;
        public GlassPane PlatePane => IsInstanceValid(_platePane) && !_platePane.IsQueuedForDeletion() ? _platePane : null;

        internal void UsePlateGlassFrame(List<Aabb> boxes, Vector3 centre, Vector2 size)
        {
            // Spawn may run before _Ready when the caller builds an off-tree host. Retain the geometry;
            // the ordinary readiness path invokes InstallPlateGlassFrame once the hinge exists.
            _plateBoxesPending = boxes; _plateCentre = centre; _plateSize = size;
            if (_pivot != null) InstallPlateGlassFrame();
        }
        List<Aabb> _plateBoxesPending;
        void InstallPlateGlassFrame()
        {
            if (_plateBoxesPending == null) return;
            if (_leafCollider != null) { RemoveChild(_leafCollider); _leafCollider.QueueFree(); }
            _plateFrame = new();
            foreach (var box in _plateBoxesPending)
            {
                var shape = new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size }, Position = box.GetCenter() };
                AddChild(shape); _plateFrame.Add((shape, box.GetCenter()));
            }
            _plateBoxesPending = null;
            _leafCollider = _plateFrame[0].Shape;  // legacy debug seam; never a solid rectangle across the pane
            SetPlateGlassOptions(GlassPane.DefaultHue, 1f, false, false, false);
            ApplySwing(_swing);
        }

        public void SetPlateGlassOptions(Color tint, float hp, bool indestructible, bool broken, bool covered)
        {
            if (!HasPlateGlassFrame) return;
            var options = (tint, Mathf.Max(1f, hp), indestructible, broken, covered);
            if (_plateOptions == options) return;
            _plateOptions = options;
            // Removing a pane deliberately (editor repair/options) does not fire a shatter callback or FX.
            if (IsInstanceValid(_platePane)) { _platePane.GetParent()?.RemoveChild(_platePane); _platePane.QueueFree(); }
            _platePane = null; _platePaneCollider = null;
            if (IsInstanceValid(_plateShards)) { _plateShards.GetParent()?.RemoveChild(_plateShards); _plateShards.QueueFree(); }
            _plateShards = null;
            // Native authored door normal = Y. Stand-up turns it into the wall's Z face; all pane geometry
            // and remnants are under the actual hinge, so opening never leaves a stationary pane in the hole.
            var transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi * .5f), _plateCentre - _pivotLocal);
            if (broken)
            {
                _plateShards = GlassShards.Build(_plateSize, tint, 173);
                if (_plateShards != null) { _pivot.AddChild(_plateShards); _plateShards.Transform = transform; }
            }
            else
            {
                _platePane = GlassPane.Build(_plateSize, tint, hp, indestructible);
                _platePane.Name = "PlateGlassPane"; _platePane.Covered = covered;
                _platePane.SetMeta(Sn.objectdoor, this);
                _pivot.AddChild(_platePane); _platePane.Transform = transform;
                foreach (var c in _platePane.GetChildren()) if (c is CollisionShape3D s) _platePaneCollider = s;
                _platePane.OnShattered += () =>
                {
                    // Flag synchronously: same-frame save and a subsequent wall rebuild must not repair it.
                    _plateOptions = (tint, Mathf.Max(1f, hp), indestructible, true, covered);
                    _platePaneCollider = null;
                    PlateGlassShattered?.Invoke();
                    // Do not remove/reparent the pane here: GlassPane.Shatter still needs its tree/global
                    // transform for the existing window sound and shard burst after this event returns.
                    _plateShards = GlassShards.Build(_plateSize, tint, 173);
                    if (_plateShards != null) { _pivot.AddChild(_plateShards); _plateShards.Transform = transform; }
                };
            }
            UpdatePlateSolidity(_swing <= .001f || _swing >= .999f);
        }

        void MovePlateColliders(Basis basis, Vector3 slide, bool atRest)
        {
            foreach (var part in _plateFrame)
                part.Shape.Transform = new Transform3D(basis, basis * (part.Centre - _pivotLocal) + _pivotLocal + slide);
            UpdatePlateSolidity(atRest);
        }

        bool PlateColliderClear(CollisionShape3D shape)
        {
            if (shape == null || !IsInstanceValid(shape) || GetWorld3D() == null) return true;
            var q = new PhysicsShapeQueryParameters3D { Shape = shape.Shape, Transform = shape.GlobalTransform,
                CollisionMask = (1u << 3) | (1u << 1) };
            return GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count == 0;
        }
        void UpdatePlateSolidity(bool atRest)
        {
            bool clear = atRest;
            if (clear)
                foreach (var part in _plateFrame) if (!PlateColliderClear(part.Shape)) { clear = false; break; }
            if (clear && _platePaneCollider != null) clear = PlateColliderClear(_platePaneCollider);
            foreach (var part in _plateFrame) part.Shape.Disabled = !clear;
            if (_platePaneCollider != null) _platePaneCollider.Disabled = !clear;
            _pendingSolid = atRest && !clear;
        }
    }
}