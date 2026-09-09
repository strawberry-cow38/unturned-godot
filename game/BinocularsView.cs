using Godot;

namespace UnturnedGodot
{
    /// <summary>BINOCULARS, two states (master 2026-09-05: "give the non-ads the PIP lenses and change the ads'd one to be a dumb
    /// overlay like in the real game source"):
    ///   LOWERED (carried at the chest, Viewmodel): this node renders the world MAGNIFIED into a SubViewport that inherits the
    ///   main World3D (a second camera on the main camera's transform at FOV / Zoom; OwnWorld3D=true would duplicate an EMPTY
    ///   world -- the scope lesson) and the two lens discs in the carried model's eyepieces show it (content/binoculars.gdshader,
    ///   Viewmodel.AddHeldLens) -- the scope's two-render PiP.
    ///   RAISED (RMB held): retail -- the MAIN camera's FOV is divided by the zoom (PlayerLook.enableZoom) and the retail
    ///   overlay (ui/player/overlay/binoculars.png, PlayerLifeUI.binocularsOverlay) is drawn over it; the PiP stops.
    /// CanvasLayer 6: over the viewmodel composite (5), under the HUD (10).</summary>
    public partial class BinocularsView : CanvasLayer
    {
        public float Zoom = 4f;
        bool _raised;
        public bool Raised
        {
            get => _raised;
            set
            {
                _raised = value;
                if (_overlay != null) _overlay.Visible = value;
                if (_world != null) _world.RenderTargetUpdateMode = value ? SubViewport.UpdateMode.Disabled : SubViewport.UpdateMode.Always;   // the PiP only feeds the carried lenses
            }
        }
        public Texture2D ViewTexture => _world?.GetTexture();

        SubViewport _world; Camera3D _cam; TextureRect _overlay; Vector2I _size; Vector2 _win;

        public override void _Ready()
        {
            Layer = 6;
            ProcessPriority = 200;   // after the player has placed the main camera for this frame
            // ⚠ INPUT: this viewport is a RENDER TARGET and nothing else, so it must not take part in input routing
            // at all (strawberry 2026-09-09: "holding binoculars kills my mouse input until i pause and unpause" --
            // cursor still hidden, camera frozen, i.e. the mode was fine and the MOTION EVENTS were being eaten).
            //
            // It carried HandleInputLocally = false, and that is the whole story: of the ten SubViewports in this
            // project -- the arms, the scope, the water mirror, the outline pass, the paperdoll, the prop
            // thumbnails, the info billboards, the resource icons, the map bake -- this was the ONLY one that set
            // it. False means the viewport defers input to its parent rather than keeping it, so an events pass
            // ran through here on the way to the player, and the player's look lives in _UnhandledInput, which is
            // downstream of anything that marks an event handled. Nothing else about the binoculars is unusual;
            // this one flag was.
            //
            // GuiDisableInput on top, because it is free and true: there is not a single Control inside this
            // viewport, only a Camera3D, so there is no GUI here to feed.
            _world = new SubViewport
            {
                OwnWorld3D = false,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                HandleInputLocally = true,   // the default every other viewport in this project uses
                GuiDisableInput = true,
            };
            AddChild(_world);
            _cam = new Camera3D { Current = true };
            _world.AddChild(_cam);
            var main = GetViewport()?.GetCamera3D();
            if (main != null) _world.World3D = main.GetWorld3D();

            var maskMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/binoculars_overlay.gdshader") };
            string mp = ProjectSettings.GlobalizePath("res://content/ui/binoculars_overlay.png");
            if (System.IO.File.Exists(mp)) { var img = new Image(); if (ContentProvider.LoadOk(img, mp)) maskMat.SetShaderParameter("mask", ImageTexture.CreateFromImage(img)); }
            _overlay = new TextureRect { Texture = new PlaceholderTexture2D { Size = Vector2.One }, Material = maskMat, StretchMode = TextureRect.StretchModeEnum.Scale,
                                         ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
            _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_overlay);
            _win = GetViewport().GetVisibleRect().Size;   // read ONCE + on resize: asking every frame is a RenderingServer sync (viewport_find_from_screen_attachment) that crawled the movie harness
            GetTree().Root.SizeChanged += () => { _win = GetViewport().GetVisibleRect().Size; Resize(); };
            Resize();
            Raised = _raised;
        }

        void Resize()
        {
            var win = _win;
            int h = Mathf.Clamp(GraphicsOptions.ScopeSize * 2, 540, Mathf.Max(540, (int)win.Y));   // Low 720 / Medium 1080 / High = the screen
            var sz = new Vector2I(Mathf.Max(16, Mathf.RoundToInt(h * win.X / Mathf.Max(1f, win.Y))), h);
            if (sz == _size) return;
            _size = sz; _world.Size = sz;
            (_overlay?.Material as ShaderMaterial)?.SetShaderParameter("screen_aspect", win.X / Mathf.Max(1f, win.Y));   // keeps the retail overlay's holes round on non-16:9 screens
        }

        /// <summary>The viewmodel holding the pair, if any. When set, the CARRIED pip looks down the barrels.</summary>
        public Viewmodel Vm;

        /// <summary>How far in front of the objective end the pip camera sits, metres. Far enough that the pair's
        /// own geometry is behind the near plane rather than filling its own lenses.</summary>
        public const float ObjectiveAhead = 0.12f;

        public override void _Process(double delta)
        {
            if (_raised) return;
            var main = GetViewport()?.GetCamera3D();
            if (main == null) return;

            // CARRIED: the discs show what the BINOCULARS are pointed at, not what you are (strawberry 2026-09-09:
            // "change binocular pip 'camera' when held ... to be in front of the binocular viewmodel and follow
            // it instead of being from center screen"). Lowered, the pair sits across your chest at its own angle,
            // so a pip rendered from the eyeline showed two little discs of exactly the view already behind them.
            //
            // The mapping is exact rather than approximate: the arms viewport's camera is at the origin with an
            // identity basis, so its world space IS camera space, and mainCam.GlobalTransform composes the held
            // pose straight into the world.
            //
            // +Y IS THE LOOKING DIRECTION, derived from the same convention the lenses are placed on rather than
            // guessed: PlayerController puts the eyepiece discs at -Y "facing -Y (the eye)", so the objectives
            // point +Y. Rx(+90) takes a camera's -Z onto +Y, which is the basis below.
            var pose = Vm?.HeldModelViewPose;
            if (pose.HasValue)
            {
                var view = pose.Value;
                var camBasis = view.Basis * new Basis(Vector3.Right, Mathf.Pi * 0.5f);
                var camPos = view.Origin + view.Basis.Y.Normalized() * ObjectiveAhead;
                _cam.GlobalTransform = main.GlobalTransform * new Transform3D(camBasis, camPos);
            }
            else _cam.GlobalTransform = main.GlobalTransform;   // nothing held (harness paths): the old eyeline behaviour

            _cam.Fov = main.Fov / Mathf.Max(1f, Zoom);
            _cam.Near = main.Near; _cam.Far = main.Far; _cam.CullMask = main.CullMask;
        }
    }
}
