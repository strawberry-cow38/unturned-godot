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

        readonly System.Collections.Generic.List<ConnectionPort> _ports = new();
        ConnectionPort _plug, _dataOut;
        SubViewport _vp;
        Camera3D _cam;
        bool _filming;

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
            var worldEnv = DayNightCycle.Current?.Env;
            var fenv = worldEnv != null ? (Godot.Environment)worldEnv.Duplicate() : new Godot.Environment();
            fenv.TonemapMode = Godot.Environment.ToneMapper.Linear;
            _cam.Environment = fenv;
            _vp.AddChild(_cam);
            // The lens looks along the housing's own forward. Godot cameras look down -Z, and the housing's
            // transform is the prop placement, so this is simply "where the model faces".
            _cam.GlobalTransform = GlobalTransform;
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine callback is off
        public override void _ExitTree() { TickHub.RemoveProcess(this); PowerNet.MarkDirty(); }
    }
}
