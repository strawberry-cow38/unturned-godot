using Godot;

namespace UnturnedGodot
{
    // Building-editor PLAY MODE (master 2026-08-09): a floating "Test Build" button drops the user in as a
    // first-person player to walk the building they just drew; Esc returns to the editor. The drawn walls are
    // already solid (WallSurface = StaticBody3D on layer 0) and BuildStage() lays a WorldBoundary ground + floor
    // plane at the stage height, so this just spawns a PlayerController on the stage, swaps the fly-cam + build
    // tool + editor UI off, and restores them on exit. Kept ENTIRELY self-contained (its own CanvasLayer button)
    // so it never touches EditorBuildingsPanel -- tinyclaw is in there for the door-on-openings fill.
    public partial class EditorPlayMode : Node3D
    {
        Editor _editor;
        EditorBuildings _buildings;
        Camera3D _flyCam;

        CanvasLayer _ui;
        Button _playBtn;
        Label _hint;
        PlayerController _player;
        bool _playing;

        public void Setup(Editor editor, EditorBuildings buildings, Camera3D flyCam)
        {
            _editor = editor;
            _buildings = buildings;
            _flyCam = flyCam;

            _ui = new CanvasLayer { Name = "PlayModeUI", Layer = 160 };
            AddChild(_ui);

            _playBtn = new Button { Text = "▶  Test Build" };
            _playBtn.AddThemeFontSizeOverride("font_size", 18);
            _playBtn.AnchorLeft = 1f; _playBtn.AnchorRight = 1f;
            _playBtn.OffsetLeft = -188f; _playBtn.OffsetRight = -16f;
            _playBtn.OffsetTop = 12f; _playBtn.OffsetBottom = 48f;
            _playBtn.Pressed += EnterPlay;
            _ui.AddChild(_playBtn);

            _hint = new Label { Text = "TEST MODE  —  press Esc to return to the editor", Visible = false };
            _hint.AddThemeFontSizeOverride("font_size", 16);
            _hint.AnchorLeft = 0.5f; _hint.AnchorRight = 0.5f;
            _hint.OffsetLeft = -240f; _hint.OffsetRight = 240f; _hint.OffsetTop = 12f;
            _hint.HorizontalAlignment = HorizontalAlignment.Center;
            _ui.AddChild(_hint);

            _editor.ModeChanged += _ => UpdateButtonVisibility();   // the Test Build button belongs to the Buildings tab
            UpdateButtonVisibility();
        }

        // Show the Test Build button only on the Buildings tab (and never mid-test). Master asked for a
        // BUILDING-editor play mode, so it has no business on the map / terrain / spawns tabs.
        void UpdateButtonVisibility()
        {
            if (_playBtn != null)
                _playBtn.Visible = !_playing && _editor != null && _editor.Mode == EEditorMode.Buildings;
        }

        void EnterPlay()
        {
            if (_playing || _editor == null) return;
            _playing = true;

            _player = new PlayerController { CaptureMouse = true };
            _editor.AddChild(_player);
            _player.GlobalPosition = ComputeSpawn();   // _Ready makes its FP camera Current + captures the mouse

            if (_buildings != null) _buildings.Active = false;                             // stop the build tool eating input
            if (_flyCam != null) { _flyCam.SetProcess(false); _flyCam.SetProcessUnhandledInput(false); }
            SetEditorUiVisible(false);

            UpdateButtonVisibility();   // hidden while _playing
            _hint.Visible = true;
        }

        void ExitPlay()
        {
            if (!_playing) return;
            _playing = false;

            if (GodotObject.IsInstanceValid(_player)) _player.QueueFree();
            _player = null;
            Input.MouseMode = Input.MouseModeEnum.Visible;

            if (_flyCam != null) { _flyCam.SetProcess(true); _flyCam.SetProcessUnhandledInput(true); _flyCam.Current = true; }
            if (_buildings != null) _buildings.Active = true;
            SetEditorUiVisible(true);

            UpdateButtonVisibility();   // restored, but only on the Buildings tab
            _hint.Visible = false;
        }

        public override void _Input(InputEvent ev)
        {
            if (_playing && ev is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
            {
                ExitPlay();
                GetViewport().SetInputAsHandled();
            }
        }

        // Hide/show the editor's UI (the dashboard + any other editor CanvasLayer child of the Editor) while
        // testing, so the play view is clean. Our own PlayModeUI sits under THIS node, not the editor, so it is
        // untouched here (the button + hint are toggled individually).
        void SetEditorUiVisible(bool on)
        {
            if (_editor == null) return;
            foreach (var c in _editor.GetChildren())
                if (c is CanvasLayer cl) cl.Visible = on;
        }

        // Footprint centre = the middle of the bbox of every wall's two base ends (UVToWorld(0,0) and
        // UVToWorld(Length,0)) -- the SAME walk the roof/floor code does. Averaging wall origins (tried first)
        // averages start-CORNERS, so it biases the spawn toward wherever the walls happen to begin and lands
        // in a wall in a draw-order-dependent way (tinyclaw caught this).
        Vector3 ComputeSpawn()
        {
            float groundY = EditorBuildings.StageOrigin.Y;   // BuildStage() lays the ground plane at the stage height
            Vector3 fallback = new Vector3(EditorBuildings.StageOrigin.X, groundY + 1.2f, EditorBuildings.StageOrigin.Z);
            var walls = _buildings?.Walls;
            if (walls == null || walls.Count == 0) return fallback;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            int n = 0;
            foreach (var w in walls)
            {
                if (!GodotObject.IsInstanceValid(w)) continue;
                Vector3 a = w.UVToWorld(0f, 0f), b = w.UVToWorld(w.Length, 0f);
                minX = Mathf.Min(minX, Mathf.Min(a.X, b.X)); maxX = Mathf.Max(maxX, Mathf.Max(a.X, b.X));
                minZ = Mathf.Min(minZ, Mathf.Min(a.Z, b.Z)); maxZ = Mathf.Max(maxZ, Mathf.Max(a.Z, b.Z));
                n++;
            }
            if (n == 0) return fallback;
            return new Vector3((minX + maxX) * 0.5f, groundY + 1.2f, (minZ + maxZ) * 0.5f);
        }
    }
}
