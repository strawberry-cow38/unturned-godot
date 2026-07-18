using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Spawns sub-editor, ported from SDG.Unturned EditorSpawns. Visualizes the map's spawn points as posts with a
    // facing arrow, and edits them by clicking the terrain: ADD drops a spawn, REMOVE deletes spawns within a radius
    // (source ESpawnMode ADD_PLAYER / REMOVE_PLAYER -> LevelPlayers.addSpawn/removeSpawn). R toggles add/remove.
    // Player spawns come from Spawns/Players.dat (u8 ver, u8 count, per point Vector3 + u8 angle*2 + bool isAlt if v>3).
    // This slice = PLAYER spawns; the other four categories (zombie/vehicle/item/animal) + save land next. Shown only
    // on the Spawns dashboard tab.
    public partial class EditorSpawns : Node3D
    {
        struct Spawn { public Vector3 Pos; public float Yaw; public bool IsAlt; }

        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly string _mapRoot;
        const uint TerrainLayer = 1u << 0, SmallPropLayer = 1u << 6, EditorPickLayer = 1u << 7;

        readonly List<Spawn> _spawns = new();
        readonly List<Node3D> _markers = new();
        Node3D _cursor;
        bool _removeMode;
        const float RemoveRadius = 12f;

        public readonly List<Vector3> Positions = new();   // marker positions (for the render hook / dashboard)
        public int PlayerCount { get { int n = 0; foreach (var s in _spawns) if (!s.IsAlt) n++; return n; } }
        public int AltCount => _spawns.Count - PlayerCount;
        public bool RemoveMode => _removeMode;

        public EditorSpawns(Editor editor, Camera3D cam, string mapRoot)
        {
            _editor = editor; _cam = cam; _flyCam = cam as EditorCamera; _mapRoot = mapRoot;
            LoadPlayerSpawns();
            RebuildMarkers();
            MakeCursor();
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
                _spawns.Add(new Spawn { Pos = new Vector3(x, y, -z), Yaw = -ang, IsAlt = isAlt });   // port negate-Z + negate yaw
            }
            GD.Print($"[editor-spawns] loaded {_spawns.Count} player spawns ({PlayerCount} regular)");
        }

        void RebuildMarkers()
        {
            foreach (var m in _markers) m.QueueFree();
            _markers.Clear(); Positions.Clear();
            foreach (var s in _spawns) { var m = MakeMarker(s.Pos, s.Yaw, s.IsAlt); AddChild(m); _markers.Add(m); Positions.Add(s.Pos); }
        }

        // a post + a top ball + a cone pointing along the spawn's facing (+Z after the root yaw). Regular = yellow, alt = cyan.
        Node3D MakeMarker(Vector3 pos, float yawDeg, bool isAlt)
        {
            var root = new Node3D { Position = pos, RotationDegrees = new Vector3(0, yawDeg, 0) };
            var mat = new StandardMaterial3D { AlbedoColor = isAlt ? new Color(0.2f, 0.85f, 1f) : new Color(1f, 0.86f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.18f, Height = 3.4f }, MaterialOverride = mat, Position = new Vector3(0, 1.7f, 0) });
            root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f }, MaterialOverride = mat, Position = new Vector3(0, 3.6f, 0) });
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.55f, Height = 1.3f }, MaterialOverride = mat, Position = new Vector3(0, 2.4f, 1.0f), RotationDegrees = new Vector3(90, 0, 0) });   // facing arrow
            return root;
        }

        void MakeCursor()
        {
            _cursor = new Node3D { Visible = false };
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 1f, 0.4f, 0.5f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            _cursor.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.22f, Height = 3.4f }, MaterialOverride = mat, Position = new Vector3(0, 1.7f, 0) });
            AddChild(_cursor);
        }

        bool RaycastTerrain(Vector2 screen, out Vector3 point)
        {
            point = Vector3.Zero;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 8000f;
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = TerrainLayer | SmallPropLayer | EditorPickLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            point = (Vector3)hit["position"]; return true;
        }

        public override void _Process(double d)
        {
            if (!Visible || (_flyCam != null && _flyCam.Flying)) { if (_cursor != null) _cursor.Visible = false; return; }
            if (RaycastTerrain(GetViewport().GetMousePosition(), out var pt))
            {
                _cursor.Position = pt; _cursor.Visible = true;
                if (_cursor.GetChild(0) is MeshInstance3D mi && mi.MaterialOverride is StandardMaterial3D m)
                    m.AlbedoColor = _removeMode ? new Color(1f, 0.3f, 0.3f, 0.5f) : new Color(0.4f, 1f, 0.4f, 0.5f);   // red = remove, green = add
            }
            else _cursor.Visible = false;
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Spawns || (_flyCam != null && _flyCam.Flying)) return;   // Spawns tab only; never while flying (RMB)
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) return;
                if (_removeMode) RemoveNear(pt); else AddSpawn(pt);
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k && k.Keycode == Key.R)
            {
                _removeMode = !_removeMode; GD.Print($"[editor-spawns] mode = {(_removeMode ? "REMOVE" : "ADD")}");
            }
        }

        public void AddSpawn(Vector3 pt)   // source LevelPlayers.addSpawn(point, rotation, selectedAlt); rotation/alt UI = a later slice
        {
            _spawns.Add(new Spawn { Pos = pt, Yaw = 0f, IsAlt = false });
            var m = MakeMarker(pt, 0f, false); AddChild(m); _markers.Add(m); Positions.Add(pt);
            GD.Print($"[editor-spawns] added spawn ({_spawns.Count} total)");
        }

        public void RemoveNear(Vector3 pt)   // source LevelPlayers.removeSpawn(point, radius)
        {
            int removed = 0;
            for (int i = _spawns.Count - 1; i >= 0; i--)
                if (_spawns[i].Pos.DistanceTo(pt) <= RemoveRadius) { _spawns.RemoveAt(i); removed++; }
            if (removed > 0) { RebuildMarkers(); GD.Print($"[editor-spawns] removed {removed} ({_spawns.Count} left)"); }
        }
    }
}
