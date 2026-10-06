using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>
    /// Cosmetic hinges shared by real vehicles and mesh-only replicas. No process callback, occupancy
    /// authority, rigid-body shapes, or network messages. Hood/trunk targets are LOCAL SP UI state only;
    /// doors derive transient pulses from already-replicated occupant identities.
    /// Pivots are direct owner children: GlassHit -> pane -> pivot -> Vehicle fits Owning's four levels.
    /// </summary>
    public sealed class VehiclePanelRig
    {
        public const float SeatPulseSeconds = 2.5f;
        public const float SwingSeconds = 0.45f;

        sealed class Panel
        {
            public AuthoredPanelDef Def;
            public Node3D Pivot;
            public MeshInstance3D Mesh, Glass;
            public StaticBody3D QueryBody;
            public Vector3 Axis;
            public float Fraction, PulseRemaining;
            public bool CompartmentOpen;
            public bool TargetOpen => Def.SeatIndex >= 0 ? PulseRemaining > 0f : CompartmentOpen;
        }

        public readonly struct PanelState
        {
            public readonly float Fraction, OpenAmount, PulseRemaining;
            public readonly bool TargetOpen, CompartmentOpen;
            internal PanelState(float fraction, float pulseRemaining, bool targetOpen, bool compartmentOpen)
            {
                Fraction = fraction;
                OpenAmount = Ease(fraction);
                PulseRemaining = pulseRemaining;
                TargetOpen = targetOpen;
                CompartmentOpen = compartmentOpen;
            }
        }

        readonly Node3D _owner;
        readonly Material _sharedPaint;
        readonly bool _queryColliders;
        readonly List<Panel> _panels = new();
        readonly Dictionary<int, Panel> _byIndex = new();
        readonly List<MeshInstance3D> _paintedMeshes = new();
        static readonly Dictionary<string, ConcavePolygonShape3D> QueryShapes = new();
        uint[] _occupants = Array.Empty<uint>();
        bool _occupancyBaseline;
        Material _charMaterial;
        uint[] _passengerScratch = Array.Empty<uint>();

        VehiclePanelRig(Node3D owner, Material sharedPaint, bool queryColliders)
        {
            _owner = owner;
            _sharedPaint = sharedPaint;
            _queryColliders = queryColliders;
        }

        public Node3D Owner => _owner;
        public int Count => _panels.Count;
        public int PaintedMeshCount => _paintedMeshes.Count;
        public bool HasOccupancyBaseline => _occupancyBaseline;
        public bool IsCharred => _charMaterial != null;
        public int QueryColliderCount
        {
            get
            {
                int count = 0;
                foreach (var mesh in _paintedMeshes)
                    foreach (var child in mesh.GetChildren())
                        if (child is StaticBody3D) count++;
                return count;
            }
        }

        // Public seams are keyed by authored PanelIndex, NOT by array order.
        Panel At(int panelIndex) => _byIndex.TryGetValue(panelIndex, out var panel)
            ? panel : throw new ArgumentOutOfRangeException(nameof(panelIndex));
        public AuthoredPanelDef GetDefinition(int panelIndex) => At(panelIndex).Def;
        public Node3D GetPivot(int panelIndex) => At(panelIndex).Pivot;
        public MeshInstance3D GetMesh(int panelIndex) => At(panelIndex).Mesh;
        public MeshInstance3D GetGlassPane(int panelIndex) => At(panelIndex).Glass;
        public StaticBody3D GetQueryBody(int panelIndex) => At(panelIndex).QueryBody;
        public MeshInstance3D GetPaintedMesh(int ordinal) => _paintedMeshes[ordinal];
        public float GetFraction(int panelIndex) => At(panelIndex).Fraction;
        public float GetOpenAmount(int panelIndex) => Ease(At(panelIndex).Fraction);
        public bool IsOpen(int panelIndex) => At(panelIndex).TargetOpen;
        public float GetPulseRemaining(int panelIndex) => At(panelIndex).PulseRemaining;
        /// <summary>Body-space panel mesh pose: T(H) * R(axis, signed angle) * T(-H).</summary>
        public Transform3D GetPose(int panelIndex)
        {
            var p = At(panelIndex);
            return p.Pivot.Transform * p.Mesh.Transform;
        }
        public PanelState GetState(int panelIndex)
        {
            var p = At(panelIndex);
            return new PanelState(p.Fraction, p.PulseRemaining, p.TargetOpen, p.CompartmentOpen);
        }
        public uint GetOccupant(int seatIndex)
            => seatIndex >= 0 && seatIndex < _occupants.Length ? _occupants[seatIndex] : 0u;

        /// <summary>Paths may be content-relative .txt OBJ files or full res:// paths.</summary>
        static string ContentPath(string path)
            => path.StartsWith("res://", StringComparison.Ordinal) ? path : "res://content/" + path;
        static Mesh LoadMesh(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Authored mesh path is empty.");
            var mesh = ContentProvider.ParseObj(ContentPath(path));
            if (mesh == null || mesh.GetSurfaceCount() == 0)
                throw new InvalidOperationException("Authored mesh is missing or empty: " + path);
            return mesh;
        }
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        public static VehiclePanelRig BuildAuthoredPanelRig(Node3D owner, AuthoredPanelDef[] defs,
            Material sharedPaint, bool queryColliders)
        {
            if (defs == null || defs.Length == 0) return null;
            if (owner == null || !GodotObject.IsInstanceValid(owner)) throw new ArgumentNullException(nameof(owner));
            if (sharedPaint == null) throw new ArgumentNullException(nameof(sharedPaint));
            // Validate/load before mutating the hierarchy, so a bad definition does not leave half a rig.
            var indices = new HashSet<int>();
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var meshes = new Mesh[defs.Length];
            for (int i = 0; i < defs.Length; i++)
            {
                var d = defs[i];
                if (d.PanelIndex < 0 || d.PanelIndex > 31 || !indices.Add(d.PanelIndex))
                    throw new ArgumentException("PanelIndex must be unique and in 0..31.", nameof(defs));
                if (d.SeatIndex < -2 || !Finite(d.Pivot) || !Finite(d.Axis)
                    || d.Axis.LengthSquared() < 1e-10f || !float.IsFinite(d.Degrees))
                    throw new ArgumentException("Invalid authored hinge/seat definition.", nameof(defs));
                if (!string.IsNullOrEmpty(d.GlassLabel) && !labels.Add(d.GlassLabel))
                    throw new ArgumentException("A glass label cannot belong to two panels.", nameof(defs));
                meshes[i] = LoadMesh(d.MeshPath);
            }
            var rig = new VehiclePanelRig(owner, sharedPaint, queryColliders);
            for (int i = 0; i < defs.Length; i++)
            {
                var d = defs[i];
                var pivot = new Node3D { Name = $"AuthoredPivot_{d.PanelIndex}", Position = d.Pivot };
                owner.AddChild(pivot);  // NO extra rig root: bounded Vehicle.Owning must still find the hull.
                var mesh = new MeshInstance3D {
                    Name = $"AuthoredPanel_{d.PanelIndex}", Mesh = meshes[i],
                    MaterialOverride = sharedPaint, Position = -d.Pivot };
                pivot.AddChild(mesh);
                var p = new Panel { Def = d, Axis = d.Axis.Normalized(), Pivot = pivot, Mesh = mesh };
                rig._panels.Add(p);
                rig._byIndex.Add(d.PanelIndex, p);
                rig._paintedMeshes.Add(mesh);
                if (queryColliders) p.QueryBody = rig.AddQueryCollider(mesh, d.MeshPath);
            }
            rig.AttachGlassPanes();
            return rig;
        }

        StaticBody3D AddQueryCollider(MeshInstance3D mesh, string path)
        {
            string key = ContentPath(path);
            if (!QueryShapes.TryGetValue(key, out var shape))
            {
                shape = mesh.Mesh.CreateTrimeshShape();
                shape.BackfaceCollision = true;
                QueryShapes.Add(key, shape);
            }
            // Existing HitMesh/GlassHit pattern: bullet/look/player query layers, mask zero,
            // NOT the world/chassis layer and NOT a shape on a VehicleBody3D.
            var body = new StaticBody3D {
                Name = "PanelHit", CollisionLayer = Vehicle.HitMeshBit | (1u << 5), CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = shape });
            mesh.AddChild(body);  // inherits exactly the same -H/hinge pose as the drawn mesh
            if (_owner is PhysicsBody3D rigid) rigid.AddCollisionExceptionWith(body);
            return body;
        }

        /// <summary>
        /// Optional fixed painted trim, also authored in body space. Call once at build time with
        /// Spec.PaintedParts. Only meshes created here or as panels are ever retagged by Char.
        /// </summary>
        public void AddPaintedParts(string[] meshPaths)
        {
            if (meshPaths == null || meshPaths.Length == 0) return;
            var meshes = new Mesh[meshPaths.Length];
            for (int i = 0; i < meshes.Length; i++) meshes[i] = LoadMesh(meshPaths[i]);
            for (int i = 0; i < meshes.Length; i++)
            {
                var mesh = new MeshInstance3D {
                    Name = $"AuthoredPainted_{_paintedMeshes.Count}", Mesh = meshes[i],
                    MaterialOverride = _charMaterial ?? _sharedPaint };
                mesh.SetMeta("no_outline", true); // fixed interior: do not draw its silhouette through windows
                _owner.AddChild(mesh);
                _paintedMeshes.Add(mesh);
                if (_queryColliders) AddQueryCollider(mesh, meshPaths[i]);
            }
        }

        static void FindPanes(Node node, string name, List<MeshInstance3D> found)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is MeshInstance3D mesh && mesh.Name.ToString() == name) found.Add(mesh);
                FindPanes(child, name, found);
            }
        }
        Transform3D InOwnerSpace(Node3D node)
        {
            var transform = node.Transform;
            for (Node parent = node.GetParent(); parent != _owner; parent = parent.GetParent())
            {
                if (parent == null) throw new InvalidOperationException("Pane is not beneath the rig owner.");
                if (parent is Node3D spatial) transform = spatial.Transform * transform;
            }
            return transform;
        }

        /// <summary>
        /// Reuses existing panes; NEVER replaces them, their child GlassHit, or Vehicle's pane lists.
        /// Works before entering the scene tree. Call again if glass was constructed after this rig.
        /// Missing labels are allowed (single-mesh canopy fallback); return value is panes attached now.
        /// </summary>
        public int AttachGlassPanes()
        {
            int attached = 0;
            foreach (var p in _panels)
            {
                if (string.IsNullOrEmpty(p.Def.GlassLabel)) continue;
                var found = new List<MeshInstance3D>();
                FindPanes(_owner, "Glass_" + p.Def.GlassLabel, found);
                if (found.Count > 1) throw new InvalidOperationException("Ambiguous authored glass label: " + p.Def.GlassLabel);
                if (found.Count == 0) continue;
                var pane = found[0];
                p.Glass = pane;
                if (pane.GetParent() == p.Pivot) continue;
                var closed = InOwnerSpace(pane);
                pane.Reparent(p.Pivot, false);
                // Use the CLOSED pivot, even if attachment is deferred until a door is already open.
                pane.Transform = new Transform3D(Basis.Identity, -p.Def.Pivot) * closed;
                attached++;
            }
            return attached;
        }

        public static float Ease(float fraction)
        {
            float t = Mathf.Clamp(fraction, 0f, 1f);
            return t * t * (3f - 2f * t); // smoothstep: zero speed at both endpoints, no overshoot through shut
        }
        static void Advance(Panel p, float dt, bool open)
            => p.Fraction = Mathf.MoveToward(p.Fraction, open ? 1f : 0f, dt / SwingSeconds);

        public void Tick(float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0f || !GodotObject.IsInstanceValid(_owner)) return;
            foreach (var p in _panels)
            {
                float previousFraction = p.Fraction;
                if (p.Def.SeatIndex >= 0)
                {
                    // Split at timer expiry so a long frame advances through opening AND closing.
                    float held = Mathf.Min(dt, p.PulseRemaining);
                    if (held > 0f) Advance(p, held, true);
                    p.PulseRemaining = Mathf.Max(0f, p.PulseRemaining - dt);
                    if (dt > held) Advance(p, dt - held, false);
                }
                else Advance(p, dt, p.CompartmentOpen);
                if (p.Fraction != previousFraction)
                    p.Pivot.Transform = new Transform3D(
                        new Basis(p.Axis, Mathf.DegToRad(p.Def.Degrees) * Ease(p.Fraction)), p.Def.Pivot);
            }
        }

        /// <summary>For actual entry/exit only; do not call for seat swaps or initial occupancy.</summary>
        public void PulseSeat(int seatIndex)
        {
            if (seatIndex < 0) return;
            foreach (var p in _panels)
                if (p.Def.SeatIndex == seatIndex) p.PulseRemaining = SeatPulseSeconds;
        }

        /// <summary>LOCAL persistent UI latch. No multiplayer compartment command/state is supplied.</summary>
        public void SetCompartment(Vehicle.AccessKind kind, bool open)
        {
            int seat = kind == Vehicle.AccessKind.Hood ? -1 : kind == Vehicle.AccessKind.Trunk ? -2 : int.MinValue;
            foreach (var p in _panels)
                if (p.Def.SeatIndex == seat) p.CompartmentOpen = open;
        }

        /// <summary>
        /// Zero means empty; passengers[0] is seat 1. First sample is baseline only. Copy input because
        /// replica arrays may be reused/mutated. Occupants present in BOTH samples moved seats internally
        /// and never cause a pulse, even if their old/new seats changed. Only actual vehicle entry/exit
        /// pulses the affected seat. A direct occupant replacement pulses that seat once.
        /// </summary>
        public void ApplyOccupancy(ushort driver, ushort[] passengers)
        {
            int length = passengers?.Length ?? 0;
            if (_passengerScratch.Length != length) _passengerScratch = new uint[length];
            for (int i = 0; i < length; i++) _passengerScratch[i] = passengers[i];
            ApplyOccupancy((uint)driver, _passengerScratch);
        }

        public void ApplyOccupancy(uint driver, uint[] passengers)
        {
            int passengerCount = passengers?.Length ?? 0;
            if (_occupancyBaseline && _occupants.Length == 1 + passengerCount && _occupants[0] == driver)
            {
                bool unchanged = true;
                for (int i = 0; i < passengerCount; i++)
                    if (_occupants[i + 1] != passengers[i]) { unchanged = false; break; }
                if (unchanged) return; // immutable occupancy: zero per-frame array/set allocations
            }
            var next = new uint[1 + (passengers?.Length ?? 0)];
            next[0] = driver;
            if (passengers != null) Array.Copy(passengers, 0, next, 1, passengers.Length);
            if (_occupancyBaseline)
            {
                var before = new HashSet<uint>(_occupants);
                var after = new HashSet<uint>(next);
                int seats = Math.Max(_occupants.Length, next.Length);
                for (int seat = 0; seat < seats; seat++)
                {
                    uint oldId = seat < _occupants.Length ? _occupants[seat] : 0u;
                    uint newId = seat < next.Length ? next[seat] : 0u;
                    if (oldId == newId) continue;
                    bool exited = oldId != 0 && !after.Contains(oldId);
                    bool entered = newId != 0 && !before.Contains(newId);
                    if (exited || entered) PulseSeat(seat);
                }
            }
            _occupants = next;
            _occupancyBaseline = true;
        }

        /// <summary>Use only on an ownership/source handoff, not on each replication sample.</summary>
        public void ResetOccupancyBaseline()
        {
            _occupants = Array.Empty<uint>();
            _occupancyBaseline = false;
        }

        /// <summary>
        /// Retag only this opt-in rig's metal pieces; do not mutate shared paint or walk the whole vehicle.
        /// Glass visibility/material and attached breakable colliders are deliberately left alone.
        /// Existing Vehicle explosion still handles the body and glass separately.
        /// </summary>
        public void Char(Material material)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            _charMaterial = material;
            foreach (var mesh in _paintedMeshes)
                if (GodotObject.IsInstanceValid(mesh)) mesh.MaterialOverride = material;
        }
    }
}
