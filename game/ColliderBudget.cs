using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Streams prop COLLISION in around the player instead of keeping every collider on the map resident.
    //
    // WHY. A node census of the real map counts 42,143 nodes, and 13,900 of them are collision: 5,886
    // StaticBody3D + 8,014 CollisionShape3D, a third of the whole tree. Every one is built at load and never
    // leaves the broadphase. The `profiler` overlay reads `physics 4.4 ms/step` against **13 active bodies** -- a
    // solver with thirteen active bodies is doing nothing, so that time is broadphase and static bookkeeping
    // over the other 13,887. At the 50 Hz sim that is ~220 ms of CPU per second. Retail streams collision;
    // this is the port catching up.
    //
    // DISABLED SHAPES, NOT CollisionLayer=0. Destructibles already drive CollisionLayer -- DestructibleField
    // .SetAlive zeroes it to break a prop and restores it on the rubble reset. A distance budget writing the
    // same field would fight it, and the visible failure would be a smashed prop going solid again when you
    // walk away and back. CollisionShape3D.Disabled is a separate axis, so the two compose: a prop is
    // collidable only when it is BOTH intact (layer) and near (shape enabled), and neither has to know about
    // the other.
    //
    // PER CELL, NOT PER BODY. Testing 13,900 bodies four times a second would put the budget's own cost into
    // the profile it is meant to shrink. Bodies bucket into 64 m cells once at build; an update tests cells
    // (a few hundred) and only touches bodies in a cell that actually crossed the boundary, which on a walking
    // player is a handful per second and zero when standing still.
    public partial class ColliderBudget : Node
    {
        public override void _Ready() { TickHub.AddProcess(this, HubProcess); SetProcess(false); }   // PERF: hub-ticked (see TickHub.AddProcess)
        public const string Group = "collbudget";

        /// <summary>Per-body collision radius, written by whoever builds the collider: the prop's own RENDER
        /// cull distance. Absent -> DefaultRadius.</summary>
        public static readonly StringName RadiusMeta = "collbudget_radius";

        /// <summary>COLLISION FOLLOWS VISIBILITY, and this is the whole correctness argument.
        ///
        /// A streamed-out collider cannot be raycast, so a bullet passes straight through it (strawberry
        /// caught this). Any fixed radius is therefore a guess about weapon range that is wrong for some gun --
        /// the first cut used a flat 128 m, which is shorter than unturned's long guns and would have quietly
        /// made distant props unshootable in SP. Keying off the prop's own LodTable cull distance instead
        /// makes the rule "if you can see it you can shoot it", which cannot be wrong by construction: the
        /// collider outlives the mesh, and a prop you cannot see is a prop you cannot aim at.
        ///
        /// It also keeps most of the saving, because the two facts line up: the numerous props are the SMALL
        /// ones (64 m cull), and the props that hold collision out to 512 m are the few big buildings.</summary>
        public const float DefaultRadius = 128f;

        /// <summary>UG_COLLDIST: 0 disables the budget entirely (the A/B control -- this is a CPU change and
        /// CPU cannot be measured on the ARM box, so a real machine needs something to compare against), a
        /// positive value forces one flat radius for every body, and unset (-1) uses each prop's own.</summary>
        public static float Flat = ParseFlat();
        static float ParseFlat()
        {
            var s = System.Environment.GetEnvironmentVariable("UG_COLLDIST");
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out float v) && v >= 0f ? v : -1f;
        }
        public static bool Disabled => Flat == 0f;

        /// <summary>Re-enable band. A cell switches ON inside Radius and OFF only past Radius+Margin, so a
        /// player standing exactly on a boundary does not thrash a hundred bodies in and out every update.</summary>
        const float Margin = 24f;
        const float Cell = 64f;
        const float Interval = 0.25f;

        sealed class Chunk
        {
            public Vector3 Centre;
            public float Radius;     // the cull distance shared by this chunk's props
            public readonly List<CollisionShape3D> Shapes = new();
            public bool On = true;   // everything starts enabled: the world is built collidable
        }

        // Keyed by cell AND radius band: two props in the same 64 m cell with different cull distances cannot
        // share an on/off decision, or the small one would hold collision to the big one's range (or worse,
        // the big one would lose it at the small one's).
        readonly Dictionary<(int, int, int), Chunk> _chunks = new();
        float _clock;
        bool _built;

        public int ChunkCountForTest => _chunks.Count;
        public int EnabledChunksForTest { get { int n = 0; foreach (var c in _chunks.Values) if (c.On) n++; return n; } }

        /// <summary>Bucket every tagged body's shapes by cell. Called once, after the world is built.</summary>
        public void Build()
        {
            _chunks.Clear();
            foreach (var n in GetTree().GetNodesInGroup(Group))
            {
                if (n is not PhysicsBody3D body || !GodotObject.IsInstanceValid(body)) continue;
                var p = body.GlobalPosition;
                float r = Flat > 0f ? Flat
                        : body.HasMeta(RadiusMeta) ? (float)body.GetMeta(RadiusMeta)
                        : DefaultRadius;
                if (r <= 0f) r = DefaultRadius;
                int band = Mathf.RoundToInt(r / 32f);   // quantise so near-identical distances share a chunk
                var key = ((int)Mathf.Floor(p.X / Cell), (int)Mathf.Floor(p.Z / Cell), band);
                if (!_chunks.TryGetValue(key, out var ch))
                {
                    ch = new Chunk { Centre = new Vector3((key.Item1 + 0.5f) * Cell, 0f, (key.Item2 + 0.5f) * Cell), Radius = band * 32f };
                    _chunks[key] = ch;
                }
                foreach (var c in body.GetChildren())
                    if (c is CollisionShape3D cs) ch.Shapes.Add(cs);
            }
            _built = true;
            int shapes = 0; foreach (var c in _chunks.Values) shapes += c.Shapes.Count;
            GD.Print($"[collbudget] {shapes} collision shapes in {_chunks.Count} cells, radius {(Flat > 0f ? $"{Flat:0}m flat" : "per-prop (LodTable cull)")}");
        }

        float _settle;

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            if (Disabled) return;
            // Build LAZILY. Both world-build paths are async, so a Build() call placed after them in source
            // order is not necessarily after them in time, and a budget that scanned an empty group would
            // silently manage nothing -- the same shape as the shadow budget shipping unreachable. Waiting for
            // the group to be populated and then settling briefly needs no cooperation from either path.
            //
            // Anything created AFTER the scan (a client's holiday props at the join handshake) is simply never
            // in a chunk, so it is never disabled. That is the safe direction to fail: an unmanaged collider
            // stays solid forever, it does not vanish.
            if (!_built)
            {
                if (GetTree().GetNodeCountInGroup(Group) == 0) return;
                _settle += (float)delta;
                if (_settle < 1.0f) return;
                Build();
                return;
            }
            _clock += (float)delta;
            if (_clock < Interval) return;
            _clock = 0f;
            Rebalance();
        }

        /// <summary>Public so a test can drive it without waiting real seconds.</summary>
        public void Rebalance()
        {
            if (Disabled) return;
            var focus = FocusPoint();
            if (focus == null) return;
            var f = focus.Value;
            // Compare against the cell's own half-diagonal so a cell counts as "near" when ANY part of it is
            // in range, not just its centre -- otherwise a prop at the far corner of a 64 m cell loses its
            // collider up to 45 m before the radius says it should.
            const float Half = Cell * 0.70711f;
            foreach (var ch in _chunks.Values)
            {
                float d = new Vector2(ch.Centre.X - f.X, ch.Centre.Z - f.Z).Length() - Half;
                bool want = ch.On ? d <= ch.Radius + Margin : d <= ch.Radius;   // hysteresis: leaving needs the extra margin
                if (want == ch.On) continue;
                ch.On = want;
                foreach (var cs in ch.Shapes)
                    if (GodotObject.IsInstanceValid(cs) && cs.Disabled == want) cs.Disabled = !want;
            }
            // Report what the first pass actually left resident. The build line says how much collision EXISTS;
            // this says how much is still in the broadphase, which is the number the change is claiming to move.
            if (!_reported)
            {
                _reported = true;
                int on = 0, total = 0;
                foreach (var c in _chunks.Values) { total += c.Shapes.Count; if (c.On) on += c.Shapes.Count; }
                GD.Print($"[collbudget] first pass: {on} of {total} collision shapes resident ({(total > 0 ? 100.0 * on / total : 0):0.0}%)");
            }
        }
        bool _reported;

        /// <summary>Where collision should stay hot. The CAMERA first: it is unambiguously local, whereas the
        /// "players" group holds every player on a multiplayer client and picking the wrong one would stream
        /// collision in around somebody else. Falls back to the player body when there is no camera yet
        /// (the frame or two before the view is built).</summary>
        Vector3? FocusPoint()
        {
            var cam = GetViewport()?.GetCamera3D();
            if (cam != null && GodotObject.IsInstanceValid(cam)) return cam.GlobalPosition;
            foreach (var p in GetTree().GetNodesInGroup("players"))
                if (p is Node3D n3 && GodotObject.IsInstanceValid(n3)) return n3.GlobalPosition;
            return null;
        }
    }
}
