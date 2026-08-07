using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot
{
    // The building tool: draw walls, drop openings on them, drag and scale the openings.
    //
    // Everything the mouse does resolves to a 2D point in the wall's own plane and stays there -- 3D happens
    // only at the projection. That is what lets clamping and snapping be exact rather than approximate.
    //
    // There is NO preview object. Placing an opening regenerates the real wall with the hole in it as you
    // hover; committing appends it to the list and rebuilds, which is visually a no-op. That is the whole
    // definition of "no bake step", and it is why a ghost can never disagree with a result here.
    public partial class EditorBuildings : Node3D
    {
        public bool Active;                    // true only while the Buildings mode is open
        Editor _editor;
        Camera3D _cam;
        EditorCamera _flyCam;

        readonly List<WallSurface> _walls = new();
        readonly Dictionary<Rid, WallSurface> _pickToWall = new();

        WallSurface _selWall;
        int _selOpening = -1;
        int _armed = -1;                       // archetype index armed for placement, -1 = none

        // drag state
        public enum Drag { None, Move, EdgeU0, EdgeU1, EdgeV0, EdgeV1 }
        Drag _drag = Drag.None;
        float _grabDU, _grabDV;
        WallOpening[] _dragCapture;

        Node3D _handles;
        const float HandlePx = 14f, SnapPx = 12f;

        /// <summary>Archetypes are PRESETS of the same struct, never distinct types -- picking "garage" sets two
        /// numbers. Sizes are the measured retail values; they are defaults you drag away from, not law.</summary>
        public struct Archetype
        {
            public string Name; public float Width, Height, Sill; public bool FloorPinned;
            public Archetype(string n, float w, float h, float sill, bool pinned)
            { Name = n; Width = w; Height = h; Sill = sill; FloorPinned = pinned; }
        }

        public static readonly Archetype[] Archetypes =
        {
            new("door",    2.5f,  WallOpenings.DoorHeight - 0.5f, 0f,   true),
            new("window",  3.31f, WallOpenings.WindowHeight,      WallOpenings.WindowSill, false),
            new("tall win",2.81f, 2.97f,                          0.88f, false),
            new("garage",  8.0f,  WallOpenings.DoorHeight - 0.25f, 0f,  true),
            new("porch",   5.5f,  WallOpenings.DoorHeight,        0f,   true),
            new("vent",    1.0f,  1.0f,                           2.5f, false),
        };

        public void Setup(Editor editor, Camera3D cam, EditorCamera flyCam)
        {
            _editor = editor; _cam = cam; _flyCam = flyCam;
            _handles = new Node3D { Name = "Handles" };
            AddChild(_handles);
            Load();
        }

        public IReadOnlyList<WallSurface> Walls => _walls;
        public WallSurface SelectedWall => _selWall;
        public int SelectedOpening => _selOpening;
        public void Arm(int archetype) => _armed = archetype;

        /// <summary>The palette new walls are drawn with. A retail "material" is only a choice of eight flat
        /// colours -- there are no textures on these buildings -- so this is an index, not an asset.</summary>
        public int ActiveMaterial;

        /// <summary>Thickness new walls start at. 0.70 exterior / 0.50 partition are both measured off retail,
        /// and neither is law -- the point of the slider is that you find out by looking.</summary>
        public float NewWallThickness = WallOpenings.DefaultThickness;

        /// <summary>Click-click wall laying. Armed from the panel; the first click drops a wall and the second
        /// commits it.</summary>
        public bool WallDrawMode;

        // The wall being laid is a REAL WallSurface being resized under the cursor, not a ghost that is later
        // swapped for the real thing. Same reason the openings have no preview object: two representations of
        // one wall is two chances to disagree, and this repo already shipped a barricade whose ghost lay on
        // its side while the placed object stood upright.
        WallSurface _drawing;
        Vector3 _drawAnchor;

        public bool Drawing => _drawing != null;
        public string ToolText => WallDrawMode ? (_drawing != null ? "drawing wall — click to finish, Esc cancels" : "wall: click to start")
                                : _armed >= 0 ? $"placing {Archetypes[Mathf.PosMod(_armed, Archetypes.Length)].Name}"
                                : "select";

        public void SetMaterial(WallSurface w, int id)
        {
            if (w == null) return;
            int before = w.MaterialId;
            w.MaterialId = Mathf.PosMod(id, Mathf.Max(1, WallMaterials.Count));
            w.Rebuild();
            _editor?.PushUndo("wall material", () => { if (IsInstanceValid(w)) { w.MaterialId = before; w.Rebuild(); } });
        }

        /// <summary>Step the palette of the selected wall, or of every wall when nothing is selected -- a
        /// building is normally one material, so recolouring all of it is the common case.</summary>
        public void CycleMaterial(int delta)
        {
            if (WallMaterials.Count == 0) return;
            if (_selWall != null) { SetMaterial(_selWall, _selWall.MaterialId + delta); ActiveMaterial = _selWall.MaterialId; return; }
            ActiveMaterial = Mathf.PosMod(ActiveMaterial + delta, WallMaterials.Count);
            foreach (var w in _walls) { w.MaterialId = ActiveMaterial; w.Rebuild(); }
        }

        // ---- public seams, driven by tests as well as by the mouse ------------------------------------

        public WallSurface AddWall(Vector3 origin, float yawDeg, float length)
        {
            var w = SpawnWall(origin, yawDeg, length, NewWallThickness, ActiveMaterial, null);
            _editor?.PushUndo("wall place", () => RemoveWall(w));
            return w;
        }

        /// <summary>Build a wall and register it, WITHOUT touching the undo stack. Undo actions run through
        /// here: a restore that called AddWall would push a fresh entry onto the stack it is being replayed
        /// from.</summary>
        WallSurface SpawnWall(Vector3 origin, float yawDeg, float length, float thickness, int material,
                              IReadOnlyList<WallOpening> openings, float height = WallOpenings.DoorHeight)
        {
            // wall runs snap to the lattice so drawn walls stay interoperable with anything built on the
            // structures grid; free-length walls would drift off it for no gain
            float snapped = Mathf.Max(WallOpenings.LatticeStep,
                Mathf.Round(length / WallOpenings.LatticeStep) * WallOpenings.LatticeStep);
            var w = new WallSurface
            {
                Length = snapped, Height = height, Thickness = thickness, MaterialId = material,
                Position = origin, RotationDegrees = new Vector3(0f, yawDeg, 0f),
            };
            AddChild(w);
            if (openings != null) { w.Openings.AddRange(openings); w.Rebuild(); }
            _walls.Add(w);
            _pickToWall[w.BodyRid] = w;
            return w;
        }

        /// <summary>Delete a wall and make it come back on undo. It used to push an EMPTY undo action, which
        /// is worse than pushing none: the step is consumed, so Ctrl+Z looks like it fired and did nothing,
        /// and the wall is gone for good. A wall is only data, so undo rebuilds it from a snapshot.</summary>
        public void DeleteWall(WallSurface w)
        {
            if (w == null || !IsInstanceValid(w)) return;
            Vector3 pos = w.Position, rot = w.RotationDegrees;
            float len = w.Length, th = w.Thickness, h = w.Height;
            int mat = w.MaterialId;
            var ops = w.Openings.ToArray();
            RemoveWall(w);
            _editor?.PushUndo("wall delete", () => SpawnWall(pos, rot.Y, len, th, mat, ops, h));
        }

        public void RemoveWall(WallSurface w)
        {
            if (w == null || !IsInstanceValid(w)) return;
            _pickToWall.Remove(w.BodyRid);
            _walls.Remove(w);
            if (_selWall == w) { _selWall = null; _selOpening = -1; }
            w.QueueFree();
        }

        /// <summary>Add an opening at a wall-space point, clamped and snapped. Returns its index, or -1.</summary>
        public int AddOpening(WallSurface w, float u, float v, int archetype)
        {
            if (w == null) return -1;
            var a = Archetypes[Mathf.PosMod(archetype, Archetypes.Length)];
            var o = new WallOpening(u - a.Width * 0.5f, a.FloorPinned ? 0f : v - a.Height * 0.5f,
                                    a.Width, a.Height, 999f, archetype);
            o = WallOpenings.Clamp(o, w.Length, w.Height, w.Openings);
            var before = w.Openings.ToArray();
            w.Openings.Add(o);
            w.Rebuild();
            _editor?.PushUndo("opening add", () => Restore(w, before));
            return w.Openings.Count - 1;
        }

        void Restore(WallSurface w, WallOpening[] snapshot)
        {
            if (w == null || !IsInstanceValid(w)) return;
            w.Openings.Clear();
            w.Openings.AddRange(snapshot);
            w.Rebuild();
            if (_selOpening >= w.Openings.Count) _selOpening = -1;
            PositionHandles();
        }

        /// <summary>Move an opening so its CENTRE lands at (u,v), clamped against the wall and its siblings and
        /// snapped to the measured targets. Never refuses -- an out-of-range drag lands flush.</summary>
        public void MoveOpening(WallSurface w, int index, float u, float v, float snapTol)
        {
            if (w == null || index < 0 || index >= w.Openings.Count) return;
            var o = w.Openings[index];
            var a = Archetypes[Mathf.PosMod(o.Archetype, Archetypes.Length)];
            o.U = u - o.Width * 0.5f;
            o.V = a.FloorPinned ? 0f : v - o.Height * 0.5f;
            if (!a.FloorPinned)
            {
                o.V = WallOpenings.Snap(o.V, WallOpenings.SillHeights, snapTol);
                float head = WallOpenings.Snap(o.V + o.Height, WallOpenings.HeadHeights, snapTol);
                o.V = head - o.Height;
            }
            o = WallOpenings.Clamp(o, w.Length, w.Height, w.Openings, index);
            w.Openings[index] = o;
            w.Rebuild();
        }

        /// <summary>Drag one edge; the opposite edge stays put. Widths snap to the measured ladder.</summary>
        public void DragEdge(WallSurface w, int index, Drag edge, float u, float v, float snapTol)
        {
            if (w == null || index < 0 || index >= w.Openings.Count) return;
            var o = w.Openings[index];
            float min = WallOpenings.MinOpening;
            switch (edge)
            {
                case Drag.EdgeU0:
                {
                    float right = o.U1;
                    float width = WallOpenings.Snap(right - u, WallOpenings.Widths, snapTol);
                    o.Width = Mathf.Max(min, Mathf.Min(width, right));
                    o.U = right - o.Width;
                    break;
                }
                case Drag.EdgeU1:
                {
                    float width = WallOpenings.Snap(u - o.U, WallOpenings.Widths, snapTol);
                    o.Width = Mathf.Max(min, Mathf.Min(width, w.Length - o.U));
                    break;
                }
                case Drag.EdgeV0:
                {
                    float top = o.V1;
                    float sill = WallOpenings.Snap(v, WallOpenings.SillHeights, snapTol);
                    o.V = Mathf.Clamp(sill, 0f, top - min);
                    o.Height = top - o.V;
                    break;
                }
                case Drag.EdgeV1:
                {
                    float head = WallOpenings.Snap(v, WallOpenings.HeadHeights, snapTol);
                    o.Height = Mathf.Clamp(head - o.V, min, w.Height - o.V);
                    break;
                }
            }
            o = WallOpenings.Clamp(o, w.Length, w.Height, w.Openings, index);
            w.Openings[index] = o;
            w.Rebuild();
        }

        public void DeleteOpening(WallSurface w, int index)
        {
            if (w == null || index < 0 || index >= w.Openings.Count) return;
            var before = w.Openings.ToArray();
            w.Openings.RemoveAt(index);
            w.Rebuild();
            if (_selOpening == index) _selOpening = -1;
            _editor?.PushUndo("opening delete", () => Restore(w, before));
            PositionHandles();
        }

        // ---- screen-space snap tolerance --------------------------------------------------------------

        /// <summary>Tolerance in METRES that means a fixed number of PIXELS at this wall's distance. A fixed
        /// world tolerance feels glue-y zoomed out and useless zoomed in; this feels identical at any zoom.</summary>
        float SnapTolerance(WallSurface w, float u, float v)
        {
            if (_cam == null || w == null) return 0.05f;
            var a = _cam.UnprojectPosition(w.UVToWorld(u, v));
            var b = _cam.UnprojectPosition(w.UVToWorld(u + 1f, v));
            float px = a.DistanceTo(b);
            return px < 0.01f ? 0.05f : SnapPx / px;
        }

        // ---- input ------------------------------------------------------------------------------------

        public override void _UnhandledInput(InputEvent ev)
        {
            if (!Active || _editor == null) return;
            if (_editor.Mode != EEditorMode.Buildings || (_flyCam != null && _flyCam.Flying)) return;

            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                var mp = GetViewport().GetMousePosition();
                if (mb.Pressed)
                {
                    if (Editor.PointerOverUI(this)) return;
                    OnPress(mp);
                }
                else if (_drag != Drag.None)
                {
                    var cap = _dragCapture; var w = _selWall;
                    _drag = Drag.None; _dragCapture = null;
                    if (cap != null && w != null) _editor.PushUndo("opening edit", () => Restore(w, cap));
                }
            }
            else if (ev is InputEventMouseMotion)
            {
                if (_drag != Drag.None) OnDrag(GetViewport().GetMousePosition());
                else if (_drawing != null) StretchDraw(GetViewport().GetMousePosition());
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                if (k.Keycode == Key.Delete || k.Keycode == Key.Backspace)
                {
                    if (_selOpening >= 0) DeleteOpening(_selWall, _selOpening);
                    else if (_selWall != null) DeleteWall(_selWall);
                }
                else if (k.Keycode == Key.Escape)
                {
                    if (_drawing != null) { RemoveWall(_drawing); _drawing = null; }
                    else if (_selOpening >= 0) _selOpening = -1;
                    else _selWall = null;
                    PositionHandles();
                }
                else if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key6) _armed = (int)(k.Keycode - Key.Key1);
            }
        }

        void OnPress(Vector2 screen)
        {
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);

            if (WallDrawMode)
            {
                if (_drawing == null)
                {
                    if (!GroundAt(from, dir, out var p)) return;
                    _drawAnchor = p;
                    _selWall = null; _selOpening = -1; PositionHandles();
                    _drawing = AddWall(p, 0f, WallOpenings.LatticeStep);
                }
                else _drawing = null;      // second click commits; AddWall already pushed the undo
                return;
            }

            // a handle on the selected opening wins over everything behind it
            if (_selWall != null && _selOpening >= 0 && _selWall.RayToUV(from, dir, out float hu, out float hv))
            {
                var o = _selWall.Openings[_selOpening];
                float tol = HandlePx / Mathf.Max(0.01f, PxPerMetre(_selWall, hu, hv));
                if (Mathf.Abs(hu - o.U) < tol && hv > o.V && hv < o.V1) { Begin(Drag.EdgeU0); return; }
                if (Mathf.Abs(hu - o.U1) < tol && hv > o.V && hv < o.V1) { Begin(Drag.EdgeU1); return; }
                if (Mathf.Abs(hv - o.V) < tol && hu > o.U && hu < o.U1) { Begin(Drag.EdgeV0); return; }
                if (Mathf.Abs(hv - o.V1) < tol && hu > o.U && hu < o.U1) { Begin(Drag.EdgeV1); return; }
            }

            // otherwise pick a wall by its collider
            var q = new PhysicsRayQueryParameters3D { From = from, To = from + dir * 8000f, CollisionMask = 1u << 0 };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count > 0 && _pickToWall.TryGetValue((Rid)hit["rid"], out var w2))
            {
                _selWall = w2;
                w2.RayToUV(from, dir, out float u, out float v);
                int idx = w2.OpeningAt(u, v);
                if (idx >= 0) { _selOpening = idx; var o = w2.Openings[idx]; _grabDU = u - (o.U + o.Width * 0.5f); _grabDV = v - (o.V + o.Height * 0.5f); Begin(Drag.Move); }
                else if (_armed >= 0) _selOpening = AddOpening(w2, u, v, _armed);
                else _selOpening = -1;
                PositionHandles();
            }
        }

        /// <summary>Where the cursor meets the ground, ignoring walls. Walls sit on collision layer 0 along
        /// with the terrain, so an un-excluded pick starts the next wall on top of the last one you drew --
        /// which looks like the tool randomly placing walls in the air.</summary>
        bool GroundAt(Vector3 from, Vector3 dir, out Vector3 point)
        {
            point = default;
            var excl = new Godot.Collections.Array<Rid>();
            foreach (var w in _walls) if (IsInstanceValid(w)) excl.Add(w.BodyRid);
            var q = new PhysicsRayQueryParameters3D { From = from, To = from + dir * 8000f, CollisionMask = 1u << 0, Exclude = excl };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count > 0) { point = (Vector3)hit["position"]; return true; }
            // no terrain under the cursor (aimed at the sky, or an empty test scene): fall back to y=0 so the
            // tool still works rather than silently doing nothing
            var plane = new Plane(Vector3.Up, 0f);
            var p = plane.IntersectsRay(from, dir);
            if (p == null) return false;
            point = p.Value;
            return true;
        }

        /// <summary>Resize the wall being laid to reach the cursor. Length snaps to the lattice and yaw to 15
        /// degrees, so a run drawn by hand still lines up with anything built on the structures grid.</summary>
        void StretchDraw(Vector2 screen)
        {
            if (_drawing == null || !IsInstanceValid(_drawing)) { _drawing = null; return; }
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            var plane = new Plane(Vector3.Up, _drawAnchor.Y);
            var hit = plane.IntersectsRay(from, dir);
            if (hit == null) return;
            var d = hit.Value - _drawAnchor;
            var flat = new Vector2(d.X, d.Z);
            if (flat.Length() < 0.05f) return;
            float yaw = Mathf.Snapped(Mathf.RadToDeg(Mathf.Atan2(-flat.Y, flat.X)), 15f);
            float len = Mathf.Max(WallOpenings.LatticeStep,
                                  Mathf.Round(flat.Length() / WallOpenings.LatticeStep) * WallOpenings.LatticeStep);
            _drawing.RotationDegrees = new Vector3(0f, yaw, 0f);
            _drawing.Length = len;
            _drawing.Rebuild();
        }

        /// <summary>Enter or leave the Buildings mode. A building is authored AGAINST A BLANK PLANE, not over
        /// the map: it is a prop being made, and every terrain pick, every existing prop and every bit of the
        /// island is something for the cursor to catch on while you are drawing a 12m wall. The map is put
        /// back exactly as it was on the way out.</summary>
        public void SetActive(bool on)
        {
            if (Active == on) return;
            Active = on;
            if (_stage == null) BuildStage();
            if (_stage != null) _stage.Visible = on;
            if (on)
            {
                if (_cam != null) { _camReturn = _cam.GlobalTransform; _haveReturn = true; }
                MoveCameraToStage();
            }
            else
            {
                if (_drawing != null) { RemoveWall(_drawing); _drawing = null; }
                if (_haveReturn && _cam != null) { _cam.GlobalTransform = _camReturn; _haveReturn = false; }
            }
        }

        /// <summary>The build stage sits well above the island rather than the map being hidden for it. There
        /// is no single node holding the map -- terrain, props and resources are all separate children of the
        /// scene root -- so "hide the world" would be a list of nodes to keep in step with worldgen forever,
        /// and the first one anybody forgot would leave a hill sticking through the building. An offset needs
        /// to know nothing, and it also keeps map colliders out of reach of the wall picker.</summary>
        public static readonly Vector3 StageOrigin = new(0f, 2000f, 0f);

        Node3D _stage;      // the blank build plane + its grid, shown only in Buildings mode
        Transform3D _camReturn;
        bool _haveReturn;

        void MoveCameraToStage()
        {
            if (_cam == null) return;
            var eye = StageOrigin + new Vector3(0f, 11f, 30f);
            _cam.GlobalPosition = eye;
            _cam.LookAt(StageOrigin + new Vector3(0f, 3f, 0f), Vector3.Up);
        }

        void BuildStage()
        {
            _stage = new Node3D { Name = "Stage", Visible = false, Position = StageOrigin };
            AddChild(_stage);

            // A ground plane you can pick against, on the world layer, because GroundAt raycasts layer 0 and
            // with the map hidden there would otherwise be nothing under the cursor at all.
            var body = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            // WorldBoundaryShape3D's plane is in the BODY's space, so parenting it under the offset stage puts
            // the pick plane at the stage height and not at world y=0.
            // Big enough that its edge is off-screen at working distances -- otherwise the map, 2 km below,
            // shows past the rim and the "blank stage" has scenery in it.
            var floor = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(900f, 900f) } };
            floor.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.26f, 0.28f, 0.26f), Roughness = 1f };
            body.AddChild(floor);
            _stage.AddChild(body);

            // Lattice lines at the same 3m pitch walls snap to, so the snapping is something you can SEE
            // rather than something you infer from where the wall lands.
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Lines);
            const float Half = 60f, Y = 0.02f;
            for (float x = -Half; x <= Half + 1e-3f; x += WallOpenings.LatticeStep)
            {
                st.AddVertex(new Vector3(x, Y, -Half)); st.AddVertex(new Vector3(x, Y, Half));
                st.AddVertex(new Vector3(-Half, Y, x)); st.AddVertex(new Vector3(Half, Y, x));
            }
            var grid = new MeshInstance3D { Mesh = st.Commit() };
            grid.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.42f, 0.46f, 0.42f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            _stage.AddChild(grid);
        }

        // ---- persistence -------------------------------------------------------------------------------
        // Drawn walls used to live only in the session: you could lay out a building, hit Save, exit, and find
        // nothing. Same file convention as the other sub-editors (editor_<map>_*.dat beside the content).

        string SavePath => ProjectSettings.GlobalizePath("res://content/buildings/")
                           + $"editor_{_editor?.MapName ?? "none"}_Walls.dat";

        /// <summary>Editor.Save() fan-out. Returns the number of walls written.</summary>
        public int Save()
        {
            var plans = new List<WallPlan>();
            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w)) continue;
                var pl = new WallPlan
                {
                    X = w.Position.X, Y = w.Position.Y, Z = w.Position.Z,
                    Yaw = w.RotationDegrees.Y, Length = w.Length, Height = w.Height,
                    Thickness = w.Thickness, Material = w.MaterialId,
                };
                pl.Openings.AddRange(w.Openings);
                plans.Add(pl);
            }
            // An empty layout still writes: otherwise deleting your last wall and saving leaves the previous
            // file on disk, and the building you deleted comes back next time you open the map.
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SavePath));
                System.IO.File.WriteAllText(SavePath, WallSave.Write(plans));
            }
            catch (System.Exception e) { GD.PrintErr($"[editor-buildings] save failed: {e.Message}"); return 0; }
            GD.Print($"[editor-buildings] saved {plans.Count} walls -> {SavePath}");
            return plans.Count;
        }

        /// <summary>Read the map's walls back. Called once at setup; replaces whatever is loaded.</summary>
        public int Load()
        {
            if (!System.IO.File.Exists(SavePath)) return 0;
            List<WallPlan> plans;
            try { plans = WallSave.Read(System.IO.File.ReadAllLines(SavePath)); }
            catch (System.Exception e) { GD.PrintErr($"[editor-buildings] load failed: {e.Message}"); return 0; }

            foreach (var w in _walls.ToArray()) RemoveWall(w);
            foreach (var pl in plans)
                SpawnWall(new Vector3(pl.X, pl.Y, pl.Z), pl.Yaw, pl.Length, pl.Thickness, pl.Material, pl.Openings, pl.Height);
            GD.Print($"[editor-buildings] loaded {plans.Count} walls");
            return plans.Count;
        }

        // ---- bake ----------------------------------------------------------------------------------------

        /// <summary>Bake the drawn walls into a placeable prop and register it, returning its name or null.
        ///
        /// The output is an .obj plus a small palette PNG in the objects directory -- exactly what a retail
        /// building already is -- so a baked building goes down the SAME placement, material and collision path
        /// as every ripped prop, with no branch anywhere asking whether a prop came from us. MatFor already
        /// treats any texture up to 16x16 as a nearest-filtered palette; that is not a coincidence, it is how
        /// the retail buildings render.
        ///
        /// The geometry is read back off the COMMITTED meshes rather than regenerated from the wall data. A
        /// second copy of the box maths could disagree with the first, and then what you baked would not be
        /// what you drew -- which is the whole failure this tool exists to avoid.</summary>
        public string Bake(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || _walls.Count == 0) return null;
            name = SafeName(name);

            // one palette for the building: every distinct wall/reveal colour in use, laid out 4 across
            var colours = new List<Color>();
            int TexelOf(Color c)
            {
                for (int i = 0; i < colours.Count; i++) if (colours[i].IsEqualApprox(c)) return i;
                if (colours.Count >= 16) return 0;      // MatFor's palette ceiling; 16 colours is four materials
                colours.Add(c);
                return colours.Count - 1;
            }

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w)) continue;
                var mat = WallMaterials.At(w.MaterialId);
                foreach (var (node, colour) in new[] { ("Mesh", mat.Wall), ("TrimMesh", mat.Reveal) })
                {
                    var mi = w.GetNodeOrNull<MeshInstance3D>(node);
                    if (mi?.Mesh == null || mi.Mesh.GetSurfaceCount() == 0) continue;
                    var arr = mi.Mesh.SurfaceGetArrays(0);
                    var mv = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                    var mn = (Vector3[])arr[(int)Mesh.ArrayType.Normal];
                    var mi2 = (int[])arr[(int)Mesh.ArrayType.Index];
                    if (mv == null || mi2 == null) continue;

                    int texel = TexelOf(colour);
                    var xf = w.GlobalTransform;
                    int b = verts.Count;
                    for (int i = 0; i < mv.Length; i++)
                    {
                        verts.Add(xf * mv[i] - StageOrigin);         // building-local: the prop's own origin
                        norms.Add((xf.Basis * (mn != null && i < mn.Length ? mn[i] : Vector3.Up)).Normalized());
                        uvs.Add(new Vector2(texel, 0f));             // resolved once the palette size is known
                    }
                    foreach (int idx in mi2) tris.Add(b + idx);
                }
            }
            if (verts.Count == 0 || tris.Count == 0) return null;

            int pw = 4, ph = Mathf.Max(2, Mathf.CeilToInt(colours.Count / 4f));
            for (int i = 0; i < uvs.Count; i++)
            {
                int t = (int)uvs[i].X;
                // GODOT-space UV, texel centre. The single V-flip lives in ObjText, which is the only place
                // that knows about file space -- inverting here as well flipped it twice and every face
                // sampled the wrong row, which showed up as a building baked entirely in the unused-texel
                // magenta. That fill colour is deliberate: a UV slip has to be unmistakable, not plausible.
                uvs[i] = new Vector2((t % pw + 0.5f) / pw, ((t / pw) + 0.5f) / ph);
            }

            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(dir + name + ".obj", ObjText(name, verts, norms, uvs, tris));
                PaletteImage(colours, pw, ph).SavePng(dir + name + "_tex.png");
                System.IO.File.WriteAllText(BuildingSourcePath(name), WallSave.Write(Plans()));
                RegisterBaked(name);
            }
            catch (System.Exception e) { GD.PrintErr($"[editor-buildings] bake failed: {e.Message}"); return null; }

            GD.Print($"[editor-buildings] baked '{name}': {tris.Count / 3} tris, {colours.Count} palette colours");
            _editor?.Objects?.ReloadCatalog();
            return name;
        }

        public static string BuildingSourcePath(string name) =>
            ProjectSettings.GlobalizePath("res://content/buildings/") + name + ".dat";

        /// <summary>Baked names live in their OWN list, never appended to guid_mesh.txt: that file is derived
        /// from the retail bundles and gets regenerated, which would silently eat every building anyone
        /// made.</summary>
        public static string BakedListPath() =>
            ProjectSettings.GlobalizePath("res://content/objects/") + "baked_buildings.txt";

        static void RegisterBaked(string name)
        {
            var have = new HashSet<string>();
            if (System.IO.File.Exists(BakedListPath()))
                foreach (var l in System.IO.File.ReadAllLines(BakedListPath()))
                    if (l.Trim().Length > 0) have.Add(l.Trim());
            if (!have.Add(name)) return;                 // re-baking an existing building overwrites, not duplicates
            var sorted = new List<string>(have);
            sorted.Sort(System.StringComparer.Ordinal);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BakedListPath()));
            System.IO.File.WriteAllLines(BakedListPath(), sorted);
        }

        static string SafeName(string raw)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in raw.Trim())
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.Length == 0 ? "Building" : sb.ToString();
        }

        List<WallPlan> Plans()
        {
            var plans = new List<WallPlan>();
            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w)) continue;
                var pl = new WallPlan
                {
                    X = w.Position.X, Y = w.Position.Y, Z = w.Position.Z,
                    Yaw = w.RotationDegrees.Y, Length = w.Length, Height = w.Height,
                    Thickness = w.Thickness, Material = w.MaterialId,
                };
                pl.Openings.AddRange(w.Openings);
                plans.Add(pl);
            }
            return plans;
        }

        static Image PaletteImage(List<Color> colours, int w, int h)
        {
            var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    img.SetPixel(x, y, i < colours.Count ? colours[i] : Colors.Magenta);
                }
            return img;
        }

        /// <summary>Write the prop .obj in the frame ObjMesh.Load + EditorObjects.Upright expect.
        ///
        /// Those two are the only authority on it, so this inverts them step by step rather than restating the
        /// convention: Upright pitches the loaded mesh 270 about X, which maps mesh (x,y,z) to node (x,z,-y),
        /// and the loader itself negates Z off the file. Winding is inverted here too because the loader
        /// ALWAYS reverses -- reversing twice is what leaves the faces pointing out.</summary>
        static string ObjText(string name, List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> tris)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string F(float x) => x.ToString("0.#####", ci);
            var sb = new System.Text.StringBuilder();
            sb.Append("# baked by the unturned-godot building tool\n");
            sb.Append("g ").Append(name).Append('\n');
            foreach (var p in v)
            {
                var f = ToObj(p);
                sb.Append("v ").Append(F(f.X)).Append(' ').Append(F(f.Y)).Append(' ').Append(F(f.Z)).Append('\n');
            }
            foreach (var t in uv) sb.Append("vt ").Append(F(t.X)).Append(' ').Append(F(1f - t.Y)).Append('\n');
            foreach (var d in n)
            {
                var f = ToObj(d);
                sb.Append("vn ").Append(F(f.X)).Append(' ').Append(F(f.Y)).Append(' ').Append(F(f.Z)).Append('\n');
            }
            for (int i = 0; i + 2 < tris.Count; i += 3)
                for (int k = 2; k >= 0; k--)      // reversed: the loader reverses again
                {
                    int a = tris[i + k] + 1;
                    sb.Append(k == 2 ? "f " : " ").Append(a).Append('/').Append(a).Append('/').Append(a);
                    if (k == 0) sb.Append('\n');
                }
            return sb.ToString();
        }

        /// <summary>node space -> prop .obj space. The inverse of what ObjMesh.Load + EditorObjects.Upright
        /// do, composed in that order:
        ///   Load (CONV 1, the default) takes the file's xyz RAW -- the old negate-Z reflected every mesh
        ///   Upright pitches 270 about X, mapping mesh (x,y,z) to node (x,z,-y)
        /// so node = (f.x, f.z, -f.y), and inverting gives f = (n.x, -n.z, n.y).
        ///
        /// Derived wrong the first time by assuming the negate-Z branch, which put the building upside down --
        /// base at -4.25, roof at 0. It survived every size check, because a mirrored box is the same size.
        /// Hence the round-trip test: sizes agree with a sign error, positions do not.</summary>
        public static Vector3 ToObj(Vector3 nodeSpace) => new(nodeSpace.X, -nodeSpace.Z, nodeSpace.Y);

        /// <summary>Set the palette for the selection if there is one, else for the next wall drawn.</summary>
        public void SelectMaterial(int id)
        {
            ActiveMaterial = WallMaterials.Count == 0 ? 0 : Mathf.PosMod(id, WallMaterials.Count);
            if (_selWall != null) SetMaterial(_selWall, ActiveMaterial);
        }

        void Begin(Drag d)
        {
            _drag = d;
            _dragCapture = _selWall?.Openings.ToArray();
        }

        void OnDrag(Vector2 screen)
        {
            if (_selWall == null || _selOpening < 0) return;
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            if (!_selWall.RayToUV(from, dir, out float u, out float v)) return;
            float tol = SnapTolerance(_selWall, u, v);
            if (_drag == Drag.Move) MoveOpening(_selWall, _selOpening, u - _grabDU, v - _grabDV, tol);
            else DragEdge(_selWall, _selOpening, _drag, u, v, tol);
            PositionHandles();
        }

        float PxPerMetre(WallSurface w, float u, float v)
        {
            if (_cam == null) return 50f;
            var a = _cam.UnprojectPosition(w.UVToWorld(u, v));
            var b = _cam.UnprojectPosition(w.UVToWorld(u + 1f, v));
            return a.DistanceTo(b);
        }

        void PositionHandles()
        {
            foreach (var c in _handles.GetChildren()) c.QueueFree();
            if (_selWall == null || _selOpening < 0 || _selOpening >= _selWall.Openings.Count) return;
            var o = _selWall.Openings[_selOpening];
            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.82f, 0.25f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true,
            };
            foreach (var (hu, hv) in new[] { (o.U, (o.V + o.V1) * 0.5f), (o.U1, (o.V + o.V1) * 0.5f),
                                             ((o.U + o.U1) * 0.5f, o.V), ((o.U + o.U1) * 0.5f, o.V1) })
            {
                float s = Mathf.Max(0.08f, HandlePx / Mathf.Max(1f, PxPerMetre(_selWall, hu, hv)));
                var m = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(s, s, s) }, MaterialOverride = mat };
                _handles.AddChild(m);
                m.GlobalPosition = _selWall.UVToWorld(hu, hv);
            }
        }
    }
}
