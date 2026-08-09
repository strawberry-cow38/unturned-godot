using Godot;

namespace UnturnedGodot
{
    // Editor PLAYTEST (master 2026-08-09): a floating button that SAVES the map and drops the user in as a
    // first-person player on the ground under the fly camera; Esc returns to the editor. Spawns a
    // PlayerController, swaps the fly-cam + build tool + editor UI off, and restores them on exit.
    //
    // Began life as a Buildings-tab-only "Test Build" that spawned at the building stage. It is now on every
    // tab and spawns under the camera, because wanting to walk terrain you just sculpted is the same wish as
    // wanting to walk a building you just drew. The stage-footprint spawn survives as the FALLBACK for when
    // there is no ground under the camera at all.
    //
    // Kept ENTIRELY self-contained (its own CanvasLayer button) so it never touches EditorBuildingsPanel.
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

            _playBtn = new Button { Text = "▶  Playtest" };
            _playBtn.AddThemeFontSizeOverride("font_size", 18);
            _playBtn.AnchorLeft = 1f; _playBtn.AnchorRight = 1f;
            _playBtn.OffsetLeft = -188f; _playBtn.OffsetRight = -16f;
            _playBtn.OffsetTop = 12f; _playBtn.OffsetBottom = 48f;
            _playBtn.Pressed += EnterPlay;
            _ui.AddChild(_playBtn);

            _hint = new Label { Text = "PLAYTEST  —  map saved · press Esc to return to the editor", Visible = false };
            _hint.AddThemeFontSizeOverride("font_size", 16);
            _hint.AnchorLeft = 0.5f; _hint.AnchorRight = 0.5f;
            _hint.OffsetLeft = -240f; _hint.OffsetRight = 240f; _hint.OffsetTop = 12f;
            _hint.HorizontalAlignment = HorizontalAlignment.Center;
            _ui.AddChild(_hint);

            _editor.ModeChanged += _ => UpdateButtonVisibility();
            UpdateButtonVisibility();
        }

        // Available on EVERY tab now (master 2026-08-09: "a playtest button in the editor"). It began as a
        // Buildings-only "Test Build", but wanting to walk the terrain you just sculpted or check a prop you
        // placed is the same wish, and a button that exists on one tab reads as a bug on the others.
        void UpdateButtonVisibility()
        {
            if (_playBtn != null) _playBtn.Visible = !_playing;
        }

        public void EnterPlay()
        {
            if (_playing || _editor == null) return;
            _playing = true;

            // SAVE FIRST (master: "saves current map and then spawns u"). Playtesting is when a crash or a
            // hard exit is most likely, and losing the edits you were about to test is the worst possible
            // moment to lose them.
            _editor.Save();

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

            UpdateButtonVisibility();   // the button comes back
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

        /// <summary>The spawn point, without entering play. Exposed so a test can check WHERE it lands without
        /// spawning a PlayerController and capturing the mouse.</summary>
        public Vector3 ComputeSpawnForTest() => ComputeSpawn();

        // Footprint centre = the middle of the bbox of every wall's two base ends (UVToWorld(0,0) and
        // UVToWorld(Length,0)) -- the SAME walk the roof/floor code does. Averaging wall origins (tried first)
        // averages start-CORNERS, so it biases the spawn toward wherever the walls happen to begin and lands
        // in a wall in a draw-order-dependent way (tinyclaw caught this).
        /// <summary>Where the playtest drops you: on the ground UNDER THE FLY CAMERA, so you land looking at
        /// whatever you were just editing (master: "spawns u to walk around somewhere near the editor camera").
        ///
        /// It has to be a downward RAY, not the camera position. The editor cam commonly sits 130 m up, and
        /// spawning there means a long fall onto terrain you cannot see yet, or straight through a roof you
        /// were looking down at. Falls back to the building stage when the ray finds nothing -- off the edge of
        /// a flat map there genuinely is no ground beneath the camera.</summary>
        Vector3 ComputeSpawn()
        {
            var cam = _flyCam;
            if (cam != null && GodotObject.IsInstanceValid(cam) && cam.IsInsideTree())
            {
                var from = cam.GlobalPosition;
                var space = cam.GetWorld3D()?.DirectSpaceState;
                if (space != null)
                {
                    // Start slightly above the camera so a cam sitting just under a surface still resolves,
                    // and reach well below the lowest thing a map is likely to have.
                    var q = PhysicsRayQueryParameters3D.Create(from + Vector3.Up * 2f, from + Vector3.Down * 4000f);
                    var hit = space.IntersectRay(q);
                    if (hit != null && hit.Count > 0 && hit.ContainsKey("position"))
                        return (Vector3)hit["position"] + Vector3.Up * 1.2f;
                }
            }

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
