using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>The map editor's power-line tool: pick a pole, pick the next one, and four wires are strung
    /// between their grey connection points.
    ///
    /// Master 2026-10-06: "a map editor tool for power line wire splines, theres 4 wire connection points on them
    /// (gray) should draw a line and the 4 wires should connect."
    ///
    /// ⭐ YOU PICK POLES, NOT ANCHORS. Four separate wires could in principle be authored one at a time, but the
    /// thing a mapper is doing is RUNNING A LINE down a road -- nobody wants to click sixteen times per span, and
    /// the four anchors always pair the same way (outer to outer, inner to inner). So a span is one click and the
    /// four wires fall out of it. PowerLineField pairs by anchor INDEX, so two poles facing opposite ways still
    /// pair correctly instead of crossing their wires over.
    ///
    /// ⭐ AND THE SELECTION CHAINS. After connecting A to B the tool leaves B selected, so running a line of
    /// twenty poles is twenty clicks rather than forty. That is the whole reason the tool exists rather than the
    /// wires being auto-generated from proximity: the mapper decides which poles are a circuit.</summary>
    public partial class EditorPowerLines : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly PowerLineField _field;

        /// <summary>Own pick layer so pole markers cannot be confused with object, terrain or road picking.
        /// ⚠ A COLLISION bit, not a visual one -- the visual layers in use are 15-19 and these are unrelated.</summary>
        const uint PolePickLayer = 1u << 21;
        const uint TerrainLayer = 1u << 0;

        static readonly Color PoleColor = new(0.35f, 0.75f, 1f);
        static readonly Color SelColor = new(1f, 0.85f, 0.15f);

        bool _on;
        int _sel = -1;
        readonly List<StaticBody3D> _markers = new();
        readonly Dictionary<StaticBody3D, int> _markerMap = new();
        MeshInstance3D _preview;   // the rubber-band line from the selected pole to the cursor

        public bool Active => _on;

        readonly List<Transform3D> _mapPoles;
        readonly EditorObjects _objects;

        /// <param name="mapPoles">Poles the MAP shipped with (WorldBuildResult.PowerLinePoles).</param>
        /// <param name="objects">The object editor, so poles placed in THIS session carry wires too. May be null.</param>
        public EditorPowerLines(Editor editor, Camera3D cam, PowerLineField field, List<Transform3D> mapPoles, EditorObjects objects)
        {
            _editor = editor; _cam = cam; _field = field;
            _mapPoles = mapPoles ?? new List<Transform3D>();
            _objects = objects;
        }

        /// <summary>The pole set as it stands right now: the map's, plus anything placed since. Rebuilt on every
        /// activation rather than cached, because the object editor can add and delete poles while this tool is
        /// closed and a stale list is indistinguishable from the tool not working.</summary>
        /// <summary>⚠ The MESH travels with each transform now, because the field holds two kinds. A pylon
        /// handed over as a plain pole would be wired on the roadside pole's four anchors and hang its
        /// conductors in mid-air beside the lattice.</summary>
        IEnumerable<(Transform3D Xform, string Mesh)> AllPoles()
        {
            foreach (var x in _mapPoles) yield return (x, PowerLineField.PoleMesh);
            if (_objects != null)
            {
                foreach (var x in _objects.PlacedOf(PowerLineField.PoleMesh)) yield return (x, PowerLineField.PoleMesh);
                foreach (var x in _objects.PlacedOf(PowerLineField.PylonMesh)) yield return (x, PowerLineField.PylonMesh);
            }
        }

        public string ModeText => _on
            ? (_sel >= 0
                ? $"POWER LINES · pole {_sel} selected · LMB another pole = string 4 wires · Del = cut this pole's wires · Esc"
                : $"POWER LINES · {_field.SpanCount} span(s) · LMB a pole to start · O = face poles at their wires · Shift+P = off")
            : "Shift+P = power lines";

        public void SetActive(bool on) { if (_on != on) Toggle(); }

        void Toggle()
        {
            _on = !_on;
            if (_on)
            {
                int n = _field.RefreshPoles(AllPoles(), out int dropped);
                _field.Rebuild();
                BuildMarkers();
                Log.Print($"[editor-powerlines] ON: {n} poles, {_field.SpanCount} spans"
                        + (dropped > 0 ? $" ({dropped} dropped -- their poles are gone)" : ""));
            }
            else { ClearMarkers(); _sel = -1; ClearPreview(); }
        }

        // ---- markers ----------------------------------------------------------------------------------------

        void BuildMarkers()
        {
            ClearMarkers();
            var mesh = new SphereMesh { Radius = 0.9f, Height = 1.8f };
            for (int i = 0; i < _field.PoleCount; i++)
            {
                // At the TOP of the pole rather than its base: that is where the wires are, it is what you are
                // aiming at, and a marker at the foot would be buried in whatever the pole is standing in.
                var p = _field.PoleXform(i) * new Vector3(0f, 0f, PowerLineField.AnchorsLocal[0].Z);
                var body = new StaticBody3D { CollisionLayer = PolePickLayer, CollisionMask = 0, Position = p };
                body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 1.1f } });
                body.AddChild(new MeshInstance3D
                {
                    Mesh = mesh,
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = PoleColor, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
                });
                AddChild(body);
                _markers.Add(body);
                _markerMap[body] = i;
            }
        }

        void ClearMarkers()
        {
            foreach (var m in _markers) { _markerMap.Remove(m); m.QueueFree(); }
            _markers.Clear();
        }

        void Recolour()
        {
            for (int i = 0; i < _markers.Count; i++)
                foreach (var c in _markers[i].GetChildren())
                    if (c is MeshInstance3D mi && mi.MaterialOverride is StandardMaterial3D sm)
                        sm.AlbedoColor = (i == _sel) ? SelColor : PoleColor;
        }

        // ---- input ------------------------------------------------------------------------------------------

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.P } && Input.IsKeyPressed(Key.Shift))
            { Toggle(); GetViewport().SetInputAsHandled(); return; }
            if (!_on) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z } && Input.IsKeyPressed(Key.Ctrl)) { _editor.Undo(); return; }
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { _sel = -1; Recolour(); ClearPreview(); return; }

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Delete } && _sel >= 0)
            {
                SnapUndo("cut power lines");
                int n = _field.DisconnectAll(_sel);
                _field.Rebuild();
                _editor.MarkDirty();
                Log.Print($"[editor-powerlines] cut {n} span(s) at pole {_sel}");
                return;
            }

            // ⭐ O = FACE THE POLES AT THEIR WIRES. Master: "add support for 3 and 4 way connections too, the
            // power pole rotating however appropriate." Three- and four-way junctions already wired -- Connect
            // never had a degree limit -- but a junction pole kept whatever yaw it was dropped at, so its
            // crossarms pointed wherever the mapper happened to be facing. This turns every pole onto the
            // principal axis of the spans it actually carries; see PowerLineField.SuggestedYawDeg.
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.O })
            {
                SnapUndo("face poles at their wires");
                int turned = 0, left = 0;
                // ⚠ BOTH KINDS, in the same order AllPoles yields them, or the yaws land on the wrong poles.
                var nodes = new List<Node3D>(_objects.PlacedOfNodes(PowerLineField.PoleMesh));
                nodes.AddRange(_objects.PlacedOfNodes(PowerLineField.PylonMesh));
                for (int i = 0; i < nodes.Count && i < _field.PoleCount; i++)
                {
                    float yaw = _field.SuggestedYawDeg(i, out bool ok);
                    if (!ok) { left++; continue; }   // a symmetric cross has no best answer -- leave it alone
                    // ⚠ PRESERVE THE SCALE. A pylon is placed at PylonScale through its basis, so rebuilding
                    // the transform from Upright alone would silently shrink it back to native on the first
                    // alignment -- and take its conductor anchors in with it.
                    float keep = nodes[i].Transform.Basis.Scale.X;
                    var b = EditorObjects.Upright(yaw);
                    nodes[i].Transform = new Transform3D(new Basis(b.X * keep, b.Y * keep, b.Z * keep), nodes[i].Position);
                    turned++;
                }
                _field.RefreshPoles(AllPoles(), out _);
                _field.Rebuild();
                BuildMarkers();
                Recolour();
                _editor.MarkDirty();
                Log.Print($"[editor-powerlines] faced {turned} pole(s) at their wires"
                        + (left > 0 ? $", left {left} symmetric junction(s) as placed" : ""));
                return;
            }

            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !Editor.PointerOverUI(this))
            {
                var body = PickMarker(GetViewport().GetMousePosition());
                if (body == null || !_markerMap.TryGetValue(body, out int idx)) return;
                if (_sel < 0) { _sel = idx; Recolour(); return; }
                if (idx == _sel) { _sel = -1; Recolour(); ClearPreview(); return; }

                SnapUndo("string power line");
                if (_field.Connect(_sel, idx, out string why))
                {
                    _field.Rebuild();
                    _editor.MarkDirty();
                    Log.Print($"[editor-powerlines] span {_sel} -> {idx} ({_field.SpanCount} total)");
                }
                else Log.Print($"[editor-powerlines] refused: {why}");
                // CHAIN: the pole you just reached becomes the new start, so a run of poles is one click each.
                _sel = idx;
                Recolour();
            }
        }

        public override void _Process(double delta)
        {
            if (!_on || _sel < 0) { ClearPreview(); return; }
            // RUBBER BAND to the cursor, so you can see which span you are about to make before you commit it --
            // the spans are long and the poles are small on screen, which is exactly when a misclick is invisible.
            if (!RaycastTerrain(GetViewport().GetMousePosition(), out var pt)) { ClearPreview(); return; }
            var from = _field.PoleXform(_sel) * new Vector3(0f, 0f, PowerLineField.AnchorsLocal[0].Z);
            if (_preview == null)
            {
                _preview = new MeshInstance3D
                {
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = SelColor, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(_preview);
            }
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            im.SurfaceAddVertex(from);
            im.SurfaceAddVertex(pt);
            im.SurfaceEnd();
            _preview.Mesh = im;
        }

        void ClearPreview() { if (_preview != null) { _preview.QueueFree(); _preview = null; } }

        // ---- plumbing ---------------------------------------------------------------------------------------

        /// <summary>Snapshot the span list for Ctrl+Z. ⚠ The POLES are not snapshotted -- they are rebuilt from the
        /// map's props, and an undo that restored a stale pole list would silently re-point every span.</summary>
        void SnapUndo(string label)
        {
            var snap = new List<PowerLineField.Span>(_field.Spans);
            _editor.PushUndo(label, () =>
            {
                _field.RestoreSpans(snap);
                _field.Rebuild();
                _sel = -1; Recolour(); ClearPreview();
            });
        }

        StaticBody3D PickMarker(Vector2 screen)
        {
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 4000f;
            var q = PhysicsRayQueryParameters3D.Create(from, to, PolePickLayer);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            return hit.Count > 0 && hit["collider"].As<GodotObject>() is StaticBody3D b ? b : null;
        }

        bool RaycastTerrain(Vector2 screen, out Vector3 point)
        {
            point = Vector3.Zero;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 4000f;
            var q = PhysicsRayQueryParameters3D.Create(from, to, TerrainLayer);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            point = (Vector3)hit["position"];
            return true;
        }

        /// <summary>Rebuild the pole markers after the object editor has added, moved or deleted poles.</summary>
        public void RefreshPoles() { if (_on) { BuildMarkers(); Recolour(); } }
    }
}
