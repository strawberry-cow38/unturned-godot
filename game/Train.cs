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
        class Spec { public string Mesh, Tex; public float Weight, HalfLen, YOff; public Vector3 Box, BoxCtr; public bool Engine, Livery, FlipY; public Color? Spawn; }
        static readonly Dictionary<string, Spec> Specs = new()
        {
            ["engine"]  = new Spec { Mesh = "train_body",   Tex = "train_body_tex",   Weight = 20f, HalfLen = 5.34f, YOff = 0f,    Box = new Vector3(3.4f, 4.1f, 10.8f),  BoxCtr = new Vector3(0f, 1.27f, 0f),  Engine = true,  Livery = true  },
            ["flatbed"] = new Spec { Mesh = "train_car",    Tex = "train_car_tex",    Weight = 8f,  HalfLen = 5.34f, YOff = 0f,    Box = new Vector3(3.4f, 1.8f, 10.8f),  BoxCtr = new Vector3(0f, 0.13f, 0f),  Engine = false, Livery = false },
            ["boxcar"]  = new Spec { Mesh = "train_boxcar", Tex = "train_boxcar_tex", Weight = 14f, HalfLen = 5.25f, YOff = 0f, FlipY = true, Box = new Vector3(3.6f, 4.75f, 10.5f), BoxCtr = new Vector3(0f, 1.62f, 0f), Engine = false, Livery = true  },
            ["tanker"]  = new Spec { Mesh = "train_tanker", Tex = "train_tanker_tex", Weight = 16f, HalfLen = 5.34f, YOff = 0f, FlipY = true, Box = new Vector3(3.4f, 4.1f, 10.7f),  BoxCtr = new Vector3(0f, 1.29f, 0f), Engine = false, Livery = true, Spawn = new Color(0.85f, 0.85f, 0.85f)  },
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
            public readonly List<MeshInstance3D> Wheels = new();   // 8 per car (4 per bogie); each spins about its axle
            public CpuParticles3D SparkF, SparkB;   // hard-brake sparks from each bogie's wheel-contact line
            public float Off;    // centre offset BEHIND the consist lead (_s); recomputed on couple/uncouple
            public float AbsS;   // scratch: absolute rail distance, used when merging two consists
        }

        RoadField _roads;
        int _road;
        float _s;       // rail distance of the LEAD car's centre (_cars[0])
        float _speed;
        readonly HashSet<Train> _noRecouple = new();   // consists just split from this one: can't re-couple until they've SEPARATED (condition-based, master)
        const float RailY = 1.55f, BogieHalf = 3.5f, CoupleGap = 0.9f;
        const float WheelRadius = 0.6f;   // extracted wheel radius; wheels roll without slip
        static readonly Vector3[] WheelOff = { new Vector3(1.47f, -0.32f, 0.94f), new Vector3(-1.47f, -0.32f, 0.94f), new Vector3(1.47f, -0.32f, -0.94f), new Vector3(-1.47f, -0.32f, -0.94f) };
        float _spinAngle;   // shared wheel roll angle (rad), advanced by distance travelled
        const float MaxSpeed = 48f, BaseAccel = 2f, BaseDecel = 1.2f, BaseBrake = 12f, RefWeight = 20f;
        const float CoupleRange = 1.3f, CoupleMaxSpeed = 11f, SeparateMargin = 1.5f, RollFriction = 3f;   // couple only when ends CLOSE + <=CoupleMaxSpeed; a FASTER hit bonks the car along the rail; passive cars coast + decay (master)
        const float SeparateSpeed = 5f, StuckGap = 0.4f;   // fixer: un-stick overlapping consists at this rate, to this gap (master)
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
            Color? body = s.Livery ? (s.Spawn ?? livery) : (Color?)null;   // tanker spawns a FIXED white; the recolor path stays live for a future paint system (master)
            var mat = MakeMat(s.Tex, body);
            var sb = new StaticBody3D();
            var mi = new MeshInstance3D { Mesh = Lm(s.Mesh), MaterialOverride = mat };
            if (s.FlipY) mi.RotationDegrees = new Vector3(0f, 0f, 180f);   // boxcar/tanker meshes are authored upside-down -> flip upright (master)
            sb.AddChild(mi);
            sb.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = s.Box }, Position = s.BoxCtr });
            var frameMesh = Lm("train_bogie_frame"); var wheelMesh = Lm("train_wheel");
            var bf = new MeshInstance3D { Mesh = frameMesh, MaterialOverride = bogieMat };
            var bb = new MeshInstance3D { Mesh = frameMesh, MaterialOverride = bogieMat };
            AddChild(sb); AddChild(bf); AddChild(bb);
            var car = new Car { Type = type, S = s, Body = sb, Bf = bf, Bb = bb };
            foreach (var bogie in new[] { bf, bb })          // each bogie: the frame plate + its 4 wheels as spinnable children
                foreach (var off in WheelOff)
                {
                    var w = new MeshInstance3D { Mesh = wheelMesh, MaterialOverride = bogieMat, Position = off };
                    bogie.AddChild(w);
                    car.Wheels.Add(w);
                }
            car.SparkF = MakeBrakeSparks(); bf.AddChild(car.SparkF);
            car.SparkB = MakeBrakeSparks(); bb.AddChild(car.SparkB);
            _cars.Add(car);
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

        // Roll every wheel by the distance travelled (roll without slip). Wheels are children of the bogies, so
        // PlaceCar re-seats the bogie each tick but leaves the wheel's LOCAL spin intact; each turns about its own
        // axle (local X). (master: split the wheels + rotate each separately)
        void SpinWheels(float dist)
        {
            if (Mathf.Abs(dist) < 1e-5f) return;
            _spinAngle -= dist / WheelRadius;   // negated: rolls the correct way (master)
            foreach (var c in _cars)
                foreach (var w in c.Wheels)
                    if (IsInstanceValid(w)) w.Rotation = new Vector3(_spinAngle, 0f, 0f);
        }

        // Metal-impact brake sparks: a continuous hot-orange emitter along each bogie's wheel-contact line, off by
        // default, switched on only under HARD braking (master). Additive glowing quads that fly in the travel
        // direction + down and fade fast.
        static CpuParticles3D MakeBrakeSparks()
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                AlbedoColor = new Color(1f, 0.6f, 0.16f),
            };
            var fade = new Curve(); fade.AddPoint(new Vector2(0f, 1f)); fade.AddPoint(new Vector2(1f, 0f));
            return new CpuParticles3D
            {
                Emitting = false, Visible = false, OneShot = false, Amount = 22, Lifetime = 0.4f, Randomness = 0.5f,
                Direction = new Vector3(0f, 1f, -1f).Normalized(), Spread = 18f,
                InitialVelocityMin = 4f, InitialVelocityMax = 9f,
                Gravity = new Vector3(0f, -16f, 0f),
                ScaleAmountMin = 0.03f, ScaleAmountMax = 0.07f, ScaleAmountCurve = fade,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(1.47f, 0.02f, 0.08f),
                Position = new Vector3(0f, -0.92f, 0f),   // the wheel-contact line, bogie-local
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
                VisibilityAabb = new Aabb(new Vector3(-6f, -6f, -6f), new Vector3(12f, 12f, 12f)),
            };
        }

        void SetBrakeSparks(bool on, float speed)
        {
            float fwdZ = speed >= 0f ? -1f : 1f;                 // bogie -Z = +s (forward); the emitting axle + dir flip with reverse
            var dir = new Vector3(0f, 1f, fwdZ).Normalized();    // launch UP at 45deg in the braking/travel direction (master)
            var pos = new Vector3(0f, -0.92f, 0.94f * fwdZ);     // the LEADING axle's 2 wheels (front pair in the travel direction)
            foreach (var c in _cars)
                foreach (var sp in new[] { c.SparkF, c.SparkB })
                    if (IsInstanceValid(sp)) { if (on) { sp.Direction = dir; sp.Position = pos; sp.Visible = true; } sp.Emitting = on; }
        }

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
            float rate; bool hardBrake = false;
            if (Mathf.Abs(throttle) < 0.05f) rate = BaseDecel * wf;
            else if (_speed != 0f && Mathf.Sign(throttle) != Mathf.Sign(_speed)) { rate = BaseBrake * wf; hardBrake = Mathf.Abs(_speed) > 3f; }   // throttle against motion + moving = hard brake -> sparks
            else rate = BaseAccel * wf;
            _speed = Mathf.MoveToward(_speed, target, rate * dt);
            _s += _speed * dt;
            ClampS();
            Place();
            SpinWheels(_speed * dt);
            SetBrakeSparks(hardBrake, _speed);
            UpdateAudio(throttle, dt);
            ResolveContact();
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
        // Consist-vs-consist contact: a slow touch COUPLES, a faster hit BONKS the other car along the rail
        // (momentum transfer + separate to the contact face). Runs from Drive (engine) and the passive coast.
        void ResolveContact()
        {
            if (_cars.Count == 0) return;
            _noRecouple.RemoveWhere(t => !IsInstanceValid(t) || t._cars.Count == 0);
            float myFront = _s + _cars[0].S.HalfLen;
            float myRear = _s - _cars.Last().Off - _cars.Last().S.HalfLen;
            foreach (var node in GetTree().GetNodesInGroup("trains"))
            {
                if (node is not Train o || o == this || !IsInstanceValid(o) || o._cars.Count == 0 || o._road != _road) continue;
                float oFront = o._s + o._cars[0].S.HalfLen;
                float oRear = o._s - o._cars.Last().Off - o._cars.Last().S.HalfLen;
                bool overlap = myFront > oRear && oFront > myRear;
                float gap = Mathf.Min(Mathf.Abs(myFront - oRear), Mathf.Abs(myRear - oFront));
                bool suppressed = _noRecouple.Contains(o);
                if (suppressed && !overlap && gap > CoupleRange + SeparateMargin) { _noRecouple.Remove(o); o._noRecouple.Remove(this); suppressed = false; }
                if (!overlap && gap >= CoupleRange) continue;   // too far apart to interact
                // Decide by CLOSING (relative) speed, not absolute -- else a fast slam bleeds to ~0 on impact and
                // then "counts as slow" and wrongly couples (master). Pick the side I am on + its approach rate.
                float penA = myFront - oRear, penB = oFront - myRear;
                bool behind = penA <= penB;
                float closing = behind ? (_speed - o._speed) : (o._speed - _speed);
                if (closing <= 0.15f) continue;   // parked beside it / separating / just collided -> don't couple, don't re-hit
                if (!suppressed && closing <= CoupleMaxSpeed) { Couple(o); return; }   // slow CLOSING contact -> couple
                if (!overlap) continue;   // fast but not touching yet -> wait until they actually meet
                // Fast CLOSING + overlapping -> COLLIDE. 1D momentum + restitution, MASS-WEIGHTED, so ramming a heavy
                // coupled chain launches the whole chain (less), a light car more; total momentum conserved (master).
                float m1 = TotalWeight, m2 = o.TotalWeight, mt = Mathf.Max(0.01f, m1 + m2);
                float u1 = _speed, u2 = o._speed;
                const float e = 0.35f;   // restitution
                _speed   = (m1 * u1 + m2 * u2 - m2 * e * (u1 - u2)) / mt;
                o._speed = (m1 * u1 + m2 * u2 + m1 * e * (u1 - u2)) / mt;
                if (behind) _s -= penA; else _s += penB;   // separate to the contact face
                o.Place(); Place();
                return;
            }
        }

        // Passive consists (no engine) coast their residual speed with friction here, so a BONKED car rolls along
        // the rail then settles. Engine consists move only via Drive() (or stay put unoccupied) and skip this.
        public override void _PhysicsProcess(double delta)
        {
            float dt = (float)delta;
            SeparateOverlaps(dt);   // fixer: shove stuck-inside-each-other consists apart, even when parked (master)
            if (HasEngine || _cars.Count == 0 || Mathf.Abs(_speed) < 0.02f) return;
            _speed = Mathf.MoveToward(_speed, 0f, RollFriction * dt);
            _s += _speed * dt;
            ClampS();
            Place();
            SpinWheels(_speed * dt);
            ResolveContact();
        }

        // FIXER (master): shove any two SEPARATE consists that are penetrating each other apart along the rail,
        // each pushing only ITSELF away (so both halves move, no cross-instance races). Gated to ~stationary
        // consists so it never fights an active drive-in/bonk (ResolveContact owns those); handles the stuck case
        // where both are parked and overlapping (e.g. two trains spawned on the same spot).
        void SeparateOverlaps(float dt)
        {
            if (_cars.Count == 0 || Mathf.Abs(_speed) > 1f) return;
            float myFront = _s + _cars[0].S.HalfLen;
            float myRear = _s - _cars.Last().Off - _cars.Last().S.HalfLen;
            foreach (var node in GetTree().GetNodesInGroup("trains"))
            {
                if (node is not Train o || o == this || !IsInstanceValid(o) || o._cars.Count == 0 || o._road != _road || Mathf.Abs(o._speed) > 1f) continue;
                float oFront = o._s + o._cars[0].S.HalfLen;
                float oRear = o._s - o._cars.Last().Off - o._cars.Last().S.HalfLen;
                if (!(myFront > oRear && oFront > myRear)) continue;   // no penetration
                float pushFwd = myFront - oRear + StuckGap;    // I'm behind o -> back off (-s) to clear + a small gap
                float pushBack = oFront - myRear + StuckGap;   // I'm ahead of o -> move forward (+s)
                float step = SeparateSpeed * dt;
                if (pushFwd <= pushBack) _s -= Mathf.Min(pushFwd, step);
                else _s += Mathf.Min(pushBack, step);
                Place();
                return;   // one unstick per tick
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
