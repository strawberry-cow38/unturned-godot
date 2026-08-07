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
        EditorBuildingsPanel _buildPanel;   // the Level-tab building tool (shares the tab with the browser)
        HBoxContainer _levelTools;          // Objects / Buildings switch, shown only in Level mode
        VSeparator _levelSep;               // divides it from the mode tabs; hides with it
        readonly Dictionary<EEditorMode, Button> _tabs = new();
        readonly Dictionary<Editor.ELevelTool, Button> _levelToolBtns = new();

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
            exit.Pressed += () => OnExit?.Invoke();
            right.AddChild(exit);

            // bottom-left: status + controls help
            _status = new Label();
            _status.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            _status.Position = new Vector2(12f, -30f);
            _status.AddThemeColorOverride("font_color", new Color(0.9f, 0.95f, 0.9f));
            _status.AddThemeColorOverride("font_outline_color", Colors.Black);
            _status.AddThemeConstantOverride("outline_size", 3);
            AddChild(_status);

            // The Level tab hosts two tools that both want the mouse, so it needs its own switch. Without one
            // the building tool is code nobody can reach -- built, registered, and invisible.
            //
            // It rides on the END of the tab bar rather than on a row of its own below it: the perf overlay
            // owns the top-left corner underneath the tabs and draws straight over anything parked there, so a
            // second row is a switch you cannot read and, at a glance, a tool that still is not wired up.
            _levelTools = new HBoxContainer();
            _levelTools.AddThemeConstantOverride("separation", 6);
            _levelSep = new VSeparator();
            bar.AddChild(_levelSep);
            bar.AddChild(_levelTools);
            foreach (Editor.ELevelTool t in System.Enum.GetValues(typeof(Editor.ELevelTool)))
            {
                var tool = t;
                var b2 = new Button { Text = t.ToString(), ToggleMode = true, CustomMinimumSize = new Vector2(112f, 40f) };
                b2.AddThemeFontSizeOverride("font_size", 16);
                b2.Pressed += () => { if (Editor != null) { Editor.LevelTool = tool; Refresh(); } };
                _levelTools.AddChild(b2);
                _levelToolBtns[t] = b2;
            }

            if (Editor?.Objects != null) { _browser = new EditorObjectBrowser(Editor.Objects); AddChild(_browser); }
            if (Editor?.Buildings != null) { _buildPanel = new EditorBuildingsPanel(Editor.Buildings); AddChild(_buildPanel); }
            if (Editor?.TerrainEd != null) { _terrainPanel = new EditorTerrainPanel(Editor.TerrainEd); AddChild(_terrainPanel); }
            if (Editor?.Spawns != null) { _spawnsPanel = new EditorSpawnsPanel(Editor.Spawns); AddChild(_spawnsPanel); }
            if (Editor != null) Editor.ModeChanged += _ => Refresh();
            Refresh();
        }

        void Refresh()
        {
            var active = Editor?.Mode ?? EEditorMode.Level;
            foreach (var kv in _tabs) kv.Value.ButtonPressed = kv.Key == active;
            var lt = Editor?.LevelTool ?? Editor.ELevelTool.Objects;
            if (_levelTools != null)
            {
                _levelTools.Visible = active == EEditorMode.Level;
                if (_levelSep != null) _levelSep.Visible = _levelTools.Visible;
            }
            foreach (var kv in _levelToolBtns) kv.Value.ButtonPressed = kv.Key == lt;
            // Only ONE Level-tab panel is up at a time -- they occupy the same corner, and two overlapping
            // palettes is how you end up clicking the one you cannot see.
            if (_browser != null) _browser.Visible = active == EEditorMode.Level && lt == Editor.ELevelTool.Objects;
            if (_buildPanel != null) _buildPanel.Visible = active == EEditorMode.Level && lt == Editor.ELevelTool.Buildings;
            if (_terrainPanel != null) _terrainPanel.Visible = active == EEditorMode.Terrain;   // terrain tool buttons under the Terrain tab
            if (_spawnsPanel != null) _spawnsPanel.Visible = active == EEditorMode.Spawns;       // spawns tool buttons under the Spawns tab
        }

        public override void _Process(double delta)
        {
            if (Editor == null || _status == null) return;
            float spd = Editor.Camera?.Speed ?? 0f;
            string space = Editor.Objects != null && Editor.Objects.GizmoLocalSpace ? "local" : "global";
            string gm = Editor.Objects?.GizmoModeText ?? "move";
            bool bld = Editor.Mode == EEditorMode.Level && Editor.LevelTool == Editor.ELevelTool.Buildings;
            string obj = Editor.Mode == EEditorMode.Level && !bld ? $"   ·   LMB place/select · drag box-select · Shift multi · {gm} gizmo (T) · Ctrl+C/V dup · Ctrl+B/N align · Del" : "";
            string build = bld && Editor.Buildings != null
                ? $"   ·   {Editor.Buildings.ToolText} · 1-6 preset · drag an edge to resize · Del removes · Esc cancels · {Editor.Buildings.Walls.Count} walls" : "";
            string spawn = Editor.Mode == EEditorMode.Spawns && Editor.Spawns != null ? $"   ·   Tab category · 1=add 2=remove · {Editor.Spawns.ModeText} · ,/. rot · [/] radius · V alt · T type · {Editor.Spawns.Count} spawns" : "";
            string envs = Editor.Mode == EEditorMode.Environment && Editor.Environment != null ? $"   ·   ,/. time · O overcast · {Editor.Environment.ModeText}{(Editor.RoadsEd != null ? $"   ·   {Editor.RoadsEd.ModeText}" : "")}" : "";
            string terr = Editor.Mode == EEditorMode.Terrain && Editor.TerrainEd != null ? $"   ·   LMB raise · Shift+LMB lower · [/] radius · ,/. strength · {Editor.TerrainEd.ModeText}" : "";
            _status.Text = $"{Editor.Mode}   ·   RMB fly · WASD · E/Q up-down · scroll = speed (×{spd:0}){obj}{build}{spawn}{envs}{terr}   ·   map: {Editor.MapName}";
        }
    }
}
