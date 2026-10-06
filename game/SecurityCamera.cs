using Godot;

namespace UnturnedGodot
{
    /// <summary>A wired CCTV camera: a powered device that films, and puts its picture out as a DATA stream
    /// (strawberry 2026-09-13: "a cctv camera will have a data output. while powered, it puts out a video data
    /// stream, which is displayed on any powered data reciever").
    ///
    /// It is built on the HOUSING half of the split Camera_0 prop (see ObjMesh.SplitCameraArm) rather than on
    /// the whole model, because the half that carries the lens is the half whose transform means "where this
    /// camera is looking" -- the arm is a bracket bolted to a wall and points nowhere in particular.
    ///
    /// TWO PORTS, and the asymmetry is the point: a CONSUMER that takes real watts, and a DATA OUT that gives
    /// nothing back. An unpowered camera has a dark data port and every screen wired to it shows nothing,
    /// which falls out of PowerNet.SolveData rather than being re-implemented here.</summary>
    public partial class SecurityCamera : Node3D, IPowerDevice
    {
        /// <summary>What the camera draws. A small figure on purpose -- a CCTV is a trickle next to a lamp, and
        /// a chain of them off one generator should still leave headroom.</summary>
        public const float Watts = 15f;
        /// <summary>The feed's resolution. Deliberately LOW: it is a security camera being shown on a CRT, it
        /// renders a whole second view of the world every frame it is watched, and 256x192 already reads as a
        /// picture at the size a screen occupies. This is the number to lower first if cameras ever cost too
        /// much.</summary>
        public const int FeedW = 256, FeedH = 192;
        /// <summary>How far the camera can see. Short: it is watching a room, and a long far-plane would have it
        /// drawing half the map into a texture nobody can resolve.</summary>
        public const float FeedFar = 60f;
        public const float FeedFov = 70f;

        /// <summary>WHERE THE LENS IS, in the housing mesh's own frame, and WHICH WAY IT LOOKS (strawberry
        /// 2026-09-13: "put the camera viewport camera just in front of the black part").
        ///
        /// ⚠ MEASURED OFF Camera_0.obj, not placed by eye. The "black part" is the dark cell of the prop's 2x1
        /// palette -- two faces, the only two that sample u &gt; 0.5 -- and averaging them gives a centroid of
        /// (-0.2665, +0.2957, -0.1281) and a face normal of (-0.683, +0.683, -0.259). The normal is confirmed
        /// OUTWARD rather than assumed: dotted against (lens centroid - body centroid) it comes out +0.17, so it
        /// points away from the housing bulk and not into it.
        ///
        /// This replaces using the prop's ORIGIN and its -Z, which is where the feed camera sat before -- the
        /// origin is wherever the exporter left it and the housing's -Z is not the direction the lens faces, so
        /// the picture was of roughly the right place from roughly the wrong spot.</summary>
        public static readonly Vector3 LensLocal = new Vector3(-0.2665f, 0.2957f, -0.1281f);
        public static readonly Vector3 LensNormalLocal = new Vector3(-0.6830f, 0.6830f, -0.2588f).Normalized();
        /// <summary>How far in front of the glass the eye sits. Just clear of it: far enough that the lens quad
        /// itself is never in shot, near enough that the picture is the camera's and not the wall behind it.</summary>
        public const float LensStandoff = 0.03f;

        readonly System.Collections.Generic.List<ConnectionPort> _ports = new();
        ConnectionPort _plug, _dataOut;
        SubViewport _vp;
        Camera3D _cam;
        bool _filming;
        ColorRect _nvRect; ShaderMaterial _nvMat;   // the feed's own night-vision pass (see SyncFeedEnv)
        Godot.Environment _fenv;                     // the feed camera's env: a LIVE-SYNCED copy of the world's
        bool _nvOn; float _envT;

        /// <summary>How long between re-syncs of the feed's environment, seconds. The world's env is MUTATED in
        /// place by DayNightCycle every frame, so a copy taken once is a copy of one instant.</summary>
        const float EnvSyncEvery = 0.4f;

        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => 0;
        public System.Collections.Generic.IReadOnlyList<ConnectionPort> PowerPorts => _ports;

        /// <summary>Live picture, or null while unpowered/unbuilt. A TV wired to this reads it directly.</summary>
        public Texture2D FeedTexture => _filming && _vp != null && IsInstanceValid(_vp) ? _vp.GetTexture() : null;
        public bool Filming => _filming;
        public ConnectionPort DataOut => _dataOut;

        /// <summary>Build a camera on a placed Camera_0 housing. <paramref name="bodyMi"/> is the split HOUSING
        /// mesh instance -- its transform is where the lens is and which way it faces.</summary>
        public static SecurityCamera Make(MeshInstance3D bodyMi, Aabb bodyLocalAabb)
        {
            if (bodyMi == null || !IsInstanceValid(bodyMi)) return null;
            var c = new SecurityCamera { Name = "SecurityCamera", Transform = bodyMi.Transform };
            c._localAabb = bodyLocalAabb;
            return c;
        }

        Aabb _localAabb;

        public override void _Ready()
        {
            AddToGroup("deployables");   // PowerNet gathers this group by IPowerDevice, not by the concrete Deployable
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked, like every other device

            // THE PORTS. Placed on opposite faces of the housing so two wires never fight for the same spot,
            // and so which socket is which is readable without the HUD label.
            _plug = ConnectionPort.Create(this, new DeployableDef.Port
            {
                Kind = DeployableDef.PortKind.Consumer,
                Pos = _localAabb.GetCenter() + new Vector3(0f, -_localAabb.Size.Y * 0.5f - 0.04f, 0f),
                Watts = Watts,
            }, "CCTV Camera");
            AddChild(_plug);
            _ports.Add(_plug);

            _dataOut = ConnectionPort.Create(this, new DeployableDef.Port
            {
                Kind = DeployableDef.PortKind.DataOut,
                Pos = _localAabb.GetCenter() + new Vector3(0f, _localAabb.Size.Y * 0.5f + 0.04f, 0f),
                Watts = 0f,   // signal: it neither gives nor takes watts, and the solver never sees it
            }, "CCTV Camera");
            AddChild(_dataOut);
            _ports.Add(_dataOut);
            PowerNet.MarkDirty();
        }

        /// <summary>Per tick: film only while powered, and ONLY while something is actually watching.
        ///
        /// ⚠ THE SECOND HALF IS THE ONE THAT MATTERS FOR COST. A SubViewport with UpdateMode.Always renders the
        /// whole world a second time every frame, forever, for every camera on the map -- whether or not a
        /// single screen is showing it. Cameras are a thing you place a lot of, so this renders ONCE PER TICK
        /// and only when a powered receiver is wired to the other end.</summary>
        public void HubProcess(double delta)
        {
            bool want = _dataOut != null && IsInstanceValid(_dataOut) && _dataOut.DataLive && _dataOut.Occupied;
            if (want && _vp == null) BuildViewport();
            _filming = want && _vp != null;
            if (_vp != null && IsInstanceValid(_vp))
                _vp.RenderTargetUpdateMode = _filming ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Disabled;
            // Only while filming, and only every EnvSyncEvery: duplicating an Environment is not free, and a camera
            // nobody is watching does not render at all, so there is nothing for a fresh copy to be fresh FOR.
            if (!_filming) return;
            _envT += (float)delta;
            if (_envT >= EnvSyncEvery) { _envT = 0f; SyncFeedEnv(force: false); }
        }

        void BuildViewport()
        {
            // ⚠ NOT its own World3D. The camera has to see the REAL world -- this is the opposite of the
            // viewmodel and the paperdoll, which each get an isolated world precisely so the scene cannot reach
            // them. Leaving OwnWorld3D false is what makes the feed a picture of the map.
            _vp = new SubViewport
            {
                Size = new Vector2I(FeedW, FeedH),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,   // a security feed is not the place to spend MSAA
            };
            AddChild(_vp);
            _cam = new Camera3D { Fov = FeedFov, Far = FeedFar, Current = true };
            // ⚠ LINEAR TONEMAP ON THE FEED CAMERA, or the picture is TONEMAPPED TWICE and the screen washes out.
            // The feed is rendered, encoded into a texture, and then that texture is sampled by the screen
            // material and tonemapped AGAIN by the main pass -- ACES applied on top of ACES, which lifts the
            // midtones until a grey-green field reads as near-white. (Seen exactly that in the first render: the
            // ground was pale and the coloured blocks had washed out of it.) The scope PiP already carries this
            // scar, with its own note about a LINEAR env so the lens is not double-tonemapped.
            //
            // The world's environment is DUPLICATED rather than replaced, so the feed keeps the map's own sky,
            // fog and ambient and differs from the naked-eye view in exactly one property.
            SyncFeedEnv(force: true);
            _vp.AddChild(_cam);

            // ⭐ THE FEED GETS ITS OWN NIGHT-VISION PASS (master 2026-10-06: "below a certain light threshold,
            // should get the civilian nightvision filter effect on the camera, that turns off when its bright
            // again"). nightvision.gdshader is a `canvas_item` screen pass, and a SubViewport can host a canvas --
            // so the SAME shader the goggles use runs over the SAME feed, rather than a second impression of what
            // night vision looks like painted into screen.gdshader. Civilian settings exactly as NightVision.Set
            // writes them for the civilian tube, which is what master asked for.
            var nvLayer = new CanvasLayer { Layer = 1 };
            _nvMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/nightvision.gdshader") };
            _nvMat.SetShaderParameter("gain", 1.9f);
            _nvMat.SetShaderParameter("tint", new Color(0.88f, 0.90f, 0.88f));   // civilian: black-and-white
            _nvMat.SetShaderParameter("grain", NightVision.GrainCivilian);
            _nvMat.SetShaderParameter("vignette", 0.45f);
            _nvMat.SetShaderParameter("hot", 0.4f);
            _nvRect = new ColorRect { Material = _nvMat, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, Color = Colors.White };
            _nvRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            nvLayer.AddChild(_nvRect);
            _vp.AddChild(nvLayer);

            AimAtLens();
        }

        /// <summary>Keep the feed's environment matching the world's, and switch the tube on when it gets dark.
        ///
        /// ⚠⚠ THE ENVIRONMENT WAS DUPLICATED ONCE AND NEVER AGAIN (master: "the viewports of cameras dont follow
        /// lighting or fog"). DayNightCycle MUTATES the world's Environment in place every frame -- sky, ambient,
        /// fog density, glow -- so `Duplicate()` at build time froze the feed at whatever instant the camera was
        /// first switched on. A camera built at noon filmed a noon-lit world all night. The copy exists for exactly
        /// ONE reason (the feed must tonemap LINEAR or the picture is tonemapped twice, once into the texture and
        /// again by the screen that samples it), so it is re-taken on a timer and that one property re-applied.
        ///
        /// ⭐ The darkness test is `IsNightTime`, which is the SAME predicate the street lamps use -- so a camera
        /// goes to night vision exactly when the lights outside come on, rather than on a second opinion about what
        /// counts as dark. ⚠ It is a clock, not a light meter: a camera in an unlit room at noon stays daylight.
        /// That is a real limit and the honest place to fix it is a luminance probe, not a fudge here.</summary>
        void SyncFeedEnv(bool force)
        {
            if (_cam == null || !IsInstanceValid(_cam)) return;
            var worldEnv = DayNightCycle.Current?.Env;
            _fenv = worldEnv != null ? (Godot.Environment)worldEnv.Duplicate() : (_fenv ?? new Godot.Environment());
            _fenv.TonemapMode = Godot.Environment.ToneMapper.Linear;   // the one deliberate difference from the world's
            _cam.Environment = _fenv;

            bool dark = DayNightCycle.Current != null && DayNightCycle.IsNightTime(DayNightCycle.Current.Time);
            if (force || dark != _nvOn)
            {
                _nvOn = dark;
                if (_nvRect != null && IsInstanceValid(_nvRect)) _nvRect.Visible = dark;
            }
        }

        /// <summary>Put the eye just in front of the glass, looking the way the glass faces. Both come from the
        /// measured lens plate (see LensLocal), mapped through the housing's own transform -- so a camera placed
        /// at any angle on any wall still films out of its lens rather than out of its origin.</summary>
        void AimAtLens()
        {
            if (_cam == null || !IsInstanceValid(_cam)) return;
            Vector3 eye = GlobalTransform * (LensLocal + LensNormalLocal * LensStandoff);
            Vector3 dir = (GlobalTransform.Basis * LensNormalLocal).Normalized();
            if (dir.LengthSquared() < 1e-6f) { _cam.GlobalTransform = GlobalTransform; return; }
            // LookAt needs an up that is not parallel to the view; a camera aimed near-vertically would
            // otherwise produce a degenerate basis. Fall back to +X, which cannot also be vertical.
            Vector3 up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
            _cam.GlobalPosition = eye;
            _cam.LookAt(eye + dir, up);
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine callback is off
        public override void _ExitTree() { TickHub.RemoveProcess(this); PowerNet.MarkDirty(); }
    }
}
