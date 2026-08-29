using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Editor dashboard UI, ported from Unturned's EditorDashboardUI (SDG.Unturned, UI/Edit/). The mode-tab bar
    // that switches which sub-editor is active (Objects/Terrain/Environment/Spawns/Volumes), plus Save + Exit
    // and a status/help line. Phase 1: the tabs switch Editor.Mode (the per-mode panels land in later phases).
    public partial class EditorDashboard : CanvasLayer
    {
        public System.Action OnExit;         // Main wires this: tear the editor down + return to the menu
        public Editor Editor;                // set by Main before AddChild

        Label _status;
        EditorObjectBrowser _browser;   // the Objects-tab palette (shown only in Objects mode)
        EditorTerrainPanel _terrainPanel;   // the Terrain-tab tool buttons (shown only in Terrain mode)
        EditorSpawnsPanel _spawnsPanel;     // the Spawns-tab tool buttons (shown only in Spawns mode)
        EditorRoadsPanel _roadsPanel;       // the road/rail AND river tool buttons (shown only in Environment mode)
        EditorBuildingsPanel _buildPanel;   // the Level-tab building tool (shares the tab with the browser)
        readonly Dictionary<EEditorMode, Button> _tabs = new();
        Label _toast; double _toastT;
        Button _exitBtn; bool _exitArmed; double _exitArmT;   // two-step exit while there is unsaved work                       // transient centered message (source EditorUI.message / EEditorMessage)
        Control _pause;                                     // ESC pause overlay (source EditorPauseUI, slim: Resume/Save/Exit)
        bool _visObjects = true, _visRoads = true, _visFoliage = true;   // F1/F2/F3 level-visibility toggles (source EditorLevelVisibilityUI)
        Label _hover;                                       // object-under-cursor readout (source EditorObjects hover hint)

        public override void _Ready()
        {
            Layer = 60;

            // top-left: the mode tabs
            var bar = new HBoxContainer { Position = new Vector2(12f, 10f) };
            bar.AddThemeConstantOverride("separation", 6);
            AddChild(bar);
            foreach (EEditorMode m in System.Enum.GetValues(typeof(EEditorMode)))
            {
                var mode = m;
                var b = new Button { Text = m.ToString(), ToggleMode = true, CustomMinimumSize = new Vector2(112f, 40f) };
                b.AddThemeFontSizeOverride("font_size", 16);
                b.Pressed += () => { if (Editor != null) Editor.Mode = mode; };
                bar.AddChild(b);
                _tabs[m] = b;
            }

            // top-right: Save + Exit
            var right = new HBoxContainer();
            right.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            right.Position = new Vector2(-206f, 10f);
            right.AddThemeConstantOverride("separation", 8);
            AddChild(right);
            var save = new Button { Text = "Save", CustomMinimumSize = new Vector2(90f, 40f) };
            save.Pressed += () => Editor?.Save();
            right.AddChild(save);
            var exit = new Button { Text = "Exit", CustomMinimumSize = new Vector2(90f, 40f) };
            // TWO-STEP EXIT WHEN DIRTY. Leaving on a single click with unsaved work is the most common way an
            // afternoon disappears, and it is silent. The first press arms and says what is at stake; the
            // second within 4s leaves anyway, so this costs a deliberate quitter one extra click and never
            // blocks them. Not a modal: a modal here would need focus handling the rest of this UI does not do.
            exit.Pressed += () =>
            {
                if (Editor != null && Editor.WouldLoseWork && !_exitArmed)
                {
                    _exitArmed = true; _exitArmT = 4.0;
                    exit.Text = "Exit anyway?";
                    ShowMessage($"Unsaved changes ({Editor.SecondsSinceSave:0}s). Ctrl+S to save, or press Exit again to discard.", 4.0);
                    return;
                }
                OnExit?.Invoke();
            };
            _exitBtn = exit;
            right.AddChild(exit);

            // bottom-left: status + controls help
            _status = new Label();
            _status.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            _status.Position = new Vector2(12f, -30f);
            _status.AddThemeColorOverride("font_color", new Color(0.9f, 0.95f, 0.9f));
            _status.AddThemeColorOverride("font_outline_color", Colors.Black);
            _status.AddThemeConstantOverride("outline_size", 3);
            AddChild(_status);

            // centered transient message toast (source EditorUI.message): save confirmations + tool notices
            _toast = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
            _toast.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            _toast.Position = new Vector2(-220f, 64f);
            _toast.CustomMinimumSize = new Vector2(440f, 0f);
            _toast.AddThemeFontSizeOverride("font_size", 20);
            _toast.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.72f));
            _toast.AddThemeColorOverride("font_outline_color", Colors.Black);
            _toast.AddThemeConstantOverride("outline_size", 4);
            _pause = BuildPauseOverlay();   // ESC pause menu (hidden until toggled)
            AddChild(_pause);
            AddChild(_toast);   // added AFTER the pause so notifications render on top of the dim
            if (System.Environment.GetEnvironmentVariable("UG_EDITOR_SHOWMENU") == "1")   // headless verify hook: show the pause menu + a toast for the --shot
                CallDeferred(nameof(ShowMenuForShot));
            if (System.Environment.GetEnvironmentVariable("UG_EDITOR_HIDETEST") == "1")   // headless verify hook: hide the F1/F2/F3 layers for the --shot
                CallDeferred(nameof(HideLayersForShot));

            _hover = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
            _hover.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            _hover.Position = new Vector2(-160f, 98f);
            _hover.CustomMinimumSize = new Vector2(320f, 0f);
            _hover.AddThemeFontSizeOverride("font_size", 15);
            _hover.AddThemeColorOverride("font_color", new Color(0.8f, 0.92f, 1f));
            _hover.AddThemeColorOverride("font_outline_color", Colors.Black);
            _hover.AddThemeConstantOverride("outline_size", 3);
            AddChild(_hover);

            if (Editor?.Objects != null) { _browser = new EditorObjectBrowser(Editor.Objects); AddChild(_browser); }
            if (Editor?.Buildings != null) { _buildPanel = new EditorBuildingsPanel(Editor.Buildings); AddChild(_buildPanel); }
            if (Editor?.TerrainEd != null) { _terrainPanel = new EditorTerrainPanel(Editor.TerrainEd); AddChild(_terrainPanel); }
            if (Editor?.Spawns != null) { _spawnsPanel = new EditorSpawnsPanel(Editor.Spawns); AddChild(_spawnsPanel); }
            if (Editor?.RoadDrawEd != null || Editor?.RoadsEd != null || Editor?.RiverEd != null) { _roadsPanel = new EditorRoadsPanel(Editor.RoadDrawEd, Editor.RoadsEd, Editor.RiverEd); AddChild(_roadsPanel); }
            if (Editor != null) Editor.ModeChanged += _ => Refresh();
            Refresh();
        }

        void Refresh()
        {
            var active = Editor?.Mode ?? EEditorMode.Level;
            foreach (var kv in _tabs) kv.Value.ButtonPressed = kv.Key == active;
            // One panel per tab; they all occupy the same corner, and two overlapping palettes is how you end
            // up clicking the one you cannot see.
            if (_browser != null) _browser.Visible = active == EEditorMode.Level;
            if (_buildPanel != null) _buildPanel.Visible = active == EEditorMode.Buildings;
            if (_terrainPanel != null) _terrainPanel.Visible = active == EEditorMode.Terrain;   // terrain tool buttons under the Terrain tab
            if (_spawnsPanel != null) _spawnsPanel.Visible = active == EEditorMode.Spawns;       // spawns tool buttons under the Spawns tab
            if (_roadsPanel != null) _roadsPanel.Visible = active == EEditorMode.Environment;     // road/rail tool buttons under the Environment tab
        }

        // transient centered notice (source EditorUI.message): e.g. save confirmation
        public void ShowMessage(string msg, double dur = 2.5)
        {
            if (_toast == null) return;
            _toast.Text = msg; _toast.Visible = true; _toastT = dur;
        }

        /// <summary>Global key reference, on ? or Shift+F1. The per-tool hints in the status line only cover the
        /// ACTIVE tool -- the session-wide keys (save, undo, the visibility toggles) were written down nowhere on
        /// screen, so the only way to learn them was to be told or to read the source.</summary>
        void ShowHelp()
        {
            ShowMessage(
                "Ctrl+S save   ·   Ctrl+Z undo   ·   F1/F2/F3 show-hide objects/roads/foliage   ·   Esc menu\n" +
                "RMB-drag fly   ·   WASD move   ·   E/Q up-down   ·   scroll speed\n" +
                "Tools: R draw road · Shift+R legacy pave · V river   ·   per-tool keys are in the status bar",
                7.0);
        }

        void TogglePause(bool? on = null) { if (_pause != null) _pause.Visible = on ?? !_pause.Visible; }
        void ShowMenuForShot() { ShowMessage("Saved 'PEI'  ·  42 props", 999.0); TogglePause(true); }   // UG_EDITOR_SHOWMENU verify hook
        void HideLayersForShot() { Editor?.Objects?.SetVisible(false); Editor?.RoadsEd?.SetVisible(false); Editor?.Objects?.SetFoliageVisible(false); ShowMessage("layers hidden: objects · roads · foliage", 999.0); }   // UG_EDITOR_HIDETEST verify hook

        // slim EditorPauseUI: dim + Resume / Save / Exit (Options/Display/etc submenus land later)
        Control BuildPauseOverlay()
        {
            var root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(dim);
            var box = new VBoxContainer { Position = new Vector2(-90f, -80f) };
            box.SetAnchorsPreset(Control.LayoutPreset.Center);
            box.AddThemeConstantOverride("separation", 10);
            root.AddChild(box);
            var title = new Label { Text = "PAUSED", HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(180f, 0f) };
            title.AddThemeFontSizeOverride("font_size", 26);
            box.AddChild(title);
            void Btn(string t, System.Action onPress) { var b = new Button { Text = t, CustomMinimumSize = new Vector2(180f, 42f) }; b.Pressed += onPress; box.AddChild(b); }
            Btn("Resume", () => TogglePause(false));
            Btn("Save", () => { Editor?.Save(); ShowMessage($"Saved '{Editor?.MapName}'"); });
            Btn("Exit to Menu", () => OnExit?.Invoke());
            return root;
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is not InputEventKey { Pressed: true, Echo: false } k) return;
            if (k.Keycode == Key.S && Input.IsKeyPressed(Key.Ctrl))   // Ctrl+S: save the whole level (source EditorInteract)
            {
                Editor?.Save(); ShowMessage($"Saved '{Editor?.MapName}'"); GetViewport().SetInputAsHandled();
            }
            else if (k.Keycode == Key.Escape)                          // ESC: toggle the pause menu (source EditorUI)
            {
                TogglePause(); GetViewport().SetInputAsHandled();
            }
            else if (k.Keycode == Key.F1) { _visObjects = !_visObjects; Editor?.Objects?.SetVisible(_visObjects); ShowMessage($"Objects: {(_visObjects ? "shown" : "hidden")}"); GetViewport().SetInputAsHandled(); }   // level-visibility toggles (source EditorLevelVisibilityUI F1-F9)
            else if (k.Keycode == Key.F2) { _visRoads = !_visRoads; Editor?.RoadsEd?.SetVisible(_visRoads); ShowMessage($"Roads: {(_visRoads ? "shown" : "hidden")}"); GetViewport().SetInputAsHandled(); }
            else if (k.Keycode == Key.F3) { _visFoliage = !_visFoliage; Editor?.Objects?.SetFoliageVisible(_visFoliage); ShowMessage($"Foliage: {(_visFoliage ? "shown" : "hidden")}"); GetViewport().SetInputAsHandled(); }
            else if (k.Keycode == Key.F1 && Input.IsKeyPressed(Key.Shift)) { ShowHelp(); GetViewport().SetInputAsHandled(); }
            else if (k.Keycode == Key.Slash && Input.IsKeyPressed(Key.Shift)) { ShowHelp(); GetViewport().SetInputAsHandled(); }   // "?"

        }

        public override void _Process(double delta)
        {
            if (_toast != null && _toast.Visible) { _toastT -= delta; if (_toastT <= 0.0) _toast.Visible = false; }   // expire the message toast
            if (_exitArmed) { _exitArmT -= delta; if (_exitArmT <= 0.0) { _exitArmed = false; if (_exitBtn != null) _exitBtn.Text = "Exit"; } }   // disarm, so a stale arm cannot swallow a later deliberate click
            if (_hover != null) { var hn = Editor?.Objects?.HoverName; _hover.Visible = !string.IsNullOrEmpty(hn); _hover.Text = hn; }   // object-under-cursor readout
            if (Editor == null || _status == null) return;
            float spd = Editor.Camera?.Speed ?? 0f;
            string space = Editor.Objects != null && Editor.Objects.GizmoLocalSpace ? "local" : "global";
            string gm = Editor.Objects?.GizmoModeText ?? "move";
            bool bld = Editor.Mode == EEditorMode.Buildings;
            string obj = Editor.Mode == EEditorMode.Level ? $"   ·   LMB place/select · drag box-select · Shift multi · {gm} gizmo (T) · Ctrl+C/V dup · Ctrl+B/N align · Del · F focus · Ctrl-snap {Editor.Objects?.GizmoSnapLabel} (. cycles)" : "";
            string build = bld && Editor.Buildings != null
                // The tool keys are listed because until they were, there were none -- every switch was a trip
                // to the panel. A shortcut nobody is told about is the same as no shortcut.
                ? $"   ·   {Editor.Buildings.ToolText} · B wall R room F floor G roof T stairs V foundation X delete · H auto-floor rooms · 1-6 preset · Q/E storey · drag an edge to resize · Del removes · Esc cancels · {Editor.Buildings.Walls.Count} walls" : "";
            string spawn = Editor.Mode == EEditorMode.Spawns && Editor.Spawns != null ? $"   ·   Tab category · 1=add 2=remove · {Editor.Spawns.ModeText} · ,/. rot · [/] radius · V alt · T type · {Editor.Spawns.Count} spawns" : "";
            string envs = Editor.Mode == EEditorMode.Environment && Editor.Environment != null ? $"   ·   ,/. time · O overcast · {Editor.Environment.ModeText}{(Editor.RoadDrawEd != null ? $"   ·   {Editor.RoadDrawEd.ModeText}" : "")}{(Editor.RoadsEd is { Paving: true } ? $"   ·   {Editor.RoadsEd.ModeText}" : "")}{(Editor.RiverEd != null ? $"   ·   {Editor.RiverEd.ModeText}" : "")}{(Editor.FoliageEd != null ? $"   ·   FOLIAGE {Editor.FoliageEd.ModeText} · LMB paint · Alt+LMB erase placed · Alt+Shift+LMB erase baked" : "")}" : "";
            string terr = Editor.Mode == EEditorMode.Terrain && Editor.TerrainEd != null ? $"   ·   LMB raise · Shift+LMB lower · [/] radius · ,/. strength · {Editor.TerrainEd.ModeText}" : "";
            // UNSAVED indicator. Nothing used to tell you there were unsaved changes, so the only way to find
            // out was to lose them. Shows the AGE of the unsaved work, not just a dot -- "unsaved" alone is easy
            // to stop seeing, whereas a number that keeps climbing is not.
            string save = Editor.IsDirty
                ? $"   ·   ● UNSAVED {Editor.SecondsSinceSave:0}s (Ctrl+S)"
                : $"   ·   {Editor.LastSaveLabel}";
            _status.Text = $"{Editor.Mode}   ·   RMB fly · WASD · E/Q up-down · scroll = speed (×{spd:0}){obj}{build}{spawn}{envs}{terr}   ·   map: {Editor.MapName}{save}";
            if (_status != null)
                _status.AddThemeColorOverride("font_color", Editor.IsDirty
                    ? new Color(1f, 0.82f, 0.35f)        // amber while unsaved
                    : new Color(0.9f, 0.95f, 0.9f));
        }
    }
}
