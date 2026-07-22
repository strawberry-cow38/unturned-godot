using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // First-person player: ported PlayerMovementSim on Godot's 50 Hz physics tick + mouse look + a hitscan
    // gun (raycast from the camera vs the zombie collision layer). Movement CONSTANTS are exact; feel goes
    // through Jolt. Builds its own camera + capsule collider so it can be spawned from code.
    // WASD move / Shift sprint / Ctrl crouch / Z prone / Space jump / LMB fire / G melee / H grenade / R reload / Esc release mouse.
    public partial class PlayerController : CharacterBody3D
    {
        readonly PlayerMovementSim _move = new PlayerMovementSim();
        readonly PlayerStanceSim _stance = new PlayerStanceSim();   // intertwined stance state machine (X = crouch, Z = prone), extracted to the engine-free sim-core (MP_PLAN §3.4)
        CapsuleShape3D _capsule; CollisionShape3D _hitbox; float _capStance = -1f;   // hitbox capsule, resized per stance (source HeightForStance)
        Camera3D _cam;
        Vector3 _interpPrev, _interpCurr; bool _interpReady;   // render interpolation: smooth the VISUAL position between the 50Hz physics ticks (master); rotation stays per-frame so the mouse is instant
        Viewmodel _viewmodel;
        public PlayerInventory Inventory;   // the ported 9-page inventory model
        InventoryUI _invUI;                 // the dashboard (Tab to open)
        CraftingUI _craftUI;                // the crafting menu (K to open)
        SkillsUI _skillsUI;                 // the skills menu (J to open) -- spend XP to level skills
        BuildTool _build;                   // B = build mode (grid-snapped structures)
        string _gunName = "eaglefire";   // gun folder name (eaglefire | maplestrike), derived from the .dat path
        float _pitchDeg;
        Vehicle _driving; bool _fp = true;   // vehicle being driven + camera mode: true = 1st person (spawn default, strawberry), false = 3rd; H toggles (on foot + driving)
        float _driveCamYaw, _driveCamPitch = 15f;   // 3rd-person driving orbit: mouse yaws/pitches the chase cam around the car (master)
        // FP RIDE free-look (#37, MP only): mouse yaw/pitch of the view in VEHICLE-LOCAL space while seated on a
        // puppet in first person (real Unturned lets you look around while driving; the fixed forward gaze made the
        // default MP ride cam feel stuck). At (0, FpRideGazePitchDeg) this reproduces the SP fixed gaze exactly --
        // the old LookingAt(eyeL + (0,-0.6,-3.9)) target = atan(-0.6/3.9) below the vehicle's forward.
        float _rideLookYaw, _rideLookPitch = FpRideGazePitchDeg;
        const float FpRideGazePitchDeg = -8.75f;
        readonly bool _ugFp = System.Environment.GetEnvironmentVariable("UG_FP") == "1";   // render harness: force 1st-person to screenshot the FP viewmodel
        RiggedCharacter _body;        // live 3rd-person player model (RiggedCharacter), visible when !_fp
        PlayerClothingController _clothing;   // P4 equip->visual wiring (drives shirt/pants paint + gear bone-attach off the worn slots)
        // Damage feedback, both source-exact and fired from TakeDamage: the red hurt flash (PlayerUI.painAlpha) and the
        // camera flinch (PlayerLook.flinchLocalRotation, an angular kick perpendicular to the hit that decays to level).
        public float PainAlpha;                     // PlayerUI.pain: red overlay alpha, set on hit, fades at 1/s
        Quaternion _flinch = Quaternion.Identity;   // PlayerLook.flinchLocalRotation: camera kick, recovers at 4/s

        [Export] public float MouseSensitivity = 0.12f;
        public int Ammo = 30;
        public int Kills { get; private set; }

        // Vitals live in the engine-free PlayerVitalsSim (MP_PLAN §3.4 sim-core: one per player, steppable
        // headless on the server). The shell exposes them through properties so every existing reader/writer
        // (HUD, DevConsole, Consume, tests) keeps its exact surface.
        readonly PlayerVitalsSim _vitals = new PlayerVitalsSim();
        public float Health { get => _vitals.Health; set => _vitals.Health = value; }
        public float MaxHealth { get => _vitals.MaxHealth; set => _vitals.MaxHealth = value; }
        public int Deaths;
        public bool Bleeding;      // HUD status indicator: set briefly after taking a hit (PlayerLifeUI's bleedingBox)
        double _bleedTimer;
        public bool Broken;        // PlayerLife.isBroken: broken legs (from a hard fall) -- blocks sprint + jump until mended
        // Survival vitals (0..1), shown live on the HUD. Rates are config-driven in Unturned (modeConfigData); these
        // are sensible stand-ins: stamina drains while sprinting + regens otherwise; food/water slowly decay; health
        // regenerates while fed + hydrated (PlayerLife gates regen on food/water) or bleeds while starved/dehydrated.
        public float Stamina { get => _vitals.Stamina; set => _vitals.Stamina = value; }
        public float Food { get => _vitals.Food; set => _vitals.Food = value; }
        public float Water { get => _vitals.Water; set => _vitals.Water = value; }
        public static bool SurvivalDrain = false;   // hunger/thirst drain OFF by default; F1 console `survival on|off` toggles it (strawberry)
        public float Infection { get => _vitals.Infection; set => _vitals.Infection = value; }   // 0..1 virus; zombie bites raise it (Zombie.askDamage's player.life.askInfect(b/3))
        public void Infect(float amount) => Infection = Mathf.Clamp(Infection + amount * Skills.ImmunityInfectionMultiplier(), 0f, 1f);   // IMMUNITY skill cuts infection gained (source UseableConsumeable:325)

        // Use a consumable (ItemConsumeableAsset): apply its Health/Food/Water/bleeding effects to the vitals.
        public void Consume(ItemAsset a)
        {
            if (a == null) return;
            if (a.useHealth > 0) Health = Mathf.Min(MaxHealth, Health + a.useHealth);
            if (a.useFood  > 0) Food  = Mathf.Min(1f, Food  + a.useFood  / 100f);
            if (a.useWater > 0) Water = Mathf.Min(1f, Water + a.useWater / 100f);
            if (a.useEnergy > 0) Stamina = Mathf.Min(1f, Stamina + a.useEnergy / 100f);   // askRest: energy drinks/bars restore stamina
            if (a.useVirus > 0) Infect(a.useVirus / 100f);   // askInfect: raises infection (IMMUNITY skill cuts it, via Infect)
            if (a.useDisinfectant > 0) Infection = Mathf.Max(0f, Infection - a.useDisinfectant / 100f);   // askDisinfect: antibiotics/vaccine lower infection
            if (a.useStopsBleeding) { Bleeding = false; _bleedTimer = 0; }
            if (a.useHealBroken) Broken = false;   // Bones_Modifier Heal (Medkit/Splint) mends broken legs
        }

        // Drop an item into the world at pos, grounded by a downward cast (ItemManager.dropItem: snap to ground +
        // a small +-0.125 spread). Spawns a WorldItem you can walk back over and pick up.
        // aim point for the F1 dev console -- the look-orb: camera ray forward to the first hit (world/vehicles/props) or max reach.
        public Vector3 LookPoint()
        {
            if (_cam == null) return GlobalPosition - GlobalTransform.Basis.Z * 3f;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
            var rq = PhysicsRayQueryParameters3D.Create(from, from + fwd * LookReach);
            rq.CollisionMask = (1u << 0) | (1u << 5) | (1u << 6);   // world + vehicles + props
            rq.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(rq);
            return hit.Count > 0 ? (Vector3)hit["position"] : from + fwd * LookReach;
        }

        // teleport (F1 console): move the VEHICLE if driving (the player rides attached to it), else the player. Zero velocity so it doesn't launch.
        public void TeleportTo(Vector3 pos)
        {
            if (_driving != null) { _driving.GlobalPosition = pos; _driving.LinearVelocity = Vector3.Zero; _driving.AngularVelocity = Vector3.Zero; }
            else
            {
                GlobalPosition = pos; Velocity = Vector3.Zero;
                _interpPrev = _interpCurr = pos;   // MUST reset the render-interp snapshots too — otherwise the next 50Hz tick does `GlobalPosition = _interpCurr` and snaps us right back to the old spot (the "gave feedback but didn't tp" bug; master was on foot, not driving)
            }
        }

        // Map arrow (M map): radians for a 2D arrow that points up=north at 0, turning clockwise. Source sets
        // localPlayerImage.RotationAngle = player yaw; we take the look/camera forward on the XZ plane. Godot 2D
        // rotation is clockwise-positive, so an up-pointing arrow rotates by atan2(fx, -fz).
        public float MapFacingAngle()
        {
            Vector3 f = _cam != null ? -_cam.GlobalTransform.Basis.Z : -GlobalTransform.Basis.Z;
            return Mathf.Atan2(f.X, -f.Z);
        }

        public void DropWorldItem(Item item, Vector3 pos)
        {
            var space = GetWorld3D().DirectSpaceState;
            var q = PhysicsRayQueryParameters3D.Create(pos + Vector3.Up, pos + Vector3.Down * 2048f);
            q.CollisionMask = 1u << 0;   // ground
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(q);
            if (hit.Count > 0) pos = (Vector3)hit["position"] + Vector3.Up * 0.25f;   // drop from just ABOVE the surface, not on it -> the collider doesn't start buried in the trimesh
            pos += new Vector3(_rng.RandfRange(-0.125f, 0.125f), 0f, _rng.RandfRange(-0.125f, 0.125f));
            WorldItem.Spawn(GetParent(), item, pos);
        }

        WorldItem _focusItem;   // the dropped item the player is currently LOOKING AT (glowing + named), pickup target for E
        ShelfItemBody _focusShelfItem;   // the SHELF display item being looked at (glowing, F to grab straight off the shelf)
        StoreShelf _focusShelf;          // the shelf being looked at (whole-shelf outline) -- the shelf of the focused item
        Vehicle _focusVehicle;  // the vehicle the player is LOOKING AT (outlined + info panel), enter target for E
        Deployable _focusDeployable;  // the placed deployable (generator) the player is LOOKING AT (outlined + HP/fuel billboard)
        GasPump _focusGasPump;        // the gas pump being LOOKED AT (outline + fuel tooltip; RMB w/ a gas can extracts)
        GridPowerSource _focusGrid;   // the grid-power box being LOOKED AT (outline + "Grid Power - <name>: <watts>" tooltip)
        SDG.Unturned.Item _heldFuelItem;  // a gas can equipped in hand -> RMB a powered pump to fill it (master's fluids)
        Deployable _fHeldDeploy;      // the deployable F is being HELD on (hold-F = pick it up; a quick tap = toggle, on release)
        float _deployPickupTimer;     // seconds F has been held on _fHeldDeploy
        const float DeployPickupTime = 1.0f;    // hold F this long over a deployable to pick it back up (wires disconnect)
        const float PickupBarDeadzone = 0.2f;   // hide the progress bar for the first 20% of the hold, so a quick tap-to-toggle doesn't flash it
        IPuppetFocusable _focusPuppet;  // MP ONLY: the replicated car/item PUPPET being looked at (client-side outline). SP has none.
        Vector3 _lookEnd;       // where the eye-ray ends (the look sphere sits here)
        MeshInstance3D _lookViz; // O-toggle visualizer of that ONE look sphere
        MeshInstance3D _lookHullViz; ImmediateMesh _lookHullMesh; bool _showLookHulls;   // I-toggle wireframe of every vehicle's look-focus hulls (culled behind-cam / past LookHullVizRange for fps)
        const float LookHullVizRange = 70f;        // don't draw hull wireframes for vehicles farther than this from the camera (fps)
        PhysicsRayQueryParameters3D _lookRayQ;     // reused across frames (no per-frame alloc)
        PhysicsShapeQueryParameters3D _lookSphereQ;
        Godot.Collections.Array<Rid> _lookExclude;

        // Look-at interaction (master): cast the eye-ray from the camera forward, up to ~3.5 m, against item interaction
        // spheres (bit 8) AND world geometry (bit 0). The CLOSEST hit wins -> a wall between you and the item blocks it
        // (LOS-correct). The hit item gets a rarity glow outline + name billboard; a different/no item clears the old.
        const float LookReach = 2.6f, LookSphereR = 0.16f;   // the eye-ray reaches this far, ending in a sphere of this radius (master shrank it by half)

        void UpdateLookFocus()
        {
            WorldItem hitItem = null; Vehicle hitVeh = null; Deployable hitDeploy = null; GasPump hitGasPump = null; GridPowerSource hitGrid = null;
            ShelfItemBody hitShelfItem = null; StoreShelf hitShelf = null;   // shelf display item / its shelf under the look-sphere
            IPuppetFocusable hitPuppet = null;   // MP ONLY: nearest replicated car/item puppet under the look-sphere (SP hits real Vehicle/WorldItem instead)
            if (!_dead && _driving == null && _riding == null && _cam != null && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                var space = GetWorld3D().DirectSpaceState;
                Vector3 from = _cam.GlobalPosition;
                Vector3 fwd = -_cam.GlobalTransform.Basis.Z;
                // 1) ray forward -> the sphere sits where the ray STOPS (on world/props/items/vehicles, or max reach).
                // Query objects are REUSED across frames (they were alloc'd fresh every frame -> GC pressure = the "dips") -- master.
                _lookExclude ??= new Godot.Collections.Array<Rid> { GetRid() };
                _lookRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = (1u << 0) | (1u << 5) | (1u << 6) | (1u << 7) | StoreShelf.ShelfItemHitLayer, Exclude = _lookExclude };
                _lookRayQ.From = from; _lookRayQ.To = from + fwd * LookReach;
                var rhit = space.IntersectRay(_lookRayQ);
                _lookEnd = rhit.Count > 0 ? (Vector3)rhit["position"] : from + fwd * LookReach;
                // a placed deployable (generator) stops the ray on the world layer -> focus it directly from the ray hit
                // (LOS-correct: a wall in the way stops the ray first). The LookReach IS the look-at radius.
                if (rhit.Count > 0)
                {
                    var rcol = rhit["collider"].As<GodotObject>();
                    if (rcol is Deployable dep && IsInstanceValid(dep)) hitDeploy = dep;
                    else if (rcol is Node grn && grn.HasMeta("gaspump") && grn.GetMeta("gaspump").As<GasPump>() is GasPump gpn && IsInstanceValid(gpn)) hitGasPump = gpn;   // gas pump collider tagged in WorldBuilder -> the fixture
                    else if (rcol is Node grn2 && grn2.HasMeta("gridpower") && grn2.GetMeta("gridpower").As<GridPowerSource>() is GridPowerSource gsn && IsInstanceValid(gsn)) hitGrid = gsn;   // grid-power box collider tagged in SpawnEditorGridPower
                    else if (rcol is ShelfItemBody sibr && IsInstanceValid(sibr)) hitShelfItem = sibr;   // ray hit an item on a shelf directly -> lock onto it (the orb is a backup)
                    else if (rcol is Node rn && ShelfOf(rn) is StoreShelf rshelf) hitShelf = rshelf;   // looked-at shelf -> whole-shelf outline + F-open (look-based, not proximity)
                }
                // 2) sphere at the ray end -> nearest ITEM (bit 7) or VEHICLE (bit 5) it overlaps is focusable
                _lookSphereQ ??= new PhysicsShapeQueryParameters3D { Shape = new SphereShape3D { Radius = LookSphereR }, CollisionMask = WorldItem.ItemHitLayer | (1u << 5) | StoreShelf.ShelfItemHitLayer, Exclude = _lookExclude };
                _lookSphereQ.Transform = new Transform3D(Basis.Identity, _lookEnd);
                float bestI = float.MaxValue, bestV = float.MaxValue, bestP = float.MaxValue, bestSI = float.MaxValue;
                foreach (var h in space.IntersectShape(_lookSphereQ, 8))
                {
                    var c = h["collider"].As<GodotObject>();
                    if (c is WorldItem wi && IsInstanceValid(wi))
                    {
                        float d = wi.GlobalPosition.DistanceSquaredTo(_lookEnd);
                        if (d < bestI) { bestI = d; hitItem = wi; }
                    }
                    else if (c is Vehicle v && IsInstanceValid(v))   // alive car (F to enter) OR a wreck (blowtorch salvage) -- both focusable (master)
                    {
                        float d = v.GlobalPosition.DistanceSquaredTo(_lookEnd);
                        if (d < bestV) { bestV = d; hitVeh = v; }
                    }
                    else if (c is ShelfItemBody sib && IsInstanceValid(sib))   // an item sitting on a shelf -> grab it straight off (F). Outline the ITEM only, not its whole shelf.
                    {
                        float d = sib.GlobalPosition.DistanceSquaredTo(_lookEnd);
                        if (d < bestSI) { bestSI = d; hitShelfItem = sib; }
                    }
                    // MP: the hit collider is a puppet's detection body (bit 5 car / bit 7 item); its parent is the
                    // IPuppetFocusable render node. SP never reaches this branch (real Vehicle/WorldItem matched above).
                    else if (c is Node body && body.GetParent() is Node3D pn && IsInstanceValid(pn) && pn is IPuppetFocusable pf)
                    {
                        float d = pn.GlobalPosition.DistanceSquaredTo(_lookEnd);
                        if (d < bestP) { bestP = d; hitPuppet = pf; }
                    }
                }
                if (hitItem != null && hitVeh != null) { if (bestV < bestI) hitItem = null; else hitVeh = null; }   // focus the nearer of the two
                if (hitShelfItem != null) hitShelf = null;   // looking at an ITEM on the shelf -> outline the item only, not the whole shelf
                if (hitVeh == null && hitItem == null)   // seats/steering seen through windows have no collider -> focus a car whose visual bounds the look-ray passes through (master). DISTANCE-CULLED so it isn't O(all vehicles) every frame (perf regression fix).
                {
                    float maxD = (LookReach + 6f) * (LookReach + 6f);
                    foreach (var node in GetTree().GetNodesInGroup("vehicles"))
                        if (node is Vehicle vv && IsInstanceValid(vv))
                        {
                            float d = vv.GlobalPosition.DistanceSquaredTo(from);
                            if (d < maxD && d < bestV && vv.LookRayHitsHull(from, _lookEnd)) { bestV = d; hitVeh = vv; }   // cheap distance gate before the tight per-hull (oriented-box) test -- no world-AABB bloat / cross-vehicle overlap (strawberry)
                        }
                }
            }
            if (_lookViz != null) { _lookViz.Visible = WorldItem.ShowLookSphere && !_dead && _driving == null; if (_lookViz.Visible) _lookViz.GlobalPosition = _lookEnd; }
            if (hitItem != _focusItem)
            {
                if (IsInstanceValid(_focusItem)) _focusItem.SetFocused(false);
                _focusItem = hitItem;
                _focusItem?.SetFocused(true);
            }
            if (hitVeh != _focusVehicle)
            {
                if (IsInstanceValid(_focusVehicle)) _focusVehicle.SetLookFocused(false);
                _focusVehicle = hitVeh;
                _focusVehicle?.SetLookFocused(true);
            }
            if (hitDeploy != _focusDeployable)
            {
                if (IsInstanceValid(_focusDeployable)) _focusDeployable.SetLookFocused(false);
                _focusDeployable = hitDeploy;
                _focusDeployable?.SetLookFocused(true);
            }
            if (hitGasPump != _focusGasPump)   // looked-at gas pump: outline + fuel tooltip
            {
                if (IsInstanceValid(_focusGasPump)) _focusGasPump.SetLookFocused(false);
                _focusGasPump = hitGasPump;
                _focusGasPump?.SetLookFocused(true);
            }
            if (hitGrid != _focusGrid)   // looked-at grid-power box: outline + "Grid Power - <name>: <watts>" tooltip
            {
                if (IsInstanceValid(_focusGrid)) _focusGrid.SetLookFocused(false);
                _focusGrid = hitGrid;
                _focusGrid?.SetLookFocused(true);
            }
            if (hitShelfItem != _focusShelfItem)   // looked-at shelf item glows (F grabs it)
            {
                if (IsInstanceValid(_focusShelfItem)) _focusShelfItem.SetFocused(false);
                _focusShelfItem = hitShelfItem;
                _focusShelfItem?.SetFocused(true);
            }
            if (hitShelf != _focusShelf)           // and its shelf gets the whole-shelf outline
            {
                if (IsInstanceValid(_focusShelf)) _focusShelf.SetShelfFocused(false);
                _focusShelf = hitShelf;
                _focusShelf?.SetShelfFocused(true);
            }
            // MP puppet outline: clears when hitPuppet is null (guarded look-block sets it null -> outline drops on death/ride too).
            if (!ReferenceEquals(hitPuppet, _focusPuppet))
            {
                if (_focusPuppet is Node3D op && IsInstanceValid(op)) _focusPuppet.SetLookFocused(false);
                _focusPuppet = hitPuppet;
                _focusPuppet?.SetLookFocused(true);
            }
        }

        // I-toggle debug overlay: draw a line-wireframe of every vehicle's look-focus HULLS (the oriented boxes the
        // focus test now uses) so their size can be eyeballed. Rebuilt each frame from the live transforms. CULLED for
        // fps: skip vehicles past LookHullVizRange or behind the camera; the focused vehicle's hulls draw cyan. (strawberry)
        static readonly Vector3[] _boxCorners = {   // unit-cube corners in [-0.5,0.5]
            new(-0.5f,-0.5f,-0.5f), new(0.5f,-0.5f,-0.5f), new(0.5f,-0.5f,0.5f), new(-0.5f,-0.5f,0.5f),
            new(-0.5f, 0.5f,-0.5f), new(0.5f, 0.5f,-0.5f), new(0.5f, 0.5f,0.5f), new(-0.5f, 0.5f,0.5f) };
        static readonly int[] _boxEdges = { 0,1,1,2,2,3,3,0, 4,5,5,6,6,7,7,4, 0,4,1,5,2,6,3,7 };   // 12 edges (24 endpoints)
        readonly System.Collections.Generic.List<(Vector3 p, Color c)> _hullVerts = new();
        void UpdateLookHullViz()
        {
            _lookHullMesh.ClearSurfaces();
            if (_cam == null) return;
            Vector3 camPos = _cam.GlobalPosition, camFwd = -_cam.GlobalTransform.Basis.Z;
            float range2 = LookHullVizRange * LookHullVizRange;
            _hullVerts.Clear();
            foreach (var node in GetTree().GetNodesInGroup("vehicles"))
            {
                if (node is not Vehicle v || !IsInstanceValid(v)) continue;
                Vector3 to = v.GlobalPosition;
                if (camPos.DistanceSquaredTo(to) > range2) continue;             // past the viz radius -> skip (fps)
                if ((to - camPos).Dot(camFwd) < -8f) continue;                    // well behind the camera -> skip (small margin so edge boxes don't pop)
                Color col = v == _focusVehicle ? new Color(0.2f, 0.9f, 1f) : new Color(0.2f, 1f, 0.4f);   // focused = cyan, else green
                foreach (var (xf, size) in v.LookHullBoxes())
                    for (int e = 0; e < _boxEdges.Length; e++)
                        _hullVerts.Add((xf * (_boxCorners[_boxEdges[e]] * size), col));
            }
            if (_hullVerts.Count == 0) return;                                    // ImmediateMesh errors on an empty surface -> emit nothing
            _lookHullMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            foreach (var (p, c) in _hullVerts) { _lookHullMesh.SurfaceSetColor(c); _lookHullMesh.SurfaceAddVertex(p); }
            _lookHullMesh.SurfaceEnd();
        }

        // --- Wire tool: look at a connection cube (highlight + info, phase 2) and build wires (select/route/place, phase 3). ---
        const float WireReach = 5.5f, WirePlaceReach = 6f;   // look-at reach for cubes / place reach for node points
        const int MaxWireNodes = 20; const float MaxWireLen = 40f;   // limits (strawberry)
        ConnectionPort _wirePort;       // the connection cube currently looked at
        bool _wiring; ConnectionPort _wireSrc;
        readonly System.Collections.Generic.List<Vector3> _wireNodes = new();   // placed node points (world) between the source and the free end
        Wire _wirePreview;              // the live wire being routed
        PhysicsRayQueryParameters3D _wireRayQ, _wirePlaceRayQ;
        CanvasLayer _wireHudLayer; Label _wireHudLabel;
        // manage a wire by poking its CONNECTION POINT (not the wire) while not routing: hold RMB to clear it, tap to unplug + re-route
        ConnectionPort _clearPort;   // the wired port an RMB hold is acting on -- ARMED by the mouse-press event, so a press that began while routing (cancel/undo) never leaks in
        float _wireClearHold;
        const float WireClearTime = 1.0f;   // hold RMB this long over a wired port to clear its wire
        const float WireClickMax = 0.28f;   // release within this = a tap -> unplug (longer, released early = an aborted clear -> nothing)
        bool _wireArrowsOn;   // in/out port arrows currently shown (only while the wire tool is out)
        public ConnectionPort WireLookPort => _wirePort;

        void UpdateWireLook()
        {
            if (!HoldingWireTool) { if (_wiring) CancelWire(); if (IsInstanceValid(_wirePort)) _wirePort.SetHighlight(ConnectionPort.PortHi.None); _wirePort = null; WireHudSet(null); return; }
            // the connection cube currently aimed at
            ConnectionPort port = null;
            if (_cam != null && !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                var space = GetWorld3D().DirectSpaceState;
                Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
                _wireRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = ConnectionPort.PortLayer };
                _wireRayQ.From = from; _wireRayQ.To = from + fwd * WireReach;
                var hit = space.IntersectRay(_wireRayQ);
                if (hit.Count > 0 && hit["collider"].As<GodotObject>() is ConnectionPort cp && IsInstanceValid(cp)) port = cp;
            }
            if (port != _wirePort) { if (IsInstanceValid(_wirePort)) _wirePort.SetHighlight(ConnectionPort.PortHi.None); _wirePort = port; }

            if (_wiring)
            {
                if (!IsInstanceValid(_wireSrc)) { CancelWire(); WireHudSet(null); return; }   // source deployable gone -> drop the wire
                bool snapEnd = CanCompleteWire(_wireSrc, _wirePort);   // snap to the compatible opposite-role port (an already-wired / burning / same-device port won't accept it)
                if (IsInstanceValid(_wirePort))   // colour the hovered target: green = can complete, red = occupied/incompatible (master); the source stays a neutral focus
                    _wirePort.SetHighlight(_wirePort == _wireSrc ? ConnectionPort.PortHi.Focus : (snapEnd ? ConnectionPort.PortHi.WireOk : ConnectionPort.PortHi.WireBad));
                Vector3 end = snapEnd ? _wirePort.GlobalPosition : WirePlacePoint();
                var pts = new System.Collections.Generic.List<Vector3> { _wireSrc.GlobalPosition };
                pts.AddRange(_wireNodes); pts.Add(end);
                float len = PolyLen(pts);
                bool overLimit = _wireNodes.Count >= MaxWireNodes || len > MaxWireLen;
                _wirePreview?.SetPoints(pts, valid: !overLimit);   // over-limit paints RED even when snapping -> completion is blocked too
                WireHudSet($"nodes {_wireNodes.Count}/{MaxWireNodes}    {len:0.0}/{MaxWireLen:0}m" + (overLimit ? "   -- LIMIT" : ""));
            }
            else
            {
                if (IsInstanceValid(_wirePort)) _wirePort.SetHighlight(ConnectionPort.PortHi.Focus);   // just looking -> a little brighter (master)
                WireHudSet(_wirePort == null ? null : _wirePort.InfoLine() + (PortWired(_wirePort) ? "   ([RMB] hold: clear · tap: unplug)" : ""));
            }
        }

        // is this port already an endpoint of a committed wire? (max 1 wire per connection point -- strawberry)
        bool PortWired(ConnectionPort p)
        {
            if (p == null) return false;
            foreach (var n in GetTree().GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w) && (w.Source == p || w.Consumer == p)) return true;
            return false;
        }
        // a SOURCE end: an output, or a passthrough re-exporting its leftover (daisy-chaining the next spotlight)
        static bool IsSourcePort(ConnectionPort p) => p != null && (p.Kind == DeployableDef.PortKind.Output || p.Kind == DeployableDef.PortKind.Passthrough);
        // a CONSUMER end: a device input (a spotlight's usage, a splitter's relay input)
        static bool IsConsumerPort(ConnectionPort p) => p != null && p.Kind == DeployableDef.PortKind.Consumer;
        // a wire has one SOURCE end + one CONSUMER end; you can start routing from EITHER (strawberry). Can `target`
        // complete a wire started at `start`? -> opposite roles, usable, unwired, on a different deployable.
        bool CanCompleteWire(ConnectionPort start, ConnectionPort target) =>
            start != null && target != null && target.Usable && target.Owner != start.Owner && !PortWired(target)
            && (IsSourcePort(start) ? IsConsumerPort(target) : IsSourcePort(target));
        // order the two picked ends into (source, consumer) for the power graph, regardless of which you started from
        static (ConnectionPort src, ConnectionPort cons) OrderWireEnds(ConnectionPort a, ConnectionPort b) => IsSourcePort(a) ? (a, b) : (b, a);

        // LMB with the wire tool: pick a SOURCE (output/passthrough) to start, place a node while routing, or complete on a CONSUMER.
        void WireLmb()
        {
            if (_dead) return;   // no wiring from the death cam
            if (!_wiring)
            {
                // start from EITHER end -- a source (output/passthrough) OR a consumer input (strawberry: wire from the input side too)
                if ((IsSourcePort(_wirePort) || IsConsumerPort(_wirePort)) && _wirePort.Usable && !PortWired(_wirePort))   // 1 wire/port + not on a burning/wrecked deployable
                {
                    _wiring = true; _wireSrc = _wirePort; _wireNodes.Clear();
                    _wirePreview = new Wire(); GetParent().AddChild(_wirePreview);
                    GD.Print($"[wire] started from {_wirePort.InfoLine()}");
                }
                return;
            }
            if (CanCompleteWire(_wireSrc, _wirePort))
            {   // complete on the compatible opposite-role port -- but only if the finished wire is within the same 20-node/40m budget as node placement
                var cpts = new System.Collections.Generic.List<Vector3> { _wireSrc.GlobalPosition }; cpts.AddRange(_wireNodes); cpts.Add(_wirePort.GlobalPosition);
                if (_wireNodes.Count <= MaxWireNodes && PolyLen(cpts) <= MaxWireLen) CompleteWire(_wirePort);
                return;
            }
            Vector3 lp = WirePlacePoint();
            var pts = new System.Collections.Generic.List<Vector3> { _wireSrc.GlobalPosition }; pts.AddRange(_wireNodes); pts.Add(lp);
            if (_wireNodes.Count >= MaxWireNodes || PolyLen(pts) > MaxWireLen) return;   // hitting the limit blocks placing (strawberry)
            _wireNodes.Add(lp);
        }

        // RMB with the wire tool while routing: undo the last node, or cancel+delete the wire if none placed yet.
        void WireRmb()
        {
            if (!_wiring) return;   // phase 5 (a completed wire) is armed via WireManageArm off the press event, not here
            if (_dead || _wireNodes.Count == 0) CancelWire();
            else _wireNodes.RemoveAt(_wireNodes.Count - 1);
        }

        // --- Tow rope tool (item 64, strawberry 2026-07-19): tie a hemp rope from one vehicle's REAR node to another's
        // FRONT node, exactly like wiring two ports. LMB (looking at a rear node) starts; LMB (looking at a front node of
        // a DIFFERENT car) completes -> Vehicle.AttachTow applies the spring pull. RMB cancels a pending tie, or unties a
        // roped car you're looking at. Node picking is analytic (aim ray vs the two world tow points) -- no port colliders. ---
        const float RopeReach = 6f;          // how far you can aim at a tow node
        const float RopePickRadius = 0.7f;   // aim within this of a node (perpendicular) to select it
        bool _roping;                        // mid-tie: a rear source node is picked, waiting for a front dest
        ITowNode _ropeSrc;                   // the tower whose rear node we started from (a Vehicle in SP, a VehiclePuppet on a joined client)
        ITowNode _towClearVeh;               // RMB-armed roped vehicle under the crosshair: hold to clear the tow rope, tap to disconnect that side (mirrors the wire tool's _clearPort)
        float _towClearHold;                 // seconds the RMB clear has been held
        CanvasLayer _ropeHudLayer; Label _ropeHudLabel;   // the rope tool's own centred HUD (separate from the wire's so neither clobbers the other)
        TowRope _ropePreview;                // the live rope being tied (follows the aim)
        ITowNode _ropeLookVeh; bool _ropeLookRear;   // the tow node currently aimed at (null = none)
        bool _ropeNubsOn;                    // are all vehicles' tow nubs currently shown (rope tool out)?

        // B11: which group the rope tool scans. A JOINED client (NetAttachTow wired) scans VehiclePuppet nodes
        // -- its real cars are RemoveFromGroup("vehicles")'d, so the pre-fix "vehicles"-only scan found NOTHING
        // and a joiner couldn't tie. The SP/loopback host (seam null) keeps scanning real "vehicles" + attaches
        // directly. Both node kinds are ITowNode, so the pick/highlight/preview code is one path either way.
        string TowScanGroup() => NetAttachTow != null ? "vehicle_puppets" : "vehicles";

        void SetAllTowNubs(bool on)
        {
            foreach (var n in GetTree().GetNodesInGroup(TowScanGroup()))
                if (n is ITowNode v && IsInstanceValid(n)) v.SetTowNodesVisible(on);
            _ropeNubsOn = on;
        }

        // Per-frame while the rope tool is out: toggle the nubs, pick the aimed tow node, drive the tie preview.
        void UpdateRopeLook()
        {
            if (!HoldingRopeTool)
            {
                if (_ropeNubsOn) SetAllTowNubs(false);
                if (_roping) CancelRope();
                if (_ropeLookVeh != null) { if (TowValid(_ropeLookVeh)) _ropeLookVeh.SetTowNubHighlighted(_ropeLookRear, false); _ropeLookVeh = null; }
                _towClearVeh = null; _towClearHold = 0f; RopeHudSet(null);   // rope put away -> drop any armed clear + hide the rope HUD
                return;
            }
            if (!_ropeNubsOn) SetAllTowNubs(true);

            ITowNode bestVeh = null; bool bestRear = false;
            if (_cam != null && !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured)
                bestVeh = PickTowNode(_cam.GlobalPosition, -_cam.GlobalTransform.Basis.Z, out bestRear);

            if (!ReferenceEquals(bestVeh, _ropeLookVeh) || bestRear != _ropeLookRear)
            {
                if (_ropeLookVeh != null && TowValid(_ropeLookVeh)) _ropeLookVeh.SetTowNubHighlighted(_ropeLookRear, false);
                _ropeLookVeh = bestVeh; _ropeLookRear = bestRear;
                _ropeLookVeh?.SetTowNubHighlighted(_ropeLookRear, true);
            }

            if (_roping)
            {
                if (!TowValid(_ropeSrc)) { CancelRope(); return; }
                bool onDest = _ropeLookVeh != null && !_ropeLookRear && !ReferenceEquals(_ropeLookVeh, _ropeSrc);
                Vector3 a = _ropeSrc.RearTowWorld;
                Vector3 b = onDest ? _ropeLookVeh.FrontTowWorld : (_cam.GlobalPosition + (-_cam.GlobalTransform.Basis.Z) * RopeReach);
                _ropePreview?.SetEndpoints(a, b, Vehicle.TowRestMin, valid: onDest);
            }

            // HUD (mirrors the wire tool): while tying, the live rope length vs the max reach; on a roped node the RMB
            // manage hint; on an open rear node the tie hint. Skipped while a clear is armed -- UpdateRopeManage owns it then.
            if (_towClearVeh == null)
            {
                if (_roping && TowValid(_ropeSrc) && _cam != null)
                {
                    bool onDest2 = _ropeLookVeh != null && !_ropeLookRear && !ReferenceEquals(_ropeLookVeh, _ropeSrc) && !_ropeLookVeh.TowRoped;
                    Vector3 tip = onDest2 ? _ropeLookVeh.FrontTowWorld : (_cam.GlobalPosition + (-_cam.GlobalTransform.Basis.Z) * RopeReach);
                    float gap = _ropeSrc.RearTowWorld.DistanceTo(tip);
                    RopeHudSet($"tow rope   {gap:0.0}/{Vehicle.TowAttachReach:0.0}m" + (gap > Vehicle.TowAttachReach ? "   -- TOO FAR" : (onDest2 ? "   [LMB] tie" : "")));
                }
                else if (_ropeLookVeh != null && _ropeLookVeh.TowRoped) RopeHudSet("[RMB]  hold: clear rope  ·  tap: disconnect");
                else if (_ropeLookVeh != null && _ropeLookRear && !_ropeLookVeh.TowRoped) RopeHudSet("[LMB] start tow");
                else RopeHudSet(null);
            }
        }

        static bool TowValid(ITowNode t) => t is GodotObject go && GodotObject.IsInstanceValid(go);

        // B11: the best tow node under the aim ray, from the ACTIVE tow group (VehiclePuppets on a joined client,
        // real Vehicles on the SP/loopback host). Analytic pick (aim ray vs the two world tow points), no
        // colliders. Public so the L1 scan test can drive it with a synthetic aim (no camera needed).
        public ITowNode PickTowNode(Vector3 from, Vector3 fwd, out bool rear)
        {
            rear = false;
            ITowNode best = null; float bestPerp = RopePickRadius;
            foreach (var n in GetTree().GetNodesInGroup(TowScanGroup()))
            {
                if (n is not ITowNode v || !IsInstanceValid(n) || !v.TowScannable) continue;
                ConsiderTowNode(v, true,  v.RearTowWorld,  from, fwd, ref best, ref rear, ref bestPerp);
                ConsiderTowNode(v, false, v.FrontTowWorld, from, fwd, ref best, ref rear, ref bestPerp);
            }
            return best;
        }

        void ConsiderTowNode(ITowNode v, bool rear, Vector3 p, Vector3 from, Vector3 fwd, ref ITowNode bestVeh, ref bool bestRear, ref float bestPerp)
        {
            float t = (p - from).Dot(fwd);
            if (t < 0f || t > RopeReach) return;   // behind the camera or out of reach
            float perp = (p - (from + fwd * t)).Length();
            if (perp < bestPerp) { bestPerp = perp; bestVeh = v; bestRear = rear; }
        }

        // LMB with the rope tool: start from a REAR node, or complete on a FRONT node of a different car.
        void RopeLmb()
        {
            if (_dead) return;
            if (!_roping)
            {
                if (_ropeLookVeh != null && _ropeLookRear && !_ropeLookVeh.TowRoped)
                {
                    _roping = true; _ropeSrc = _ropeLookVeh;
                    _ropePreview = new TowRope(); GetParent().AddChild(_ropePreview);
                    GD.Print("[rope] tow started (rear)");
                }
                return;
            }
            if (!TowValid(_ropeSrc)) { CancelRope(); return; }   // source car despawned mid-tie -> drop the pending rope
            if (_ropeLookVeh != null && !_ropeLookRear && !ReferenceEquals(_ropeLookVeh, _ropeSrc))
            {
                // B11: a joined client (NetAttachTow wired) sends the tie as an INTENT by NetId -- the server
                // validates + attaches the REAL nodes, and the committed rope renders only when A6's replicated
                // TowedNetId echoes back (never mutate tow state client-side). The SP/loopback host attaches
                // its own real Vehicle nodes directly (the seam is null, so no double-attach).
                if (NetAttachTow != null) NetAttachTow(_ropeSrc.TowNetId, _ropeLookVeh.TowNetId);
                // review #5: mirror the server OnAttachTow not-remote-driven guard (VehicleNetSync:91) on the direct
                // loopback-host path too -- never rope a vehicle a REMOTE client is actively driving (NetDriverId != 0
                // = a remote holds the seat; a held/client-auth body must not become a rope end).
                else if (_ropeSrc is Vehicle towerV && _ropeLookVeh is Vehicle towedV
                         && towerV.NetDriverId == 0 && towedV.NetDriverId == 0 && towerV.AttachTow(towedV)) GD.Print("[rope] towing");
                CancelRope();
            }
        }

        // RMB PRESS with the rope tool while NOT tying: arm a clear/disconnect on the roped vehicle under the crosshair
        // (mirrors the wire tool's WireManageArm). Arming off the press edge keeps a routing-cancel press from managing.
        void RopeManageArm()
        {
            if (_dead || _driving != null || !HoldingRopeTool || Input.MouseMode != Input.MouseModeEnum.Captured) return;
            if (CanManageTow(_ropeLookVeh)) { _towClearVeh = _ropeLookVeh; _towClearHold = 0f; }
        }

        // SP knows a node is roped (real Vehicle.TowRoped); a joined client's puppet keeps it loose (always false), so on
        // the wire we allow managing ANY aimed node -- the server drops the rope on either end or no-ops (like the old untie).
        bool CanManageTow(ITowNode v) => v != null && TowValid(v) && (NetDetachTow != null || v.TowRoped);

        // Per-frame: an armed RMB hold on a roped tow node -- held to WireClearTime CLEARS the tow rope, released quickly
        // (<= WireClickMax) DISCONNECTS that side. One rope per car end, so both untie the single rope; the hold is the
        // deliberate clear (with a % readout), the tap the quick disconnect. Mirrors UpdateWireManage (master tow UX 2026-07-20).
        void UpdateRopeManage(float delta)
        {
            if (_towClearVeh == null) return;
            bool active = HoldingRopeTool && !_roping && !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured;
            if (!active || !ReferenceEquals(_ropeLookVeh, _towClearVeh) || !CanManageTow(_towClearVeh)) { _towClearVeh = null; _towClearHold = 0f; RopeHudSet(null); return; }
            if (Input.IsMouseButtonPressed(MouseButton.Right))
            {
                _towClearHold += delta;
                if (_towClearHold >= WireClearTime) { DoDetachTow(_towClearVeh); _towClearVeh = null; _towClearHold = 0f; RopeHudSet(null); return; }   // held long enough -> clear
                RopeHudSet($"clearing tow rope... {Mathf.Clamp((int)(_towClearHold / WireClearTime * 100f), 0, 99)}%");
            }
            else { if (_towClearHold <= WireClickMax) DoDetachTow(_towClearVeh); _towClearVeh = null; _towClearHold = 0f; RopeHudSet(null); }   // released quick -> tap-disconnect
        }

        // Untie a tow rope on the vehicle you're looking at: a joined client sends the intent by NetId (B11, the server
        // drops the rope on either end, no-ops if there's none), the SP/loopback host unties its real node directly.
        void DoDetachTow(ITowNode veh)
        {
            if (veh == null || !TowValid(veh)) return;
            if (NetDetachTow != null) NetDetachTow(veh.TowNetId);
            else if (veh.TowRoped && veh is Vehicle rv) rv.DetachTow();
        }

        // The rope tool's centred HUD (mirrors WireHudSet): live rope length vs max reach, or the RMB manage hint. Its
        // OWN label so it never clobbers the wire tool's HUD (the two tools are mutually exclusive but share the screen slot).
        void RopeHudSet(string text)
        {
            if (string.IsNullOrEmpty(text)) { if (_ropeHudLabel != null) _ropeHudLabel.Visible = false; return; }
            if (_ropeHudLabel == null)
            {
                _ropeHudLayer = new CanvasLayer { Layer = 40 }; AddChild(_ropeHudLayer);
                _ropeHudLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
                _ropeHudLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
                _ropeHudLabel.AnchorLeft = 0.5f; _ropeHudLabel.AnchorRight = 0.5f; _ropeHudLabel.OffsetTop = 90f; _ropeHudLabel.OffsetLeft = -300f; _ropeHudLabel.OffsetRight = 300f;
                _ropeHudLabel.AddThemeFontSizeOverride("font_size", 26);
                _ropeHudLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
                _ropeHudLabel.AddThemeConstantOverride("outline_size", 6);
                _ropeHudLayer.AddChild(_ropeHudLabel);
            }
            _ropeHudLabel.Text = text; _ropeHudLabel.Visible = true;
        }

        void CancelRope()
        {
            _roping = false; _ropeSrc = null;
            if (_ropePreview != null && IsInstanceValid(_ropePreview)) _ropePreview.QueueFree();
            _ropePreview = null;
        }

        void CompleteWire(ConnectionPort target)
        {
            if (_wirePreview == null || !IsInstanceValid(_wireSrc)) { CancelWire(); return; }
            var (src, cons) = OrderWireEnds(_wireSrc, target);   // you may have started from the consumer end -> order into (source, consumer)
            if (RequestConnectWire(src, cons))
            {   // MP: the link is a REQUEST -- drop the local preview; the committed wire renders when
                // WireConnected echoes through the replica view (server wires are 2-point, nodes are SP cosmetics)
                GD.Print($"[wire] connect requested {src.ProviderName} -> {cons.ProviderName} (wire)");
                CancelWire();
                return;
            }
            var pts = new System.Collections.Generic.List<Vector3> { _wireSrc.GlobalPosition };
            pts.AddRange(_wireNodes); pts.Add(target.GlobalPosition);   // the preview polyline follows the ROUTE you drew (visual); the graph endpoints are ordered source->consumer
            _wirePreview.Source = src; _wirePreview.Consumer = cons;
            _wirePreview.SetPoints(pts, valid: true);
            _wirePreview.AddToGroup("wires");
            PowerNet.MarkDirty();   // a new wire changes the graph
            GD.Print($"[wire] connected {src.ProviderName} -> {cons.ProviderName} ({_wireNodes.Count} nodes)");
            _wirePreview = null; _wiring = false; _wireSrc = null; _wireNodes.Clear();
        }

        void CancelWire()
        {
            _wirePreview?.QueueFree(); _wirePreview = null;
            _wiring = false; _wireSrc = null; _wireNodes.Clear();
        }

        // --- Hose tool (item 66): connect a fluid Source port -> a Consumer port. Mirror of the wire tool, LEANER first
        // pass -- a STRAIGHT hose (no multi-node routing / clear-hold yet). Type-lock ("cannot mix fluids") is enforced
        // at completion (HoseCompletion, a pure testable predicate); gravity gates whether the finished hose actually
        // FLOWS (FluidNet). The look-ray hits HosePort.PortLayer (1<<11) only, so it never picks a power port. ---
        const float HoseReach = 5.5f;
        HosePort _hosePort;          // the fluid port currently looked at
        bool _hosing; HosePort _hoseSrc;   // mid-route: a start port is picked, waiting for the opposite-role end
        Hose _hosePreview;           // the live hose being routed (follows the look point)
        PhysicsRayQueryParameters3D _hoseRayQ;
        CanvasLayer _hoseHudLayer; Label _hoseHudLabel;
        public HosePort HoseLookPort => _hosePort;   // L1 probe

        // scene wrapper over the engine-free FluidHoseRule for the two live ports (the type-lock rule is L0-tested in core)
        HoseVerdict CompletionVerdict(HosePort start, HosePort target)
        {
            if (!IsInstanceValid(start) || !IsInstanceValid(target) || !target.Usable) return HoseVerdict.None;
            var st = start.Owner?.Tank != null ? start.Owner.Tank.Type : FluidType.None;
            var tt = target.Owner?.Tank != null ? target.Owner.Tank.Type : FluidType.None;
            return FluidHoseRule.Completion(start.Kind, target.Kind,
                st == FluidType.None, tt == FluidType.None, st == tt,
                ReferenceEquals(start.Owner, target.Owner), PortHosed(target));
        }

        // is this fluid port already an endpoint of a committed hose? (max 1 hose per port, lean pass)
        bool PortHosed(HosePort p)
        {
            if (p?.Node == null) return false;
            foreach (var n in GetTree().GetNodesInGroup("hoses"))
                if (n is Hose h && GodotObject.IsInstanceValid(h) && (h.Source == p.Node || h.Consumer == p.Node)) return true;
            return false;
        }

        // Per-frame while the hose tool is out: pick the aimed HosePort (highlight + info), drive the route preview.
        void UpdateHoseLook()
        {
            if (!HoldingHoseTool)
            {
                if (_hosing) CancelHose();
                if (IsInstanceValid(_hosePort)) _hosePort.SetHighlight(HosePort.PortHi.None);
                _hosePort = null; HoseHudSet(null); return;
            }
            HosePort port = null;
            if (_cam != null && !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                var space = GetWorld3D().DirectSpaceState;
                Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
                _hoseRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = HosePort.PortLayer };
                _hoseRayQ.From = from; _hoseRayQ.To = from + fwd * HoseReach;
                var hit = space.IntersectRay(_hoseRayQ);
                if (hit.Count > 0 && hit["collider"].As<GodotObject>() is HosePort hp && IsInstanceValid(hp)) port = hp;
            }
            if (port != _hosePort)
            {
                if (IsInstanceValid(_hosePort) && _hosePort != _hoseSrc) _hosePort.SetHighlight(HosePort.PortHi.None);
                _hosePort = port;
            }

            if (_hosing)
            {
                if (!IsInstanceValid(_hoseSrc)) { CancelHose(); HoseHudSet(null); return; }
                var v = CompletionVerdict(_hoseSrc, _hosePort);
                if (IsInstanceValid(_hosePort) && _hosePort != _hoseSrc)
                    _hosePort.SetHighlight(v == HoseVerdict.Ok ? HosePort.PortHi.HoseOk : HosePort.PortHi.HoseBad);
                Vector3 end = v == HoseVerdict.Ok ? _hosePort.GlobalPosition : HoseFreeEnd();
                _hosePreview?.SetPoints(new System.Collections.Generic.List<Vector3> { _hoseSrc.GlobalPosition, end }, valid: v != HoseVerdict.Mismatch);
                HoseHudSet(v == HoseVerdict.Mismatch ? "cannot mix fluids" : "hose   [LMB] connect  ·  [RMB] cancel");
            }
            else
            {
                if (IsInstanceValid(_hosePort)) _hosePort.SetHighlight(HosePort.PortHi.Focus);
                HoseHudSet(_hosePort == null ? null : _hosePort.InfoLine());
            }
        }

        Vector3 HoseFreeEnd()   // the free end while routing = your look point at max reach (a straight hose, no world snap yet)
        {
            if (_cam == null) return GlobalPosition;
            Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
            return from + fwd * HoseReach;
        }

        // LMB with the hose tool: start from a usable, unhosed port (either role), or complete on a compatible opposite port.
        void HoseLmb()
        {
            if (_dead) return;
            if (!_hosing)
            {
                if (IsInstanceValid(_hosePort) && _hosePort.Usable && !PortHosed(_hosePort))
                {
                    _hosing = true; _hoseSrc = _hosePort;
                    _hoseSrc.SetHighlight(HosePort.PortHi.Focus);
                    _hosePreview = new Hose(); GetParent().AddChild(_hosePreview);   // preview: null endpoints -> FluidNet skips it until committed
                    GD.Print($"[hose] started from {_hosePort.InfoLine()}");
                }
                return;
            }
            if (CompletionVerdict(_hoseSrc, _hosePort) == HoseVerdict.Ok) CompleteHose(_hosePort);
            // a Mismatch/None target does nothing on LMB (no node routing in the lean pass)
        }

        void CompleteHose(HosePort target)
        {
            if (_hosePreview == null || !IsInstanceValid(_hoseSrc)) { CancelHose(); return; }
            var (srcPort, consPort) = _hoseSrc.Kind == FluidPortKind.Source ? (_hoseSrc, target) : (target, _hoseSrc);
            AdoptFluidType(srcPort.Owner, consPort.Owner);   // an empty tank adopts the other's fluid (strawberry)
            _hosePreview.Source = srcPort.Node; _hosePreview.Consumer = consPort.Node;
            _hosePreview.SetPoints(new System.Collections.Generic.List<Vector3> { srcPort.GlobalPosition, consPort.GlobalPosition }, valid: true);
            if (!_hosePreview.IsInGroup("hoses")) _hosePreview.AddToGroup("hoses");
            if (IsInstanceValid(_hoseSrc)) _hoseSrc.SetHighlight(HosePort.PortHi.None);
            if (IsInstanceValid(target)) target.SetHighlight(HosePort.PortHi.None);
            GD.Print($"[hose] connected {srcPort.Owner?.Role} -> {consPort.Owner?.Role}");
            _hosePreview = null; _hosing = false; _hoseSrc = null;
        }

        void CancelHose()
        {
            _hosePreview?.QueueFree(); _hosePreview = null;
            if (IsInstanceValid(_hoseSrc)) _hoseSrc.SetHighlight(HosePort.PortHi.None);
            _hosing = false; _hoseSrc = null;
        }

        // an empty (None) container adopts the other end's fluid type on connect; two set types were already type-locked equal
        static void AdoptFluidType(FluidContainer a, FluidContainer b)
        {
            if (a?.Tank == null || b?.Tank == null) return;
            if (a.Tank.Type == FluidType.None && b.Tank.Type != FluidType.None) a.Tank.Type = b.Tank.Type;
            else if (b.Tank.Type == FluidType.None && a.Tank.Type != FluidType.None) b.Tank.Type = a.Tank.Type;
        }

        void HoseHudSet(string text)
        {
            if (string.IsNullOrEmpty(text)) { if (_hoseHudLabel != null) _hoseHudLabel.Visible = false; return; }
            if (_hoseHudLabel == null)
            {
                _hoseHudLayer = new CanvasLayer { Layer = 40 }; AddChild(_hoseHudLayer);
                _hoseHudLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
                _hoseHudLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
                _hoseHudLabel.AnchorLeft = 0.5f; _hoseHudLabel.AnchorRight = 0.5f; _hoseHudLabel.OffsetTop = 120f; _hoseHudLabel.OffsetLeft = -300f; _hoseHudLabel.OffsetRight = 300f;
                _hoseHudLabel.AddThemeFontSizeOverride("font_size", 26);
                _hoseHudLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
                _hoseHudLabel.AddThemeConstantOverride("outline_size", 6);
                _hoseHudLayer.AddChild(_hoseHudLabel);
            }
            _hoseHudLabel.Text = text; _hoseHudLabel.Visible = true;
        }

        // Manage a wire by poking its CONNECTION POINT (the wire itself is non-interactive). While the tool is out and
        // NOT routing, look at a wired port: hold RMB -> clear the whole wire (progress readout); tap RMB -> unplug it
        // (pick it back up for re-routing from its source). RMB while routing stays undo (WireRmb, event-driven).
        Wire WireOnPort(ConnectionPort p)   // the committed wire plugged into this port (either endpoint), or null
        {
            if (p == null) return null;
            foreach (var n in GetTree().GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w) && (w.Source == p || w.Consumer == p)) return w;
            return null;
        }

        // RMB PRESS with the wire tool while NOT routing: arm a clear/unplug on the wired port under the crosshair.
        // Arming off the press EDGE means a press that began during routing (undo/cancel) can't become a manage action.
        void WireManageArm()
        {
            if (_dead || _driving != null || !HoldingWireTool || Input.MouseMode != Input.MouseModeEnum.Captured) return;
            if (WireOnPort(_wirePort) != null) { _clearPort = _wirePort; _wireClearHold = 0f; }
        }

        // Per-frame: drive an ARMED RMB hold on a wired port -- held to WireClearTime clears the wire; released quickly
        // (<= WireClickMax) unplugs it; released mid-hold does nothing.
        void UpdateWireManage(float delta)
        {
            if (_clearPort == null) return;
            bool active = HoldingWireTool && !_wiring && !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured;
            Wire w = WireOnPort(_clearPort);
            if (!active || _wirePort != _clearPort || w == null) { _clearPort = null; _wireClearHold = 0f; return; }   // looked away / state changed / wire gone -> abort
            if (Input.IsMouseButtonPressed(MouseButton.Right))
            {
                _wireClearHold += delta;
                if (_wireClearHold >= WireClearTime)   // held long enough -> clear the whole wire
                {
                    if (!RequestRemoveWire(w))   // MP: a replicated wire clears server-side; WireRemoved echoes the teardown
                    { w.RemoveFromGroup("wires"); w.QueueFree(); PowerNet.MarkDirty(); }   // drop the group THIS frame so power + PortWired update immediately
                    _clearPort = null; _wireClearHold = 0f; WireHudSet(null); return;
                }
                WireHudSet($"clearing wire... {Mathf.Clamp((int)(_wireClearHold / WireClearTime * 100f), 0, 99)}%");
            }
            // released quick -> tap-unplug. MP: an unplug is a plain removal request (server wires keep no
            // routed nodes to pick back up) -- re-route fresh once the removal echoes.
            else { if (_wireClearHold <= WireClickMax && !RequestRemoveWire(w)) UnplugWire(w); _clearPort = null; _wireClearHold = 0f; }
        }

        // Unplug a wire: drop its consumer link + leave the "wires" group, and pick it back up as a routing preview from
        // its source (all node points kept), so poking either endpoint re-picks-up the wire to re-route.
        void UnplugWire(Wire wire)
        {
            if (wire == null || !IsInstanceValid(wire) || !IsInstanceValid(wire.Source)) { wire?.QueueFree(); return; }
            _wireSrc = wire.Source;
            _wireNodes.Clear();
            for (int i = 1; i < wire.Points.Count - 1; i++) _wireNodes.Add(wire.Points[i]);   // keep the node points; drop source[0] + consumer[last]
            wire.Consumer = null;
            wire.RemoveFromGroup("wires"); PowerNet.MarkDirty();   // stop delivering power immediately
            _wirePreview = wire; _wiring = true;
            GD.Print($"[wire] unplugged -> routing from source with {_wireNodes.Count} kept nodes");
        }

        // In/out arrows on every connection point while the wire tool is out: blue where you can wire, red where the
        // port is occupied or on a wrecked deployable (the placement-ghost arrows are handled by DeployablePlacer).
        void UpdateWireArrows()
        {
            bool show = HoldingWireTool && !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured;
            if (!show)
            {
                if (_wireArrowsOn) { foreach (var n in GetTree().GetNodesInGroup("ports")) if (n is ConnectionPort p && IsInstanceValid(p)) p.SetArrowState(false, false); _wireArrowsOn = false; }
                return;
            }
            _wireArrowsOn = true;
            foreach (var n in GetTree().GetNodesInGroup("ports"))
                if (n is ConnectionPort p && IsInstanceValid(p))
                    p.SetArrowState(true, p.Usable && !PortWired(p));
        }

        Vector3 WirePlacePoint()   // the free end / node drop = your look point (raycast to world/props), else max reach
        {
            if (_cam == null) return GlobalPosition;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
            _wirePlaceRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = (1u << 0) | (1u << 6) };
            _wirePlaceRayQ.From = from; _wirePlaceRayQ.To = from + fwd * WirePlaceReach;
            // the wire routes STRAIGHT THROUGH deployables (strawberry) -- exclude the player + every deployable body so the
            // free end lands on the ground/structure behind them instead of sticking to a generator/splitter/box face.
            var exclude = new Godot.Collections.Array<Rid> { GetRid() };
            foreach (var n in GetTree().GetNodesInGroup("deployables"))
                if (n is Deployable dep && GodotObject.IsInstanceValid(dep)) exclude.Add(dep.GetRid());
            _wirePlaceRayQ.Exclude = exclude;
            var hit = space.IntersectRay(_wirePlaceRayQ);
            return hit.Count > 0 ? (Vector3)hit["position"] : from + fwd * WirePlaceReach;
        }

        static float PolyLen(System.Collections.Generic.List<Vector3> pts) { float s = 0f; for (int i = 0; i + 1 < pts.Count; i++) s += pts[i].DistanceTo(pts[i + 1]); return s; }

        void WireHudSet(string text)
        {
            if (string.IsNullOrEmpty(text)) { if (_wireHudLabel != null) _wireHudLabel.Visible = false; return; }
            if (_wireHudLabel == null)
            {
                _wireHudLayer = new CanvasLayer { Layer = 40 }; AddChild(_wireHudLayer);
                _wireHudLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
                _wireHudLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
                _wireHudLabel.AnchorLeft = 0.5f; _wireHudLabel.AnchorRight = 0.5f; _wireHudLabel.OffsetTop = 90f; _wireHudLabel.OffsetLeft = -300f; _wireHudLabel.OffsetRight = 300f;
                _wireHudLabel.AddThemeFontSizeOverride("font_size", 26);
                _wireHudLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
                _wireHudLabel.AddThemeConstantOverride("outline_size", 6);
                _wireHudLayer.AddChild(_wireHudLabel);
            }
            _wireHudLabel.Text = text; _wireHudLabel.Visible = true;
        }

        // Hold F over a placed deployable to pick it back up into the bag (master): its wires disconnect. A quick TAP
        // instead toggles a generator (handled on F release). Wrecks/burning ones are blowtorch-salvaged, not picked up.
        void UpdateDeployPickup(float delta)
        {
            if (_fHeldDeploy == null) return;
            bool fHeld = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsPhysicalKeyPressed(Key.F);
            if (!fHeld || !IsInstanceValid(_fHeldDeploy) || _fHeldDeploy != _focusDeployable
                || _fHeldDeploy.IsWreck || _fHeldDeploy.OnFire || _dead || _driving != null)
            {   // released, looked away, or it can't be picked up -> cancel the hold
                if (IsInstanceValid(_fHeldDeploy)) _fHeldDeploy.PickupProgress = 0f;
                _fHeldDeploy = null; _deployPickupTimer = 0f;
                return;
            }
            _deployPickupTimer += delta;
            float frac = Mathf.Clamp(_deployPickupTimer / DeployPickupTime, 0f, 1f);
            _fHeldDeploy.PickupProgress = frac >= PickupBarDeadzone ? frac : 0f;   // deadzone: no bar for the first 20% -> a quick tap-to-toggle shows nothing
            if (_deployPickupTimer >= DeployPickupTime)
            {
                var d = _fHeldDeploy;
                _fHeldDeploy = null; _deployPickupTimer = 0f; d.PickupProgress = 0f;
                PickupDeployable(d);
            }
        }

        // Return a live placed deployable to the bag: disconnect its wires + despawn, grant the item back (dropped at its
        // feet if the bag is full). A REPLICATED (MP) node (NetId!=0) routes the pickup as an intent over the wire
        // (B2): the server tears it down + hands the item back through the owner-inventory echo, and the
        // DeployableReplicaView retires the node off EventDeployableRemoved -- send-and-return, no local mutation
        // (the replica view stays the SOLE node owner). SP/local nodes (NetId==0) take the direct path below.
        internal void PickupDeployable(Deployable d)
        {
            if (d == null || !IsInstanceValid(d) || d.IsWreck || d.OnFire) return;
            if (d.NetId != 0) { NetPickupDeployable?.Invoke(d.NetId); return; }
            ushort id = d.Def?.Id ?? 0;
            string name = d.Def?.Name;
            Vector3 pos = d.GlobalPosition;
            var item = id != 0 ? SDG.Unturned.Assets.makeLoot(id) : null;
            if (item != null)   // stamp the current HP (quality %) + fuel onto the item so re-placing restores them
            {
                if (d.HealthMax > 0f) item.quality = (byte)Mathf.Clamp(Mathf.RoundToInt(d.Health / d.HealthMax * 100f), 1, 100);
                if (d.FuelMax > 0f) item.fuelLevel = d.Fuel;
            }
            d.Pickup();   // frees any wires plugged into it + despawns
            if (item != null)
            {
                bool handsFree = Unarmed;
                if (!(Inventory?.tryAddItem(item) ?? false)) DropWorldItem(item, pos + Vector3.Up * 1f);   // bag full -> drop where it stood
                else { _invUI?.Refresh(); if (handsFree) EquipItemAsset(item.GetAsset(), item); }   // hands free -> hold it (a deployable re-enters placement mode)
            }
            GD.Print($"[deploy] picked up #{id} ({name})");
        }

        // Wreck salvage (master): a focused wreck shows a state prompt -- red "Too hot" while burning, red "Requires blowtorch"
        // if you have none, white "Hold LMB to salvage" with a blowtorch equipped. Holding LMB breaks it into scrap + despawns it.
        void UpdateSalvage(float delta)
        {
            var v = (_focusVehicle != null && IsInstanceValid(_focusVehicle)) ? _focusVehicle : null;
            bool lmb = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Left) && !_dead && _driving == null && !(_invUI?.IsOpen ?? false);
            bool sparks = HasBlowtorch && lmb;   // the torch is LIT whenever the trigger's held (source: Repeated Start_Swing continuous use); it repairs a hurt car / salvages a cold wreck when aimed at one
            if (v != null && HasBlowtorch && !v.IsWreck && v.Hurt)   // blowtorch REPAIR: full-auto healing of a hurt alive car while LMB is held (master), with torch sparks
            {
                if (lmb) { v.Repair((_melee?.VehicleDamage ?? 10f) * 3f * delta); sparks = true; }   // ~30 HP/s continuous
                _salvageTimer = 0f;
            }
            else if (v != null && v.IsWreck)   // a WRECK: state prompt + hold-LMB-to-salvage
            {
                Color red = new Color(0.90f, 0.25f, 0.20f), white = new Color(0.95f, 0.95f, 0.95f);
                if (v.WreckOnFire) { v.SetSalvagePrompt("Too hot to salvage", red); _salvageTimer = 0f; }
                else if (!HasBlowtorch) { v.SetSalvagePrompt("Requires blowtorch to salvage", red); _salvageTimer = 0f; }
                else if (lmb)
                {
                    _salvageTimer += delta; sparks = true;
                    if (_salvageTimer >= SalvageTime) { v.Salvage(); _focusVehicle = null; _salvageTimer = 0f; sparks = false; }
                    else v.SetSalvagePrompt($"Salvaging... {Mathf.Clamp((int)(_salvageTimer / SalvageTime * 100f), 0, 99)}%", white);
                }
                else { v.SetSalvagePrompt("Hold LMB to salvage", white); _salvageTimer = 0f; }
            }
            else if (_focusDeployable != null && IsInstanceValid(_focusDeployable) && HasBlowtorch && !_focusDeployable.IsWreck && _focusDeployable.Hurt)   // blowtorch REPAIR a hurt live generator (full-auto heal while LMB held), same as a car
            {
                if (lmb) { _focusDeployable.Repair((_melee?.VehicleDamage ?? 10f) * 3f * delta); sparks = true; }   // ~30 HP/s continuous
                _salvageTimer = 0f;
            }
            else if (_focusDeployable != null && IsInstanceValid(_focusDeployable) && _focusDeployable.IsWreck)   // a burnt-out generator: same blowtorch salvage as a car wreck
            {
                Color red = new Color(0.90f, 0.25f, 0.20f), white = new Color(0.95f, 0.95f, 0.95f);
                var dp = _focusDeployable;
                if (dp.WreckOnFire) { dp.SetSalvagePrompt("Too hot to salvage", red); _salvageTimer = 0f; }
                else if (!HasBlowtorch) { dp.SetSalvagePrompt("Requires blowtorch to salvage", red); _salvageTimer = 0f; }
                else if (lmb)
                {
                    _salvageTimer += delta; sparks = true;
                    if (_salvageTimer >= SalvageTime)
                    {
                        // MP: a replicated wreck tears down server-side (scrap spawns there too); the
                        // removal echoes back through the replica view. SP/local nodes salvage direct.
                        if (NetSalvageDeployable != null && dp.NetId != 0) NetSalvageDeployable(dp.NetId);
                        else dp.Salvage();
                        _focusDeployable = null; _salvageTimer = 0f; sparks = false;
                    }
                    else dp.SetSalvagePrompt($"Salvaging... {Mathf.Clamp((int)(_salvageTimer / SalvageTime * 100f), 0, 99)}%", white);
                }
                else { dp.SetSalvagePrompt("Hold LMB to salvage", white); _salvageTimer = 0f; }
            }
            else _salvageTimer = 0f;
            // Repeated tool: drive the continuous-use ANIM off the LMB edge -- Start_Swing (loops) on press, Stop_Swing on release (source startSwing/stopSwing)
            bool wantTorch = IsRepeatedMelee && lmb;
            if (wantTorch && !_torchAnimOn) { _viewmodel?.StartTorch(); _torchAnimOn = true; }
            else if (!wantTorch && _torchAnimOn) { _viewmodel?.StopTorch(); _torchAnimOn = false; }
            _viewmodel?.SetTorchSparks(sparks);   // blue welding-arc sparks fly from the torch while lit (master)
        }

        // F (interact): pick up the item you're LOOKING AT (the focused one), adding it to the inventory.
        public void TryPickup()
        {
            // grab an item you're looking at straight off a shelf (before the dropped-item path)
            if (_focusShelfItem != null && IsInstanceValid(_focusShelfItem) && _focusShelfItem.Shelf != null)
            {
                var shelf = _focusShelfItem.Shelf;
                var grabbed = shelf.GrabItem(_focusShelfItem.CellKey);   // removes it from the grid -> the display syncs the model away
                _focusShelfItem = null;
                if (grabbed == null) return;
                if (Inventory.tryAddItem(grabbed))
                {
                    GD.Print($"[shelf-grab] {grabbed.GetAsset()?.itemName}");
                    _invUI?.Refresh();
                    if (Unarmed) EquipItemAsset(grabbed.GetAsset(), grabbed);   // hands free -> hold it (strawberry: restore force-into-hands on pickup, for all items)
                }
                else shelf.Storage.tryAddItem(grabbed);   // inventory full -> put it back on the shelf
                return;
            }
            var wi = _focusItem;
            if (wi == null || !IsInstanceValid(wi) || !Inventory.tryAddItem(wi.Item)) return;
            var item = wi.Item; var asset = item.GetAsset();
            bool wasUnarmed = Unarmed;
            GD.Print($"[pickup] {asset?.itemName}");
            wi.QueueFree();
            _focusItem = null;
            _invUI?.Refresh();
            if (wasUnarmed) EquipItemAsset(asset, item);   // picked up with an empty hand -> equip it in the hand (strawberry)
        }

        float _meleeCd;
        MeleeDef _melee;   // the equipped melee weapon (null = bare fists)
        string _heldMeleeName;   // content name of the held melee (for tool checks, e.g. the blowtorch)
        public bool HasBlowtorch => _melee != null && _melee.Repair;   // a REPAIR tool in hand (source: blowtorch carries the "Repair" flag) -> repairs hurt cars + salvages wrecks
        public bool IsRepeatedMelee => _melee != null && _melee.Repeated;   // a "Repeated" tool (blowtorch/chainsaw): continuous HOLD, NO weak/strong swing, NO strong (RMB) attack (source ItemMeleeAsset: "'Repeated' melee weapons don't have strong attacks")
        float _salvageTimer;   // seconds of LMB-hold accumulated against the focused wreck (blowtorch salvage)
        const float SalvageTime = 3f;   // hold this long to break a wreck down
        bool _torchAnimOn;     // is the Repeated-tool continuous-use anim (Start_Swing) currently playing? (tracked off the LMB edge)

        // Equip a melee weapon: load its real ItemMeleeAsset .dat (Range + per-target damage) so a swing is
        // weapon-specific. Holsters any gun viewmodel (the in-hand melee VIEWMODEL is the next melee-system increment).
        public void EquipHeldMelee(string meleeName)
        {
            SaveGunState(); _heldItem = null; _heldConsumable = null; _heldFuelItem = null; ClearDeployable();   // stash the outgoing gun's state; equipping a melee REPLACES any held consumable (not a layer)
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false;   // swapping off a gun mid-reload aborts it (master)
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;
            string p = ProjectSettings.GlobalizePath($"res://content/{meleeName}.dat");
            _melee = System.IO.File.Exists(p) ? MeleeDef.FromDatText(meleeName, System.IO.File.ReadAllText(p)) : new MeleeDef { Name = meleeName };
            _heldMeleeName = meleeName;   // remember the tool (blowtorch salvage check)
            _torchAnimOn = false;         // fresh weapon -> the continuous-use anim isn't running yet
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { MeleeMesh = $"{meleeName}.txt", MeleeAlbedo = $"{meleeName}_albedo.png" };   // show the melee weapon in-hand (arms + model, no gun FX)
            AddChild(_viewmodel);
            RelinkViewmodelLighting();   // re-take the world lighting on the new viewmodel (else fullbright)
            GD.Print($"[melee] equipped {_melee.Name} (range {_melee.Range}, zombie dmg {_melee.ZombieDamage}, stamina {_melee.Stamina})");
        }

        // Put whatever's in hand away -> UNARMED (bare fists). The src has no "holding nothing" combat state: empty
        // hands ARE fists (PlayerEquipment hardcodes the punch), so dequipping lands you on the fists melee.
        public void Dequip() => EquipUnarmed();

        // Unarmed = bare fists: arms in the melee ready hold, LMB weak / RMB strong punch, no weapon mesh.
        public void EquipUnarmed()
        {
            SaveGunState(); ClearDeployable();
            _heldItem = null; Gun = null; _heldConsumable = null; _heldFuelItem = null; _heldConsumableMesh = null;
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false;
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;
            _torchAnimOn = false; _pendingMeleeHit = -1f;
            _melee = MeleeDef.Fists; _heldMeleeName = "fists";   // fists ARE a melee -> the existing LMB/RMB swing path punches
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { Fists = true };
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            GD.Print("[equip] unarmed -> fists (LMB/RMB to punch)");
        }

        // Hotbar (master): 1 = primary slot, 2 = secondary slot; RMB an item + 3-9 binds that key to it, then the key equips it.
        public readonly System.Collections.Generic.Dictionary<int, (byte page, byte x, byte y)> HotbarBinds = new();
        public void BindHotbar(int key, byte page, byte x, byte y) { HotbarBinds[key] = (page, x, y); GD.Print($"[hotbar] key {key} -> item at page {page} ({x},{y})"); }
        public void EquipHotbar(int n)
        {
            if (n == 1) { EquipFromLocation(0, 0, 0); return; }        // primary slot (page 0)
            if (n == 2) { EquipFromLocation(1, 0, 0); return; }        // secondary slot (page 1)
            if (HotbarBinds.TryGetValue(n, out var loc)) EquipFromLocation(loc.page, loc.x, loc.y);   // a bound item (3-9)
        }
        void EquipFromLocation(byte page, byte x, byte y)
        {
            if (Inventory == null || page >= Inventory.items.Length) return;
            var pg = Inventory.items[page];
            byte idx = pg.getIndex(x, y);
            if (idx == byte.MaxValue) return;                          // empty slot -> nothing to equip
            var j = pg.getItem(idx);
            EquipItemAsset(j.GetAsset(), j.item);
        }

        // Dispatch-equip an item into the hand by its asset type (gun / melee / consumable). True if it equipped.
        public bool EquipItemAsset(ItemAsset asset, SDG.Unturned.Item backing)
        {
            if (asset == null) return false;
            if (asset.gunName != null) { EquipHeldGun(asset.gunName, backing); return true; }
            if (asset.meleeName != null) { EquipHeldMelee(asset.meleeName); return true; }
            if (asset.IsConsumable) { EquipHeldConsumable(asset, asset.itemName?.ToLowerInvariant().Replace(" ", "_")); return true; }   // EquipHeldConsumable snapshots the revert target itself
            var deploy = DeployableDef.ById(asset.id);
            if (deploy != null) { EquipHeldDeployable(deploy, backing); return true; }   // generator/spotlight -> hold + placement ghost, LMB plants + consumes one from the bag
            var tool = ToolDef.ById(asset.id);
            if (tool != null) { EquipTool(tool, backing); return true; }   // Wire (65) / Rope (64) / future tools = data-driven (was hard-coded ids)
            if (asset.IsFuelContainer) { EquipHeldFuelCan(asset, backing); return true; }   // a gas can -> hold it, RMB a powered pump to fill it
            return false;
        }

        // Equip a gas can into the hand (master's fluids): hold it, then RMB a powered gas pump to fill it. No extracted
        // carry model yet -> EmptyHands (invisible in-hand); the mechanic is what matters. HoldingWireTool clears itself
        // (it's derived from the viewmodel), and this replaces any gun/melee/consumable/deployable in hand.
        public void EquipHeldFuelCan(ItemAsset asset, SDG.Unturned.Item backing)
        {
            SaveGunState(); ClearDeployable();
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldConsumableMesh = null;
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false;
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;
            _heldFuelItem = backing;
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { DeployableMesh = "gascan.txt", DeployableAlbedo = "gascan_albedo.png", NaturalHold = true };   // the ripped 1P gas-can model held with BOTH HANDS (NaturalHold -> plays the can's own two-handed Fuel_Equip carry anim, source animations.prefab); HoldingDeployable stays false (no _deployable) so RMB still extracts
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            GD.Print($"[fuel] holding {asset?.itemName} -- {(backing != null ? Mathf.Max(0f, backing.fuelLevel) : 0f):0}/{asset?.fuelCapacity:0} fuel (RMB a powered pump to fill)");
        }

        // RMB with a gas can in hand + looking at a POWERED pump: fill the can as much as possible = min(its free space,
        // the pump's remaining fuel). One click (master). Nothing happens if the pump's unpowered/empty or the can's full.
        // RMB = SUCK fuel INTO the can: from a powered pump, else OUT of a vehicle you're looking at (master: cars are suckable).
        // A2 test seam (L1 unify.gaspump_fixture_extract): headless tests can't drive the look-ray or spin up the
        // gas-can viewmodel, so they set the focused pump + the held can directly + call TryExtractFuel to exercise
        // the REAL controller extract path (the replica-pump wire route).
        internal void SetFocusGasPumpForTest(GasPump pump) => _focusGasPump = pump;
        internal void SetHeldFuelCanForTest(SDG.Unturned.Item backing) => _heldFuelItem = backing;

        internal void TryExtractFuel()
        {
            if (_heldFuelItem == null) return;
            var asset = _heldFuelItem.GetAsset();
            if (asset == null || !asset.IsFuelContainer) return;
            float canFuel = Mathf.Max(0f, _heldFuelItem.fuelLevel);
            float space = asset.fuelCapacity - canFuel;
            if (space <= 0.01f) { GD.Print("[fuel] can is full"); return; }
            if (IsInstanceValid(_focusGasPump))
            {
                // A2 (SP/MP-unify): a REPLICATED pump (NetId!=0, consuming loopback / joined client) routes the
                // extract as an intent over the wire and RETURNS -- the server drains the shared station tank + fills
                // the can, and the owner-inventory echo re-adopts the fuller can. NO local Extract/fuelLevel add (the
                // direct tank-drain is DISABLED under consume; a local add would double-count + desync). Powered is
                // checked server-side (a fresh Solve). Direct SP pumps (NetId==0) take the local path below.
                if (_focusGasPump.NetId != 0) { NetExtractFuel?.Invoke(_focusGasPump.NetId); return; }
                if (!_focusGasPump.IsPowered) { GD.Print("[fuel] that pump has no power"); return; }
                float pulled = _focusGasPump.Extract(space);   // drains the pump's shared station tank, capped at what's left
                if (pulled > 0f) { _heldFuelItem.fuelLevel = canFuel + pulled; _invUI?.Refresh(); GD.Print($"[fuel] +{pulled:0} from pump -> can {_heldFuelItem.fuelLevel:0}/{asset.fuelCapacity:0}"); }
            }
            else if (IsInstanceValid(_focusVehicle) && _focusVehicle.FuelMax > 0f)   // siphon fuel out of a car
            {
                float pulled = Mathf.Min(space, _focusVehicle.Fuel);
                if (pulled <= 0.01f) { GD.Print("[fuel] that vehicle is empty"); return; }
                _focusVehicle.Fuel -= pulled; _heldFuelItem.fuelLevel = canFuel + pulled; _invUI?.Refresh();
                GD.Print($"[fuel] siphoned {pulled:0} from {_focusVehicle.DisplayName} -> can {_heldFuelItem.fuelLevel:0}/{asset.fuelCapacity:0}");
            }
        }

        // RMB with a gas can in hand + looking at a GENERATOR (any FuelMax deployable): POUR the can into it
        // (source UseableFuel EUseMode.Deposit). Moves min(what's in the can, the tank's free space). This is how
        // you refuel a generator that ran dry -- then a manual [F] restarts it (it doesn't auto-resume).
        // LMB = POUR the can's fuel INTO a generator (any FuelMax deployable), else a vehicle you're looking at (master:
        // cars are fillable). Refuel a dead gen -> manual [F] restart; refuel a dead car -> re-enter restarts it.
        void TryDepositFuel()
        {
            if (_heldFuelItem == null) return;
            var asset = _heldFuelItem.GetAsset();
            if (asset == null || !asset.IsFuelContainer) return;
            float canFuel = Mathf.Max(0f, _heldFuelItem.fuelLevel);
            if (canFuel <= 0.01f) { GD.Print("[fuel] can is empty"); return; }
            if (IsInstanceValid(_focusDeployable) && _focusDeployable.FuelMax > 0f)
            {
                float space = _focusDeployable.FuelMax - _focusDeployable.Fuel;
                if (space <= 0.01f) { GD.Print("[fuel] that tank is full"); return; }
                float poured = Mathf.Min(canFuel, space);
                _focusDeployable.Fuel += poured; _heldFuelItem.fuelLevel = canFuel - poured; _invUI?.Refresh();
                PowerNet.MarkDirty();   // a dry gen just got fuel back -> re-evaluate the net (still needs a manual restart)
                GD.Print($"[fuel] poured {poured:0} -> {_focusDeployable.Def?.Name} {_focusDeployable.Fuel:0}/{_focusDeployable.FuelMax:0}; can {_heldFuelItem.fuelLevel:0} left");
            }
            else if (IsInstanceValid(_focusVehicle) && _focusVehicle.FuelMax > 0f)
            {
                float space = _focusVehicle.FuelMax - _focusVehicle.Fuel;
                if (space <= 0.01f) { GD.Print("[fuel] that tank is full"); return; }
                float poured = Mathf.Min(canFuel, space);
                _focusVehicle.Fuel += poured; _heldFuelItem.fuelLevel = canFuel - poured; _invUI?.Refresh();
                GD.Print($"[fuel] poured {poured:0} -> {_focusVehicle.DisplayName} {_focusVehicle.Fuel:0}/{_focusVehicle.FuelMax:0}; can {_heldFuelItem.fuelLevel:0} left");
            }
        }

        // A closure that re-equips whatever is held RIGHT NOW (used to revert after a consumable stack empties);
        // a gun/melee reverts only if it's still in the bag, else fists.
        System.Action _revertEquip;
        System.Action CaptureHeldForRevert()
        {
            if (Gun != null && _melee == null && _heldConsumable == null)
            {
                string g = _gunName; var it = _heldItem; ushort? id = it?.id;
                return () => { if (id == null || (Inventory?.getItemCount(id.Value) ?? 0) > 0) EquipHeldGun(g, it); else EquipUnarmed(); };
            }
            if (_melee != null && _melee.Name != "fists") { string m = _heldMeleeName; return () => EquipHeldMelee(m); }
            return EquipUnarmed;   // was fists / unarmed -> back to fists
        }

        // UNARMED = bare fists (or genuinely nothing): the "empty hand" state. A picked-up item auto-equips here.
        public bool Unarmed => Gun == null && _heldConsumable == null && _deployable == null && !HoldingWireTool && !HoldingHoseTool && _heldFuelItem == null && (_melee == null || _melee.Name == "fists");

        // Is this inventory item the one currently IN HAND? (drives the inventory's Equip<->Dequip toggle.)
        public bool IsHeld(ItemAsset asset, SDG.Unturned.Item item)
        {
            if (asset == null) return false;
            if (Gun != null && _melee == null && _heldConsumable == null && _deployable == null)
                return item != null ? ReferenceEquals(_heldItem, item) : (_heldItem != null && _heldItem.id == asset.id);
            if (_melee != null && _melee.Name != "fists") return asset.meleeName != null && asset.meleeName == _heldMeleeName;
            if (_heldConsumable != null) return _heldConsumable.id == asset.id;
            if (_heldFuelItem != null) return item != null ? ReferenceEquals(_heldFuelItem, item) : asset.IsFuelContainer;   // a held gas can -> dropping it goes unarmed (master)
            if (_deployable != null) return _deployable.Id == asset.id;
            if (HoldingWireTool) return asset.id == 65;
            if (HoldingRopeTool) return asset.id == 64;
            if (HoldingHoseTool) return asset.id == 66;
            return false;
        }

        // --- Consumables held in hand (food/drink/medical): equip -> hold -> LMB eats/drinks -> effects apply (source UseableConsumeable). ---
        ItemAsset _heldConsumable;   // the consumable held in hand (null = none); LMB starts eating/drinking it
        string _heldConsumableMesh;  // its mesh name -> re-equip another of the same type after one is consumed (master)
        float _consumeTimer;         // >0 while eating -- applies the consumable's effects when it hits 0
        const float ConsumeUseTime = 2.2f;   // default eat/drink duration (fallback when an item has no mapped Use-clip length)
        float _consumeUseLen = ConsumeUseTime;   // THIS item's eat/drink duration = source useTime = its Use-clip length (per-item)
        public bool HoldingConsumable => _heldConsumable != null;

        // --- Deployables held in hand (generator / spotlight): equip -> aim shows a placement ghost -> LMB plants it. ---
        public bool HoldingWireTool => _viewmodel != null && _viewmodel.IsWireViewmodel;   // Wire tool (item 65) in hand -> wiring mode (LMB/RMB build/cancel wires); derived from the viewmodel so no state to clear
        public bool HoldingRopeTool => _viewmodel != null && _viewmodel.IsRopeViewmodel;   // Rope tool (item 64) in hand -> tow mode (LMB tie rear->front, RMB cancel/untie); derived from the viewmodel
        public bool HoldingHoseTool => _viewmodel != null && _viewmodel.IsHoseViewmodel;   // Hose tool (item 66) in hand -> fluid-hose mode (LMB source->consumer, RMB cancel); derived from the viewmodel
        DeployableDef _deployable;      // held deployable (null = none)
        SDG.Unturned.Item _deployItem;  // the backing inventory item (null = console `deploy`, i.e. infinite/no consume)
        DeployablePlacer _placer;       // the world-space ghost preview
        float _placeTimer;              // >0 while the brief place gesture runs; the object drops at 0
        Vector3 _placePoint; float _placeYaw;   // target FROZEN at click -> the object drops there even if you look away
        const float PlaceTime = 0.45f;  // src UseableBarricade builds over the Use-clip length; a short stand-in here
        public bool HoldingDeployable => _deployable != null;

        // A gun is genuinely OUT only when one is loaded AND nothing else is in hand. A melee/held item is mutually
        // exclusive with the gun, so it fully disarms: no firing, no ammo HUD, no reload/firemode logic (master).
        public bool HasGunOut => Gun != null && _melee == null && _heldConsumable == null && _deployable == null;

        // Equip a consumable to the hands from the inventory: hold its model; LMB to eat/drink.
        // captureRevert=false only for the auto-re-equip of the NEXT of the same stack (keeps the original revert target).
        public void EquipHeldConsumable(ItemAsset asset, string meshName, bool captureRevert = true)
        {
            if (captureRevert && _heldConsumable == null) _revertEquip = CaptureHeldForRevert();   // fresh switch INTO a consumable -> remember what to fall back to when the stack empties
            if (string.IsNullOrEmpty(meshName)) meshName = "canned_beans";   // generic held stand-in so an unmapped consumable never shows a null/broken mesh
            _heldConsumable = asset;
            _heldConsumableMesh = meshName;   // remembered so consuming one can auto-equip another of the same type (master)
            _consumeTimer = 0f;
            _melee = null; ClearDeployable();
            var an = ConsumableRegistry.Anims(meshName);   // this item's own eat/drink archetype clips + source useTime (Use-clip length)
            _consumeUseLen = an.UseLen > 0f ? an.UseLen : ConsumeUseTime;
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { ConsumableMesh = $"{meshName}.txt", ConsumableAlbedo = $"{meshName}_albedo.png", ConsumableEquipClip = an.Equip, ConsumableUseClip = an.Use, ConsumableColor = ConsumableRegistry.FlatColor(meshName) };
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            GD.Print($"[consume] holding {asset?.itemName ?? meshName} ({an.Use}, {_consumeUseLen:0.0}s) -- click to eat/drink");
        }

        // LMB while holding a consumable: begin eating/drinking (plays the Use anim + starts the use timer).
        public void StartConsume()
        {
            if (_heldConsumable == null || _consumeTimer > 0f || _dead) return;
            _consumeTimer = _consumeUseLen;   // source-accurate: the length of THIS item's Use animation
            _viewmodel?.PlayConsumeUse();
            PlayConsumeSound(_heldConsumable.id);   // source playConsume: player.playSound(asset.use) at use start
            GD.Print($"[consume] eating {_heldConsumable?.itemName}...");
        }

        // Ticked each frame: run the eat timer; when it elapses, apply the consumable's effects (source consume()).
        void TickConsume(float dt)
        {
            if (_consumeTimer <= 0f) return;
            _consumeTimer -= dt;
            if (_consumeTimer <= 0f && _heldConsumable != null)
            {
                Consume(_heldConsumable);   // apply Health/Food/Water/etc. (MP too: vitals stay client-led until the vitals split; the server mirrors coarse health itself)
                ushort id = _heldConsumable.id;
                var asset = _heldConsumable; string mesh = _heldConsumableMesh;
                GD.Print($"[consume] consumed {_heldConsumable.itemName}");
                _heldConsumable = null; _heldFuelItem = null;             // one use per item: this one leaves the hand + is deleted (master)
                int left;
                if (NetConsume != null)
                {
                    // MP: the DELETION is the server's -- send the cell holding one of these (the server
                    // removes by id, the cell just names the item) and let the owner echo empty the bag.
                    // The re-equip decision predicts the echo (count - 1); the hand is client-local state.
                    if (FindBagCell(id, out byte cp, out byte cx, out byte cy)) NetConsume(cp, cx, cy);
                    left = (Inventory?.getItemCount(id) ?? 1) - 1;
                }
                else
                {
                    Inventory?.removeItemAmount(id, 1);  // delete the one that was eaten
                    left = Inventory?.getItemCount(id) ?? 0;
                }
                if (left > 0)
                    EquipHeldConsumable(asset, mesh, captureRevert: false);   // still have another of the same type -> auto-equip a FRESH one (keep the original revert target)
                else
                    (_revertEquip ?? EquipUnarmed)();   // stack empty -> revert to the last-held item if still valid, else fists (strawberry)
            }
        }

        // test-only: drive the eat/drink timer from a headless self-test (--consumeholdtest)
        public void DebugConsumeTick(float dt) => TickConsume(dt);

        // The first grid cell holding an item of this id (page,x,y) -- the NetConsume address (the held
        // consumable doesn't carry its source cell; the server deletes by id, so any matching cell names it).
        bool FindBagCell(ushort id, out byte page, out byte x, out byte y)
        {
            page = x = y = 0;
            if (Inventory == null) return false;
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                var pg = Inventory.items[p];
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var j = pg.getItem(i);
                    if (j?.item != null && j.item.id == id) { page = p; x = j.x; y = j.y; return true; }
                }
            }
            return false;
        }

        // Equip a deployable to the hands: empty-hand carry + a world-space placement ghost that follows your aim
        // (blue valid / red invalid). LMB plants a real object. (src UseableBarricade equip/tick/startPrimary.)
        public void EquipHeldDeployable(DeployableDef def, SDG.Unturned.Item backing = null)
        {
            if (def == null) return;
            SaveGunState();
            if (_deployable == null) _revertEquip = CaptureHeldForRevert();   // fresh switch INTO a deployable -> remember what to fall back to when the last one is placed
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldFuelItem = null; _heldConsumableMesh = null;
            _reloading = false; _torchAnimOn = false;
            _deployable = def; _deployItem = backing; _placeTimer = 0f;
            _viewmodel?.QueueFree();
            _viewmodel = def.HoldMesh != null
                ? new Viewmodel { DeployableMesh = def.HoldMesh, DeployableAlbedo = def.HoldAlbedo }   // carry model in-hand + Deploy_Equip raise; LMB plays Deploy_Use
                : new Viewmodel { EmptyHands = true };   // no extracted carry model yet (spotlight) -> ghost-only feedback
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            _placer?.QueueFree();
            _placer = new DeployablePlacer();
            GetParent().AddChild(_placer);      // world space: the ghost stays put in the world, not glued to the player
            _placer.SetDef(def);
            GD.Print($"[deploy] holding {def.Name} -- aim, LMB to place");
        }

        // Equip the Wire tool (item 65): the wiring tool held in hand. Wiring interaction (select node / route / place /
        // cancel / undo) lands in later phases; this just puts it in the hand (HoldingWireTool drives the mode).
        // General held-TOOL equip (master's holdable pass 2026-07-20): drive the in-hand tool viewmodel from a ToolDef
        // -- mesh + flat colour + the rope/wire kind bit. Was two near-identical EquipWireTool/EquipRopeTool bodies;
        // now ONE path + a data registry (ToolDef), so a new held tool is a data entry, not another hard-coded branch.
        // Behaviour byte-identical (the per-tool-kind revert guard is preserved).
        public void EquipTool(ToolDef def, SDG.Unturned.Item backing = null)
        {
            SaveGunState();
            bool alreadyThisKind = def.IsRope ? HoldingRopeTool : (def.IsHose ? HoldingHoseTool : HoldingWireTool);
            if (!alreadyThisKind) _revertEquip = CaptureHeldForRevert();   // remember what to fall back to
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldFuelItem = null; _heldConsumableMesh = null;
            _reloading = false; _torchAnimOn = false; ClearDeployable();
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { ToolMesh = def.HeldMesh, ToolColor = def.HeldColor, IsRopeTool = def.IsRope, IsHoseTool = def.IsHose };
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            GD.Print($"[tool] holding the {def.Name}");
        }

        public void EquipWireTool(SDG.Unturned.Item backing = null) => EquipTool(ToolDef.Wire, backing);   // Wire (item 65) = the power wiring tool

        // Equip the Rope tool (item 64): the vehicle tow-rope. Held in hand -> HoldingRopeTool drives tow mode (LMB ties
        // this car's REAR node to another car's FRONT node like a wire; RMB cancels/unties). Reuses the wire hold mesh
        // tinted hemp-brown. SP/integrated-server only (the pull force needs both vehicle bodies in one physics space).
        public void EquipRopeTool(SDG.Unturned.Item backing = null) => EquipTool(ToolDef.Rope, backing);

        // Equip the Hose tool (item 66): the fluid hose. Held in hand -> HoldingHoseTool drives hose mode (LMB starts at a
        // source/consumer HosePort, LMB completes on a compatible opposite-role port -> a Hose; RMB cancels a pending route).
        // Type-lock ("cannot mix fluids") is enforced at completion; gravity gates whether the finished hose actually flows.
        public void EquipHoseTool(SDG.Unturned.Item backing = null) => EquipTool(ToolDef.Hose, backing);

        // Put the held deployable away (called whenever another item is equipped).
        void ClearDeployable()
        {
            if (_deployable == null && _placer == null) return;
            _deployable = null; _deployItem = null; _placeTimer = 0f;
            _placer?.QueueFree(); _placer = null;
        }

        // LMB while holding a deployable: if the current aim is valid, FREEZE the target (point+yaw) at the click
        // and start the brief place gesture -- the object drops there even if you look away during the delay.
        void TryPlaceDeployable()
        {
            if (_placer == null || _deployable == null || _placeTimer > 0f || _dead) return;
            if (!_placer.Aim(_cam)) return;   // only from a VALID (blue) spot
            _placePoint = _placer.Point; _placeYaw = _placer.Yaw;   // FROZEN at click (strawberry: don't drift with the mouse)
            _viewmodel?.PlayDeployUse();   // arms play the src "Use" place motion; the object drops when it finishes
            float useLen = _viewmodel?.DeployUseLength() ?? 0f;
            _placeTimer = useLen > 0f ? useLen : PlaceTime;   // build over the Use-clip length (src useTime), else the short stand-in
        }

        // Ticked each frame while holding a deployable: follow the aim with the ghost, or -- mid-place -- hold the
        // ghost frozen at the click point and drop the object there when the gesture finishes.
        void TickDeploy(float dt)
        {
            if (_deployable == null || _placer == null) return;
            if (_placeTimer > 0f)   // FROZEN: ghost stays at the click point, aim is ignored until the object drops
            {
                _placer.Freeze(_placePoint, _placeYaw);
                _placeTimer -= dt;
                if (_placeTimer <= 0f)
                {
                    if (NetPlaceDeployable != null)
                    {
                        // MP: the placement is a REQUEST -- the server validates spot + supplies, spends
                        // the item, and broadcasts; DeployableReplicaView spawns the real node. Ghost/fx
                        // stay local; the revert decision predicts the echo's spend (count - 1).
                        RequestPlaceDeployable(_deployable.Id, _placePoint, _placeYaw);
                        PlayPlaceSound(_deployable.PlaceSound, _placePoint);
                        GD.Print($"[deploy] place requested: {_deployable.Name} at {_placePoint} (wire)");
                        if (_deployItem != null && Inventory != null && Inventory.getItemCount(_deployItem.id) <= 1)
                        { (_revertEquip ?? EquipUnarmed)(); return; }   // the last one just went over the wire -> revert
                        _viewmodel?.PlayDeployHold();
                        return;
                    }
                    Deployable.Spawn(GetParent(), _deployable, _placePoint, _placeYaw, _deployItem);   // backing item restores a picked-up generator's fuel + HP
                    PlayPlaceSound(_deployable.PlaceSound, _placePoint);   // src: playSound(barricadeAsset.use) on build -- the .dat PlacementAudioClip
                    GD.Print($"[deploy] placed {_deployable.Name} at {_placePoint}");
                    // consume one from the bag (like a placed barricade). Console `deploy` has no backing item -> infinite.
                    if (_deployItem != null && Inventory != null)
                    {
                        ushort id = _deployItem.id;
                        Inventory.removeItemAmount(id, 1);
                        if (Inventory.getItemCount(id) <= 0) { (_revertEquip ?? EquipUnarmed)(); return; }   // stack empty -> revert to last-held / fists
                    }
                    _viewmodel?.PlayDeployHold();   // still holding one -> arms settle back to the carry hold (not stuck at the end of Deploy_Use)
                }
                return;
            }
            bool active = !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured && !(_invUI?.IsOpen ?? false);
            _placer.SetGhostVisible(active);
            if (active) _placer.Aim(_cam);
        }
        public static bool DebugCanLoadWav(string stem) => LoadWavOneShot($"res://content/sounds/{stem}.wav") != null;   // test: the exported WAV parses as 16-bit PCM
        public bool DebugUsesMag() => UsesMagItem;           // test: does the equipped gun use magazine items
        public void DebugMagSwap() => DoMagSwap();           // test: run one reload magazine swap
        public bool DebugHasSpareMag() => FindBestMag() != null;   // test: is there a compatible spare mag to reload from
        // bolt/pump rechamber (source needsRechamber): after firing, wait RechamberAfterShotDelay, then cycle the action
        // (the Hammer / bolt-cycle clip) -> fire+reload stay blocked until it finishes. PlayHammer also rotates the gun.
        void TickRechamber(double delta)
        {
            if (_needsRechamber && !_rechambering)
            {
                _rechamberDelayTimer -= delta;
                if (_rechamberDelayTimer <= 0)
                {
                    _rechambering = true;
                    _shotCountForRechamber = 0;
                    float hl = _viewmodel?.HammerLength ?? 0f;
                    _rechamberAnimTimer = hl > 0f ? hl : 0.5f;   // the bolt-cycle clip length (small fallback if a gun ships none)
                    _viewmodel?.PlayHammer();
                }
            }
            else if (_rechambering)
            {
                _rechamberAnimTimer -= delta;
                if (_rechamberAnimTimer <= 0) { _rechambering = false; _needsRechamber = false; }   // action cycled -> ready to fire again
            }
        }
        public int DebugRechamberCount() => Gun?.RechamberAfterShotCount ?? -1;   // test: 1 for bolt/pump, 0 for self-loading
        public bool DebugNeedsRechamber() => _needsRechamber || _rechambering;    // test: is the gun mid-cycle (can't fire)
        public void DebugFireRechamber() { if (Gun != null && Gun.RechamberAfterShotCount > 0 && ++_shotCountForRechamber >= Gun.RechamberAfterShotCount) { _needsRechamber = true; _rechamberDelayTimer = Gun.RechamberAfterShotDelay; } }   // test: the post-shot rechamber trigger (the tail of Fire)
        public void DebugRechamberTick(double dt) => TickRechamber(dt);   // test: advance the bolt-cycle timers
        public bool DebugIsShotgun() => Gun?.IsShotgun ?? false;   // test: pump/break shell gun
        public bool DebugShellReload() => Gun?.ShellReload ?? false;   // test: shell-by-shell (pump tube) reload
        public bool DebugHasChamber() => HasChamber;         // test: does the gun get a +1 chambered round
        public void DebugCompleteReload() { int max = Gun?.AmmoMax ?? 30; if (UsesShells) Ammo += ConsumeShells(max - Ammo); else if (UsesMagItem) DoMagSwap(); else Ammo = (HasChamber && Ammo > 0) ? max + 1 : max; }   // test: run the reload fill (same branch as the reload tick)
        public bool DebugUsesShells() => UsesShells;         // test: does the gun feed from loose shells
        public int DebugCountShells() => CountShells();      // test: shells of the gun's caliber carried
        public int DebugPellets() => UsesShells && ShellAsset != null ? System.Math.Max(1, ShellAsset.pellets) : System.Math.Max(1, Gun?.Pellets ?? 1);   // test: rays per shot (shotgun = shell pellets)
        public void DebugSetHeldItem(SDG.Unturned.Item it) => _heldItem = it;      // test: link a backing item to the held gun
        public void DebugSaveGunState() => SaveGunState();                          // test: mirror live gun state to the backing item
        public void DebugRestoreGunState(SDG.Unturned.Item it) => RestoreGunState(it);   // test: restore a gun's state from an item
        public int DebugFiremodeIdx() => (int)_firemode;                            // test: current fire-mode index
        public void DebugSetFiremode(int m) => _firemode = (FireMode)m;             // test: set the fire mode

        // Play the consumable's use/eat/drink sound (source ItemConsumeableAsset.use, content/sounds/<stem>.wav).
        AudioStreamPlayer _consumeAudio;
        void PlayConsumeSound(ushort id)
        {
            string snd = ConsumableRegistry.Sound(id);
            if (snd == null) return;
            var stream = LoadWavOneShot($"res://content/sounds/{snd}.wav");
            if (stream == null) return;
            if (_consumeAudio == null || !IsInstanceValid(_consumeAudio)) { _consumeAudio = new AudioStreamPlayer(); AddChild(_consumeAudio); }
            _consumeAudio.Stream = stream;
            _consumeAudio.Play();
        }
        // Deployable placement sound (src: playSound(barricadeAsset.use) on build) -- a positional one-shot at the spot.
        void PlayPlaceSound(string stem, Vector3 at)
        {
            if (string.IsNullOrEmpty(stem)) return;
            var stream = LoadWavOneShot($"res://content/sounds/{stem}.wav");
            if (stream == null) return;
            var p = new AudioStreamPlayer3D { Stream = stream, UnitSize = 6f };
            GetParent().AddChild(p);
            p.GlobalPosition = at;   // world pos only valid once in the tree
            p.Play();
            p.Finished += p.QueueFree;   // self-cleanup after the one-shot
        }

        // Runtime one-shot WAV loader: walk the RIFF chunks for fmt+data (UnityPy exports may carry extra chunks, so the
        // fixed-44-byte-header assumption in Vehicle.LoadWav isn't safe here). 16-bit PCM only; anything else -> no sound.
        public static AudioStreamWav LoadWavOneShot(string resPath, bool loop = false)
        {
            string p = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(p)) return null;
            byte[] b = System.IO.File.ReadAllBytes(p);
            if (b.Length < 44) return null;
            int channels = 1, rate = 48000, bits = 16, dataOff = -1, dataLen = 0, i = 12;   // past "RIFF"<size>"WAVE"
            while (i + 8 <= b.Length)
            {
                string cid = System.Text.Encoding.ASCII.GetString(b, i, 4);
                int csz = System.BitConverter.ToInt32(b, i + 4);
                if (cid == "fmt " && i + 24 <= b.Length) { channels = System.BitConverter.ToInt16(b, i + 10); rate = System.BitConverter.ToInt32(b, i + 12); bits = System.BitConverter.ToInt16(b, i + 22); }
                else if (cid == "data") { dataOff = i + 8; dataLen = System.Math.Min(csz, b.Length - dataOff); break; }
                i += 8 + csz + (csz & 1);
            }
            if (dataOff < 0 || bits != 16) return null;
            byte[] pcm = new byte[dataLen]; System.Array.Copy(b, dataOff, pcm, 0, dataLen);
            return new AudioStreamWav { Data = pcm, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = channels == 2,
                                        LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled, LoopEnd = loop ? dataLen / (channels * bits / 8) : 0 };
        }

        // G: melee swing -- hit the nearest zombie in front within the weapon's reach (proximity, not a raycast). Reuses
        // the zombie damage path. Rounds out combat (Unturned lets you swing/punch when out of ammo or up close).
        // Melee swing (source UseableMelee): LMB = WEAK, RMB = STRONG. A strong swing hits for x Strength but winds up
        // slower and costs the same stamina; both drain the weapon's Stamina and make swing-noise at its Alert_Radius.
        public void MeleeAttack(bool strong = false)
        {
            if (_meleeCd > 0f || _cam == null || _dead || _driving != null || _heldConsumable != null || (_invUI?.IsOpen ?? false)) return;
            if (IsRepeatedMelee) return;   // Repeated tools (blowtorch/chainsaw) have NO weak/strong swing -- you don't punch with them; their use is the continuous LMB-hold (source UseableMelee.startPrimary/startSecondary)
            float staminaCost = strong ? (_melee?.Stamina ?? 0f) / 100f : 0f;   // only the STRONG (RMB) swing costs stamina; the WEAK (LMB) attack is free (master)
            if (staminaCost > 0f && Stamina < staminaCost) return;   // too winded for a strong swing
            if (staminaCost > 0f) { Stamina = Mathf.Max(0f, Stamina - staminaCost); _vitals.StaminaRegenDelay = 1f; }
            // cooldown = this weapon's actual swing-anim length (per-weapon: knife fast, sledge slow) so click-spam can't
            // out-pace the swing cadence + the rate matches the animation (master). Fallback for fists / a missing clip.
            _meleeCd = _viewmodel?.MeleeSwingLength(strong) ?? 0f;
            if (_meleeCd <= 0.05f) _meleeCd = strong ? 0.75f : 0.45f;
            _viewmodel?.SwingMelee(strong);   // source Weak / Strong swing anim
            float alert = _melee?.Alert ?? 0f;
            if (alert > 0f) SoundBus.Emit(GetTree(), GlobalPosition, alert);   // swing NOISE fires with the swing (source AlertTool.alert); 0 = stealthy
            // DAMAGE lands at the END of the swing (source: isDamageable is only true once the swing anim has played),
            // NOT instantly on click -- scheduled here and applied by the tick, re-evaluating targets when it connects (master).
            if (NetMelee != null) { NetMelee(strong, RotationDegrees.Y); return; }   // D1: swing fx played above; the SERVER owns the deferred hit (ServerCombat re-evaluates at land time)
            _pendingMeleeStrong = strong; _pendingMeleeHit = _meleeCd * 0.7f;
        }

        float _pendingMeleeHit = -1f; bool _pendingMeleeStrong;   // deferred melee hit: >0 = a swing is mid-flight, damage lands when it reaches 0
        // The deferred melee hit -- runs when the swing connects (end of the anim); targets are re-evaluated NOW so a moving target can be missed.
        void ApplyMeleeHit(bool strong)
        {
            if (_cam == null || _dead) return;
            float range = _melee?.Range ?? 2.2f;
            float mult = strong ? (_melee?.Strength ?? 1.5f) : 1f;   // STRONG swing hits harder (source dmg *= strength)
            if (_focusVehicle != null && IsInstanceValid(_focusVehicle) && !_focusVehicle.IsWreck
                && (_focusVehicle.GlobalPosition - GlobalPosition).Length() < range + 3f)   // vehicles are big -> generous reach
            {
                if (HasBlowtorch) { if (_focusVehicle.Hurt) _focusVehicle.Repair(_melee?.VehicleDamage ?? 10f); }
                else { _focusVehicle.TakeDamage((_melee?.VehicleDamage ?? 10f) * mult); GD.Print($"[melee] hit {_focusVehicle.DisplayName} for {(_melee?.VehicleDamage ?? 10f) * mult:0}"); }
                return;
            }
            if (_focusDeployable != null && IsInstanceValid(_focusDeployable) && !_focusDeployable.IsWreck
                && (_focusDeployable.GlobalPosition - GlobalPosition).Length() < range + 2f)   // looking at a placed generator: melee damages it (a blowtorch is for salvaging the wreck, not smashing)
            {
                if (HasBlowtorch) { if (_focusDeployable.Hurt) _focusDeployable.Repair(_melee?.VehicleDamage ?? 10f); }   // blowtorch repairs a hurt generator (continuous heal is in UpdateSalvage)
                else { _focusDeployable.TakeDamage((_melee?.VehicleDamage ?? 10f) * mult); GD.Print($"[melee] hit {_focusDeployable.Def?.Name} for {(_melee?.VehicleDamage ?? 10f) * mult:0}"); }
                return;
            }
            float dmg = (_melee?.ZombieDamage ?? 45f) * mult * Skills.OverkillMeleeMultiplier();   // weapon .dat Zombie_Damage x OVERKILL skill
            Vector3 origin = GlobalPosition + Vector3.Up * 1.2f, fwd = -_cam.GlobalTransform.Basis.Z;
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    Vector3 to = z.GlobalPosition + Vector3.Up - origin;
                    if (to.Length() < range + 0.5f && to.Normalized().Dot(fwd) > 0.3f)   // in front, in reach
                    {
                        bool wd = z.Dead;
                        z.DamageHit(dmg, z.GlobalPosition + Vector3.Up, fwd);
                        if (!wd && z.Dead) Kills++;
                        GD.Print($"[melee] {(strong ? "STRONG" : "weak")} hit ({_melee?.Name ?? "fists"} {dmg:0} dmg)");
                        break;   // one target per swing
                    }
                }
        }

        // PlayerLife.onLanded: landing faster than the fall-damage threshold (map default 22 m/s, and the port has
        // normal gravity so totalGravityMultiplier > 0.67 always holds) deals damage = min(101, |verticalVelocity|),
        // rounded. Source multiplies by the DEFENSE/STRENGTH skill (still 1.0 -- no skill system) then the WHOLE-BODY
        // clothing fallingDamageMultiplier (PlayerLife:2430 `damage *= clothing.fallingDamageMultiplier`) -- now WIRED.
        // Leg-breaking (source breakLegs) is now gated by worn clothing's Prevents_Falling_Broken_Bones (PlayerLife:2436) -- WIRED.
        void CheckFallDamage(float verticalVel)
        {
            if (NetAvatar) return;   // v1 invulnerability (see TakeDamage) -- and a broken-legs flag would silently eat the wire's jump bit
            if (!FallMath.Hurts(verticalVel)) return;          // a normal jump lands at ~7 m/s -> no damage
            Broken = FallMath.BreaksLegs(verticalVel, Inventory?.PreventsFallingBoneBreak ?? false);   // legs break on a hard fall UNLESS worn clothing has Prevents_Falling_Broken_Bones (source PlayerLife:2436)
            int dmg = FallMath.Damage(verticalVel, (Inventory?.FallingDamageMultiplier ?? 1f) * Skills.StrengthFallMultiplier());   // worn clothing (whole-body product) + STRENGTH skill both cut fall damage (source PlayerLife 2428-2430)
            if (dmg > 0) { GD.Print($"[fall] landed at {verticalVel:F1} m/s -> {dmg} damage, legs broken"); TakeDamage(dmg); }
        }

        float _grenadeCd;

        // DamageTool.explode (bounded): every zombie within radius takes zombieDamage * (1 - range/radius) -- LINEAR
        // falloff (Zombie.cs:270); the thrower (player) within radius takes playerDamage * (1 - (range/radius)^2) --
        // SQUARED falloff (Player.cs:1975). Out of radius = nothing. Walls block the blast (LoS) + worn clothing cuts it
        // (explosionArmor); vehicles take it too. Still no LIMB or buildable damage.
        public void Explode(Vector3 point, float radius, float zombieDamage, float playerDamage, float vehicleDamage)
        {
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    float range = z.GlobalPosition.DistanceTo(point);
                    if (range > radius) continue;
                    if (ExplosionBlocked(point, z.GlobalPosition)) continue;   // a wall between the blast and the zombie stops it (source LineOfSightTest)
                    bool wd = z.Dead;
                    z.DamageHit(ExplosionMath.Linear(zombieDamage, range, radius), z.GlobalPosition, (z.GlobalPosition - point).Normalized());
                    if (!wd && z.Dead) Kills++;
                }
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))   // source DamageTool.explode also damages vehicles (Grenade.dat Vehicle_Damage 100)
                if (n is Vehicle v && !v.Exploded)
                {
                    float range = v.GlobalPosition.DistanceTo(point);
                    if (range > radius) continue;
                    if (ExplosionBlocked(point, v.GlobalPosition)) continue;
                    v.TakeDamage(ExplosionMath.Linear(vehicleDamage, range, radius));   // linear falloff (port's simplified explosion model)
                }
            float pr = GlobalPosition.DistanceTo(point);
            if (pr <= radius && !ExplosionBlocked(point, GlobalPosition)) { float t = ExplosionMath.Squared(playerDamage, pr, radius); if (t > 0f) TakeDamage(t * (Inventory?.ExplosionArmor ?? 1f)); }   // wall blocks it (LoS) + worn clothing cuts it (source getPlayerExplosionArmor)
            PlayerRegistry.FlinchAllFromExplosion(point, Mathf.Max(radius * 2f, 12f), 30f);   // camera shake toward the blast (real Bomb effects ~16r/30mag)
            GD.Print($"[explode] r={radius} at {point}");
        }

        // Explosion line-of-sight (source ExplosionDamageParameters.LineOfSightTest): raycast from the blast to the target
        // on the WORLD/LOS-blocking layer -- a wall/terrain between them shields the target (no damage). Both ends raised to
        // chest height so the ray doesn't graze the ground; targets aren't on WorldLayer so only walls register.
        bool ExplosionBlocked(Vector3 point, Vector3 target)
        {
            Vector3 a = point + Vector3.Up * 0.8f, b = target + Vector3.Up * 0.8f;
            var q = PhysicsRayQueryParameters3D.Create(a, b, ZombieNav.WorldLayer);
            return GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0;
        }

        // Explosion camera shake -- src: EffectManager.cs:1615 -> PlayerLook.FlinchFromExplosion. A flinch rotation toward the
        // blast (axis = Cross(up, dir-from-blast-to-cam), in cam-local space) with EXPONENTIAL distance falloff 1-(dist/radius)^2;
        // magnitude in degrees from the explosion EffectAsset's CameraShake (real Bomb_* values: radius 6-32, mag 2-45).
        // (Explosion call sites reach every player through PlayerRegistry.FlinchAllFromExplosion -- the old
        // PlayerController.Local static is gone, MP_PLAN §5 item 7.)
        public void FlinchFromExplosion(Vector3 point, float radius, float magnitudeDegrees)
        {
            if (_cam == null) return;
            Vector3 rel = _cam.GlobalPosition - point;
            float dist = rel.Length();
            if (dist <= 0f || dist >= radius) return;                                   // outside the shake radius -> nothing
            Vector3 worldAxis = Vector3.Up.Cross(rel / dist).Normalized();
            Vector3 localAxis = (_cam.GlobalTransform.Basis.Inverse() * worldAxis).Normalized();
            float deg = magnitudeDegrees * (1f - (dist / radius) * (dist / radius));     // src exponential falloff
            if (localAxis.IsFinite() && Mathf.Abs(deg) > 0.01f)
                _flinch = (_flinch * new Quaternion(localAxis, Mathf.DegToRad(deg))).Normalized();   // rides the existing _flinch spring
        }

        // Throw a grenade from the muzzle (ItemThrowableAsset). Bounded first pass: a fixed throw arc, ~1 s between
        // throws, no inventory consumption yet (like the generic melee).
        public void ThrowGrenade()
        {
            if (_grenadeCd > 0f) return;
            _grenadeCd = 1.0f;
            Vector3 fwd = _cam != null ? -_cam.GlobalTransform.Basis.Z : -GlobalTransform.Basis.Z;
            Vector3 vel = fwd * 16f + Vector3.Up * 5f + Velocity;   // arc forward + inherit motion
            Vector3 origin = (_cam?.GlobalPosition ?? GlobalPosition) + fwd * 0.5f;
            if (NetGrenade != null)   // D1: the SERVER spawns/flies/explodes it (ProjectileReplicaView renders the flight)
            {
                if (vel.Length() > 47.5f) vel = vel.Normalized() * 47.5f;   // stay under the server's 48 m/s sanity cap (a sprint-throw must not get silently rejected)
                NetGrenade(origin, vel);
                GD.Print("[grenade] thrown (wire)");
                return;
            }
            var g = new Grenade { Thrower = this, Vel = vel };
            GetParent().AddChild(g);
            g.GlobalPosition = origin;
            GD.Print("[grenade] thrown");
        }

        StorageCrate _openCrate;

        // F: open the nearest storage crate within ~2.5 m -- loads its grid into the STORAGE page (7) so the existing
        // dashboard + TryDrag handle it, and opens the dashboard.
        static StoreShelf ShelfOf(Node n)   // walk up from a look-ray collider to its StoreShelf (the trimesh body is a grandchild of the shelf)
        {
            for (int i = 0; i < 4 && n != null; i++) { if (n is StoreShelf s) return s; n = n.GetParent(); }
            return null;
        }

        public bool OpenNearestCrate()
        {
            StorageCrate near = null; float best = 6.25f;   // 2.5 m, squared
            foreach (var n in GetTree().GetNodesInGroup("crates"))
                if (n is StorageCrate c && c is not StoreShelf)   // shelves/containers are LOOK-based (OpenCrate on the focused shelf), never proximity -- so a shelf behind you never opens
                {
                    float d = GlobalPosition.DistanceSquaredTo(c.GlobalPosition);
                    if (d < best) { best = d; near = c; }
                }
            return OpenCrate(near);
        }

        // open a specific container -- the shelf you're LOOKING at, or a nearby non-shelf crate: loads its grid into STORAGE page 7.
        public bool OpenCrate(StorageCrate crate)
        {
            if (crate == null) return false;
            if (crate is StoreShelf shelf) crate = shelf.ResolveSide(GlobalPosition);   // double-sided gondola: open the side the player is on
            // B9: a REPLICATED container (server-owned, NetId != 0) opens over the WIRE -- its local grid is only a
            // display mirror; the server holds the authoritative contents (StorageOpened + the owner echo carry them
            // into STORAGE page 7, and OnReplicatedStorageOpened opens the dashboard on the fact, never on the send).
            if (crate.NetId != 0 && NetOpenStorage != null)
                return RequestOpenStorage(crate.NetId);
            var near = crate;
            _openCrate = near;
            CopyPage(near.Storage, Inventory.items[PlayerInventory.STORAGE], near.Width, near.Height);
            (near as StoreShelf)?.BeginLiveDisplay(Inventory.items[PlayerInventory.STORAGE]);   // live-update the shelf models as the grid is edited (not just on close)
            GD.Print($"[crate] opened ({near.Storage.getItemCount()} items)");
            _invUI?.Open();
            Input.MouseMode = Input.MouseModeEnum.Visible;
            return true;
        }

        // save the open crate's contents back and clear the STORAGE view (called when the dashboard closes)
        void CloseCrate()
        {
            if (NetCloseStorage != null && _openCrateNetId != 0)
            {
                // MP: the server saves the STORAGE page back into the crate and clears it; the owner
                // echo empties the local view (no local copy-back -- the crate grid is the server's).
                NetCloseStorage(); _openCrateNetId = 0;
                return;
            }
            // GAP B1: a NON-replicated crate (_openCrateNetId==0 -- a look-opened / SP-local shelf that was
            // never routed over the wire, so OnReplicatedStorageOpened never latched a NetId) FALLS THROUGH to
            // the local copy-back below. Without this guard the net branch above returned early on NetId==0 and
            // the edited STORAGE page was silently dropped (item-loss on close).
            if (_openCrate == null) return;
            CopyPage(Inventory.items[PlayerInventory.STORAGE], _openCrate.Storage, _openCrate.Width, _openCrate.Height);
            (_openCrate as StoreShelf)?.EndLiveDisplay();   // stop mirroring page 7; final sync from the written-back grid
            var s = Inventory.items[PlayerInventory.STORAGE];
            s.clear(); s.loadSize(0, 0);
            _openCrate = null;
        }

        // MP storage state (wired only by ClientWorldSession): the crate the SERVER says we have open.
        // Latched on the StorageOpened fact -- never on the request -- so the dashboard mirrors arbitration.
        uint _openCrateNetId;
        public bool DashboardOpen => _invUI?.IsOpen ?? false;   // L1 net tests: did the storage fact open the dashboard
        public void DebugCloseCrate() => CloseCrate();          // L1 net tests: the ESC/Tab crate-close path without an InputEvent

        /// <summary>StorageOpened landed (server-validated): latch the crate + open the dashboard. The
        /// CRATE grid itself arrives via the owner-block echo (the server loads it into STORAGE page 7,
        /// the SP OpenNearestCrate mechanic), so there's nothing to copy here.</summary>
        public void OnReplicatedStorageOpened(uint netId)
        {
            _openCrateNetId = netId;
            _invUI?.Open();
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }

        /// <summary>StorageClosed landed (ours or a server-side force-close): drop the latch; the echo
        /// clears the STORAGE page.</summary>
        public void OnReplicatedStorageClosed() => _openCrateNetId = 0;

        static void CopyPage(SDG.Unturned.Items from, SDG.Unturned.Items to, byte w, byte h)
        {
            to.clear(); to.loadSize(w, h);
            for (byte i = 0; i < from.getItemCount(); i++)
            {
                var j = from.getItem(i);
                to.addItem(j.x, j.y, j.rot, j.item);
            }
        }

        /// <summary>MP (wired only by ClientWorldSession): copy the replicated owner-block grid INTO the
        /// shell's EXISTING Inventory instance -- never swap the reference (InventoryUI/CraftingUI, the
        /// reload mag hunt, and the armor math all hold it). Worn refs first (direct field writes -- the
        /// wearX helpers would RESIZE and wipe the pages), then every page cell-for-cell; the page sizes
        /// come off the wire, so worn-bag grids stay right even before asset resolution. The replica entry
        /// is rebuilt fresh per snapshot (InventoryReplication.ReadSnapshot), so adopting its jars shares
        /// nothing with the server grid. InventoryUI's signature poll repaints on its next _Process (its
        /// !_dragging guard already protects a mid-drag).</summary>
        public void AdoptReplicatedInventory(PlayerInventory replica)
        {
            if (replica == null || Inventory == null || ReferenceEquals(replica, Inventory)) return;
            Inventory.wornHat = replica.wornHat; Inventory.wornGlasses = replica.wornGlasses; Inventory.wornMask = replica.wornMask;
            Inventory.wornShirt = replica.wornShirt; Inventory.wornVest = replica.wornVest;
            Inventory.wornBackpack = replica.wornBackpack; Inventory.wornPants = replica.wornPants;
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                var from = replica.items[p];
                CopyPage(from, Inventory.items[p], from.width, from.height);
            }
        }
        /// <summary>MP (called only by ClientWorldSession, each tick): mirror the replicated owner skills
        /// block into the shell's local PlayerSkills -- the AdoptReplicatedInventory analogue. The skill
        /// multipliers (recoil/stamina/crafting gates) all read this instance, so the server's levels/XP
        /// drive them; SkillsUI repaints off its MP signature poll.</summary>
        public void AdoptReplicatedSkills(SDG.Unturned.PlayerSkills replica)
        {
            if (replica == null || ReferenceEquals(replica, Skills)) return;
            Skills.NetSetExperience(replica.experience);
            for (int s = 0; s < SDG.Unturned.PlayerSkills.SPECIALITIES; s++)
            {
                var from = replica.skills[s]; var to = Skills.skills[s];
                for (int i = 0; i < to.Length && i < from.Length; i++) to[i].level = from[i].level;
            }
        }

        // ---- P3a (SP/MP-unify): server-authoritative HP adoption. When the owner's health is server-owned,
        // the replicated PlayerCombatReplication coarse Health (0..100 byte, SystemId 2) is the ONLY writer of
        // the shell's HP -- the AdoptReplicatedInventory/Skills analogue. Local regen/starve/fall/zombie damage
        // can't move it (those damage sources route server-side in P3b); death + respawn are driven off the
        // server's PlayerDied/PlayerRespawned facts via NetDie()/NetRespawn(). Wired ONLY by ClientWorldSession
        // (MP shell) + MpLoopback --spconsume; null in default SP so vitals stay local + byte-identical. ----
        public bool NetVitalsAdopted { get; private set; }
        float _netAdoptedHealth = 100f;   // the coarse server HP the shell is pinned to while adopting

        /// <summary>P3b (SP/MP-unify): server-side damage routing for a body whose HP is server-authoritative.
        /// When set, an incoming TakeDamage (zombie melee/acid, vehicle/deployable blast, and on the loopback
        /// listen-server host its own fall/OOB) is FORWARDED to the server sink (ServerCombat.DamagePlayerExternal)
        /// instead of moving local HP. The follower NetAvatar body (PlayerNetSync) and the loopback host shell
        /// (MpLoopback --spconsume) wire this. Null in default SP AND on a true MP client shell (whose local
        /// TakeDamage no-ops via NetVitalsAdopted and whose fall/OOB are SERVER-derived from its state claims),
        /// so those paths stay byte-identical.</summary>
        public System.Action<float> NetDamageSink;

        // P3b (review finding 5): the 1-3 tick spawn window before the first AdoptReplicatedVitals latches
        // NetVitalsAdopted -- a fall/starvation death firing there would run the LOCAL death path and fight the
        // server clock. Set at shell spawn (ClientWorldSession/MpLoopback) so local death is suppressed until
        // adoption is confirmed. Never set in default SP, so that path is byte-identical.
        bool _pendServerVitals;
        public void ExpectServerVitals() => _pendServerVitals = true;

        /// <summary>MP/loopback owner: mirror the owner's replicated CombatEntity coarse health (0..100 byte)
        /// into the shell's vitals, re-asserted as the LAST writer each tick (UpdateVitals re-pins to it,
        /// TakeDamage no-ops), so nothing local moves HP while the server owns it. MaxHealth is the source 100.
        /// v1 grain note: the coarse byte is +-1 HP -- fine for the HUD's Player.Health read; a fine owner-only
        /// vitals block (exact float, sub-HP) is a later interest-block refinement, not needed for the gate.</summary>
        public void AdoptReplicatedVitals(int coarseHealth)
        {
            NetVitalsAdopted = true;
            MaxHealth = 100f;
            _netAdoptedHealth = Mathf.Clamp(coarseHealth, 0, 100);
            Health = _netAdoptedHealth;   // apply immediately -- the HUD may read Player.Health at any point
        }

        // ---- B5 (SP/MP-unify): server-authoritative FINE vitals (food/water/stamina/infection). The owner-
        // only SystemVitals(13) block is the sole writer of these while adopted -- UpdateVitals skips the local
        // PlayerVitalsSim.Step fine mutation (which was the shipped bug: it drained food to 0 locally while the
        // `died` result was discarded and the server ran no hunger sim). Wired ONLY by ClientWorldSession (MP
        // shell) + MpLoopback --spconsume; null in default SP so vitals stay local + byte-identical. HP is a
        // SEPARATE authority (the coarse CombatEntity byte via AdoptReplicatedVitals); this never touches it.
        public bool NetFineVitalsAdopted { get; private set; }

        /// <summary>Mirror the owner's replicated fine vitals into the shell each tick (the AdoptReplicatedVitals
        /// analogue). Stamina is server-owned but the SPRINT decision stays client-auth -- the shell reads this
        /// adopted Stamina to gate sprint, and the server derives `sprinting` from the adopted stance (a few
        /// ticks of lag, like HP adoption). Bleeding/Broken ride the wire but the server has no source yet, so
        /// they are NOT clobbered here (they'd only ever wipe a locally-meaningful flag to false).</summary>
        public void AdoptReplicatedFineVitals(float food, float water, float stamina, float infection)
        {
            NetFineVitalsAdopted = true;
            Food = Mathf.Clamp(food, 0f, 1f);
            Water = Mathf.Clamp(water, 0f, 1f);
            Stamina = Mathf.Clamp(stamina, 0f, 1f);
            Infection = Mathf.Clamp(infection, 0f, 1f);
        }

        // Server-owned death/respawn while adopting: the shell renders the SP death corpse/cam + respawn
        // visuals, but the SERVER owns the 3.5 s clock (the local _deathTimer self-respawn is disabled) and the
        // respawn REPOSITION rides the recov/freeze-until-echo primitive (a bare GlobalPosition write is
        // overwritten by the client-auth owner's next PlayerStateCommand), never a local teleport here.
        bool _serverOwnedRespawn;
        public bool IsDead => _dead;   // L1 net tests: did the server death fact render on the owner

        /// <summary>Server-authoritative death (PlayerDiedEvent for self): render the local Die() corpse +
        /// death-cam, but disable the local self-respawn clock -- the server owns the timer and drives
        /// NetRespawn() off PlayerRespawnedEvent. Idempotent (a re-broadcast is a no-op).</summary>
        public void NetDie()
        {
            if (_dead) return;
            Health = 0f;
            _serverOwnedRespawn = true;
            Die();
        }

        /// <summary>Server-authoritative respawn (PlayerRespawnedEvent for self): the SP Respawn() visuals
        /// (clear corpse, restore cam + vitals). reposition=false for the client-auth MP shell -- the move to
        /// SpawnPos rides the server's PlayerRecovEvent (freeze-until-echo), because a GlobalPosition write is
        /// clobbered by the shell's next state claim. reposition=true for the loopback listen-server, where the
        /// node IS the authority (ServerDrive reads it) so it repositions itself to its local Spawn.</summary>
        public void NetRespawn(bool reposition)
        {
            if (!_dead) return;
            _serverOwnedRespawn = false;
            Respawn(reposition);
        }

        public Vector3 Spawn = new Vector3(0, 1f, 0);

        // Zombie sensing (AlertTool/PlayerStance): Agro increments once per zombie that starts hunting this
        // player -- it drives their approach path (every 3rd zombie RUSHes, the rest split left/right, so a
        // horde fans out to surround). Moving/Stance feed the stealth detection radius below.
        public int Agro;
        public bool Moving { get; private set; }
        public EPlayerStance Stance => _move.Stance;
        float _footNoiseT;   // Phase 3 hearing: throttle the continuous footstep-noise emit (~2.5x/s while moving)

        // Port of PlayerStance.GetStealthDetectionRadius: the radius (m) within which a zombie can sense this
        // player, by stance -- standing 12, crouched 6, sprinting 20, prone 3, x1.1 while moving. AlertTool
        // clamps it to [1, 64]. Crouch-walking (or crawling prone) is how you sneak past a horde.
        public float GetStealthDetectionRadius()
        {
            if (IsDriving) return StealthDetection.DrivingRadius(_driving.ForwardSpeedPct());   // source DRIVING: DETECT_FORWARD(48) * fwd-speed% -> loud at speed, ~silent when parked
            return StealthDetection.Radius(_move.Stance, Moving);   // the DETECT_* table lives in core/UnturnedSim/CombatMath.cs (L0-tested)
        }

        // When set (e.g. by a recorded demo or a net-driven bot), overrides keyboard input: x=strafe, y=forward.
        public UnityEngine.Vector2? ScriptedInput;
        // The move axes THIS shell captured on its last physics tick (x=strafe, y=forward) -- what the
        // MP loopback/client host sends as MoveInput so the wire carries exactly what the sim consumed.
        public UnityEngine.Vector2 LastMoveInput;
        // The jump input the sim consumed on that same tick (post Broken-gate, same as the axes) -- the
        // C3 client session sends it as the MoveInput v2 jump bit, so the wire carries exactly what the
        // local shell jumped on (a Broken shell that can't jump locally must not jump on the server).
        public bool LastJumpInput;

        // Net-session position seams (§7 risk 5). The shell does MANUAL render interp: _PhysicsProcess
        // RESTORES GlobalPosition from _interpCurr before moving and _Process lerps _interpPrev..
        // _interpCurr every frame -- so a bare GlobalPosition write from the net session is silently
        // overwritten one tick later and never renders. Net code must therefore read the TRUE physics
        // position (not the render-lerped GlobalPosition) and move the node through TeleportTo, which
        // shifts the interp samples WITH it.
        public Vector3 TruePhysicsPosition => _interpReady ? _interpCurr : GlobalPosition;

        /// <summary>The movement-sim's carried state (horizontal components are re-derived from input
        /// every Step; y is the ballistic DOF). Rides the v9 state stream; a recov re-seeds it.</summary>
        public UnityEngine.Vector3 MoveSimVelocity => _move.Velocity;

        // ---- mp-clientauth-foot seams (wire v9): the shell OWNS its on-foot movement and REPORTS it;
        // the server envelope-validates and adopts. These are the report/rollback/follower hooks --
        // only ClientWorldSession + PlayerNetSync call them; SP never constructs those paths. ----

        /// <summary>The grounded state the movement sim consumed on the last physics tick -- rides the
        /// state stream as dressing, like LastMoveInput/LastJumpInput.</summary>
        public bool LastGroundedInput;
        /// <summary>Look pitch in degrees (the camera's X rotation) -- state-stream dressing.</summary>
        public float LookPitchDegrees => _pitchDeg;

        /// <summary>Server rollback (PlayerRecovEvent): call after TeleportTo(last-good) -- re-seed the
        /// sim's carried velocity (y = the ballistic DOF; horizontal re-derives from input next tick).</summary>
        public void NetRecovRestore(UnityEngine.Vector3 simVelocity) => _move.Velocity = simVelocity;

        /// <summary>Follower-body hold (PlayerNetSync): the server body no longer integrates ANY movement
        /// -- the owner's client owns it, the entity carries the adopted claim, and the sync teleports
        /// this body onto it every tick. Skips the whole movement tail of _PhysicsProcess.</summary>
        public bool NetHold;

        /// <summary>Dress the held follower from the adopted claim: stance drives the hitbox capsule (a
        /// crouched player must be HIT as crouched) and the zombie stealth radius; moving feeds the
        /// radius's x1.1 modifier.</summary>
        public void NetHoldPose(EPlayerStance stance, bool moving)
        {
            _move.Stance = stance;
            UpdateHitbox(stance);
            Moving = moving;
        }

        // Likewise forces the stance (bypassing the Shift/Ctrl/Z keys) for demos, bots, and self-tests.
        public EPlayerStance? ScriptedStance;
        // Likewise forces jump (bypassing Space) -- PlayerNetSync injects the MoveInput v2 jump bit here.
        public bool? ScriptedJump;

        // C6 MP RIDE MODE (PEI_CLIENT_PLAN §3 C6 / MP_PLAN §3.6 v1): driving a REPLICATED vehicle -- a
        // mesh-only VehiclePuppet the server dead-reckons, not a local Vehicle. Session-only: SP never
        // wires the callbacks and never seats a puppet, so _riding stays null and every riding guard
        // below is inert (the direct _driving path is untouched). While riding, the shell hides/freezes
        // exactly like SP driving, captures WASD/space as drive INTENT (LastDriveInput), and the session
        // streams it to the server (SendDriveInput @50 Hz); the server's Vehicle node does the physics.
        VehiclePuppet _riding;
        public bool IsRiding => _riding != null;
        public VehiclePuppet RidingPuppet => _riding;
        public UnityEngine.Vector2 LastDriveInput;   // captured while riding: x=steer, y=throttle (the DriveVehicle axes)
        public bool LastHandbrakeInput;
        public System.Action<uint> NetEnterVehicle;  // wired by ClientWorldSession: F near a puppet asks the server for the seat
        public System.Action NetExitVehicle;         // F while riding asks the server to free it (exit teleport follows)

        // D1 MP combat routing seams (PEI_COMBAT_PLAN §3 D1) -- the NetEnterVehicle pattern: wired ONLY by
        // ClientWorldSession, null in SP/loopback so every direct combat path below stays byte-identical.
        // When set, the trigger pull still plays ALL its local fx (recoil/muzzle/tracer/casings/swing anim)
        // but authority moves to the wire: bullets go cosmetic (no damage, no impact fx -- those render from
        // the server's ImpactFx/HitConfirmed events), melee/grenade intent is sent instead of resolved.
        public System.Action<Vector3, Vector3> NetFire;      // (muzzle, undeviated aim axis) -> Client.SendFire
        public System.Action<int, float> NetDamageObject;    // (destructibleIndex, objectDamage) -> the authoritative ServerDestructibles in the loopback. In SP the local bullet path (StepBullets) owns hits, but destructible HEALTH is server-owned (ServerDestructibles mirrors the alive-bit back onto the field), so a local prop hit must route THERE, not break the field locally (a local break gets reverted by the next mirror tick). Null in pure --direct SP (no server) -> props inert there (documented fallback).
        public System.Action<bool, float> NetMelee;          // (strong, yawDegrees) -> Client.SendMelee
        public System.Action<Vector3, Vector3> NetGrenade;   // (origin, velocity) -> Client.SendGrenade
        public System.Action NetReload;                      // -> Client.SendReload (server ammo/reload clock tracks the local one)
        public System.Action<uint> NetPickupItem;            // wired by ClientWorldSession: F on a focused WorldItemPuppet asks the server for the item (Client.SendPickupItem)

        // Phase 6/8 client-shell seams (mp-parity-clientseams) -- the NetPickupItem pattern, one per UI
        // action the server already validates: wired ONLY by ClientWorldSession, null in SP/loopback so
        // every direct mutation below stays byte-identical. When set, the action site sends INTENT and
        // skips its local mutation -- the owner-block echo / broadcast fact renders the result (the
        // client never re-packs its own bag, plants its own generator, or levels its own skill).
        public System.Action<byte, byte, byte, byte, byte, byte, byte> NetMoveItem;   // (page0,x0,y0, page1,x1,y1, rot1) -> Client.SendMoveItem
        public System.Action<byte, byte, byte, byte> NetEquipItem;   // (fromPage,x,y, slot) -> Client.SendEquipItem (the holster-to-hand-slot TryDrag; the viewmodel equip stays local)
        public System.Action<byte, byte, byte> NetDropItem;          // (page,x,y) -> Client.SendDropItem (server removes + tosses the world item)
        public System.Action<byte, byte, byte> NetConsume;           // (page,x,y) -> Client.SendConsume (server deletes the item; vitals stay client-led until the vitals split)
        public System.Action<ushort> NetCraft;                       // blueprintIndex (BlueprintRegistry.All order, content-hash-matched) -> Client.SendCraft
        public System.Action<ushort, Vector3, float> NetPlaceDeployable;   // (defId,pos,yaw) -> Client.SendPlaceDeployable (server spends the item + broadcasts; the replica view renders it)
        public System.Action<uint> NetSalvageDeployable;             // -> Client.SendSalvageDeployable (removal echoes back through the replica view)
        public System.Action<uint> NetPickupDeployable;              // B2: -> Client.SendPickupDeployable (the removal + owner-inventory echo return the item; the replica view retires the node)
        public System.Action<uint> NetExtractFuel;                   // A2: pumpNetId -> Client.SendExtractFuel (server drains the shared station tank into the held can; owner echo re-adopts the fuller can)
        public System.Action<uint, uint> NetAttachTow;               // B11: (towerNetId, towedNetId) -> Client.SendAttachTow; the committed rope echoes back via A6's replicated TowedNetId (never mutated client-side)
        public System.Action<uint> NetDetachTow;                     // B11: netId (either end) -> Client.SendDetachTow; the cleared relationship echoes back via A6's TowedNetId->0
        public System.Action<uint, byte, uint, byte> NetConnectWire; // (srcId,srcPort, dstId,dstPort) -> Client.SendConnectWire
        public System.Action<uint> NetRemoveWire;                    // wireId -> Client.SendRemoveWire
        public System.Action<uint, bool> NetToggleDeployable;        // (netId,on) -> Client.SendToggleDeployable (NetSetPowered lands the echo)
        public System.Action<uint> NetOpenStorage;                   // crate netId -> Client.SendOpenStorage (StorageOpened + the owner echo carry the grid back)
        public System.Action NetCloseStorage;                        // -> Client.SendCloseStorage (server saves the STORAGE page back into the crate)
        public System.Action<byte, byte> NetUpgradeSkill;            // (speciality,index) -> Client.SendUpgradeSkill
        // A4 (SP/MP-unify) crop seams -- the NetPickupItem pattern: wired ONLY by ClientWorldSession, null in
        // SP/loopback so the direct CropManager path below stays byte-identical. Plant routes seed+point;
        // harvest routes the grown replica's server NetId (the yield drops as a replicated world item).
        public System.Action<ushort, Vector3> NetPlantCrop;          // (seedId, worldPos) -> Client.SendPlantCrop
        public System.Action<uint> NetHarvestCrop;                   // grown crop NetId -> Client.SendHarvestCrop

        VehiclePuppet NearestPuppet()
        {
            if (NetEnterVehicle == null) return null;   // not an MP shell -> no puppets to consider (SP fast-out)
            VehiclePuppet best = null; float bestD = 4.0f * 4.0f;   // the NearestVehicle prompt range
            foreach (var n in GetTree().GetNodesInGroup("vehicle_puppets"))
                if (n is VehiclePuppet p && IsInstanceValid(p))
                {
                    float d = GlobalPosition.DistanceSquaredTo(p.GlobalPosition);
                    if (d < bestD) { bestD = d; best = p; }
                }
            return best;
        }

        /// <summary>The C6 interact seam: ask the server for the seat of the nearest replicated vehicle
        /// (~4 m, the SP NearestVehicle range). The server validates reach/occupancy/alive at the §2.3
        /// choke point; the seat lands via the VehicleEntered fact -> EnterPuppet. False when not an MP
        /// shell or nothing is near, so F falls through to the next interaction.</summary>
        public bool RequestEnterNearestPuppet()
        {
            var p = NearestPuppet();
            if (p == null) return false;
            NetEnterVehicle(p.NetId);
            return true;
        }

        /// <summary>The MP pickup interact seam: F while LOOKING AT a replicated dropped item asks the
        /// server for it. Focus-driven like SP pickup (UpdateLookFocus already sets _focusPuppet from the
        /// eye-ray), unlike the proximity-driven vehicle enter. False when not an MP shell or the focus
        /// isn't an item puppet, so F falls through to the next interaction.</summary>
        public bool RequestPickupFocusedPuppet() => _focusPuppet is WorldItemPuppet wp && RequestPickupPuppet(wp);

        /// <summary>The request itself -- a REQUEST only, no local state changes: the pickup lands when the
        /// server's WorldItemRemoved + owner-block echo come back (or ItemPickupDenied keeps the item).
        /// Public + puppet-typed so the L1 net tests can drive it without the focus raycast (the same
        /// pattern net.shell_drive uses to drive ride mode).</summary>
        public bool RequestPickupPuppet(WorldItemPuppet wp)
        {
            if (NetPickupItem == null || wp == null || !IsInstanceValid(wp)) return false;
            NetPickupItem(wp.NetId);
            return true;
        }

        /// <summary>F while seated in MP: ask the server to free the seat -- ride mode (exit lands via
        /// VehicleExited -> ExitPuppet) or Part A predicted driving (-> ExitVehicleAt). The client never
        /// unseats itself; the SP direct ExitVehicle stays for pure-SP driving only.</summary>
        public bool RequestExitPuppet()
        {
            if (NetExitVehicle == null) return false;
            if (_riding == null && !DrivingPredicted) return false;
            NetExitVehicle();
            return true;
        }

        /// <summary>A4 MP harvest interact seam (the RequestPickupPuppet pattern): F near a GROWN replicated
        /// crop (~3 m, the SP CropManager.NearestGrown reach) asks the server to harvest it. Scans the "crop"
        /// group for the nearest grown NetId!=0 -- the CropReplicaView stamps both, so a SP direct CropNode
        /// (NetId 0, growth via PlantedCrop) is never matched here and a joined client (no CropManager) has
        /// only replicas. A REQUEST only, no local mutation: the crop despawns + the yield world item appear
        /// when the server's CropHarvested + WorldItem facts come back. Public so the L1 tests drive it
        /// without the F raycast. False when not an MP shell or nothing grown is near -> F falls through.</summary>
        public bool RequestHarvestNearestCrop(float reach = 3.0f)
        {
            if (NetHarvestCrop == null) return false;   // not an MP shell -> no replicated crops (SP fast-out)
            CropNode best = null; float bestD = reach * reach;
            foreach (var n in GetTree().GetNodesInGroup("crop"))
                if (n is CropNode c && IsInstanceValid(c) && c.Grown && c.NetId != 0)
                {
                    float d = GlobalPosition.DistanceSquaredTo(c.GlobalPosition);
                    if (d < bestD) { bestD = d; best = c; }
                }
            if (best == null) return false;
            NetHarvestCrop(best.NetId);
            return true;
        }

        // ---- Phase 6/8 request helpers (the RequestPickupPuppet pattern): PUBLIC so the UI action
        // sites AND the L1 net tests drive the same seam without a mouse/raycast. Each is a REQUEST
        // only -- no local state changes; false = not an MP shell, so the caller runs its SP path. ----

        /// <summary>MP grid move (InventoryUI drag-drop): the server's TryDrag is the validator+applier;
        /// the owner-block echo repaints the bag.</summary>
        public bool RequestMoveItem(byte page0, byte x0, byte y0, byte page1, byte x1, byte y1, byte rot1)
        {
            if (NetMoveItem == null) return false;
            NetMoveItem(page0, x0, y0, page1, x1, y1, rot1);
            return true;
        }

        /// <summary>MP holster-to-slot (InventoryUI Equip): the server runs the same TryDrag into the
        /// hand slot; the in-hand viewmodel equip stays local at the call site.</summary>
        public bool RequestEquipItem(byte fromPage, byte x, byte y, byte slot)
        {
            if (NetEquipItem == null) return false;
            NetEquipItem(fromPage, x, y, slot);
            return true;
        }

        /// <summary>MP drop (InventoryUI Drop): the server removes the jar + tosses the world item; the
        /// echo empties the cell and the item puppet renders the drop.</summary>
        public bool RequestDropItem(byte page, byte x, byte y)
        {
            if (NetDropItem == null) return false;
            NetDropItem(page, x, y);
            return true;
        }

        /// <summary>MP consume (InventoryUI Use button): the server deletes the item by id (the cell just
        /// names one) + applies useHealth into the server CombatEntity; the owner echo empties/decrements the
        /// cell. Mirrors TickConsume's completion routing -- vitals stay client-led (AdoptReplicatedVitals owns
        /// HP), so the caller still applies Consume(asset) locally and skips its own decrement when this routes.</summary>
        public bool RequestConsume(byte page, byte x, byte y)
        {
            if (NetConsume == null) return false;
            NetConsume(page, x, y);
            return true;
        }

        /// <summary>MP deployable placement (TickDeploy's place-confirm): the server validates the spot +
        /// spends the item; DeployablePlaced broadcasts and the replica view spawns the real node.</summary>
        public bool RequestPlaceDeployable(ushort defId, Vector3 pos, float yawDeg)
        {
            if (NetPlaceDeployable == null) return false;
            NetPlaceDeployable(defId, pos, yawDeg);
            return true;
        }

        /// <summary>MP generator toggle (the F interact): only a REPLICATED node (NetId != 0) routes over
        /// the wire -- the echo lands via DeployableReplicaView.NetSetPowered.</summary>
        public bool RequestToggleDeployable(Deployable d)
        {
            if (NetToggleDeployable == null || d == null || !IsInstanceValid(d) || d.NetId == 0) return false;
            NetToggleDeployable(d.NetId, !d.PoweredTarget);
            return true;
        }

        /// <summary>MP wire link (CompleteWire): both endpoints must be replicated nodes; the port
        /// sub-address is the def port order (the replica view's mapping). The committed wire renders
        /// only when WireConnected echoes back.</summary>
        public bool RequestConnectWire(ConnectionPort src, ConnectionPort dst)
        {
            if (NetConnectWire == null || src == null || dst == null) return false;
            var so = src.Owner; var co = dst.Owner;
            if (so == null || co == null || so.PowerNetId == 0 || co.PowerNetId == 0) return false;   // a world fixture (gas pump) has NetId 0 -> SP local wire, no server request
            int si = PortIndexOf(so.PowerPorts, src), di = PortIndexOf(co.PowerPorts, dst);
            if (si < 0 || di < 0) return false;
            NetConnectWire(so.PowerNetId, (byte)si, co.PowerNetId, (byte)di);
            return true;
        }
        static int PortIndexOf(System.Collections.Generic.IReadOnlyList<ConnectionPort> ports, ConnectionPort p)
        {
            for (int i = 0; i < ports.Count; i++) if (ports[i] == p) return i;
            return -1;
        }

        /// <summary>MP wire removal (the RMB clear/unplug manage actions): the wire node vanishes when
        /// WireRemoved echoes through the replica view.</summary>
        public bool RequestRemoveWire(Wire w)
        {
            if (NetRemoveWire == null || w == null || !IsInstanceValid(w) || w.NetId == 0) return false;
            NetRemoveWire(w.NetId);
            return true;
        }

        /// <summary>MP storage open: a REQUEST -- the dashboard opens only when the server's
        /// StorageOpened fact comes back (OnReplicatedStorageOpened), never on the send.</summary>
        public bool RequestOpenStorage(uint netId)
        {
            if (NetOpenStorage == null) return false;
            NetOpenStorage(netId);
            return true;
        }

        /// <summary>MP skill upgrade (SkillsUI): the server's PlayerSkills.TryUpgrade is the validator;
        /// the owner skills block echoes the new level/XP into AdoptReplicatedSkills.</summary>
        public bool RequestUpgradeSkill(byte speciality, byte index)
        {
            if (NetUpgradeSkill == null) return false;
            NetUpgradeSkill(speciality, index);
            return true;
        }

        /// <summary>Seat CONFIRMED (VehicleEntered for self): the SP EnterVehicle local effects, minus the
        /// vehicle-side ones (engine/fuel/HUD box live on the server's Vehicle node, not the puppet).</summary>
        public void EnterPuppet(VehiclePuppet pup)
        {
            _riding = pup;
            _rideLookYaw = 0f; _rideLookPitch = FpRideGazePitchDeg;   // FP free-look starts at the classic forward gaze (#37)
            _burstLeft = 0;                                    // entering cancels an in-progress burst, like SP
            _viewmodel?.SetShown(false);
            if (_cam != null) _cam.TopLevel = true;            // free the camera into world space (chase cam)
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = true;   // the hidden shell must not shove the world
            Visible = false;
            Velocity = Vector3.Zero;
            LastDriveInput = UnityEngine.Vector2.zero;
            LastHandbrakeInput = false;
        }

        /// <summary>Seat FREED (VehicleExited for self): restore the shell at the server's exit teleport
        /// spot (the session computes it from the replica + terrain-snaps it, §7 risk 6).</summary>
        public void ExitPuppet(Vector3 exitPos)
        {
            _riding = null;
            GlobalPosition = exitPos;
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = false;
            Visible = true;
            Velocity = Vector3.Zero;
            _viewmodel?.SetShown(true);
            if (_cam != null) { _cam.TopLevel = false; _cam.Position = new Vector3(0f, 1.6f, 0f); _cam.Rotation = Vector3.Zero; }
            _pitchDeg = 0f;
            LastDriveInput = UnityEngine.Vector2.zero;
            LastHandbrakeInput = false;
        }

        /// <summary>Test seams (L1): set the vehicle-cam look angles directly. The live path is mouse motion in
        /// _UnhandledInput, which a headless host can't deliver (Input.MouseMode never reads Captured without a
        /// real display), so the ride-cam tests drive these and assert the camera consumes them.</summary>
        public void DebugSetRideLook(float yawDeg, float pitchDeg) { _rideLookYaw = yawDeg; _rideLookPitch = pitchDeg; }
        public void DebugSetDriveOrbit(float yawDeg, float pitchDeg) { _driveCamYaw = yawDeg; _driveCamPitch = pitchDeg; }

        // The DriveVehicle shape for a puppet seat: capture drive INTENT only (the session streams it;
        // the server's Vehicle node does the physics) and ride along with the dead-reckoned puppet.
        void RidePuppet()
        {
            if (!IsInstanceValid(_riding)) return;   // puppet retired mid-ride (despawn) -- hold; the VehicleExited fact restores the shell
            float throttle, steer;
            if (ScriptedDrive.HasValue) { steer = ScriptedDrive.Value.X; throttle = ScriptedDrive.Value.Y; }
            else if (UiInputBlocked) { throttle = 0f; steer = 0f; }   // menu open -> don't steer/accelerate through it
            else
            {
                throttle = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
                steer = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            }
            LastDriveInput = new UnityEngine.Vector2(steer, throttle);
            LastHandbrakeInput = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Space);
            GlobalPosition = _riding.GlobalPosition;   // ride along so the exit fallback + 3P seated body land at the vehicle
        }

        // Headless SERVER AVATAR construction (PEI_CLIENT_PLAN §2.3 / C2): a remote peer's body on the
        // dedicated world. Keeps the capsule / MoveAndSlide / floor tuning / PlayerRegistry registration +
        // the Scripted* input seams; skips the whole client-only subtree (camera-current, viewmodel,
        // inventory/craft/skills UIs, OutlineOverlay, BuildTool, demo inventory, mouse capture) and NEVER
        // reads global Input.* (a headless server has none; L1 test hosts have a window whose input must
        // not leak into avatars). Set at construction, before AddChild. Default false = SP byte-identical.
        public bool NetAvatar;

        void UpdateHitbox(EPlayerStance stance)   // collision capsule per stance (STAND 2 / CROUCH 1.2 / PRONE 0.8), bottom pinned to the feet
        {
            float h = PlayerMovementDef.HeightForStance(stance);
            if (Mathf.Abs(h - _capStance) < 0.001f) return;
            _capStance = h; _capsule.Height = h; _hitbox.Position = new Vector3(0f, h / 2f, 0f);
        }

        const float StepHeight = 0.5f;   // curbs/thresholds up to this high are stepped over (master: stop snagging on sidewalks; bumped 0.4->0.5)
        // If the horizontal motion is blocked at foot level but clear a step higher, raise onto the step; FloorSnapLength then
        // pulls us back down onto it. Reused by both the player and zombies (source has stair/ledge handling in PlayerMovement).
        void StepUp(float delta, bool grounded)
        {
            if (!grounded) return;
            Vector3 motion = new Vector3(Velocity.X, 0f, Velocity.Z) * delta;
            if (motion.LengthSquared() < 1e-6f) return;
            if (!TestMove(GlobalTransform, motion)) return;   // not blocked at foot level
            var raised = new Transform3D(GlobalTransform.Basis, GlobalPosition + Vector3.Up * StepHeight);
            if (TestMove(raised, motion)) return;             // blocked even raised: a wall, not a step
            GlobalPosition += Vector3.Up * StepHeight;
        }


        bool HeadroomFor(float height)   // is there space to occupy a taller capsule? (blocks standing up under a ceiling -- master)
        {
            // LENIENCY (master): skip the bottom `foot` metres + slim the probe, so the FLOOR under the player (which the
            // capsule would otherwise clip) isn't mistaken for a ceiling. Only a genuine low overhead blocks standing now.
            const float foot = 0.25f;
            float h = Mathf.Max(0.1f, height - foot);
            var q = new PhysicsShapeQueryParameters3D
            {
                Shape = new CapsuleShape3D { Height = h, Radius = 0.30f },
                Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * (foot + h / 2f)),
                CollisionMask = CollisionMask,
                Exclude = new Godot.Collections.Array<Rid> { GetRid() },
            };
            return GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count == 0;
        }
        public bool CaptureMouse = true;

        public GunDef Gun;          // real ItemGunAsset stats (damage/range/firerate/mag) when loaded
        float _fireCd;              // seconds until the gun can fire again
        const float GunshotRadius = 48f;   // earshot of an unsuppressed shot (AlertTool noise); suppressors would cut it
        bool _reloading;            // reloading -> can't fire; magazine refills when the timer elapses
        double _reloadTimer;
        // Per-shot rechamber (bolt/pump). After a shot the action must cycle (bolt-cycle / pump) before firing/reloading again.
        bool _needsRechamber;        // fired -> awaiting the cycle (source needsRechamber: blocks fire/aim/reload/inspect)
        bool _rechambering;          // true while the bolt-cycle (Hammer) animation plays
        double _rechamberDelayTimer; // RechamberAfterShotDelay countdown before the cycle animation starts
        double _rechamberAnimTimer;  // the Hammer (bolt-cycle) clip length
        int _shotCountForRechamber;  // shots since the last cycle -> fires the rechamber at RechamberAfterShotCount
        bool _hammerPending;        // reloaded from EMPTY -> after the mag swap, play the rechamber Hammer clip (source: the reload's 2nd half)
        double _hammerDur;
        float _reloadSpeed = 1f;    // DEXTERITY reload speed, kept so the Hammer clip plays at the same rate
        bool _hammerActive;         // true while the rack (Hammer, reload 2nd half) is playing -> the completion tick just finishes
        int _loadedMagId;           // the magazine item loaded in the gun (its ammo = Ammo); set to Gun.MagazineId on equip
        SDG.Unturned.Item _heldItem;   // the inventory/world Item backing the held gun -> where its ammo/firemode/mag PERSIST (master)
        // Mirror the held gun's live state onto its backing item so it survives hands<->inventory<->drop (source: equipment.state).
        void SaveGunState() { if (_heldItem != null && Gun != null) { _heldItem.gunAmmo = Ammo; _heldItem.gunFiremode = (int)_firemode; _heldItem.gunMagId = _loadedMagId; if (_viewmodel != null && _viewmodel.IsGunViewmodel) _heldItem.gunAttach = _viewmodel.GetAttachMask(); } }   // only save the attach mask from the GUN's own viewmodel -- a consumable/fists viewmodel returns 0 and would wipe the gun's attachments (strawberry)
        void RestoreGunState(SDG.Unturned.Item item)
        {
            if (item == null || item.gunAmmo < 0) return;   // a fresh gun with no saved state keeps its LoadGun defaults
            Ammo = item.gunAmmo;
            if (item.gunFiremode >= 0 && System.Enum.IsDefined(typeof(FireMode), item.gunFiremode)) _firemode = (FireMode)item.gunFiremode;
            if (item.gunMagId >= 0) _loadedMagId = item.gunMagId;
        }

        // Working magazines (increment 1: the Military STANAG). A gun uses mag ITEMS when its default Magazine is a
        // registered magazine (else the old whole-mag reload). A mag fits when its caliber matches the gun's.
        bool UsesMagItem => Gun != null && !Gun.ShellReload && (SDG.Unturned.Assets.find((ushort)Gun.MagazineId)?.IsMagazine ?? false);
        // +1 round in the chamber: a non-shell gun keeps its chambered round through a reload -> capacity is AmmoMax+1. Reloaded
        // from EMPTY (Ammo 0) it has to RACK a round out of the fresh mag (the Hammer clip) and tops out at AmmoMax (no bonus).
        bool HasChamber => Gun != null && !Gun.IsShotgun;   // +1 chamber = mag-fed guns only; neither shotgun action (pump tube / break double-barrel) gets it
        int ChamberedCap => (Gun?.AmmoMax ?? 30) + (HasChamber ? 1 : 0);   // absolute max Ammo = a full mag plus the one in the chamber
        (byte page, byte idx, Item item)? FindBestMag()   // the spare mag in inventory that fits the gun, with the MOST ammo
        {
            if (Inventory == null || Gun == null) return null;
            (byte, byte, Item)? best = null; int bestAmmo = -1;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = Inventory.items[b];
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i); if (jar?.item == null) continue;
                    var a = SDG.Unturned.Assets.find(jar.item.id);
                    if (a != null && a.IsMagazine && a.magCaliber == Gun.Caliber && jar.item.amount > bestAmmo) { bestAmmo = jar.item.amount; best = (b, i, jar.item); }
                }
            }
            return best;
        }
        void DoMagSwap()   // pull the fullest spare mag in, put the old one back with its leftover rounds (source magazine swap)
        {
            var found = FindBestMag();
            if (!found.HasValue) { Ammo = Gun?.AmmoMax ?? Ammo; return; }   // gated by StartReload, but be safe
            var (fb, fi, fresh) = found.Value;
            int oldAmmo = Ammo;
            bool chambered = HasChamber && oldAmmo > 0;                                   // a round rides in the chamber through a TACTICAL swap
            Inventory.items[fb].removeItem(fi);                                          // take the fresh mag out of the bag
            int loaded = System.Math.Min(fresh.amount, Gun?.AmmoMax ?? fresh.amount);    // rounds from the fresh mag
            Ammo = loaded + (chambered ? 1 : 0);                                         // +1: the already-chambered round stays on top of the fresh mag
            Inventory?.tryAddItem(new Item((ushort)_loadedMagId, (byte)System.Math.Max(0, oldAmmo - (chambered ? 1 : 0))));   // old mag back MINUS the chambered round (it stayed in the gun)
            _loadedMagId = fresh.id;
        }

        // Loose ammo (shotgun shells, master: real ammo types). A gun uses shells when a stackable isAmmo item matches its
        // caliber (12ga=113 -> caliber 8; 20ga=381 -> caliber 16). Reload CONSUMES shells from the stack (vs swapping a mag).
        bool UsesShells => Gun != null && Gun.Caliber > 0 && ShellAsset != null;
        SDG.Unturned.ItemAsset ShellAsset   // the shell item whose caliber fits the equipped gun (null if none registered)
        {
            get { foreach (var a in SDG.Unturned.Assets.all()) if (a.isAmmo && a.magCaliber == Gun.Caliber) return a; return null; }
        }
        int CountShells()   // total loose shells of the gun's caliber carried across all pages
        {
            if (Inventory == null || Gun == null) return 0;
            int n = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = Inventory.items[b];
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i); var a = jar?.item != null ? SDG.Unturned.Assets.find(jar.item.id) : null;
                    if (a != null && a.isAmmo && a.magCaliber == Gun.Caliber) n += jar.item.amount;
                }
            }
            return n;
        }
        int ConsumeShells(int want)   // remove up to `want` matching shells from inventory stacks; returns how many were actually taken
        {
            if (Inventory == null || Gun == null || want <= 0) return 0;
            int taken = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2) && taken < want; b++)
            {
                var pg = Inventory.items[b];
                for (int i = pg.getItemCount() - 1; i >= 0 && taken < want; i--)
                {
                    var jar = pg.getItem((byte)i); var a = jar?.item != null ? SDG.Unturned.Assets.find(jar.item.id) : null;
                    if (a == null || !a.isAmmo || a.magCaliber != Gun.Caliber) continue;
                    int t = System.Math.Min(want - taken, jar.item.amount);
                    jar.item.amount = (byte)(jar.item.amount - t); taken += t;
                    if (jar.item.amount <= 0) pg.removeItem((byte)i);   // empty shell stack -> free the slot
                }
            }
            return taken;
        }
        const double ReloadTime = 1.633; // Eaglefire Gun_Reload clip length (no reload-time key in the .dat)
        float _recoilPending, _recoilYawPending;  // un-applied recoil kick (deg); drains additively into the real aim and STAYS -- never auto-returns (master: additive, no recover-to-origin)
        readonly RandomNumberGenerator _rng = new();
        enum FireMode { Safety, Semi, Auto, Burst }   // EFiremode; the gun's available set comes from its .dat flags
        FireMode _firemode = FireMode.Semi;
        public string FiremodeName => _firemode.ToString().ToUpper();   // for the HUD
        // let the FP viewmodel take the world's lighting (day/night sun + ambient)
        DirectionalLight3D _worldSun; Godot.Environment _worldEnv;
        public void LinkWorldLighting(DirectionalLight3D sun, Godot.Environment env)
        {
            _worldSun = sun; _worldEnv = env;   // stored so a re-equipped viewmodel (consumable/gun swap) can re-link
            if (_viewmodel != null) { _viewmodel.WorldSun = sun; _viewmodel.WorldEnv = env; }
        }
        void RelinkViewmodelLighting() { if (_viewmodel != null) { _viewmodel.WorldSun = _worldSun; _viewmodel.WorldEnv = _worldEnv; } }

        // Mirror the nearest DYNAMIC world lights (muzzle flash / vehicle headlights / flares -- tagged into the "dynlight"
        // group) into the viewmodel subviewport so they spill onto the gun. ADDITIVE on the sun-mirror rig (master). Throttled
        // (~17/s) + capped at 4; each light's view-space offset from the player camera becomes the mirror's local position.
        int _lightScanCd;
        readonly System.Collections.Generic.List<(Vector3, Color, float, float)> _mirrorLights = new();
        const int MaxMirrorLights = 4;
        static float LightRange(Light3D l) => l is OmniLight3D o ? o.OmniRange : l is SpotLight3D s ? s.SpotRange : 12f;
        void ScanWorldLights()
        {
            if (_cam == null || _viewmodel == null) return;
            if (--_lightScanCd > 0) return;
            _lightScanCd = 3;
            _mirrorLights.Clear();
            Vector3 camPos = _cam.GlobalPosition;
            var found = new System.Collections.Generic.List<(float d2, Light3D l)>();
            foreach (var n in GetTree().GetNodesInGroup("dynlight"))
                // IsVisibleInTree (not just .Visible): headlights toggle OFF by hiding their PARENT container, so an
                // off headlight still reads Visible=true + LightEnergy=9. Walk the ancestor chain so we only mirror lights
                // actually ON. (Sirens/fire toggle LightEnergy to 0 -> the energy check already skips those.)
                if (n is Light3D dl && IsInstanceValid(dl) && dl.IsVisibleInTree() && dl.LightEnergy > 0.01f)
                {
                    float rng = LightRange(dl);
                    float d2 = camPos.DistanceSquaredTo(dl.GlobalPosition);
                    if (d2 < rng * rng * 4f) found.Add((d2, dl));   // within ~2x its range of the player
                }
            found.Sort((a, b) => a.d2.CompareTo(b.d2));
            for (int i = 0; i < found.Count && i < MaxMirrorLights; i++)
            {
                var dl = found[i].l;
                Vector3 lp = _cam.ToLocal(dl.GlobalPosition);   // light in the player camera's view space
                _mirrorLights.Add((new Vector3(-lp.X, lp.Y, -lp.Z), dl.LightColor, dl.LightEnergy, LightRange(dl)));   // subview cam is 180 deg about Y vs the player cam -> negate X+Z (master: was inverted L/R + fwd/back)

            }
            _viewmodel.SetWorldLights(_mirrorLights);
        }
        int _burstLeft;                               // rounds remaining in the current burst
        float _burstCd;                               // NON-source anti-spam-click cooldown between bursts (master's call)

        bool _dead;
        double _deathTimer;
        RiggedCharacter _corpse;

        // Zombie melee lands here; on death, drop a ragdoll corpse + third-person death-cam, then respawn.
        // fromPos = the attacker's world position, used only to aim the camera flinch; null for sourceless damage
        // (starvation/infection) which flashes but doesn't kick the camera.
        public void TakeDamage(float amount, Vector3? fromPos = null)
        {
            // P3b: a server-owned body ROUTES damage to the server sink (zombie melee/acid + vehicle/deployable
            // blast on a NetAvatar follower body; also fall/OOB on the loopback host shell) instead of moving
            // local HP. Must precede the guards below, which would otherwise swallow the hit. The server sink
            // owns HP/death; the local cosmetics (flash/flinch) are skipped -- the death fact renders via NetDie().
            // review #12: Bleeding is a purely COSMETIC HUD status (no HP drain -- the timer just clears it), and
            // AdoptReplicatedFineVitals deliberately doesn't adopt it, so surface it locally on a real hit BEFORE the
            // server-owned-body early-returns below -- else a hit on the loopback host / MP shell never shows the
            // bleeding icon. NOT on NetAvatar (a remote puppet must not sprout our bleeding state).
            if (amount > 1f && (NetDamageSink != null || NetVitalsAdopted || _pendServerVitals) && !NetAvatar) { Bleeding = true; _bleedTimer = 5.0; }
            if (NetDamageSink != null) { NetDamageSink(amount); return; }
            if (NetAvatar) return;   // C2 v1: server avatars are invulnerable to LOCAL damage -- zombies chase + swing but an unreplicated death would desync every client (server-authoritative vitals are deferred, PEI_CLIENT_PLAN §6)
            if (NetVitalsAdopted || _pendServerVitals) return;   // P3a: HP is server-owned; P3b: also suppress in the pre-adoption spawn window (review finding 5). A local death here would fight the server clock and rubber-band. Server-owned bodies route via NetDamageSink above; a true MP client's fall/OOB are server-derived from its claims.
            if (_dead || Health <= 0f) return;
            Health -= amount;
            if (amount > 1f) { Bleeding = true; _bleedTimer = 5.0; }   // show the bleeding status icon after a real hit

            // Hurt flash — PlayerLifeUI.onDamaged -> PlayerUI.pain: a red full-screen overlay whose alpha is
            // Clamp(damage/40, 0, 1) * 0.75, but only for a real hit (source gates it on damage > 5).
            if (amount > 5f) PainAlpha = Mathf.Clamp(amount / 40f, 0f, 1f) * 0.75f;

            // Camera flinch — PlayerLook.FlinchFromDamage: rotate the view by Min(damage, 25) * 0.5 degrees around the
            // axis Cross(up, hitDir) (perpendicular to where the hit came from), converted into camera-local space so a
            // frontal hit pitches the view and a side hit rolls it. The kick accumulates and later recovers to level.
            if (fromPos.HasValue && _cam != null)
            {
                Vector3 dir = GlobalPosition - fromPos.Value; dir.Y = 0f;   // horizontal hit direction (attacker -> me)
                if (dir.LengthSquared() > 0.0001f)
                {
                    Vector3 worldAxis = Vector3.Up.Cross(dir.Normalized()).Normalized();
                    Vector3 localAxis = (_cam.GlobalTransform.Basis.Inverse() * worldAxis).Normalized();   // InverseTransformDirection
                    float deg = Mathf.Min(amount, 25f) * 0.5f;
                    if (localAxis.IsFinite())   // a degenerate cam basis could NaN the axis -> skip rather than poison _flinch
                        _flinch = (_flinch * new Quaternion(localAxis, Mathf.DegToRad(deg))).Normalized();
                }
            }

            if (Health <= 0f) { Deaths++; Die(); }
        }

        void Die()
        {
            _dead = true;
            _deathTimer = 3.5;
            _burstLeft = 0;   // death cancels any in-progress burst (no resume after respawn)
            if (_wiring) CancelWire();   // death drops any in-progress wire (no stale preview / death-cam nodes)
            EjectFromVehicleOnDeath();   // review #3: detach before the corpse/respawn setup, else the dead driver wedges
            Velocity = Vector3.Zero;

            _corpse = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));
            if (_corpse != null)
            {
                GetParent().AddChild(_corpse);
                _corpse.GlobalPosition = GlobalPosition - new Vector3(0f, 0.9f, 0f);
                _corpse.Rotation = new Vector3(0f, Rotation.Y, 0f);
                var r = new RandomNumberGenerator(); r.Randomize();
                // Unturned RagdollTool force: (dir + up*8 + randXZ +-16) * 32, applied as one physics step (~*0.02).
                Vector3 f = (-GlobalTransform.Basis.Z * 5f + Vector3.Up * 8f + new Vector3(r.RandfRange(-16f, 16f), 0f, r.RandfRange(-16f, 16f))) * 0.64f;
                _corpse.RagdollStart(f);
            }
            _viewmodel?.SetAiming(false);
            _viewmodel?.SetShown(false);   // no gun in the death-cam
            if (_cam != null)
            {
                _cam.TopLevel = true;   // hold the death-cam still in world space while the body flops
                _cam.LookAtFromPosition(GlobalPosition + new Vector3(2.2f, 2.2f, 2.8f), GlobalPosition - new Vector3(0f, 0.6f, 0f), Vector3.Up);
            }
        }

        // Review #3: a player who dies while driving/riding must detach from the vehicle at the moment of death.
        // Otherwise _PhysicsProcess's _driving/_riding branch (3541-3542) returns BEFORE the _dead respawn block,
        // so the dead body keeps calling DriveVehicle forever and the P3a server-clocked respawn never fires
        // (wedged). We restore the body state EnterVehicle disabled -- collision, Visible, HUD, and Park the car --
        // because Respawn() does NOT re-enable those, so the post-respawn shell would otherwise be invisible +
        // non-colliding. Cam + viewmodel are left to Die() (death-cam). Idempotent: no-op when on foot.
        void EjectFromVehicleOnDeath()
        {
            if (_driving == null && _riding == null) return;
            var v = _driving; _driving = null; _riding = null;
            if (v != null) { v.EngineOn = false; v.Park(); GlobalPosition = v.GlobalPosition + v.GlobalTransform.Basis.X * 2.4f + Vector3.Up * 1.0f; }
            if (Hud != null) Hud.Vehicle = null;
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = false;
            Visible = true;
        }

        void Respawn(bool reposition = true)
        {
            _dead = false;
            Health = MaxHealth;
            _netAdoptedHealth = MaxHealth;   // P3a: keep the adopted pin in sync with the fresh HP (the server's coarse Health is 100 on respawn too) so the next UpdateVitals doesn't yank it back down
            Stamina = Food = Water = 1f; Infection = 0f; Bleeding = false; Broken = false;   // fresh vitals on respawn
            if (reposition) GlobalPosition = Spawn;   // P3a: the client-auth MP shell skips this -- the server's recov teleport owns the move to SpawnPos (a GlobalPosition write would be overwritten by the next state claim)
            Velocity = Vector3.Zero;
            _corpse?.QueueFree(); _corpse = null;
            _clothing?.Refresh();   // re-sync the worn clothing onto the (persistent) body after death (source re-applies thirdClothes on spawn)
            _viewmodel?.SetShown(true);
            if (_cam != null)
            {
                _cam.TopLevel = false;
                _cam.Position = new Vector3(0f, 1.6f, 0f);
                _cam.Rotation = Vector3.Zero;
                _pitchDeg = 0f;
                PainAlpha = 0f; _flinch = Quaternion.Identity;   // clear any lingering hurt feedback
            }
        }

        // Survival sim driving the live HUD vitals -- the stepping itself lives in the engine-free
        // PlayerVitalsSim (MP_PLAN §3.4 sim-core); the shell computes sprinting, feeds the skill multipliers
        // (PlayerSkills is game-layer), and owns what death means. Mechanism source-accurate, RATES are the
        // same stand-ins as before (Unturned's real ones live in server modeConfigData, not the binary).
        void UpdateVitals(bool moving, float dt)
        {
            if (NetAvatar) return;   // v1 invulnerability (see TakeDamage): no local starvation/infection death on a server avatar either
            if (_dead) return;
            // B5 (SP/MP-unify): when the fine vitals (food/water/stamina/infection) are server-owned, the
            // owner-block adoption (AdoptReplicatedFineVitals) is their SOLE writer -- SKIP the local sim's
            // fine mutation entirely (running it would re-introduce the shipped bug: local food draining to 0
            // while the server owns the real drain + death). HP stays pinned to the coarse adopted value.
            if (NetFineVitalsAdopted)
            {
                if (NetVitalsAdopted) Health = _netAdoptedHealth;
                return;
            }
            bool sprinting = moving && _move.Stance == EPlayerStance.SPRINT;
            bool died = _vitals.Step(sprinting, SurvivalDrain, dt, new PlayerVitalsSim.Multipliers
            {
                ExerciseStaminaDrain = Skills.ExerciseStaminaDrainMultiplier(),   // EXERCISE slows the drain
                CardioStaminaRegen = Skills.CardioStaminaRegenMultiplier(),       // CARDIO speeds the regen
                SurvivalDrain = Skills.SurvivalDrainMultiplier(),                 // SURVIVAL slows hunger/thirst
                VitalityRegen = Skills.VitalityRegenMultiplier(),                 // VITALITY speeds regen while fed + hydrated
            });
            // P3a: while server-owned, the cosmetic vitals above (stamina/food/water/infection) still step for
            // the local HUD, but HP is re-pinned to the adopted server value as the LAST writer of the tick --
            // local regen/starve never moves it, and starvation never triggers a local death (server-owned).
            if (NetVitalsAdopted) { Health = _netAdoptedHealth; return; }
            if (_pendServerVitals) return;   // P3b (review finding 5): no local starvation death in the pre-adoption spawn window (server owns HP the moment it adopts)
            if (died) { Deaths++; Die(); }
        }

        public Camera3D Camera => _cam;

        // Load a real gun .dat (e.g. Eaglefire) through the ported UnturnedDat layer and equip it.
        public void LoadGun(string datPath)
        {
            string text;
            if (datPath.StartsWith("res://") || datPath.StartsWith("user://"))
            {
                using var f = Godot.FileAccess.Open(datPath, Godot.FileAccess.ModeFlags.Read);
                text = f?.GetAsText();
            }
            else text = System.IO.File.Exists(datPath) ? System.IO.File.ReadAllText(datPath) : null;
            if (string.IsNullOrEmpty(text)) { GD.PushError($"[gun] .dat not found: {datPath}"); return; }
            Gun = GunDef.FromDatText(text);
            _gunName = System.IO.Path.GetFileNameWithoutExtension(datPath);
            Ammo = Gun.AmmoMax;
            _loadedMagId = Gun.MagazineId;   // the gun comes equipped with its default magazine loaded (its ammo = Ammo)
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;   // fresh gun -> not mid-cycle
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false;   // switching weapons mid-reload aborts the reload (anim + logic) -- master
            _viewmodel?.SetReloading(false);
            // reset to a valid firemode for THIS gun — don't inherit the previous one (e.g. Auto carried onto the
            // semi-only shotgun would let it hold-fire full-auto). Prefer Semi, then Auto/Burst, else Safety.
            var modes = AvailableModes();
            _firemode = System.Array.IndexOf(modes, FireMode.Semi) >= 0 ? FireMode.Semi
                      : System.Array.IndexOf(modes, FireMode.Auto) >= 0 ? FireMode.Auto
                      : modes[0];
            _burstLeft = 0;
            GD.Print($"[gun] {Gun.Id}: zombieDmg={Gun.ZombieDamage} vehicleDmg={Gun.VehicleDamage} range={Gun.Range} firerate={Gun.Firerate} mag={Gun.AmmoMax} pellets={Gun.Pellets} mode={_firemode}");
        }

        public string HeldGunName => _gunName;

        // Hold a specific gun by its content name: reload the GunDef + rebuild the per-gun viewmodel. Used by Q-switch
        // and by the inventory's Equip action (equipping a gun makes it the held weapon).
        public void EquipHeldGun(string gunName, SDG.Unturned.Item backingItem = null)
        {
            SaveGunState();   // stash the OUTGOING gun's live state onto its item before we swap away
            LoadGun($"res://content/{gunName}.dat");   // sets Gun + _gunName + Ammo + firemode (fresh defaults)
            _heldItem = backingItem;
            RestoreGunState(backingItem);   // a gun coming from inventory/world remembers its ammo/firemode/mag
            _melee = null; _heldConsumable = null; _heldFuelItem = null; _heldMeleeName = null; ClearDeployable();   // equipping a gun REPLACES the held consumable/melee/deployable (not a layer) -- master
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { GunName = _gunName };
            AddChild(_viewmodel);
            RelinkViewmodelLighting();   // a re-equipped viewmodel must re-take the world lighting, else it renders fullbright (master: Drive PEI)
            if (backingItem != null && backingItem.gunAttach >= 0) _viewmodel.ApplyAttachMask(backingItem.gunAttach);   // restore the gun's saved attachments (e.g. a detached suppressor stays off) -- master
            GD.Print($"[gun] holding {_gunName}");
        }

        // Every player is queryable through PlayerRegistry (nearest-player / iterate-players -- the
        // replacement for the old Local static). _ExitTree fires on QueueFree, so teardown self-cleans.
        public override void _EnterTree() => PlayerRegistry.Register(this);
        public override void _ExitTree() => PlayerRegistry.Unregister(this);

        public override void _Ready()
        {
            AddToGroup("players");     // so vehicle explosions (+ future area effects) can find nearby players
            CollisionLayer = 1 << 3;   // player bit
            CollisionMask = (1 << 0) | (1 << 6);    // walk on ground (bit 0) + collide with transparent props on bit 6 (see-through to the item LOS raycast but still solid for the player -- master)

            _capsule = new CapsuleShape3D { Height = PlayerMovementDef.HEIGHT_STAND, Radius = 0.35f };
            _hitbox = new CollisionShape3D { Shape = _capsule, Position = new Vector3(0, PlayerMovementDef.HEIGHT_STAND / 2f, 0) };
            AddChild(_hitbox);
            FloorMaxAngle = Mathf.DegToRad(55f);   // climb steeper slopes than Godot's 45 default (master)
            FloorSnapLength = 0.5f;                 // stay glued to the ground over small steps / undulations

            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;   // opt the PLAYER out of Godot's global physics interp -- on-foot uses MANUAL position-only interp so the mouse stays instant (master)
            // NetAvatar keeps the (non-Current) camera node -- look math (LookPoint, aim) reads it -- but a
            // Current camera per avatar would hijack the host viewport (L1 sandbox / any windowed server).
            _cam = new Camera3D { Position = new Vector3(0, 1.6f, 0), Current = !NetAvatar, PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off };
            _cam.CullMask &= ~OutlineOverlay.OutlineLayer;   // don't render the items' silhouette meshes in the main view (only the offscreen mask cam does)
            AddChild(_cam);
            if (NetAvatar)
            {
                // server avatar: capsule + camera node + registry registration are enough. Everything below
                // is the client-only subtree (viewmodel/UIs/outline/build/demo inventory) -- and RegisterAll
                // clears+rebuilds the asset table, which a mid-game join must never do to a live server.
                Inventory = new PlayerInventory();   // empty; readers all touch it through null-safe/worn-nothing paths
                return;
            }
            CallDeferred(Node.MethodName.AddChild, new OutlineOverlay());   // screen-space look-at outline (deferred so the viewport/camera exist)
            _lookViz = new MeshInstance3D   // the ONE look-END sphere (O toggles it); TopLevel so it sits in world space at the ray end
            {
                Mesh = new SphereMesh { Radius = LookSphereR, Height = LookSphereR * 2f, RadialSegments = 16, Rings = 10 },
                TopLevel = true, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.3f, 0.8f, 1f, 0.25f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            };
            AddChild(_lookViz);
            _lookHullMesh = new ImmediateMesh();   // I-toggle: line-wireframe of every vehicle's look-focus hulls, rebuilt each frame from the ORIENTED boxes
            _lookHullViz = new MeshInstance3D
            {
                Mesh = _lookHullMesh, TopLevel = true, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.2f, 1f, 0.4f), VertexColorUseAsAlbedo = true, NoDepthTest = true },
            };
            AddChild(_lookHullViz);

            _body = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));   // live 3rd-person body
            if (_body != null) { _body.Visible = false; CallDeferred(Node.MethodName.AddSibling, _body); }
            _viewmodel = new Viewmodel { GunName = _gunName };   // per-gun visuals
            AddChild(_viewmodel);
            _rng.Randomize();

            // the ported inventory + its dashboard. Demo-populate it (real items) so there's something to show.
            ItemCatalog.RegisterAll();
            Inventory = new PlayerInventory();
            PopulateDemoInventory();
            // P4: dress the 3P body off the worn slots. The demo kit already wears Cargo Pants (209) + Alicepack (253);
            // add a starter shirt + hat, then Refresh() paints/attaches every worn slot so the player isn't bare skin.
            _clothing = new PlayerClothingController(_body, Inventory);
            ApplyDefaultOutfit();
            _invUI = new InventoryUI { Inv = Inventory, Player = this, Clothing = _clothing };   // P5: drop-to-slot equip drives the on-body visual through the same controller
            AddChild(_invUI);
            _craftUI = new CraftingUI { Inv = Inventory, Player = this };
            AddChild(_craftUI);
            _skillsUI = new SkillsUI { Player = this };
            AddChild(_skillsUI);
            _build = new BuildTool { Cam = _cam };
            GetParent().AddChild(_build);   // structures live in the scene, not under the player

            if (CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
            foreach (var a in OS.GetCmdlineUserArgs()) if (a == "--pdie") _pdieTest = 2.0; // render-test: die at 2s
        }
        double _pdieTest = -1;

        public PauseMenu PauseMenu;   // ESC viewmodel-tuning menu (set by BuildPlayable); null in demos
        public AttachmentMenu AttachMenu;   // T weapon-attachment menu (set by BuildPlayable); null in demos

        public override void _UnhandledInput(InputEvent @event)
        {
            if (NetAvatar) return;   // a server avatar is driven ONLY through the Scripted* seams, never local input
            // Inventory dashboard open -> EAT ALL game input except Tab (to close it) + Escape: no firing / world interactions /
            // reloading / look through the open UI. (The UI Controls still get their own clicks; those don't reach _UnhandledInput.) (master)
            if (_invUI != null && _invUI.IsOpen && !(@event is InputEventKey { Keycode: Key.Tab or Key.Escape })) return;
            // while driving, only E (exit) / V (cam) / L (lights) / Escape + LMB (horn) / RMB (lights) are live -- no fire, aim, reload, etc.
            // (riding a replicated puppet gates identically -- the vehicle-side keys just no-op below in v1)
            if (_driving != null || _riding != null)
            {
                bool allowedKey = @event is InputEventKey { Pressed: true } dk && (dk.Keycode == Key.F || dk.Keycode == Key.H || dk.Keycode == Key.L || dk.Keycode == Key.Ctrl || dk.Keycode == Key.Escape);   // F = exit (interact key moved off E); H cam, L lights, Ctrl siren, Esc pause
                bool allowedMouse = @event is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right };
                bool camOrbit = @event is InputEventMouseMotion;   // mouse MOTION must pass through -> it orbits the 3rd-person chase cam (this guard was silently eating it, so the cam sat fixed) (strawberry 2026-07-15)
                if (!allowedKey && !allowedMouse && !camOrbit) return;
            }
            // clicks belong to an open UI (inventory / crate / dashboard) when the cursor's visible -- don't fire / honk / aim THROUGH them (master)
            if (@event is InputEventMouseButton && Input.MouseMode != Input.MouseModeEnum.Captured) return;
            if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                if ((_driving != null || _riding != null) && !_fp)   // driving in 3rd person: the mouse ORBITS the chase cam around the car instead of turning the driver (master)
                {
                    _driveCamYaw -= mm.Relative.X * MouseSensitivity;
                    _driveCamPitch = Mathf.Clamp(_driveCamPitch + mm.Relative.Y * MouseSensitivity, -25f, 70f);   // inverted Y: mouse up -> cam tilts down (strawberry)
                }
                else if (_riding != null || DrivingPredicted)   // FP RIDE free-look (#37): the mouse turns the VIEW while A/D steers the car (real Unturned). MP only (ride mode OR Part A predicted driving) -- SP FP driving keeps its fixed gaze.
                {
                    _rideLookYaw -= mm.Relative.X * MouseSensitivity;
                    _rideLookPitch = Mathf.Clamp(_rideLookPitch - mm.Relative.Y * MouseSensitivity, -89f, 89f);   // same Y convention as on-foot look: mouse up -> look up
                }
                else if (_driving == null && _riding == null)
                {
                    RotateY(Mathf.DegToRad(-mm.Relative.X * MouseSensitivity));
                    _pitchDeg = Mathf.Clamp(_pitchDeg - mm.Relative.Y * MouseSensitivity, -89f, 89f);
                    _cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
                }
            }
            else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                if (_driving != null) _driving.Honk();                 // LMB while driving: horn
                else if (_riding != null) { }                          // riding a replicated vehicle: no net horn in v1
                else if (HoldingWireTool) WireLmb();                    // wire tool: pick output / place node / complete on a consumer
                else if (HoldingHoseTool) HoseLmb();                    // hose tool: pick a fluid port / complete on the opposite-role port
                else if (HoldingRopeTool) RopeLmb();                    // rope tool: pick a rear tow node / complete on a front tow node
                else if (_build != null && _build.Active) _build.Place();   // build mode: place a structure
                else if (HoldingDeployable) TryPlaceDeployable();       // holding a deployable: LMB plants it at the ghost
                else if (HoldingConsumable) StartConsume();             // holding a food/drink: LMB eats/drinks it
                else if (_heldFuelItem != null) TryDepositFuel();       // holding a gas can: LMB POURS fuel into the generator/vehicle you're aimed at (master)
                else if (IsRepeatedMelee) { }                          // Repeated tool (blowtorch/chainsaw): LMB is a continuous HOLD driven by the use-tick (UpdateSalvage), never a swing/punch (source UseableMelee.startPrimary: isRepeated -> startSwing)
                else if (_melee != null) MeleeAttack(false);            // LMB with a normal melee = WEAK swing (source UseableMelee)
                else StartFire();
            }
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } rmb)
            {
                if (_driving != null) { if (rmb.Pressed) _driving.ToggleHeadlights(); }   // RMB while driving: toggle lights
                else if (_riding != null) { }                                             // riding: no net light toggle in v1
                else if (HoldingWireTool) { if (rmb.Pressed) { if (_wiring) WireRmb(); else WireManageArm(); } }   // routing: undo/cancel; else: arm a completed-wire clear/unplug (phase 5)
                else if (HoldingHoseTool) { if (rmb.Pressed && _hosing) CancelHose(); }   // hose tool: cancel a pending route (clear/unplug a placed hose = fast-follow)
                else if (HoldingRopeTool) { if (rmb.Pressed) { if (_roping) CancelRope(); else RopeManageArm(); } }   // rope tool: cancel a pending tie; else arm a clear/disconnect (hold RMB clears the rope, tap disconnects that side) -- mirrors the wire tool
                else if (HoldingDeployable) { if (rmb.Pressed) Dequip(); }   // RMB cancels placement entirely -> empty hands (strawberry)
                else if (_heldFuelItem != null) { if (rmb.Pressed) TryExtractFuel(); }   // gas can in hand: RMB a powered PUMP to SUCK fuel into the can (LMB pours it out into a gen/vehicle) (master)
                else if (_melee != null) { if (rmb.Pressed && !IsRepeatedMelee) MeleeAttack(true); }   // RMB = STRONG swing on a normal melee; a Repeated tool (blowtorch/chainsaw) has NO strong attack (source startSecondary: if(!isRepeated)) and no ADS
                else _viewmodel?.SetAiming(rmb.Pressed);   // hold RMB to ADS -- GUNS only (a melee weapon has no sights)
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.R })
            {
                if (HoldingDeployable && _placer != null) _placer.YawOffset += 90f;   // R rotates the deployable ghost 90 deg (strawberry)
                else if (HasGunOut) StartReload();   // no reload without a gun out (master)
            }
            else if (@event is InputEventKey { Pressed: true } hk && hk.Keycode >= Key.Key1 && hk.Keycode <= Key.Key9)
                EquipHotbar((int)hk.Keycode - (int)Key.Key0);   // hotbar keys (bag CLOSED): 1/2 = primary/secondary slot, 3-9 = bound item. Binding (RMB item + 3-9) is handled in InventoryUI while the bag's open.
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.V })
            {
                if (_driving == null && HasGunOut) CycleFiremode();   // V on foot: cycle firemode (only with a gun out)
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.H })
                _fp = !_fp;   // H: toggle 3rd / 1st person camera (on foot + driving)
            // (Q weapon-switch removed -- master: we have the inventory + spawn commands to test weapons now)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.L })
            {
                if (_driving != null) _driving.ToggleHeadlights();         // L while driving: toggle headlights
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Ctrl })
            {
                if (_driving != null && _driving.HasSiren) _driving.ToggleSiren();   // Ctrl while driving an emergency vehicle: toggle siren/lightbar (master)
            }
            else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F })   // F = INTERACT (moved off E, strawberry): exit/hitch/pickup/enter/harvest/open-crate; nothing to interact with -> inspect the held weapon. Echo:false so HOLDING F can't double-fire the hitch toggle (uncouple then instantly re-couple).
            {
                if (_driving != null && !DrivingPredicted) ExitVehicle();  // hop out (SP direct exit; a Part A predicted drive falls through to the server REQUEST below)
                else if (RequestExitPuppet()) { }                          // riding a replicated vehicle: ask the server to free the seat (C6)
                else if (TryToggleHitch()) { }                             // on foot at a trailer hitch: couple / uncouple
                else if (_focusShelfItem != null || _focusItem != null) TryPickup();   // looking at a SHELF item or a dropped item: grab it (shelf item takes priority in TryPickup)
                else if (RequestPickupFocusedPuppet()) { }                 // MP: looking at a REPLICATED dropped item -> ask the server for it (like SP, a focused item wins over a nearby vehicle)
                else if (_focusVehicle != null && IsInstanceValid(_focusVehicle) && !_focusVehicle.IsWreck && !_focusVehicle.IsTrailer) EnterVehicle(_focusVehicle); // looking at a LIVE, drivable vehicle: get in (a wreck is salvaged with LMB; a trailer is towed, not driven)
                else if (RequestEnterNearestPuppet()) { }                  // MP shell near a REPLICATED vehicle: ask the server for the seat (C6; false in SP -- no puppets)
                else if (_focusDeployable != null && IsInstanceValid(_focusDeployable))
                {   // looking at a placed deployable: F starts a HOLD -> pick it up (UpdateDeployPickup); a quick TAP toggles
                    // a generator's power (fired on release). Consume F so it doesn't fall through to open a nearby crate.
                    _fHeldDeploy = _focusDeployable; _deployPickupTimer = 0f;
                }
                else if (RequestHarvestNearestCrop()) { }                  // MP shell near a GROWN replicated crop: ask the server to harvest it (A4; false in SP -- no NetHarvestCrop seam)
                else if (CropManager.NearestGrown(GlobalPosition) is CropNode grownCrop) CropManager.Harvest(grownCrop, this);  // harvest a nearby fully-grown crop (source InteractableFarm harvest)
                else if (_focusShelf != null && IsInstanceValid(_focusShelf) && OpenCrate(_focusShelf)) { }   // looking at a shelf/container -> open it (look-based, not proximity)
                else if (OpenNearestCrate()) { }                           // open a nearby storage crate
                else if (_melee != null) _viewmodel?.PlayMeleeInspect();   // nothing to interact with -> inspect (melee plays its own Inspect clip)
                else _viewmodel?.PlayInspect();                            // ...or the gun's own inspect
            }
            else if (@event is InputEventKey { Pressed: false, Keycode: Key.F } && _fHeldDeploy != null)
            {   // released F over a deployable: a quick TAP toggles a generator (a long hold already picked it up in UpdateDeployPickup)
                if (IsInstanceValid(_fHeldDeploy) && _deployPickupTimer < DeployPickupTime && _fHeldDeploy.CanTogglePower)
                {
                    if (!RequestToggleDeployable(_fHeldDeploy)) _fHeldDeploy.TogglePower();
                }
                if (IsInstanceValid(_fHeldDeploy)) _fHeldDeploy.PickupProgress = 0f;
                _fHeldDeploy = null; _deployPickupTimer = 0f;
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.B })
                _build?.Toggle();     // toggle build mode
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.C })
                _build?.CycleType();  // cycle the structure type (floor/wall)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.G })
                MeleeAttack();        // melee swing at a zombie in reach
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.H })
                ThrowGrenade();       // throw a grenade
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.P, Echo: false })
            {
                WorldItem.ShowLabels = !WorldItem.ShowLabels;                       // P: toggle ALL item ESP name tags
                GetTree().CallGroup("esp_labels", "set_visible", WorldItem.ShowLabels);
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.O, Echo: false })
                WorldItem.ShowLookSphere = !WorldItem.ShowLookSphere;               // O: toggle the look-END sphere visualizer (master)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.I, Echo: false })
            { _showLookHulls = !_showLookHulls; if (_lookHullViz != null) _lookHullViz.Visible = _showLookHulls; }   // I: toggle the look-focus HULL wireframes for every vehicle (strawberry)
            else if (@event is InputEventKey { Keycode: Key.T, Echo: false } tKey)
            {
                if (AttachMenu != null)   // T (hold): show the weapon-attachment menu while held, release to close
                {
                    // attachments are gun-only: no menu for melee/fists/consumable/deployable (strawberry)
                    if (tKey.Pressed && !AttachMenu.IsOpen && _viewmodel != null && _viewmodel.IsGunViewmodel)
                    {
                        AttachMenu.VM = _viewmodel;
                        AttachMenu.Open();
                        Input.MouseMode = Input.MouseModeEnum.Visible;
                    }
                    else if (!tKey.Pressed && AttachMenu.IsOpen)
                    {
                        AttachMenu.Close();
                        Input.MouseMode = Input.MouseModeEnum.Captured;
                    }
                }
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Tab })
            {
                if (_viewmodel != null && _viewmodel.InAttachView) return;   // no inventory while the T attachment menu is up
                SaveGunState();   // capture the held gun's live state (ammo/mag/firemode/attachments) so dropping/moving it in the inventory keeps it (master)
                if (_invUI != null && _invUI.IsOpen) CloseCrate();   // closing the dashboard saves an open crate
                _invUI?.Toggle();   // open/close the inventory dashboard, freeing the mouse while it's open
                Input.MouseMode = (_invUI != null && _invUI.IsOpen) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.K })
            {
                if (_viewmodel != null && _viewmodel.InAttachView) return;   // no crafting while the T attachment menu is up
                _craftUI?.Toggle();   // K: open/close the crafting menu (lists what you can make from your supplies)
                Input.MouseMode = (_craftUI != null && _craftUI.IsOpen) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.J })
            {
                _skillsUI?.Toggle();   // J: open/close the skills menu (spend XP to level skills)
                Input.MouseMode = (_skillsUI != null && _skillsUI.IsOpen) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                // ESC backs out of an open menu FIRST -- close the inventory/crafting/skills dashboard rather than
                // stacking the pause menu on top of it (strawberry). Only when nothing's open does ESC pause.
                if (_invUI != null && _invUI.IsOpen)
                {
                    SaveGunState(); CloseCrate(); _invUI.Close();
                    Input.MouseMode = Input.MouseModeEnum.Captured;
                }
                else if (_craftUI != null && _craftUI.IsOpen)
                {
                    _craftUI.Close(); Input.MouseMode = Input.MouseModeEnum.Captured;
                }
                else if (_skillsUI != null && _skillsUI.IsOpen)
                {
                    _skillsUI.Close(); Input.MouseMode = Input.MouseModeEnum.Captured;
                }
                else if (PauseMenu != null)   // nothing open -> ESC opens the pause menu (freezes the sim; the menu handles ESC-to-resume itself since we're then paused)
                {
                    if (!PauseMenu.IsOpen) PauseMenu.Open();   // Open() sets Paused + frees the mouse
                }
                else
                    Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                        ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
        }

        public void OpenInventory() { _invUI?.Open(); Input.MouseMode = Input.MouseModeEnum.Visible; }
        public void DemoSelect(byte page, byte x, byte y) { _invUI?.DebugSelect(page, x, y); Input.MouseMode = Input.MouseModeEnum.Visible; }
        public void DemoEquip(byte page, byte x, byte y) => _invUI?.DebugEquip(page, x, y);

        // seed the inventory with real items: wear the Alicepack (8x7) + Cargo Pants (6x3) so those pages open up,
        // put both guns in the hand slots, and scatter medical/food/water across pockets + backpack to show packing
        void PopulateDemoInventory() => PopulateDemoKit(Inventory);

        // DevConsole `wear`/`unwear` seam: equip clothing by item (state + visual) / clear a slot. No-op on a NetAvatar
        // (no local body/clothing controller). Public so the F1 console can drive live equip testing.
        public void WearClothing(Item item) => _clothing?.Wear(item);
        public void UnwearClothing(EItemType slot) => _clothing?.Unwear(slot);

        // SP starter outfit so a fresh spawn isn't bare skin. Wear the Orange Hoodie (shirt 3) + Tophat (hat 27) --
        // both catalog-loaded with 0x0 storage, so no inventory-grid disruption -- then Refresh() paints/attaches every
        // worn slot, including the demo kit's already-worn Cargo Pants (209, a ripped-texture pants). Cargo Pants keeps
        // its 6x3 storage; picking a fresh pants id here would resize the PANTS page and drop the demo's in-pants item.
        void ApplyDefaultOutfit()
        {
            if (_clothing == null) return;
            _clothing.Wear(new Item(3));    // Orange Hoodie -> shirt texture on torso/arms
            // NOTE: gear (hat/vest) attach works structurally but its per-slot placement/scale is not yet
            // tuned (renders oversized/offset -- see docs/CLOTHING_PLAN.md P3b-tune), so the default outfit
            // ships shirt+pants only. Re-add gear here once AttachGear offsets are hand-tuned per slot.
            _clothing.Refresh();            // sync all worn slots (shirt above + the demo's Cargo Pants)
        }

        /// <summary>The demo kit, shared by the SP shell (above) and the dedicated server's join seeding
        /// (DedicatedServer -- MP pickup Step 4: the same bag the client always showed, now granted into
        /// the SERVER grid so the owner-block adoption renders truth instead of a client-side fiction).</summary>
        public static void PopulateDemoKit(PlayerInventory inv)
        {
            inv.wearBackpack(new Item(253));   // Alicepack -> backpack slot + 8x7 storage
            inv.wearPants(new Item(209));      // Cargo Pants -> pants slot + 6x3 storage
            inv.equipToSlot(0, new Item(4));     // Eaglefire -> primary
            inv.equipToSlot(1, new Item(363));   // Maplestrike -> secondary
            // items DON'T stack (Unturned is grid-based): each is its own single (amount-1) grid item.
            inv.items[2].tryAddItem(new Item(15));            // Medkit in pockets
            inv.items[2].tryAddItem(new Item(95));            // Bandage
            inv.items[2].tryAddItem(new Item(95));            // Bandage (separate slot -- no stacking)
            inv.items[2].tryAddItem(new Item(14));            // Bottled Water
            var bag = inv.items[PlayerInventory.BACKPACK];
            bag.tryAddItem(new Item(15));                           // Medkit
            bag.tryAddItem(new Item(13));                           // Canned Beans
            bag.tryAddItem(new Item(13));                           // Canned Beans (separate)
            bag.tryAddItem(new Item(14));                           // Bottled Water
            bag.tryAddItem(new Item(14));                           // Bottled Water (separate)
            bag.tryAddItem(new Item(95));                           // Bandage
            bag.tryAddItem(new Item(6, 30));                        // Military Magazine (full, 30 rounds)
            bag.tryAddItem(new Item(6, 30));                        // Military Magazine (full)
            bag.tryAddItem(new Item(6, 12));                        // Military Magazine (partial, 12 left)
            bag.tryAddItem(new Item(6, 0));                         // Military Magazine (EMPTY -> shows x0)
            bag.tryAddItem(new Item(381, 32));                      // 20 Gauge Shells (full stack of 32 -> Masterkey / Sawed-Off ammo)
            bag.tryAddItem(new Item(113, 32));                      // 12 Gauge Shells (full stack of 32 -> Bluntforce ammo)
            bag.tryAddItem(new Item(121, 1));                       // Military Knife (melee: LMB weak / RMB strong swing)
            bag.tryAddItem(new Item(136, 1));                       // Sledgehammer (heavy melee -- anti-structure)
            bag.tryAddItem(new Item(76, 1));                        // Blowtorch (repair live hurt cars / salvage cold wrecks)
            inv.items[PlayerInventory.PANTS].tryAddItem(new Item(13));  // Canned Beans in pants
        }

        // R to reload: block firing, then refill the magazine after the reload's duration. The reload takes the
        // Gun_Reload clip's length (the Eaglefire .dat has no separate reload-time key), so ReloadTime = that.
        void StartReload()
        {
            if (_reloading || _dead) return;
            if (_needsRechamber || _rechambering) return;   // must finish cycling the bolt/pump first (source: reload gated by needsRechamber)
            int max = Gun?.AmmoMax ?? 30;
            if (Ammo >= ChamberedCap) return;   // already topped off (full mag + the round in the chamber)
            if (UsesMagItem && FindBestMag() == null) { _viewmodel?.PlayDryFire(); return; }   // working magazines: no spare mag in the bag -> can't reload
            if (UsesShells && CountShells() <= 0) { _viewmodel?.PlayDryFire(); return; }        // shotgun with no shells in the bag -> can't reload
            _burstLeft = 0;   // reloading cancels any in-progress burst -> it won't resume after the reload (master)
            _reloading = true;
            _hammerActive = false;
            // Empty-mag reload -> after the mag swap, RECHAMBER: play the Hammer clip (the reload's source 2nd half). Not for
            // shell-fed shotguns (their pump is the reload). Source ERechamberGunAfterReloadMode.IfAmmoWasEmpty (the common case).
            _hammerPending = Ammo <= 0 && HasChamber && (_viewmodel?.HasHammer ?? false);   // rack after an empty reload only on chambered (mag-fed) guns -- neither shotgun racks on reload
            float rspeed = Skills.DexterityReloadSpeed();   // DEXTERITY: faster reload -- speeds the anim + shortens the timer to match
            _reloadSpeed = rspeed;
            _viewmodel?.SetReloading(true, rspeed);
            double full = (_viewmodel?.ReloadLength ?? ReloadTime) / rspeed;   // per-gun reload duration (masterkey 2.467s vs rifles 1.633s), sped up by DEXTERITY
            _hammerDur = _hammerPending ? (_viewmodel.HammerLength / rspeed) : 0.0;
            _reloadTimer = Gun?.ShellReload == true ? full / System.Math.Max(1, max) : full;   // shell-fed shotguns (Pump/Break) load ONE shell per interval (see the reload tick + StartFire cancel)
            NetReload?.Invoke();   // D1: the server's ammo/reload clock (ReloadTicks) tracks the local reload
        }

        // LMB press -> fire per the current mode (safety = nothing, semi = one, burst = queue BurstCount, auto = start).
        void StartFire()
        {
            if (_dead) return;   // ignore fire commands on the death screen (master)
            if (!HasGunOut) return;   // no gun in hand (fists / melee / held item) -> no firing at all (master: gun & held item mutually exclusive)
            if (_reloading) { if (Gun?.ShellReload == true && Ammo > 0) { _reloading = false; _viewmodel?.SetReloading(false); } else return; }   // shell-fed shotgun: firing CANCELS the shell-by-shell reload (shoot what's loaded); other guns ignore fire mid-reload (master)
            if (_viewmodel != null && _viewmodel.InAttachView) return;   // no firing while the T attachment menu is up
            if (_viewmodel != null && _viewmodel.IsInspecting) { _viewmodel.CancelInspect(); return; }   // firing mid-inspect cancels it + snaps the gun to the shoot pose; no shot this click
            if (_firemode == FireMode.Safety) return;
            // dry-fire: trigger pulled on an empty chamber -> hammer click, no shot
            if (Ammo <= 0 && !_reloading && _fireCd <= 0f) { _viewmodel?.PlayDryFire(); return; }
            switch (_firemode)
            {
                case FireMode.Semi: Fire(); break;
                case FireMode.Auto: Fire(); break;   // held-fire continues in _PhysicsProcess
                case FireMode.Burst: if (_burstCd <= 0f && _burstLeft <= 0) _burstLeft = Gun?.BurstCount ?? 3; break;   // cooldown gate + can't start a new burst mid-burst (master)
            }
        }

        // V cycles through the modes the gun's .dat actually offers (Eaglefire: Safety -> Semi -> Burst).
        void CycleFiremode()
        {
            var modes = AvailableModes();
            int i = System.Array.IndexOf(modes, _firemode);
            _firemode = modes[(i + 1) % modes.Length];
            _burstLeft = 0;
            SaveGunState();   // remember the fire mode on the backing item (master)
        }

        FireMode[] AvailableModes()
        {
            var list = new System.Collections.Generic.List<FireMode>();
            if (Gun != null)
            {
                if (Gun.HasSafety) list.Add(FireMode.Safety);
                if (Gun.HasSemi) list.Add(FireMode.Semi);
                if (Gun.HasAuto) list.Add(FireMode.Auto);
                if (Gun.BurstCount > 0) list.Add(FireMode.Burst);
            }
            if (list.Count == 0) list.Add(FireMode.Semi);
            return list.ToArray();
        }

        // Random unit vector within a cone of half-angle `spread` (radians) around `dir` — the port of
        // RandomEx.GetRandomForwardVectorInCone the source applies to each bullet's direction.
        Vector3 DeviateInCone(Vector3 dir, float spread)
        {
            float ang = _rng.RandfRange(0f, spread);
            float az = _rng.RandfRange(0f, Mathf.Tau);
            Vector3 up = Mathf.Abs(dir.Dot(Vector3.Up)) < 0.99f ? Vector3.Up : Vector3.Right;
            Vector3 right = dir.Cross(up).Normalized();
            Vector3 realUp = right.Cross(dir).Normalized();
            Vector3 offset = (right * Mathf.Cos(az) + realUp * Mathf.Sin(az)) * Mathf.Sin(ang);
            return (dir * Mathf.Cos(ang) + offset).Normalized();
        }

        // Hitscan: ray from the camera along its forward, masked to the zombie layer. Damage/range/firerate
        // come from the equipped gun's real ItemGunAsset .dat when loaded.
        public bool Fire()
        {
            if (_fireCd > 0f || Ammo <= 0 || _reloading || _needsRechamber || _rechambering || _cam == null || _dead || _driving != null
                || !HasGunOut || (_invUI?.IsOpen ?? false)) return false;   // !HasGunOut: no gun in hand (melee/held item disarm it) -> no shot, even from the polled auto/burst tick after switching away mid-fire (master)
            // -- also while the bolt/pump still needs cycling -- kills a queued burst the frame we die (the tick calls Fire()) + ignores death-screen clicks (master). _driving guard fixes the "stray tracer flies straight south" bug: the auto/burst tick (_PhysicsProcess) calls Fire() on held-LMB WITHOUT a driving check, and while driving _cam is TopLevel (detached chase cam) -> aim = the chase cam's fixed heading, not the player's look. LMB honks while driving anyway.
            if (_viewmodel != null && (!_viewmodel.IsEquipComplete || _viewmodel.IsInspecting || _viewmodel.InAttachView)) return false;   // no firing until equip finishes, or during inspect / attachment menu (source canFire gates)
            float damage = Gun?.ZombieDamage ?? 34f;   // range/travel are encoded in the bullet's steps + velocity
            float vehDamage = Gun?.VehicleDamage ?? 40f;   // bullets hurt vehicles less than zombies (source Vehicle_Damage)
            float objDamage = Gun?.ObjectDamage ?? 25f;    // bullets vs destructible props (source Object_Damage)
            _fireCd = Gun != null ? (Gun.Firerate + 1) / 50f : 0.1f;   // interval = firerate+1 ticks: source fires when clock-lastFire > firerate (STRICT >, UseableGun.tockShoot), so the real gap is firerate+1. Off-by-one made fast guns (zube firerate 4: 750rpm vs correct 600) fire ~25% too hot -- master's "very high ROF"
            Ammo--;
            // fire feedback + the gun's real per-shot viewmodel shake (Shake_Min/Max_*); zero if no gun loaded
            if (Gun != null)
            {
                float rvPitch = _rng.RandfRange(Gun.RecoilMinY, Gun.RecoilMaxY);   // vertical recoil -> muzzle climb
                float rvYaw = _rng.RandfRange(Gun.RecoilMinX, Gun.RecoilMaxX);     // horizontal recoil -> gun yaw
                _viewmodel?.Kick(new Vector3(Gun.ShakeMinX, Gun.ShakeMinY, Gun.ShakeMinZ),
                                 new Vector3(Gun.ShakeMaxX, Gun.ShakeMaxY, Gun.ShakeMaxZ), rvPitch, rvYaw);
            }
            else _viewmodel?.Kick(Vector3.Zero, Vector3.Zero, 0f, 0f);
            float sharp = Skills.SharpshooterRecoilMultiplier();   // SHARPSHOOTER: up to -40% recoil + spread at max level (source UseableGun)
            if (Gun != null)   // additive recoil: each shot kicks the AIM up + random-sign yaw (scaled by Recover); it accumulates and STAYS -- player pulls back down (master)
            {
                _recoilPending += _rng.RandfRange(Gun.RecoilMinY, Gun.RecoilMaxY) * Gun.RecoverY * sharp;
                _recoilYawPending += _rng.RandfRange(Gun.RecoilMinX, Gun.RecoilMaxX) * Gun.RecoverX * (_rng.Randf() < 0.5f ? -1f : 1f) * sharp;
            }

            Vector3 from = _cam.GlobalPosition;
            // Aim from the player's AUTHORITATIVE look (body yaw + camera pitch), NOT the camera's live GLOBAL basis.
            // Reading _cam.GlobalTransform.Basis meant a shot could inherit a transiently-bad camera axis -- flinch/
            // hit-shake (line 1223 sets _cam.Basis = flinch*look) or a frame where the cam basis wasn't the player's
            // -- firing the bullet off in a FIXED world direction regardless of where you aimed (the "stray tracer
            // flies straight south, any gun, any time" bug). Recoil is preserved (it drains into Rotation.Y/_pitchDeg).
            Basis cb = new Basis(Vector3.Up, Rotation.Y) * new Basis(Vector3.Right, Mathf.DegToRad(_pitchDeg));  // X=right, Y=up, -Z=forward
            Vector3 aim = -cb.Z;                                            // undeviated shot axis, from the real look angles
            float aimA = _viewmodel?.AimAlpha ?? 0f;
            // muzzle: hip sits lower-right (where the barrel is); ADS pulls the gun onto the camera axis, so the
            // muzzle centres (X offset -> 0) as you aim -> the bullet + tracer keep originating from the barrel.
            Vector3 muzzle = from + cb.X * (0.12f * (1f - aimA)) - cb.Y * 0.035f + aim * 0.4f;
            SpawnMuzzleLight(muzzle);   // once per shot — the Muzzle_0 flash lights the world

            // Ballistics: each pellet is a SIMULATED PROJECTILE (travel + drop), not an instant ray. Velocity =
            // dir * MuzzleVelocity; it steps every physics tick (0.02s) in StepBullets, dropping under gravity, its
            // tracer flying with it, hits/damage landing when it arrives. (source: BulletInfo + UseableGun.cs:1539.)
            float spread = Gun != null && Gun.SpreadAngleDegrees > 0f
                ? Mathf.DegToRad(Gun.SpreadAngleDegrees) * Mathf.Lerp(1f, Gun.SpreadAim, aimA) * sharp : 0f;   // SHARPSHOOTER tightens spread too (source UseableGun:5055)
            int pellets = UsesShells && ShellAsset != null ? Mathf.Max(1, ShellAsset.pellets) : Mathf.Max(1, Gun?.Pellets ?? 1);   // shotgun buckshot: pellets come from the LOADED shell (source ItemMagazineAsset.pellets) -- 12ga=6, 20ga=8
            float muzzleVel = Gun?.MuzzleVelocity ?? 500f;
            int steps = Gun?.BallisticSteps ?? 20;
            float gravity = -9.81f * (Gun?.GravityMultiplier ?? 4f);
            for (int i = 0; i < pellets; i++)
            {
                Vector3 dir = spread > 0.0001f ? DeviateInCone(aim, spread) : aim;
                SpawnBullet(muzzle, dir * muzzleVel, steps, gravity, damage, vehDamage, objDamage);
            }
            // AlertTool point-noise: an unsuppressed gunshot pulls zombies within earshot over to investigate. A silenced
            // barrel skips the alert ENTIRELY (source UseableGun ~936: only alert if barrel==null || !isSilenced) -> stealth.
            if (!(_viewmodel?.IsSuppressed ?? false)) SoundBus.Emit(GetTree(), GlobalPosition, SoundBus.Gunshot);   // Phase 3 sound bus: unsuppressed gunshot loudness (suppressed = silent)
            // bolt/pump: this shot needs the action cycled before the next one (source RechamberAfterShotCount -> needsRechamber)
            if (Gun != null && Gun.RechamberAfterShotCount > 0 && ++_shotCountForRechamber >= Gun.RechamberAfterShotCount)
            { _needsRechamber = true; _rechamberDelayTimer = Gun.RechamberAfterShotDelay; }
            SaveGunState();   // keep the backing item's ammo current so a drop/holster mid-fight preserves it (master)
            NetFire?.Invoke(muzzle, aim);   // D1: the UNDEVIATED aim ray over the wire -- the server spawns the authoritative bullet (spread is client fx; the bullets above went cosmetic in SpawnBullet)
            return true;   // shot fired; the actual hits/kills land later in StepBullets
        }

        // A simulated bullet (Unturned's BulletInfo): flies from the muzzle with a velocity, dropping under gravity,
        // stepped every physics tick; its tracer travels with it; it hits/despawns on contact or after its steps.
        // Cosmetic (D1): true on every bullet an MP shell fires locally -- it flies + tracers exactly like SP
        // for responsiveness, but on contact it just vanishes: NO damage, NO Kills++, NO hitmarker, NO impact
        // decals/blood. The server's bullet is the authority; impact fx render from the broadcast ImpactFx
        // event (single fx authority -- otherwise the shooter would render both its local impact AND the echo)
        // and the hitmarker moves to HitConfirmed so it only ever tells the truth. Never set in SP.
        sealed class Bullet { public Vector3 Pos, Vel, Origin; public int StepsLeft; public float Gravity, Damage, VehicleDamage, ObjectDamage; public bool Cosmetic; public MeshInstance3D Tracer; public Node3D RocketVis; }
        readonly System.Collections.Generic.List<Bullet> _bullets = new();

        void SpawnBullet(Vector3 pos, Vector3 vel, int steps, float gravity, float damage, float vehicleDamage, float objectDamage)
        {
            var b = new Bullet { Pos = pos, Origin = pos, Vel = vel, StepsLeft = Mathf.Max(1, steps), Gravity = gravity, Damage = damage, VehicleDamage = vehicleDamage, ObjectDamage = objectDamage, Cosmetic = NetFire != null, Tracer = MakeTracer() };
            if (b.Tracer != null) { GetTree().CurrentScene?.AddChild(b.Tracer); UpdateTracer(b); }
            if (Gun?.Action == "Rocket") b.RocketVis = SpawnRocketVis(pos);   // launcher: the rocket is a VISIBLE flying projectile, not an invisible bullet
            _bullets.Add(b);
        }

        // Step every live bullet exactly like the source (UseableGun.cs:1539-1542): raycast this tick's segment for a
        // hit, else advance pos += vel*0.02 and apply gravity vel.y += g*0.02. Called once per 50 Hz physics tick.
        void StepBullets()
        {
            if (_bullets.Count == 0) return;
            var space = GetWorld3D().DirectSpaceState;
            for (int i = _bullets.Count - 1; i >= 0; i--)
            {
                var b = _bullets[i];
                // integration goes through the shared core model (BallisticsMath) so the MP server's bullets
                // (ServerCombat) fly the same trajectory by construction -- the ops are IEEE-identical to the
                // old inline pos + vel*0.02 / vel.y += g*0.02
                var un = BallisticsMath.NextPos(new UnityEngine.Vector3(b.Pos.X, b.Pos.Y, b.Pos.Z), new UnityEngine.Vector3(b.Vel.X, b.Vel.Y, b.Vel.Z));
                Vector3 next = new Vector3(un.x, un.y, un.z);
                var query = PhysicsRayQueryParameters3D.Create(b.Pos, next, (1u << 0) | (1u << 1) | (1u << 4) | (1u << 5) | (1u << 6) | (1u << 9)); // world + enemy + ragdoll + vehicle + props + water surface
                var hit = space.IntersectRay(query);
                if (hit.Count > 0)
                {
                    if (b.Cosmetic) { RemoveBullet(i); continue; }   // MP: the tracer stops here, but damage AND impact fx are the server's (ImpactFx/HitConfirmed events render them)
                    Vector3 point = hit["position"].AsVector3();
                    Vector3 hdir = b.Vel.Normalized();
                    var collider = hit["collider"].As<GodotObject>();
                    if (collider is ZombieController z) { bool head = z.IsHeadshot(point); SpawnFleshImpact(point, hdir); bool wd = z.Dead; z.DamageHit(b.Damage, point, hdir); if (!wd && z.Dead) Kills++; HitmarkerHUD.Instance?.Show(head); }   // hitmarker: white body / red headshot (source EPlayerHit)
                    else if (collider is PhysicalBone3D pb) { SpawnFleshImpact(point, hdir); pb.ApplyImpulse(hdir * 7f, point - pb.GlobalPosition); }
                    else if (collider is Vehicle veh) { veh.TakeDamage(b.VehicleDamage); SpawnSurfaceImpact(point, hit["normal"].AsVector3(), Surf.Metal, veh); }   // source Vehicle_Damage (35) + metal sparks, hole follows the car
                    else if (collider is Deployable dep && !dep.IsWreck) { dep.TakeDamage(b.VehicleDamage); SpawnSurfaceImpact(point, hit["normal"].AsVector3(), Surf.Metal); }   // gunfire damages a placed generator (metal sparks) -- Vehicle_Damage
                    else   // world/prop/terrain -> material impact; terrain samples its splatmap PER-POINT (sand/road/dirt/grass) for the real ground material
                    {
                        Surf sf = Surf.Concrete;
                        if (collider is Node n)
                        {
                            if (Terrain.Active != null && n.IsInGroup("terrain")) sf = Terrain.Active.SurfAt(point.X, point.Z);
                            else if (n.HasMeta(SurfMeta)) sf = (Surf)(int)n.GetMeta(SurfMeta);
                        }
                        // destructible prop: route the hit to the authoritative destructible system (server-owned health).
                        // In the loopback NetDamageObject -> Server.DestructibleHost.DamageObject; the break replicates +
                        // the DestructibleNetSync mirror hides the mesh. (Cosmetic MP bullets never reach here -- line 3061.)
                        if (collider is Node dn && dn.HasMeta(DestructibleField.MetaKey))
                            NetDamageObject?.Invoke((int)dn.GetMeta(DestructibleField.MetaKey), b.ObjectDamage);
                        SpawnSurfaceImpact(point, hit["normal"].AsVector3(), sf);
                    }
                    if (Gun?.Action == "Rocket") { Explode(point, 9f, 250f, 200f, 300f); GD.Print("[rocket] launcher warhead detonated"); }   // rocket launcher: AoE blast on impact (vehicles hit hardest), reusing the grenade explode
                    RemoveBullet(i);
                    continue;
                }
                b.Pos = next;
                var uv = BallisticsMath.StepVel(new UnityEngine.Vector3(b.Vel.X, b.Vel.Y, b.Vel.Z), b.Gravity);
                b.Vel = new Vector3(uv.x, uv.y, uv.z);
                UpdateTracer(b);
                if (b.RocketVis != null && IsInstanceValid(b.RocketVis)) { b.RocketVis.GlobalPosition = b.Pos; var _vd = b.Vel.Normalized(); if (Mathf.Abs(_vd.Y) < 0.98f) b.RocketVis.LookAt(b.Pos + b.Vel, Vector3.Up); }   // fly the rocket model along the ballistic, nose along velocity
                if (--b.StepsLeft <= 0) RemoveBullet(i);
            }
        }

        void RemoveBullet(int i) { _bullets[i].Tracer?.QueueFree(); _bullets[i].RocketVis?.QueueFree(); _bullets.RemoveAt(i); }

        // The rocket launcher's projectile is a VISIBLE flying rocket (projectile.prefab Model_0; no _MainTex -> flat dark body).
        ArrayMesh _rocketMesh; bool _rocketTried;
        Node3D SpawnRocketVis(Vector3 pos)
        {
            if (!_rocketTried) { _rocketTried = true; try { _rocketMesh = ContentProvider.ParseObj("res://content/rocket_projectile.txt"); } catch { } }
            if (_rocketMesh == null) return null;
            var rv = new MeshInstance3D { Mesh = _rocketMesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.324f, 0.397f, 0.331f), Roughness = 0.75f, Metallic = 0f } };   // projectile.prefab material _Color (olive body) + _Glossiness 0.25 -> roughness 0.75
            GetTree().CurrentScene?.AddChild(rv);
            rv.GlobalPosition = pos;
            return rv;
        }

        Texture2D _bulletHoleTex; bool _bhTried;
        Texture2D BulletHoleTex()
        {
            if (!_bhTried) { _bhTried = true; string p = ProjectSettings.GlobalizePath("res://content/bullet_hole.png"); if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) _bulletHoleTex = ImageTexture.CreateFromImage(img); } }
            return _bulletHoleTex;
        }

        // Real per-material bullet-hole decal (Effects/Impacts/<mat>_WithDecal, MeshRenderer _MainTex extracted), cached.
        // Only hard surfaces (concrete/metal/wood) leave a hole; falls back to the generated bullet_hole if a texture's missing.
        readonly System.Collections.Generic.Dictionary<Surf, Texture2D> _decalTex = new System.Collections.Generic.Dictionary<Surf, Texture2D>();
        Texture2D DecalTex(Surf surf)
        {
            if (_decalTex.TryGetValue(surf, out var cached)) return cached;
            string name = surf switch { Surf.Metal => "metal", Surf.Wood => "wood", _ => "concrete" };
            string p = ProjectSettings.GlobalizePath($"res://content/decal_{name}.png");
            Texture2D t = null;
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) { img.GenerateMipmaps(); t = ImageTexture.CreateFromImage(img); } }
            _decalTex[surf] = t ??= BulletHoleTex();
            return t;
        }

        // surface materials for bullet impacts (a slice of the source EPhysicsMaterial set). Tagged on colliders via
        // SetMeta("surf", (int)Surf) -- terrain = Grass, vehicles = Metal, untagged (buildings/props) = Concrete.
        public enum Surf { Concrete, Grass, Dirt, Metal, Wood, Sand, Water }
        public const string SurfMeta = "surf";
        public static Color SurfDust(Surf s) => s switch
        {
            Surf.Grass => new Color(0.40f, 0.50f, 0.28f),
            Surf.Dirt  => new Color(0.45f, 0.35f, 0.25f),
            Surf.Metal => new Color(1f, 0.82f, 0.35f),
            Surf.Wood  => new Color(0.50f, 0.38f, 0.24f),
            Surf.Sand  => new Color(0.78f, 0.70f, 0.52f),
            Surf.Water => new Color(0.62f, 0.72f, 0.85f),   // pale blue-white splash
            _          => new Color(0.58f, 0.56f, 0.52f),   // concrete
        };

        // Bullet impact: a projected bullet-hole DECAL (hard surfaces only) + the REAL source impact effect debris burst
        // at the hit, oriented to the surface normal (Effects/Impacts/<mat>_static, extracted textures + params). Metal =
        // additive sparks; soft ground (grass/dirt/sand) = no decal.
        void SpawnSurfaceImpact(Vector3 point, Vector3 normal, Surf surf, Node3D attachTo = null)
        {
            if (System.Environment.GetEnvironmentVariable("UG_IMPACTDEBUG") == "1") GD.Print($"[impact] surf={surf} @ {point.Round()} tex={(ImpactTex(surf) != null)}");
            var scene = GetTree().CurrentScene;
            if (scene == null) { GD.PrintErr("[impact] CurrentScene NULL -> no impact spawned"); return; }
            Vector3 up = normal.Normalized();
            bool hard = surf is Surf.Concrete or Surf.Metal or Surf.Wood;
            bool metal = surf == Surf.Metal;
            var tex = DecalTex(surf);
            if (hard && tex != null)
            {
                var dec = new Decal { TextureAlbedo = tex, Size = new Vector3(0.16f, 0.3f, 0.16f), AlbedoMix = 1f, Modulate = Colors.White };   // real per-material decal carries its own colour
                (attachTo ?? (Node)scene).AddChild(dec);   // vehicle hits: parent to the car so the hole FOLLOWS it (master); world hits: static in the scene
                Vector3 t = Mathf.Abs(up.Dot(Vector3.Up)) < 0.95f ? Vector3.Up : Vector3.Right;
                Vector3 right = up.Cross(t).Normalized();
                dec.GlobalTransform = new Transform3D(new Basis(right, up, right.Cross(up)), point + up * 0.06f);   // +Y = normal -> projects DOWN into the surface (local-to-parent once attached)
                var t1 = GetTree().CreateTimer(18.0); t1.Timeout += () => { if (IsInstanceValid(dec)) dec.QueueFree(); };
            }
            // Source ParticleSystem (Effects/Impacts/<mat>_static): a one-shot BURST of debris -- concrete/wood/gravel/foliage
            // = 8 @ 0.25-0.5m, 2-4 m/s; metal = 16 @ 0.125-0.25m, 4-8 m/s; all gravityModifier 1 (fall), ~1s life. The debris
            // sheets are 4 frames (a random chip per particle); metal is one spark sprite.
            var itex = ImpactTex(surf);
            bool sheet = itex != null && itex.GetWidth() >= itex.GetHeight() * 3;   // 32x8 debris strip = 4 chips; 16x16 metal = single
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = Colors.White, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                BlendMode = metal ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            };
            if (itex != null) mat.AlbedoTexture = itex;
            if (sheet) { mat.ParticlesAnimHFrames = 4; mat.ParticlesAnimVFrames = 1; mat.ParticlesAnimLoop = false; }
            if (metal) { mat.EmissionEnabled = true; if (itex != null) mat.EmissionTexture = itex; mat.Emission = new Color(1f, 0.7f, 0.2f); mat.EmissionEnergyMultiplier = 2.5f; }
            var dust = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Amount = metal ? 16 : 8, Lifetime = 1.0f, Explosiveness = 1f,
                Direction = up, Spread = 70f, InitialVelocityMin = metal ? 4f : 2f, InitialVelocityMax = metal ? 8f : 4f,
                Gravity = new Vector3(0f, -9.8f, 0f), ScaleAmountMin = metal ? 0.125f : 0.25f, ScaleAmountMax = metal ? 0.25f : 0.5f,
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat },
            };
            if (sheet) { dust.AnimOffsetMin = 0f; dust.AnimOffsetMax = 1f; }   // random static chip frame per particle
            scene.AddChild(dust);
            dust.GlobalPosition = point + up * 0.03f;
            var t2 = GetTree().CreateTimer(1.4); t2.Timeout += () => { if (IsInstanceValid(dust)) dust.QueueFree(); };
            PlayImpactSound(ImpactSnd(surf), point);   // source impact effects carry per-material audio
        }

        // Real impact-effect debris texture per surface (Effects/Impacts/<mat>_static extracted PNG), cached. Surf->effect:
        // grass=foliage, dirt/sand=gravel, metal/wood/concrete same-named.
        readonly System.Collections.Generic.Dictionary<Surf, ImageTexture> _impactTex = new System.Collections.Generic.Dictionary<Surf, ImageTexture>();
        ImageTexture ImpactTex(Surf surf)
        {
            if (_impactTex.TryGetValue(surf, out var cached)) return cached;
            string name = surf switch
            {
                Surf.Metal => "metal", Surf.Wood => "wood", Surf.Sand => "gravel",
                Surf.Grass => "foliage", Surf.Dirt => "gravel", Surf.Water => "water", _ => "concrete",
            };
            string p = ProjectSettings.GlobalizePath($"res://content/impact_{name}_static_0.png");
            ImageTexture tex = null;
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) tex = ImageTexture.CreateFromImage(img); }
            _impactTex[surf] = tex;
            return tex;
        }

        // Impact SOUND — each source impact effect carries its own audio (Effects/Impacts/<mat>/<mat>.mp3), extracted to WAV.
        // A 3D one-shot at the hit point, cached per surface. grass=foliage, dirt/sand=gravel, else same-named.
        static readonly System.Collections.Generic.Dictionary<Surf, AudioStream> _impactSnd = new System.Collections.Generic.Dictionary<Surf, AudioStream>();
        static AudioStream _fleshSnd; static bool _fleshSndTried;
        static AudioStream LoadWav(string rel)
        {
            string p = ProjectSettings.GlobalizePath(rel);
            return System.IO.File.Exists(p) ? AudioStreamWav.LoadFromFile(p) : null;
        }
        AudioStream ImpactSnd(Surf surf)
        {
            if (_impactSnd.TryGetValue(surf, out var cached)) return cached;
            string name = surf switch
            {
                Surf.Metal => "metal", Surf.Wood => "wood", Surf.Sand => "gravel",
                Surf.Grass => "foliage", Surf.Dirt => "gravel", Surf.Water => "water", _ => "concrete",
            };
            var a = LoadWav($"res://content/impact_{name}.wav");
            _impactSnd[surf] = a;
            return a;
        }
        void PlayImpactSound(AudioStream a, Vector3 pos)
        {
            if (a == null) return;
            var scene = GetTree().CurrentScene;
            if (scene == null) return;
            var pl = new AudioStreamPlayer3D { Stream = a, UnitSize = 5f, MaxDistance = 70f, VolumeDb = -3f };
            scene.AddChild(pl);
            pl.GlobalPosition = pos;
            pl.Play();
            pl.Finished += () => { if (IsInstanceValid(pl)) pl.QueueFree(); };
            if (System.Environment.GetEnvironmentVariable("UG_IMPACTDEBUG") == "1") GD.Print($"[impactaudio] played @ {pos.Round()}");
        }

        // The traveling tracer: a thin additive "Bullet"-textured streak that rides with the bullet, oriented along
        // its velocity (the Military_30's Trail_0). Made once per bullet; UpdateTracer re-places it each step.
        MeshInstance3D MakeTracer()
        {
            if (!_tracerTexTried)
            {
                _tracerTexTried = true;
                string p = ProjectSettings.GlobalizePath("res://content/bullet.png");
                if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) _tracerTex = ImageTexture.CreateFromImage(img); }
            }
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                // HDR albedo (>1) so the additive streak crosses the world glow HDR threshold (0.9) and BLOOMS -> a
                // glowing tracer day+night. Modest x2.2 to stay tasteful (thin 0.05m box, won't wash). No glow = just brighter.
                AlbedoColor = new Color(2.2f, 1.98f, 1.21f),
            };
            if (_tracerTex != null) mat.AlbedoTexture = _tracerTex;
            return new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, 5f) }, MaterialOverride = mat };
        }

        void UpdateTracer(Bullet b)
        {
            if (b.Tracer == null) return;
            Vector3 axis = b.Vel.LengthSquared() > 1e-6f ? b.Vel.Normalized() : Vector3.Forward;
            // the streak trails from the MUZZLE (Origin) up to the bullet, capped at 5 m -- so it never extends behind
            // the barrel toward the camera (master: tracer should come from the barrel, not the eye).
            float len = Mathf.Min(5f, b.Pos.DistanceTo(b.Origin));
            if (len < 0.02f) { b.Tracer.Visible = false; return; }
            b.Tracer.Visible = true;
            Vector3 back = b.Pos - axis * len;
            Vector3 up = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
            b.Tracer.LookAtFromPosition((back + b.Pos) * 0.5f, b.Pos, up);   // centred between muzzle-side + head
            b.Tracer.Scale = new Vector3(1f, 1f, len / 5f);                   // shrink the 5 m box to the trail length
        }

        // Flesh impact — the REAL source Flesh_Dynamic effect (impact ID 5), extracted texture + params: a 16-particle
        // burst of the 4-frame blood sprite, size 0.5-1.0m, 3-6 m/s, gravityModifier 1, ~1s life, sprayed back out of the
        // wound (-dir). One-shot GpuParticles3D at the world hit point, auto-freed. (Was a flat-red placeholder quad @ 24
        // particles / 0.1m — now the real blood texture at source counts/sizes.)
        void SpawnFleshImpact(Vector3 point, Vector3 dir)
        {
            if (!_fleshTexTried)
            {
                _fleshTexTried = true;
                string fp = ProjectSettings.GlobalizePath("res://content/impact_flesh_dynamic_0.png");
                if (System.IO.File.Exists(fp)) { var fi = Image.LoadFromFile(fp); if (fi != null) _fleshTex = ImageTexture.CreateFromImage(fi); }
            }
            var pm = new ParticleProcessMaterial
            {
                Direction = -dir, Spread = 60f,
                InitialVelocityMin = 3f, InitialVelocityMax = 6f,       // source startSpeed 3-6
                Gravity = new Vector3(0f, -9.8f, 0f),                   // gravityModifier 1
                ScaleMin = 0.5f, ScaleMax = 1.0f,                       // source startSize 0.5-1.0m (QuadMesh Size 1 -> metres)
                Color = Colors.White,                                   // texture supplies the blood red
                AnimOffsetMin = 0f, AnimOffsetMax = 1f,                 // random static blood frame per particle (4-frame sheet)
            };
            var mat = new StandardMaterial3D
            {
                AlbedoColor = Colors.White, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            };
            if (_fleshTex != null) { mat.AlbedoTexture = _fleshTex; mat.ParticlesAnimHFrames = 4; mat.ParticlesAnimVFrames = 1; mat.ParticlesAnimLoop = false; }
            else mat.AlbedoColor = new Color(0.5f, 0.02f, 0.02f);       // fallback: red if the texture is missing
            var ps = new GpuParticles3D
            {
                Amount = 16, Lifetime = 1.0, OneShot = true, Explosiveness = 1f,   // source burst of 16
                ProcessMaterial = pm,
                DrawPass1 = new QuadMesh { Size = Vector2.One, Material = mat },
                Emitting = true,
            };
            GetTree().CurrentScene?.AddChild(ps);
            ps.GlobalPosition = point;
            var timer = GetTree().CreateTimer(1.4);
            timer.Timeout += () => { if (IsInstanceValid(ps)) ps.QueueFree(); };
            if (!_fleshSndTried) { _fleshSndTried = true; _fleshSnd = LoadWav("res://content/impact_flesh.wav"); }
            PlayImpactSound(_fleshSnd, point);   // source flesh impact carries blood-splat audio
        }

        // D1 (PEI_COMBAT_PLAN §3): render a SERVER-asserted bullet end (the broadcast ImpactFx event) through
        // the same local spawners SP bullets use. The MP shell's own bullets are cosmetic (no impact fx), so
        // this is the ONE impact-fx authority in MP -- every client, the shooter included, renders the server's
        // point; nobody double-renders. The event carries only pos + flesh/world, so world surface/normal are
        // recovered locally with a short probe ray through the point; a miss (e.g. a replicated-vehicle hit --
        // puppets have no colliders) falls back to a soft up-facing debris burst with no decal.
        public void RenderImpactFx(Vector3 point, bool flesh)
        {
            if (flesh)
            {
                Vector3 fdir = point - (_cam?.GlobalPosition ?? GlobalPosition);
                SpawnFleshImpact(point, fdir.LengthSquared() > 1e-4f ? fdir.Normalized() : Vector3.Forward);
                return;
            }
            Vector3 camPos = _cam?.GlobalPosition ?? (GlobalPosition + Vector3.Up * 1.6f);
            Vector3 toward = point - camPos;
            toward = toward.LengthSquared() > 1e-4f ? toward.Normalized() : Vector3.Forward;
            var q = PhysicsRayQueryParameters3D.Create(point - toward * 0.5f, point + toward * 0.5f, (1u << 0) | (1u << 5) | (1u << 6));   // world + vehicle + props
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count > 0)
            {
                Vector3 p = hit["position"].AsVector3();
                Surf sf = Surf.Concrete;
                if (hit["collider"].As<GodotObject>() is Node n)
                {
                    if (Terrain.Active != null && n.IsInGroup("terrain")) sf = Terrain.Active.SurfAt(p.X, p.Z);
                    else if (n.HasMeta(SurfMeta)) sf = (Surf)(int)n.GetMeta(SurfMeta);
                }
                SpawnSurfaceImpact(p, hit["normal"].AsVector3(), sf);
            }
            else
                SpawnSurfaceImpact(point, Vector3.Up, Surf.Dirt);   // surface unrecoverable -> soft debris burst, no decal
        }

        static Texture2D _tracerTex;      // the "Bullet" sprite, loaded once (shared by MakeTracer)
        static bool _tracerTexTried;
        static Texture2D _fleshTex; static bool _fleshTexTried;   // the real Flesh_Dynamic blood sprite (loaded once)

        // Brief world-space muzzle flash light. The source Muzzle_0 effect illuminates the environment on each shot;
        // our viewmodel flash lives in an isolated SubViewport world, so it can't light the main scene. Warm Muzzle_0
        // colour (Unity (0.941,0.756,0.152)), flashed a couple of frames at the muzzle so nearby surfaces/zombies pop.
        void SpawnMuzzleLight(Vector3 pos)
        {
            var light = new OmniLight3D
            {
                OmniRange = 6f,
                LightColor = new Color(0.941f, 0.756f, 0.152f),
                LightEnergy = 3.5f,
            };
            GetTree().CurrentScene?.AddChild(light);
            light.GlobalPosition = pos;
            var timer = GetTree().CreateTimer(0.05);   // brief flash, in step with the muzzle sprite
            timer.Timeout += () => { if (IsInstanceValid(light)) light.QueueFree(); };
        }

        public override void _Process(double delta)
        {
            if (NetAvatar) return;   // per-frame work is all client-side (render interp, look focus, recoil drain, cam) -- none of it on a server avatar
            if (_interpReady && !_dead && _driving == null)   // RENDER INTERPOLATION (master): lerp the visual position between the last two 50Hz ticks so it doesn't step at 50Hz while rendering at 60+
                GlobalPosition = _interpPrev.Lerp(_interpCurr, (float)Engine.GetPhysicsInterpolationFraction());
            if (_driving != null && !_dead)   // driving: position the cam from the vehicle's Godot-INTERPOLATED visual transform, so cam + car mesh are both smooth + IN SYNC (master: godot smoothing for the car)
                PositionDriveCam(_driving.GetGlobalTransformInterpolated());
            if (_riding != null && !_dead && IsInstanceValid(_riding))   // C6 riding: chase the dead-reckoned puppet (it moves per-FRAME in VehicleReplicaView, no physics interp to sample)
                PositionRideCam(_riding.GlobalTransform);
            OutlineOverlay.DrivingSuppress = _driving != null || _riding != null;   // in a vehicle: nothing focusable -> kill the outline overlay's per-frame 2nd cull + dilate (the 3p-cam POI fps drop, strawberry)
            { ulong _t = Time.GetTicksUsec(); UpdateLookFocus(); Prof.Add("lookat", _t); }   // eye-ray -> focus the item you're aiming at
            UpdateWireLook();                                                                 // wire tool: look at a connection cube -> highlight + info readout
            UpdateHoseLook();                                                                 // hose tool: look at a fluid port -> highlight + info + drive the route preview
            UpdateRopeLook();                                                                 // rope tool: look at a vehicle tow node -> highlight + drive the tie preview
            UpdateRopeManage((float)delta);                                                   // rope tool: poke a roped node -> hold RMB clear / tap RMB disconnect (mirrors the wire tool)
            UpdateWireManage((float)delta);                                                   // wire tool: poke a wired port -> hold RMB clear / tap RMB unplug
            UpdateWireArrows();                                                               // wire tool: show in/out arrows on every connection point (blue avail / red occupied)
            if (_showLookHulls) UpdateLookHullViz();                                          // I-toggle: rebuild the look-hull wireframes
            { ulong _t = Time.GetTicksUsec(); UpdateSalvage((float)delta); Prof.Add("salvage", _t); }   // wreck salvage prompt + blowtorch teardown
            UpdateDeployPickup((float)delta);   // hold-F to pick a placed deployable back up (its wires disconnect)
            // Additive recoil (master): drain the pending kick INTO the real aim over a couple frames (a smooth climb),
            // then leave it there -- the view stays kicked up and the player pulls the mouse back down. Never recovers on its own.
            if (_recoilPending != 0f || _recoilYawPending != 0f)
            {
                float step = Mathf.Min(1f, 18f * (float)delta);
                float dp = _recoilPending * step;
                _pitchDeg = Mathf.Clamp(_pitchDeg + dp, -89f, 89f);   // pitch folds into the actual aim -- stays put
                _recoilPending -= dp;
                float dy = _recoilYawPending * step;
                RotateY(Mathf.DegToRad(dy));                          // yaw folds into the body -- stays put
                _recoilYawPending -= dy;
            }
            PainAlpha = Mathf.Max(0f, PainAlpha - (float)delta);                 // hurt flash fades at 1/s (PlayerUI line 1835)
            // flinch recovers to level at 4/s (PlayerLook line 1330). GUARD: a degenerate hit can leave _flinch NaN or
            // denormalized, and Godot's Slerp/Basis assert IsNormalized -> that was the "Quaternion is not normalized" spam.
            if (!_flinch.IsFinite() || _flinch.LengthSquared() < 1e-6f) _flinch = Quaternion.Identity;
            _flinch = _flinch.Normalized().Slerp(Quaternion.Identity, 4f * (float)delta);
            if (_cam != null && !_dead && _driving == null && _riding == null)   // while driving/riding, the drive cam above owns the view
            {
                if (_ugFp) _fp = true;   // render harness (UG_FP=1): force 1st-person so the FP viewmodel is captured
                if (_fp)
                {
                    // FP: eye height follows the stance (PlayerLook.heightLook 1.75/1.2/0.35, lerped 4/s), pitched by the mouse
                    float targetEye = Stance switch { EPlayerStance.CROUCH => 1.2f, EPlayerStance.PRONE => 0.35f, _ => 1.75f };
                    var cp = _cam.Position; cp.X = 0f; cp.Z = 0f; cp.Y = Mathf.Lerp(cp.Y, targetEye, 4f * (float)delta); _cam.Position = cp;
                    var look = Basis.FromEuler(new Vector3(Mathf.DegToRad(_pitchDeg), 0f, 0f), EulerOrder.Yxz);   // flinch left-multiplies the look
                    _cam.Basis = new Basis(_flinch) * look;
                }
                else
                {
                    // 3rd person on foot: chase behind + above (child of the player, so it follows the body yaw); mouse Y orbits a bit
                    _cam.Position = new Vector3(0f, 1.9f, 3.4f);
                    _cam.Rotation = new Vector3(Mathf.DegToRad(Mathf.Clamp(_pitchDeg * 0.5f, -40f, 25f) - 6f), 0f, 0f);
                }
            }
            UpdateBody(delta);
        }

        // live 3rd-person body: shown when !_fp; stands at the player (facing the body yaw, animated by ground speed) or sits in the driver seat
        void UpdateBody(double delta)
        {
            if (_viewmodel != null) _viewmodel.SetShown(_fp && _driving == null && _riding == null && !_dead);   // FP gun arms: first-person on foot only
            if (_body == null) return;
            _body.Visible = !_fp && !_dead;   // dead -> the corpse ragdoll handles the body
            if (_fp || _dead) { return; }
            if (_driving != null)   // in the driver seat (best-effort idle pose)
            {
                _body.GlobalTransform = _driving.GlobalTransform * new Transform3D(Basis.Identity, _driving.SeatOffset);   // per-vehicle driver seat (prefab Seat_0)
                _body.PlayLoop(_body.ClipLength("Idle_Drive") > 0f ? "Idle_Drive" : "Idle_Sit");   // seated DRIVING pose (hands on the wheel) instead of a standing idle (master)
            }
            else if (_riding != null && IsInstanceValid(_riding))   // C6: same seated pose on the replicated puppet's seat
            {
                _body.GlobalTransform = _riding.GlobalTransform * new Transform3D(Basis.Identity, _riding.SeatOffset);
                _body.PlayLoop(_body.ClipLength("Idle_Drive") > 0f ? "Idle_Drive" : "Idle_Sit");
            }
            else   // on foot: at the player's feet, facing the body yaw, locomotion by horizontal speed
            {
                _body.GlobalPosition = GlobalPosition;
                _body.Rotation = new Vector3(0f, Rotation.Y, 0f);
                _body.SetLocomotion(new Vector2(Velocity.X, Velocity.Z).Length(), Stance);   // crouch/prone anims by stance (master)
            }
            _body.Tick(delta);
        }

        // --- Vehicle enter/exit (source: InteractableVehicle). F enters the nearest vehicle's driver seat / exits. ---
        public bool IsDriving => _driving != null;
        public Vehicle Driving => _driving;   // the vehicle being driven (for zombies to swipe at, source targetPassengerVehicle)
        public void SetSuppressor(bool on) => _viewmodel?.SetSlotAttached("Barrel", on);   // test hook: toggle the silenced barrel
        public void ForceAim(bool on) => _viewmodel?.SetAiming(on);   // test hook (UG_ADS firetest): drive ADS headlessly to render the real in-game aim view

        Vehicle NearestVehicle()
        {
            Vehicle best = null; float bestD = 4.0f * 4.0f;   // within ~4 m
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && !v.Exploded)   // a wrecked car can't be entered (master); F near only a wreck falls through to pickup
                {
                    float d = GlobalPosition.DistanceSquaredTo(v.GlobalPosition);
                    if (d < bestD) { bestD = d; best = v; }
                }
            return best;
        }

        // On-foot trailer hitch (master steer: back the cab under the trailer, hop out, walk to the hitch, press E).
        // Uncouples the nearby trailer if it's already hitched; else couples a cab that's backed under its kingpin.
        bool TryToggleHitch()
        {
            if (_driving != null) return false;
            // Must be LOOKING AT the trailer AND standing within HitchReach of its kingpin (strawberry: look +
            // zone + E). This now matches the billboard prompt, which only surfaces while look-focused AND in range.
            var trailer = _focusVehicle;
            if (trailer == null || !IsInstanceValid(trailer) || !trailer.IsTrailer || trailer.Exploded) return false;
            if (GlobalPosition.DistanceSquaredTo(trailer.KingpinWorld) > Vehicle.HitchReach * Vehicle.HitchReach) return false;   // in the hitch zone
            if (trailer.CoupledCab != null) { trailer.Uncouple(); return true; }   // already hitched -> disconnect
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle cab && cab.CanTow && cab.CoupledTrailer == null && cab.CoupleTo(trailer)) return true;   // a cab backed under -> couple
            return false;
        }

        public HUD Hud;   // set by the scene builder; the vehicle status box binds to the driven vehicle on enter/exit
        public SDG.Unturned.PlayerSkills Skills { get; } = new();   // the player's skills/XP (source PlayerSkills); gates crafting, boosts farming, etc.

        // MP Part A: driving a client-local predicted vehicle (ClientWorldSession built it) -- gates the
        // free-look extension; always false in pure SP (the flag is only ever set by the session).
        bool DrivingPredicted => _driving != null && _driving.NetClientPredicted;

        // Public since Part A: ClientWorldSession seats the shell on its client-local vehicle through this
        // EXACT SP path (one enter seam, zero MP-only side effects here).
        public void EnterVehicle(Vehicle v)
        {
            if (v.NetDriverId != 0) return;   // MP §3.6: a remote player holds the seat (single driver) -- never set in pure SP, so the direct path is unchanged
            if (v.NetClientPredicted) { _rideLookYaw = 0f; _rideLookPitch = FpRideGazePitchDeg; }   // Part A free-look starts at the classic forward gaze, like EnterPuppet (#37)
            _driving = v;
            _burstLeft = 0;                                    // entering a vehicle cancels an in-progress burst (no resume on exit)
            v.EngineOn = !v.OnFire;                            // start the engine (source) -- but a burnt/on-fire car stays dead (master)
            if (Hud != null) Hud.Vehicle = v;                  // show the vehicle status box (fuel/health/battery)
            _viewmodel?.SetShown(false);                       // no gun while driving
            if (_cam != null) _cam.TopLevel = true;            // free the camera into world space
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = true;   // stop the player body fighting the vehicle
            Visible = false;
            Velocity = Vector3.Zero;
        }

        void ExitVehicle()
        {
            var v = _driving; _driving = null;
            if (v != null) { v.EngineOn = false; v.Park(); }   // stop burning fuel + brake so it doesn't roll away
            if (Hud != null) Hud.Vehicle = null;               // hide the vehicle status box
            if (v != null) GlobalPosition = v.GlobalPosition + v.GlobalTransform.Basis.X * 2.4f + Vector3.Up * 1.0f;
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = false;
            Visible = true;
            _viewmodel?.SetShown(true);
            if (_cam != null) { _cam.TopLevel = false; _cam.Position = new Vector3(0f, 1.6f, 0f); _cam.Rotation = Vector3.Zero; }
            _pitchDeg = 0f;
        }

        /// <summary>MP Part A exit: the SP restore block at the SERVER's authoritative exit spot (the
        /// VehicleExited fact carries it -- ClientWorldSession.OnVehicleExited). No Park/engine-off side
        /// effects on the vehicle: the session destroys the local node right after; the server's own node
        /// gets the real SP exit effects from VehicleNetSync's seat-freed branch.</summary>
        public void ExitVehicleAt(Vector3 exitPos)
        {
            var v = _driving; _driving = null;
            if (v != null) v.EngineOn = false;
            if (Hud != null) Hud.Vehicle = null;               // hide the vehicle status box
            GlobalPosition = exitPos;
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = false;
            Visible = true;
            Velocity = Vector3.Zero;
            _viewmodel?.SetShown(true);
            if (_cam != null) { _cam.TopLevel = false; _cam.Position = new Vector3(0f, 1.6f, 0f); _cam.Rotation = Vector3.Zero; }
            _pitchDeg = 0f;
        }

        public Vector2? ScriptedDrive;   // test hook: (steer, throttle) instead of keys
        public bool DriveFP { set => _fp = value; }   // test hook: force first-person cam
        public void EnterNearestVehicle() { var v = NearestVehicle(); if (v != null) EnterVehicle(v); }

        // While any menu/cursor UI is up (F1 dev console, inventory, craft, skills, map) the mouse is un-captured. Gate
        // all POLLED gameplay input on this so the menu is MODAL -- no walking/steering/firing/stance through it. Look +
        // single-fire already gate on Captured; this closes the movement/auto-fire/driving/stance gaps. Scripted
        // (harness) input bypasses -- it sets Scripted* directly. (strawberry 2026-07-15)
        bool UiInputBlocked => Input.MouseMode != Input.MouseModeEnum.Captured;

        void DriveVehicle(float delta)
        {
            if (_driving.Exploded) { ExitVehicle(); TakeDamage(150f); return; }   // caught in the blast -> ejected + killed (source explode kills passengers)
            float throttle, steer;
            if (ScriptedDrive.HasValue) { steer = ScriptedDrive.Value.X; throttle = ScriptedDrive.Value.Y; }
            else if (UiInputBlocked) { throttle = 0f; steer = 0f; }   // menu open -> don't steer/accelerate through it
            else
            {
                throttle = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
                steer = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            }
            bool handbrake = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Space);
            _driving.Drive(throttle, steer, handbrake);
            LastDriveInput = new UnityEngine.Vector2(steer, throttle);   // Part A: the session's VehicleState carries the axes as wheel/light dressing (inert in SP -- nothing reads these outside MP)
            LastHandbrakeInput = handbrake;
            GlobalPosition = _driving.GlobalPosition;   // ride along so exit + FP cam land at the vehicle (the cam is positioned in _Process from the vehicle's INTERPOLATED transform)
        }

        void PositionDriveCam(Transform3D vt)   // SP driving: the cam math below, fed by the driven Vehicle's eye + size
        {
            float size = 0f;
            if (!_fp)
            {
                size = _driving.WorldMeshAabb().Size.Length();          // bounding diagonal -> bigger vehicle, further back
                if (_driving.CoupledTrailer != null && IsInstanceValid(_driving.CoupledTrailer))
                    size += _driving.CoupledTrailer.WorldMeshAabb().Size.Length() * 0.7f;   // towing -> pull the cam out further so the whole rig stays in frame (strawberry)
            }
            PositionVehicleCam(vt, _driving.DriverEyeLocal, size);
        }

        // C6 ride mode: the same cam anchored on the replicated puppet (no trailer towing over the wire in v1)
        void PositionRideCam(Transform3D vt) => PositionVehicleCam(vt, _riding.DriverEyeLocal, _fp ? 0f : _riding.MeshSize);

        void PositionVehicleCam(Transform3D vt, Vector3 eyeL, float size)   // FP / chase cam from the (interpolated) vehicle transform. Full global transform atomically
        {                                                                    // (position + orientation): a LookAt updated pos but not rotation through turns -> car slid out of frame.
            if (_cam == null) return;
            var fwd = -vt.Basis.Z; fwd.Y = 0f;
            fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
            if (_fp)   // first-person from the driver's head, looking forward over the hood (eyeL per-vehicle: tall cabs sit higher so the view clears a long hood)
            {
                var eye = vt * eyeL;
                if (_riding != null || DrivingPredicted)   // MP ride OR Part A predicted driving (#37): FREE-LOOK -- yaw/pitch in vehicle-local space; (0, FpRideGazePitchDeg) == the fixed gaze below
                {
                    var look = vt.Basis.Orthonormalized() * new Basis(Vector3.Up, Mathf.DegToRad(_rideLookYaw)) * new Basis(Vector3.Right, Mathf.DegToRad(_rideLookPitch));
                    _cam.GlobalTransform = new Transform3D(look, eye);
                }
                else   // SP driving: the classic fixed forward gaze over the hood
                    _cam.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(vt * (eyeL + new Vector3(0f, -0.6f, -3.9f)), Vector3.Up);
            }
            else            // third-person chase: ORBIT behind the car (mouse yaw/pitch), AUTO-ZOOMED for the vehicle's size (master)
            {
                float dist = Mathf.Clamp(size * 1.1f, 6.5f, 34f);   // raised cap so the semi+trailer fits
                float pitchR = Mathf.DegToRad(_driveCamPitch);
                Vector3 dir = new Basis(Vector3.Up, Mathf.DegToRad(_driveCamYaw)) * (-fwd);   // behind the heading, orbited by the mouse yaw
                var eye = vt.Origin + dir * (dist * Mathf.Cos(pitchR)) + Vector3.Up * (dist * Mathf.Sin(pitchR) + size * 0.22f);
                _cam.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(vt.Origin + Vector3.Up * (size * 0.15f), Vector3.Up);
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_pdieTest > 0) { _pdieTest -= delta; if (_pdieTest <= 0) { _pdieTest = -1; TakeDamage(9999f); } }
            // below-map kill: Unturned Level.isPointWithinValidHeight = y in [-1024,1024]; fall past the map floor -> die + respawn (covers driving too)
            if (!NetAvatar && !_dead && GlobalPosition.Y < -1030f) { GD.Print("[oob] fell below the map -> killed"); TakeDamage(9999f); }   // NetAvatar: TakeDamage is a no-op (invulnerable) -- gate here too so a pathological fall can't spam the log every tick
            if (NetHold) return;   // mp-clientauth-foot: a follower body never moves itself -- the entity owns the transform, PlayerNetSync teleports this body onto it
            if (_driving != null) { _interpReady = false; LastMoveInput = UnityEngine.Vector2.zero; LastJumpInput = false; DriveVehicle((float)delta); return; }   // driving: skip on-foot movement (+ pause the render-interp so exiting doesn't smear)
            if (_riding != null) { _interpReady = false; LastMoveInput = UnityEngine.Vector2.zero; LastJumpInput = false; RidePuppet(); return; }   // C6 ride mode: same freeze -- capture drive intent only, the SERVER drives
            if (_interpReady && !_dead) GlobalPosition = _interpCurr;   // render-interp (master): restore the TRUE physics position before moving (undoes the _Process visual smoothing)
            StepBullets();   // advance in-flight bullets (travel + drop) each 50 Hz tick — matches the source 0.02s step
            if (_bleedTimer > 0) { _bleedTimer -= delta; if (_bleedTimer <= 0) Bleeding = false; }
            if (_dead)
            {
                _deathTimer -= delta;
                Velocity = Vector3.Zero;
                LastMoveInput = UnityEngine.Vector2.zero;
                LastJumpInput = false;
                // P3a: while server-owned, the SERVER owns the 3.5 s respawn clock -- NetRespawn() drives the
                // revive off PlayerRespawnedEvent; the local timer must not self-respawn (it would fight the
                // server, respawning early / at the wrong place). Default SP keeps its local timer verbatim.
                if (_deathTimer <= 0 && !_serverOwnedRespawn) Respawn();
                return;
            }
            if (_fireCd > 0f) _fireCd -= (float)delta;
            if (_meleeCd > 0f) _meleeCd -= (float)delta;
            if (_pendingMeleeHit > 0f) { _pendingMeleeHit -= (float)delta; if (_pendingMeleeHit <= 0f) ApplyMeleeHit(_pendingMeleeStrong); }   // deferred melee damage lands at swing-end (master)
            if (_burstCd > 0f) _burstCd -= (float)delta;
            if (_grenadeCd > 0f) _grenadeCd -= (float)delta;
            if (_reloading)
            {
                _reloadTimer -= delta;
                if (_reloadTimer <= 0)
                {
                    int max = Gun?.AmmoMax ?? 30;
                    if (_hammerActive) { _hammerActive = false; _reloading = false; _viewmodel?.SetReloading(false); }   // the rack (reload 2nd half) finished
                    else if (Gun?.ShellReload == true)   // pump shotgun: load ONE shell per interval from the shell stack (fire mid-reload keeps what's loaded); stop when full or out of shells
                    {
                        if (!UsesShells || ConsumeShells(1) > 0) Ammo = System.Math.Min(Ammo + 1, max);
                        if (Ammo >= max || (UsesShells && CountShells() <= 0)) { _reloading = false; _viewmodel?.SetReloading(false); }
                        else _reloadTimer = (_viewmodel?.ReloadLength ?? ReloadTime) / System.Math.Max(1, max);   // next shell -- do NOT re-fire SetReloading (the reload anim + sound play ONCE at the start; replaying per shell was the "completely wrong" sound) (master)
                    }
                    else   // whole reload: break-action shotgun loads its barrels from the shell stack; else a mag-swap / whole refill
                    {
                        if (UsesShells) Ammo += ConsumeShells(max - Ammo);   // break-action: fill the barrels from the shell stack (limited by what's carried)
                        else if (UsesMagItem) DoMagSwap(); else Ammo = (HasChamber && Ammo > 0) ? max + 1 : max;   // +1: a non-empty reload keeps the chambered round (empty -> just max, then the rack)
                        if (_hammerPending) { _hammerPending = false; _hammerActive = true; _viewmodel?.PlayHammer(_reloadSpeed); _reloadTimer = _hammerDur; }   // empty reload: now RACK the round (source Hammer clip = the reload's 2nd half)
                        else { _reloading = false; _viewmodel?.SetReloading(false); }
                    }
                    SaveGunState();   // reload finished -> mirror the new ammo/mag onto the backing item (master persistence)
                }
            }
            TickRechamber(delta);   // bolt/pump: run the post-shot bolt-cycle timer -> the Hammer clip, then re-enable firing
            // burst rounds + full-auto hold fire on cooldown (Fire() still enforces ammo/reload/cd)
            if (_fireCd <= 0f && !_reloading)
            {
                if (_burstLeft > 0) { if (Fire()) { _burstLeft--; if (_burstLeft == 0) _burstCd = 0.2f; } else _burstLeft = 0; }
                else if (_firemode == FireMode.Auto && !NetAvatar && !UiInputBlocked && Input.IsMouseButtonPressed(MouseButton.Left)) Fire();   // NetAvatar: never poll global input (a windowed L1 host's held mouse must not fire server avatars)
            }

            // Stance FSM: the shell polls the keys, the engine-free PlayerStanceSim owns the state machine
            // (X = crouch, Z = prone, sprint overlay, broken-legs demotion, headroom gate -- MP_PLAN §3.4).
            // NetAvatar never polls the keys -- PlayerNetSync forces ScriptedStance from the MoveInput
            // stance bits instead, so the avatar integrates at the stance the client shell predicted at.
            bool xNow = !NetAvatar && !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.X);
            bool zNow = !NetAvatar && !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Z);
            bool sprintNow = !NetAvatar && !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Shift);
            StepStanceOnce(xNow, zNow, sprintNow, ScriptedStance);

            float forward, strafe;
            if (ScriptedInput.HasValue) { strafe = ScriptedInput.Value.x; forward = ScriptedInput.Value.y; }
            else if (UiInputBlocked) { forward = 0f; strafe = 0f; }   // menu open -> don't walk through it
            else
            {
                forward = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
                strafe  = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            }
            bool jump = (ScriptedJump ?? (!NetAvatar && !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Space))) && !Broken;   // broken legs can't jump (PlayerMovement.cs:1310); ScriptedJump = the wire's MoveInput v2 jump bit (C2)

            LastMoveInput = new UnityEngine.Vector2(strafe, forward);   // shell-captured axes for the MP input command
            LastJumpInput = jump;   // the wire jump bit is the HELD key the sim consumed (post-Broken) -- C3 reverted the F1 takeoff-edge encoding: a mispredicted takeoff is corrected by rewind+replay, not by wire gymnastics

            // feed the viewmodel its locomotion so the walk bob picks the right SPEED_*/BOB_* + gates on movement
            bool moving = Mathf.Abs(forward) > 0.01f || Mathf.Abs(strafe) > 0.01f;
            Moving = moving;                                  // exposed for zombie stealth detection
            _viewmodel?.SetLocomotion(moving, _move.Stance);
            UpdateVitals(moving, (float)delta);
            TickConsume((float)delta);   // eat/drink timer -> applies the held consumable's effects
            TickDeploy((float)delta);    // deployable: follow the aim with the ghost + finish a pending place
            if (_viewmodel != null && _worldSun != null && _viewmodel.WorldSun == null) RelinkViewmodelLighting();   // safety: any viewmodel created before/without a link (Drive PEI timing, vehicle exit) still takes the world lighting
            ScanWorldLights();   // mirror nearby dynamic world lights (muzzle/headlights/flares) onto the gun

            // Phase 3 hearing: moving on foot makes FOOTSTEP noise the zombies can hear, loudness = the source stealth
            // detection radius by stance/speed (sprint 20 loud .. prone 3 near-silent). Throttled; a motionless player
            // makes no sound (must be SEEN instead). Zombies within earshot path to it via SoundBus.Hear.
            _footNoiseT -= (float)delta;
            if (moving && _footNoiseT <= 0f)
            {
                _footNoiseT = 0.4f;
                float loud = GetStealthDetectionRadius() * Skills.SneakyBeakyNoiseMultiplier();   // SNEAKYBEAKY quiets footsteps -> zombies hear you from less far (source PlayerMovement:791)
                if (loud > 2f) SoundBus.Emit(GetTree(), GlobalPosition, loud);
            }

            StepMoveOnce(strafe, forward, jump, (float)delta, out bool wasAirborne, out float vy, out bool groundedEntering);
            LastGroundedInput = groundedEntering;   // the grounded the sim consumed -- state-stream dressing
            _interpPrev = _interpReady ? _interpCurr : GlobalPosition; _interpCurr = GlobalPosition; _interpReady = true;   // snapshot this tick's start/end for render interpolation (master)
            if (wasAirborne && IsOnFloor()) CheckFallDamage(vy);   // just touched down -> fall damage on a hard landing
        }

        // ---- the movement kernel: the ONE deterministic movement step, split in two halves because
        // the live tick interleaves per-tick client work (viewmodel locomotion, vitals, footstep
        // noise) between the stance decision and the move. Everything physics-relevant lives HERE. ----

        /// <summary>Stance half: one stance-FSM step + the capsule resize (source HeightForStance).</summary>
        void StepStanceOnce(bool crouchKey, bool proneKey, bool sprintKey, EPlayerStance? scriptedStance)
        {
            _move.Stance = _stance.Step(crouchKey, proneKey, sprintKey, Stamina, Broken, scriptedStance, _capStance, HeadroomFor);
            UpdateHitbox(_move.Stance);   // resize the collision capsule to match the stance (source HeightForStance)
        }

        /// <summary>Movement half: grounded resolve -> sim Step -> StepUp -> MoveAndSlide.
        /// groundedEntering = the grounded state the sim consumed;
        /// verticalVel = this step's sim vertical velocity (fall damage).
        /// (The v9 note: the MP DeterministicGround fork -- det spherecast ground + snap, the F6
        /// real-step StepUp gate -- is deleted with the two-body model; every body runs the SP path.)</summary>
        void StepMoveOnce(float strafe, float forward, bool jump, float delta,
                          out bool wasAirborne, out float verticalVel, out bool groundedEntering)
        {
            bool grounded = IsOnFloor();
            groundedEntering = grounded;
            var v = _move.Step(new UnityEngine.Vector2(strafe, forward), jump, grounded, delta);
            Vector3 world = GlobalTransform.Basis * new Vector3(v.x, 0f, -v.z);
            wasAirborne = !grounded;                     // ground state going into this step
            Velocity = new Vector3(world.X, v.y, world.Z);
            StepUp(delta, grounded);   // climb small curbs/thresholds so we don't snag (master)
            MoveAndSlide();
            verticalVel = v.y;
        }
    }
}
