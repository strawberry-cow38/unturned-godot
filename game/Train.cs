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
            public readonly List<(CpuParticles3D ps, float z)> Sparks = new();   // per-wheel brake-spark emitter + its bogie-local Z (axle tag)
            public AudioStreamPlayer3D EngSnd, AddSnd, HornSnd;   // per-ENGINE-car audio (each engine sounds); null on non-engine cars
            public float Off;    // centre offset BEHIND the consist lead (_s); recomputed on couple/uncouple
            public float AbsS;   // scratch: absolute rail distance, used when merging two consists
        }

        RoadField _roads;
        int _road;
        float _s;       // rail distance of the LEAD car's centre (_cars[0])
        float _speed;
        float _prevS, _currS; bool _visReady;   // render-frame CURVE interp: sample the real rail curve each frame, not Godot's linear-between-50Hz-ticks (kills corner jitter at speed, master)
        readonly HashSet<Train> _noRecouple = new();   // consists just split from this one: can't re-couple until they've SEPARATED (condition-based, master)
        const float RailY = 1.55f, BogieHalf = 3.5f, CoupleGap = 0.9f;
        const float WheelRadius = 0.6f;   // extracted wheel radius; wheels roll without slip
        static readonly Vector3[] WheelOff = { new Vector3(1.47f, -0.32f, 0.94f), new Vector3(-1.47f, -0.32f, 0.94f), new Vector3(1.47f, -0.32f, -0.94f), new Vector3(-1.47f, -0.32f, -0.94f) };
        float _spinAngle;   // shared wheel roll angle (rad), advanced by distance travelled
        const float BrakeSparkDelay = 1f;   // sustained braking this long (s) -> sparks (master, time-based not decel-based)
        float _brakeTime;
        const float MaxSpeed = 48f, BaseAccel = 2f, BaseDecel = 1.2f, BaseBrake = 12f, RefWeight = 20f;
        const float CoupleRange = 1.3f, CoupleMaxSpeed = 11f, SeparateMargin = 1.5f, RollFriction = 3f;   // couple only when ends CLOSE + <=CoupleMaxSpeed; a FASTER hit bonks the car along the rail; passive cars coast + decay (master)
        const float SeparateSpeed = 5f, StuckGap = 0.4f;   // fixer: un-stick overlapping consists at this rate, to this gap (master)
        readonly List<Car> _cars = new();
        readonly List<MeshInstance3D> _ropes = new();   // short coupler rope per gap (also the look-at/F-uncouple target)
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
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;   // our _Process samples the rail curve each frame -> no linear corner-cutting between ticks
            AddCar(type);
            RecomputeOffsets();
            RebuildRopes();
            Place();
            ResetPhysicsInterpolation();
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
            foreach (var bogie in new[] { bf, bb })
                foreach (var off in WheelOff)
                {
                    var ps = MakeBrakeSparks();
                    ps.Position = new Vector3(off.X, off.Y - WheelRadius, off.Z);   // the wheel's rail-contact point
                    bogie.AddChild(ps); car.Sparks.Add((ps, off.Z));
                }
            if (s.Engine) SetupCarAudio(car);
            _cars.Add(car);
        }

        // Lead car has offset 0; each following car sits its own half + a coupler gap + the previous car's half behind.
        void RecomputeOffsets()
        {
            for (int i = 0; i < _cars.Count; i++)
                _cars[i].Off = i == 0 ? 0f : _cars[i - 1].Off + _cars[i - 1].S.HalfLen + CoupleGap + _cars[i].S.HalfLen;
        }

        bool HasEngine => _cars.Any(c => c.S.Engine);
        int EngineCount => _cars.Count(c => c.S.Engine);
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
        void Place() => PlaceAt(_s);
        void PlaceAt(float s) { foreach (var c in _cars) PlaceCar(c, s - c.Off); PlaceRopesAt(s); SpinTo(s); }
        void SnapVis() { _prevS = _currS = _s; _visReady = true; }
        void AdvanceS(float ds) { _prevS = _visReady ? _currS : _s; _s += ds; ClampS(); _currS = _s; _visReady = true; }
        public override void _Process(double delta)
        {
            if (_visReady && _cars.Count > 0)
                PlaceAt(Mathf.Lerp(_prevS, _currS, Mathf.Clamp((float)Engine.GetPhysicsInterpolationFraction(), 0f, 1f)));
        }

        // Roll every wheel by the distance travelled (roll without slip). Wheels are children of the bogies, so
        // PlaceCar re-seats the bogie each tick but leaves the wheel's LOCAL spin intact; each turns about its own
        // axle (local X). (master: split the wheels + rotate each separately)
        void SpinTo(float s)
        {
            _spinAngle = -s / WheelRadius;   // absolute: wheel roll proportional to distance along the rail (roll without slip; negated dir, master)
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
                AlbedoColor = new Color(1f, 0.62f, 0.18f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            var fade = new Curve(); fade.AddPoint(new Vector2(0f, 1f)); fade.AddPoint(new Vector2(1f, 0f));
            return new CpuParticles3D
            {
                Emitting = false, Visible = false, OneShot = false, Amount = 22, Lifetime = 0.4f, Randomness = 0.5f,
                Direction = new Vector3(0f, 1f, -1f).Normalized(), Spread = 18f,
                InitialVelocityMin = 4f, InitialVelocityMax = 9f,
                Gravity = new Vector3(0f, -16f, 0f),
                ScaleAmountMin = 0.03f, ScaleAmountMax = 0.07f, ScaleAmountCurve = fade,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Point,   // a POINT at one wheel's rail contact (Position set per-wheel in AddCar)
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
                VisibilityAabb = new Aabb(new Vector3(-6f, -6f, -6f), new Vector3(12f, 12f, 12f)),
            };
        }

        void SetBrakeSparks(bool on, float speed)
        {
            float fwdZ = speed >= 0f ? 1f : -1f;                  // leading axle + spark dir along travel (bogie orientation, matches the spin flip); flips on reverse
            var dir = new Vector3(0f, 1f, fwdZ).Normalized();    // launch UP at 45deg in the braking/travel direction (master)
            foreach (var c in _cars)
                foreach (var (ps, z) in c.Sparks)
                    if (IsInstanceValid(ps))
                    {
                        bool lead = on && Mathf.Sign(z) == fwdZ;   // ONLY the leading axle's 2 wheels emit
                        if (lead) { ps.Direction = dir; ps.Visible = true; }
                        ps.Emitting = lead;
                    }
        }

        public Node3D Loco => EngineCar?.Body;
        public Transform3D DriverEyeWorld
        {
            get { var l = (_boardedCar != null && IsInstanceValid(_boardedCar.Body)) ? _boardedCar.Body : Loco; return l != null ? l.GlobalTransform * new Transform3D(Basis.Identity, new Vector3(0f, 2.3f, -2.6f)) : GlobalTransform; }
        }

        public void Drive(float throttle, float dt)
        {
            if (!HasEngine || _cars.Count == 0) return;
            float wf = (EngineCount * RefWeight) / Mathf.Max(1f, TotalWeight);   // COMBINED engine power / total weight -> more engines pull more (master)
            float target = Mathf.Clamp(throttle, -0.6f, 1f) * MaxSpeed;
            float rate; bool braking = false;
            if (Mathf.Abs(throttle) < 0.05f) rate = BaseDecel * wf;
            else if (_speed != 0f && Mathf.Sign(throttle) != Mathf.Sign(_speed)) { rate = BaseBrake * wf; braking = true; }
            else rate = BaseAccel * wf;
            _brakeTime = (braking && Mathf.Abs(_speed) > 3f) ? _brakeTime + dt : 0f;   // sustained hold builds up; any release/accel resets it
            bool hardBrake = _brakeTime > BrakeSparkDelay;   // sparks kick in after ~1s of held braking (master)
            _speed = Mathf.MoveToward(_speed, target, rate * dt);
            AdvanceS(_speed * dt);
            Place();
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
                // Contact happens at the FIXED ATTACH DISTANCE (CoupleGap), never body-touch: cars couple AT that
                // distance and can't get below it -- anything closer is pushed back out to it (master).
                float penA = myFront - oRear, penB = oFront - myRear;
                bool behind = penA <= penB;
                float pen = behind ? penA : penB;          // >0 = bodies overlap; the body gap is -pen
                float bodyGap = -pen;
                bool suppressed = _noRecouple.Contains(o);
                if (bodyGap > CoupleGap)                    // still farther apart than the coupler reach -> no contact yet
                {
                    if (suppressed && bodyGap > CoupleGap + SeparateMargin) { _noRecouple.Remove(o); o._noRecouple.Remove(this); }
                    continue;
                }
                // AT/within the fixed attach distance. Decide by CLOSING (relative) speed, not absolute.
                float closing = behind ? (_speed - o._speed) : (o._speed - _speed);
                if (!suppressed && closing > 0.15f && closing <= CoupleMaxSpeed) { Couple(o); return; }   // reached it slowly -> COUPLE (locks to CoupleGap)
                // else (too fast / suppressed / just resting): hold them at exactly the attach distance, collide if fast
                float sep = pen + CoupleGap;                // >=0: push apart until bodyGap == CoupleGap
                if (closing > CoupleMaxSpeed)               // fast slam -> mass-weighted momentum exchange, launches the car
                {
                    float m1 = TotalWeight, m2 = o.TotalWeight, mt = Mathf.Max(0.01f, m1 + m2);
                    float u1 = _speed, u2 = o._speed; const float e = 0.35f;
                    _speed   = (m1 * u1 + m2 * u2 - m2 * e * (u1 - u2)) / mt;
                    o._speed = (m1 * u1 + m2 * u2 + m1 * e * (u1 - u2)) / mt;
                }
                if (sep > 1e-4f) { if (behind) _s -= sep; else _s += sep; }
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
            if (_occupied || _cars.Count == 0 || Mathf.Abs(_speed) < 0.02f) return;   // occupied -> Drive owns it; empty engine consist coasts (keeps momentum on exit, master)
            _speed = Mathf.MoveToward(_speed, 0f, RollFriction * dt);
            AdvanceS(_speed * dt);
            Place();
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
                Place(); SnapVis();
                return;   // one unstick per tick
            }
        }

        void Couple(Train o)
        {
            var all = new List<Car>();
            foreach (var c in _cars) { c.AbsS = _s - c.Off; all.Add(c); }
            foreach (var c in o._cars) { c.AbsS = o._s - c.Off; c.Body.Reparent(this, true); c.Bf.Reparent(this, true); c.Bb.Reparent(this, true); all.Add(c); }
            all.Sort((a, b) => b.AbsS.CompareTo(a.AbsS));   // front (highest rail distance) first
            _s = all[0].AbsS;                               // lead stays put; the rest snap to coupler spacing behind it
            _cars.Clear(); _cars.AddRange(all);
            o._cars.Clear();
            RecomputeOffsets();   // lock to the fixed CoupleGap spacing -- they couple AT the attach distance so this is exact, not a pull (master)
            RebuildRopes();
            Place();
            SnapVis();
            SetOccupied(_occupied);   // newly-joined engine cars keep their own audio through the reparent -> start their loops if occupied
            ResetPhysicsInterpolation();
            if (IsInstanceValid(o)) o.QueueFree();
        }

        // ---- audio: only a consist WITH an engine hums; parented to the engine car so it's 3D-positional ----
        // Each ENGINE car owns its own audio (parented to its Body, so it follows the car through couple/uncouple).
        // Multiple engines in a consist therefore all sound at once. (master)
        void SetupCarAudio(Car c)
        {
            var e = PlayerController.LoadWavOneShot("res://content/train_engine.wav", loop: true);
            if (e != null) { c.EngSnd = new AudioStreamPlayer3D { Stream = e, VolumeDb = 3f, UnitSize = 16f, MaxDistance = 100f, PitchScale = 0.8f }; c.Body.AddChild(c.EngSnd); }
            var a = PlayerController.LoadWavOneShot("res://content/train_engine_add.wav", loop: true);
            if (a != null) { c.AddSnd = new AudioStreamPlayer3D { Stream = a, VolumeDb = -80f, UnitSize = 12f, MaxDistance = 80f, PitchScale = 0.85f }; c.Body.AddChild(c.AddSnd); }
            var h = PlayerController.LoadWavOneShot("res://content/train_horn.wav", loop: false);
            if (h != null) { c.HornSnd = new AudioStreamPlayer3D { Stream = h, VolumeDb = 13f, UnitSize = 22f, MaxDistance = 170f }; c.Body.AddChild(c.HornSnd); }
        }
        void UpdateAudio(float throttle, float dt)
        {
            float sp = MaxSpeed > 0f ? Mathf.Abs(_speed) / MaxSpeed : 0f;
            float mv = Mathf.Abs(_speed);
            float target = mv > 0.2f ? Mathf.Lerp(-2f, 5f, Mathf.Clamp(mv / 15f, 0f, 1f)) : -80f;   // rev layer audible in ANY motion
            foreach (var c in _cars)
            {
                if (c.EngSnd != null) c.EngSnd.PitchScale = 0.8f + 0.7f * sp;
                if (c.AddSnd != null) { c.AddSnd.PitchScale = 0.85f + 0.95f * sp; c.AddSnd.VolumeDb = Mathf.MoveToward(c.AddSnd.VolumeDb, target, 120f * dt); }
            }
        }
        /// <summary>Boarded/left: EVERY engine car's base loop + rev layer run only while occupied. (master)</summary>
        public void SetOccupied(bool on)
        {
            _occupied = on;
            if (!on) _boardedCar = null;
            foreach (var c in _cars)
            {
                if (c.EngSnd != null) { if (on) { if (!c.EngSnd.Playing) c.EngSnd.Play(); } else c.EngSnd.Stop(); }
                if (c.AddSnd != null) { if (on) { if (!c.AddSnd.Playing) c.AddSnd.Play(); c.AddSnd.VolumeDb = -80f; } else c.AddSnd.Stop(); }
                if (!on && c.HornSnd != null && c.HornSnd.Playing) c.HornSnd.Stop();
            }
        }
        /// <summary>One honk per LMB press from EVERY engine car (one-shot, not a loop). (master)</summary>
        public void Honk() { foreach (var c in _cars) if (c.HornSnd != null) c.HornSnd.Play(); }

        // ---- look-focus outline of the ENGINE car (only a drivable consist is boardable) ----
        bool _lookFocused;
        Car _lookEngine, _boardedCar, _outlinedEngine;   // engine the player is LOOKING at / IS IN / whose outline is lit -- board ANY engine (master)
        static void CollectMeshes(Node n, List<MeshInstance3D> outl) { if (n is MeshInstance3D mi) outl.Add(mi); foreach (var c in n.GetChildren()) CollectMeshes(c, outl); }

        // nearest ENGINE car whose hull box the look-ray passes through (so any engine is boardable, not just the first)
        Car EngineHit(Vector3 from, Vector3 to)
        {
            Car best = null; float bestD = float.MaxValue;
            foreach (var c in _cars)
            {
                if (!c.S.Engine || !IsInstanceValid(c.Body)) continue;
                var inv = c.Body.GlobalTransform.AffineInverse();
                var size = c.S.Box;
                if (new Aabb(c.S.BoxCtr - size * 0.5f, size).IntersectsSegment(inv * from, inv * to))
                {
                    float d = c.Body.GlobalPosition.DistanceSquaredTo(from);
                    if (d < bestD) { bestD = d; best = c; }
                }
            }
            return best;
        }

        void OutlineEngine(Car c, bool on)
        {
            if (c == null) return;
            var meshes = new List<MeshInstance3D>();
            if (IsInstanceValid(c.Body)) CollectMeshes(c.Body, meshes);
            if (c.Bf != null) meshes.Add(c.Bf);
            if (c.Bb != null) meshes.Add(c.Bb);
            foreach (var mi in meshes) if (IsInstanceValid(mi)) mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
        }

        /// <summary>Remember which engine the player is in (the one they looked at) -> the driver cam sits in ITS cab.</summary>
        public void MarkBoarded() { _boardedCar = _lookEngine ?? EngineCar; }

        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (on) { _outlinedEngine = _lookEngine ?? EngineCar; OutlineEngine(_outlinedEngine, true); WorldItem.FocusColor = new Color(0.55f, 0.8f, 1f); }
            else { OutlineEngine(_outlinedEngine, false); _outlinedEngine = null; }
        }

        public bool LookRayHitsLoco(Vector3 from, Vector3 to)
        {
            var hit = EngineHit(from, to);
            if (_lookFocused && hit != _outlinedEngine) { OutlineEngine(_outlinedEngine, false); OutlineEngine(hit, true); _outlinedEngine = hit; }   // move the outline as you look between engines
            _lookEngine = hit;
            return hit != null;
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

        void PlaceRopesAt(float s)
        {
            for (int i = 0; i < _ropes.Count && i + 1 < _cars.Count; i++)
            {
                float sRear = (s - _cars[i].Off) - _cars[i].S.HalfLen;
                float sFront = (s - _cars[i + 1].Off) + _cars[i + 1].S.HalfLen;
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
            nt.RebuildRopes(); nt.Place(); nt.SnapVis(); nt.ResetPhysicsInterpolation();
            _noRecouple.Add(nt); nt._noRecouple.Add(this);   // neither half re-couples to the other until they've driven apart (master: condition, not a timer)
            RebuildRopes(); Place(); SnapVis();   // this keeps its offsets (the front cars never moved)
        }
    }
}
