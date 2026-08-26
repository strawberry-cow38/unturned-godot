using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // RIVER CARVING, as a spline tool next to the road tools.
    //
    // strawberry_cow, 2026-08-24: "why isnt the river tool under the same area as the road spline tools. why
    // does it use the terrain brush circle? it just places single nodes". All three complaints are the same
    // mistake: I filed this under the TERRAIN tab because carving is a terrain operation, which is true about
    // the implementation and false about the tool. You drive it by placing anchors and pulling a curve through
    // them -- that is a spline tool, so it lives with the spline tools, and it inherited the terrain brush's
    // radius ring and per-anchor markers only because of where it was parked.
    //
    // So: Environment tab, its own section in the roads panel, and a live preview that draws the ACTUAL curve
    // and BOTH BANKS before you commit. The banks are the part that answers "it just places single nodes" --
    // a river has width, and until you can see the width you are placing dots and hoping.
    //
    // MUTUALLY EXCLUSIVE with the two road tools, enforced by the panel: all three bind LMB on the terrain, so
    // two live at once means one click does two things.
    //
    // UNDO (added 2026-08-26, strawberry). The old comment here said river undo needed "a heightmap snapshot
    // per carve" and deferred it as too expensive. That stopped being true when rivers became DISPLACEMENT
    // rather than baked geometry: the ground is re-derived from the anchors every regenerate, so undoing a
    // river only has to restore the anchor list -- a handful of Vector3s, not a heightmap. Terrain.Snapshot-
    // Rivers/RestoreRivers do exactly that, and every mutating action here goes through SnapUndo first.
    public partial class EditorRiver : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly Terrain _terr;
        const uint TerrainLayer = 1u << 0;

        /// <summary>Sub-tools, mirroring EditorRoadDraw so the two feel like one family of tools.
        ///
        /// They differ from the road tool's in ONE way, on purpose: Curve here takes as many anchors as you
        /// like and commits on Enter, where the road's Curve is exactly 3 clicks through a quadratic. A river
        /// is a long meander with several bends, and making the user draw it as a chain of 3-click arcs would
        /// be worse -- Terrain.RiverPathPoints already runs a Catmull-Rom through N anchors, which is the
        /// better curve, so the interaction follows the maths rather than the other way round.</summary>
        public enum ETool { Straight, Curve, Freehand }
        public static readonly string[] ToolNames = { "Straight", "Curve", "Freehand" };
        ETool _tool = ETool.Curve;
        public ETool Tool => _tool;
        public void SetTool(ETool t) { _tool = t; CancelPath(); }

        public const float MinHalfWidth = 2f, MaxHalfWidth = 40f;
        public const float MinDepth = 0.5f, MaxDepth = 20f;
        /// <summary>Freehand banks a point every this-many metres. Same value and same reason as the road
        /// tool's SampleSpacing: fine enough not to corner, coarse enough that a drawn river is not a thousand
        /// anchors.</summary>
        public const float SampleSpacing = 8f;

        float _halfWidth = 8f, _depth = 4f;
        public float HalfWidth => _halfWidth;
        public float Depth => _depth;
        public void SetHalfWidth(float v) { _halfWidth = Mathf.Clamp(v, MinHalfWidth, MaxHalfWidth); UpdatePreview(); }
        public void SetDepth(float v) { _depth = Mathf.Clamp(v, MinDepth, MaxDepth); UpdatePreview(); }

        // EXISTING rivers' anchors, as draggable handles. strawberry: "i want it as an editable spline, not a
        // permanent deform of the terrain." The non-permanent half was already true -- nothing is baked into
        // the heightmap -- but until now you could draw a river and never touch it again, which is not what
        // anyone means by editable.
        const uint HandlePickLayer = 1u << 12;   // its own layer; the road tool owns 1<<11 and EditorRoads 1<<10
        readonly List<StaticBody3D> _handles = new();
        readonly List<(int river, int anchor)> _handleIds = new();
        int _dragRiver = -1, _dragAnchor = -1;
        Vector3 _dragTo;

        bool _carving;                       // tool active (V)
        bool _stroke;                        // mid-drag (Freehand)
        readonly List<Vector3> _anchors = new();
        readonly List<MeshInstance3D> _marks = new();
        MeshInstance3D _preview;

        public bool Carving => _carving;
        public int AnchorCount => _anchors.Count;
        public int RiverSegmentCount => _terr?.RiverSegmentCount ?? 0;

        /// <summary>Panel entry point -- the button drives the same seam the key does, so the two cannot
        /// disagree about which tool owns the mouse.</summary>
        public void SetActive(bool on) { if (_carving != on) SetCarving(on); }

        /// <summary>Re-cut every existing river with the CURRENT carve code. A saved river replays its baked
        /// geometry verbatim on load -- deliberately, it is what makes loading cheap -- so a fix to the carve
        /// never reaches a river that already exists. This is the migration button for exactly that.</summary>
        public int RebuildExisting() => _terr?.RebuildRiversFromRecipe() ?? 0;

        /// <summary>Snapshot every river and register the restore as one undo step. Call BEFORE mutating.
        /// Cheap: see Terrain.SnapshotRivers.</summary>
        void SnapUndo(string label)
        {
            if (_terr == null) return;
            var snap = _terr.SnapshotRivers();
            _editor?.PushUndo(label, () => { _terr.RestoreRivers(snap); BuildHandles(); });
        }

        public string ModeText => !_carving
            ? "V carve river"
            : $"RIVER[{ToolNames[(int)_tool]}] · half-width {_halfWidth:0.#}m ([/]) · depth {_depth:0.#}m (-/=) · {_anchors.Count} placed · T tool · LMB place/drag node · Enter carve · Del undo point · Esc";

        public EditorRiver(Editor editor, Camera3D cam, Terrain terr)
        {
            _editor = editor; _cam = cam; _terr = terr;
            _flyCam = cam as EditorCamera;
            _editor.ModeChanged += _ => { if (_editor.Mode != EEditorMode.Environment && _carving) SetCarving(false); };
        }

        void SetCarving(bool on)
        {
            _carving = on;
            if (on) BuildHandles();
            else { CancelPath(); ClearHandles(); _dragRiver = -1; }
        }

        void ClearHandles()
        {
            foreach (var h in _handles) if (IsInstanceValid(h)) h.QueueFree();
            _handles.Clear(); _handleIds.Clear();
        }

        /// <summary>One pickable sphere per anchor of every river. Rebuilt whenever the set changes rather
        /// than moved, because an anchor list is small and a stale handle is a handle that edits the wrong
        /// node.</summary>
        void BuildHandles()
        {
            ClearHandles();
            if (_terr == null) return;
            for (int ri = 0; ri < _terr.RiverCount; ri++)
            {
                var riv = _terr.Rivers[ri];
                for (int ai = 0; ai < riv.Anchors.Count; ai++)
                {
                    var body = new StaticBody3D { CollisionLayer = HandlePickLayer, Position = riv.Anchors[ai] };
                    body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 2.2f } });
                    body.AddChild(new MeshInstance3D
                    {
                        Mesh = new SphereMesh { Radius = 1.3f, Height = 2.6f, RadialSegments = 8, Rings = 4 },
                        MaterialOverride = new StandardMaterial3D
                        {
                            AlbedoColor = new Color(1f, 0.85f, 0.25f),
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                            NoDepthTest = true,
                        },
                    });
                    AddChild(body);
                    _handles.Add(body); _handleIds.Add((ri, ai));
                }
            }
        }

        int PickHandle(Vector2 screen)
        {
            if (_cam == null) return -1;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 12000f;
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = HandlePickLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return -1;
            for (int i = 0; i < _handles.Count; i++) if (_handles[i] == (Node)hit["collider"]) return i;
            return -1;
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Environment || (_flyCam != null && _flyCam.Flying)) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.V })
            {
                SetCarving(!_carving);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (!_carving) return;

            if (ev is InputEventKey { Pressed: true, Echo: false } k)
            {
                switch (k.Keycode)
                {
                    case Key.Escape:
                        if (_anchors.Count > 0) CancelPath(); else SetCarving(false);
                        GetViewport().SetInputAsHandled(); return;
                    case Key.T:
                        SetTool((ETool)(((int)_tool + 1) % ToolNames.Length));
                        GetViewport().SetInputAsHandled(); return;
                    case Key.Enter:
                    case Key.KpEnter:
                        Commit();
                        GetViewport().SetInputAsHandled(); return;
                    case Key.Delete:
                    case Key.Backspace:
                        DropLastAnchor();
                        GetViewport().SetInputAsHandled(); return;
                    case Key.K when _anchors.Count == 0:
                        // K, not R. EditorRoadDraw toggles itself on R BEFORE its own active-check, so R here
                        // would silently switch the road tool on underneath the river tool -- the panel's
                        // mutual exclusion cannot help, because the key never reaches it.
                        //
                        // Rebuild every river from its anchors. Cheap now -- there is no baked geometry left to
                        // reconstruct, just derived state to regenerate.
                        _terr?.RegenerateRivers();
                        BuildHandles();
                        GetViewport().SetInputAsHandled(); return;
                    // [ / ] width, - / = depth. Deliberately NOT , / . -- those are time-of-day in
                    // EditorEnvironment and are live the whole time this tool is, so taking them would break a
                    // control that has nothing to do with rivers. [ and ] belong to EditorRoads, which the
                    // panel force-disables whenever this tool is on, so they are free here.
                    case Key.Bracketleft:  SetHalfWidth(_halfWidth - 1f); GetViewport().SetInputAsHandled(); return;
                    case Key.Bracketright: SetHalfWidth(_halfWidth + 1f); GetViewport().SetInputAsHandled(); return;
                    case Key.Minus:        SetDepth(_depth - 0.5f); GetViewport().SetInputAsHandled(); return;
                    case Key.Equal:        SetDepth(_depth + 0.5f); GetViewport().SetInputAsHandled(); return;
                }
            }

            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !Editor.PointerOverUI(this))
            {
                if (mb.Pressed)
                {
                    // A HANDLE UNDER THE CURSOR WINS over starting a new stroke -- otherwise an existing
                    // river's node is a thing you can create and never touch again, which is the complaint.
                    int h = PickHandle(GetViewport().GetMousePosition());
                    if (h >= 0)
                    {
                        (_dragRiver, _dragAnchor) = _handleIds[h];
                        _dragTo = _handles[h].Position;
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (_tool == ETool.Freehand) { _stroke = true; CancelPath(); AddAnchorAtCursor(); }
                    else
                    {
                        AddAnchorAtCursor();
                        // Straight is exactly two points and commits itself; Curve keeps collecting until
                        // Enter, which is what lets one river have several bends.
                        if (_tool == ETool.Straight && _anchors.Count >= 2) Commit();
                    }
                }
                else if (_dragRiver >= 0)
                {
                    // COMMIT ON RELEASE. Moving an anchor re-derives the displacement, the paint, the water and
                    // every affected chunk; doing that per mouse-move would rebuild the terrain 60x a second.
                    SnapUndo("move river anchor");
                    _terr?.MoveRiverAnchor(_dragRiver, _dragAnchor, _dragTo);
                    _dragRiver = -1;
                    BuildHandles();
                }
                else if (_stroke) { _stroke = false; Commit(); }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (ev is InputEventMouseMotion)
            {
                if (_dragRiver >= 0)
                {
                    if (RaycastTerrain(GetViewport().GetMousePosition(), out var np))
                    {
                        _dragTo = np;
                        for (int i = 0; i < _handleIds.Count; i++)
                            if (_handleIds[i] == (_dragRiver, _dragAnchor)) _handles[i].Position = np;
                    }
                    return;
                }
                if (_stroke)
                {
                    // Bank a point only once the cursor has actually travelled -- otherwise a slow drag lays
                    // hundreds of anchors a few centimetres apart and the Catmull-Rom through them is noise.
                    if (RaycastTerrain(GetViewport().GetMousePosition(), out var p)
                        && (_anchors.Count == 0 || _anchors[^1].DistanceTo(p) >= SampleSpacing))
                    { _anchors.Add(p); MarkAnchor(p); }
                }
                UpdatePreview();
            }
        }

        void AddAnchorAtCursor()
        {
            if (!RaycastTerrain(GetViewport().GetMousePosition(), out var p)) return;
            _anchors.Add(p);
            MarkAnchor(p);
            UpdatePreview();
        }

        void DropLastAnchor()
        {
            if (_anchors.Count == 0) return;
            _anchors.RemoveAt(_anchors.Count - 1);
            var m = _marks[^1]; _marks.RemoveAt(_marks.Count - 1);
            if (IsInstanceValid(m)) m.QueueFree();
            UpdatePreview();
        }

        /// <summary>Cut the collected anchors as one river, then clear. Separate from placing them so Curve can
        /// take as many bends as it likes before anything touches the heightmap.</summary>
        public void Commit()
        {
            if (_terr != null && _anchors.Count >= 2)
            {
                SnapUndo("carve river");
                _terr.CarveRiverPath(_anchors, _halfWidth, _depth);
                BuildHandles();
                GD.Print($"[river] carved {_anchors.Count} anchors, half-width {_halfWidth:0.#}m, depth {_depth:0.#}m -> {_terr.RiverSegmentCount} segments total");
            }
            CancelPath();
        }

        void CancelPath()
        {
            _anchors.Clear();
            foreach (var m in _marks) if (IsInstanceValid(m)) m.QueueFree();
            _marks.Clear();
            ClearPreview();
        }

        void MarkAnchor(Vector3 p)
        {
            var m = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 1.1f, Height = 2.2f, RadialSegments = 8, Rings = 4 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.35f, 0.75f, 1f),
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    NoDepthTest = true,
                },
                Position = p,
            };
            AddChild(m);
            _marks.Add(m);
        }

        /// <summary>The live promise of where the cut lands: the curve's CENTRELINE plus BOTH BANKS at
        /// +/- half-width, with the trailing span running out to the cursor.
        ///
        /// The centreline comes from Terrain.RiverPathPoints -- the same call CarveRiverPath makes -- rather
        /// than a second copy of the spline maths here. A preview that recomputes the curve its own way is
        /// exactly the kind of check that agrees with itself and lies about the thing it is previewing.
        ///
        /// The banks are drawn flat at the centreline's own height rather than sampled onto the terrain: this
        /// is a plan-view footprint ("this is the strip that will be cut"), not a render of the finished bed,
        /// and pretending otherwise by draping it would suggest a precision the preview does not have.</summary>
        void UpdatePreview()
        {
            ClearPreview();
            if (!_carving || _anchors.Count == 0) return;

            var pts = new List<Vector3>(_anchors);
            // Rubber-band: while placing, the span from the last anchor to the cursor is part of what you are
            // about to cut, so it has to be in the preview or the tool under-promises by one segment.
            if (!_stroke && RaycastTerrain(GetViewport().GetMousePosition(), out var cur)) pts.Add(cur);
            if (pts.Count < 2) return;

            var line = _terr != null ? _terr.RiverPathPoints(pts) : pts;
            if (line.Count < 2) return;

            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.35f, 0.75f, 1f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true,
            };
            var im = new ImmediateMesh();

            im.SurfaceBegin(Mesh.PrimitiveType.LineStrip, mat);
            foreach (var p in line) im.SurfaceAddVertex(p + Vector3.Up * 0.35f);
            im.SurfaceEnd();

            for (int side = 0; side < 2; side++)
            {
                im.SurfaceBegin(Mesh.PrimitiveType.LineStrip, mat);
                for (int i = 0; i < line.Count; i++)
                {
                    // Tangent from the neighbours so the offset does not kink at every sample; the perpendicular
                    // is taken in the XZ plane because the width of a river is a map-plane measurement.
                    Vector3 a = line[Mathf.Max(0, i - 1)], b = line[Mathf.Min(line.Count - 1, i + 1)];
                    var t = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
                    if (t.LengthSquared() < 1e-6f) t = Vector3.Forward;
                    t = t.Normalized();
                    var n = new Vector3(-t.Z, 0f, t.X) * (side == 0 ? _halfWidth : -_halfWidth);
                    im.SurfaceAddVertex(line[i] + n + Vector3.Up * 0.35f);
                }
                im.SurfaceEnd();
            }

            _preview = new MeshInstance3D { Mesh = im };
            AddChild(_preview);
        }

        void ClearPreview() { if (IsInstanceValid(_preview)) _preview?.QueueFree(); _preview = null; }

        bool RaycastTerrain(Vector2 screen, out Vector3 pt)
        {
            pt = Vector3.Zero;
            if (_cam == null) return false;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 12000f;
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = TerrainLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            pt = (Vector3)hit["position"];
            return true;
        }

        // --- test seams: the tool's behaviour without a mouse ---
        public void DebugSetCarving(bool on) => SetCarving(on);
        public void DebugAddAnchor(Vector3 p) { _anchors.Add(p); MarkAnchor(p); UpdatePreview(); }
        public int DebugAnchorMarkerCount => _marks.Count;
        public bool DebugHasPreview => IsInstanceValid(_preview);
    }
}
