using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The NPC tab (master 2026-09-11: "can just keep em without spawns on the map. add them as a new
    /// map editor tab. place preset/custom npcs").
    ///
    /// SO NO MAP SHIPS A PLACED NPC, AND THAT IS THE DESIGN rather than a gap. Retail puts people down as
    /// ObjectNPCAssets in a map's Objects.dat -- a person is a placement with the row shape of a fence -- and of
    /// every map installed here only Russia does it. Authoring a set for PEI would have been inventing content
    /// and calling it a port. This puts the placing in the editor instead, where a map author decides.
    ///
    /// The markers are REAL NpcCharacters, not capsules with labels. You are placing a person whose hat, face
    /// and skin come out of the same asset the game will build them from, so "who is that and are they facing
    /// the road" is answerable by looking rather than by saving and loading the map.
    ///
    /// ⚠ They sit on their OWN collision layer. An NpcCharacter is normally solid on the world layer, which is
    /// the layer the placement raycast uses to find the ground -- left alone, the first person you placed would
    /// stand between you and everywhere you wanted to put the next one.</summary>
    public partial class EditorNpcs : Node3D
    {
        /// <summary>One placed person. `Key` is a catalog key today and is deliberately a STRING rather than the
        /// numeric id, so a custom character from the NPC editor can be addressed here without the file format
        /// changing under maps that already exist.</summary>
        public struct Placed
        {
            public string Key;
            public Vector3 Pos;
            public float Yaw;
        }

        const uint TerrainLayer = 1u << 0, SmallPropLayer = 1u << 6;
        const uint EditorPickLayer = EditorObjects.PickLayer;
        const uint NpcPickLayer = 1u << 19;   // ours alone: river 1<<12, roaddraw 1<<11, roads 1<<10

        readonly List<Placed> _placed = new();
        readonly List<NpcCharacter> _markers = new();
        readonly List<string> _keys = new();          // catalog keys, sorted -- the palette's order and ours

        readonly Editor _editor;
        readonly Camera3D _cam;
        EditorCamera _flyCam;

        int _pick;                 // which preset the next click places
        int _selected = -1;        // index into _placed, or -1
        float _rotation;           // yaw for the next placement
        Node3D _ghost;             // the pending placement, under the cursor

        public int Count => _placed.Count;
        public string PickKey => _keys.Count > 0 ? _keys[Mathf.Clamp(_pick, 0, _keys.Count - 1)] : "";
        public string PickName => NpcCatalog.CharacterByKey(PickKey)?.Name ?? PickKey;
        public int PickIndex => _pick;
        public IReadOnlyList<string> Keys => _keys;
        public int SelectedIndex => _selected;

        public EditorNpcs(Editor editor, Camera3D cam)
        {
            _editor = editor;
            _cam = cam;
            Name = "EditorNpcs";
        }

        public override void _Ready()
        {
            NpcCatalog.Load();
            foreach (var c in NpcCatalog.Characters) _keys.Add(c.Key);
            _keys.Sort(System.StringComparer.OrdinalIgnoreCase);
            _flyCam = _cam as EditorCamera;   // EditorCamera IS the Camera3D, same as EditorSpawns reads it
            _editor.ModeChanged += _ => RefreshVisibility();
            RefreshVisibility();
            Load();
        }

        void RefreshVisibility()
        {
            Visible = _editor.Mode == EEditorMode.Npcs;
            // The PLACED people stay visible in every tab -- they are part of the map, the way objects are, and
            // a map that looks empty until you click the right tab is a map you will forget has anybody in it.
            // Only the ghost and the selection are tab-scoped, and Visible=false on this node would hide both
            // the markers and them, so the markers are reparented out of it. See EnsureMarkerRoot.
            if (_ghost != null) _ghost.Visible = false;
        }

        Node3D _markerRoot;
        Node3D MarkerRoot => _markerRoot ??= NewMarkerRoot();
        Node3D NewMarkerRoot()
        {
            var n = new Node3D { Name = "PlacedNpcs" };
            (GetParent() ?? (Node)this).AddChild(n);
            return n;
        }

        // ---- files ------------------------------------------------------------------------------------------
        /// <summary>Same shape and the same folder as the spawn translator: an editor overlay keyed by map, never
        /// the source map. A save here cannot damage anything retail shipped.</summary>
        string SavePath => ProjectSettings.GlobalizePath("res://content/spawns/") + $"editor_{_editor.MapName}_npcs.txt";

        public int Save()
        {
            string sp = SavePath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sp));
            using var w = new System.IO.StreamWriter(sp, false);
            foreach (var p in _placed)
                w.WriteLine($"{p.Key} {p.Pos.X:0.###} {p.Pos.Y:0.###} {p.Pos.Z:0.###} {p.Yaw:0.###}");
            Log.Print($"[editor-npcs] saved {_placed.Count} placed NPCs -> {sp}");
            return _placed.Count;
        }

        public void Load()
        {
            _placed.Clear();
            string sp = SavePath;
            if (System.IO.File.Exists(sp))
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var line in System.IO.File.ReadLines(sp))
                {
                    var q = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    if (q.Length < 5) continue;
                    if (!float.TryParse(q[1], System.Globalization.NumberStyles.Float, ci, out float x)) continue;
                    if (!float.TryParse(q[2], System.Globalization.NumberStyles.Float, ci, out float y)) continue;
                    if (!float.TryParse(q[3], System.Globalization.NumberStyles.Float, ci, out float z)) continue;
                    float.TryParse(q[4], System.Globalization.NumberStyles.Float, ci, out float yaw);
                    _placed.Add(new Placed { Key = q[0], Pos = new Vector3(x, y, z), Yaw = yaw });
                }
            }
            RebuildMarkers();
        }

        // ---- markers ----------------------------------------------------------------------------------------
        void RebuildMarkers()
        {
            foreach (var m in _markers) if (GodotObject.IsInstanceValid(m)) m.QueueFree();
            _markers.Clear();
            for (int i = 0; i < _placed.Count; i++) _markers.Add(BuildMarker(_placed[i], i == _selected));
        }

        NpcCharacter BuildMarker(in Placed p, bool selected)
        {
            var def = NpcCatalog.CharacterByKey(p.Key);
            if (def == null) return null;
            var npc = NpcCharacter.Spawn(MarkerRoot, def, p.Pos, p.Yaw);
            if (npc == null) return null;
            // ⚠ OFF THE WORLD LAYER. Solid-on-layer-0 is right in the game and wrong here: that is the layer the
            // ground raycast uses, so a placed person would shadow every click behind them.
            npc.CollisionLayer = NpcPickLayer;
            npc.CollisionMask = 0;
            npc.SetLookFocused(true);   // nameplates always up in the editor: the whole point is knowing who is who
            if (selected) Highlight(npc, true);
            return npc;
        }

        /// <summary>A ring on the ground, not a tint on the body. Tinting means reaching into a RiggedCharacter's
        /// materials, which are SHARED with every other instance wearing that garment -- selecting one chef would
        /// light up all of them.</summary>
        void Highlight(NpcCharacter npc, bool on)
        {
            if (npc == null || !GodotObject.IsInstanceValid(npc)) return;
            var ring = npc.GetNodeOrNull<MeshInstance3D>("SelRing");
            if (!on) { ring?.QueueFree(); return; }
            if (ring != null) return;
            ring = new MeshInstance3D
            {
                Name = "SelRing",
                Mesh = new TorusMesh { InnerRadius = 0.48f, OuterRadius = 0.60f, RingSegments = 24 },
                Position = new Vector3(0f, 0.03f, 0f),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(1f, 0.84f, 0.22f),
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    NoDepthTest = true,
                },
            };
            npc.AddChild(ring);
        }

        void SetSelected(int idx)
        {
            if (_selected >= 0 && _selected < _markers.Count) Highlight(_markers[_selected], false);
            _selected = idx;
            if (_selected >= 0 && _selected < _markers.Count) Highlight(_markers[_selected], true);
        }

        // ---- the ghost under the cursor ---------------------------------------------------------------------
        void EnsureGhost()
        {
            string key = PickKey;
            if (_ghost != null && GodotObject.IsInstanceValid(_ghost) && (string)_ghost.GetMeta("key", "") == key) return;
            _ghost?.QueueFree();
            var def = NpcCatalog.CharacterByKey(key);
            if (def == null) { _ghost = null; return; }
            var g = NpcCharacter.Spawn(this, def, Vector3.Zero, 0f);
            if (g == null) { _ghost = null; return; }
            g.CollisionLayer = 0; g.CollisionMask = 0;   // a preview is not a thing in the world
            g.SetMeta("key", key);
            _ghost = g;
        }

        bool RaycastGround(Vector2 screen, out Vector3 point)
        {
            point = Vector3.Zero;
            if (_cam == null) return false;
            var from = _cam.ProjectRayOrigin(screen);
            var q = new PhysicsRayQueryParameters3D
            {
                From = from,
                To = from + _cam.ProjectRayNormal(screen) * 8000f,
                CollisionMask = TerrainLayer | SmallPropLayer | EditorPickLayer,
            };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            point = (Vector3)hit["position"];
            return true;
        }

        /// <summary>Which placed person is under the cursor, or -1. Its own raycast against its own layer, which
        /// is what lets a click select a person standing on ground that is also clickable.</summary>
        int NpcUnder(Vector2 screen)
        {
            if (_cam == null) return -1;
            var from = _cam.ProjectRayOrigin(screen);
            var q = new PhysicsRayQueryParameters3D
            {
                From = from,
                To = from + _cam.ProjectRayNormal(screen) * 8000f,
                CollisionMask = NpcPickLayer,
            };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return -1;
            var body = hit["collider"].As<GodotObject>() as NpcCharacter;
            return body == null ? -1 : _markers.IndexOf(body);
        }

        public override void _Process(double delta)
        {
            if (_editor.Mode != EEditorMode.Npcs || (_flyCam != null && _flyCam.Flying))
            {
                if (_ghost != null) _ghost.Visible = false;
                return;
            }
            EnsureGhost();
            if (_ghost == null) return;
            if (Editor.PointerOverUI(this) || !RaycastGround(GetViewport().GetMousePosition(), out var pt))
            {
                _ghost.Visible = false;
                return;
            }
            _ghost.Visible = true;
            _ghost.GlobalPosition = pt;
            _ghost.RotationDegrees = new Vector3(0f, _rotation, 0f);
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Npcs || (_flyCam != null && _flyCam.Flying)) return;

            if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && !Editor.PointerOverUI(this))
            {
                var mouse = GetViewport().GetMousePosition();
                // A PERSON UNDER THE CURSOR IS A SELECTION, NOT A PLACEMENT. Otherwise the only way to select
                // somebody is to miss them, and standing two people in a doorway becomes impossible.
                int hit = NpcUnder(mouse);
                if (hit >= 0) { SetSelected(hit); _rotation = _placed[hit].Yaw; return; }
                if (!RaycastGround(mouse, out var pt)) return;
                Add(new Placed { Key = PickKey, Pos = pt, Yaw = _rotation });
                return;
            }

            if (ev is not InputEventKey { Pressed: true, Echo: false } k) return;
            if (Input.IsKeyPressed(Key.Ctrl) && k.Keycode == Key.Z) { _editor.Undo(); return; }
            switch (k.Keycode)
            {
                case Key.Delete or Key.Backspace: RemoveSelected(); break;
                case Key.Comma: Rotate(-15f); break;
                case Key.Period: Rotate(+15f); break;
                case Key.Tab: SetPick(_pick + 1); break;
                case Key.Escape: SetSelected(-1); break;
            }
        }

        void Rotate(float by)
        {
            _rotation = Mathf.Wrap(_rotation + by, 0f, 360f);
            if (_selected < 0 || _selected >= _placed.Count) return;
            var p = _placed[_selected];
            p.Yaw = _rotation;
            _placed[_selected] = p;
            if (_selected < _markers.Count && GodotObject.IsInstanceValid(_markers[_selected]))
                _markers[_selected].RotationDegrees = new Vector3(0f, p.Yaw, 0f);
            _editor.MarkDirty();
        }

        public void SetPick(int i)
        {
            if (_keys.Count == 0) return;
            _pick = ((i % _keys.Count) + _keys.Count) % _keys.Count;
        }

        public void Add(Placed p)
        {
            _placed.Add(p);
            _markers.Add(BuildMarker(p, false));
            SetSelected(_placed.Count - 1);
            _editor.MarkDirty();
            var added = p;
            _editor.PushUndo("place NPC", () =>
            {
                int i = _placed.FindIndex(q => q.Key == added.Key && q.Pos.IsEqualApprox(added.Pos));
                if (i < 0) return;
                RemoveAt(i);
            });
        }

        public void RemoveSelected()
        {
            if (_selected < 0 || _selected >= _placed.Count) return;
            var gone = _placed[_selected];
            int at = _selected;
            RemoveAt(at);
            _editor.PushUndo("remove NPC", () => { _placed.Insert(Mathf.Min(at, _placed.Count), gone); RebuildMarkers(); });
        }

        void RemoveAt(int i)
        {
            if (i < 0 || i >= _placed.Count) return;
            if (_selected == i) SetSelected(-1);
            _placed.RemoveAt(i);
            if (i < _markers.Count)
            {
                if (GodotObject.IsInstanceValid(_markers[i])) _markers[i].QueueFree();
                _markers.RemoveAt(i);
            }
            if (_selected > i) _selected--;
            _editor.MarkDirty();
        }

        /// <summary>How many of each preset are down, for the palette's counts. A tab that cannot tell you
        /// whether you already placed the mechanic is one you place two mechanics with.</summary>
        public int CountOf(string key)
        {
            int n = 0;
            foreach (var p in _placed) if (p.Key == key) n++;
            return n;
        }

        // ---- test seams -------------------------------------------------------------------------------------
        public IReadOnlyList<Placed> DebugPlaced => _placed;
        public void DebugAdd(string key, Vector3 pos, float yaw) => Add(new Placed { Key = key, Pos = pos, Yaw = yaw });
        public int DebugSaveThenReload() { int n = Save(); Load(); return n; }
    }
}
