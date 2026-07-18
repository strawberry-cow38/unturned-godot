using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Spawns sub-editor (Phase A), ported from SDG.Unturned EditorSpawns. Visualizes the map's spawn points as posts
    // with a facing arrow; the source edits five categories (player/zombie/vehicle/item/animal) with ADD/REMOVE modes
    // (EditorSpawns.spawnMode) clicking the terrain to LevelXxx.addSpawn/removeSpawn. This slice renders PLAYER spawns
    // (Spawns/Players.dat: u8 ver, u8 count, per point Vector3 + u8 angle*2 + bool isAlt if v>3; source LevelPlayers).
    // Add/remove + the other categories + save land next. Shown only on the Spawns dashboard tab.
    public partial class EditorSpawns : Node3D
    {
        readonly Editor _editor;
        readonly string _mapRoot;
        readonly List<Node3D> _markers = new();
        public int PlayerCount, AltCount;
        public readonly List<Vector3> Positions = new();

        public EditorSpawns(Editor editor, string mapRoot)
        {
            _editor = editor; _mapRoot = mapRoot;
            LoadPlayerSpawns();
            _editor.ModeChanged += _ => RefreshVisibility();
            RefreshVisibility();
        }

        void RefreshVisibility() { Visible = _editor.Mode == EEditorMode.Spawns; }

        void LoadPlayerSpawns()
        {
            string ppath = _mapRoot + "/Spawns/Players.dat";
            if (!System.IO.File.Exists(ppath)) { GD.Print("[editor-spawns] no Players.dat"); return; }
            var pd = System.IO.File.ReadAllBytes(ppath); int p = 0;
            byte ver = pd[p++], count = pd[p++];
            for (int i = 0; i < count; i++)
            {
                float x = System.BitConverter.ToSingle(pd, p); p += 4;
                float y = System.BitConverter.ToSingle(pd, p); p += 4;
                float z = System.BitConverter.ToSingle(pd, p); p += 4;
                float ang = pd[p++] * 2f;
                bool isAlt = ver > 3 && pd[p++] != 0;
                AddMarker(new Vector3(x, y, -z), -ang, isAlt);   // port negate-Z layout + negate yaw (source LevelSpawns)
                if (isAlt) AltCount++; else PlayerCount++;
            }
            GD.Print($"[editor-spawns] loaded {PlayerCount} player + {AltCount} alt spawns");
        }

        // a post + a cone pointing along the spawn's facing (+Z after the root yaw). Regular = green, alt = blue.
        void AddMarker(Vector3 pos, float yawDeg, bool isAlt)
        {
            var root = new Node3D { Position = pos, RotationDegrees = new Vector3(0, yawDeg, 0) };
            var mat = new StandardMaterial3D { AlbedoColor = isAlt ? new Color(0.2f, 0.85f, 1f) : new Color(1f, 0.86f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.18f, Height = 3.4f }, MaterialOverride = mat, Position = new Vector3(0, 1.7f, 0) });   // post
            root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f }, MaterialOverride = mat, Position = new Vector3(0, 3.6f, 0) });   // bright top ball (visible from above)
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.55f, Height = 1.3f }, MaterialOverride = mat, Position = new Vector3(0, 2.4f, 1.0f), RotationDegrees = new Vector3(90, 0, 0) });   // facing arrow
            AddChild(root);
            _markers.Add(root);
            Positions.Add(pos);
        }
    }
}
