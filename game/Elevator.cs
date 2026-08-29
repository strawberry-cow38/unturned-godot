using Godot;

namespace UnturnedGodot
{
    /// <summary>An interactive elevator/lift built from the Elevator_0 prop. Look at it and press Interact (F) and the
    /// car rides between a bottom and a top stop; an AnimatableBody3D with SyncToPhysics carries whatever CharacterBody
    /// stands on it. Focus + F mirrors the TV/lamp fixtures: a HitMeta on the body so PlayerController's look-ray finds
    /// it, SetLookFocused draws the white look outline, Call() toggles the destination. Master 2026-08-29: "theres an
    /// elevator prop -- wire it to move up/down on an interaction."</summary>
    public partial class Elevator : AnimatableBody3D
    {
        public static readonly StringName HitMeta = "elevator_hit";   // the look-ray finds THIS Elevator off its own body's meta (mirrors TVDevice.HitMeta)
        public float[] Floors = { 0f, 4f, 8f };   // floor stop heights (metres above _baseY); a floor button calls GoToFloor(index)
        float TopFloor => Floors[Floors.Length - 1];
        int _curFloor;
        const float MoveSpeed = 1.6f; // vertical m/s -- an elevator, not a rocket
        float _baseY, _targetY;       // bottom-stop world Y, and where we're currently heading
        bool _up;                     // false = at/heading to bottom, true = top
        bool _latched;                // has _baseY been fixed to the spawn Y yet?
        MeshInstance3D _mi;
        public float BaseLift;        // raise the node this far (metres) so the stood-up car's base rests on the ground
        MeshInstance3D _topBox, _rope;   // cosmetic winch: gray box fixed at the top of travel + a black rope down to the car
        Vector3 _cableTopLocal;          // node-local point the rope attaches to (car top-centre)
        float _anchorY;                  // world Y the fixed top box hangs the rope from
        public bool AutoCycle;           // demo: auto-reverse at each stop after a dwell (for the ride video)
        public bool AutoFloors;          // demo: step through EVERY floor in sequence (bounce 0->top->0) for the multi-floor render
        int _seqDir = 1;                 // AutoFloors travel direction
        public float SpeedMul = 1f;      // demo: >1 speeds the car up (e.g. the fast up/down GIF)
        public float DwellTime = 1.5f;   // demo: dwell at each stop before reversing
        public float CarTopY;            // node-local Y of the car roof (a demo rider stands here)
        float _dwell = 1f;
        bool _measureFloor;   // diag (UG_ELEVMESHCOL): raycast the real mesh once to print the true interior-floor world Y
        enum DoorPhase { Idle, Closing, Moving, Opening }
        DoorPhase _phase = DoorPhase.Idle;   // Idle = parked with doors open; a call runs Closing -> Moving -> Opening -> Idle
        int _destFloor;                       // where a call is taking us (the car moves only once the doors are shut)
        float _door = 1f;                     // 0 = doors shut, 1 = doors open (start open at floor 0)
        bool _forceShut;                      // diag (UG_ELEVDOORSHUT): freeze the doors closed for a fit-check still
        MeshInstance3D _doorL, _doorR;        // the two center-opening panels (children -> ride with the car)
        const float DoorClosedZ = 0.95f, DoorOpenZ = 2.0f;   // right-panel |Z|: shut (meets centre) vs open (slid aside); left = -that
        const float DoorSpeed = 1.6f;         // door travel in _door units/sec (~0.6s to open or shut)

        /// <summary>Assemble the elevator from Elevator_0.obj (+ its vertex-colour texture), a box collider off the mesh
        /// AABB, and the HitMeta tag. The caller positions it; _Ready latches that as the bottom stop.</summary>
        public static Elevator Build()
        {
            var e = new Elevator { SyncToPhysics = true };   // move it in _PhysicsProcess -> riders on top are carried by the solver
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var mesh = ObjMesh.Load(dir + "Elevator_0.obj");
            if (mesh == null) { GD.Print("[elevator] no Elevator_0.obj mesh"); return e; }
            var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
            string tp = dir + "Elevator_0_tex.png";
            if (Godot.FileAccess.FileExists(tp)) { var img = Image.LoadFromFile(tp); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            // ORIENT: the extracted Elevator_0.obj is tipped (measured AABB 5.0 x 4.4 x 3.9). Master's steer: the
            // machinery housing that sat on the +Z 'front' face belongs on TOP -- RotX -90 rolls +Z up to +Y so the
            // motor/winch sits atop the ~5 x 4.4 m car. Master 2026-08-29: "machinery on the front should be on top".
            var standUp = new Basis(Vector3.Right, -Mathf.Pi * 0.5f);
            e._mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Basis = standUp };
            e.AddChild(e._mi);
            Aabb ab = new Transform3D(standUp, Vector3.Zero) * mesh.GetAabb();   // bounds AFTER standing up
            // HOLLOW collider: a thin FLOOR slab (the car IS a hollow mesh, not a facade -- the 360 showed a doorway +
            // interior). A rider stands INSIDE on this floor and rides with the car, instead of on the roof off a solid
            // box. Walls are left to the mesh for now (add wall colliders if players walk out). Master 2026-08-29.
            const float FloorT = 0.25f;   // collider TOP sits at the mesh's real interior floor (measured 0.25 above the AABB base via a trimesh raycast), so the rider + landings line up with the floor exactly
            e.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(ab.Size.X, FloorT, ab.Size.Z) },
                Position = new Vector3(ab.Position.X + ab.Size.X * 0.5f, ab.Position.Y + FloorT * 0.5f, ab.Position.Z + ab.Size.Z * 0.5f) });
            e.BaseLift = -ab.Position.Y;   // node Y needed to sit the car's base on the ground
            if (System.Environment.GetEnvironmentVariable("UG_ELEVMESHCOL") == "1")
            {   // diag: give the RAW mesh a trimesh collider on a private layer so a downward ray finds the TRUE interior-floor Y
                var mc = new StaticBody3D { CollisionLayer = 1u << 10, CollisionMask = 0 };
                mc.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape(), Basis = standUp });
                e.AddChild(mc);
                e._measureFloor = true;
            }
            e._cableTopLocal = new Vector3(ab.Position.X + ab.Size.X * 0.5f, ab.Position.Y + ab.Size.Y, ab.Position.Z + ab.Size.Z * 0.5f);   // top-centre of the car -> the rope hangs from here
            e.CarTopY = ab.Position.Y + ab.Size.Y;   // car roof height (node-local)
            // BUTTON PANEL on the +X back interior wall: a dark backing plate + one coloured floor button per stop,
            // stacked in a column, faces turned to the -X doorway. THIS is the interactable now, not the whole car
            // (master 2026-08-29). Parented to the car so the panel rides with it; ElevatorButton.Press -> GoToFloor.
            {
                float wallX = ab.Position.X + ab.Size.X;         // +X interior wall
                float cz = ab.Position.Z + ab.Size.Z * 0.5f;     // centred across the car
                int nf = e.Floors.Length;
                float baseY = ab.Position.Y + 0.9f, stepY = 0.52f;
                e.AddChild(new MeshInstance3D {
                    Mesh = new BoxMesh { Size = new Vector3(0.08f, stepY * nf + 0.3f, 0.62f) },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.12f), Roughness = 0.7f },
                    Position = new Vector3(wallX - 0.5f, baseY + stepY * (nf - 1) * 0.5f, cz),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                for (int f = 0; f < nf; f++)
                {
                    var col = f == 0 ? new Color(0.35f, 0.85f, 0.45f) : f == nf - 1 ? new Color(0.92f, 0.34f, 0.32f) : new Color(0.95f, 0.85f, 0.35f);
                    var btn = ElevatorButton.Make(e, f, col);
                    e.AddChild(btn);
                    btn.Position = new Vector3(wallX - 0.57f, baseY + f * stepY, cz);
                    btn.RotationDegrees = new Vector3(0f, 90f, 0f);   // turn the big button face to the -X doorway
                }
            }
            // SLIDING DOORS on the -X front: two center-opening panels covering the measured opening (Z +-1.85, Y
            // 0.30..3.85). Children -> they ride with the car; the phase machine shuts them before it moves + opens
            // them when it stops (master 2026-08-29 "double sliding door... closes before the elevator moves and opens
            // when it stops"). Each panel's |Z| lerps DoorClosedZ (meet at centre) -> DoorOpenZ (slid aside).
            {
                float doorX = ab.Position.X + 0.16f;   // just inside the -X face, sat in the frame
                float panelW = 1.95f, panelH = 2.48f, panelT = 0.12f, cy = 1.49f;   // W = half-opening; H fits the MEASURED door hole (Y 0.25..2.73, floor->lintel) so it seats seamlessly under the header
                var dmat = new StandardMaterial3D { AlbedoColor = new Color(0.60f, 0.62f, 0.66f), Metallic = 0.35f, Roughness = 0.45f };
                e._doorL = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(panelT, panelH, panelW) }, MaterialOverride = dmat, Position = new Vector3(doorX, cy, -DoorClosedZ) };
                e._doorR = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(panelT, panelH, panelW) }, MaterialOverride = dmat, Position = new Vector3(doorX, cy, DoorClosedZ) };
                e.AddChild(e._doorL); e.AddChild(e._doorR);
                if (System.Environment.GetEnvironmentVariable("UG_ELEVDOORSHUT") == "1") { e._door = 0f; e._forceShut = true; }   // diag: park closed for a fit still
            }
            e.SetMeta(HitMeta, e);   // the look-ray hits this body -> reads the meta -> this Elevator
            return e;
        }

        public override void _Ready() { LatchBase(); BuildCable(); UpdateDoors(); }

        // Fix the bottom stop to wherever we were spawned, the FIRST time we're readied OR called -- so it never matters
        // whether Position was set before AddChild or a Call arrives before _Ready ran.
        void LatchBase() { if (!_latched) { _baseY = GlobalPosition.Y; _targetY = _baseY; _latched = true; } }

        /// <summary>Demo up/down (AutoCycle): flip the destination between the bottom floor and the top floor.</summary>
        public void Call() { LatchBase(); _up = !_up; GoToFloor(_up ? Floors.Length - 1 : 0); }

        /// <summary>A floor button was pressed: shut the doors, ride to floor f (0 = bottom), reopen. Clamped to the list.</summary>
        public void GoToFloor(int f)
        {
            LatchBase();
            int nf = Mathf.Clamp(f, 0, Floors.Length - 1);
            if (nf == _curFloor && _phase == DoorPhase.Idle) return;   // already parked here, doors open
            _destFloor = nf;
            _phase = DoorPhase.Closing;   // shut the doors first; the car only moves once they're closed
        }

        // demo (AutoFloors): advance one floor, bouncing at the ends -> 0,1,2,1,0,1,2,...
        void StepFloor() { if (Floors.Length < 2) return; int n = _curFloor + _seqDir; if (n >= Floors.Length) { n = Floors.Length - 2; _seqDir = -1; } else if (n < 0) { n = 1; _seqDir = 1; } GoToFloor(n); }

        public override void _PhysicsProcess(double delta)
        {
            if (_forceShut) { _door = 0f; UpdateDoors(); return; }   // diag: hold closed
            float dt = (float)delta;
            switch (_phase)
            {
                case DoorPhase.Closing:   // shut the doors, THEN release the car to move
                    _door = Mathf.MoveToward(_door, 0f, DoorSpeed * dt);
                    if (_door <= 0.02f) { _curFloor = _destFloor; _targetY = _baseY + Floors[_destFloor]; _phase = DoorPhase.Moving; }
                    break;
                case DoorPhase.Moving:   // doors shut -> ride to the target floor
                {
                    var p = GlobalPosition;
                    float ny = Mathf.MoveToward(p.Y, _targetY, MoveSpeed * SpeedMul * dt);
                    GlobalPosition = new Vector3(p.X, ny, p.Z);
                    UpdateCable();
                    if (Mathf.Abs(ny - _targetY) < 0.0005f) _phase = DoorPhase.Opening;
                    break;
                }
                case DoorPhase.Opening:   // arrived -> open the doors
                    _door = Mathf.MoveToward(_door, 1f, DoorSpeed * dt);
                    if (_door >= 0.98f) _phase = DoorPhase.Idle;
                    break;
                default:   // Idle: parked with doors open -> one-shot measurement + demo stepping
                    if (_measureFloor) { _measureFloor = false; MeasureFloorAndDoor(); }
                    if (AutoFloors) { _dwell -= dt; if (_dwell <= 0f) { _dwell = DwellTime; StepFloor(); } }   // demo: dwell, then next floor
                    else if (AutoCycle) { _dwell -= dt; if (_dwell <= 0f) { _dwell = DwellTime; Call(); } }     // demo: dwell, then reverse
                    break;
            }
            UpdateDoors();
        }

        // Slide the two panels: |Z| lerps DoorClosedZ (meet at centre) -> DoorOpenZ (aside) by _door (0 shut, 1 open).
        void UpdateDoors()
        {
            if (_doorL == null) return;
            float z = Mathf.Lerp(DoorClosedZ, DoorOpenZ, _door);
            _doorL.Position = new Vector3(_doorL.Position.X, _doorL.Position.Y, -z);
            _doorR.Position = new Vector3(_doorR.Position.X, _doorR.Position.Y, z);
        }

        // diag (UG_ELEVMESHCOL): print the true interior-floor world Y + the door-frame opening bounds, once per park.
        void MeasureFloorAndDoor()
        {
            var p = GlobalPosition;
            var ss = GetWorld3D().DirectSpaceState;
            var q = PhysicsRayQueryParameters3D.Create(p + new Vector3(-0.05f, 2.0f, 0f), p + new Vector3(-0.05f, -0.5f, 0f));
            q.CollisionMask = 1u << 10;
            var hit = ss.IntersectRay(q);
            if (hit.Count > 0) { float fy = hit["position"].AsVector3().Y; GD.Print($"[elev-floor] floor {_curFloor}: elevator floor top world Y = {fy:0.000} (car base Y = {p.Y:0.000})"); }
            else GD.Print("[elev-floor] NO floor hit");
            float zmin = 9f, zmax = -9f;
            for (float z = -2.5f; z <= 2.5f; z += 0.05f) {
                var h = ss.IntersectRay(PhysicsRayQueryParameters3D.Create(p + new Vector3(-4f, 1.5f, z), p + new Vector3(4f, 1.5f, z), 1u << 10));
                if (h.Count > 0 && h["position"].AsVector3().X - p.X > -2f) { zmin = Mathf.Min(zmin, z); zmax = Mathf.Max(zmax, z); }
            }
            // DOOR opening HEIGHT: the CONTIGUOUS open span from the floor UP -- stops at the first solid (the lintel),
            // so a clerestory/interior-ceiling gap ABOVE the header doesn't inflate it (that bug made the doors too tall).
            float dBot = -1f, dTop = -1f;
            for (float y = 0.1f; y <= 4f; y += 0.025f) {
                var h = ss.IntersectRay(PhysicsRayQueryParameters3D.Create(p + new Vector3(-4f, y, 0f), p + new Vector3(4f, y, 0f), 1u << 10));
                bool open = h.Count > 0 && h["position"].AsVector3().X - p.X > -2f;
                if (open) { if (dBot < 0f) dBot = y; dTop = y; }
                else if (dBot >= 0f) break;   // was open, now solid => that's the lintel; the door opening ends here
            }
            GD.Print($"[elev-door] opening Z [{zmin:0.00}, {zmax:0.00}] (w {zmax - zmin:0.00}); door Y [{dBot:0.00}, {dTop:0.00}] (h {dTop - dBot:0.00}) contiguous floor->lintel");
        }

        // COSMETIC winch (master 2026-08-29 "add a black rope with a gray box at the top of the lift's travel. purely
        // cosmetic."): a gray housing fixed at the top of travel + a black rope from it down to the car's top. Both are
        // TopLevel so they DON'T ride with the car; the rope is restretched between the (moving) car top and the (fixed)
        // box each physics tick, so it spools shorter as the car rises. No collision, no function.
        void BuildCable()
        {
            if (_topBox != null) return;
            float carTopUp = _baseY + TopFloor + _cableTopLocal.Y;   // world Y of the car's top when at the top floor
            _anchorY = carTopUp + 0.5f;
            _topBox = new MeshInstance3D {
                Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.9f, 1.3f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.57f), Roughness = 0.9f },
                TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_topBox);
            _topBox.GlobalPosition = new Vector3(GlobalPosition.X + _cableTopLocal.X, _anchorY + 0.45f, GlobalPosition.Z + _cableTopLocal.Z);
            _rope = new MeshInstance3D {
                Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 1f, RadialSegments = 6 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.Black, Roughness = 1f },
                TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_rope);
            UpdateCable();
        }

        void UpdateCable()
        {
            if (_rope == null) return;
            Vector3 top = GlobalPosition + _cableTopLocal;   // node isn't rotated (only its mesh child is), so a plain add gives the world car-top
            float len = Mathf.Max(0.02f, _anchorY - top.Y);
            _rope.GlobalPosition = new Vector3(top.X, (top.Y + _anchorY) * 0.5f, top.Z);
            _rope.Scale = new Vector3(1f, len, 1f);   // CylinderMesh height 1 -> Y-scale = rope length
        }

        /// <summary>Whole-car white look outline on gain, same affordance the TV/lamp/monitor use (an interaction with
        /// no outline reads as scenery). Toggles the OutlineOverlay mask layer on the mesh.</summary>
        public void SetLookFocused(bool on)
        {
            if (_mi == null || !IsInstanceValid(_mi)) return;
            _mi.Layers = on ? (_mi.Layers | OutlineOverlay.OutlineLayer) : (_mi.Layers & ~OutlineOverlay.OutlineLayer);
        }
    }
}
