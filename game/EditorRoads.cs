using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Roads sub-editor, ported from SDG.Unturned EditorRoads (isPaving mode). Under the Environment tab. R toggles PAVING:
    // a YELLOW sphere at every bezier vertex (all roads). Selecting a joint/road reveals that road's CYAN tangent HANDLES
    // (the curve controls, on lines from each vertex) -- only the selected road's, so it stays uncluttered. LMB a marker to
    // select. G snaps the selection to the cursor: vertex = source moveVertex, handle = source moveTangent (mode-aware
    // MIRROR/ALIGNED/FREE). LMB on ground adds a vertex (a vertex selected) / a new road (nothing). Del/Ctrl+Del removes a
    // joint/road; N cycles the joint's tangent mode; M cycles the road's material; Esc deselects. Each edit re-extrudes
    // just that road (RoadField.RebuildRoad). Save writes Paths.dat back; reopening loads those edits. Source = the spec.
    public partial class EditorRoads : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly RoadField _roads;
        const uint RoadPickLayer = 1u << 10;   // own pick layer so road markers don't clash with object/terrain picking
        const uint TerrainLayer = 1u << 0;
        static readonly string[] ModeNames = { "MIRROR", "ALIGNED", "FREE" };

        bool _paving;
        int _selRoad = -1, _selJoint = -1, _selTan = -1;   // _selTan: -1 = vertex selected, 0/1 = a tangent handle
        int _handleRoad = -1;                              // which road's tangent handles are currently shown
        StaticBody3D _selBody;
        readonly List<StaticBody3D> _markers = new();          // vertex markers (all roads)
        readonly List<StaticBody3D> _handleMarkers = new();    // tangent handles (selected road only)
        readonly Dictionary<StaticBody3D, (int r, int j, int t)> _markerMap = new();   // both, for picking
        MeshInstance3D _handleLines;
        static readonly Color VertColor = new(1f, 0.85f, 0.2f), TanColor = new(0.2f, 0.85f, 1f), SelColor = new(1f, 0.15f, 0.1f);

        public bool Paving => _paving;
        public string ModeText => _paving
            ? (_selRoad >= 0
                ? $"PAVING · r{_selRoad} {(_selTan >= 0 ? $"tan{_selTan}" : $"j{_selJoint}")} · G move · N mode({ModeNames[_roads.JointMode(_selRoad, _selJoint)]}) · M mat({_roads.RoadMaterialName(_selRoad)}) · Del · Esc · R=off"
                : "PAVING · LMB marker=select · LMB ground=new road · R=off")
            : "R = roads paving";

        static string SavePath => ProjectSettings.GlobalizePath("res://content/roads/") + "editor_Paths.dat";

        public int Save()   // Editor.Save() fan-out: write Paths.dat back (only if there are roads)
        {
            if (_roads == null || _roads.RoadCount == 0) return 0;
            if (_roads.SavePaths(SavePath)) { GD.Print($"[editor-roads] saved -> {SavePath}"); return 1; }
            return 0;
        }

        public EditorRoads(Editor editor, Camera3D cam, RoadField roads)
        {
            _editor = editor; _cam = cam; _flyCam = cam as EditorCamera; _roads = roads;
            if (_roads != null && System.IO.File.Exists(SavePath) && _roads.ReloadPaths(SavePath)) GD.Print("[editor-roads] loaded saved road edits");
            _editor.ModeChanged += _ => { if (_editor.Mode != EEditorMode.Environment && _paving) SetPaving(false); };
        }

        void SetPaving(bool on) { _paving = on; if (on) BuildMarkers(); else ClearMarkers(); }

        void BuildMarkers()   // vertex markers for ALL roads (handles are shown per-selected-road, see ShowRoadHandles)
        {
            ClearMarkers();
            if (_roads == null) return;
            var vmesh = new SphereMesh { Radius = 1.4f, Height = 2.8f, RadialSegments = 8, Rings = 5 };
            for (int r = 0; r < _roads.RoadCount; r++)
                for (int j = 0; j < _roads.JointCount(r); j++)
                    AddMarker(_markers, vmesh, _roads.JointPos(r, j) + Vector3.Up * 1.2f, VertColor, 1.7f, r, j, -1);
            GD.Print($"[editor-roads] paving ON: {_markers.Count} joints across {_roads.RoadCount} roads");
        }

        void ShowRoadHandles(int road)   // reveal the tangent handles (+ lines) for one road only -> uncluttered
        {
            ClearHandles();
            _handleRoad = road;
            if (_roads == null || road < 0 || road >= _roads.RoadCount) return;
            var tmesh = new SphereMesh { Radius = 0.9f, Height = 1.8f, RadialSegments = 7, Rings = 4 };
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines, new StandardMaterial3D { AlbedoColor = TanColor, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded });
            for (int j = 0; j < _roads.JointCount(road); j++)
            {
                Vector3 v = _roads.JointPos(road, j) + Vector3.Up * 1.2f;
                for (int t = 0; t < 2; t++)
                {
                    Vector3 h = _roads.TangentPos(road, j, t) + Vector3.Up * 1.2f;
                    if (h.DistanceSquaredTo(v) < 0.5f) continue;   // zero tangent -> handle on the vertex, skip
                    AddMarker(_handleMarkers, tmesh, h, TanColor, 1.1f, road, j, t);
                    im.SurfaceAddVertex(v); im.SurfaceAddVertex(h);
                }
            }
            im.SurfaceEnd();
            if (im.GetSurfaceCount() > 0) { _handleLines = new MeshInstance3D { Mesh = im }; AddChild(_handleLines); }
        }

        void AddMarker(List<StaticBody3D> into, Mesh mesh, Vector3 pos, Color col, float pickR, int r, int j, int t)
        {
            var body = new StaticBody3D { CollisionLayer = RoadPickLayer, CollisionMask = 0, Position = pos };
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = pickR } });
            body.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MarkerMat(col) });
            AddChild(body);
            into.Add(body);
            _markerMap[body] = (r, j, t);
        }

        void ClearHandles()
        {
            foreach (var m in _handleMarkers) { _markerMap.Remove(m); m.QueueFree(); }
            _handleMarkers.Clear();
            _handleLines?.QueueFree(); _handleLines = null;
            _handleRoad = -1;
        }

        void ClearMarkers()
        {
            ClearHandles();
            foreach (var m in _markers) { _markerMap.Remove(m); m.QueueFree(); }
            _markers.Clear();
            _selBody = null; _selRoad = _selJoint = -1; _selTan = -1;
        }

        static StandardMaterial3D MarkerMat(Color c) => new() { AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true };
        static void SetMarkerColor(StaticBody3D body, Color c) { foreach (var ch in body.GetChildren()) if (ch is MeshInstance3D mi) mi.MaterialOverride = MarkerMat(c); }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (_editor.Mode != EEditorMode.Environment || (_flyCam != null && _flyCam.Flying)) return;
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R }) { SetPaving(!_paving); return; }
            if (!_paving) return;
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                var body = PickMarker(GetViewport().GetMousePosition());
                if (body != null && _markerMap.TryGetValue(body, out var rj)) Select(body, rj.r, rj.j, rj.t);
                else if (RaycastTerrain(GetViewport().GetMousePosition(), out var pt))   // source: LMB on ground = add vertex (a vertex selected) / add road (nothing)
                {
                    if (_selRoad >= 0 && _selTan < 0)
                    {
                        int road = _selRoad, ni = _roads.AddVertexNearSelected(road, _selJoint, pt);
                        if (ni >= 0) RefreshAndSelect(road, ni, -1);
                    }
                    else if (_selRoad < 0) { int nr = _roads.AddRoad(pt); RefreshAndSelect(nr, 0, -1); }
                }
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.G } && _selRoad >= 0)
            {
                if (RaycastTerrain(GetViewport().GetMousePosition(), out var pt))
                {
                    if (_selTan >= 0) _roads.SetTangent(_selRoad, _selJoint, _selTan, pt);   // source moveTangent (mode-aware)
                    else _roads.SetJointPos(_selRoad, _selJoint, pt);                        // source moveVertex
                    RefreshAndSelect(_selRoad, _selJoint, _selTan);
                }
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.N } && _selRoad >= 0)   // cycle tangent mode
                _roads.SetJointMode(_selRoad, _selJoint, (byte)((_roads.JointMode(_selRoad, _selJoint) + 1) % 3));
            else if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M } && _selRoad >= 0 && _roads.MaterialCount > 0)   // cycle material
            {
                _roads.SetRoadMaterial(_selRoad, (_roads.RoadMaterial(_selRoad) + 1) % _roads.MaterialCount);
                RefreshAndSelect(_selRoad, _selJoint, _selTan);
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) Deselect();
            else if (ev is InputEventKey { Pressed: true, Echo: false } dk && (dk.Keycode == Key.Delete || dk.Keycode == Key.Backspace) && _selRoad >= 0 && _selTan < 0)
            {
                if (Input.IsKeyPressed(Key.Ctrl)) _roads.RemoveRoad(_selRoad);   // source Ctrl+Del: whole road
                else _roads.RemoveVertex(_selRoad, _selJoint);                   // source Del: the joint (whole road if <2 left)
                Deselect(); BuildMarkers();
            }
        }

        void RefreshAndSelect(int r, int j, int t)   // after an edit: rebuild vertex markers + the road's handles, reselect
        {
            BuildMarkers();
            if (r >= 0 && r < _roads.RoadCount) { ShowRoadHandles(r); SelectMarker(r, j, t); }
        }

        void SelectMarker(int road, int joint, int tan)
        {
            foreach (var kv in _markerMap) if (kv.Value.r == road && kv.Value.j == joint && kv.Value.t == tan) { Select(kv.Key, road, joint, tan); return; }
        }

        void Select(StaticBody3D body, int road, int joint, int tan)
        {
            if (road != _handleRoad) ShowRoadHandles(road);   // switching roads -> reveal the new road's handles
            if (_selBody != null && IsInstanceValid(_selBody) && _markerMap.TryGetValue(_selBody, out var old))
                SetMarkerColor(_selBody, old.t < 0 ? VertColor : TanColor);
            _selBody = body; _selRoad = road; _selJoint = joint; _selTan = tan;
            SetMarkerColor(body, SelColor);
        }

        void Deselect()
        {
            if (_selBody != null && IsInstanceValid(_selBody) && _markerMap.TryGetValue(_selBody, out var old))
                SetMarkerColor(_selBody, old.t < 0 ? VertColor : TanColor);
            _selBody = null; _selRoad = _selJoint = -1; _selTan = -1;
            ClearHandles();
        }

        StaticBody3D PickMarker(Vector2 screen)
        {
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 12000f;
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = RoadPickLayer };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            return hit.Count > 0 ? hit["collider"].As<StaticBody3D>() : null;
        }

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

        // --- harness demos (UG_EDITORROADS [+ UG_ROADADD / UG_ROADTAN / UG_ROADCLEAN]) ---
        public void DemoMove(int road, int joint, Vector3 to)
        {
            SetPaving(true);
            if (road < _roads.RoadCount && joint < _roads.JointCount(road)) { _roads.SetJointPos(road, joint, to); RefreshAndSelect(road, joint, -1); GD.Print($"[editor-roads] demo moved road {road} joint {joint}"); }
        }

        public Vector3 DemoAddVertex(int road, Vector3 offset)
        {
            SetPaving(true);
            if (road >= _roads.RoadCount || _roads.JointCount(road) < 1) return Vector3.Zero;
            int last = _roads.JointCount(road) - 1;
            Vector3 at = _roads.JointPos(road, last) + offset;
            int ni = _roads.AddVertexNearSelected(road, last, at);
            RefreshAndSelect(road, ni, -1);
            GD.Print($"[editor-roads] demo added vertex to road {road} -> joint {ni} at {at}");
            return at;
        }

        public void DemoRemoveVertex(int road, int joint)
        {
            if (road >= _roads.RoadCount) return;
            int before = _roads.JointCount(road);
            bool removedRoad = _roads.RemoveVertex(road, joint);
            GD.Print($"[editor-roads] demo removed road {road} joint {joint}: {before} joints -> {(removedRoad ? "ROAD removed" : _roads.JointCount(road) + " joints")}");
        }

        public Vector3 DemoMoveTangent(int road, int joint, int ti, Vector3 handleWorld)
        {
            SetPaving(true);
            if (road < _roads.RoadCount && joint < _roads.JointCount(road))
            {
                _roads.SetTangent(road, joint, ti, handleWorld);
                RefreshAndSelect(road, joint, ti);
                GD.Print($"[editor-roads] demo moved road {road} joint {joint} tangent {ti} -> handle {handleWorld} (mode {ModeNames[_roads.JointMode(road, joint)]})");
            }
            return handleWorld;
        }

        public void DemoSetMaterial(int road, int m)
        {
            if (road >= _roads.RoadCount) return;
            int before = _roads.RoadMaterial(road);
            _roads.SetRoadMaterial(road, m);
            GD.Print($"[editor-roads] demo set road {road} material {before} -> {_roads.RoadMaterial(road)} ({_roads.RoadMaterialName(road)}, of {_roads.MaterialCount})");
        }

        public Vector3 DemoPave(int road, int joint) { SetPaving(true); return DemoJoint(road, joint); }
        public int DemoJointCount(int road) => _roads != null ? _roads.JointCount(road) : 0;
        public bool HasRoads => _roads != null && _roads.RoadCount > 0;
        public Vector3 DemoJoint(int road, int joint) => HasRoads && road < _roads.RoadCount && joint < _roads.JointCount(road) ? _roads.JointPos(road, joint) : Vector3.Zero;
    }
}
