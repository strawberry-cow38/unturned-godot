using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot
{
    // A TRAIN is a consist of coupled cars riding a track spline (RoadField material 4 = Tracks). Each car spawns
    // solo via `spawntrain <type>` (engine/flatbed/boxcar/tanker); driving an engine into another car at low speed
    // COUPLES them into one consist. Only a consist containing an engine can be driven; total car weight sets how
    // sluggishly it accelerates + brakes. Uncouple hitbox + rope are phase 2. (master 2026-08-19)
    public partial class Train : Node3D
    {
        class Spec { public string Mesh, Tex; public float Weight, HalfLen, YOff; public Vector3 Box, BoxCtr; public bool Engine, Livery, FlipY; }
        static readonly Dictionary<string, Spec> Specs = new()
        {
            ["engine"]  = new Spec { Mesh = "train_body",   Tex = "train_body_tex",   Weight = 20f, HalfLen = 5.34f, YOff = 0f,    Box = new Vector3(3.4f, 4.1f, 10.8f),  BoxCtr = new Vector3(0f, 1.27f, 0f),  Engine = true,  Livery = true  },
            ["flatbed"] = new Spec { Mesh = "train_car",    Tex = "train_car_tex",    Weight = 8f,  HalfLen = 5.34f, YOff = 0f,    Box = new Vector3(3.4f, 1.8f, 10.8f),  BoxCtr = new Vector3(0f, 0.13f, 0f),  Engine = false, Livery = false },
            ["boxcar"]  = new Spec { Mesh = "train_boxcar", Tex = "train_boxcar_tex", Weight = 14f, HalfLen = 5.25f, YOff = 0f, FlipY = true, Box = new Vector3(3.6f, 4.75f, 10.5f), BoxCtr = new Vector3(0f, 1.62f, 0f), Engine = false, Livery = true  },
            ["tanker"]  = new Spec { Mesh = "train_tanker", Tex = "train_tanker_tex", Weight = 16f, HalfLen = 5.34f, YOff = 0f, FlipY = true, Box = new Vector3(3.4f, 4.1f, 10.7f),  BoxCtr = new Vector3(0f, 1.29f, 0f), Engine = false, Livery = true  },
        };
        static readonly Dictionary<string, string> Alias = new() { ["loco"] = "engine", ["locomotive"] = "engine", ["fuel"] = "tanker", ["fueltanker"] = "tanker", ["car"] = "flatbed", ["flat"] = "flatbed", ["box"] = "boxcar" };
        public static string ResolveType(string name)
        {
            name = (name ?? "").ToLowerInvariant().Trim();
            if (Alias.TryGetValue(name, out var a)) name = a;
            return Specs.ContainsKey(name) ? name : null;
        }
        public static string TypeList => string.Join(", ", Specs.Keys);

        class Car
        {
            public string Type; public Spec S;
            public StaticBody3D Body; public MeshInstance3D Bf, Bb;
            public float Off;    // centre offset BEHIND the consist lead (_s); recomputed on couple/uncouple
            public float AbsS;   // scratch: absolute rail distance, used when merging two consists
        }

        RoadField _roads;
        int _road;
        float _s;       // rail distance of the LEAD car's centre (_cars[0])
        float _speed;
        readonly HashSet<Train> _noRecouple = new();   // consists just split from this one: can't re-couple until they've SEPARATED (condition-based, master)
        const float RailY = 1.55f, BogieHalf = 3.5f, CoupleGap = 0.9f;
        const float MaxSpeed = 48f, BaseAccel = 2f, BaseDecel = 1.2f, BaseBrake = 12f, RefWeight = 20f;
        const float CoupleRange = 1.3f, CoupleMaxSpeed = 7f, SeparateMargin = 1.5f;   // couple only when the ends are CLOSE (waits until in range, master); must part by +margin before re-coupling
        readonly List<Car> _cars = new();
        readonly List<MeshInstance3D> _ropes = new();   // short coupler rope per gap (also the look-at/F-uncouple target)
        AudioStreamPlayer3D _engineSnd, _addSnd, _hornSnd;
        bool _occupied;   // base engine loop + rev layer only run while someone is aboard (master)

        static Mesh Lm(string n) => ContentProvider.ParseObj($"res://content/{n}.txt");

        static Material MakeMat(string tex, Color? liveryBody)
        {
            var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            if (img.Load(ProjectSettings.GlobalizePath($"res://content/{tex}.png")) == Error.Ok)
            {
                if (liveryBody.HasValue) { img.Convert(Image.Format.Rgba8); img.SetPixel(0, 1, liveryBody.Value); }   // random livery -> body palette slot (0,1)
                m.AlbedoTexture = ImageTexture.CreateFromImage(img);
            }
            return m;
        }

        /// <summary>Spawn a single car of <paramref name="type"/> onto the nearest track spline. Null if the type is
        /// unknown or there is no track road (material 4) in the world (only Yukon has tracks).</summary>
        public static Train Spawn(Node parent, RoadField roads, Vector3 near, string type)
        {
            type = ResolveType(type);
            if (type == null || roads == null || !roads.NearestTrack(near, out int road, out float s)) return null;
            var spec = Specs[type];
            var t = new Train { _roads = roads, _road = road };
            float end = spec.HalfLen + BogieHalf;
            if (roads.RoadLoops(road)) t._s = s;
            else { float len = roads.RoadLength(road); t._s = Mathf.Clamp(s, end, Mathf.Max(end, len - end)); }
            parent.AddChild(t);
            t.Build(type);
            return t;
        }

        void Build(string type)
        {
            AddToGroup("trains");
            AddCar(type);
            RecomputeOffsets();
            RebuildRopes();
            Place();
            ResetPhysicsInterpolation();
            RebuildAudio();
        }

        void AddCar(string type)
        {
            var s = Specs[type];
            var bogieMat = MakeMat("train_bogie_tex", null);
            Color livery = GD.Randf() < 0.1f ? new Color(0.45f, 0.45f, 0.47f) : Color.FromHsv(GD.Randf(), 0.5f, 0.55f);
            var mat = MakeMat(s.Tex, s.Livery ? livery : (Color?)null);
            var sb = new StaticBody3D();
            var mi = new MeshInstance3D { Mesh = Lm(s.Mesh), MaterialOverride = mat };
            if (s.FlipY) mi.RotationDegrees = new Vector3(0f, 0f, 180f);   // boxcar/tanker meshes are authored upside-down -> flip upright (master)
            sb.AddChild(mi);
            sb.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = s.Box }, Position = s.BoxCtr });
            var bf = new MeshInstance3D { Mesh = Lm("train_bogie"), MaterialOverride = bogieMat };
            var bb = new MeshInstance3D { Mesh = Lm("train_bogie"), MaterialOverride = bogieMat };
            AddChild(sb); AddChild(bf); AddChild(bb);
            _cars.Add(new Car { Type = type, S = s, Body = sb, Bf = bf, Bb = bb });
        }

        // Lead car has offset 0; each following car sits its own half + a coupler gap + the previous car's half behind.
        void RecomputeOffsets()
        {
            for (int i = 0; i < _cars.Count; i++)
                _cars[i].Off = i == 0 ? 0f : _cars[i - 1].Off + _cars[i - 1].S.HalfLen + CoupleGap + _cars[i].S.HalfLen;
        }

        bool HasEngine => _cars.Any(c => c.S.Engine);
        Car EngineCar => _cars.FirstOrDefault(c => c.S.Engine);
        float TotalWeight => _cars.Sum(c => c.S.Weight);
        public bool Drivable => HasEngine;

        void PlaceCar(Car c, float sctr)
        {
            _roads.EvaluateAlong(_road, sctr + BogieHalf, out var pf, out var tf);
            _roads.EvaluateAlong(_road, sctr - BogieHalf, out var pb, out var tb);
            Vector3 ctr = (pf + pb) * 0.5f + Vector3.Up * (RailY + c.S.YOff);
            Vector3 fwd = pf - pb; fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
            c.Body.GlobalTransform = new Transform3D(Basis.Identity, ctr).LookingAt(ctr + fwd, Vector3.Up);
            Vector3 cf = pf + Vector3.Up * (RailY - 0.4f);
            c.Bf.GlobalTransform = new Transform3D(Basis.Identity, cf).LookingAt(cf + tf, Vector3.Up);
            Vector3 cb = pb + Vector3.Up * (RailY - 0.4f);
            c.Bb.GlobalTransform = new Transform3D(Basis.Identity, cb).LookingAt(cb + tb, Vector3.Up);
        }
        void Place() { foreach (var c in _cars) PlaceCar(c, _s - c.Off); PlaceRopes(); }

        public Node3D Loco => EngineCar?.Body;
        public Transform3D DriverEyeWorld
        {
            get { var l = Loco; return l != null ? l.GetGlobalTransformInterpolated() * new Transform3D(Basis.Identity, new Vector3(0f, 2.3f, -2.6f)) : GlobalTransform; }
        }

        public void Drive(float throttle, float dt)
        {
            if (!HasEngine || _cars.Count == 0) return;
            float wf = RefWeight / Mathf.Max(1f, TotalWeight);   // heavier consist -> proportionally weaker accel + brake (master)
            float target = Mathf.Clamp(throttle, -0.6f, 1f) * MaxSpeed;
            float rate;
            if (Mathf.Abs(throttle) < 0.05f) rate = BaseDecel * wf;
            else if (_speed != 0f && Mathf.Sign(throttle) != Mathf.Sign(_speed)) rate = BaseBrake * wf;
            else rate = BaseAccel * wf;
            _speed = Mathf.MoveToward(_speed, target, rate * dt);
            _s += _speed * dt;
            ClampS();
            Place();
            UpdateAudio(throttle, dt);
            TryCouple();
        }

        void ClampS()
        {
            if (_roads.RoadLoops(_road)) return;
            float total = _roads.RoadLength(_road);
            float lo = _cars.Last().Off + _cars.Last().S.HalfLen;   // rear car's rear end >= 0
            float hi = Mathf.Max(lo, total - _cars[0].S.HalfLen);   // lead car's front end <= total
            if (_s < lo) { _s = lo; _speed = 0f; }
            if (_s > hi) { _s = hi; _speed = 0f; }
        }

        // ---- coupling: drive an engine consist into another car at low speed -> they link into one ----
        void TryCouple()
        {
            if (_speed == 0f || Mathf.Abs(_speed) > CoupleMaxSpeed) return;
            _noRecouple.RemoveWhere(t => !IsInstanceValid(t) || t._cars.Count == 0);
            float myFront = _s + _cars[0].S.HalfLen;
            float myRear = _s - _cars.Last().Off - _cars.Last().S.HalfLen;
            foreach (var node in GetTree().GetNodesInGroup("trains"))
            {
                if (node is not Train o || o == this || !IsInstanceValid(o) || o._cars.Count == 0 || o._road != _road) continue;
                float oFront = o._s + o._cars[0].S.HalfLen;
                float oRear = o._s - o._cars.Last().Off - o._cars.Last().S.HalfLen;
                float gap = Mathf.Min(Mathf.Abs(myFront - oRear), Mathf.Abs(myRear - oFront));
                if (_noRecouple.Contains(o))
                {
                    if (gap > CoupleRange + SeparateMargin) { _noRecouple.Remove(o); o._noRecouple.Remove(this); }   // parted far enough -> re-coupling allowed again
                    continue;   // still hugging the car we just split from -> do not re-grab it
                }
                if (gap < CoupleRange) { Couple(o); return; }
            }
        }

        void Couple(Train o)
        {
            o.TeardownAudio();   // if the absorbed consist had its own engine audio, silence it -> the merged consist rebuilds ONE set below
            var all = new List<Car>();
            foreach (var c in _cars) { c.AbsS = _s - c.Off; all.Add(c); }
            foreach (var c in o._cars) { c.AbsS = o._s - c.Off; c.Body.Reparent(this, true); c.Bf.Reparent(this, true); c.Bb.Reparent(this, true); all.Add(c); }
            all.Sort((a, b) => b.AbsS.CompareTo(a.AbsS));   // front (highest rail distance) first
            _s = all[0].AbsS;                               // lead stays put; the rest snap to coupler spacing behind it
            _cars.Clear(); _cars.AddRange(all);
            o._cars.Clear();
            foreach (var c in _cars) c.Off = _s - c.AbsS;   // link at CURRENT positions -> coupling never pulls the loose stock in (master)
            RebuildRopes();
            RebuildAudio();
            Place();
            ResetPhysicsInterpolation();
            if (IsInstanceValid(o)) o.QueueFree();
        }

        // ---- audio: only a consist WITH an engine hums; parented to the engine car so it's 3D-positional ----
        void RebuildAudio()
        {
            var eng = EngineCar;
            if (eng == null) { TeardownAudio(); return; }
            if (_engineSnd != null && IsInstanceValid(_engineSnd) && _engineSnd.GetParent() == eng.Body) return;   // already on the right car -> no restart blip
            TeardownAudio();
            var loco = eng.Body;
            var e = PlayerController.LoadWavOneShot("res://content/train_engine.wav", loop: true);
            if (e != null) { _engineSnd = new AudioStreamPlayer3D { Stream = e, VolumeDb = -6f, UnitSize = 12f, MaxDistance = 80f, PitchScale = 0.8f }; loco.AddChild(_engineSnd); }   // base: plays only while OCCUPIED (SetOccupied)
            var a = PlayerController.LoadWavOneShot("res://content/train_engine_add.wav", loop: true);
            if (a != null) { _addSnd = new AudioStreamPlayer3D { Stream = a, VolumeDb = -80f, UnitSize = 12f, MaxDistance = 80f, PitchScale = 0.85f }; loco.AddChild(_addSnd); }   // rev layer: volume rides MOTION
            var h = PlayerController.LoadWavOneShot("res://content/train_horn.wav", loop: false);
            if (h != null) { _hornSnd = new AudioStreamPlayer3D { Stream = h, VolumeDb = 6f, UnitSize = 18f, MaxDistance = 140f }; loco.AddChild(_hornSnd); }   // ONE-SHOT press-to-honk, loud
            if (_occupied) SetOccupied(true);   // rebuilt while aboard (e.g. after coupling) -> resume the loops
        }
        void TeardownAudio()
        {
            foreach (var p in new[] { _engineSnd, _addSnd, _hornSnd }) if (p != null && IsInstanceValid(p)) p.QueueFree();
            _engineSnd = _addSnd = _hornSnd = null;
        }
        void UpdateAudio(float throttle, float dt)
        {
            float sp = MaxSpeed > 0f ? Mathf.Abs(_speed) / MaxSpeed : 0f;
            if (_engineSnd != null) _engineSnd.PitchScale = 0.8f + 0.7f * sp;
            if (_addSnd != null)
            {
                _addSnd.PitchScale = 0.85f + 0.95f * sp;
                float mv = Mathf.Abs(_speed);
                float target = mv > 0.2f ? Mathf.Lerp(-8f, -2f, Mathf.Clamp(mv / 15f, 0f, 1f)) : -80f;   // audible in ANY motion, louder the faster (master)
                _addSnd.VolumeDb = Mathf.MoveToward(_addSnd.VolumeDb, target, 120f * dt);
            }
        }
        /// <summary>Someone boarded/left the engine. Base engine loop + rev layer run only while occupied.</summary>
        public void SetOccupied(bool on)
        {
            _occupied = on;
            if (_engineSnd != null) { if (on) { if (!_engineSnd.Playing) _engineSnd.Play(); } else _engineSnd.Stop(); }
            if (_addSnd != null) { if (on) { if (!_addSnd.Playing) _addSnd.Play(); _addSnd.VolumeDb = -80f; } else _addSnd.Stop(); }
            if (!on && _hornSnd != null && _hornSnd.Playing) _hornSnd.Stop();
        }
        /// <summary>One honk per LMB press (restarts the clip; it is a one-shot, not a loop). (master)</summary>
        public void Honk() { _hornSnd?.Play(); }

        // ---- look-focus outline of the ENGINE car (only a drivable consist is boardable) ----
        bool _lookFocused;
        List<MeshInstance3D> _locoMeshes;
        static void CollectMeshes(Node n, List<MeshInstance3D> outl) { if (n is MeshInstance3D mi) outl.Add(mi); foreach (var c in n.GetChildren()) CollectMeshes(c, outl); }

        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (on)
            {
                _locoMeshes = new List<MeshInstance3D>();
                var eng = EngineCar;
                if (eng != null) { CollectMeshes(eng.Body, _locoMeshes); if (eng.Bf != null) _locoMeshes.Add(eng.Bf); if (eng.Bb != null) _locoMeshes.Add(eng.Bb); }
            }
            if (_locoMeshes != null)
                foreach (var mi in _locoMeshes)
                    if (IsInstanceValid(mi)) mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = new Color(0.55f, 0.8f, 1f);
        }

        public bool LookRayHitsLoco(Vector3 from, Vector3 to)
        {
            var eng = EngineCar;
            if (eng == null || !IsInstanceValid(eng.Body)) return false;
            var inv = eng.Body.GlobalTransform.AffineInverse();
            var size = eng.S.Box;
            return new Aabb(eng.S.BoxCtr - size * 0.5f, size).IntersectsSegment(inv * from, inv * to);
        }

        // ---- coupler ropes + uncoupling (phase 2) ----
        void RebuildRopes()
        {
            foreach (var r in _ropes) if (IsInstanceValid(r)) r.QueueFree();
            _ropes.Clear();
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.14f, 0.11f, 0.08f), Roughness = 1f };
            for (int i = 0; i < _cars.Count - 1; i++)
            {
                var rope = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.055f, BottomRadius = 0.055f, Height = 1f, RadialSegments = 6, Rings = 0 }, MaterialOverride = mat };
                AddChild(rope);
                _ropes.Add(rope);
            }
        }

        void PlaceRopes()
        {
            for (int i = 0; i < _ropes.Count && i + 1 < _cars.Count; i++)
            {
                float sRear = (_s - _cars[i].Off) - _cars[i].S.HalfLen;
                float sFront = (_s - _cars[i + 1].Off) + _cars[i + 1].S.HalfLen;
                _roads.EvaluateAlong(_road, sRear, out var pa, out _);
                _roads.EvaluateAlong(_road, sFront, out var pb, out _);
                PositionRope(_ropes[i], pa + Vector3.Up * (RailY + 0.05f), pb + Vector3.Up * (RailY + 0.05f));
            }
        }

        static void PositionRope(MeshInstance3D rope, Vector3 a, Vector3 b)
        {
            if (!IsInstanceValid(rope)) return;
            Vector3 mid = (a + b) * 0.5f, d = b - a; float len = d.Length();
            if (len < 1e-3f) { rope.Visible = false; return; }
            rope.Visible = true;
            Vector3 y = d / len, x = y.Cross(Vector3.Up);
            if (x.LengthSquared() < 1e-4f) x = Vector3.Right;
            x = x.Normalized(); Vector3 z = x.Cross(y).Normalized();
            rope.GlobalTransform = new Transform3D(new Basis(x, y, z).Scaled(new Vector3(1f, len, 1f)), mid);
        }

        public int CouplerCount => Mathf.Max(0, _cars.Count - 1);
        public Vector3 CouplerWorld(int i) => (i >= 0 && i < _ropes.Count && IsInstanceValid(_ropes[i])) ? _ropes[i].GlobalPosition : GlobalPosition;
        public void SetCouplerFocused(int i, bool on)
        {
            if (i < 0 || i >= _ropes.Count || !IsInstanceValid(_ropes[i])) return;
            var r = _ropes[i];
            r.Layers = on ? (r.Layers | OutlineOverlay.OutlineLayer) : (r.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = new Color(1f, 0.55f, 0.15f);
        }

        /// <summary>Split the consist at coupler i (between car i and i+1): the cars behind break off into their own
        /// Train, keeping position + momentum. On foot only (F on the coupler rope).</summary>
        public void Uncouple(int i)
        {
            if (i < 0 || i >= _cars.Count - 1) return;
            int n = _cars.Count - (i + 1);
            var rear = _cars.GetRange(i + 1, n);
            float leadOff = rear[0].Off;   // rear lead's offset from THIS lead -- rebase the rear offsets by it so positions are UNCHANGED (no pull)
            _cars.RemoveRange(i + 1, n);
            var nt = new Train { _roads = _roads, _road = _road, _speed = _speed };
            (GetParent() ?? GetTree().Root).AddChild(nt);
            nt.AddToGroup("trains");
            foreach (var c in rear) { c.Body.Reparent(nt, true); c.Bf.Reparent(nt, true); c.Bb.Reparent(nt, true); c.Off -= leadOff; nt._cars.Add(c); }
            nt._s = _s - leadOff;
            nt.RebuildRopes(); nt.RebuildAudio(); nt.Place(); nt.ResetPhysicsInterpolation();
            _noRecouple.Add(nt); nt._noRecouple.Add(this);   // neither half re-couples to the other until they've driven apart (master: condition, not a timer)
            RebuildRopes(); RebuildAudio(); Place();   // this keeps its offsets (the front cars never moved)
        }
    }
}
