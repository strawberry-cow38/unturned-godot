using Godot;

namespace UnturnedGodot
{
    // FREEZE MODE (strawberry 2026-08-17): "keeps the sim paused, gives you complete freecam (with the editor
    // controls) maybe with right arrow to advance 1 sim tick at a time".
    //
    // Entered from the ESC menu. The tree stays PAUSED the whole time -- this node and its camera run with
    // ProcessMode.Always so they keep moving while the world does not -- and the right arrow lets exactly one
    // physics tick through.
    //
    // HOW THE SINGLE STEP WORKS, and why it is a real tick rather than a simulated one: Godot has no "advance the
    // physics server by one frame" call from script, so the honest way to get one tick is to actually unpause,
    // let exactly one physics frame run, and pause again. Faking it -- nudging transforms, or running at
    // Engine.TimeScale = 0.001 -- would produce something that looks like a step but is not the step the game
    // would take, which makes it useless for the thing a step mode is FOR: watching what the sim really does.
    //
    // The re-pause happens in _PhysicsProcess with PhysicsProcessPriority pinned high, so this node runs AFTER the
    // world's bodies on that frame. Re-pausing before them would consume the unpause and skip the tick, giving a
    // step button that appears to work and advances nothing.
    public partial class FreezeMode : CanvasLayer
    {
        EditorCamera _cam;
        Camera3D _prevCam;
        Label _hud;
        bool _stepArmed, _stepping;
        int _ticks;

        public bool Active { get; private set; }

        public override void _Ready()
        {
            Layer = 61;                                   // above the pause menu
            ProcessMode = Node.ProcessModeEnum.Always;     // must keep running while the tree is paused
            ProcessPhysicsPriority = 1000;                 // and must run LAST, after the bodies we are stepping
            Visible = false;

            var box = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            box.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            box.Position = new Vector2(-190f, 12f);
            AddChild(box);
            var margin = new MarginContainer();
            foreach (var sName in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(sName, 10);
            box.AddChild(margin);
            _hud = new Label { Text = "FROZEN", HorizontalAlignment = HorizontalAlignment.Center };
            margin.AddChild(_hud);
        }

        public void Enter(Node world)
        {
            if (Active) return;
            Active = true;
            Visible = true;
            _ticks = 0;
            GetTree().Paused = true;
            Input.MouseMode = Input.MouseModeEnum.Visible;

            // Start the freecam exactly where the player was looking, so entering freeze does not teleport the view.
            _prevCam = GetViewport().GetCamera3D();
            _cam = new EditorCamera { ProcessMode = Node.ProcessModeEnum.Always };
            (world ?? GetTree().Root).AddChild(_cam);
            if (_prevCam != null) _cam.GlobalTransform = _prevCam.GlobalTransform;
            _cam.Current = true;
            UpdateHud();
        }

        public void Exit()
        {
            if (!Active) return;
            Active = false;
            Visible = false;
            if (_cam != null && IsInstanceValid(_cam)) { _cam.QueueFree(); _cam = null; }
            if (_prevCam != null && IsInstanceValid(_prevCam)) _prevCam.Current = true;
            _prevCam = null;
            _stepArmed = _stepping = false;
            GetTree().Paused = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        void UpdateHud() =>
            _hud.Text = $"FROZEN  —  {_ticks} tick{(_ticks == 1 ? "" : "s")} stepped   [→] step one tick   [ESC] resume";

        public override void _UnhandledInput(InputEvent e)
        {
            if (!Active || e is not InputEventKey { Pressed: true, Echo: false } k) return;
            if (k.Keycode == Key.Right) { _stepArmed = true; GetViewport().SetInputAsHandled(); }
            else if (k.Keycode == Key.Escape) { Exit(); GetViewport().SetInputAsHandled(); }
        }

        public override void _Process(double delta)
        {
            if (!Active || !_stepArmed || _stepping) return;
            // Unpause here, in the IDLE frame, so the next physics frame is a full one for the world. Unpausing
            // from inside _PhysicsProcess would land mid-frame and make "one tick" depend on node order.
            _stepArmed = false;
            _stepping = true;
            _stepFrames = 0;
            GetTree().Paused = false;
        }

        // RE-PAUSE ONE FRAME LATER, NOT THIS ONE. Godot's physics iteration calls _PhysicsProcess during
        // flush_queries and only THEN runs PhysicsServer3D.step(), so pausing from inside the first callback after
        // unpausing cancels the very tick it was meant to let through. That is not a theory: the first version did
        // exactly this, counted its steps happily, and moved a free-falling body 0.0000 m across five presses.
        //
        // So the first callback is allowed to pass (its frame integrates), and the pause goes in on the second,
        // before that frame's step. Exactly one integration gets through, which the suite checks by comparing the
        // distance moved against a single measured tick rather than against arithmetic.
        int _stepFrames;

        public override void _PhysicsProcess(double delta)
        {
            if (!_stepping) return;
            if (++_stepFrames < 2) return;
            _stepping = false;
            _stepFrames = 0;
            GetTree().Paused = true;
            _ticks++;
            UpdateHud();
        }

        /// <summary>Step once from code, for tests: mirrors pressing the right arrow.</summary>
        public void RequestStep() { if (Active) _stepArmed = true; }
        public int TicksStepped => _ticks;
    }
}
