using Godot;

namespace UnturnedGodot
{
    // Editor controller, ported from Unturned's Editor (SDG.Unturned, Edit/Editor.cs). A thin singleton that
    // owns the edit session -- the active mode (which sub-editor is live) + the loaded map -- and fans Save()
    // out to the sub-editors. The heavy lifting lives in per-mode sub-editors (Objects/Terrain/Spawns/...),
    // ported phase by phase. Phase 1 = the shell + mode switching + the free-fly cam + dashboard.
    public enum EEditorMode { Objects, Terrain, Environment, Spawns, Volumes }   // dashboard tabs (source EditorDashboardUI)

    public partial class Editor : Node3D
    {
        public static Editor Instance { get; private set; }
        public string MapName { get; private set; }
        public Node3D World { get; private set; }        // the loaded map (terrain + objects) = the edit target
        public EditorCamera Camera { get; private set; }

        [Signal] public delegate void ModeChangedEventHandler(int mode);

        EEditorMode _mode = EEditorMode.Objects;
        public EEditorMode Mode
        {
            get => _mode;
            set { if (_mode == value) return; _mode = value; EmitSignal(SignalName.ModeChanged, (int)value); }
        }

        public void Setup(string mapName, Node3D world, EditorCamera cam)
        {
            Instance = this; MapName = mapName; World = world; Camera = cam;
        }

        public override void _ExitTree() { if (Instance == this) Instance = null; }

        // source Editor.save() -> EditorInteract.save() + EditorObjects.save() + EditorSpawns.save().
        // wired per-phase as the sub-editors land; Phase 1 has nothing persistent yet.
        public void Save() => GD.Print($"[editor] save '{MapName}' (Phase 1: no sub-editors persist yet)");
    }
}
