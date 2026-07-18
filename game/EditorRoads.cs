using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Roads sub-editor, ported from SDG.Unturned EditorRoads (isPaving mode). Lives under the Environment tab (source
    // EditorEnvironmentRoadsUI). Press R to toggle PAVING: a marker sphere appears at every road's bezier vertices.
    // LMB a marker selects that joint; G snaps the selected joint to the cursor's terrain point (source tool_2
    // moveVertex) -> the road's spline mesh re-extrudes LIVE. Save writes Paths.dat back (RoadField.SavePaths).
    // Increment 1 = select + move vertices. Tangent handles, add/remove vertex, add/remove road, and the material
    // picker are the NEXT slices -- source EditorRoads has all of them (addVertex/removeVertex/moveTangent/selected).
    public partial class EditorRoads : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorCamera _flyCam;
        readonly RoadField _roads;
        const uint RoadPickLayer = 1u << 10;   // own pick layer so road markers don't clash with object/terrain picking
        const uint TerrainLayer = 1u << 0;

        bool _paving;
        int _selRoad = -1, _selJoint = -1;
        StaticBody3D _selBody;
        readonly List<StaticBody3D> _markers = new();
        readonly Dictionary<StaticBody3D, (int r, int j)> _markerMap = new();
        static readonly Color MarkerColor = new(1f, 0.85f, 0.2f), SelColor = new(1f, 0.15f, 0.1f);

        public bool Paving => _paving;
        public string ModeText => _paving
            ? $"PAVING · LMB select joint · G move to cursor · {(_selRoad >= 0 ? $"road {_selRoad} joint {_selJoint}" : "none sel")} · R=off"
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
            _editor.ModeChanged += _ => { if (_editor.Mode != EEditorMode.Environment && _paving) SetPaving(false); };
        }

        void SetPaving(bool on) { _paving = on; if (on) BuildMarkers(); else ClearMarkers(); }

        void BuildMarkers()
        {
            ClearMarkers();
            if (_roads == null) return;
            var mesh = new SphereMesh { Radius = 1.4f, Height = 2.8f, RadialSegments = 8, Rings = 5 };
            for (int r = 0; r < _roads.RoadCount; r++)
                for (int j = 0; j < _roads.JointCount(r); j++)
                {
                    var body = new StaticBody3D { CollisionLayer = RoadPickLayer, CollisionMask = 0, Position = _roads.JointPos(r, j) + Vector3.Up * 1.2f };
                    body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 1.7f } });
                    body.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = MarkerMat(MarkerColor) });
                    AddChild(body);
                    _markers.Add(body);
                    _markerMap[body] = (r, j);
                }
            GD.Print($"[editor-roads] paving ON: {_markers.Count} joints across {_roads.RoadCount} roads");
        }

        void ClearMarkers()
        {
            foreach (var m in _markers) m.QueueFree();
            _markers.Clear(); _markerMap.Clear();
            _selBody = null; _selRoad = _selJoint = -1;
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
                if (body != null && _markerMap.TryGetValue(body, out var rj)) Select(body, rj.r, rj.j);
            }
            else if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.G } && _selRoad >= 0)
            {
                if (RaycastTerrain(GetViewport().GetMousePosition(), out var pt))
                {
                    _roads.SetJointPos(_selRoad, _selJoint, pt);   // source moveVertex -> RebuildRoad re-extrudes the spline live
                    if (_selBody != null) _selBody.Position = pt + Vector3.Up * 1.2f;
                }
            }
        }

        void Select(StaticBody3D body, int road, int joint)
        {
            if (_selBody != null) SetMarkerColor(_selBody, MarkerColor);
            _selBody = body; _selRoad = road; _selJoint = joint;
            SetMarkerColor(body, SelColor);
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

        // harness (UG_EDITORROADS): enable paving + move a joint so a headless render shows the road bending
        public void DemoMove(int road, int joint, Vector3 to)
        {
            SetPaving(true);
            if (road < _roads.RoadCount && joint < _roads.JointCount(road))
            {
                _selRoad = road; _selJoint = joint;
                _roads.SetJointPos(road, joint, to);
                GD.Print($"[editor-roads] demo moved road {road} joint {joint} -> {to}");
            }
        }

        public bool HasRoads => _roads != null && _roads.RoadCount > 0;
        public Vector3 DemoJoint(int road, int joint) => HasRoads && road < _roads.RoadCount && joint < _roads.JointCount(road) ? _roads.JointPos(road, joint) : Vector3.Zero;
    }
}
