using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // The standalone Asset Factory editor (main-menu tool). Compose meshes into one asset and
    // HAND-PLACE its colliders / volumes / named hook-points with the gizmo — no more guessing
    // mounts from bundle math — then Save a self-contained .assetbundle the game auto-loads.
    // Reuses EditorCamera (fly) + EditorGizmo (transform).
    //
    // Phase 2: parts (add/select/gizmo/delete/save).  Phase 3 (here): colliders + volumes + named
    // points as gizmo-selectable items, with per-type hook-name presets.  Phase 4: per-type binders.
    public partial class AssetFactoryEditor : Node3D
    {
        public System.Action OnExit;
        public System.Action<string> OnPlay;   // ▶ Play: save + drop into a per-type preview scene (passes the bundle name)

        enum Kind { Part, Collider, Volume, Point }

        AssetBundle _bundle = new() { Name = "new_asset", Type = "prop" };
        string _savePath;
        Node3D _composeRoot;
        readonly List<MeshInstance3D> _partNodes = new();
        readonly List<Node3D> _colNodes = new();
        readonly List<Node3D> _volNodes = new();
        readonly List<Node3D> _ptNodes = new();
        Kind _selKind = Kind.Part;
        int _selIdx = -1;

        EditorCamera _cam;
        EditorGizmo _gizmo;

        VBoxContainer _listBox;
        Panel _picker;
        Panel _openPanel; VBoxContainer _openList;   // open a saved .assetbundle to edit
        LineEdit _nameEdit;
        OptionButton _typeOpt, _hookOpt, _surfOpt, _powerKind;   // behaviours: impact surface, power in/out
        LineEdit _powerWatts, _powerLabel;
        LineEdit _gunDamage, _gunRpm, _gunAmmo, _gunRange, _gunVehDmg, _gunSpread, _gunPellets, _gunRecoil, _gunVel;   // gun-type stats authored per bundle (ApplyFactoryGunStats reads these)
        OptionButton _gunPreset;   // pick any real weapon -> load its full stats as a starting point
        LineEdit _depHealth, _depFuel, _depEnergy, _depCharge;   // deployable DEVICE stats (BuildDeployableDef reads deploy_*)
        CheckBox _depBattery, _depSwitch, _depTurbine, _depShatter;   // deployable device behaviour toggles
        LineEdit _vehEngine, _vehSpeed, _vehSteer, _vehBrake, _vehFuel, _vehHealth, _vehSusp;   // vehicle drive stats (BuildFromBundle reads these)
        OptionButton _vehPreset, _vehWheel, _vehSteerModel, _vehSeatModel;   // preset = full driving feel base; wheel/steer/seat = swap the mesh asset
        VBoxContainer _gunPanel, _devicePanel, _vehiclePanel;   // type-gated panels: gun on gun, device on deployable, vehicle on vehicle
        CheckBox _poweredLight;   // powered-flag behaviour
        Label _status;
        string[] _meshNames = System.Array.Empty<string>();
        Kind _clipKind; object _clipObj;    // copy/paste clipboard (a cloned item)
        string _pickerName;                 // the mesh highlighted in the picker (E places it)
        SubViewport _previewVp; Node3D _previewPivot; MeshInstance3D _previewMesh;   // 3D spinning preview of the highlighted mesh

        public void Setup(string loadPath = null)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.53f, 0.67f, 0.86f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.92f, 0.92f, 0.94f), AmbientLightEnergy = 1.1f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -38f, 0f), LightEnergy = 1.25f, ShadowEnabled = true });
            AddChild(BuildGroundGrid());

            _cam = new EditorCamera { Position = new Vector3(3.5f, 2.6f, 4.5f), RotationDegrees = new Vector3(-22f, 38f, 0f), Current = true };
            AddChild(_cam);
            _gizmo = new EditorGizmo(_cam);
            AddChild(_gizmo);
            _composeRoot = new Node3D { Name = "Compose" };
            AddChild(_composeRoot);

            if (loadPath != null)
            {
                var b = AssetBundle.Load(loadPath);
                if (b != null) { _bundle = b; _savePath = loadPath; }
                else GD.Print($"[assetfactory] could not load {loadPath} — starting empty");
            }
            _meshNames = ScanMeshes();
            RebuildAll();
            AutoFitColliderIfNone();   // a loaded bundle with no collider gets one FIT to the prop -> the viz box hugs the mesh
            RebuildAll();
            BuildUI();
            Select(Kind.Part, _bundle.Parts.Count > 0 ? 0 : -1);
            GD.Print($"[assetfactory] editor up: {_bundle.Name} [{_bundle.Type}] — {_bundle.Parts.Count}p/{_bundle.Colliders.Count}c/{_bundle.Volumes.Count}v/{_bundle.Points.Count}pt, {_meshNames.Length} meshes");

            if (System.Environment.GetEnvironmentVariable("UG_AFSELFTEST") == "1") SelfTest();
            if (System.Environment.GetEnvironmentVariable("UG_AFPICKER") == "1")   // render hook: open the picker + preview a mesh
            {
                TogglePicker(true);
                SetPickerMesh(System.Array.IndexOf(_meshNames, "axe_fire.txt") >= 0 ? "axe_fire.txt" : (_meshNames.Length > 0 ? _meshNames[0] : null));
            }
            if (System.Environment.GetEnvironmentVariable("UG_AFOPEN") == "1") { RefreshOpenList(); if (_openPanel != null) _openPanel.Visible = true; }   // render hook: show the open-bundle list
        }

        // ---- live nodes <-> bundle ------------------------------------------
        void RebuildAll()
        {
            foreach (var n in AllNodes()) if (IsInstanceValid(n)) n.QueueFree();
            _partNodes.Clear(); _colNodes.Clear(); _volNodes.Clear(); _ptNodes.Clear();
            foreach (var p in _bundle.Parts) { var mi = AssetBundleLoader.BuildPart(p) ?? Placeholder(p); _composeRoot.AddChild(mi); _partNodes.Add(mi); }
            foreach (var c in _bundle.Colliders) { var n = BoxViz(new Color(1f, 0.85f, 0.1f, 0.28f), c.Pos, c.Rot, c.Size); _composeRoot.AddChild(n); _colNodes.Add(n); }
            foreach (var v in _bundle.Volumes) { var n = BoxViz(new Color(0.1f, 0.85f, 1f, 0.24f), v.Pos, v.Rot, v.Size); _composeRoot.AddChild(n); _volNodes.Add(n); }
            foreach (var pt in _bundle.Points) { var n = PointViz(pt.Pos, pt.Rot); _composeRoot.AddChild(n); _ptNodes.Add(n); }
            RebuildVehiclePreview();   // vehicle: draw the REAL wheel meshes at the Wheel_* points (master "show how it'll look")
        }

        readonly List<Node3D> _vehPreview = new();   // wheel/steer/seat meshes + headlight markers & lights (editor preview)
        void RebuildVehiclePreview()
        {
            foreach (var w in _vehPreview) if (IsInstanceValid(w)) w.QueueFree();
            _vehPreview.Clear();
            if (_bundle.Type != "vehicle") return;
            if (_partNodes.Count > 0) { var bp = Vehicle.BodyPaintFor(_bundle); if (bp != null) _partNodes[0].MaterialOverride = bp; }   // paint the body like the runtime (else it keeps its flat BuildPart albedo)
            var bodyPart = _bundle.Parts.Count > 0 ? _bundle.Parts[0] : null;
            var bodyMeshName = bodyPart?.Mesh ?? "";
            var bBase = bodyMeshName.EndsWith("_body.txt") ? bodyMeshName[..^9] : bodyMeshName.EndsWith(".txt") ? bodyMeshName[..^4] : bodyMeshName;
            bool eRealHl = bBase.Length > 0 && Godot.FileAccess.FileExists($"res://content/{bBase}_headlights.txt");   // ships ripped lights == a real vehicle body
            bool eRealTl = bBase.Length > 0 && Godot.FileAccess.FileExists($"res://content/{bBase}_taillights.txt");
            // wheels at each Wheel_* hook (left wheels mirrored so the tread faces out, like the runtime rig)
            var wheelName = Vehicle.WheelMeshFor(_bundle); var wheelTex = Vehicle.WheelTexFor(_bundle);
            foreach (var pt in _bundle.Points)
            {
                if (pt.Name == null || !pt.Name.StartsWith("Wheel_")) continue;
                var p = AssetBundle.V3(pt.Pos);
                AddDetailMesh(wheelName, wheelTex, p, new Vector3(p.X < 0 ? -1f : 1f, 1f, 1f));
            }
            // seats + steering wheel: a real vehicle body uses its OWN ripped set at the body transform (jeep body -> the
            // jeep's 4 seats, master "does jeep not have 4 seats usually?"); a custom body uses the author-placed hooks
            // (ripped meshes are baked at source coords -> AABB-centre them on the hook, like Build() at runtime).
            if (eRealHl)
            {
                if (Godot.FileAccess.FileExists($"res://content/{bBase}_seats.txt")) AddLightMesh($"{bBase}_seats.txt", new Color(0.25f, 0.25f, 0.25f), bodyPart, false);
                if (Godot.FileAccess.FileExists($"res://content/{bBase}_steer.txt")) AddLightMesh($"{bBase}_steer.txt", new Color(0.28f, 0.23f, 0.14f), bodyPart, false);
            }
            else
            {
                var steerPt = _bundle.Points.Find(pt => pt.Name == "Steer");
                if (steerPt != null) AddCenteredDetail(Vehicle.SteerMeshFor(_bundle), new Color(0.13f, 0.11f, 0.08f), AssetBundle.V3(steerPt.Pos));
                var seatPt = _bundle.Points.Find(pt => pt.Name != null && pt.Name.StartsWith("Seat_"));
                if (seatPt != null) AddCenteredDetail(Vehicle.SeatMeshFor(_bundle), new Color(0.22f, 0.22f, 0.24f), AssetBundle.V3(seatPt.Pos));
            }
            // headlight + taillight LENS geometry (master "the actual headlight parts... are missing"): the REAL ripped
            // {base}_headlights/taillights mesh at the body transform if a vehicle body, else a cream/red lens box at each
            // hook. + a forward spotlight per Headlight hook as a light cue.
            if (eRealHl) AddLightMesh($"{bBase}_headlights.txt", new Color(1f, 0.96f, 0.72f), bodyPart);
            if (eRealTl) AddLightMesh($"{bBase}_taillights.txt", new Color(1f, 0.2f, 0.2f), bodyPart);
            foreach (var pt in _bundle.Points)
            {
                if (pt.Name == null) continue;
                if (pt.Name.StartsWith("Headlight"))
                {
                    var hp = AssetBundle.V3(pt.Pos);
                    if (!eRealHl) { var lens = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.26f, 0.20f, 0.10f) }, Position = hp, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.96f, 0.72f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } }; _composeRoot.AddChild(lens); _vehPreview.Add(lens); }
                    var beam = new SpotLight3D { Position = hp, SpotRange = 8f, SpotAngle = 32f, LightColor = new Color(1f, 0.95f, 0.75f), LightEnergy = 3f };   // fires local -Z = the vehicle's forward
                    _composeRoot.AddChild(beam); _vehPreview.Add(beam);
                }
                else if (pt.Name.StartsWith("Taillight") && !eRealTl)
                {
                    var tp = AssetBundle.V3(pt.Pos);
                    var lens = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.24f, 0.16f, 0.08f) }, Position = tp, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.15f, 0.15f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
                    _composeRoot.AddChild(lens); _vehPreview.Add(lens);
                }
            }
            GD.Print($"[vehpreview] rendered {_vehPreview.Count} detail nodes (wheels + steer + seat + head/tail light lenses at their hooks)");
        }

        void AddDetailMesh(string mesh, string albedo, Vector3 pos, Vector3 scale)
        {
            if (string.IsNullOrEmpty(mesh)) return;
            var part = new AssetBundle.Part { Mesh = mesh, Albedo = albedo ?? AssetBundle.ResolveAlbedo(mesh), Pos = new[] { pos.X, pos.Y, pos.Z }, Rot = new[] { 0f, 0f, 0f }, Scale = new[] { scale.X, scale.Y, scale.Z } };
            var mi = AssetBundleLoader.BuildPart(part);
            if (mi == null) return;
            _composeRoot.AddChild(mi); _vehPreview.Add(mi);
        }

        // seat/steer: ripped meshes baked at their SOURCE-vehicle coords, drawn dark-solid + AABB-centred on the hook so
        // the editor preview lands them exactly where Build() puts them on the drivable vehicle (else they float away).
        void AddCenteredDetail(string mesh, Color mat, Vector3 hook)
        {
            if (string.IsNullOrEmpty(mesh)) return;
            var m = ContentProvider.ParseObj($"res://content/{mesh}");
            if (m == null) return;
            var mi = new MeshInstance3D { Mesh = m, MaterialOverride = new StandardMaterial3D { AlbedoColor = mat, CullMode = BaseMaterial3D.CullModeEnum.Disabled }, Position = hook - m.GetAabb().GetCenter() };   // double-sided: ripped seat/steer front-faces would otherwise cull -> "inside out" (master)
            _composeRoot.AddChild(mi); _vehPreview.Add(mi);
        }

        // real ripped headlight/taillight LENS mesh at the BODY part's transform (baked in body space), unshaded glow so
        // it reads as a lit lens in the editor -- mirrors BuildFromBundle loading {base}_headlights/taillights.txt.
        void AddLightMesh(string mesh, Color col, AssetBundle.Part bodyPart, bool unshaded = true)
        {
            if (bodyPart == null) return;
            var m = ContentProvider.ParseObj($"res://content/{mesh}");
            if (m == null) return;
            var basis = AssetBundle.EulerDegBasis(bodyPart.Rot).Scaled(AssetBundle.V3(bodyPart.Scale, Vector3.One));
            var mat = new StandardMaterial3D { AlbedoColor = col, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            if (unshaded) mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;   // lights glow; seats/steer stay shaded
            var mi = new MeshInstance3D { Mesh = m, Transform = new Transform3D(basis, AssetBundle.V3(bodyPart.Pos)), MaterialOverride = mat };
            _composeRoot.AddChild(mi); _vehPreview.Add(mi);
        }

        IEnumerable<Node3D> AllNodes()
        {
            foreach (var n in _partNodes) yield return n;
            foreach (var n in _colNodes) yield return n;
            foreach (var n in _volNodes) yield return n;
            foreach (var n in _ptNodes) yield return n;
        }

        Node3D SelNode() => _selKind switch
        {
            Kind.Part => Valid(_partNodes, _selIdx) ? _partNodes[_selIdx] : null,
            Kind.Collider => Valid(_colNodes, _selIdx) ? _colNodes[_selIdx] : null,
            Kind.Volume => Valid(_volNodes, _selIdx) ? _volNodes[_selIdx] : null,
            Kind.Point => Valid(_ptNodes, _selIdx) ? _ptNodes[_selIdx] : null,
            _ => null,
        };
        static bool Valid<T>(List<T> l, int i) => i >= 0 && i < l.Count;

        void WriteBack()
        {
            var n = SelNode(); if (n == null) return;
            var pos = new[] { n.Position.X, n.Position.Y, n.Position.Z };
            var rot = new[] { n.RotationDegrees.X, n.RotationDegrees.Y, n.RotationDegrees.Z };
            var scl = new[] { n.Scale.X, n.Scale.Y, n.Scale.Z };
            switch (_selKind)
            {
                case Kind.Part: { var p = _bundle.Parts[_selIdx]; p.Pos = pos; p.Rot = rot; p.Scale = scl; break; }
                case Kind.Collider: { var c = _bundle.Colliders[_selIdx]; c.Pos = pos; c.Rot = rot; if (c.Shape == "box") c.Size = scl; break; }
                case Kind.Volume: { var v = _bundle.Volumes[_selIdx]; v.Pos = pos; v.Rot = rot; v.Size = scl; break; }
                case Kind.Point: { var t = _bundle.Points[_selIdx]; t.Pos = pos; t.Rot = rot; break; }
            }
        }

        void Select(Kind k, int i)
        {
            WriteBack();
            _selKind = k;
            int count = k switch { Kind.Part => _partNodes.Count, Kind.Collider => _colNodes.Count, Kind.Volume => _volNodes.Count, Kind.Point => _ptNodes.Count, _ => 0 };
            _selIdx = (i >= 0 && i < count) ? i : -1;
            var node = SelNode();
            // pivot the gizmo at a PART's visual centre (mesh AABB centre), not its node origin, so the gimbal sits
            // dead-centre on the prop (master). colliders/volumes/points are already centred at their node -> zero.
            Vector3 pivot = (k == Kind.Part && node is MeshInstance3D mi && mi.Mesh != null) ? mi.Mesh.GetAabb().GetCenter() : Vector3.Zero;
            _gizmo.Attach(node, pivot);
            RefreshList();
        }

        // ---- add / delete ---------------------------------------------------
        void AddPart(string mesh)
        {
            WriteBack();
            _bundle.Parts.Add(new AssetBundle.Part { Mesh = mesh, Albedo = AssetBundle.ResolveAlbedo(mesh), Pos = new[] { 0f, 1f, 0f }, Rot = new[] { 0f, 0f, 0f }, Scale = new[] { 1f, 1f, 1f } });
            RebuildAll(); Select(Kind.Part, _bundle.Parts.Count - 1); Status($"added part {mesh}");
        }

        void AddCollider()
        {
            WriteBack();
            // hug the prop's actual mesh bounds, not a floating default [1,1,1]@[0,1,0] (master: the outline should match the prop)
            var pos = new[] { 0f, 1f, 0f }; var size = new[] { 1f, 1f, 1f };
            if (TryPartsAabb(out var bb)) { var c = bb.GetCenter(); var s = bb.Size; pos = new[] { c.X, c.Y, c.Z }; size = new[] { s.X, s.Y, s.Z }; }
            _bundle.Colliders.Add(new AssetBundle.Collider { Shape = "box", Pos = pos, Rot = new[] { 0f, 0f, 0f }, Size = size });
            RebuildAll(); Select(Kind.Collider, _bundle.Colliders.Count - 1); Status("added box collider (fit to prop)");
        }

        void AddVolume()
        {
            WriteBack();
            _bundle.Volumes.Add(new AssetBundle.Volume { Name = "volume", Pos = new[] { 0f, 1f, 0f }, Rot = new[] { 0f, 0f, 0f }, Size = new[] { 1f, 1f, 1f } });
            RebuildAll(); Select(Kind.Volume, _bundle.Volumes.Count - 1); Status("added volume");
        }

        void AddPoint()
        {
            WriteBack();
            string nm = (_hookOpt != null && _hookOpt.Selected >= 0) ? _hookOpt.GetItemText(_hookOpt.Selected) : "Point_0";
            _bundle.Points.Add(new AssetBundle.Point { Name = nm, Pos = new[] { 0f, 1f, 0f }, Rot = new[] { 0f, 0f, 0f } });
            RebuildAll(); Select(Kind.Point, _bundle.Points.Count - 1); Status($"added point {nm}");
        }

        void CopySelected()
        {
            if (_selIdx < 0) return;
            object clone = _selKind switch
            {
                Kind.Part => ClonePart(_bundle.Parts[_selIdx]),
                Kind.Collider => CloneCollider(_bundle.Colliders[_selIdx]),
                Kind.Volume => CloneVolume(_bundle.Volumes[_selIdx]),
                Kind.Point => ClonePoint(_bundle.Points[_selIdx]),
                _ => null,
            };
            if (clone != null) { _clipKind = _selKind; _clipObj = clone; Status($"copied {_selKind}"); }
        }

        void PasteClipboard()
        {
            if (_clipObj == null) return;
            switch (_clipKind)
            {
                case Kind.Part: { var p = ClonePart((AssetBundle.Part)_clipObj); Nudge(p.Pos); _bundle.Parts.Add(p); RebuildAll(); Select(Kind.Part, _bundle.Parts.Count - 1); break; }
                case Kind.Collider: { var c = CloneCollider((AssetBundle.Collider)_clipObj); Nudge(c.Pos); _bundle.Colliders.Add(c); RebuildAll(); Select(Kind.Collider, _bundle.Colliders.Count - 1); break; }
                case Kind.Volume: { var v = CloneVolume((AssetBundle.Volume)_clipObj); Nudge(v.Pos); _bundle.Volumes.Add(v); RebuildAll(); Select(Kind.Volume, _bundle.Volumes.Count - 1); break; }
                case Kind.Point: { var t = ClonePoint((AssetBundle.Point)_clipObj); Nudge(t.Pos); _bundle.Points.Add(t); RebuildAll(); Select(Kind.Point, _bundle.Points.Count - 1); break; }
            }
            Status("pasted");
        }

        static void Nudge(float[] pos) { if (pos != null && pos.Length >= 3) { pos[0] += 0.5f; pos[2] += 0.5f; } }
        static AssetBundle.Part ClonePart(AssetBundle.Part p) => new() { Mesh = p.Mesh, Albedo = p.Albedo, Color = (float[])p.Color?.Clone(), Pos = (float[])p.Pos.Clone(), Rot = (float[])p.Rot.Clone(), Scale = (float[])p.Scale.Clone() };
        static AssetBundle.Collider CloneCollider(AssetBundle.Collider c) => new() { Shape = c.Shape, Pos = (float[])c.Pos.Clone(), Rot = (float[])c.Rot.Clone(), Size = (float[])c.Size.Clone() };
        static AssetBundle.Volume CloneVolume(AssetBundle.Volume v) => new() { Name = v.Name, Pos = (float[])v.Pos.Clone(), Rot = (float[])v.Rot.Clone(), Size = (float[])v.Size.Clone() };
        static AssetBundle.Point ClonePoint(AssetBundle.Point p) => new() { Name = p.Name, Pos = (float[])p.Pos.Clone(), Rot = (float[])p.Rot.Clone() };

        void DeleteSelected()
        {
            if (_selIdx < 0) return;
            switch (_selKind)
            {
                case Kind.Part: if (Valid(_bundle.Parts, _selIdx)) _bundle.Parts.RemoveAt(_selIdx); break;
                case Kind.Collider: if (Valid(_bundle.Colliders, _selIdx)) _bundle.Colliders.RemoveAt(_selIdx); break;
                case Kind.Volume: if (Valid(_bundle.Volumes, _selIdx)) _bundle.Volumes.RemoveAt(_selIdx); break;
                case Kind.Point: if (Valid(_bundle.Points, _selIdx)) _bundle.Points.RemoveAt(_selIdx); break;
            }
            RebuildAll();
            Select(_selKind, -1);
            Status("deleted");
        }

        void Save()
        {
            WriteBack();
            _bundle.Name = SanitizeName(_nameEdit?.Text);
            if (_typeOpt != null && _typeOpt.Selected >= 0) _bundle.Type = _typeOpt.GetItemText(_typeOpt.Selected);
            AutoFitColliderIfNone();
            string path = _savePath ?? $"res://content/assets/{_bundle.Name}.assetbundle";
            _bundle.Save(path); _savePath = path;
            Status($"saved {_bundle.Name}.assetbundle ({_bundle.Parts.Count}p/{_bundle.Colliders.Count}c/{_bundle.Volumes.Count}v/{_bundle.Points.Count}pt)");
        }

        // The parts' combined WORLD-space AABB (transformed mesh bounds) -- so a box collider can HUG the prop.
        bool TryPartsAabb(out Aabb bb)
        {
            bb = default; bool has = false;
            foreach (var n in _partNodes)
            {
                if (!IsInstanceValid(n) || n.Mesh == null) continue;
                var lb = n.Mesh.GetAabb(); var xf = n.Transform;
                for (int i = 0; i < 8; i++)
                {
                    var corner = lb.Position + new Vector3((i & 1) * lb.Size.X, ((i >> 1) & 1) * lb.Size.Y, ((i >> 2) & 1) * lb.Size.Z);
                    var wp = xf * corner;
                    if (!has) { bb = new Aabb(wp, Vector3.Zero); has = true; } else bb = bb.Expand(wp);
                }
            }
            return has;
        }

        void AutoFitColliderIfNone()
        {
            if (_bundle.Colliders.Count > 0 || _partNodes.Count == 0) return;
            if (!TryPartsAabb(out var bb)) return;
            var c = bb.GetCenter(); var s = bb.Size;
            _bundle.Colliders.Add(new AssetBundle.Collider { Shape = "box", Pos = new[] { c.X, c.Y, c.Z }, Rot = new[] { 0f, 0f, 0f }, Size = new[] { s.X, s.Y, s.Z } });
        }

        // ---- input ----------------------------------------------------------
        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { if (_gizmo.TryBeginDrag(mb.Position)) GetViewport().SetInputAsHandled(); }
                else if (_gizmo.Dragging) { _gizmo.EndDrag(); WriteBack(); }
            }
            else if (ev is InputEventMouseMotion mm && _gizmo.Dragging)
                _gizmo.DragTo(mm.Position, Input.IsKeyPressed(Key.Ctrl));
            else if (ev is InputEventKey k && k.Pressed && !k.Echo)
            {
                if (k.CtrlPressed && k.Keycode == Key.C) CopySelected();
                else if (k.CtrlPressed && k.Keycode == Key.V) PasteClipboard();
                else if (k.Keycode == Key.T) { _gizmo.CycleMode(); Status($"gizmo: {GizmoMode()}"); }
                else if (k.Keycode == Key.G) { _gizmo.LocalSpace = !_gizmo.LocalSpace; Status(_gizmo.LocalSpace ? "local space" : "global space"); }
                else if (k.Keycode == Key.B) { _gizmo.LocalSpace = false; Status("global space"); }   // B = global transform space
                else if (k.Keycode == Key.N) { _gizmo.LocalSpace = true; Status("local space"); }      // N = local transform space
                else if (k.Keycode == Key.E) { if (_pickerName != null) AddPart(_pickerName); }         // E = place the highlighted picker mesh
                else if (k.Keycode == Key.Delete) DeleteSelected();
            }
        }

        string GizmoMode() => _gizmo.Mode switch { EditorGizmo.EMode.Rotate => "rotate", EditorGizmo.EMode.Scale => "scale", _ => "move" };

        // ---- UI -------------------------------------------------------------
        void BuildUI()
        {
            var layer = new CanvasLayer();
            AddChild(layer);
            var panel = new PanelContainer { Position = new Vector2(12, 12), CustomMinimumSize = new Vector2(300, 0) };
            layer.AddChild(panel);
            var col = new VBoxContainer();
            panel.AddChild(col);

            var title = new Label { Text = "ASSET FACTORY" };
            title.AddThemeFontSizeOverride("font_size", 22);
            col.AddChild(title);

            col.AddChild(new Label { Text = "name" });
            _nameEdit = new LineEdit { Text = _bundle.Name, CustomMinimumSize = new Vector2(276, 0) };
            col.AddChild(_nameEdit);
            col.AddChild(new Label { Text = "type" });
            _typeOpt = new OptionButton();
            foreach (var t in new[] { "prop", "deployable", "vehicle", "gun" }) _typeOpt.AddItem(t);
            for (int i = 0; i < _typeOpt.ItemCount; i++) if (_typeOpt.GetItemText(i) == _bundle.Type) _typeOpt.Selected = i;
            _typeOpt.ItemSelected += _ => { RepopulateHooks(); UpdatePanelVis(); };
            col.AddChild(_typeOpt);

            col.AddChild(new Label { Text = "— behaviours —" });
            var bRow = new HBoxContainer();
            bRow.AddChild(new Label { Text = "impact surface" });
            _surfOpt = new OptionButton();
            foreach (var sName in new[] { "none", "concrete", "grass", "dirt", "metal", "wood", "sand", "water" }) _surfOpt.AddItem(sName);
            SyncSurfUI();
            _surfOpt.ItemSelected += _ => { var v = _surfOpt.GetItemText(_surfOpt.Selected); _bundle.SetParam("surface", v == "none" ? "" : v); Status($"impact surface: {v}"); };
            bRow.AddChild(_surfOpt);
            col.AddChild(bRow);

            var pRow = new HBoxContainer();
            pRow.AddChild(new Label { Text = "power" });
            _powerKind = new OptionButton();
            foreach (var k in new[] { "none", "output", "consumer", "passthrough" }) _powerKind.AddItem(k);
            _powerKind.ItemSelected += _ => WritePower();
            pRow.AddChild(_powerKind);
            _powerWatts = new LineEdit { PlaceholderText = "watts", CustomMinimumSize = new Vector2(64, 0) };
            _powerWatts.TextChanged += _ => WritePower();
            pRow.AddChild(_powerWatts);
            col.AddChild(pRow);
            _powerLabel = new LineEdit { PlaceholderText = "port label (renameable)", CustomMinimumSize = new Vector2(276, 0) };
            _powerLabel.TextChanged += _ => WritePower();
            col.AddChild(_powerLabel);
            _poweredLight = new CheckBox { Text = "powered light (on when powered)" };
            _poweredLight.Toggled += _ => WritePower();
            col.AddChild(_poweredLight);
            SyncPowerUI();

            // GUN panel (gated to type=gun by UpdatePanelVis). preset dropdown (master 2026-07-23): pick any of the 31
            // real weapons -> load its FULL stats as the starting point, then tweak the fields. "— none —" leaves params.
            _gunPanel = new VBoxContainer();
            col.AddChild(_gunPanel);
            var presetRow = new HBoxContainer();
            presetRow.AddChild(new Label { Text = "preset" });
            _gunPreset = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
            _gunPreset.AddItem("— none —");
            foreach (var n in GunPresets.Names()) _gunPreset.AddItem(n);
            _gunPreset.ItemSelected += i => { if (i > 0) { var nm = _gunPreset.GetItemText((int)i); GunPresets.WriteToBundle(_bundle, nm); SyncGunUI(); Status($"loaded weapon preset: {nm}"); } };
            presetRow.AddChild(_gunPreset);
            _gunPanel.AddChild(presetRow);

            var gRow = new HBoxContainer();   // gun stats (type=gun): each factory gun shoots its own numbers
            gRow.AddChild(new Label { Text = "gun stats" });
            _gunDamage = NumField("dmg"); gRow.AddChild(_gunDamage);
            _gunRpm = NumField("rpm"); gRow.AddChild(_gunRpm);
            _gunAmmo = NumField("mag"); gRow.AddChild(_gunAmmo);
            _gunRange = NumField("range"); gRow.AddChild(_gunRange);
            _gunVehDmg = NumField("veh dmg"); gRow.AddChild(_gunVehDmg);
            _gunPanel.AddChild(gRow);
            var gRow2 = new HBoxContainer();   // FEEL: the recoil/spread/pellets/velocity master asked for
            gRow2.AddChild(new Label { Text = "feel" });
            _gunSpread = NumField("spread"); gRow2.AddChild(_gunSpread);
            _gunPellets = NumField("pellets"); gRow2.AddChild(_gunPellets);
            _gunRecoil = NumField("recoil"); gRow2.AddChild(_gunRecoil);
            _gunVel = NumField("velocity"); gRow2.AddChild(_gunVel);
            _gunPanel.AddChild(gRow2);
            SyncGunUI();

            // deployable DEVICE panel (master 2026-07-23, gated to type=deployable): author a factory deployable as a
            // real power device. ports come from PowerOut/In/Thru named points (add via +Point); these are the knobs.
            _devicePanel = new VBoxContainer();
            col.AddChild(_devicePanel);
            var dRow = new HBoxContainer();
            dRow.AddChild(new Label { Text = "device" });
            _depHealth = NumFieldD("health"); dRow.AddChild(_depHealth);
            _depFuel = NumFieldD("fuel"); dRow.AddChild(_depFuel);
            _depEnergy = NumFieldD("energy"); dRow.AddChild(_depEnergy);
            _depCharge = NumFieldD("chg W"); dRow.AddChild(_depCharge);
            _devicePanel.AddChild(dRow);
            var dRow2 = new HBoxContainer();
            _depBattery = DevCheck("battery"); dRow2.AddChild(_depBattery);
            _depSwitch = DevCheck("switch"); dRow2.AddChild(_depSwitch);
            _depTurbine = DevCheck("turbine"); dRow2.AddChild(_depTurbine);
            _depShatter = DevCheck("shatter"); dRow2.AddChild(_depShatter);
            _devicePanel.AddChild(dRow2);
            SyncDeviceUI();

            // VEHICLE panel (gated to type=vehicle): preset dropdown of all 18 real vehicles + drive-stat overrides.
            _vehiclePanel = new VBoxContainer();
            col.AddChild(_vehiclePanel);
            var vpRow = new HBoxContainer();
            vpRow.AddChild(new Label { Text = "preset" });
            _vehPreset = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
            _vehPreset.AddItem("— none (jeep) —");
            foreach (var n in Vehicle.SpecNames) _vehPreset.AddItem(n);
            _vehPreset.ItemSelected += i => { if (i > 0) { var nm = _vehPreset.GetItemText((int)i); Vehicle.WritePresetParams(_bundle, nm); SyncVehicleUI(); RebuildVehiclePreview(); Status($"vehicle preset: {nm}"); } else { _bundle.SetParam("veh_preset", ""); SyncVehicleUI(); RebuildVehiclePreview(); Status("vehicle preset: none (jeep)"); } };
            vpRow.AddChild(_vehPreset);
            _vehiclePanel.AddChild(vpRow);
            var vRow = new HBoxContainer();
            vRow.AddChild(new Label { Text = "drive" });
            _vehEngine = NumFieldV("engine"); vRow.AddChild(_vehEngine);
            _vehSpeed = NumFieldV("speed"); vRow.AddChild(_vehSpeed);
            _vehSteer = NumFieldV("steer"); vRow.AddChild(_vehSteer);
            _vehBrake = NumFieldV("brake"); vRow.AddChild(_vehBrake);
            _vehFuel = NumFieldV("fuel"); vRow.AddChild(_vehFuel);
            _vehHealth = NumFieldV("health"); vRow.AddChild(_vehHealth);
            _vehiclePanel.AddChild(vRow);
            var vRow2 = new HBoxContainer();   // wheel asset + suspension (master: swap wheels + adjust suspension)
            vRow2.AddChild(new Label { Text = "wheel" });
            _vehWheel = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
            _vehWheel.AddItem("— preset's —");
            foreach (var w in WheelAssets) _vehWheel.AddItem(w);
            _vehWheel.ItemSelected += i => { _bundle.SetParam("veh_wheel", i > 0 ? _vehWheel.GetItemText((int)i) : ""); RebuildVehiclePreview(); Status(i > 0 ? $"wheel: {_vehWheel.GetItemText((int)i)}" : "wheel: preset's"); };
            vRow2.AddChild(_vehWheel);
            vRow2.AddChild(new Label { Text = "susp" });
            _vehSusp = NumFieldV("travel"); vRow2.AddChild(_vehSusp);
            _vehiclePanel.AddChild(vRow2);
            var vRow3 = new HBoxContainer();   // steering-wheel + seat model pickers (master "steering wheel model, seat positions and models?")
            vRow3.AddChild(new Label { Text = "steer" });
            _vehSteerModel = new OptionButton { CustomMinimumSize = new Vector2(140, 0) };
            _vehSteerModel.AddItem("— preset's —");
            foreach (var sMesh in SteerAssets) _vehSteerModel.AddItem(sMesh);
            _vehSteerModel.ItemSelected += i => { _bundle.SetParam("veh_steer_model", i > 0 ? _vehSteerModel.GetItemText((int)i) : ""); RebuildVehiclePreview(); Status(i > 0 ? $"steer model: {_vehSteerModel.GetItemText((int)i)}" : "steer model: preset's"); };
            vRow3.AddChild(_vehSteerModel);
            vRow3.AddChild(new Label { Text = "seat" });
            _vehSeatModel = new OptionButton { CustomMinimumSize = new Vector2(140, 0) };
            _vehSeatModel.AddItem("— preset's —");
            foreach (var sMesh in SeatAssets) _vehSeatModel.AddItem(sMesh);
            _vehSeatModel.ItemSelected += i => { _bundle.SetParam("veh_seat_model", i > 0 ? _vehSeatModel.GetItemText((int)i) : ""); RebuildVehiclePreview(); Status(i > 0 ? $"seat model: {_vehSeatModel.GetItemText((int)i)}" : "seat model: preset's"); };
            vRow3.AddChild(_vehSeatModel);
            _vehiclePanel.AddChild(vRow3);
            SyncVehicleUI();
            UpdatePanelVis();

            var addRow = new HBoxContainer();
            var addPart = new Button { Text = "＋Part" }; addPart.Pressed += () => TogglePicker(true); addRow.AddChild(addPart);
            var addCol = new Button { Text = "＋Box" }; addCol.Pressed += AddCollider; addRow.AddChild(addCol);
            var addVol = new Button { Text = "＋Vol" }; addVol.Pressed += AddVolume; addRow.AddChild(addVol);
            col.AddChild(addRow);

            var ptRow = new HBoxContainer();
            _hookOpt = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
            col.AddChild(new Label { Text = "hook point:" });
            RepopulateHooks();
            ptRow.AddChild(_hookOpt);
            var addPt = new Button { Text = "＋Point" }; addPt.Pressed += AddPoint; ptRow.AddChild(addPt);
            col.AddChild(ptRow);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(276, 240) };
            _listBox = new VBoxContainer();
            scroll.AddChild(_listBox);
            col.AddChild(scroll);

            var delBtn = new Button { Text = "🗑 Delete Selected" }; delBtn.Pressed += DeleteSelected; col.AddChild(delBtn);
            var openBtn = new Button { Text = "📂 Open" }; openBtn.Pressed += () => { TogglePicker(false); RefreshOpenList(); if (_openPanel != null) _openPanel.Visible = true; }; col.AddChild(openBtn);
            var saveBtn = new Button { Text = "💾 Save" }; saveBtn.Pressed += Save; col.AddChild(saveBtn);
            var playBtn = new Button { Text = "▶ Play / Preview" }; playBtn.Pressed += () => { Save(); OnPlay?.Invoke(_bundle.Name); }; col.AddChild(playBtn);   // save + drop into a per-type test scene
            var exitBtn = new Button { Text = "Exit" }; exitBtn.Pressed += () => OnExit?.Invoke(); col.AddChild(exitBtn);
            _status = new Label { Text = "" }; col.AddChild(_status);
            col.AddChild(new Label { Text = "select an item → drag gizmo · T mode · G space · Del" });

            BuildPicker(layer);
            BuildOpenPanel(layer);
            RefreshList();
        }

        void BuildOpenPanel(CanvasLayer layer)
        {
            _openPanel = new Panel { Position = new Vector2(324, 12), CustomMinimumSize = new Vector2(300, 400), Visible = false };
            layer.AddChild(_openPanel);
            var col = new VBoxContainer { CustomMinimumSize = new Vector2(300, 400) };
            _openPanel.AddChild(col);
            col.AddChild(new Label { Text = "open a saved .assetbundle to edit" });
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(288, 340) };
            _openList = new VBoxContainer();
            scroll.AddChild(_openList);
            col.AddChild(scroll);
            var close = new Button { Text = "close" }; close.Pressed += () => { if (_openPanel != null) _openPanel.Visible = false; }; col.AddChild(close);
        }

        void RefreshOpenList()
        {
            if (_openList == null) return;
            foreach (var c in _openList.GetChildren()) c.QueueFree();
            foreach (var f in DirAccess.GetFilesAt(AssetCatalog.Dir))
            {
                if (!f.EndsWith(".assetbundle")) continue;
                string path = AssetCatalog.Dir + f;
                var b = new Button { Text = f, Alignment = HorizontalAlignment.Left };
                b.Pressed += () => { LoadBundle(path); if (_openPanel != null) _openPanel.Visible = false; };
                _openList.AddChild(b);
            }
        }

        void LoadBundle(string path)
        {
            var b = AssetBundle.Load(path);
            if (b == null) { Status($"failed to open {path}"); return; }
            _bundle = b; _savePath = path;
            if (_nameEdit != null) _nameEdit.Text = _bundle.Name;
            if (_typeOpt != null) for (int i = 0; i < _typeOpt.ItemCount; i++) if (_typeOpt.GetItemText(i) == _bundle.Type) _typeOpt.Selected = i;
            RepopulateHooks();
            SyncSurfUI();
            SyncPowerUI();
            SyncGunUI();
            SyncDeviceUI();
            SyncVehicleUI();
            UpdatePanelVis();
            RebuildAll();
            Select(Kind.Part, _bundle.Parts.Count > 0 ? 0 : -1);
            Status($"opened {System.IO.Path.GetFileNameWithoutExtension(path)} ({_bundle.Parts.Count}p/{_bundle.Colliders.Count}c/{_bundle.Volumes.Count}v/{_bundle.Points.Count}pt)");
        }

        void RepopulateHooks()
        {
            if (_hookOpt == null) return;
            _hookOpt.Clear();
            string type = (_typeOpt != null && _typeOpt.Selected >= 0) ? _typeOpt.GetItemText(_typeOpt.Selected) : _bundle.Type;
            foreach (var h in HooksFor(type)) _hookOpt.AddItem(h);
            if (_hookOpt.ItemCount > 0) _hookOpt.Selected = 0;
        }

        void SyncSurfUI()
        {
            if (_surfOpt == null) return;
            string cur = _bundle.ParamString("surface") ?? "none";
            for (int i = 0; i < _surfOpt.ItemCount; i++) if (_surfOpt.GetItemText(i) == cur) { _surfOpt.Selected = i; return; }
            _surfOpt.Selected = 0;
        }

        void WritePower()
        {
            if (_powerKind == null) return;
            string k = _powerKind.GetItemText(_powerKind.Selected);
            if (k == "none") { _bundle.SetParam("power_kind", ""); _bundle.SetParam("power_watts", 0f); _bundle.SetParam("power_label", ""); _bundle.SetParam("powered_light", false); }
            else
            {
                _bundle.SetParam("power_kind", k);
                _bundle.SetParam("power_watts", float.TryParse(_powerWatts?.Text, out var w) ? w : 0f);
                _bundle.SetParam("power_label", _powerLabel?.Text ?? "");
                _bundle.SetParam("powered_light", _poweredLight?.ButtonPressed ?? false);
            }
            Status($"power: {k}");
        }

        void SyncPowerUI()
        {
            if (_powerKind == null) return;
            string k = _bundle.ParamString("power_kind") ?? "none";
            int idx = 0;
            for (int i = 0; i < _powerKind.ItemCount; i++) if (_powerKind.GetItemText(i) == k) { idx = i; break; }
            _powerKind.Selected = idx;
            float w = _bundle.ParamFloat("power_watts", 0f);
            if (_powerWatts != null) _powerWatts.Text = w > 0f ? w.ToString("0") : "";
            if (_powerLabel != null) _powerLabel.Text = _bundle.ParamString("power_label") ?? "";
            if (_poweredLight != null) _poweredLight.ButtonPressed = _bundle.ParamBool("powered_light");
        }

        LineEdit NumField(string ph) { var e = new LineEdit { PlaceholderText = ph, CustomMinimumSize = new Vector2(58, 0) }; e.TextChanged += _ => WriteGunStats(); return e; }

        void WriteGunStats()
        {
            SetNumParam("gun_damage", _gunDamage?.Text);
            SetNumParam("gun_rpm", _gunRpm?.Text);
            SetNumParam("gun_ammo", _gunAmmo?.Text);
            SetNumParam("gun_range", _gunRange?.Text);
            SetNumParam("gun_vehicle_damage", _gunVehDmg?.Text);
            SetNumParam("gun_spread", _gunSpread?.Text);
            SetNumParam("gun_pellets", _gunPellets?.Text);
            SetNumParam("gun_muzzle_velocity", _gunVel?.Text);
            // "recoil" = the vertical-kick knob -> drives both min/max Y (a slight min<max range reads natural, not fixed)
            if (float.TryParse(_gunRecoil?.Text, out var rc) && rc > 0f) { _bundle.SetParam("gun_recoil_min_y", rc * 0.8f); _bundle.SetParam("gun_recoil_max_y", rc); }
        }

        void SetNumParam(string key, string text) { if (float.TryParse(text, out var v) && v > 0f) _bundle.SetParam(key, v); }

        void SyncGunUI()
        {
            if (_gunDamage == null) return;
            _gunDamage.Text = ParamNumStr("gun_damage"); _gunRpm.Text = ParamNumStr("gun_rpm");
            _gunAmmo.Text = ParamNumStr("gun_ammo"); _gunRange.Text = ParamNumStr("gun_range");
            _gunVehDmg.Text = ParamNumStr("gun_vehicle_damage");
            _gunSpread.Text = ParamNumStr("gun_spread"); _gunPellets.Text = ParamNumStr("gun_pellets");
            _gunRecoil.Text = ParamNumStr("gun_recoil_max_y"); _gunVel.Text = ParamNumStr("gun_muzzle_velocity");
        }

        string ParamNumStr(string key) { float v = _bundle.ParamFloat(key, 0f); return v > 0f ? v.ToString("0.###") : ""; }

        // --- deployable DEVICE panel (author a factory power device: generator/battery/switch/turbine) ---
        LineEdit NumFieldD(string ph) { var e = new LineEdit { PlaceholderText = ph, CustomMinimumSize = new Vector2(58, 0) }; e.TextChanged += _ => WriteDeviceStats(); return e; }
        CheckBox DevCheck(string label) { var c = new CheckBox { Text = label }; c.Toggled += _ => WriteDeviceStats(); return c; }

        void WriteDeviceStats()
        {
            SetNumParam("deploy_health", _depHealth?.Text);
            SetNumParam("deploy_fuel", _depFuel?.Text);
            SetNumParam("deploy_energy_max", _depEnergy?.Text);
            SetNumParam("deploy_charge_watts", _depCharge?.Text);
            _bundle.SetParam("deploy_battery", _depBattery?.ButtonPressed ?? false);
            _bundle.SetParam("deploy_switch", _depSwitch?.ButtonPressed ?? false);
            _bundle.SetParam("deploy_wind_turbine", _depTurbine?.ButtonPressed ?? false);
            _bundle.SetParam("deploy_shatter", _depShatter?.ButtonPressed ?? false);
        }

        void SyncDeviceUI()
        {
            if (_depHealth == null) return;
            _depHealth.Text = ParamNumStr("deploy_health"); _depFuel.Text = ParamNumStr("deploy_fuel");
            _depEnergy.Text = ParamNumStr("deploy_energy_max"); _depCharge.Text = ParamNumStr("deploy_charge_watts");
            _depBattery.SetPressedNoSignal(_bundle.ParamBool("deploy_battery"));   // NoSignal: don't re-fire WriteDeviceStats during a sync
            _depSwitch.SetPressedNoSignal(_bundle.ParamBool("deploy_switch"));
            _depTurbine.SetPressedNoSignal(_bundle.ParamBool("deploy_wind_turbine"));
            _depShatter.SetPressedNoSignal(_bundle.ParamBool("deploy_shatter"));
        }

        // Show only the panel relevant to the current type: gun stats/feel on a gun, device knobs on a deployable.
        void UpdatePanelVis()
        {
            string t = (_typeOpt != null && _typeOpt.Selected >= 0) ? _typeOpt.GetItemText(_typeOpt.Selected) : _bundle.Type;
            if (_gunPanel != null) _gunPanel.Visible = t == "gun";
            if (_devicePanel != null) _devicePanel.Visible = t == "deployable";
            if (_vehiclePanel != null) _vehiclePanel.Visible = t == "vehicle";
        }

        // --- vehicle DRIVE panel (author a factory vehicle: preset base + engine/speed/steer/brake/fuel/health) ---
        LineEdit NumFieldV(string ph) { var e = new LineEdit { PlaceholderText = ph, CustomMinimumSize = new Vector2(58, 0) }; e.TextChanged += _ => WriteVehicleStats(); return e; }

        void WriteVehicleStats()
        {
            SetNumParam("engine", _vehEngine?.Text);
            SetNumParam("speed_max", _vehSpeed?.Text);
            SetNumParam("veh_steer", _vehSteer?.Text);
            SetNumParam("veh_brake", _vehBrake?.Text);
            SetNumParam("veh_fuel", _vehFuel?.Text);
            SetNumParam("health", _vehHealth?.Text);
            SetNumParam("veh_suspension", _vehSusp?.Text);
        }

        // The distinct real wheel meshes (from the vehicle Specs) offered by the wheel picker.
        static readonly string[] WheelAssets = { "jeep_wheel.txt", "quad_wheel.txt", "bus_wheel.txt", "sedan_wheel.txt", "hatchback_wheel.txt", "humvee_wheel.txt", "roadster_wheel.txt", "tractor_wheel_front.txt" };
        static readonly string[] SteerAssets = { "jeep_steer.txt", "roadster_steer.txt", "sedan_steer.txt", "hatchback_steer.txt", "humvee_steer.txt", "offroad_steer.txt", "quad_steer.txt", "golf_steer.txt", "van_steer.txt", "truck_steer.txt", "bus_steer.txt", "ambulance_steer.txt", "firetruck_steer.txt", "police_steer.txt", "tractor_steer.txt", "ural_steer.txt" };
        static readonly string[] SeatAssets = { "jeep_seats.txt", "roadster_seats.txt", "sedan_seats.txt", "hatchback_seats.txt", "humvee_seats.txt", "offroad_seats.txt", "golf_seats.txt", "bus_seats.txt", "ambulance_seats.txt", "firetruck_seats.txt" };

        void SyncVehicleUI()
        {
            if (_vehEngine == null) return;
            _vehEngine.Text = ParamNumStr("engine"); _vehSpeed.Text = ParamNumStr("speed_max");
            _vehSteer.Text = ParamNumStr("veh_steer"); _vehBrake.Text = ParamNumStr("veh_brake");
            _vehFuel.Text = ParamNumStr("veh_fuel"); _vehHealth.Text = ParamNumStr("health");
            _vehSusp.Text = ParamNumStr("veh_suspension");
            var p = _bundle.ParamString("veh_preset", "");   // reflect the loaded preset in the dropdown (setting Selected doesn't re-fire ItemSelected)
            if (_vehPreset != null) { int idx = 0; for (int i = 1; i < _vehPreset.ItemCount; i++) if (_vehPreset.GetItemText(i) == p) { idx = i; break; } _vehPreset.Selected = idx; }
            var wm = _bundle.ParamString("veh_wheel", "");   // reflect the chosen wheel asset
            if (_vehWheel != null) { int idx = 0; for (int i = 1; i < _vehWheel.ItemCount; i++) if (_vehWheel.GetItemText(i) == wm) { idx = i; break; } _vehWheel.Selected = idx; }
            var stm = _bundle.ParamString("veh_steer_model", "");   // reflect the chosen steering-wheel model
            if (_vehSteerModel != null) { int idx = 0; for (int i = 1; i < _vehSteerModel.ItemCount; i++) if (_vehSteerModel.GetItemText(i) == stm) { idx = i; break; } _vehSteerModel.Selected = idx; }
            var sem = _bundle.ParamString("veh_seat_model", "");   // reflect the chosen seat model
            if (_vehSeatModel != null) { int idx = 0; for (int i = 1; i < _vehSeatModel.ItemCount; i++) if (_vehSeatModel.GetItemText(i) == sem) { idx = i; break; } _vehSeatModel.Selected = idx; }
        }

        static string[] HooksFor(string type) => type switch
        {
            "vehicle" => new[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR", "Seat_0", "Seat_1", "Steer", "Exit_0", "Exhaust", "Headlight_0", "Headlight_1", "Taillight_0", "Taillight_1", "Light_0" },
            "gun" => new[] { "Muzzle", "Sight", "Magazine", "Eject", "View", "Barrel", "Grip", "Tactical", "Aim" },
            "deployable" => new[] { "Storage", "Anchor", "Light_0", "Point_0" },
            _ => new[] { "Point_0", "Point_1", "Anchor" },
        };

        void RefreshList()
        {
            if (_listBox == null) return;
            foreach (var c in _listBox.GetChildren()) c.QueueFree();
            AddSection("PARTS", Kind.Part, System.Linq.Enumerable.Select(_bundle.Parts, p => p.Mesh ?? "?"));
            AddSection("COLLIDERS", Kind.Collider, System.Linq.Enumerable.Select(_bundle.Colliders, c => c.Shape));
            AddSection("VOLUMES", Kind.Volume, System.Linq.Enumerable.Select(_bundle.Volumes, v => v.Name));
            AddSection("POINTS", Kind.Point, System.Linq.Enumerable.Select(_bundle.Points, p => p.Name));
        }

        void AddSection(string head, Kind kind, IEnumerable<string> labels)
        {
            var list = new List<string>(labels);
            if (list.Count == 0) return;
            var h = new Label { Text = head }; h.AddThemeColorOverride("font_color", new Color(0.7f, 0.8f, 1f)); _listBox.AddChild(h);
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i; Kind k = kind;
                bool selected = _selKind == kind && _selIdx == i;
                var b = new Button { Text = (selected ? "▶ " : "   ") + list[i], Alignment = HorizontalAlignment.Left };
                b.Pressed += () => Select(k, idx);
                _listBox.AddChild(b);
            }
        }

        void BuildPicker(CanvasLayer layer)
        {
            _picker = new Panel { Position = new Vector2(324, 12), CustomMinimumSize = new Vector2(320, 580), Visible = false };
            layer.AddChild(_picker);
            var col = new VBoxContainer { CustomMinimumSize = new Vector2(320, 580) };
            _picker.AddChild(col);
            col.AddChild(new Label { Text = "click a mesh to preview  •  E (or Add) to place" });

            // 3D spinning preview (own-world SubViewport so it renders JUST the mesh, not the editor)
            _previewVp = new SubViewport { Size = new Vector2I(300, 210), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, OwnWorld3D = true };
            var pcam = new Camera3D { Position = new Vector3(0f, 0.55f, 2.3f), RotationDegrees = new Vector3(-13.4f, 0f, 0f), Current = true };   // aimed at origin (no LookAt — not in tree yet)
            _previewVp.AddChild(pcam);
            _previewVp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-42f, -32f, 0f), LightEnergy = 1.2f });
            _previewVp.AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.18f, 0.2f, 0.24f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.8f, 0.8f, 0.82f), AmbientLightEnergy = 0.8f } });
            _previewPivot = new Node3D();
            _previewVp.AddChild(_previewPivot);
            _previewMesh = new MeshInstance3D();
            _previewPivot.AddChild(_previewMesh);
            var vpc = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(300, 210) };
            vpc.AddChild(_previewVp);
            col.AddChild(vpc);

            var addBtn = new Button { Text = "Add (E)" };
            addBtn.Pressed += () => { if (_pickerName != null) AddPart(_pickerName); };
            col.AddChild(addBtn);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(300, 260) };
            var box = new VBoxContainer();
            scroll.AddChild(box);
            col.AddChild(scroll);
            foreach (var m in _meshNames)
            {
                string mm = m;
                var b = new Button { Text = m, Alignment = HorizontalAlignment.Left };
                b.Pressed += () => SetPickerMesh(mm);
                box.AddChild(b);
            }
            var close = new Button { Text = "close" }; close.Pressed += () => TogglePicker(false); col.AddChild(close);
        }

        void SetPickerMesh(string name)
        {
            _pickerName = name;
            if (_previewMesh == null) return;
            var mesh = ContentProvider.ParseObj($"res://content/{name}");
            if (mesh == null) { Status($"no mesh {name}"); return; }
            _previewMesh.Mesh = mesh;
            var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.9f };
            var alb = AssetBundle.ResolveAlbedo(name);
            var tex = alb != null ? LoadTex($"res://content/{alb}") : null;
            if (tex != null) mat.AlbedoTexture = tex; else mat.AlbedoColor = new Color(0.72f, 0.74f, 0.8f);
            _previewMesh.MaterialOverride = mat;
            var aabb = mesh.GetAabb();
            float r = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z));
            float sc = r > 0.001f ? 1.5f / r : 1f;
            _previewMesh.Scale = Vector3.One * sc;
            _previewMesh.Position = -aabb.GetCenter() * sc;
            Status($"preview: {name} — E to place");
        }

        public override void _Process(double delta)
        {
            if (_previewPivot != null && _picker != null && _picker.Visible) _previewPivot.RotateY((float)delta * 0.9f);
        }

        void TogglePicker(bool on) { if (_picker != null) _picker.Visible = on; if (on && _openPanel != null) _openPanel.Visible = false; }
        void Status(string s) { if (_status != null) _status.Text = s; GD.Print($"[assetfactory] {s}"); }

        // ---- viz builders ---------------------------------------------------
        static Node3D BoxViz(Color c, float[] pos, float[] rot, float[] size)
        {
            var mat = new StandardMaterial3D
            {
                AlbedoColor = c, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            return new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = mat,
                Position = AssetBundle.V3(pos), RotationDegrees = AssetBundle.V3(rot), Scale = AssetBundle.V3(size, Vector3.One),
            };
        }

        static Node3D PointViz(float[] pos, float[] rot)
        {
            var mat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.55f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            return new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = Vector3.One * 0.18f }, MaterialOverride = mat,
                Position = AssetBundle.V3(pos), RotationDegrees = AssetBundle.V3(rot),
            };
        }

        static MeshInstance3D Placeholder(AssetBundle.Part p) => new()
        {
            Name = "(missing)",
            Mesh = new BoxMesh { Size = Vector3.One * 0.4f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0f, 1f) },
            Transform = new Transform3D(AssetBundle.EulerDegBasis(p.Rot).Scaled(AssetBundle.V3(p.Scale, Vector3.One)), AssetBundle.V3(p.Pos)),
        };

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        static string[] ScanMeshes()
        {
            var list = new List<string>();
            foreach (var f in DirAccess.GetFilesAt("res://content/")) if (f.EndsWith(".txt")) list.Add(f);
            list.Sort();
            return list.ToArray();
        }

        static string SanitizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "new_asset";
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s.Trim().ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.ToString();
        }

        static Node3D BuildGroundGrid()
        {
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.47f, 0.52f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines, mat);
            const int N = 12;
            for (int i = -N; i <= N; i++)
            {
                im.SurfaceAddVertex(new Vector3(i, 0, -N)); im.SurfaceAddVertex(new Vector3(i, 0, N));
                im.SurfaceAddVertex(new Vector3(-N, 0, i)); im.SurfaceAddVertex(new Vector3(N, 0, i));
            }
            im.SurfaceEnd();
            var holder = new Node3D { Name = "Grid" };
            holder.AddChild(new MeshInstance3D { Mesh = im });
            return holder;
        }

        void SelfTest()
        {
            string mesh = System.Array.IndexOf(_meshNames, "axe_fire.txt") >= 0 ? "axe_fire.txt" : (_meshNames.Length > 0 ? _meshNames[0] : null);
            if (mesh == null) { GD.Print("[assetfactory] SELFTEST: no meshes"); return; }
            AddPart(mesh);
            if (_partNodes.Count > 0) { _partNodes[0].Position = new Vector3(1.2f, 0.5f, -0.3f); _partNodes[0].RotationDegrees = new Vector3(0, 45, 0); }
            Select(Kind.Part, 0);
            AddCollider();
            if (_colNodes.Count > 0) _colNodes[0].Scale = new Vector3(2f, 1f, 3f);
            Select(Kind.Collider, 0);
            AddPoint();
            _bundle.SetParam("surface", "wood");   // behaviour: impact-fx surface
            _bundle.SetParam("power_kind", "output"); _bundle.SetParam("power_watts", 1500f); _bundle.SetParam("power_label", "Main Output");   // behaviour: power out
            _bundle.SetParam("powered_light", true);   // behaviour: powered flag (light gated by power)
            _nameEdit.Text = "selftest_asset";
            Save();
            var r = AssetBundle.Load(_savePath);
            GD.Print(r != null
                ? $"[assetfactory] SELFTEST OK: {r.Name} type={r.Type} p={r.Parts.Count} c={r.Colliders.Count} v={r.Volumes.Count} pt={r.Points.Count} col0.size=({r.Colliders[0].Size[0]},{r.Colliders[0].Size[1]},{r.Colliders[0].Size[2]}) pt0={r.Points[0].Name} surface={r.ParamString("surface")} power={r.ParamString("power_kind")}/{r.ParamFloat("power_watts")}w"
                : "[assetfactory] SELFTEST FAIL");
        }
    }
}
