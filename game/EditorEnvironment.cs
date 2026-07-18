using Godot;

namespace UnturnedGodot
{
    // Environment sub-editor (first slice), ported from SDG.Unturned EditorEnvironment (Lighting tab). The source
    // Lighting UI edits LevelLighting (time-of-day, azimuth/bias, sea/snow level, rain/snow weather, moon). This slice
    // covers the two most visible: TIME-OF-DAY (source timeSlider) and OVERCAST weather -> driven through the port's
    // DayNightCycle (Time 0..1, Overcast). While the Environment tab is active the editor PREVIEWS the real day-night
    // lighting (the other tabs freeze it to a clean fog-free look); ',' '.' scrub time, O toggles overcast. Persists to
    // content/spawns/editor_environment.txt. Azimuth/sea-level/moon + Nav/Nodes/Roads sub-editors land next.
    public partial class EditorEnvironment : Node
    {
        readonly Editor _editor;
        readonly DayNightCycle _dayNight;
        readonly System.Action _cleanLighting;   // restore the editor's clean fog-free look (BuildEditor's setup)
        bool _wasActive;

        public float Time => _dayNight?.Time ?? 0.5f;
        public bool Overcast => _dayNight?.Overcast ?? false;
        public string ModeText => $"time {Time:0.00} ({TimeName()}){(Overcast ? " · overcast" : "")}";

        string TimeName() => Time < 0.2f || Time > 0.85f ? "night" : Time < 0.35f ? "dawn" : Time < 0.65f ? "day" : "dusk";

        public EditorEnvironment(Editor editor, DayNightCycle dayNight, System.Action cleanLighting)
        {
            _editor = editor; _dayNight = dayNight; _cleanLighting = cleanLighting;
            Load();
            _editor.ModeChanged += _ => Sync();
            Sync();
        }

        void Sync()   // Environment tab -> preview the real day-night; other tabs -> the clean editor look
        {
            bool active = _editor.Mode == EEditorMode.Environment;
            if (active == _wasActive || _dayNight == null) { _wasActive = active; return; }
            _wasActive = active;
            if (active) { _dayNight.VisualsEnabled = true; _dayNight.Apply(); }
            else { _dayNight.VisualsEnabled = false; _cleanLighting?.Invoke(); }
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Environment || _dayNight == null) return;
            if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                switch (k.Keycode)
                {
                    case Key.Comma: _dayNight.Time = Mathf.Wrap(_dayNight.Time - 0.02f, 0f, 1f); _dayNight.Apply(); break;   // scrub time back (source timeSlider)
                    case Key.Period: _dayNight.Time = Mathf.Wrap(_dayNight.Time + 0.02f, 0f, 1f); _dayNight.Apply(); break;   // scrub time forward
                    case Key.O: _dayNight.Overcast = !_dayNight.Overcast; _dayNight.Apply(); break;                            // toggle overcast (source snow/rain toggle)
                }
            }
        }

        static string SavePath => ProjectSettings.GlobalizePath("res://content/spawns/") + $"editor_{Editor.Instance?.MapName ?? "PEI"}_environment.txt";

        void Load()
        {
            if (_dayNight == null || !System.IO.File.Exists(SavePath)) return;
            var p = System.IO.File.ReadAllText(SavePath).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 1 && float.TryParse(p[0], out var t)) _dayNight.Time = t;
            if (p.Length >= 2) _dayNight.Overcast = p[1] == "1";
            GD.Print($"[editor-env] loaded time={_dayNight.Time:0.00} overcast={_dayNight.Overcast}");
        }

        public int Save()   // Editor.Save() fan-out (source Editor.save -> EditorEnvironment lighting)
        {
            if (_dayNight == null) return 0;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SavePath));
            System.IO.File.WriteAllText(SavePath, $"{_dayNight.Time:0.###} {(_dayNight.Overcast ? 1 : 0)}");
            GD.Print($"[editor-env] saved time={_dayNight.Time:0.00} overcast={_dayNight.Overcast}");
            return 1;
        }

        // harness (--editor UG_EDITORENV): jump to the Environment tab at a set time so a render shows the preview
        public void DemoSet(float time, bool overcast)
        {
            if (_dayNight == null) return;
            _editor.Mode = EEditorMode.Environment;
            _dayNight.Time = time; _dayNight.Overcast = overcast; _dayNight.VisualsEnabled = true; _dayNight.Apply();
        }
    }
}
