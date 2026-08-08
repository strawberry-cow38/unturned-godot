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
        MeshInstance3D _ghost;                 // translucent preview of the armed opening
        MeshInstance3D _gridFlag;              // where the next draw will start
        Label3D _readout;                      // the size billboard shown while dragging
        const float HandlePx = 14f, SnapPx = 12f;

        /// <summary>The billboard that says how big the thing you are dragging currently is, and which
        /// measured preset it is sitting on. The snapping was already there and invisible: you could feel an
        /// edge catch without knowing what it caught on, or whether 3.31 was a retail size or a number you
        /// happened to stop at. strawberry_cow: "show like a text billboard that tells us the width + the
        /// standard size".</summary>
        void ShowReadout(WallSurface w, WallOpening o)
        {
            if (w == null) { HideReadout(); return; }
            EnsureReadout();
            string near = NearestPresetName(o);
            Billboard(w.UVToWorld(o.U + o.Width * 0.5f, o.V + o.Height + 0.35f),
                      near == null ? $"{o.Width:0.00} × {o.Height:0.00} m"
                                   : $"{o.Width:0.00} × {o.Height:0.00} m\n{near}");
        }

        /// <summary>Put the billboard somewhere with some text on it.</summary>
        void Billboard(Vector3 at, string text)
        {
            if (_readout == null) return;
            _readout.Text = text;
            _readout.GlobalPosition = at;
            _readout.Visible = true;
        }

        void HideReadout() { if (_readout != null) _readout.Visible = false; }

        void EnsureReadout()
        {
            if (_readout != null) return;
            _readout = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true, FontSize = 96, PixelSize = 0.004f,
                Modulate = new Color(1f, 0.95f, 0.6f),
                OutlineSize = 24, OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
            };
            AddChild(_readout);
        }

        /// <summary>A marker on the grid square the wall will start from, so you can see where a draw is
        /// going to land BEFORE you commit to it. The grid is 3 m and invisible until something lands on it,
        /// which made placement feel like a guess. strawberry_cow: "a visual flag that follows the grid to
        /// show where my wall draw is gonna be".</summary>
        void UpdateGridFlag(Vector2 screen)
        {
            if (_cam == null || !(WallDrawMode || RoomDrawMode || SlabDrawMode)) { HideGridFlag(); return; }
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            if (!GroundAt(from, dir, out var p)) { HideGridFlag(); return; }
            var snapped = new Vector3(WallOpenings.SnapGrid(p.X), p.Y, WallOpenings.SnapGrid(p.Z));
            if (_gridFlag == null)
            {
                _gridFlag = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.25f, 2.2f, 0.25f) },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(1f, 0.85f, 0.2f, 0.75f),
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        NoDepthTest = true,
                    },
                };
                AddChild(_gridFlag);
            }
            _gridFlag.GlobalPosition = snapped + new Vector3(0f, 1.1f, 0f);
            _gridFlag.Visible = true;
        }

        void HideGridFlag() { if (_gridFlag != null) _gridFlag.Visible = false; }

        /// <summary>Which retail preset this opening is currently sitting on, or null if it is between
        /// them. Naming the preset is the point -- "3.31 x 2.97" means nothing, "window" does.</summary>
        static string NearestPresetName(WallOpening o)
        {
            foreach (var a in Archetypes)
                if (Mathf.Abs(o.Width - a.Width) < 0.02f && Mathf.Abs(o.Height - a.Height) < 0.02f)
                    return a.Name;
            // not a whole preset, but the width alone may still be a measured one
            foreach (float wd in WallOpenings.Widths)
                if (Mathf.Abs(o.Width - wd) < 0.02f) return $"{wd:0.##} m (retail width)";
            return null;
        }

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
        public void Arm(int archetype) { _armed = archetype; if (archetype < 0) { HideGhost(); HideReadout(); } }

        /// <summary>The palette new walls are drawn with. A retail "material" is only a choice of eight flat
        /// colours -- there are no textures on these buildings -- so this is an index, not an asset.</summary>
        public int ActiveMaterial;

        /// <summary>Thickness new walls start at. 0.70 exterior / 0.50 partition are both measured off retail,
        /// and neither is law -- the point of the slider is that you find out by looking.</summary>
        public float NewWallThickness = WallOpenings.DefaultThickness;

        /// <summary>Click-click wall laying. Armed from the panel; the first click drops a wall and the second
        /// commits it.</summary>
        public bool WallDrawMode;

        /// <summary>Draw a roof or floor as a RECTANGLE you drag, instead of one auto-fitted to the current
        /// walls. Auto-fit is a guess about what you meant and there is no way to argue with it -- it takes
        /// the bounding box of every wall, so an L-shaped building gets a slab over the courtyard and a
        /// building mid-edit gets a slab over whatever happens to exist. strawberry_cow: "roof tool should
        /// be a drag rect instead of an auto-bake tool." Add roof/Add floor still auto-fit for the simple
        /// case; this is the one you reach for when it guesses wrong.</summary>
        /// <summary>Drag a rectangle and get four walls on the grid. The common case is a room and laying it
        /// one wall at a time is four draws that all have to agree with each other.</summary>
        public bool RoomDrawMode;
        readonly List<WallSurface> _room = new();
        Vector3 _roomAnchor;
        public bool DrawingRoom => _room.Count > 0;

        public bool SlabDrawMode;
        public SurfaceKind SlabDrawKind = SurfaceKind.Roof;
        WallSurface _drawingSlab;
        Vector3 _slabAnchor;
        public bool DrawingSlab => _drawingSlab != null;

        // The wall being laid is a REAL WallSurface being resized under the cursor, not a ghost that is later
        // swapped for the real thing. Same reason the openings have no preview object: two representations of
        // one wall is two chances to disagree, and this repo already shipped a barricade whose ghost lay on
        // its side while the placed object stood upright.
        WallSurface _drawing;
        Vector3 _drawAnchor;

        public bool Drawing => _drawing != null;
        public string ToolText => SlabDrawMode ? (_drawingSlab != null ? $"drag the {SlabDrawKind.ToString().ToLower()} out — release to place" : $"{SlabDrawKind.ToString().ToLower()}: drag a rectangle")
                                : WallDrawMode ? (_drawing != null ? "drag the wall out — release to place, Esc cancels" : "wall: press and drag")
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

        /// <summary>Draw a wall. Length snaps to the lattice HERE, because snapping belongs to the act of
        /// drawing -- it is a drafting aid for a hand on a mouse.</summary>
        public WallSurface AddWall(Vector3 origin, float yawDeg, float length)
        {
            var w = SpawnWall(SnapOrigin(origin), yawDeg, SnapRun(length), NewWallThickness, ActiveMaterial, null);
            _editor?.PushUndo("wall place", () => RemoveWall(w));
            return w;
        }

        /// <summary>Wall runs snap to the lattice so drawn walls stay interoperable with anything built on the
        /// structures grid.</summary>
        public static float SnapRun(float length) => Mathf.Max(WallOpenings.LatticeStep,
            Mathf.Round(length / WallOpenings.LatticeStep) * WallOpenings.LatticeStep);

        /// <summary>Wall ENDS land on the same grid their lengths snap to. Snapping the length alone still
        /// lets two exact-3 m walls miss each other, because nothing pinned where either one started -- which
        /// is the source of the small gaps between drawn walls. Y is left alone; the grid is a floor plan.</summary>
        public static Vector3 SnapOrigin(Vector3 p)
            => new(WallOpenings.SnapGrid(p.X), p.Y, WallOpenings.SnapGrid(p.Z));

        /// <summary>Build a wall and register it, WITHOUT touching the undo stack. Undo actions run through
        /// here: a restore that called AddWall would push a fresh entry onto the stack it is being replayed
        /// from.</summary>
        WallSurface SpawnWall(Vector3 origin, float yawDeg, float length, float thickness, int material,
                              IReadOnlyList<WallOpening> openings, float height = WallOpenings.DoorHeight,
                              float pitchDeg = 0f, SurfaceKind kind = SurfaceKind.Wall, float gableRise = 0f,
                              int texel = -1, float insetL0 = 0f, float insetL1 = 0f,
                              float insetR0 = 0f, float insetR1 = 0f)
        {
            // NO SNAPPING HERE. This is the path Load() and ImportRetail() come through, and rounding a
            // length on the way in makes loading lossy: an imported wall measured off the mesh to the
            // centimetre was being rounded to the nearest 3 m -- up to 1.5 m per wall -- so imported buildings
            // mis-met at their corners and openings recovered against the true length fell off the shortened
            // end, where the partition clamps them away silently. It also meant Load() was not the inverse of
            // Save(). Snapping is a DRAWING aid and now lives in AddWall.
            var w = new WallSurface
            {
                Length = Mathf.Max(0.01f, length), Height = height, Thickness = thickness, MaterialId = material, Kind = kind,
                GableRise = gableRise, Texel = texel,
                InsetL0 = insetL0, InsetL1 = insetL1, InsetR0 = insetR0, InsetR1 = insetR1,
                Position = origin, RotationDegrees = new Vector3(pitchDeg, yawDeg, 0f),
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
            var kind = w.Kind;
            float gable = w.GableRise;
            var ops = w.Openings.ToArray();
            RemoveWall(w);
            _editor?.PushUndo("wall delete", () => SpawnWall(pos, rot.Y, len, th, mat, ops, h, rot.X, kind, gable));
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
        /// <summary>Where an armed opening would land on this wall. The PREVIEW and the PLACEMENT both call
        /// this, so the ghost cannot describe a different opening from the one you get -- which is the whole
        /// reason this tool avoided preview objects in the first place. A ghost is fine; a ghost with its own
        /// copy of the placement rules is not.</summary>
        public WallOpening PlannedOpening(WallSurface w, float u, float v, int archetype)
        {
            var a = Archetypes[Mathf.PosMod(archetype, Archetypes.Length)];
            var o = new WallOpening(u - a.Width * 0.5f, a.FloorPinned ? 0f : v - a.Height * 0.5f,
                                    a.Width, a.Height, 999f, archetype);
            return WallOpenings.Clamp(o, w.Length, w.Height, w.Openings);
        }

        public int AddOpening(WallSurface w, float u, float v, int archetype)
        {
            if (w == null) return -1;
            var o = PlannedOpening(w, u, v, archetype);
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
                // Shift PINS to the measured sill/head heights; without it the snap is the ordinary
                // screen-space one you can pull away from. strawberry_cow wanted the standard heights on
                // demand rather than always -- "they stay at the standard fixed heights while holding
                // shift" -- because the old behaviour snapped whether you wanted it or not.
                float tol = Input.IsKeyPressed(Key.Shift) ? float.MaxValue : snapTol;
                o.V = WallOpenings.Snap(o.V, WallOpenings.SillHeights, tol);
                float head = WallOpenings.Snap(o.V + o.Height, WallOpenings.HeadHeights, tol);
                o.V = head - o.Height;
            }
            var was = w.Openings[index];
            o = WallOpenings.Clamp(o, w.Length, w.Height, w.Openings, index,
                                   was.U + was.Width * 0.5f, was.V + was.Height * 0.5f);
            // A hard edge, not a shove. When a drag leaves no legal spot between two neighbours, Clamp's
            // two-sided fallback parks the opening on top of one of them, and the editor looks like it is
            // rearranging your building behind your back. Refusing instead makes a neighbour feel like a
            // wall you slide along. strawberry_cow: "just prevent them from overlapping, not by moving,
            // just like a hard edge".
            if (WallOpenings.Overlaps(o, w.Openings, index)) return;
            if (Mathf.Abs(o.U - was.U) < 1e-5f && Mathf.Abs(o.V - was.V) < 1e-5f) return;
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
            // Same hard edge when RESIZING: an edge dragged into a neighbour stops against it rather than
            // growing through it and displacing it.
            if (WallOpenings.Overlaps(o, w.Openings, index)) return;
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
                    HideReadout();
                    if (cap != null && w != null) _editor.PushUndo("opening edit", () => Restore(w, cap));
                }
                // Release finishes the wall. Click-to-start/click-to-finish left the tool in a state that
                // looks identical to idle while it is actually mid-wall, and every other drag in this editor
                // is press-move-release.
                else if (_drawing != null)
                {
                    StretchDraw(mp);
                    _drawing = null;               // AddWall already pushed the undo
                    HideReadout();
                    MergeDuplicateWalls();
                    SolveCornersNow();
                }
                else if (_room.Count > 0)
                {
                    StretchRoom(mp);
                    bool tiny = _room[0].Length < 0.5f || _room[1].Length < 0.5f;
                    if (tiny) { foreach (var w in new List<WallSurface>(_room)) RemoveWall(w); _editor?.PopUndo(); }
                    _room.Clear();
                    HideReadout();
                    if (!tiny) { MergeDuplicateWalls(); SolveCornersNow(); }
                }
                else if (_drawingSlab != null)
                {
                    StretchSlab(mp);
                    // a slab dragged to nothing is a misclick, not a surface
                    if (_drawingSlab.Length < 0.5f || _drawingSlab.Height < 0.5f)
                    { RemoveWall(_drawingSlab); _editor?.PopUndo(); }
                    _drawingSlab = null;
                    HideReadout();
                }
            }
            else if (ev is InputEventMouseMotion)
            {
                if (_drag != Drag.None) OnDrag(GetViewport().GetMousePosition());
                else if (_drawing != null) StretchDraw(GetViewport().GetMousePosition());
                else if (_room.Count > 0) StretchRoom(GetViewport().GetMousePosition());
                else if (_drawingSlab != null) StretchSlab(GetViewport().GetMousePosition());
                else { UpdateGhost(GetViewport().GetMousePosition()); UpdateGridFlag(GetViewport().GetMousePosition()); }
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
                    if (_drawing != null) { CancelDraw(); }
                    else if (_room.Count > 0) { foreach (var w in new List<WallSurface>(_room)) RemoveWall(w); _room.Clear(); _editor?.PopUndo(); }
                    else if (_drawingSlab != null) { RemoveWall(_drawingSlab); _drawingSlab = null; _editor?.PopUndo(); }
                    else if (_selOpening >= 0) _selOpening = -1;
                    else _selWall = null;
                    PositionHandles();
                }
                else if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key6) _armed = (int)(k.Keycode - Key.Key1);
                // Ctrl+Z. The undo STACK was always here -- every wall, opening and edit pushes onto it --
                // but the key was only ever bound in EditorObjects, so in Buildings mode there was nothing
                // to press. An undo history nobody can reach is the same as no undo history.
                else if (k.CtrlPressed && k.Keycode == Key.Z)
                {
                    if (_drawing != null) CancelDraw();
                    else if (_drawingSlab != null) { RemoveWall(_drawingSlab); _drawingSlab = null; _editor?.PopUndo(); }
                    else { _editor.Undo(); _selOpening = -1; PositionHandles(); }
                }
            }
        }

        void OnPress(Vector2 screen)
        {
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);

            if (RoomDrawMode)
            {
                if (_room.Count == 0 && GroundAt(from, dir, out var rp))
                {
                    _roomAnchor = new Vector3(WallOpenings.SnapGrid(rp.X), rp.Y, WallOpenings.SnapGrid(rp.Z));
                    _selWall = null; _selOpening = -1; PositionHandles();
                    for (int i = 0; i < 4; i++)
                        _room.Add(SpawnWall(_roomAnchor, i * 90f, 0.01f, NewWallThickness, ActiveMaterial, null));
                    var made = new List<WallSurface>(_room);
                    _editor?.PushUndo("room place", () => { foreach (var w in made) RemoveWall(w); });
                }
                return;
            }

            if (SlabDrawMode)
            {
                if (_drawingSlab == null && GroundAt(from, dir, out var sp))
                {
                    _slabAnchor = new Vector3(WallOpenings.SnapGrid(sp.X), SlabTopY(sp.Y), WallOpenings.SnapGrid(sp.Z));
                    _selWall = null; _selOpening = -1; PositionHandles();
                    _drawingSlab = SpawnWall(_slabAnchor, 0f, 0.01f, SlabThickness, ActiveMaterial, null,
                                             0.01f, -90f, SlabDrawKind);
                    _editor?.PushUndo(SlabDrawKind == SurfaceKind.Roof ? "roof draw" : "floor draw",
                                      () => RemoveWall(_drawingSlab));
                }
                return;
            }

            if (WallDrawMode)
            {
                if (_drawing == null)
                {
                    if (!GroundAt(from, dir, out var p)) return;
                    _drawAnchor = p;
                    _selWall = null; _selOpening = -1; PositionHandles();
                    _drawing = AddWall(p, 0f, WallOpenings.LatticeStep);
                }
                // press starts it; the release handler commits it
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

            // An opening is picked GEOMETRICALLY, before the collider pick, and that is not an optimisation.
            // The partition puts a real hole in the collider wherever there is a hole in the mesh -- that is
            // the whole point of it -- so a ray aimed at an opening passes straight through and hits nothing.
            // The click then fell through to "place a new one", which is exactly what it looked like from the
            // outside: strawberry_cow, "i'm not sure how to move openings? clicking them seems to just make
            // new ones". You could only ever grab one by its frame.
            if (PickOpening(from, dir, out var ow, out int oi, out float ou, out float ov))
            {
                _selWall = ow;
                _selOpening = oi;
                var so = ow.Openings[oi];
                _grabDU = ou - (so.U + so.Width * 0.5f);
                _grabDV = ov - (so.V + so.Height * 0.5f);
                Begin(Drag.Move);
                PositionHandles();
                return;
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

        /// <summary>Abandon the wall being laid, INCLUDING the undo step AddWall pushed for it. Dropping the
        /// wall and leaving the step behind is the failure DeleteWall's comment calls out: Ctrl+Z then fires,
        /// reports success and does nothing.</summary>
        void CancelDraw()
        {
            if (_drawing == null) return;
            RemoveWall(_drawing);
            _drawing = null;
            _editor?.PopUndo();
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
        /// <summary>The height a drawn slab sits at: on top of the walls if there are any, otherwise where
        /// you clicked. A roof you draw before there are walls is a roof at ground level, which is at least
        /// somewhere you can see it and drag.</summary>
        float SlabTopY(float fallback)
        {
            float top = float.MinValue;
            foreach (var w in _walls)
                if (IsInstanceValid(w) && w.Kind == SurfaceKind.Wall)
                    top = Mathf.Max(top, w.Position.Y + w.Height);
            if (top <= float.MinValue) return fallback;
            return SlabDrawKind == SurfaceKind.Roof ? top + SlabThickness * 0.5f : fallback;
        }

        /// <summary>Close the corners of what has been drawn so far, as its own undoable step.
        ///
        /// Corner solving used to happen only at BAKE, so a building looked notched the entire time you were
        /// working on it and tidied itself only on the way out. Drawn walls genuinely do need it -- they are
        /// laid endpoint to endpoint on their centrelines, which leaves a missing quarter at every corner.
        /// (An IMPORTED wall does not: it comes from a facade plane spanning the whole building and already
        /// overlaps its neighbour. Running this on an import puts a pilaster on every corner.)</summary>
        void SolveCornersNow()
        {
            var undo = SolveCorners();
            if (undo.Count > 0) _editor?.PushUndo("corner solve", () => RestoreCorners(undo));
        }

        /// <summary>Resize the four walls of the room being dragged. They are laid corner to corner going
        /// round, so the corner solver has real coincident endpoints to work with.</summary>
        void StretchRoom(Vector2 screen)
        {
            if (_room.Count < 4) return;
            foreach (var w in _room) if (!IsInstanceValid(w)) { _room.Clear(); return; }
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            var plane = new Plane(Vector3.Up, _roomAnchor.Y);
            var hit = plane.IntersectsRay(from, dir);
            if (hit == null) return;
            float x1 = WallOpenings.SnapGrid(hit.Value.X), z1 = WallOpenings.SnapGrid(hit.Value.Z);
            float minX = Mathf.Min(_roomAnchor.X, x1), maxX = Mathf.Max(_roomAnchor.X, x1);
            float minZ = Mathf.Min(_roomAnchor.Z, z1), maxZ = Mathf.Max(_roomAnchor.Z, z1);
            float w0 = Mathf.Max(0.01f, maxX - minX), d0 = Mathf.Max(0.01f, maxZ - minZ);
            float y = _roomAnchor.Y;

            // yaw 0 runs +X, 90 runs -Z, 180 runs -X, 270 runs +Z -- corner to corner, anticlockwise
            Set(_room[0], new Vector3(minX, y, maxZ), 0f, w0);
            Set(_room[1], new Vector3(maxX, y, maxZ), 90f, d0);
            Set(_room[2], new Vector3(maxX, y, minZ), 180f, w0);
            Set(_room[3], new Vector3(minX, y, minZ), 270f, d0);
            EnsureReadout();
            Billboard(new Vector3((minX + maxX) * 0.5f, y + 3f, (minZ + maxZ) * 0.5f), $"{w0:0.0} × {d0:0.0} m");

            static void Set(WallSurface w, Vector3 pos, float yaw, float len)
            {
                w.Position = pos;
                w.RotationDegrees = new Vector3(0f, yaw, 0f);
                w.Length = len;
                w.Rebuild();
            }
        }

        /// <summary>Fold walls that are duplicates of each other into one.
        ///
        /// Drawing a room against an existing one puts two walls on the shared edge, and two coincident walls
        /// are not a thicker wall -- they are z-fighting, doubled collision, and an opening cut in one of them
        /// that the other quietly fills back in. strawberry_cow: "shared dupe walls become one real wall".
        ///
        /// Two walls are the same wall when they lie on the same line, at the same height, and their runs
        /// touch. The survivor is stretched to cover both and inherits both sets of openings.</summary>
        public int MergeDuplicateWalls()
        {
            int merged = 0;
            for (int i = 0; i < _walls.Count; i++)
            {
                var a = _walls[i];
                if (!IsInstanceValid(a) || a.Kind != SurfaceKind.Wall) continue;
                for (int j = _walls.Count - 1; j > i; j--)
                {
                    var b = _walls[j];
                    if (!IsInstanceValid(b) || b.Kind != SurfaceKind.Wall) continue;
                    // same line: parallel (either direction), same base height, and b's ends on a's axis
                    float dy = Mathf.Abs(Mathf.Wrap(a.RotationDegrees.Y - b.RotationDegrees.Y, -90f, 90f));
                    if (dy > 5f) continue;
                    if (Mathf.Abs(a.Position.Y - b.Position.Y) > 0.05f) continue;
                    var ax = (a.UVToWorld(a.Length, 0f) - a.UVToWorld(0f, 0f)).Normalized();
                    var b0 = b.UVToWorld(0f, 0f);
                    var b1 = b.UVToWorld(b.Length, 0f);
                    var rel = b0 - a.UVToWorld(0f, 0f);
                    var perp = rel - ax * rel.Dot(ax);
                    if (new Vector2(perp.X, perp.Z).Length() > 0.2f) continue;      // a different line
                    float t0 = (b0 - a.UVToWorld(0f, 0f)).Dot(ax), t1 = (b1 - a.UVToWorld(0f, 0f)).Dot(ax);
                    if (t0 > t1) (t0, t1) = (t1, t0);
                    if (t1 < -0.05f || t0 > a.Length + 0.05f) continue;             // no overlap: two walls in a row
                    float lo = Mathf.Min(0f, t0), hi = Mathf.Max(a.Length, t1);
                    if (lo < -0.001f) a.Position = a.UVToWorld(lo, 0f);
                    foreach (var o in b.Openings)
                    {
                        var moved = o;
                        moved.U += t0 - lo;                                          // into the survivor's frame
                        if (!WallOpenings.Overlaps(moved, a.Openings)) a.Openings.Add(moved);
                    }
                    a.Length = hi - lo;
                    a.Height = Mathf.Max(a.Height, b.Height);
                    a.Rebuild();
                    RemoveWall(b);
                    merged++;
                }
            }
            return merged;
        }

        void StretchSlab(Vector2 screen)
        {
            if (_drawingSlab == null || !IsInstanceValid(_drawingSlab)) { _drawingSlab = null; return; }
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            var plane = new Plane(Vector3.Up, _slabAnchor.Y);
            var hit = plane.IntersectsRay(from, dir);
            if (hit == null) return;
            float x1 = WallOpenings.SnapGrid(hit.Value.X), z1 = WallOpenings.SnapGrid(hit.Value.Z);
            float minX = Mathf.Min(_slabAnchor.X, x1), maxX = Mathf.Max(_slabAnchor.X, x1);
            float minZ = Mathf.Min(_slabAnchor.Z, z1), maxZ = Mathf.Max(_slabAnchor.Z, z1);
            // same frame AddSlab builds in: origin at (minX, y, maxZ), run along +X, depth along -Z
            float run = Mathf.Max(0.01f, maxZ - minZ);
            _drawingSlab.Position = new Vector3(minX, _slabAnchor.Y, maxZ);
            _drawingSlab.Length = Mathf.Max(0.01f, maxX - minX);

            // The pitch slider applies to a DRAWN roof too; it used to be flat whatever the slider said.
            // Same convention as AddGableRoof -- pitch - 90, spawned at maxZ with yaw 0 so it rises toward
            // -Z -- and that sign is only correct BECAUSE the yaw is fixed here. Reading a yaw off geometry
            // instead needs 90 - pitch; see BuildingImport.
            float deg = SlabDrawKind == SurfaceKind.Roof ? Mathf.Clamp(ActiveRoofPitch, 0f, 70f) : 0f;
            if (deg > 0.1f)
            {
                // the drag rect is the FOOTPRINT, so the surface itself is longer than the run it covers
                _drawingSlab.Height = run / Mathf.Cos(Mathf.DegToRad(deg));
                _drawingSlab.RotationDegrees = new Vector3(deg - 90f, 0f, 0f);
            }
            else
            {
                _drawingSlab.Height = run;
                _drawingSlab.RotationDegrees = new Vector3(-90f, 0f, 0f);
            }
            _drawingSlab.Rebuild();
            EnsureReadout();
            Billboard(new Vector3((minX + maxX) * 0.5f, _slabAnchor.Y + 0.6f, (minZ + maxZ) * 0.5f),
                      $"{maxX - minX:0.0} × {run:0.0} m" + (deg > 0.1f ? $"  ·  {deg:0.#}°" : ""));
        }

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
            float len = SnapRun(flat.Length());
            _drawing.RotationDegrees = new Vector3(0f, yaw, 0f);
            _drawing.Length = len;
            _drawing.Rebuild();
            EnsureReadout();
            Billboard(_drawing.UVToWorld(len * 0.5f, _drawing.Height + 0.4f),
                      $"{len:0.0} m  ·  {Mathf.Wrap(yaw, 0f, 360f):0}°");
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
                CancelDraw();
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
            // Three-quarter, not straight on. A frontal view of a building shows one elevation and flattens
            // everything else -- you cannot see a roof's pitch, a wall's depth or which way a corner turns
            // from it, so it is the wrong camera both to build from and to judge a change by.
            var off = new Vector3(22f, 14f, 26f);
            // UG_EDITCAM=yaw,dist,height orbits this view for a capture. One three-quarter frame is enough to
            // build from and nowhere near enough to JUDGE from: I read a correctly-placed T-shaped roof off
            // this exact angle as "wings overhanging the building" and went looking for a bug that was not
            // there. A second angle is cheaper than that mistake.
            var spec = System.Environment.GetEnvironmentVariable("UG_EDITCAM");
            if (!string.IsNullOrEmpty(spec))
            {
                var p = spec.Split(',');
                float yaw = p.Length > 0 && float.TryParse(p[0], out var y) ? y : 0f;
                float dist = p.Length > 1 && float.TryParse(p[1], out var d) ? d : 34f;
                float high = p.Length > 2 && float.TryParse(p[2], out var h) ? h : 14f;
                off = new Vector3(0f, high, dist).Rotated(Vector3.Up, Mathf.DegToRad(yaw));
            }
            _cam.GlobalPosition = StageOrigin + off;
            _cam.LookAt(StageOrigin + new Vector3(0f, 2f, -4.5f), Vector3.Up);
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
                    Yaw = w.RotationDegrees.Y, Pitch = w.RotationDegrees.X, Kind = w.Kind,
                    Length = w.Length, Height = w.Height, GableRise = w.GableRise, Texel = w.Texel,
                    InsetL0 = w.InsetL0, InsetL1 = w.InsetL1, InsetR0 = w.InsetR0, InsetR1 = w.InsetR1,
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
                SpawnWall(new Vector3(pl.X, pl.Y, pl.Z), pl.Yaw, pl.Length, pl.Thickness, pl.Material,
                          pl.Openings, pl.Height, pl.Pitch, pl.Kind, pl.GableRise, pl.Texel,
                          pl.InsetL0, pl.InsetL1, pl.InsetR0, pl.InsetR1);
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

            // Corners are solved for the bake only. The .dat written below is deliberately the UNSOLVED
            // layout: reopening a building must give you back the walls you drew, at the lengths you drew
            // them, not ones silently grown by half a neighbour each time it was baked.
            var plans = Plans();
            var cornerUndo = SolveCorners();
            try { return BakeSolved(name, plans); }
            finally { RestoreCorners(cornerUndo); }
        }

        string BakeSolved(string name, List<WallPlan> plans)
        {

            // one palette for the building: every distinct wall/reveal colour in use, laid out 4 across
            var colours = new List<Color>();
            int TexelOf(Color c)
            {
                for (int i = 0; i < colours.Count; i++) if (colours[i].IsEqualApprox(c)) return i;
                if (colours.Count >= 16)
                {
                    // MatFor's palette ceiling. Returning texel 0 means the overflow walls silently wear the
                    // FIRST material's colour, and the magenta canary cannot fire because 0 is a real texel --
                    // so say so, or a five-material building looks merely odd rather than broken.
                    GD.PrintErr($"[editor-buildings] more than 16 distinct colours in one building; extra walls will wear the first palette entry");
                    return 0;
                }
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
                // w.Tint, not WallMaterials.At(w.MaterialId).Wall -- the SAME accessor the live surface
                // paints itself with. Reading the palette directly here meant a surface with a texel
                // override looked right in the editor and baked out in the wall colour: the imported roof
                // was dark grey on the stage and cream the moment it became a prop.
                foreach (var (node, colour) in new[] { ("Mesh", w.Tint), ("TrimMesh", w.TrimTint) })
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
                // ObjMesh caches by path forever, so a re-bake under the same name would keep serving the
                // FIRST bake's geometry until a restart -- exactly the loop this tool exists for.
                ObjMesh.Forget(dir + name + ".obj");
                PaletteImage(colours, pw, ph).SavePng(dir + name + "_tex.png");
                // NOTE: written in STAGE-ABSOLUTE coordinates (y ~ 2000) while the .obj beside it is
                // building-local, and there is no un-bake path yet -- so re-editing a baked building currently
                // goes through the lossy mesh importer while this lossless file sits next to it unused.
                System.IO.File.WriteAllText(BuildingSourcePath(name), WallSave.Write(plans));
                RegisterBaked(name);
            }
            catch (System.Exception e) { GD.PrintErr($"[editor-buildings] bake failed: {e.Message}"); return null; }

            GD.Print($"[editor-buildings] baked '{name}': {tris.Count / 3} tris, {colours.Count} palette colours");
            _editor?.Objects?.ReloadCatalog();
            return name;
        }

        // ---- corner solving -------------------------------------------------------------------------

        /// <summary>Extend walls through their shared corners so the outer corner is filled, returning what to
        /// put back. Applied only for the duration of a BAKE.
        ///
        /// While you are drawing, walls just interpenetrate -- that is what strawberry asked for, and it is the
        /// right behaviour: a corner that re-solves itself on every mouse move fights the drag. But two walls
        /// meeting at their centre-lines leave a quarter of a wall's thickness missing at the OUTER corner, a
        /// square notch you can see through from outside and nothing inside ever fills. So the solve happens
        /// once, at the moment the geometry stops being editable.
        ///
        /// Each wall simply runs on past the junction by half its neighbour's thickness. The overlap that
        /// creates is inside the corner post where nothing can see it, which is the same reason the reveal
        /// linings are allowed to interpenetrate the wall.</summary>
        public List<(WallSurface W, float Len, Vector3 Pos)> SolveCorners()
        {
            // LIMITATION: extending by half the neighbour's thickness fills the notch EXACTLY at 90 degrees.
            // At the 30-60 degree corners the 15-degree yaw snap allows, a true mitre needs more, so a sliver
            // of notch survives the bake (or the extension pushes through the far face).
            // 0.35 m, flat. I briefly scaled this with thickness so that imported corners would match, which
            // widened the search to over a metre and made walls that merely pass near each other count as a
            // corner. Imported walls do not need it -- see ImportRetail, they already overlap -- and drawn
            // walls meet exactly, so the slack was pure false-positive surface.
            const float Tol = 0.35f;
            var undo = new List<(WallSurface, float, Vector3)>();
            // Foundations are corner-solved too. A foundation is a wall, so it has the same missing quarter at
            // every corner -- and being underground is exactly why it would never get noticed: the notch is a
            // hole in the buried skirt that only shows when the ground falls away beside it.
            //
            // Wall and foundation corners cannot be confused for each other: the endpoints compared below are
            // at each surface's BASE, and a foundation's base is a storey underground, far outside Tol.
            var walls = new List<WallSurface>();
            foreach (var w in _walls)
                if (IsInstanceValid(w) && (w.Kind == SurfaceKind.Wall || w.Kind == SurfaceKind.Foundation)) walls.Add(w);

            // grow[i] = how far to push wall i's start back and its end forward
            var growStart = new float[walls.Count];
            var growEnd = new float[walls.Count];
            for (int i = 0; i < walls.Count; i++)
                for (int j = 0; j < walls.Count; j++)
                {
                    if (i == j) continue;
                    var a = walls[i];
                    var b = walls[j];
                    // parallel walls do not form a corner -- they form a seam, and extending them just makes
                    // two walls overlap end to end
                    float dy = Mathf.Abs(Mathf.Wrap(a.RotationDegrees.Y - b.RotationDegrees.Y, -90f, 90f));
                    if (dy < 20f) continue;

                    foreach (var (mine, isStart) in new[] { (a.UVToWorld(0f, 0f), true), (a.UVToWorld(a.Length, 0f), false) })
                        foreach (var theirs in new[] { b.UVToWorld(0f, 0f), b.UVToWorld(b.Length, 0f) })
                        {
                            if ((mine - theirs).Length() > Tol) continue;
                            float grow = b.Thickness * 0.5f;
                            if (isStart) growStart[i] = Mathf.Max(growStart[i], grow);
                            else growEnd[i] = Mathf.Max(growEnd[i], grow);
                        }
                }

            for (int i = 0; i < walls.Count; i++)
            {
                if (growStart[i] <= 0f && growEnd[i] <= 0f) continue;
                var w = walls[i];
                undo.Add((w, w.Length, w.Position));
                // the run direction in world, from the surface's own projection rather than from the yaw
                var dir = (w.UVToWorld(1f, 0f) - w.UVToWorld(0f, 0f)).Normalized();
                w.Position -= dir * growStart[i];
                w.Length += growStart[i] + growEnd[i];
                w.Rebuild();
            }
            return undo;
        }

        public void RestoreCorners(List<(WallSurface W, float Len, Vector3 Pos)> undo)
        {
            foreach (var (w, len, pos) in undo)
            {
                if (!IsInstanceValid(w)) continue;
                w.Length = len; w.Position = pos; w.Rebuild();
            }
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
                    Yaw = w.RotationDegrees.Y, Pitch = w.RotationDegrees.X, Kind = w.Kind,
                    Length = w.Length, Height = w.Height, GableRise = w.GableRise, Texel = w.Texel,
                    InsetL0 = w.InsetL0, InsetL1 = w.InsetL1, InsetR0 = w.InsetR0, InsetR1 = w.InsetR1,
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

        // ---- slabs: floors and flat roofs ------------------------------------------------------------

        /// <summary>Thickness a floor or flat roof starts at -- the measured storey pitch is 4.75, which is a
        /// 4.25 opening plus a 0.50 slab.</summary>
        public const float SlabThickness = 0.50f;

        /// <summary>Add a floor or a flat roof spanning the footprint of the walls already drawn.
        ///
        /// A slab is one of these same surfaces PITCHED FLAT, not a new kind of object: the rectangle-minus-
        /// openings problem is the same lying down, so the partition, the collider, the reveal lining around a
        /// stairwell, the palette and the bake all work already. Every test written for a wall covers it.
        ///
        /// It spans the walls rather than being drawn, because the useful floor is almost always "the one that
        /// fits this room", and dragging a rectangle that has to line up with four walls by hand is a worse
        /// version of a button.</summary>
        /// <summary>LIMITATION: the footprint is a world-axis AABB of the wall endpoints, so this is only
        /// correct for a building whose walls run along X or Z. Draw at 15/30/45 degrees -- which the wall tool
        /// allows -- and the slab is the AABB of the rotated footprint, overhanging on every side, which is
        /// exactly what the retail measurement says should not happen. Mixed thicknesses also grow by the
        /// MAX half-thickness on all four sides, so a 0.50 partition meeting a 0.70 wall overhangs by 0.10.</summary>
        public WallSurface AddSlab(SurfaceKind kind)
        {
            if (_walls.Count == 0) return null;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            float baseY = float.MaxValue, topY = float.MinValue;
            int seen = 0, material = ActiveMaterial;
            float maxWallThickness = WallOpenings.DefaultThickness;
            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w) || w.Kind != SurfaceKind.Wall) continue;   // slabs do not stack on slabs
                maxWallThickness = Mathf.Max(maxWallThickness, w.Thickness);
                foreach (float u in new[] { 0f, w.Length })
                {
                    var p = w.UVToWorld(u, 0f);
                    minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
                    minZ = Mathf.Min(minZ, p.Z); maxZ = Mathf.Max(maxZ, p.Z);
                }
                baseY = Mathf.Min(baseY, w.Position.Y);
                topY = Mathf.Max(topY, w.Position.Y + w.Height);
                material = w.MaterialId;      // a slab wears the walls' palette, not whatever the picker last showed
                seen++;
            }
            if (seen == 0) return null;

            // FLUSH WITH THE OUTER WALL FACE. I had put half a thickness of lip on this because it looked
            // more like a roof; strawberry asked whether retail does that and it does not -- 24 of 26
            // buildings measured have the roof and floor ending on the wall face within 6 cm, and the two that
            // differ have a smaller upper storey rather than an overhang.
            //
            // The footprint above is measured on the wall MID-planes (UVToWorld's v=0 line lies at local z=0),
            // so flush means growing by half a wall. Stopping at the centre-line instead leaves the outer half
            // of every wall poking through the roof, which is a thin bright seam rather than an obvious fault.
            float half = maxWallThickness * 0.5f;
            minX -= half; maxX += half; minZ -= half; maxZ += half;

            // The slab's top lands where you would stand on it: at the walls' base for a floor, at their head
            // for a roof. Thickness runs along local Z, which the -90 pitch turns into world up.
            float top = kind == SurfaceKind.Roof ? topY + SlabThickness : baseY;
            var origin = new Vector3(minX, top - SlabThickness * 0.5f, maxZ);
            var slab = SpawnWall(origin, 0f, maxX - minX, SlabThickness, material, null,
                                 maxZ - minZ, -90f, kind);
            _editor?.PushUndo(kind == SurfaceKind.Roof ? "roof place" : "floor place", () => RemoveWall(slab));
            return slab;
        }

        /// <summary>Port a retail building into editable walls, REPLACING whatever is on the stage.
        ///
        /// Replacing rather than adding: an import lands on the same footprint every time, so merging it into
        /// a building already being drawn just stacks two buildings in the same place and leaves you to pick
        /// them apart by hand.</summary>
        public int ImportRetail(string buildingName)
        {
            if (string.IsNullOrWhiteSpace(buildingName)) return 0;
            string obj = ProjectSettings.GlobalizePath("res://content/objects/") + buildingName + ".obj";
            if (!System.IO.File.Exists(obj)) { GD.PrintErr($"[editor-buildings] no mesh for {buildingName}"); return 0; }

            int mat = 0;
            for (int i = 0; i < WallMaterials.Count; i++)
                if (WallMaterials.At(i).Name == buildingName) { mat = i; break; }   // its own palette, if we have it

            var plans = BuildingImport.FromObj(obj, mat);
            if (plans.Count == 0) return 0;

            foreach (var w in new List<WallSurface>(_walls)) RemoveWall(w);
            foreach (var pl in plans)
                SpawnWall(StageOrigin + new Vector3(pl.X, pl.Y, pl.Z), pl.Yaw, pl.Length, pl.Thickness,
                          pl.Material, pl.Openings, pl.Height, pl.Pitch, pl.Kind, pl.GableRise, pl.Texel,
                          pl.InsetL0, pl.InsetL1, pl.InsetR0, pl.InsetR1);
            // NO corner solving on an import, and the reason is worth keeping: an imported wall does not
            // have the notch the solver exists to fill.
            //
            // Corner solving is for DRAWN walls, which are laid endpoint to endpoint on their centrelines and
            // so leave a missing quarter at every corner. An imported wall is recovered from a facade PLANE
            // that spans the whole building, so it already runs into its neighbour: measured on House_00 the
            // west facade ends at Z 8.00 and the south wall occupies Z 7.35..8.05, so it terminates inside it
            // and the corner is already solid. Extending it another half-thickness pushes it out to 8.35 --
            // 0.30 m proud of the building -- and every corner grows a pilaster. strawberry_cow, immediately:
            // "nope now its extending wayyy too far."
            ActiveMaterial = mat;
            int nw = 0, nr = 0, nf = 0, nop = 0, ngab = 0;
            foreach (var pl in plans)
            {
                if (pl.Kind == SurfaceKind.Roof) nr++;
                else if (pl.Kind == SurfaceKind.Foundation) nf++;
                else nw++;
                if (pl.GableRise > 0.01f) ngab++;
                nop += pl.Openings.Count;
            }
            // UG_EDITIMPORT_DUMP=1 lists every emitted surface with its world extent. A render shows you
            // THAT the import is wrong; only the extents tell you which surface is missing, and I read one
            // three-quarter frame as an oversized roof when the roof was right and the walls were absent.
            if (System.Environment.GetEnvironmentVariable("UG_EDITIMPORT_DUMP") == "1")
                foreach (var pl in plans)
                {
                    var rt = new Vector3(Mathf.Cos(Mathf.DegToRad(pl.Yaw)), 0f, -Mathf.Sin(Mathf.DegToRad(pl.Yaw)));
                    var o = new Vector3(pl.X, pl.Y, pl.Z);
                    var e = o + rt * pl.Length + Vector3.Up * pl.Height;
                    GD.Print($"[import]  {pl.Kind,-10} {pl.Length,6:0.0} x {pl.Height,5:0.0}  yaw {pl.Yaw,7:0.0}  pitch {pl.Pitch,6:0.0}"
                             + $"  thick {pl.Thickness:0.00}  gable {pl.GableRise:0.0}  ops {pl.Openings.Count}"
                             + $"  inset L {pl.InsetL0:0.0}/{pl.InsetL1:0.0} R {pl.InsetR0:0.0}/{pl.InsetR1:0.0}"
                             + $"  texel {pl.Texel}"
                             + $"   X {Mathf.Min(o.X, e.X),6:0.0}..{Mathf.Max(o.X, e.X),6:0.0}"
                             + $"  Y {Mathf.Min(o.Y, e.Y),6:0.0}..{Mathf.Max(o.Y, e.Y),6:0.0}"
                             + $"  Z {Mathf.Min(o.Z, e.Z),6:0.0}..{Mathf.Max(o.Z, e.Z),6:0.0}");
                }
            GD.Print($"[editor-buildings] imported {buildingName}: {plans.Count} surfaces "
                     + $"({nw} wall, {nr} roof, {nf} foundation, {ngab} gabled) with {nop} openings");
            return plans.Count;
        }

        /// <summary>Measured retail roof pitches, area-weighted across the 52 buildings. Snap targets, not
        /// law. 0 is first and is the DEFAULT because 80% of retail roof area is flat -- a pitched roof is the
        /// special case here, which is the opposite of what a house-shaped intuition suggests.</summary>
        public static readonly float[] RoofPitches = { 0f, 12f, 15f, 18f, 20f, 22f, 27f, 30f, 45f };
        // Flat, because that is what the measurement says -- 80% of retail roof AREA. It read 20 here while
        // the comment above and the button tooltip both claimed flat was the default, so the first press of
        // Add roof built a gable on a fresh session.
        public float ActiveRoofPitch;

        /// <summary>Add a gable roof: two sloped surfaces meeting at a ridge, and the end walls raised into
        /// gable ends to close it.
        ///
        /// The triangular end is put on the WALL, not on the roof, because that is what it is -- retail gable
        /// ends are the wall carrying on up to the roof line. It also keeps the roof pieces rectangular, so
        /// they stay ordinary surfaces.</summary>
        /// <summary>LIMITATION: axis-aligned only, same as AddSlab, and the gable cap assumes the end wall
        /// spans the whole footprint -- its apex sits at the WALL's midpoint. An L-shaped plan, an offset end
        /// wall or a diagonal one gets a triangle that misses the roof planes. The along/across-ridge
        /// classification is a 45-degree threshold, which is meaningless for a diagonal wall.</summary>
        public int AddGableRoof(float pitchDeg)
        {
            if (pitchDeg <= 0.1f) return AddSlab(SurfaceKind.Roof) != null ? 1 : 0;   // flat is a slab
            pitchDeg = Mathf.Clamp(pitchDeg, 1f, 70f);

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            float topY = float.MinValue;
            int seen = 0, material = ActiveMaterial;
            float maxWallThickness = WallOpenings.DefaultThickness;
            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w) || w.Kind != SurfaceKind.Wall) continue;
                maxWallThickness = Mathf.Max(maxWallThickness, w.Thickness);
                foreach (float u in new[] { 0f, w.Length })
                {
                    var p = w.UVToWorld(u, 0f);
                    minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
                    minZ = Mathf.Min(minZ, p.Z); maxZ = Mathf.Max(maxZ, p.Z);
                }
                topY = Mathf.Max(topY, w.Position.Y + w.Height);
                material = w.MaterialId;
                seen++;
            }
            if (seen == 0) return 0;

            // flush with the outer wall face, same as a flat roof -- see AddSlab
            float halfW = maxWallThickness * 0.5f;
            minX -= halfW; maxX += halfW; minZ -= halfW; maxZ += halfW;

            float spanX = maxX - minX, spanZ = maxZ - minZ;
            bool ridgeAlongX = spanX >= spanZ;                 // the ridge runs the LONG way, as a roof does
            float half = (ridgeAlongX ? spanZ : spanX) * 0.5f;
            float th = Mathf.DegToRad(pitchDeg);
            float rise = half * Mathf.Tan(th);
            float slope = half / Mathf.Cos(th);
            float pitchNode = pitchDeg - 90f;                  // -90 is flat; adding the pitch tilts it up

            var made = new List<WallSurface>();
            if (ridgeAlongX)
            {
                made.Add(SpawnWall(new Vector3(minX, topY, maxZ), 0f, spanX, SlabThickness, material, null, slope, pitchNode, SurfaceKind.Roof));
                made.Add(SpawnWall(new Vector3(maxX, topY, minZ), 180f, spanX, SlabThickness, material, null, slope, pitchNode, SurfaceKind.Roof));
            }
            else
            {
                made.Add(SpawnWall(new Vector3(minX, topY, minZ), -90f, spanZ, SlabThickness, material, null, slope, pitchNode, SurfaceKind.Roof));
                made.Add(SpawnWall(new Vector3(maxX, topY, maxZ), 90f, spanZ, SlabThickness, material, null, slope, pitchNode, SurfaceKind.Roof));
            }

            // Raise the walls that run ACROSS the ridge into gable ends. A wall parallel to the ridge stays
            // flat-topped -- putting a peak on all four is the classic wrong-looking roof.
            var raised = new List<(WallSurface W, float Prev)>();
            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w) || w.Kind != SurfaceKind.Wall) continue;
                float yaw = Mathf.Wrap(w.RotationDegrees.Y, 0f, 180f);
                bool runsAlongX = yaw < 45f || yaw > 135f;
                if (runsAlongX == ridgeAlongX) continue;       // parallel to the ridge: no gable
                raised.Add((w, w.GableRise));
                w.GableRise = rise;
                w.Rebuild();
            }

            _editor?.PushUndo("gable roof", () =>
            {
                foreach (var m in made) RemoveWall(m);
                foreach (var (w, prev) in raised) if (IsInstanceValid(w)) { w.GableRise = prev; w.Rebuild(); }
            });
            return made.Count + raised.Count;
        }

        /// <summary>Hang a foundation under every wall drawn: a hollow skirt, which is what retail is.
        ///
        /// It follows whatever footprint you drew, including a non-rectangular one, because it is built per
        /// wall rather than from a bounding box -- and it needs no geometry of its own, being a wall.</summary>
        public int AddFoundation(float depth = WallOpenings.FoundationDepth)
        {
            var made = new List<WallSurface>();
            foreach (var w in new List<WallSurface>(_walls))
            {
                if (!IsInstanceValid(w) || w.Kind != SurfaceKind.Wall) continue;
                // directly under its wall, same run and thickness, reaching down by `depth`
                var origin = w.Position - new Vector3(0f, depth, 0f);
                var f = SpawnWall(origin, w.RotationDegrees.Y, w.Length, w.Thickness, w.MaterialId, null,
                                  depth, w.RotationDegrees.X, SurfaceKind.Foundation);
                made.Add(f);
            }
            if (made.Count == 0) return 0;
            _editor?.PushUndo("foundation place", () => { foreach (var f in made) RemoveWall(f); });
            return made.Count;
        }

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

        /// <summary>Find the opening under the cursor by intersecting the ray with each wall's PLANE, taking
        /// the nearest. Needed because an opening is a hole in the collider and therefore invisible to a
        /// physics ray -- see the note in OnPress.</summary>
        bool PickOpening(Vector3 from, Vector3 dir, out WallSurface wall, out int index, out float u, out float v)
        {
            wall = null; index = -1; u = v = 0f;
            float best = float.MaxValue;
            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w)) continue;
                if (!w.RayToUV(from, dir, out float wu, out float wv)) continue;
                int idx = w.OpeningAt(wu, wv);
                if (idx < 0) continue;
                float d = from.DistanceSquaredTo(w.UVToWorld(wu, wv));
                if (d >= best) continue;
                best = d; wall = w; index = idx; u = wu; v = wv;
            }
            return index >= 0;
        }

        /// <summary>Show where the armed opening would go, on the wall under the cursor. Deliberately a
        /// flat translucent panel and not a second copy of the wall: it marks the RECT, and the rect comes
        /// from PlannedOpening, the same call that places it.</summary>
        void UpdateGhost(Vector2 screen)
        {
            if (_armed < 0 || _cam == null) { HideGhost(); return; }
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);
            WallSurface best = null;
            float bu = 0f, bv = 0f, bd = float.MaxValue;
            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w) || w.Kind != SurfaceKind.Wall) continue;
                if (!w.RayToUV(from, dir, out float u, out float v)) continue;
                float d = from.DistanceSquaredTo(w.UVToWorld(u, v));
                if (d < bd) { bd = d; best = w; bu = u; bv = v; }
            }
            if (best == null) { HideGhost(); return; }

            var o = PlannedOpening(best, bu, bv, _armed);
            if (_ghost == null)
            {
                _ghost = new MeshInstance3D
                {
                    Mesh = new QuadMesh { Size = Vector2.One },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.35f, 0.8f, 1f, 0.35f),
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                        NoDepthTest = true,
                    },
                };
                AddChild(_ghost);
            }
            ((QuadMesh)_ghost.Mesh).Size = new Vector2(Mathf.Max(0.05f, o.Width), Mathf.Max(0.05f, o.Height));
            _ghost.GlobalTransform = new Transform3D(best.GlobalTransform.Basis,
                best.UVToWorld(o.U + o.Width * 0.5f, o.V + o.Height * 0.5f)
                + best.GlobalTransform.Basis.Z.Normalized() * (best.Thickness * 0.5f + 0.02f));
            _ghost.Visible = true;
            ShowReadout(best, o);
        }

        void HideGhost()
        {
            if (_ghost != null) _ghost.Visible = false;
            if (_armed >= 0) HideReadout();
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
            if (_selOpening >= 0 && _selOpening < _selWall.Openings.Count)
                ShowReadout(_selWall, _selWall.Openings[_selOpening]);
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
