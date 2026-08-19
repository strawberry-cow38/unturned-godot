using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A train that rides a track ROAD spline (RoadField material 4 = Tracks): a locomotive + trailing cargo cars,
    // each sitting on 2 bogies snapped to the rail at its distance-along. The body SPANS its two bogies so it
    // articulates correctly through curves; the cars trail at fixed 11 m offsets. Spawned onto the nearest track
    // by the `spawntrain` console command. Movement (throttle -> advance the distance) is the next phase; for now
    // it is placed statically on the rail. (master 2026-08-18)
    public partial class Train : Node3D
    {
        RoadField _roads;
        int _road;
        float _s;                       // distance-along the track of the LOCO's centre
        const float RailY = 1.55f;      // lift the body so its wheels sit ON the rail (master: "a little higher on the tracks")
        const float BogieHalf = 3.5f;   // bogie spacing from a unit's centre (source Track_Front/Back at +-3.5)
        const float CarGap = 11f;       // car-to-car spacing along the rail (source Train_Car spacing)
        readonly List<(Node3D body, MeshInstance3D bf, MeshInstance3D bb, float off)> _units = new();
        const float MaxSpeed = 40f, Accel = 3f, Decel = 2f;   // BIG inertia (master): high top speed, slow to build, long coast
        float _speed;

        /// <summary>Spawn a train onto the nearest track spline to <paramref name="near"/>. Null if there is no
        /// track road (material 4) in the world (only Yukon has tracks).</summary>
        public static Train Spawn(Node parent, RoadField roads, Vector3 near)
        {
            if (roads == null || !roads.NearestTrack(near, out int road, out float s)) return null;
            var t = new Train { _roads = roads, _road = road };
            // Keep the whole train ON the rail: the tail car sits at s-33, the loco's front bogie at s+3.5, so
            // clamp the loco's centre into a range that fits (open roads clamp; a loop wraps so any s is fine).
            if (roads.RoadLoops(road)) t._s = s;
            else { float len = roads.RoadLength(road); t._s = Mathf.Clamp(s, 3f * CarGap + BogieHalf, Mathf.Max(3f * CarGap + BogieHalf, len - BogieHalf)); }
            parent.AddChild(t);
            t.Build();
            return t;
        }

        Material MakeMat(string tex, Color? liveryBody)
        {
            var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            if (img.Load(ProjectSettings.GlobalizePath($"res://content/{tex}.png")) == Error.Ok)
            {
                // PAINTABLE LIVERY: recolour the body palette slot (0,1) to a random livery; the orange stripe
                // slot (3,1) stays fixed (master). vanilla trains are not paintable -- this is our addition.
                if (liveryBody.HasValue) { img.Convert(Image.Format.Rgba8); img.SetPixel(0, 1, liveryBody.Value); }
                m.AlbedoTexture = ImageTexture.CreateFromImage(img);
            }
            return m;
        }

        void Build()
        {
            AddToGroup("trains");   // so the player can find + board the nearest one
            // random livery per spawn: 10% muted grey else a muted random hue (the game's RandomHueOrGrayscale feel)
            Color livery = GD.Randf() < 0.1f ? new Color(0.45f, 0.45f, 0.47f) : Color.FromHsv(GD.Randf(), 0.5f, 0.55f);
            var bodyMat = MakeMat("train_body_tex", livery);
            var carMat = MakeMat("train_car_tex", null);
            var bogieMat = MakeMat("train_bogie_tex", null);
            Mesh Lm(string n) => ContentProvider.ParseObj($"res://content/{n}.txt");
            Mesh body = Lm("train_body"), bogie = Lm("train_bogie"), car = Lm("train_car");

            void MakeUnit(Mesh m, Material mat, float off, Vector3 boxSize, Vector3 boxCenter)
            {
                var sb = new StaticBody3D();   // solid: the player collides with it + can stand on it
                sb.AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = mat });
                sb.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = boxSize }, Position = boxCenter });
                var bf = new MeshInstance3D { Mesh = bogie, MaterialOverride = bogieMat };
                var bb = new MeshInstance3D { Mesh = bogie, MaterialOverride = bogieMat };
                AddChild(sb); AddChild(bf); AddChild(bb);
                _units.Add((sb, bf, bb, off));
            }
            MakeUnit(body, bodyMat, 0f, new Vector3(3.4f, 4.1f, 10.8f), new Vector3(0f, 1.27f, 0f));           // loco
            MakeUnit(car, carMat, CarGap, new Vector3(3.4f, 1.8f, 10.8f), new Vector3(0f, 0.13f, 0f));        // car 1
            MakeUnit(car, carMat, 2f * CarGap, new Vector3(3.4f, 1.8f, 10.8f), new Vector3(0f, 0.13f, 0f));   // car 2
            MakeUnit(car, carMat, 3f * CarGap, new Vector3(3.4f, 1.8f, 10.8f), new Vector3(0f, 0.13f, 0f));   // car 3
            Place();
            ResetPhysicsInterpolation();   // placed this frame -> don't interpolate the units up from the origin pose (project physics_interpolation=true)
        }

        void PlaceUnit((Node3D body, MeshInstance3D bf, MeshInstance3D bb, float off) u, float sctr)
        {
            _roads.EvaluateAlong(_road, sctr + BogieHalf, out var pf, out var tf);
            _roads.EvaluateAlong(_road, sctr - BogieHalf, out var pb, out var tb);
            Vector3 c = (pf + pb) * 0.5f + Vector3.Up * RailY;
            Vector3 fwd = pf - pb; fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
            u.body.GlobalTransform = new Transform3D(Basis.Identity, c).LookingAt(c + fwd, Vector3.Up);
            Vector3 cf = pf + Vector3.Up * (RailY - 0.4f);
            u.bf.GlobalTransform = new Transform3D(Basis.Identity, cf).LookingAt(cf + tf, Vector3.Up);
            Vector3 cb = pb + Vector3.Up * (RailY - 0.4f);
            u.bb.GlobalTransform = new Transform3D(Basis.Identity, cb).LookingAt(cb + tb, Vector3.Up);
        }

        void Place() { foreach (var u in _units) PlaceUnit(u, _s - u.off); }

        /// <summary>The loco body (unit 0) -- proximity + seat reference.</summary>
        public Node3D Loco => _units.Count > 0 ? _units[0].body : null;

        /// <summary>Driver eye/seat in the loco cab, facing forward down the rail (loco -Z).</summary>
        public Transform3D DriverEyeWorld
        {
            get { var l = Loco; return l != null ? l.GetGlobalTransformInterpolated() * new Transform3D(Basis.Identity, new Vector3(0f, 2.3f, -2.6f)) : GlobalTransform; }
        }

        /// <summary>Advance the whole train along the rail by the throttle (W/S). No steering -- the rail steers.
        /// Cars trail on their fixed offsets. Open roads stop at the ends; a loop wraps.</summary>
        public void Drive(float throttle, float dt)
        {
            float target = Mathf.Clamp(throttle, -0.6f, 1f) * MaxSpeed;
            float rate = Mathf.Abs(throttle) < 0.05f ? Decel : Accel;   // released -> long coast (Decel); throttle held -> slow build / brake (Accel)
            _speed = Mathf.MoveToward(_speed, target, rate * dt);
            _s += _speed * dt;
            if (!_roads.RoadLoops(_road))
            {
                float lo = 3f * CarGap + BogieHalf, hi = Mathf.Max(lo, _roads.RoadLength(_road) - BogieHalf);
                if (_s < lo) { _s = lo; _speed = 0f; }
                if (_s > hi) { _s = hi; _speed = 0f; }
            }
            Place();
        }

        bool _lookFocused;
        System.Collections.Generic.List<MeshInstance3D> _locoMeshes;

        static void CollectMeshes(Node n, System.Collections.Generic.List<MeshInstance3D> outl)
        {
            if (n is MeshInstance3D mi) outl.Add(mi);
            foreach (var c in n.GetChildren()) CollectMeshes(c, outl);
        }

        /// <summary>Outline the LOCO (its body mesh + 2 bogies) when the player looks at it, exactly like a
        /// Vehicle: flip the meshes onto OutlineOverlay.OutlineLayer so the mask cam draws the rim.</summary>
        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (_locoMeshes == null)
            {
                _locoMeshes = new System.Collections.Generic.List<MeshInstance3D>();
                if (_units.Count > 0)
                {
                    CollectMeshes(_units[0].body, _locoMeshes);
                    if (_units[0].bf != null) _locoMeshes.Add(_units[0].bf);
                    if (_units[0].bb != null) _locoMeshes.Add(_units[0].bb);
                }
            }
            foreach (var mi in _locoMeshes)
                if (IsInstanceValid(mi))
                    mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = new Color(0.55f, 0.8f, 1f);
        }

        /// <summary>Does the look-ray pass through the loco's hull box? Segment vs the loco OBB (3.4x4.1x10.8 at
        /// local (0,1.27,0)), matching Vehicle.LookRayHitsHull -- lets the player focus it through the cab glass.</summary>
        public bool LookRayHitsLoco(Vector3 from, Vector3 to)
        {
            if (_units.Count == 0 || !IsInstanceValid(_units[0].body)) return false;
            var inv = _units[0].body.GlobalTransform.AffineInverse();
            var size = new Vector3(3.4f, 4.1f, 10.8f);
            var min = new Vector3(0f, 1.27f, 0f) - size * 0.5f;
            return new Aabb(min, size).IntersectsSegment(inv * from, inv * to);
        }
    }
}
