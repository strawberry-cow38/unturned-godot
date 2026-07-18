using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Spawns sub-editor, ported from SDG.Unturned EditorSpawns. Visualizes + edits the map's spawn points by clicking
    // the terrain (source ESpawnMode ADD/REMOVE per category -> LevelXxx.addSpawn/removeSpawn). Category-aware:
    //   Tab cycles category (Player / Vehicle); 1 = ADD, 2 = REMOVE (source tool_0/tool_1); ',' '.' rotate the spawn
    //   (source rotation); '[' ']' size the remove radius (source radius byte 2..30, sphere scaled radius*2);
    //   V toggles player/alt (source selectedAlt); T cycles the vehicle TABLE/type (source selectedVehicle).
    // Player = Spawns/Players.dat (Vector3 + angle*2 + isAlt). Vehicle = Spawns/Vehicles.dat points (u8 type + Vector3
    // + angle*2; type = table: 0 Civilian 1 Police 2 Fire 3 Military 4 Medic 5 Farm). Zombie/Item/Animal land next.
    // Edits persist to a port translator (content/spawns/editor_<cat>.txt) -- writing the binary .dat would clobber the
    // retail install. NOTE: cursors are close primitive stand-ins for the source Edit/* prefabs (not ripped).
    public partial class EditorSpawns : Node3D
    {
        enum ECategory { Player, Vehicle, Item, Zombie, Animal }
        struct Spawn { public Vector3 Pos; public float Yaw; public bool IsAlt; public int Type; }

        static readonly string[] VehicleTypes = { "Civilian", "Police", "Fire", "Military", "Medic", "Farm" };
        static readonly Color[] VehicleColors = {
            new(0.85f, 0.85f, 0.85f), new(0.25f, 0.4f, 1f), new(1f, 0.25f, 0.2f),
            new(0.3f, 0.75f, 0.3f), new(1f, 0.55f, 0.75f), new(0.85f, 0.6f, 0.25f) };

        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly string _mapRoot;
        const uint TerrainLayer = 1u << 0, SmallPropLayer = 1u << 6, EditorPickLayer = 1u << 7;

        ECategory _category = ECategory.Player;
        readonly List<Spawn> _spawns = new();
        readonly List<Node3D> _markers = new();
        Node3D _addCursor, _removeCursor;
        MeshInstance3D _addBody, _addArrow;
        bool _removeMode;
        float _rotation;
        byte _radius = 8;
        bool _alt;
        int _vehType;   // source selectedVehicle / item table index
        Color[] _tableColors;

        public readonly List<Vector3> Positions = new();
        public int Count => _spawns.Count;
        public int PlayerCount { get { int n = 0; foreach (var s in _spawns) if (!s.IsAlt) n++; return n; } }
        bool IsPointCloud => _category == ECategory.Item || _category == ECategory.Zombie || _category == ECategory.Animal;   // dense -> MultiMesh, no facing
        int TypeCount() => (_category == ECategory.Item || _category == ECategory.Animal) ? Mathf.Max(1, _tableColors?.Length ?? 1) : VehicleTypes.Length;
        public string ModeText
        {
            get
            {
                string cat = _category switch { ECategory.Vehicle => $"Vehicle[{VehicleTypes[Mathf.Clamp(_vehType, 0, 5)]}]", ECategory.Item => $"Item[t{_vehType}]", ECategory.Zombie => "Zombie", ECategory.Animal => $"Animal[t{_vehType}]", _ => "Player" };
                if (_removeMode) return $"{cat} · REMOVE (radius {_radius})";
                string what = _category switch { ECategory.Vehicle => VehicleTypes[Mathf.Clamp(_vehType, 0, 5)], ECategory.Item => $"table {_vehType}", ECategory.Zombie => "zombie", ECategory.Animal => $"table {_vehType}", _ => (_alt ? "ALT" : "player") };
                return $"{cat} · add {what}";
            }
        }

        public EditorSpawns(Editor editor, Camera3D cam, string mapRoot)
        {
            _editor = editor; _cam = cam; _flyCam = cam as EditorCamera; _mapRoot = mapRoot;
            LoadCategory();
            RebuildMarkers();
            MakeCursors();
            _editor.ModeChanged += _ => RefreshVisibility();
            RefreshVisibility();
        }

        void RefreshVisibility() { Visible = _editor.Mode == EEditorMode.Spawns; }

        string TranslatorPath(ECategory c) => ProjectSettings.GlobalizePath("res://content/spawns/") + $"editor_{c.ToString().ToLower()}.txt";

        void LoadCategory()   // the editor translator (edited state) if present, else the retail .dat
        {
            if (_category == ECategory.Item) LoadItemTables();          // Items.dat table colours
            else if (_category == ECategory.Animal) LoadAnimalTables();  // Fauna.dat table colours
            string sp = TranslatorPath(_category);
            if (System.IO.File.Exists(sp)) { LoadTranslator(sp); return; }
            if (_category == ECategory.Player) LoadPlayerSpawns();
            else if (_category == ECategory.Vehicle) LoadVehicleSpawns();
            else if (_category == ECategory.Item) LoadRegionSpawns("Jars.dat");
            else if (_category == ECategory.Zombie) LoadRegionSpawns("Animals.dat");   // PEI zombie spawn points (bucketed into navmesh pockets)
            else LoadAnimalSpawns();   // Animal: Fauna.dat (River format, source LevelAnimals)
        }

        void LoadAnimalTables()   // Fauna.dat table colours (source LevelAnimals: color + name + tableID(u16 if v>2) + tiers)
        {
            string fpath = _mapRoot + "/Spawns/Fauna.dat";
            if (!System.IO.File.Exists(fpath)) { _tableColors = System.Array.Empty<Color>(); return; }
            var fd = System.IO.File.ReadAllBytes(fpath); int fp = 0;
            byte U8() => fd[fp++];
            void RStr() { int n = U8(); fp += n; }
            byte ver = U8(); byte tcount = U8();
            _tableColors = new Color[tcount];
            for (int t = 0; t < tcount; t++)
            {
                byte r = U8(), g = U8(), b = U8();
                _tableColors[t] = new Color(r / 255f, g / 255f, b / 255f);
                RStr(); if (ver > 2) fp += 2;
                byte tiers = U8();
                for (int ti = 0; ti < tiers; ti++) { RStr(); fp += 4; byte sc = U8(); fp += sc * 2; }
            }
        }

        void LoadAnimalSpawns()   // Fauna.dat points (River, source LevelAnimals): skip tables, u16 pointCount, per point u8 type + Vector3 (no angle)
        {
            string fpath = _mapRoot + "/Spawns/Fauna.dat";
            if (!System.IO.File.Exists(fpath)) { GD.Print("[editor-spawns] no Fauna.dat"); return; }
            var fd = System.IO.File.ReadAllBytes(fpath); int fp = 0;
            byte U8() => fd[fp++];
            ushort U16() { var v = System.BitConverter.ToUInt16(fd, fp); fp += 2; return v; }
            void RStr() { int n = U8(); fp += n; }
            byte ver = U8(); byte tcount = U8();
            for (int t = 0; t < tcount; t++) { fp += 3; RStr(); if (ver > 2) fp += 2; byte tiers = U8(); for (int ti = 0; ti < tiers; ti++) { RStr(); fp += 4; byte sc = U8(); fp += sc * 2; } }
            ushort pcount = U16();
            for (int i = 0; i < pcount; i++)
            {
                byte type = U8();
                float px = System.BitConverter.ToSingle(fd, fp); fp += 4;
                float py = System.BitConverter.ToSingle(fd, fp); fp += 4;
                float pz = System.BitConverter.ToSingle(fd, fp); fp += 4;
                _spawns.Add(new Spawn { Pos = new Vector3(px, py, -pz), Type = type });   // authored Y, port negate-Z
            }
            GD.Print($"[editor-spawns] loaded {_spawns.Count} animal spawns");
        }

        void LoadItemTables()   // Spawns/Items.dat table colours (source LevelItems tables)
        {
            string ipath = _mapRoot + "/Spawns/Items.dat";
            if (!System.IO.File.Exists(ipath)) { _tableColors = System.Array.Empty<Color>(); return; }
            var b = System.IO.File.ReadAllBytes(ipath); int o = 0;
            byte U8() => b[o++];
            void RStr() { int n = U8(); o += n; }
            byte ver = U8();
            if (ver > 1 && ver < 3) o += 8;
            byte tcount = U8();
            _tableColors = new Color[tcount];
            for (int t = 0; t < tcount; t++)
            {
                byte r = U8(), g = U8(), bl = U8();
                _tableColors[t] = new Color(r / 255f, g / 255f, bl / 255f);
                RStr(); if (ver > 3) o += 2;
                byte tiers = U8();
                for (int ti = 0; ti < tiers; ti++) { RStr(); o += 4; byte sc = U8(); o += sc * 2; }
            }
        }

        void LoadRegionSpawns(string file)   // Jars.dat / Animals.dat: byte ver, 64x64 regions each [u16 count, count x (u8 type + Vector3)]
        {
            string path = _mapRoot + "/Spawns/" + file;
            if (!System.IO.File.Exists(path)) { GD.Print($"[editor-spawns] no {file}"); return; }
            var b = System.IO.File.ReadAllBytes(path); int o = 0;
            byte version = b[o++];
            if (version == 0) return;
            for (int x = 0; x < 64; x++) for (int y = 0; y < 64; y++)
            {
                ushort count = System.BitConverter.ToUInt16(b, o); o += 2;
                for (int i = 0; i < count; i++)
                {
                    byte type = b[o++];
                    float px = System.BitConverter.ToSingle(b, o); o += 4;
                    float py = System.BitConverter.ToSingle(b, o); o += 4;
                    float pz = System.BitConverter.ToSingle(b, o); o += 4;
                    _spawns.Add(new Spawn { Pos = new Vector3(px, py, -pz), Type = type });   // authored Y, port negate-Z
                }
            }
            GD.Print($"[editor-spawns] loaded {_spawns.Count} {_category} spawns");
        }

        void LoadTranslator(string sp)
        {
            foreach (var line in System.IO.File.ReadLines(sp))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5) continue;
                if (float.TryParse(p[0], out var x) && float.TryParse(p[1], out var y) && float.TryParse(p[2], out var z) && float.TryParse(p[3], out var yaw))
                    _spawns.Add(new Spawn { Pos = new Vector3(x, y, z), Yaw = yaw, IsAlt = p[4] == "1", Type = p.Length > 5 && int.TryParse(p[5], out var t) ? t : 0 });
            }
            GD.Print($"[editor-spawns] loaded {_spawns.Count} {_category} spawns (editor translator)");
        }

        void LoadPlayerSpawns()   // Spawns/Players.dat: u8 ver, u8 count, per point Vector3 + u8 angle*2 + bool isAlt if v>3
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

        void LoadVehicleSpawns()   // Spawns/Vehicles.dat: header (tables) then u16 pointCount, per point u8 type + Vector3(skip y) + u8 angle*2
        {
            string vpath = _mapRoot + "/Spawns/Vehicles.dat";
            if (!System.IO.File.Exists(vpath)) { GD.Print("[editor-spawns] no Vehicles.dat"); return; }
            var vd = System.IO.File.ReadAllBytes(vpath); int vp = 0;
            byte U8() => vd[vp++];
            ushort U16() { var v = System.BitConverter.ToUInt16(vd, vp); vp += 2; return v; }
            void RStr() { int n = U8(); vp += n; }
            byte ver = U8();
            if (ver > 1 && ver < 3) vp += 8;   // SteamID
            byte tcount = U8();
            for (int t = 0; t < tcount; t++) { vp += 3; RStr(); if (ver > 3) vp += 2; byte tiers = U8(); for (int ti = 0; ti < tiers; ti++) { RStr(); vp += 4; byte sc = U8(); vp += sc * 2; } }
            ushort pcount = U16();
            for (int i = 0; i < pcount; i++)
            {
                byte type = U8();
                float px = System.BitConverter.ToSingle(vd, vp); vp += 4; vp += 4; float pz = System.BitConverter.ToSingle(vd, vp); vp += 4;   // x, skip y, z
                float ang = U8() * 2f;
                if (type > 5) continue;   // air/water/tank (6-11) not modelled
                float gz = -pz;
                _spawns.Add(new Spawn { Pos = new Vector3(px, RaycastDown(px, gz), gz), Yaw = -ang + 180f, Type = type });   // vehicle facing (master: +180 vs the player -ang convention)
            }
            GD.Print($"[editor-spawns] loaded {_spawns.Count} vehicle spawns");
        }

        float RaycastDown(float x, float z)   // vehicle .dat skips point.y; sample the terrain height here
        {
            var q = new PhysicsRayQueryParameters3D { From = new Vector3(x, 800f, z), To = new Vector3(x, -400f, z), CollisionMask = TerrainLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            return hit.Count > 0 ? ((Vector3)hit["position"]).Y : 0f;
        }

        public int Save()   // Editor.Save() fan-out: persist the live category (source Editor.save -> EditorSpawns.save)
        {
            string sp = TranslatorPath(_category);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sp));
            using var w = new System.IO.StreamWriter(sp, false);
            foreach (var s in _spawns)
                w.WriteLine($"{s.Pos.X:0.###} {s.Pos.Y:0.###} {s.Pos.Z:0.###} {s.Yaw:0.###} {(s.IsAlt ? 1 : 0)} {s.Type}");
            GD.Print($"[editor-spawns] saved {_spawns.Count} {_category} spawns -> {sp}");
            return _spawns.Count;
        }

        Color MarkerColor(in Spawn s)
        {
            if (_category == ECategory.Vehicle) return VehicleColors[Mathf.Clamp(s.Type, 0, 5)];
            if (_category == ECategory.Item || _category == ECategory.Animal) return _tableColors != null && s.Type < _tableColors.Length ? _tableColors[s.Type] : new Color(0.7f, 0.55f, 0.35f);
            if (_category == ECategory.Zombie) return new Color(0.55f, 0.15f, 0.6f);   // zombie = purple
            return s.IsAlt ? new Color(0.2f, 0.85f, 1f) : new Color(1f, 0.86f, 0.1f);
        }

        void RebuildMarkers()
        {
            foreach (var m in _markers) m.QueueFree();
            _markers.Clear(); Positions.Clear();
            if (IsPointCloud)   // dense cloud (item 2470 / zombie ~1456) -> one MultiMesh, per-instance colour
            {
                Mesh cloud; float yoff;
                if (_category == ECategory.Zombie) { cloud = new BoxMesh { Size = new Vector3(0.65f, 1.9f, 0.65f) }; yoff = 0.95f; }        // human-height block
                else if (_category == ECategory.Animal) { cloud = new BoxMesh { Size = new Vector3(1.1f, 0.8f, 1.9f) }; yoff = 0.4f; }       // low quadruped body
                else { cloud = new BoxMesh { Size = new Vector3(0.9f, 0.9f, 0.9f) }; yoff = 0.45f; }                                         // item cube
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = cloud, InstanceCount = _spawns.Count };
                for (int i = 0; i < _spawns.Count; i++)
                {
                    mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, _spawns[i].Pos + Vector3.Up * yoff));
                    mm.SetInstanceColor(i, MarkerColor(_spawns[i]));
                    Positions.Add(_spawns[i].Pos);
                }
                var mmi = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
                AddChild(mmi); _markers.Add(mmi);
                return;
            }
            foreach (var s in _spawns) { var m = MakeMarker(s.Pos, s.Yaw, MarkerColor(s)); AddChild(m); _markers.Add(m); Positions.Add(s.Pos); }
        }

        Node3D MakeMarker(Vector3 pos, float yawDeg, Color col)
        {
            var root = new Node3D { Position = pos, RotationDegrees = new Vector3(0, yawDeg, 0) };
            var mat = new StandardMaterial3D { AlbedoColor = col, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            if (_category == ECategory.Vehicle)
            {
                // block-shaped (car footprint) + a forward arrow -- source Edit/Vehicle = a box with an "Arrow" child
                root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.2f, 1.1f, 4.8f) }, MaterialOverride = mat, Position = new Vector3(0, 0.55f, 0) });
                root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.8f, Height = 1.8f }, MaterialOverride = mat, Position = new Vector3(0, 0.55f, 3.2f), RotationDegrees = new Vector3(90, 0, 0) });
            }
            else
            {
                root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.18f, Height = 3.4f }, MaterialOverride = mat, Position = new Vector3(0, 1.7f, 0) });
                root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f }, MaterialOverride = mat, Position = new Vector3(0, 3.6f, 0) });
                root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.55f, Height = 1.3f }, MaterialOverride = mat, Position = new Vector3(0, 2.4f, 1.0f), RotationDegrees = new Vector3(90, 0, 0) });
            }
            return root;
        }

        void MakeCursors()
        {
            _addCursor = new Node3D { Visible = false };
            _addBody = new MeshInstance3D(); _addArrow = new MeshInstance3D();
            _addCursor.AddChild(_addBody); _addCursor.AddChild(_addArrow);
            AddChild(_addCursor);
            ShapeAddCursor();
            _removeCursor = new Node3D { Visible = false };
            _removeCursor.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 1f, Height = 2f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.3f, 0.3f, 0.22f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } });
            AddChild(_removeCursor);
            UpdateAddCursorColor();
        }

        void ShapeAddCursor()   // add cursor matches the category (vehicle block vs player capsule), both with a forward arrow
        {
            if (_category == ECategory.Vehicle)
            {
                _addBody.Mesh = new BoxMesh { Size = new Vector3(2.2f, 1.1f, 4.8f) }; _addBody.Position = new Vector3(0, 0.55f, 0);
                _addArrow.Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.8f, Height = 1.8f }; _addArrow.Position = new Vector3(0, 0.55f, 3.2f);
            }
            else if (IsPointCloud)
            {
                if (_category == ECategory.Zombie) { _addBody.Mesh = new BoxMesh { Size = new Vector3(0.65f, 1.9f, 0.65f) }; _addBody.Position = new Vector3(0, 0.95f, 0); }
                else if (_category == ECategory.Animal) { _addBody.Mesh = new BoxMesh { Size = new Vector3(1.1f, 0.8f, 1.9f) }; _addBody.Position = new Vector3(0, 0.4f, 0); }
                else { _addBody.Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.9f, 0.9f) }; _addBody.Position = new Vector3(0, 0.45f, 0); }
                _addArrow.Mesh = null;   // item/zombie/animal spawns are points -- no facing arrow
            }
            else
            {
                _addBody.Mesh = new CapsuleMesh { Radius = 0.4f, Height = 1.9f }; _addBody.Position = new Vector3(0, 0.95f, 0);
                _addArrow.Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.4f, Height = 1f }; _addArrow.Position = new Vector3(0, 0.95f, 1.0f);
            }
            _addArrow.RotationDegrees = new Vector3(90, 0, 0);
        }

        void UpdateAddCursorColor()
        {
            Color c = _category switch {
                ECategory.Vehicle => VehicleColors[Mathf.Clamp(_vehType, 0, 5)],
                ECategory.Item or ECategory.Animal => (_tableColors != null && _vehType < _tableColors.Length ? _tableColors[_vehType] : new Color(0.7f, 0.55f, 0.35f)),
                ECategory.Zombie => new Color(0.55f, 0.15f, 0.6f),
                _ => (_alt ? new Color(0.2f, 0.85f, 1f) : new Color(1f, 0.86f, 0.1f)) };
            c.A = 0.4f;
            var mat = new StandardMaterial3D { AlbedoColor = c, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
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
            if (_removeMode) { _removeCursor.Position = pt; _removeCursor.Scale = Vector3.One * _radius; _removeCursor.Visible = true; _addCursor.Visible = false; }
            else { _addCursor.Position = pt; _addCursor.RotationDegrees = new Vector3(0, _rotation, 0); _addCursor.Visible = true; _removeCursor.Visible = false; }
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Spawns || (_flyCam != null && _flyCam.Flying)) return;
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) return;
                if (_removeMode) RemoveNear(pt); else AddSpawn(pt, _rotation, _alt, _vehType);
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                switch (k.Keycode)
                {
                    case Key.Tab: SwitchCategory(); break;                                      // cycle category
                    case Key.Key1: _removeMode = false; break;                                  // tool_0 -> ADD
                    case Key.Key2: _removeMode = true; break;                                   // tool_1 -> REMOVE
                    case Key.V: _alt = !_alt; UpdateAddCursorColor(); break;                    // player/alt (source selectedAlt)
                    case Key.T: _vehType = (_vehType + 1) % TypeCount(); UpdateAddCursorColor(); break;   // cycle table/type (source selectedVehicle/selectedItem)
                    case Key.Comma: _rotation = Mathf.Wrap(_rotation - 15f, 0f, 360f); break;   // rotate (source rotation)
                    case Key.Period: _rotation = Mathf.Wrap(_rotation + 15f, 0f, 360f); break;
                    case Key.Bracketleft: _radius = (byte)Mathf.Max(2, _radius - 1); break;     // remove radius (source byte 2..30)
                    case Key.Bracketright: _radius = (byte)Mathf.Min(30, _radius + 1); break;
                }
            }
        }

        void SwitchCategory()
        {
            Save();                                                     // persist the current category before switching
            _category = (ECategory)(((int)_category + 1) % 5);          // Player -> Vehicle -> Item -> Zombie -> Animal -> ...
            _vehType = 0;
            _spawns.Clear();
            LoadCategory();
            RebuildMarkers();
            ShapeAddCursor();
            UpdateAddCursorColor();
            GD.Print($"[editor-spawns] category -> {_category} ({_spawns.Count})");
        }

        public void AddSpawn(Vector3 pt, float yaw = 0f, bool isAlt = false, int type = 0)   // source LevelXxx.addSpawn(point, rotation, ...)
        {
            var s = new Spawn { Pos = pt, Yaw = yaw, IsAlt = isAlt, Type = type };
            _spawns.Add(s);
            if (IsPointCloud) RebuildMarkers();   // point-cloud MultiMesh rebuild
            else { var m = MakeMarker(pt, yaw, MarkerColor(s)); AddChild(m); _markers.Add(m); Positions.Add(pt); }
            GD.Print($"[editor-spawns] added {_category} spawn ({_spawns.Count} total)");
        }

        public void RemoveNear(Vector3 pt)   // source LevelXxx.removeSpawn(point, radius)
        {
            int removed = 0;
            for (int i = _spawns.Count - 1; i >= 0; i--)
                if (_spawns[i].Pos.DistanceTo(pt) <= _radius) { _spawns.RemoveAt(i); removed++; }
            if (removed > 0) { RebuildMarkers(); GD.Print($"[editor-spawns] removed {removed} ({_spawns.Count} left)"); }
        }

        // harness (--editor UG_EDITORSPAWNS): cycle to a category so a render shows its markers
        public void DemoGoItem() { int g = 0; while (_category != ECategory.Item && g++ < 6) SwitchCategory(); }
        public void DemoGoZombie() { int g = 0; while (_category != ECategory.Zombie && g++ < 6) SwitchCategory(); }
        public void DemoGoAnimal() { int g = 0; while (_category != ECategory.Animal && g++ < 6) SwitchCategory(); }
    }
}
