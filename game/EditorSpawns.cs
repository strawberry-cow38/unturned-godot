using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Spawns sub-editor, ported from SDG.Unturned EditorSpawns. Visualizes the map's spawn points and edits them by
    // clicking the terrain (source ESpawnMode ADD_PLAYER/REMOVE_PLAYER -> LevelPlayers.addSpawn/removeSpawn):
    //   1 = ADD mode, 2 = REMOVE mode (source tool_0 / tool_1);  ',' / '.' rotate the add spawn (source rotation);
    //   '[' / ']' size the remove radius (source radius byte 2..30, the remove cursor is a sphere scaled radius*2);
    //   V toggles player/alt (source selectedAlt).
    // Player spawns = Spawns/Players.dat (u8 ver, u8 count, per point Vector3 + u8 angle*2 + bool isAlt if v>3). This
    // slice = PLAYER spawns; the other four categories (zombie/vehicle/item/animal, each with a spawn TABLE) land next.
    // NOTE: the source cursors are Resources prefabs (Edit/Player, Edit/Remove...); these are close primitive stand-ins
    // (player capsule + facing arrow; radius-scaled sphere) -- the exact editor prefab meshes aren't ripped yet.
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
        Node3D _addCursor, _removeCursor;
        MeshInstance3D _addBody, _addArrow;
        bool _removeMode;
        float _rotation;      // source EditorSpawns.rotation (add-spawn yaw)
        byte _radius = 8;     // source EditorSpawns.radius (byte, 2..30; remove cursor = sphere scaled radius*2)
        bool _alt;            // source selectedAlt (player vs alt spawn)

        public readonly List<Vector3> Positions = new();
        public int PlayerCount { get { int n = 0; foreach (var s in _spawns) if (!s.IsAlt) n++; return n; } }
        public int AltCount => _spawns.Count - PlayerCount;
        public string ModeText => _removeMode ? $"REMOVE (radius {_radius})" : $"add {(_alt ? "ALT" : "player")} @ {_rotation:0}°";

        public EditorSpawns(Editor editor, Camera3D cam, string mapRoot)
        {
            _editor = editor; _cam = cam; _flyCam = cam as EditorCamera; _mapRoot = mapRoot;
            Load();
            RebuildMarkers();
            MakeCursors();
            _editor.ModeChanged += _ => RefreshVisibility();
            RefreshVisibility();
        }

        void RefreshVisibility() { Visible = _editor.Mode == EEditorMode.Spawns; }

        void LoadPlayerSpawns()   // retail Spawns/Players.dat
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

        static string SaveDir => ProjectSettings.GlobalizePath("res://content/spawns/");
        static string SavePath => SaveDir + "editor_players.txt";

        void Load()   // the editor translator (edited state) if present, else the retail Players.dat
        {
            if (!System.IO.File.Exists(SavePath)) { LoadPlayerSpawns(); return; }
            foreach (var line in System.IO.File.ReadLines(SavePath))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5) continue;
                if (float.TryParse(p[0], out var x) && float.TryParse(p[1], out var y) && float.TryParse(p[2], out var z) && float.TryParse(p[3], out var yaw))
                    _spawns.Add(new Spawn { Pos = new Vector3(x, y, z), Yaw = yaw, IsAlt = p[4] != "0" });
            }
            GD.Print($"[editor-spawns] loaded {_spawns.Count} player spawns (editor translator)");
        }

        // Save the edited player spawns to a port-format translator (like the objects editor_PEI.txt). The source
        // persists to binary Spawns/Players.dat, but writing that would clobber the retail install, so keep our own.
        public int Save()
        {
            System.IO.Directory.CreateDirectory(SaveDir);
            using var w = new System.IO.StreamWriter(SavePath, false);
            foreach (var s in _spawns)
                w.WriteLine($"{s.Pos.X:0.###} {s.Pos.Y:0.###} {s.Pos.Z:0.###} {s.Yaw:0.###} {(s.IsAlt ? 1 : 0)}");
            GD.Print($"[editor-spawns] saved {_spawns.Count} player spawns -> {SavePath}");
            return _spawns.Count;
        }

        void RebuildMarkers()
        {
            foreach (var m in _markers) m.QueueFree();
            _markers.Clear(); Positions.Clear();
            foreach (var s in _spawns) { var m = MakeMarker(s.Pos, s.Yaw, s.IsAlt); AddChild(m); _markers.Add(m); Positions.Add(s.Pos); }
        }

        // a post + top ball + facing-arrow cone. Regular = yellow, alt = cyan (source colors the cursor from the table).
        Node3D MakeMarker(Vector3 pos, float yawDeg, bool isAlt)
        {
            var root = new Node3D { Position = pos, RotationDegrees = new Vector3(0, yawDeg, 0) };
            var mat = new StandardMaterial3D { AlbedoColor = isAlt ? new Color(0.2f, 0.85f, 1f) : new Color(1f, 0.86f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.18f, Height = 3.4f }, MaterialOverride = mat, Position = new Vector3(0, 1.7f, 0) });
            root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f }, MaterialOverride = mat, Position = new Vector3(0, 3.6f, 0) });
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.55f, Height = 1.3f }, MaterialOverride = mat, Position = new Vector3(0, 2.4f, 1.0f), RotationDegrees = new Vector3(90, 0, 0) });
            return root;
        }

        void MakeCursors()
        {
            // ADD cursor: a player-height capsule + facing arrow (approximates Resources "Edit/Player"), rotated by _rotation
            _addCursor = new Node3D { Visible = false };
            _addBody = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.4f, Height = 1.9f }, Position = new Vector3(0, 0.95f, 0) };
            _addArrow = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.4f, Height = 1f }, Position = new Vector3(0, 0.95f, 1.0f), RotationDegrees = new Vector3(90, 0, 0) };
            _addCursor.AddChild(_addBody); _addCursor.AddChild(_addArrow);
            AddChild(_addCursor);
            // REMOVE cursor: a translucent unit sphere scaled to the remove radius (source: remove.localScale = radius*2)
            _removeCursor = new Node3D { Visible = false };
            _removeCursor.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 1f, Height = 2f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.3f, 0.3f, 0.22f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } });
            AddChild(_removeCursor);
            UpdateAddCursorColor();
        }

        void UpdateAddCursorColor()
        {
            var mat = new StandardMaterial3D { AlbedoColor = _alt ? new Color(0.2f, 0.85f, 1f, 0.4f) : new Color(1f, 0.86f, 0.1f, 0.4f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            _addBody.MaterialOverride = mat; _addArrow.MaterialOverride = mat;
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
            if (!Visible || (_flyCam != null && _flyCam.Flying) || !RaycastTerrain(GetViewport().GetMousePosition(), out var pt))
            {
                if (_addCursor != null) _addCursor.Visible = false;
                if (_removeCursor != null) _removeCursor.Visible = false;
                return;
            }
            if (_removeMode)
            {
                _removeCursor.Position = pt; _removeCursor.Scale = Vector3.One * _radius; _removeCursor.Visible = true; _addCursor.Visible = false;
            }
            else
            {
                _addCursor.Position = pt; _addCursor.RotationDegrees = new Vector3(0, _rotation, 0); _addCursor.Visible = true; _removeCursor.Visible = false;
            }
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Spawns || (_flyCam != null && _flyCam.Flying)) return;   // Spawns tab only; never while flying (RMB)
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) return;
                if (_removeMode) RemoveNear(pt); else AddSpawn(pt, _rotation, _alt);
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                switch (k.Keycode)
                {
                    case Key.Key1: _removeMode = false; break;                                  // source tool_0 -> ADD
                    case Key.Key2: _removeMode = true; break;                                   // source tool_1 -> REMOVE
                    case Key.V: _alt = !_alt; UpdateAddCursorColor(); break;                    // toggle player/alt (source selectedAlt)
                    case Key.Comma: _rotation = Mathf.Wrap(_rotation - 15f, 0f, 360f); break;   // rotate the add spawn (source rotation)
                    case Key.Period: _rotation = Mathf.Wrap(_rotation + 15f, 0f, 360f); break;
                    case Key.Bracketleft: _radius = (byte)Mathf.Max(2, _radius - 1); break;     // remove radius (source byte 2..30)
                    case Key.Bracketright: _radius = (byte)Mathf.Min(30, _radius + 1); break;
                }
            }
        }

        public void AddSpawn(Vector3 pt, float yaw = 0f, bool isAlt = false)   // source LevelPlayers.addSpawn(point, rotation, selectedAlt)
        {
            _spawns.Add(new Spawn { Pos = pt, Yaw = yaw, IsAlt = isAlt });
            var m = MakeMarker(pt, yaw, isAlt); AddChild(m); _markers.Add(m); Positions.Add(pt);
            GD.Print($"[editor-spawns] added {(isAlt ? "alt" : "player")} spawn @ {yaw:0}deg ({_spawns.Count} total)");
        }

        public void RemoveNear(Vector3 pt)   // source LevelPlayers.removeSpawn(point, radius)
        {
            int removed = 0;
            for (int i = _spawns.Count - 1; i >= 0; i--)
                if (_spawns[i].Pos.DistanceTo(pt) <= _radius) { _spawns.RemoveAt(i); removed++; }
            if (removed > 0) { RebuildMarkers(); GD.Print($"[editor-spawns] removed {removed} ({_spawns.Count} left)"); }
        }
    }
}
