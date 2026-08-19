using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // DRAW-A-ROAD/RAIL. strawberry, 2026-08-19: "we need a tool thats more like a 'draw-a-road/rail' tool,
    // instead of nodes, with support for branches, junctions, etc." The node tool it replaces is not deleted --
    // it is still there on Shift+R as LEGACY PAVE, because placing a single joint by hand is the right tool for
    // fixing one vertex and this one is the wrong tool for that.
    //
    // A JUNCTION IS A REAL NODE (strawberry's call, 2026-08-19: "we should invent a junction node. the
    // existing maps are considered 'legacy' and simply use the old tool"). My first cut derived junctions from
    // coincident road ends, which works for routing but cannot be EDITED: to move a 3-way you would drag three
    // separate ends to the same spot and hope they still matched to the millimetre. A node owns its position
    // and road ends bind to it, so dragging the node drags every rail attached -- and they cannot drift apart.
    //
    // The nodes live in a SIDECAR file next to Paths.dat, which stays exactly retail-shaped. A legacy map has
    // no sidecar, therefore no junctions, and is edited with the legacy tool on Shift+R. That is the whole
    // split: new graph for new maps, retail format untouched for the old ones.
    public partial class EditorRoadDraw : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly RoadField _roads;
        const uint TerrainLayer = 1u << 0;

        /// <summary>Cursor must travel this far before another point is banked. Straight from the feel of it:
        /// too small and a drawn road is a thousand joints that save slowly and edit horribly; too large and
        /// curves visibly corner. 8 m matches the scale of the map's existing roads.</summary>
        public const float SampleSpacing = 8f;
        /// <summary>Snap radius for junctions. Generous on purpose -- you are aiming at a road end with a
        /// mouse from a fly camera, and the failure of snapping too eagerly (a junction you did not mean) is
        /// far cheaper to fix than the failure of not snapping (a junction that looks right and is not).</summary>
        public const float SnapRadius = 12f;

        bool _drawing;                       // tool active (R)
        bool _stroke;                        // mid-drag, laying points
        readonly List<Vector3> _pts = new();
        int _lastRoad = -1;                  // last road committed, for M/L/Del without re-picking
        int _material;
        MeshInstance3D _preview;             // the live rubber-band line
        MeshInstance3D _snapRing;            // shown when the cursor is over a snappable end
        const uint NodePickLayer = 1u << 11;   // own pick layer (EditorRoads uses 1<<10 for its joint markers)
        readonly List<StaticBody3D> _nodeMarkers = new();
        readonly Dictionary<StaticBody3D, int> _nodeMap = new();
        int _selNode = -1;                   // junction node under edit
        bool _dragNode;                      // LMB held on a node -> live drag

        public bool Drawing => _drawing;
        public int LastRoad => _lastRoad;

        public string ModeText => !_drawing
            ? "R draw road/rail · Shift+R legacy"
            : $"DRAW{(_stroke ? $" ({_pts.Count} pts)" : "")}{(_selNode >= 0 ? $" · NODE {_selNode} ({_roads.JunctionEdges(_selNode).Count} rails)" : "")} · LMB drag to lay / grab a node · M mat={MatName()} · {_roads.JunctionCount} nodes, {_roads.Junctions().Count} junctions · Del · Esc";

        string MatName() => _roads != null && _roads.MaterialCount > 0 ? _roads.RoadMaterialName(_lastRoad >= 0 ? _lastRoad : 0) ?? $"{_material}" : $"{_material}";

        public EditorRoadDraw(Editor editor, Camera3D cam, RoadField roads)
        {
            _editor = editor; _cam = cam; _roads = roads;
            _flyCam = cam as EditorCamera;
            _editor.ModeChanged += _ => { if (_editor.Mode != EEditorMode.Environment && _drawing) SetDrawing(false); };
        }

        void SetDrawing(bool on)
        {
            _drawing = on;
            if (on) BuildNodeMarkers();
            else { CancelStroke(); ClearSnapRing(); ClearNodeMarkers(); _selNode = -1; _dragNode = false; }
        }

        // JUNCTION NODES ARE THE ONLY THING WORTH DRAWING HERE. The rails already render themselves; a node is
        // the piece with no geometry of its own, so without a marker it is an invisible object you are asked
        // to connect things to. Green = a loose node (fewer than 2 rails, i.e. not yet a junction), amber = a
        // real junction, red = selected -- so "did that actually connect" is answerable at a glance instead of
        // by reading the status line.
        void BuildNodeMarkers()
        {
            ClearNodeMarkers();
            if (_roads == null) return;
            var mesh = new SphereMesh { Radius = 1.9f, Height = 3.8f };
            for (int i = 0; i < _roads.JunctionCount; i++)
            {
                int deg = _roads.JunctionEdges(i).Count;
                var col = i == _selNode ? new Color(1f, 0.15f, 0.1f)
                        : deg >= 2 ? new Color(1f, 0.75f, 0.15f)
                                   : new Color(0.2f, 1f, 0.4f);
                var body = new StaticBody3D { CollisionLayer = NodePickLayer, CollisionMask = 0, Position = _roads.JunctionPos(i) };
                body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 2.2f } });
                body.AddChild(new MeshInstance3D
                {
                    Mesh = mesh,
                    MaterialOverride = new StandardMaterial3D
                    { AlbedoColor = col, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true },
                });
                AddChild(body);
                _nodeMarkers.Add(body);
                _nodeMap[body] = i;
            }
        }

        void ClearNodeMarkers()
        {
            foreach (var m in _nodeMarkers) m.QueueFree();
            _nodeMarkers.Clear(); _nodeMap.Clear();
        }

        int PickNode(Vector2 screen)
        {
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 12000f;
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = NodePickLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return -1;
            return hit["collider"].As<GodotObject>() is StaticBody3D b && _nodeMap.TryGetValue(b, out int i) ? i : -1;
        }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Environment || (_flyCam != null && _flyCam.Flying)) return;

            // R toggles THIS tool; Shift+R is the legacy node tool's key and is handled by EditorRoads, so
            // bail out rather than eating it.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R })
            {
                if (Input.IsKeyPressed(Key.Shift)) return;
                SetDrawing(!_drawing);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (!_drawing) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { if (_stroke) CancelStroke(); else SetDrawing(false); return; }

            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !Editor.PointerOverUI(this))
            {
                if (mb.Pressed)
                {
                    // A node under the cursor takes priority over starting a stroke -- otherwise a junction
                    // is a thing you can create and never touch again, and dragging one is the whole reason
                    // it is a node.
                    int n = PickNode(GetViewport().GetMousePosition());
                    if (n >= 0) { _selNode = n; _dragNode = true; SnapUndo("move junction"); BuildNodeMarkers(); }
                    else { _selNode = -1; BuildNodeMarkers(); BeginStroke(); }
                }
                else { if (_dragNode) _dragNode = false; else EndStroke(); }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (ev is InputEventMouseMotion)
            {
                if (_dragNode && _selNode >= 0)
                {
                    if (RaycastTerrain(GetViewport().GetMousePosition(), out var np))
                    { _roads.MoveJunction(_selNode, np); BuildNodeMarkers(); }
                }
                else if (_stroke) SampleStroke();
                else UpdateSnapRing();
                return;
            }

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M } && _lastRoad >= 0 && _roads.MaterialCount > 0)
            {
                SnapUndo("material");
                _material = (_roads.RoadMaterial(_lastRoad) + 1) % _roads.MaterialCount;
                _roads.SetRoadMaterial(_lastRoad, _material);
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false } dk && (dk.Keycode == Key.Delete || dk.Keycode == Key.Backspace))
            {
                if (_selNode >= 0)
                {
                    SnapUndo("delete junction");
                    _roads.RemoveJunction(_selNode);   // frees every end bound to it; the rails stay
                    _selNode = -1; BuildNodeMarkers();
                }
                else if (_lastRoad >= 0)
                {
                    SnapUndo("delete drawn road");
                    _roads.RemoveRoad(_lastRoad);
                    _lastRoad = -1; BuildNodeMarkers();
                }
            }
        }

        void SnapUndo(string label)   // capture the PRE-edit roads state; call BEFORE an edit
        {
            var snap = _roads.Snapshot();
            _editor.PushUndo(label, () => { _roads.Restore(snap); _lastRoad = -1; _selNode = -1; if (_drawing) BuildNodeMarkers(); });
        }

        void BeginStroke()
        {
            if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) return;
            _pts.Clear();
            // Pull the first point onto a node/joint if one is in range, so the rubber band starts where the
            // road will actually start. The BINDING is decided in EndStroke (ResolveJunction) -- doing it here
            // would create nodes for strokes the user then cancels.
            int nd0 = _roads.JunctionAt(pt, SnapRadius);
            if (nd0 >= 0) pt = _roads.JunctionPos(nd0);
            else if (_roads.NearestJoint(pt, SnapRadius, out int sr, out int sj)) pt = _roads.JointPos(sr, sj);
            _pts.Add(pt);
            _stroke = true;
        }

        void SampleStroke()
        {
            if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) return;
            if (_pts.Count == 0) { _pts.Add(pt); return; }
            if ((pt - _pts[^1]).LengthSquared() < SampleSpacing * SampleSpacing) return;
            _pts.Add(pt);
            DrawPreview();
        }

        void EndStroke()
        {
            if (!_stroke) return;
            _stroke = false;
            ClearPreview();
            if (_pts.Count < 2) { _pts.Clear(); return; }

            // Resolve BOTH ends to junction nodes before the road exists, then bind the road to them after.
            SnapUndo("draw road");
            int jStart = ResolveJunction(_pts, 0);
            int jEnd = ResolveJunction(_pts, -1);
            int road = _roads.AddRoadFromPolyline(_pts, _material);
            if (road >= 0)
            {
                _lastRoad = road;
                if (jStart >= 0) _roads.BindRoadEnd(road, atEnd: false, jStart);
                if (jEnd >= 0) _roads.BindRoadEnd(road, atEnd: true, jEnd);
            }
            GD.Print($"[road-draw] drew road {road} with {_pts.Count} joints, ends -> nodes ({jStart}, {jEnd})" +
                     $" -> {_roads.JunctionCount} nodes, {_roads.Junctions().Count} of them connecting 2+ roads");
            _pts.Clear();
            BuildNodeMarkers();
        }

        /// <summary>Resolve one end of a stroke to a junction NODE, creating one if needed, and return its
        /// index (-1 = leave this end free). Runs BEFORE the road exists, so the caller binds afterwards.
        ///
        /// Three cases, in priority order: an existing NODE nearby (bind to it -- this is how a 4th rail joins
        /// a 3-way); an existing road END nearby (promote that end into a new node and bind it, so the two
        /// become a real junction); an existing road MIDDLE nearby (split the road there, promote the split
        /// point into a node, bind BOTH halves -- which is what makes a branch a 3-way instead of a rail
        /// touching another rail's flank).
        ///
        /// Both ends go through this, not just the last one. The first version split only on the stroke's END,
        /// so drawing a branch by STARTING on an existing road produced something that looked connected and
        /// routed as a dead end -- caught by editor.road_draw_tool driving the tool, and invisible to the
        /// model-level test that called SplitRoadAt itself.</summary>
        int ResolveJunction(List<Vector3> work, int index)
        {
            if (work.Count == 0) return -1;
            int i = index < 0 ? work.Count - 1 : 0;

            int node = _roads.JunctionAt(work[i], SnapRadius);
            if (node >= 0) { work[i] = _roads.JunctionPos(node); return node; }

            if (!_roads.NearestJoint(work[i], SnapRadius, out int r, out int j)) return -1;
            var at = _roads.JointPos(r, j);
            work[i] = at;

            bool isEnd = j == 0 || j == _roads.JointCount(r) - 1;
            if (isEnd)
            {
                node = _roads.AddJunction(at);
                _roads.BindRoadEnd(r, atEnd: j != 0, node);
                return node;
            }

            int tail = _roads.SplitRoadAt(r, j);
            node = _roads.AddJunction(at);
            _roads.BindRoadEnd(r, atEnd: true, node);                 // head half now ENDS here
            if (tail >= 0) _roads.BindRoadEnd(tail, atEnd: false, node);   // tail half now STARTS here
            return node;
        }

        void CancelStroke() { _stroke = false; _pts.Clear(); ClearPreview(); }

        void DrawPreview()
        {
            ClearPreview();
            if (_pts.Count < 2) return;
            _preview = new MeshInstance3D();
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.LineStrip, new StandardMaterial3D
            { AlbedoColor = new Color(0.2f, 1f, 0.4f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true });
            foreach (var p in _pts) im.SurfaceAddVertex(p + Vector3.Up * 0.25f);
            im.SurfaceEnd();
            _preview.Mesh = im;
            AddChild(_preview);
        }

        void ClearPreview() { _preview?.QueueFree(); _preview = null; }

        void UpdateSnapRing()
        {
            ClearSnapRing();
            if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) return;
            int nd = _roads.JunctionAt(pt, SnapRadius);
            Vector3 at;
            if (nd >= 0) at = _roads.JunctionPos(nd);
            else if (_roads.NearestJoint(pt, SnapRadius, out int r, out int j)) at = _roads.JointPos(r, j);
            else return;
            _snapRing = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 1.6f, Height = 3.2f },
                MaterialOverride = new StandardMaterial3D
                { AlbedoColor = new Color(0.2f, 1f, 0.4f, 0.7f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                  Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true },
                Position = at,
            };
            AddChild(_snapRing);
        }

        void ClearSnapRing() { _snapRing?.QueueFree(); _snapRing = null; }

        bool RaycastTerrain(Vector2 screen, out Vector3 pt)
        {
            pt = Vector3.Zero;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 12000f;
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = TerrainLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            pt = (Vector3)hit["position"]; return true;
        }

        // --- test seams: the tool's behaviour without a mouse ---
        public void DebugSetDrawing(bool on) => SetDrawing(on);
        public int DebugNodeMarkerCount => _nodeMarkers.Count;
        public int DebugSelectedNode => _selNode;
        public void DebugDragNode(int node, Vector3 to) { _selNode = node; _roads.MoveJunction(node, to); BuildNodeMarkers(); }
        public int DebugDrawRoad(IReadOnlyList<Vector3> pts, bool snapEnds = true)
        {
            var work = new List<Vector3>(pts);
            int jS = -1, jE = -1;
            if (snapEnds && work.Count >= 2) { jS = ResolveJunction(work, 0); jE = ResolveJunction(work, -1); }   // same path the real stroke takes
            int road = _roads.AddRoadFromPolyline(work, _material);
            if (road >= 0)
            {
                _lastRoad = road;
                if (jS >= 0) _roads.BindRoadEnd(road, atEnd: false, jS);
                if (jE >= 0) _roads.BindRoadEnd(road, atEnd: true, jE);
            }
            return road;
        }
    }
}
