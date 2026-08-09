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
        public enum Drag { None, Move, EdgeU0, EdgeU1, EdgeV0, EdgeV1, MoveWall }
        Drag _drag = Drag.None;
        float _grabDU, _grabDV;
        WallOpening[] _dragCapture;
        Vector3 _wallGrab, _wallFrom;          // where the drag started, and where the wall was
        WallSurface _dragWall;                 // the wall the drag STARTED on, which may not be where it ends
        List<WallPlan> _dragStage;             // stage snapshot, for a drag that crosses walls

        // Hold-to-repeat undo. The lead-in stops a normal Ctrl+Z from firing twice on a slow keypress;
        // after it, repeats run fast enough to walk back a bad five minutes without hammering the key.
        const double UndoRepeatDelay = 0.45, UndoRepeatEvery = 0.07;
        double _undoHeld = -1.0, _undoNext;

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
            /// <summary>Does a freshly placed one of these come with glass in it? strawberry_cow: "toggleable
            /// on/off so not every opening is a window". The preset is the default, not a lock -- any opening
            /// can be glazed or unglazed afterwards, including a garage door if that is what you want.</summary>
            public bool Glazed;
            public Archetype(string n, float w, float h, float sill, bool pinned, bool glazed = false)
            { Name = n; Width = w; Height = h; Sill = sill; FloorPinned = pinned; Glazed = glazed; }
        }

        public static readonly Archetype[] Archetypes =
        {
            // 2.85 is the DOOR LEAF (Door_Pine/Door_Metal measure 2.45 x 0.51 x 2.80) plus the same 0.05 of
            // clearance the width already carried. It used to be DoorHeight - 0.5 = 3.75, which came from the
            // WALL: DoorHeight is misnamed -- it is retail WALL_HEIGHT, and StoreyHeight/WallSurface.Height
            // both read it. So the doorway was "a wall, less half a metre" and never had anything to do with a
            // door; 3.75 is in fact GATE_Pine's height. A door hung in it got scaled up ~34% to fill it
            // (strawberry_cow: "the default doorways opening size is too big? where did it come from lol").
            new("door",    2.5f,  2.85f,                          0f,   true),
            new("window",  3.31f, WallOpenings.WindowHeight,      WallOpenings.WindowSill, false, glazed: true),
            new("tall win",2.81f, 2.97f,                          0.88f, false, glazed: true),
            // Sized to the GATE that fills it (Gate_Birch/Maple/Pine/Metal are all exactly 4.00 x 0.51 x 3.75),
            // plus the same 0.05. It was 8.0 x 4.0 -- twice the gate's width. Not a double bay: a WallOpening
            // carries ONE DoorProp, so an 8.0 hole cannot hold two 4.0 gates whatever was intended.
            new("garage",  4.05f, 3.80f,                          0f,  true),
            new("porch",   5.5f,  WallOpenings.DoorHeight,        0f,   true),
            new("vent",    1.0f,  1.0f,                           2.5f, false),
        };

        // ---- glazing options applied to NEWLY placed openings ------------------------------------------
        // "complete with plenty of options. color hue, mark indestructable, set hp, etc." -- these are the
        // panel's current settings, stamped onto an opening as it is placed. Editing an existing opening
        // goes through SetOpeningGlass instead, so the two paths cannot drift into different rules.

        /// <summary>0xRRGGBB, or 0 for the pane's own default glass blue-grey.</summary>
        public int ActiveGlassTint;
        public float ActiveGlassHp = 1f;
        public bool ActiveGlassIndestructible;
        /// <summary>Force glazing on/off for new openings regardless of the archetype preset. Null = follow
        /// the preset, which is what makes "window" glazed and "garage" not without anyone choosing.</summary>
        public bool? GlazeNew;

        /// <summary>Prop name hung in new FLOOR-PINNED openings; null = leave the hole empty (the default, so
        /// nothing starts carrying a door nobody asked for). Only floor-pinned archetypes take it -- a door
        /// stamped into a window is not a thing anyone means by "default door", and FloorPinned is already the
        /// flag that separates door/garage/porch from window/vent.</summary>
        public string ActiveDoorProp;

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
        /// <summary>Which storey you are building on. Everything you place lands at this height instead of
        /// on the terrain, so a second floor is a thing you switch to rather than a height you have to hit.
        ///
        /// Q and E move down and up. They are the camera's ascend/descend while RMB-flying, and this input
        /// handler already returns early in that case, so the two never both fire.</summary>
        public int ActiveFloor;
        public static float StoreyHeight => WallOpenings.DoorHeight;
        public float FloorY => ActiveFloor * StoreyHeight;

        /// <summary>How far a building's floor line sits ABOVE the ground it stands on.
        ///
        /// At zero the floor slab is exactly at ground level and the foundation is entirely buried, so two
        /// of the things the tool builds are invisible in every view -- you are asked to trust that the floor
        /// and the skirt are down there. strawberry_cow: "make buildings sit slightly above the ground (and
        /// all the tools adapted to the new height) so we can see the floor/foundys."
        ///
        /// Applied at the single seam where a placement turns terrain into a height, so everything derived
        /// from the walls -- slabs, roofs, foundations, gables -- follows without knowing about it.</summary>
        public const float GroundClearance = 0.25f;

        public bool WallDrawMode;

        /// <summary>Draw a roof or floor as a RECTANGLE you drag, instead of one auto-fitted to the current
        /// walls. Auto-fit is a guess about what you meant and there is no way to argue with it -- it takes
        /// the bounding box of every wall, so an L-shaped building gets a slab over the courtyard and a
        /// building mid-edit gets a slab over whatever happens to exist. strawberry_cow: "roof tool should
        /// be a drag rect instead of an auto-bake tool." Add roof/Add floor still auto-fit for the simple
        /// case; this is the one you reach for when it guesses wrong.</summary>
        /// <summary>Drag a rectangle and get four walls on the grid. The common case is a room and laying it
        /// one wall at a time is four draws that all have to agree with each other.</summary>
        /// <summary>Drag a rectangle and get a foundation skirt around it, without needing walls first.
        /// AddFoundation puts one under each wall you have already drawn, which is no use when the
        /// foundation is the thing you want to lay out first.</summary>
        public bool FoundationDrawMode;

        public bool RoomDrawMode;
        readonly List<WallSurface> _room = new();
        Vector3 _roomAnchor;
        SurfaceKind _roomKind = SurfaceKind.Wall;
        public bool DrawingRoom => _room.Count > 0;

        /// <summary>Delete tool. Click a wall to remove it; drag along one to cut that span out of it.</summary>
        public bool DeleteDrawMode;
        WallSurface _cutWall;
        float _cutFrom;

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
        public string ToolText => $"floor {ActiveFloor} (Q/E) · " + ToolName;
        string ToolName => FoundationDrawMode ? "foundation: drag a rectangle"
                                : DeleteDrawMode ? "delete: click a wall, or drag along one to cut a piece out"
                                : SlabDrawMode ? (_drawingSlab != null ? $"drag the {SlabDrawKind.ToString().ToLower()} out — release to place" : $"{SlabDrawKind.ToString().ToLower()}: drag a rectangle")
                                : WallDrawMode ? (_drawing != null ? "drag the wall out — release to place, Esc cancels" : "wall: press and drag")
                                : _armed >= 0 ? $"placing {Archetypes[Mathf.PosMod(_armed, Archetypes.Length)].Name}"
                                : "select";

        /// <summary>Which SIDE of the selected wall the material picker will paint. Set by which face you
        /// clicked, not by a mode.
        ///
        /// This was a "paint back side" checkbox, which is a modal flag standing in for a selection: you had
        /// to remember which way it was set and there was nothing on screen telling you. strawberry_cow:
        /// "why is the painting tool a paint back side toggle instead of just being able to select (with a
        /// selection ghost) either side of the wall and painting it." Click the side you mean; the ghost
        /// shows you which one you have.</summary>
        public bool SelectedBack { get; private set; }
        MeshInstance3D _sideGhost;

        /// <summary>Select a wall and which of its two faces you are working on. The click path and the
        /// tests both come through here, so there is one definition of "the selected side".</summary>
        public void SelectSide(WallSurface w, bool back)
        {
            _selWall = w;
            _selOpening = -1;
            SelectedBack = back;
            if (w == null) HideSideGhost(); else ShowSideGhost(w, back);
            PositionHandles();
        }

        public void SetMaterial(WallSurface w, int id)
        {
            if (w == null) return;
            int wrapped = Mathf.PosMod(id, Mathf.Max(1, WallMaterials.Count));
            if (SelectedBack)
            {
                int wasBack = w.MaterialIdBack;
                w.MaterialIdBack = wrapped;
                w.Rebuild();
                _editor?.PushUndo("wall material (back)",
                    () => { if (IsInstanceValid(w)) { w.MaterialIdBack = wasBack; w.Rebuild(); } });
                return;
            }
            int before = w.MaterialId;
            w.MaterialId = wrapped;
            w.Rebuild();
            _editor?.PushUndo("wall material", () => { if (IsInstanceValid(w)) { w.MaterialId = before; w.Rebuild(); } });
        }

        /// <summary>Step the palette of the selected wall, or of every wall when nothing is selected -- a
        /// building is normally one material, so recolouring all of it is the common case.</summary>
        public void CycleMaterial(int delta)
        {
            if (WallMaterials.Count == 0) return;
            if (_selWall != null) { SetMaterial(_selWall, _selWall.MaterialId + delta); ActiveMaterial = _selWall.MaterialId; return; }
            // Recolouring EVERY wall was the one edit with no undo behind it: a stray scroll repainted the
            // whole building and Ctrl+Z could not take it back.
            var before = Snapshot();
            ActiveMaterial = Mathf.PosMod(ActiveMaterial + delta, WallMaterials.Count);
            foreach (var w in _walls) { w.MaterialId = ActiveMaterial; w.Rebuild(); }
            _editor?.PushUndo("recolour building", () => RestoreAll(before));
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
                              float insetR0 = 0f, float insetR1 = 0f,
                              int materialBack = -1, int texelBack = -1)
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
                MaterialIdBack = materialBack, TexelBack = texelBack,
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
            o.Glazed = GlazeNew ?? a.Glazed;
            o.GlassTint = ActiveGlassTint;
            o.GlassHp = ActiveGlassHp;
            o.GlassIndestructible = ActiveGlassIndestructible;
            if (a.FloorPinned) o.DoorProp = ActiveDoorProp;   // door/garage/porch can carry one; a window cannot
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

        /// <summary>Change one opening's glazing. Every field is optional so the panel can flip one control
        /// without restating the rest -- a tint picker that also had to resend "hp" would eventually send a
        /// stale one. Undoable, because strawberry_cow's standing complaint about this tool is things that
        /// Ctrl+Z cannot walk back.</summary>
        public void SetOpeningGlass(WallSurface w, int index, bool? glazed = null, int? tint = null,
                                    float? hp = null, bool? indestructible = null, bool? broken = null)
        {
            if (w == null || !IsInstanceValid(w) || index < 0 || index >= w.Openings.Count) return;
            var before = w.Openings.ToArray();
            var o = w.Openings[index];
            if (glazed.HasValue) o.Glazed = glazed.Value;
            if (tint.HasValue) o.GlassTint = tint.Value;
            if (hp.HasValue) o.GlassHp = hp.Value;
            if (indestructible.HasValue) o.GlassIndestructible = indestructible.Value;
            if (broken.HasValue) o.GlassBroken = broken.Value;
            // Re-glazing a smashed window has to clear the smash, or turning glass off and on again leaves an
            // opening that claims to be glazed and shows nothing.
            if (glazed == true && !broken.HasValue) o.GlassBroken = false;
            if (o.Equals(w.Openings[index])) return;      // no-op: pushing an undo step here makes Ctrl+Z look broken
            w.Openings[index] = o;
            w.Rebuild();
            _editor?.PushUndo("glass", () => Restore(w, before));
        }

        /// <summary>Hang a door in one opening, or clear it with null. Same shape as SetOpeningGlass -- undoable,
        /// no-ops when nothing changes -- because the door rides the opening exactly like the glass does and a
        /// second set of rules for it would be a second set of bugs.
        ///
        /// The whole data path for this existed for hours before this method did: WallOpening.DoorProp,
        /// WallSurface.PlaceDoor, save/load, bake and undo all handled it, and it had green tests. The only
        /// callers were the demo room and the tests, so the feature was real and unreachable at the same time
        /// (strawberry_cow: "how do i add a door to a door opening?" -- you could not).</summary>
        public void SetOpeningDoor(WallSurface w, int index, string prop)
        {
            if (w == null || !IsInstanceValid(w) || index < 0 || index >= w.Openings.Count) return;
            if (string.IsNullOrEmpty(prop)) prop = null;    // "" and null both mean no door; store one of them
            var before = w.Openings.ToArray();
            var o = w.Openings[index];
            if (o.DoorProp == prop) return;                 // no-op: an undo step here makes Ctrl+Z look broken
            o.DoorProp = prop;
            if (prop == null) o.DoorOpen = false;           // a hole cannot be ajar; leaving it set re-opens the next door hung here
            w.Openings[index] = o;
            w.Rebuild();
            _editor?.PushUndo("door", () => Restore(w, before));
        }

        /// <summary>Flip glazing on the selected opening. Bound to a key + the panel button.</summary>
        public void ToggleGlass(WallSurface w, int index)
        {
            if (w == null || index < 0 || index >= w.Openings.Count) return;
            SetOpeningGlass(w, index, glazed: !w.Openings[index].Glazed);
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

        public override void _Process(double delta)
        {
            // Hold Z (with Ctrl) to keep undoing. Driven from _Process rather than key auto-repeat so the
            // rate is ours and not the OS keyboard setting, which varies per machine and is usually far too
            // slow to be useful for walking back a mistake.
            if (_undoHeld < 0.0) return;
            if (!Active || _editor == null || _editor.Mode != EEditorMode.Buildings
                || !Input.IsKeyPressed(Key.Z) || !Input.IsKeyPressed(Key.Ctrl))
            { _undoHeld = -1.0; return; }
            _undoHeld += delta;
            if (_undoHeld < _undoNext) return;
            _undoNext += UndoRepeatEvery;
            UndoOnce();
        }

        void UndoOnce()
        {
            if (_drawing != null) { CancelDraw(); return; }
            if (_room.Count > 0) { foreach (var w in new List<WallSurface>(_room)) RemoveWall(w); _room.Clear(); _editor?.PopUndo(); return; }
            if (_drawingSlab != null) { RemoveWall(_drawingSlab); _drawingSlab = null; _editor?.PopUndo(); return; }
            _editor?.Undo();
            _selOpening = -1;
            PositionHandles();
        }

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
                else if (_drag == Drag.MoveWall)
                {
                    var w = _selWall; var was = _wallFrom;
                    _drag = Drag.None;
                    HideReadout();
                    if (w != null && IsInstanceValid(w) && w.Position != was)
                    {
                        var moved = w;
                        _editor?.PushUndo("wall move", () =>
                        { if (IsInstanceValid(moved)) { moved.Position = was; moved.Rebuild(); } });
                        Undoable("merge after move", MergeDuplicateWalls);
                        SolveCornersNow();
                    }
                }
                else if (_drag != Drag.None)
                {
                    var cap = _dragCapture; var w = _dragWall;
                    var stage = _dragStage;
                    _drag = Drag.None; _dragCapture = null; _dragWall = null; _dragStage = null;
                    HideReadout();
                    // If the opening hopped walls, one wall's snapshot cannot undo it -- two walls changed.
                    // Fall back to the whole-stage step so the gesture is still a single Ctrl+Z.
                    if (stage != null && w != _selWall) _editor?.PushUndo("opening moved to another wall",
                                                                         () => RestoreAll(stage));
                    else if (cap != null && w != null) _editor.PushUndo("opening edit", () => Restore(w, cap));
                }
                // Release finishes the wall. Click-to-start/click-to-finish left the tool in a state that
                // looks identical to idle while it is actually mid-wall, and every other drag in this editor
                // is press-move-release.
                else if (_drawing != null)
                {
                    StretchDraw(mp);
                    _drawing = null;               // AddWall already pushed the undo
                    HideReadout();
                    Undoable("merge duplicate walls", MergeDuplicateWalls);
                    SolveCornersNow();
                }
                else if (_room.Count > 0)
                {
                    StretchRoom(mp);
                    bool tiny = _room[0].Length < 0.5f || _room[1].Length < 0.5f;
                    if (tiny) { foreach (var w in new List<WallSurface>(_room)) RemoveWall(w); _editor?.PopUndo(); }
                    _room.Clear();
                    HideReadout();
                    if (!tiny) { Undoable("merge duplicate walls", MergeDuplicateWalls); SolveCornersNow(); }
                }
                else if (_cutWall != null)
                {
                    // a click deletes the wall; a drag takes out the span you dragged over
                    var w = _cutWall; _cutWall = null;
                    if (!IsInstanceValid(w)) return;
                    float to = _cutFrom;
                    if (PickWallAt(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp), out var same, out float u)
                        && same == w) to = u;
                    var before = Snapshot();
                    if (Mathf.Abs(to - _cutFrom) < 0.15f) RemoveWall(w);
                    else if (RemoveSpan(w, _cutFrom, to) == 0) return;
                    _editor?.PushUndo("wall cut", () => RestoreAll(before));
                }
                else if (_drawingSlab != null)
                {
                    StretchSlab(mp);
                    var slab = _drawingSlab;
                    _drawingSlab = null;
                    HideReadout();
                    // a slab dragged to nothing is a misclick, not a surface
                    if (slab.Length < 0.5f || slab.Height < 0.5f) { RemoveWall(slab); _editor?.PopUndo(); }
                    else if (SlabDrawKind == SurfaceKind.Roof && ActiveRoofPitch > 0.1f)
                    {
                        // A PITCHED roof drawn as a rect is a gable over that footprint, not the single
                        // slope the drag showed. The drag draws the footprint flat -- honest about being an
                        // area rather than pretending to be the result -- and the release builds the gable
                        // and raises the end walls, exactly as Add roof does.
                        var c0 = slab.UVToWorld(0f, 0f);
                        var c1 = slab.UVToWorld(slab.Length, slab.Height);
                        float y = slab.Position.Y + SlabThickness * 0.5f;
                        int mat = slab.MaterialId;
                        RemoveWall(slab);
                        _editor?.PopUndo();
                        // BuildGableOver pushes its OWN step, which also puts the raised gable-end walls
                        // back. Wrapping it in a second one here would make the gesture take two Ctrl+Z
                        // presses -- the exact thing the wall-move and cross-wall-drag paths go out of
                        // their way to avoid.
                        // The rect you drag is the BUILDING footprint, not the roof's outer edge, so it
                        // gets the same overhang auto-fit applies -- strawberry_cow: "the draw roof tool
                        // doesnt do overhangs". A drawn FLAT roof deliberately does not come through here
                        // and stays flush, which is the correction they made earlier.
                        float oh = WallOpenings.DefaultThickness * 0.5f + RoofOverhang;
                        BuildGableOver(Mathf.Min(c0.X, c1.X) - oh, Mathf.Max(c0.X, c1.X) + oh,
                                       Mathf.Min(c0.Z, c1.Z) - oh, Mathf.Max(c0.Z, c1.Z) + oh,
                                       y, ActiveRoofPitch, mat, WallOpenings.DefaultThickness);
                    }
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
                    else { _selWall = null; HideSideGhost(); }
                    PositionHandles();
                }
                else if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key6) _armed = (int)(k.Keycode - Key.Key1);
                else if (k.Keycode == Key.E) ActiveFloor = Mathf.Min(ActiveFloor + 1, 12);
                else if (k.Keycode == Key.Q) ActiveFloor = Mathf.Max(ActiveFloor - 1, 0);
                // Ctrl+Z. The undo STACK was always here -- every wall, opening and edit pushes onto it --
                // but the key was only ever bound in EditorObjects, so in Buildings mode there was nothing
                // to press. An undo history nobody can reach is the same as no undo history.
                else if (k.CtrlPressed && k.Keycode == Key.Z)
                {
                    UndoOnce();
                    _undoHeld = 0.0; _undoNext = UndoRepeatDelay;   // arm hold-to-repeat
                }
            }
        }

        void OnPress(Vector2 screen)
        {
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);

            if (DeleteDrawMode)
            {
                if (PickWallAt(from, dir, out var dw, out float du))
                {
                    _cutWall = dw; _cutFrom = du;
                    _selWall = null; _selOpening = -1; PositionHandles();
                }
                return;
            }

            if (RoomDrawMode || FoundationDrawMode)
            {
                if (_room.Count == 0 && GroundOnFloor(from, dir, out var rp))
                {
                    _roomAnchor = new Vector3(WallOpenings.SnapGrid(rp.X), rp.Y, WallOpenings.SnapGrid(rp.Z));
                    _selWall = null; _selOpening = -1; PositionHandles();
                    _roomKind = FoundationDrawMode ? SurfaceKind.Foundation : SurfaceKind.Wall;
                    float h = _roomKind == SurfaceKind.Foundation ? WallOpenings.FoundationDepth
                                                                  : WallOpenings.DoorHeight;
                    // a foundation hangs BELOW the level you drew it on; a room stands on it
                    if (_roomKind == SurfaceKind.Foundation) _roomAnchor.Y -= h;
                    for (int i = 0; i < 4; i++)
                        _room.Add(SpawnWall(_roomAnchor, i * 90f, 0.01f, NewWallThickness, ActiveMaterial, null,
                                            h, 0f, _roomKind));
                    var made = new List<WallSurface>(_room);
                    _editor?.PushUndo(_roomKind == SurfaceKind.Foundation ? "foundation draw" : "room place",
                                      () => { foreach (var w in made) RemoveWall(w); });
                }
                return;
            }

            if (SlabDrawMode)
            {
                if (_drawingSlab == null && GroundOnFloor(from, dir, out var sp))
                {
                    _slabAnchor = new Vector3(WallOpenings.SnapGrid(sp.X), SlabTopY(sp.Y), WallOpenings.SnapGrid(sp.Z));
                    _selWall = null; _selOpening = -1; PositionHandles();
                    _drawingSlab = SpawnWall(_slabAnchor, 0f, 0.01f, SlabThickness, ActiveMaterial, null,
                                             0.01f, -90f, SlabDrawKind);
                    // Capture the SURFACE in a local, not the _drawingSlab field. The closure used to read
                    // the field, which is nulled the moment the drag ends -- so by the time you pressed
                    // Ctrl+Z it removed nothing and the step silently did nothing. Auto-fit Add roof was
                    // fine because it already captured a local, which is exactly why this only ever failed
                    // "sometimes". strawberry_cow: "it doesnt undo roofs properly sometimes".
                    var drawn = _drawingSlab;
                    _editor?.PushUndo(SlabDrawKind == SurfaceKind.Roof ? "roof draw" : "floor draw",
                                      () => RemoveWall(drawn));
                }
                return;
            }

            if (WallDrawMode)
            {
                if (_drawing == null)
                {
                    if (!GroundOnFloor(from, dir, out var p)) return;
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
                else
                {
                    // WHICH SIDE. The ray travelling the same way as the surface's +Z means it struck the
                    // back; opposed means the front. That is the side the material picker will paint.
                    SelectSide(w2, dir.Dot(w2.GlobalTransform.Basis.Z) > 0f);
                    // nothing else claimed the click, so this is a wall move. Grabbing on the GROUND plane
                    // at the wall's own height rather than on the wall's surface: dragging a wall sideways
                    // has to keep tracking once the cursor leaves the wall, which surface UVs cannot do.
                    _selOpening = -1;
                    if (GroundAtY(from, dir, w2.Position.Y, out var g))
                    { _wallGrab = g; _wallFrom = w2.Position; _drag = Drag.MoveWall; }
                }
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
        /// <summary>Where a placement lands: the terrain under the cursor, raised to the active storey.
        /// On floor 0 this is just the ground.</summary>
        bool GroundOnFloor(Vector3 from, Vector3 dir, out Vector3 point)
        {
            if (!GroundAt(from, dir, out point)) return false;
            point.Y += FloorY + GroundClearance;
            return true;
        }

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
        /// <summary>Move an opening from one wall to another, keeping its size and archetype.
        ///
        /// An opening belongs to the wall it is cut into, so crossing to a neighbour is a remove and an add
        /// rather than a move -- but it has to LOOK like one drag, so the caller wraps the whole gesture in
        /// a single undo step. Refused if it will not fit or would land on something.</summary>
        public bool ReparentOpening(WallSurface from, int index, WallSurface to, float u, float v)
        {
            if (from == null || to == null || from == to) return false;
            if (index < 0 || index >= from.Openings.Count) return false;
            var o = from.Openings[index];
            if (o.Width > to.Length + WallOpenings.Eps || o.Height > to.Height + WallOpenings.Eps) return false;

            var moved = o;
            moved.U = u - o.Width * 0.5f;
            moved.V = Archetypes[Mathf.PosMod(o.Archetype, Archetypes.Length)].FloorPinned
                      ? 0f : v - o.Height * 0.5f;
            moved = WallOpenings.Clamp(moved, to.Length, to.Height, to.Openings);
            if (WallOpenings.Overlaps(moved, to.Openings)) return false;

            from.Openings.RemoveAt(index);
            from.Rebuild();
            to.Openings.Add(moved);
            to.Rebuild();
            return true;
        }

        /// <summary>Cut a span out of a wall: shorten it, or split it in two if the span is in the middle.
        ///
        /// Openings travel with whichever piece still contains them, and one straddling the cut is dropped
        /// rather than clipped -- half a window is not a window, and silently resizing someone's door to fit
        /// a cut they made somewhere else is worse than removing it.</summary>
        public int RemoveSpan(WallSurface w, float u0, float u1)
        {
            if (w == null || !IsInstanceValid(w)) return 0;
            if (u0 > u1) (u0, u1) = (u1, u0);
            u0 = Mathf.Max(0f, u0);
            u1 = Mathf.Min(w.Length, u1);
            if (u1 - u0 < 0.05f) return 0;

            var dir = (w.UVToWorld(1f, 0f) - w.UVToWorld(0f, 0f)).Normalized();
            bool head = u0 <= 0.02f, tail = u1 >= w.Length - 0.02f;
            if (head && tail) { RemoveWall(w); return 1; }

            var kept = new List<WallOpening>(w.Openings);
            if (head)                                   // trim the start back to u1
            {
                w.Position += dir * u1;
                w.Length -= u1;
                w.Openings.Clear();
                foreach (var o in kept)
                {
                    var m = o; m.U -= u1;
                    if (m.U >= -0.02f && m.U + m.Width <= w.Length + 0.02f) w.Openings.Add(m);
                }
                w.Rebuild();
                return 1;
            }
            if (tail)                                   // trim the end back to u0
            {
                w.Length = u0;
                w.Openings.Clear();
                foreach (var o in kept)
                    if (o.U + o.Width <= w.Length + 0.02f) w.Openings.Add(o);
                w.Rebuild();
                return 1;
            }

            // a bite out of the middle: this wall keeps [0,u0], a new one takes [u1,Length]
            float fullLen = w.Length;
            var rightOrigin = w.UVToWorld(u1, 0f);
            float yaw = w.RotationDegrees.Y, pitch = w.RotationDegrees.X;
            var tailOpenings = new List<WallOpening>();
            foreach (var o in kept)
                if (o.U >= u1 - 0.02f) { var m = o; m.U -= u1; tailOpenings.Add(m); }

            w.Length = u0;
            w.Openings.Clear();
            foreach (var o in kept) if (o.U + o.Width <= u0 + 0.02f) w.Openings.Add(o);
            w.Rebuild();

            SpawnWall(rightOrigin, yaw, fullLen - u1, w.Thickness, w.MaterialId, tailOpenings,
                      w.Height, pitch, w.Kind, w.GableRise, w.Texel,
                      w.InsetL0, w.InsetL1, w.InsetR0, w.InsetR1);
            return 2;
        }

        /// <summary>Every surface on the stage, as plans. Used for the coarse undo steps -- the operations
        /// that rearrange the whole building rather than touch one wall.</summary>
        public List<WallPlan> Snapshot()
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
                    MaterialBack = w.MaterialIdBack, TexelBack = w.TexelBack,
                    Thickness = w.Thickness, Material = w.MaterialId,
                };
                pl.Openings.AddRange(w.Openings);
                plans.Add(pl);
            }
            return plans;
        }

        /// <summary>Put the stage back exactly as a Snapshot found it.</summary>
        public void RestoreAll(List<WallPlan> plans)
        {
            foreach (var w in new List<WallSurface>(_walls)) RemoveWall(w);
            foreach (var pl in plans)
                SpawnWall(new Vector3(pl.X, pl.Y, pl.Z), pl.Yaw, pl.Length, pl.Thickness, pl.Material,
                          pl.Openings, pl.Height, pl.Pitch, pl.Kind, pl.GableRise, pl.Texel,
                          pl.InsetL0, pl.InsetL1, pl.InsetR0, pl.InsetR1, pl.MaterialBack, pl.TexelBack);
            _selWall = null; _selOpening = -1;
            PositionHandles();
        }

        /// <summary>Snapshot, run something that rearranges the building, and push ONE undo step for it --
        /// but only if it actually changed something, so the stack does not fill with no-ops that make Ctrl+Z
        /// look broken.</summary>
        void Undoable(string label, System.Func<int> op)
        {
            var before = Snapshot();
            if (op() <= 0) return;
            _editor?.PushUndo(label, () => RestoreAll(before));
        }

        /// <summary>Wipe the plot -- every wall, floor, roof and foundation on the stage.
        ///
        /// UNDOABLE, which is the whole reason this is not a two-line loop. A clear button is the single most
        /// destructive control in the editor, and an editor that can lose an hour's building to one misclick
        /// is worse than one with no clear button. RestoreAll rebuilds the lot from the snapshot, exactly as
        /// the delete and import paths already do.
        ///
        /// Returns how many surfaces went, so the caller can say "nothing to clear" rather than reporting a
        /// success that did nothing -- and so an empty plot does not consume an undo step.</summary>
        public int ClearPlot()
        {
            int n = 0;
            foreach (var w in _walls) if (IsInstanceValid(w)) n++;
            if (n == 0) return 0;
            var before = Snapshot();
            foreach (var w in new List<WallSurface>(_walls)) RemoveWall(w);
            _selWall = null; _selOpening = -1;
            PositionHandles();
            _editor?.PushUndo("clear plot", () => RestoreAll(before));
            return n;
        }

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
            // DIRECTIONAL. The ridge runs along the longer axis, as a real roof does, and the slope rises
            // from the edge you STARTED the drag on toward the one you finished on -- so dragging north to
            // south gives you a roof falling to the north. It used to always build the same way round
            // regardless of the drag, which made the tool feel like it was ignoring you.
            float spanX = Mathf.Max(0.01f, maxX - minX), spanZ = Mathf.Max(0.01f, maxZ - minZ);
            bool ridgeAlongX = spanX >= spanZ;
            float run, yawDeg, lengthAlong;
            Vector3 origin;
            if (ridgeAlongX)
            {
                run = spanZ; lengthAlong = spanX;
                bool startedAtMaxZ = Mathf.Abs(_slabAnchor.Z - maxZ) < Mathf.Abs(_slabAnchor.Z - minZ);
                yawDeg = startedAtMaxZ ? 0f : 180f;
                origin = startedAtMaxZ ? new Vector3(minX, _slabAnchor.Y, maxZ)
                                       : new Vector3(maxX, _slabAnchor.Y, minZ);
            }
            else
            {
                run = spanX; lengthAlong = spanZ;
                bool startedAtMaxX = Mathf.Abs(_slabAnchor.X - maxX) < Mathf.Abs(_slabAnchor.X - minX);
                yawDeg = startedAtMaxX ? 90f : -90f;
                origin = startedAtMaxX ? new Vector3(maxX, _slabAnchor.Y, maxZ)
                                       : new Vector3(minX, _slabAnchor.Y, minZ);
            }
            _drawingSlab.Position = origin;
            _drawingSlab.Length = lengthAlong;

            // The pitch slider applies to a DRAWN roof too; it used to be flat whatever the slider said.
            // Same convention as AddGableRoof -- pitch - 90, spawned at maxZ with yaw 0 so it rises toward
            // -Z -- and that sign is only correct BECAUSE the yaw is fixed here. Reading a yaw off geometry
            // instead needs 90 - pitch; see BuildingImport.
            // The preview is the FOOTPRINT, laid flat. A pitched roof drawn here becomes a whole gable on
            // release, so showing one tilted plane mid-drag would be a preview of something you do not get.
            _drawingSlab.Height = run;
            _drawingSlab.RotationDegrees = new Vector3(-90f, yawDeg, 0f);
            _drawingSlab.Rebuild();
            EnsureReadout();
            bool gable = SlabDrawKind == SurfaceKind.Roof && ActiveRoofPitch > 0.1f;
            Billboard(new Vector3((minX + maxX) * 0.5f, _slabAnchor.Y + 0.6f, (minZ + maxZ) * 0.5f),
                      $"{maxX - minX:0.0} × {maxZ - minZ:0.0} m"
                      + (gable ? $"\ngable {ActiveRoofPitch:0.#}°" : ""));
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
                    MaterialBack = w.MaterialIdBack, TexelBack = w.TexelBack,
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
                          pl.InsetL0, pl.InsetL1, pl.InsetR0, pl.InsetR1, pl.MaterialBack, pl.TexelBack);
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
        /// <summary>Extend wall ends so corners close -- including TEE and CROSS junctions.
        ///
        /// The old rule was endpoint-to-endpoint: two walls form a corner only when their ENDS coincide, and
        /// the fix was to grow by half the neighbour's thickness. That misses both cases strawberry_cow hit
        /// in the editor, three walls meeting in a tee and four in a cross, because a stem wall's end lands
        /// MID-SPAN of the wall it runs into and matches neither of its endpoints.
        ///
        /// It also could not be run twice. Growing by a RELATIVE half-thickness walks the wall further out
        /// on every call, so running it over an already-overlapping imported building put a 0.30 m pilaster
        /// on every corner -- which is why imports had to skip corner solving entirely.
        ///
        /// The target is ABSOLUTE instead. For each end, find where this wall's own line crosses a
        /// neighbour's CENTRELINE; if the end falls short of that crossing, extend to it. Never past it,
        /// never shrink. A wall already through its neighbour is left alone, so the pass is idempotent and
        /// safe on anything, drawn or imported.
        ///
        /// LIMITATION: reaching the centreline fills the junction because the neighbour's own body covers
        /// the rest. At the 15-degree yaw snap's sharper angles a true mitre would need more, so a sliver of
        /// notch can survive on a very acute corner.</summary>
        public List<(WallSurface W, float Len, Vector3 Pos)> SolveCorners()
        {
            const float MaxGrow = 1.6f;        // a wall thickness and a bit; past that it is not a junction
            var undo = new List<(WallSurface, float, Vector3)>();
            // Foundations solve too: a foundation is a wall, so it has the same missing quarter at every
            // corner, and being underground is exactly why nobody would notice.
            var walls = new List<WallSurface>();
            foreach (var w in _walls)
                if (IsInstanceValid(w) && (w.Kind == SurfaceKind.Wall || w.Kind == SurfaceKind.Foundation)) walls.Add(w);

            var growStart = new float[walls.Count];
            var growEnd = new float[walls.Count];
            for (int i = 0; i < walls.Count; i++)
            {
                var a = walls[i];
                var a0 = a.UVToWorld(0f, 0f);
                var ad = new Vector3(a.UVToWorld(a.Length, 0f).X - a0.X, 0f, a.UVToWorld(a.Length, 0f).Z - a0.Z);
                if (ad.LengthSquared() < 1e-6f) continue;
                ad = ad.Normalized();

                for (int j = 0; j < walls.Count; j++)
                {
                    if (i == j) continue;
                    var b = walls[j];
                    // parallel walls are a seam, not a corner -- extending them just overlaps two walls
                    float dy = Mathf.Abs(Mathf.Wrap(a.RotationDegrees.Y - b.RotationDegrees.Y, -90f, 90f));
                    if (dy < 20f) continue;
                    // and they must be the same storey, or a foundation solves against the wall above it
                    if (Mathf.Abs(a.Position.Y - b.Position.Y) > 0.6f) continue;

                    if (!CrossOnPlan(a0, ad, b.UVToWorld(0f, 0f), b.UVToWorld(b.Length, 0f), out float t, out float s2))
                        continue;
                    if (s2 < -0.02f || s2 > 1.02f) continue;          // the crossing is off the end of b

                    // How far PAST the crossing to run, and the two junctions want different answers.
                    //
                    // At a CORNER -- the crossing is at one of b's own ends -- stopping on b's centreline
                    // leaves the outer quarter square uncovered by either wall. That square IS the notch,
                    // so run on to b's far face. At a TEE the crossing is mid-span of b, b's own body
                    // already fills everything past it, and running to the far face would poke out the
                    // other side of the wall you just joined.
                    bool atEndOfB = s2 < 0.08f || s2 > 0.92f;
                    float target = t + (atEndOfB ? b.Thickness * 0.5f : 0f);

                    // target is ABSOLUTE, so an end already past it grows by nothing and the pass stays
                    // idempotent -- that is what makes it safe to run over an import.
                    if (target < 0f && target >= -MaxGrow) growStart[i] = Mathf.Max(growStart[i], -target);
                    else if (target > a.Length && target <= a.Length + MaxGrow)
                        growEnd[i] = Mathf.Max(growEnd[i], target - a.Length);
                }
            }

            for (int i = 0; i < walls.Count; i++)
            {
                if (growStart[i] <= 1e-4f && growEnd[i] <= 1e-4f) continue;
                var w = walls[i];
                undo.Add((w, w.Length, w.Position));
                var dir = (w.UVToWorld(1f, 0f) - w.UVToWorld(0f, 0f)).Normalized();
                w.Position -= dir * growStart[i];
                w.Length += growStart[i] + growEnd[i];
                w.Rebuild();
            }
            return undo;
        }

        /// <summary>Where the ray (from, dir) crosses the segment b0..b1, in PLAN. `t` is distance along the
        /// ray, `s` the 0..1 parameter along the segment.</summary>
        static bool CrossOnPlan(Vector3 from, Vector3 dir, Vector3 b0, Vector3 b1, out float t, out float s)
        {
            t = s = 0f;
            var p = new Vector2(from.X, from.Z);
            var d = new Vector2(dir.X, dir.Z);
            var q = new Vector2(b0.X, b0.Z);
            var e = new Vector2(b1.X, b1.Z) - q;
            float den = d.X * e.Y - d.Y * e.X;
            if (Mathf.Abs(den) < 1e-6f) return false;          // parallel in plan
            var r = q - p;
            t = (r.X * e.Y - r.Y * e.X) / den;
            s = (r.X * d.Y - r.Y * d.X) / den;
            return true;
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
                    MaterialBack = w.MaterialIdBack, TexelBack = w.TexelBack,
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
        /// <summary>How far a PITCHED roof runs past the walls it covers. A flat roof does not -- it stays
        /// flush with the wall face.
        ///
        /// Retail measures flush for both: 24 of 26 buildings end the roof on the wall face within 6 cm, and
        /// this was 0 for that reason. strawberry_cow overrode it from the result, then corrected the
        /// correction -- "flat roofs SHOULDNT*". Measurement wins on what retail IS; the person looking at
        /// the game wins on what it should look like, including which half of it.</summary>
        public const float RoofOverhang = 0.4f;

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
                // Bound the walls' OUTER FACES, not their centrelines.
                //
                // This measured centrelines and then grew the result by half a thickness to reach the face.
                // That was right while walls stopped at their centreline corners -- and wrong the moment
                // corner solving started running on draw, because a solved wall ALREADY runs past the
                // junction to the outer face, so the half-thickness got added to a wall that had it. The
                // slab then hung over every corner by exactly that much. strawberry_cow spotted it in a
                // render and correctly guessed floors over foundations.
                //
                // Taking the real face corners is right in both cases rather than right in the one I
                // happened to write it in.
                var face = w.GlobalTransform.Basis.Z.Normalized() * (w.Thickness * 0.5f);
                foreach (float u in new[] { 0f, w.Length })
                    foreach (var side in new[] { face, -face })
                    {
                        var p = w.UVToWorld(u, 0f) + side;
                        minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
                        minZ = Mathf.Min(minZ, p.Z); maxZ = Mathf.Max(maxZ, p.Z);
                    }
                baseY = Mathf.Min(baseY, w.Position.Y);
                topY = Mathf.Max(topY, w.Position.Y + w.Height);
                // (the slab used to copy this from the walls; it takes the ACTIVE material now -- see below)
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
            // No growth: the bounds above are already the outer faces. Only a pitched roof overhangs, and
            // AddGableRoof applies that; a flat slab hanging past the walls reads as a ledge.


            // The slab's top lands where you would stand on it: at the walls' base for a floor, at their head
            // for a roof. Thickness runs along local Z, which the -90 pitch turns into world up.
            float top = kind == SurfaceKind.Roof ? topY + SlabThickness : baseY;
            var origin = new Vector3(minX, top - SlabThickness * 0.5f, maxZ);
            // What you PICKED, not what the walls happen to be wearing. strawberry_cow: "make all things u
            // place after selecting a material BE that material automatically." Copying it off the walls
            // meant the material picker silently did nothing for slabs.
            material = ActiveMaterial;
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

            // An import replaces the whole stage, so it gets a whole-stage undo. It is the single most
            // destructive button in the panel and it was the one thing Ctrl+Z could not walk back.
            var beforeImport = Snapshot();
            _editor?.PushUndo($"import {buildingName}", () => RestoreAll(beforeImport));
            foreach (var w in new List<WallSurface>(_walls)) RemoveWall(w);
            foreach (var pl in plans)
                SpawnWall(StageOrigin + new Vector3(pl.X, pl.Y + GroundClearance, pl.Z), pl.Yaw, pl.Length, pl.Thickness,
                          pl.Material, pl.Openings, pl.Height, pl.Pitch, pl.Kind, pl.GableRise, pl.Texel,
                          pl.InsetL0, pl.InsetL1, pl.InsetR0, pl.InsetR1, pl.MaterialBack, pl.TexelBack);
            // Corner solving runs on imports again, now that it is safe to.
            //
            // The first attempt at this grew a 0.30 m pilaster on every corner, because the old rule added a
            // RELATIVE half-thickness to any end near a neighbour -- and an imported wall, recovered from a
            // facade plane spanning the whole building, already runs into its neighbour. The rule is an
            // ABSOLUTE target now, so an end already past the junction grows by nothing and this is a no-op
            // wherever the import was already correct. What it does fix is the case that was never covered
            // either way: strawberry_cow's tee, "the 3 wall meet appears on house 00, which is where a gap
            // was" -- an interior wall running into the middle of a facade, matching neither of its ends.
            int solved = SolveCorners().Count;

            ActiveMaterial = mat;
            int nw = 0, nr = 0, nf = 0, nfl = 0, nop = 0, ngab = 0;
            foreach (var pl in plans)
            {
                if (pl.Kind == SurfaceKind.Roof) nr++;
                else if (pl.Kind == SurfaceKind.Foundation) nf++;
                else if (pl.Kind == SurfaceKind.Floor) nfl++;
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
                             + $"  texel {pl.Texel}  y0 {pl.Y:0.000} y1 {pl.Y + pl.Height:0.000}"
                             + $"   X {Mathf.Min(o.X, e.X),6:0.0}..{Mathf.Max(o.X, e.X),6:0.0}"
                             + $"  Y {Mathf.Min(o.Y, e.Y),6:0.0}..{Mathf.Max(o.Y, e.Y),6:0.0}"
                             + $"  Z {Mathf.Min(o.Z, e.Z),6:0.0}..{Mathf.Max(o.Z, e.Z),6:0.0}");
                }
            GD.Print($"[editor-buildings] {solved} surfaces extended to close corners");
            GD.Print($"[editor-buildings] imported {buildingName}: {plans.Count} surfaces "
                     + $"({nw} wall, {nr} roof, {nfl} floor, {nf} foundation, {ngab} gabled) with {nop} openings");
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
            float halfW = maxWallThickness * 0.5f + RoofOverhang;
            minX -= halfW; maxX += halfW; minZ -= halfW; maxZ += halfW;

            return BuildGableOver(minX, maxX, minZ, maxZ, topY, pitchDeg, material, maxWallThickness);
        }

        /// <summary>Build a gable roof over an explicit footprint: two slopes meeting at a ridge, and the
        /// walls that run across the ridge raised into gable ends to close it.
        ///
        /// Split out of AddGableRoof so the DRAG-RECT roof can use it. Drawing one slope at a time was me
        /// exposing the primitive instead of the thing you want -- strawberry_cow: "why am i placing one
        /// slope at a time instead of drawing a gable zone over a rect".</summary>
        public int BuildGableOver(float minX, float maxX, float minZ, float maxZ, float topY,
                                  float pitchDeg, int material, float maxWallThickness)
        {
            pitchDeg = Mathf.Clamp(pitchDeg, 1f, 70f);
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
            //
            // The gable triangle's SLOPE has to be the roof's slope, which means it is set by the WALL's own
            // half-length, not by the roof footprint's. Those differ the moment the roof overhangs, and
            // setting GableRise = rise (the footprint's) made the triangle steeper than the roof it sits
            // under: measured on a 9 m wall at 20 deg with a 0.75 m overhang, 3.01 deg steeper, meeting the
            // roof only at the apex and opening a 0.27 m wedge of daylight along both sloped edges.
            //
            // What closes it is a straight band between the wall top and the triangle -- strawberry_cow's
            // "the wall portion between the roof part and the actual walls". It is a separate surface rather
            // than a taller wall because raising w.Height would COMPOUND on a second roof build (the same
            // relative-vs-absolute trap that grew a pilaster on every corner), and because that band wears
            // the wall's colour, which is what they asked for when the importer met the same shape.
            float tanP = Mathf.Tan(th);
            var raised = new List<(WallSurface W, float Prev)>();
            var bands = new List<WallSurface>();
            // A COPY: spawning a band appends to _walls, and mutating it mid-foreach throws. Same reason
            // AddFoundation iterates a copy.
            foreach (var w in new List<WallSurface>(_walls))
            {
                if (!IsInstanceValid(w) || w.Kind != SurfaceKind.Wall) continue;
                float yaw = Mathf.Wrap(w.RotationDegrees.Y, 0f, 180f);
                bool runsAlongX = yaw < 45f || yaw > 135f;
                if (runsAlongX == ridgeAlongX) continue;       // parallel to the ridge: no gable
                float triRise = w.Length * 0.5f * tanP;        // slope-matched to the roof above it
                float band = rise - triRise;                   // 0 when the roof does not overhang this wall
                if (band > 0.01f)
                {
                    bands.Add(SpawnWall(w.Position + new Vector3(0f, w.Height, 0f), w.RotationDegrees.Y,
                                        w.Length, w.Thickness, w.MaterialId, null, band,
                                        w.RotationDegrees.X, SurfaceKind.Wall, triRise));
                }
                else
                {
                    raised.Add((w, w.GableRise));
                    w.GableRise = triRise;
                    w.Rebuild();
                }
            }

            _editor?.PushUndo("gable roof", () =>
            {
                foreach (var m in made) RemoveWall(m);
                foreach (var b in bands) RemoveWall(b);
                foreach (var (w, prev) in raised) if (IsInstanceValid(w)) { w.GableRise = prev; w.Rebuild(); }
            });
            return made.Count + raised.Count + bands.Count;
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
            _dragWall = _selWall;
            // Only a MOVE can cross to another wall, and only then is a two-wall undo needed. Snapshotting
            // the stage on every edge-resize would be waste.
            _dragStage = d == Drag.Move ? Snapshot() : null;
        }

        /// <summary>The wall under the cursor, and how far along its run the cursor is.</summary>
        /// <summary>Where a ray meets the horizontal plane at height y.</summary>
        static bool GroundAtY(Vector3 from, Vector3 dir, float y, out Vector3 hit)
        {
            hit = Vector3.Zero;
            var p = new Plane(Vector3.Up, y).IntersectsRay(from, dir);
            if (p == null) return false;
            hit = p.Value;
            return true;
        }

        bool PickWallAt(Vector3 from, Vector3 dir, out WallSurface wall, out float u)
        {
            wall = null; u = 0f;
            float best = float.MaxValue;
            foreach (var w in _walls)
            {
                if (!IsInstanceValid(w)) continue;
                if (!w.RayToUVInside(from, dir, out float wu, out float wv)) continue;
                float d = from.DistanceSquaredTo(w.UVToWorld(wu, wv));
                if (d >= best) continue;
                best = d; wall = w; u = wu;
            }
            return wall != null;
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
                if (!w.RayToUVInside(from, dir, out float wu, out float wv)) continue;
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
                if (!w.RayToUVInside(from, dir, out float u, out float v)) continue;
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

        /// <summary>Translucent panel over the face you have selected, so "which side am I painting" is a
        /// thing you can see rather than a checkbox you have to remember.</summary>
        void ShowSideGhost(WallSurface w, bool back)
        {
            if (w == null) { HideSideGhost(); return; }
            if (_sideGhost == null)
            {
                _sideGhost = new MeshInstance3D
                {
                    Mesh = new QuadMesh { Size = Vector2.One },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(1f, 0.75f, 0.2f, 0.22f),
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    },
                };
                AddChild(_sideGhost);
            }
            ((QuadMesh)_sideGhost.Mesh).Size = new Vector2(Mathf.Max(0.05f, w.Length),
                                                           Mathf.Max(0.05f, w.Height));
            float off = w.Thickness * 0.5f + 0.03f;
            _sideGhost.GlobalTransform = new Transform3D(w.GlobalTransform.Basis,
                w.UVToWorld(w.Length * 0.5f, w.Height * 0.5f)
                + w.GlobalTransform.Basis.Z.Normalized() * (back ? -off : off));
            _sideGhost.Visible = true;
        }

        void HideSideGhost() { if (_sideGhost != null) _sideGhost.Visible = false; }

        void HideGhost()
        {
            if (_ghost != null) _ghost.Visible = false;
            if (_armed >= 0) HideReadout();
        }

        void OnDrag(Vector2 screen)
        {
            if (_drag == Drag.MoveWall)
            {
                if (_selWall == null || !IsInstanceValid(_selWall)) { _drag = Drag.None; return; }
                var f = _cam.ProjectRayOrigin(screen);
                var d = _cam.ProjectRayNormal(screen);
                if (!GroundAtY(f, d, _wallFrom.Y, out var now)) return;
                var moved = _wallFrom + (now - _wallGrab);
                _selWall.Position = new Vector3(WallOpenings.SnapGrid(moved.X), _wallFrom.Y,
                                                WallOpenings.SnapGrid(moved.Z));
                _selWall.Rebuild();
                EnsureReadout();
                Billboard(_selWall.UVToWorld(_selWall.Length * 0.5f, _selWall.Height + 0.4f),
                          $"{_selWall.Position.X:0.#}, {_selWall.Position.Z:0.#}");
                PositionHandles();
                return;
            }
            if (_selWall == null || _selOpening < 0) return;
            var from = _cam.ProjectRayOrigin(screen);
            var dir = _cam.ProjectRayNormal(screen);

            // Dragging an opening onto ANOTHER wall carries it across. Checked before the own-wall path,
            // because while the cursor is over a neighbour the ray still meets this wall's infinite plane
            // somewhere and the opening would otherwise slide to that phantom point.
            if (_drag == Drag.Move && PickWallAt(from, dir, out var over, out float ou)
                && over != _selWall && over.Kind == SurfaceKind.Wall
                && over.RayToUVInside(from, dir, out float nu, out float nv))
            {
                var src = _selWall;
                int idx = _selOpening;
                if (ReparentOpening(src, idx, over, nu, nv))
                {
                    _selWall = over;
                    _selOpening = over.Openings.Count - 1;
                    var no = over.Openings[_selOpening];
                    _grabDU = nu - (no.U + no.Width * 0.5f);
                    _grabDV = nv - (no.V + no.Height * 0.5f);
                    ShowReadout(over, no);
                    PositionHandles();
                }
                return;
            }

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
