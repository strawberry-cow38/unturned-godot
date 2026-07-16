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
        bool _xHeld, _zHeld; EPlayerStance _baseStance = EPlayerStance.STAND;   // intertwined stance state machine: X = crouch key, Z = prone key (master)
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
        Vehicle _driving; bool _fp;   // vehicle being driven + camera mode: _fp false = 3rd person (default), true = 1st; H toggles (on foot + driving)
        float _driveCamYaw, _driveCamPitch = 15f;   // 3rd-person driving orbit: mouse yaws/pitches the chase cam around the car (master)
        readonly bool _ugFp = System.Environment.GetEnvironmentVariable("UG_FP") == "1";   // render harness: force 1st-person to screenshot the FP viewmodel
        RiggedCharacter _body;        // live 3rd-person player model (RiggedCharacter), visible when !_fp
        // Damage feedback, both source-exact and fired from TakeDamage: the red hurt flash (PlayerUI.painAlpha) and the
        // camera flinch (PlayerLook.flinchLocalRotation, an angular kick perpendicular to the hit that decays to level).
        public float PainAlpha;                     // PlayerUI.pain: red overlay alpha, set on hit, fades at 1/s
        Quaternion _flinch = Quaternion.Identity;   // PlayerLook.flinchLocalRotation: camera kick, recovers at 4/s

        [Export] public float MouseSensitivity = 0.12f;
        public int Ammo = 30;
        public int Kills { get; private set; }

        public float Health = 100f;
        public float MaxHealth = 100f;
        public int Deaths;
        public bool Bleeding;      // HUD status indicator: set briefly after taking a hit (PlayerLifeUI's bleedingBox)
        double _bleedTimer;
        public bool Broken;        // PlayerLife.isBroken: broken legs (from a hard fall) -- blocks sprint + jump until mended
        // Survival vitals (0..1), shown live on the HUD. Rates are config-driven in Unturned (modeConfigData); these
        // are sensible stand-ins: stamina drains while sprinting + regens otherwise; food/water slowly decay; health
        // regenerates while fed + hydrated (PlayerLife gates regen on food/water) or bleeds while starved/dehydrated.
        public float Stamina = 1f, Food = 1f, Water = 1f;
        float _staminaRegenDelay;   // seconds to wait after releasing sprint before stamina regenerates
        public float Infection;   // 0..1 virus; zombie bites raise it (Zombie.askDamage's player.life.askInfect(b/3))
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
            if (hit.Count > 0) pos = (Vector3)hit["position"];
            pos += new Vector3(_rng.RandfRange(-0.125f, 0.125f), 0f, _rng.RandfRange(-0.125f, 0.125f));
            WorldItem.Spawn(GetParent(), item, pos);
        }

        WorldItem _focusItem;   // the dropped item the player is currently LOOKING AT (glowing + named), pickup target for E
        Vehicle _focusVehicle;  // the vehicle the player is LOOKING AT (outlined + info panel), enter target for E
        Deployable _focusDeployable;  // the placed deployable (generator) the player is LOOKING AT (outlined + HP/fuel billboard)
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
            WorldItem hitItem = null; Vehicle hitVeh = null; Deployable hitDeploy = null;
            if (!_dead && _driving == null && _cam != null && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                var space = GetWorld3D().DirectSpaceState;
                Vector3 from = _cam.GlobalPosition;
                Vector3 fwd = -_cam.GlobalTransform.Basis.Z;
                // 1) ray forward -> the sphere sits where the ray STOPS (on world/props/items/vehicles, or max reach).
                // Query objects are REUSED across frames (they were alloc'd fresh every frame -> GC pressure = the "dips") -- master.
                _lookExclude ??= new Godot.Collections.Array<Rid> { GetRid() };
                _lookRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = (1u << 0) | (1u << 5) | (1u << 6) | (1u << 7), Exclude = _lookExclude };
                _lookRayQ.From = from; _lookRayQ.To = from + fwd * LookReach;
                var rhit = space.IntersectRay(_lookRayQ);
                _lookEnd = rhit.Count > 0 ? (Vector3)rhit["position"] : from + fwd * LookReach;
                // a placed deployable (generator) stops the ray on the world layer -> focus it directly from the ray hit
                // (LOS-correct: a wall in the way stops the ray first). The LookReach IS the look-at radius.
                if (rhit.Count > 0 && rhit["collider"].As<GodotObject>() is Deployable dep && IsInstanceValid(dep)) hitDeploy = dep;
                // 2) sphere at the ray end -> nearest ITEM (bit 7) or VEHICLE (bit 5) it overlaps is focusable
                _lookSphereQ ??= new PhysicsShapeQueryParameters3D { Shape = new SphereShape3D { Radius = LookSphereR }, CollisionMask = WorldItem.ItemHitLayer | (1u << 5), Exclude = _lookExclude };
                _lookSphereQ.Transform = new Transform3D(Basis.Identity, _lookEnd);
                float bestI = float.MaxValue, bestV = float.MaxValue;
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
                }
                if (hitItem != null && hitVeh != null) { if (bestV < bestI) hitItem = null; else hitVeh = null; }   // focus the nearer of the two
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
            else if (_focusDeployable != null && IsInstanceValid(_focusDeployable) && _focusDeployable.IsWreck)   // a burnt-out generator: same blowtorch salvage as a car wreck
            {
                Color red = new Color(0.90f, 0.25f, 0.20f), white = new Color(0.95f, 0.95f, 0.95f);
                var dp = _focusDeployable;
                if (dp.WreckOnFire) { dp.SetSalvagePrompt("Too hot to salvage", red); _salvageTimer = 0f; }
                else if (!HasBlowtorch) { dp.SetSalvagePrompt("Requires blowtorch to salvage", red); _salvageTimer = 0f; }
                else if (lmb)
                {
                    _salvageTimer += delta; sparks = true;
                    if (_salvageTimer >= SalvageTime) { dp.Salvage(); _focusDeployable = null; _salvageTimer = 0f; sparks = false; }
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
            SaveGunState(); _heldItem = null; _heldConsumable = null; ClearDeployable();   // stash the outgoing gun's state; equipping a melee REPLACES any held consumable (not a layer)
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
            _heldItem = null; Gun = null; _heldConsumable = null; _heldConsumableMesh = null;
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
            return false;
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
        public bool Unarmed => Gun == null && _heldConsumable == null && _deployable == null && (_melee == null || _melee.Name == "fists");

        // Is this inventory item the one currently IN HAND? (drives the inventory's Equip<->Dequip toggle.)
        public bool IsHeld(ItemAsset asset, SDG.Unturned.Item item)
        {
            if (asset == null) return false;
            if (Gun != null && _melee == null && _heldConsumable == null && _deployable == null)
                return item != null ? ReferenceEquals(_heldItem, item) : (_heldItem != null && _heldItem.id == asset.id);
            if (_melee != null && _melee.Name != "fists") return asset.meleeName != null && asset.meleeName == _heldMeleeName;
            if (_heldConsumable != null) return _heldConsumable.id == asset.id;
            if (_deployable != null) return _deployable.Id == asset.id;
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
                Consume(_heldConsumable);   // apply Health/Food/Water/etc.
                ushort id = _heldConsumable.id;
                var asset = _heldConsumable; string mesh = _heldConsumableMesh;
                GD.Print($"[consume] consumed {_heldConsumable.itemName}");
                _heldConsumable = null;             // one use per item: this one leaves the hand + is deleted (master)
                Inventory?.removeItemAmount(id, 1);  // delete the one that was eaten
                if ((Inventory?.getItemCount(id) ?? 0) > 0)
                    EquipHeldConsumable(asset, mesh, captureRevert: false);   // still have another of the same type -> auto-equip a FRESH one (keep the original revert target)
                else
                    (_revertEquip ?? EquipUnarmed)();   // stack empty -> revert to the last-held item if still valid, else fists (strawberry)
            }
        }

        // test-only: drive the eat/drink timer from a headless self-test (--consumeholdtest)
        public void DebugConsumeTick(float dt) => TickConsume(dt);

        // Equip a deployable to the hands: empty-hand carry + a world-space placement ghost that follows your aim
        // (blue valid / red invalid). LMB plants a real object. (src UseableBarricade equip/tick/startPrimary.)
        public void EquipHeldDeployable(DeployableDef def, SDG.Unturned.Item backing = null)
        {
            if (def == null) return;
            SaveGunState();
            if (_deployable == null) _revertEquip = CaptureHeldForRevert();   // fresh switch INTO a deployable -> remember what to fall back to when the last one is placed
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldConsumableMesh = null;
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
                    Deployable.Spawn(GetParent(), _deployable, _placePoint, _placeYaw);
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
        static AudioStreamWav LoadWavOneShot(string resPath)
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
            return new AudioStreamWav { Data = pcm, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = channels == 2, LoopMode = AudioStreamWav.LoopModeEnum.Disabled };
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
            if (staminaCost > 0f) { Stamina = Mathf.Max(0f, Stamina - staminaCost); _staminaRegenDelay = 1f; }
            // cooldown = this weapon's actual swing-anim length (per-weapon: knife fast, sledge slow) so click-spam can't
            // out-pace the swing cadence + the rate matches the animation (master). Fallback for fists / a missing clip.
            _meleeCd = _viewmodel?.MeleeSwingLength(strong) ?? 0f;
            if (_meleeCd <= 0.05f) _meleeCd = strong ? 0.75f : 0.45f;
            _viewmodel?.SwingMelee(strong);   // source Weak / Strong swing anim
            float alert = _melee?.Alert ?? 0f;
            if (alert > 0f) SoundBus.Emit(GetTree(), GlobalPosition, alert);   // swing NOISE fires with the swing (source AlertTool.alert); 0 = stealthy
            // DAMAGE lands at the END of the swing (source: isDamageable is only true once the swing anim has played),
            // NOT instantly on click -- scheduled here and applied by the tick, re-evaluating targets when it connects (master).
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
                if (!HasBlowtorch) { _focusDeployable.TakeDamage((_melee?.VehicleDamage ?? 10f) * mult); GD.Print($"[melee] hit {_focusDeployable.Def?.Name} for {(_melee?.VehicleDamage ?? 10f) * mult:0}"); }
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
            const float threshold = 22.0f;
            if (verticalVel >= -threshold) return;             // a normal jump lands at ~7 m/s -> no damage
            Broken = !(Inventory?.PreventsFallingBoneBreak ?? false);   // legs break on a hard fall UNLESS worn clothing has Prevents_Falling_Broken_Bones (source PlayerLife:2436)
            float armored = Mathf.Abs(verticalVel) * (Inventory?.FallingDamageMultiplier ?? 1f) * Skills.StrengthFallMultiplier();   // worn clothing (whole-body product) + STRENGTH skill both cut fall damage (source PlayerLife 2428-2430)
            int dmg = Mathf.RoundToInt(Mathf.Min(101f, armored));   // RoundAndClampToByte; damage <= 101
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
                    float times = 1f - range / radius;
                    bool wd = z.Dead;
                    z.DamageHit(zombieDamage * times, z.GlobalPosition, (z.GlobalPosition - point).Normalized());
                    if (!wd && z.Dead) Kills++;
                }
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))   // source DamageTool.explode also damages vehicles (Grenade.dat Vehicle_Damage 100)
                if (n is Vehicle v && !v.Exploded)
                {
                    float range = v.GlobalPosition.DistanceTo(point);
                    if (range > radius) continue;
                    if (ExplosionBlocked(point, v.GlobalPosition)) continue;
                    v.TakeDamage(vehicleDamage * (1f - range / radius));   // linear falloff (port's simplified explosion model)
                }
            float pr = GlobalPosition.DistanceTo(point);
            if (pr <= radius && !ExplosionBlocked(point, GlobalPosition)) { float t = 1f - (pr / radius) * (pr / radius); if (t > 0f) TakeDamage(playerDamage * t * (Inventory?.ExplosionArmor ?? 1f)); }   // wall blocks it (LoS) + worn clothing cuts it (source getPlayerExplosionArmor)
            Local?.FlinchFromExplosion(point, Mathf.Max(radius * 2f, 12f), 30f);   // camera shake toward the blast (real Bomb effects ~16r/30mag)
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
        public static PlayerController Local;   // the interactive player (set in _Ready); explosions shake THIS camera
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
            var g = new Grenade { Thrower = this, Vel = fwd * 16f + Vector3.Up * 5f + Velocity };   // arc forward + inherit motion
            GetParent().AddChild(g);
            g.GlobalPosition = (_cam?.GlobalPosition ?? GlobalPosition) + fwd * 0.5f;
            GD.Print("[grenade] thrown");
        }

        StorageCrate _openCrate;

        // F: open the nearest storage crate within ~2.5 m -- loads its grid into the STORAGE page (7) so the existing
        // dashboard + TryDrag handle it, and opens the dashboard.
        public bool OpenNearestCrate()
        {
            StorageCrate near = null; float best = 6.25f;   // 2.5 m, squared
            foreach (var n in GetTree().GetNodesInGroup("crates"))
                if (n is StorageCrate c)
                {
                    float d = GlobalPosition.DistanceSquaredTo(c.GlobalPosition);
                    if (d < best) { best = d; near = c; }
                }
            if (near == null) return false;
            _openCrate = near;
            CopyPage(near.Storage, Inventory.items[PlayerInventory.STORAGE], near.Width, near.Height);
            GD.Print($"[crate] opened ({near.Storage.getItemCount()} items)");
            _invUI?.Open();
            Input.MouseMode = Input.MouseModeEnum.Visible;
            return true;
        }

        // save the open crate's contents back and clear the STORAGE view (called when the dashboard closes)
        void CloseCrate()
        {
            if (_openCrate == null) return;
            CopyPage(Inventory.items[PlayerInventory.STORAGE], _openCrate.Storage, _openCrate.Width, _openCrate.Height);
            var s = Inventory.items[PlayerInventory.STORAGE];
            s.clear(); s.loadSize(0, 0);
            _openCrate = null;
        }

        static void CopyPage(SDG.Unturned.Items from, SDG.Unturned.Items to, byte w, byte h)
        {
            to.clear(); to.loadSize(w, h);
            for (byte i = 0; i < from.getItemCount(); i++)
            {
                var j = from.getItem(i);
                to.addItem(j.x, j.y, j.rot, j.item);
            }
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
            if (IsDriving) return Mathf.Clamp(48f * _driving.ForwardSpeedPct(), 1f, 64f);   // source DRIVING: DETECT_FORWARD(48) * fwd-speed% -> loud at speed, ~silent when parked
            float move = Moving ? 1.1f : 1f;                       // DETECT_MOVE
            float r = _move.Stance switch
            {
                EPlayerStance.SPRINT => 20f * move,                // DETECT_SPRINT
                EPlayerStance.CROUCH => 6f * move,                 // DETECT_CROUCH
                EPlayerStance.PRONE  => 3f * move,                 // DETECT_PRONE
                _ => 12f * move,                                   // DETECT_STAND
            };
            return Mathf.Clamp(r, 1f, 64f);
        }

        // When set (e.g. by a recorded demo or a net-driven bot), overrides keyboard input: x=strafe, y=forward.
        public UnityEngine.Vector2? ScriptedInput;
        // Likewise forces the stance (bypassing the Shift/Ctrl/Z keys) for demos, bots, and self-tests.
        public EPlayerStance? ScriptedStance;

        void UpdateHitbox(EPlayerStance stance)   // collision capsule per stance (STAND 2 / CROUCH 1.2 / PRONE 0.8), bottom pinned to the feet
        {
            float h = PlayerMovementDef.HeightForStance(stance);
            if (Mathf.Abs(h - _capStance) < 0.001f) return;
            _capStance = h; _capsule.Height = h; _hitbox.Position = new Vector3(0f, h / 2f, 0f);
        }

        const float StepHeight = 0.5f;   // curbs/thresholds up to this high are stepped over (master: stop snagging on sidewalks; bumped 0.4->0.5)
        // If the horizontal motion is blocked at foot level but clear a step higher, raise onto the step; FloorSnapLength then
        // pulls us back down onto it. Reused by both the player and zombies (source has stair/ledge handling in PlayerMovement).
        void StepUp(float delta)
        {
            if (!IsOnFloor()) return;
            Vector3 motion = new Vector3(Velocity.X, 0f, Velocity.Z) * delta;
            if (motion.LengthSquared() < 1e-6f) return;
            var raised = new Transform3D(GlobalTransform.Basis, GlobalPosition + Vector3.Up * StepHeight);
            if (TestMove(GlobalTransform, motion) && !TestMove(raised, motion))
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

        void Respawn()
        {
            _dead = false;
            Health = MaxHealth;
            Stamina = Food = Water = 1f; Infection = 0f; Bleeding = false; Broken = false;   // fresh vitals on respawn
            GlobalPosition = Spawn;
            Velocity = Vector3.Zero;
            _corpse?.QueueFree(); _corpse = null;
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

        // Survival sim driving the live HUD vitals. The mechanism is source-accurate (PlayerLife: stamina burns while
        // sprinting + regens otherwise; health regenerates only while fed AND hydrated; you take damage when food or
        // water bottoms out). The RATES are stand-ins -- Unturned's real ones live in server modeConfigData, not the
        // binary -- so they're tuned to be visible, not eyeballed from the game.
        void UpdateVitals(bool moving, float dt)
        {
            if (_dead) return;
            bool sprinting = moving && _move.Stance == EPlayerStance.SPRINT;
            if (sprinting) { Stamina = Mathf.Max(0f, Stamina - 0.22f * dt * Skills.ExerciseStaminaDrainMultiplier()); _staminaRegenDelay = 1f; }   // EXERCISE slows the drain; hold regen 1s after releasing sprint
            else { _staminaRegenDelay = Mathf.Max(0f, _staminaRegenDelay - dt); if (_staminaRegenDelay <= 0f) Stamina = Mathf.Min(1f, Stamina + 0.33f * dt * Skills.CardioStaminaRegenMultiplier()); }   // CARDIO speeds the regen
            Food  = Mathf.Max(0f, Food  - 0.0050f * dt * Skills.SurvivalDrainMultiplier());   // SURVIVAL slows hunger
            Water = Mathf.Max(0f, Water - 0.0070f * dt * Skills.SurvivalDrainMultiplier());   // SURVIVAL slows thirst
            Infection = Mathf.Max(0f, Infection - 0.01f * dt);       // virus slowly clears if you stop getting bitten
            bool sick = Infection > 0.75f;                           // heavy infection makes you ill (loses health)
            if (Food > 0.30f && Water > 0.30f && Health < MaxHealth && !sick)
                Health = Mathf.Min(MaxHealth, Health + 2f * dt * Skills.VitalityRegenMultiplier());     // VITALITY speeds regen while fed + hydrated (blocked while sick)
            else if (Food <= 0f || Water <= 0f || sick)
                Health = Mathf.Max(0f, Health - (sick ? 2f : 1.5f) * dt);   // starve / dehydrate / infection sickness
            if (Health <= 0f) { Deaths++; Die(); }
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
            _melee = null; _heldConsumable = null; _heldMeleeName = null; ClearDeployable();   // equipping a gun REPLACES the held consumable/melee/deployable (not a layer) -- master
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { GunName = _gunName };
            AddChild(_viewmodel);
            RelinkViewmodelLighting();   // a re-equipped viewmodel must re-take the world lighting, else it renders fullbright (master: Drive PEI)
            if (backingItem != null && backingItem.gunAttach >= 0) _viewmodel.ApplyAttachMask(backingItem.gunAttach);   // restore the gun's saved attachments (e.g. a detached suppressor stays off) -- master
            GD.Print($"[gun] holding {_gunName}");
        }

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
            _cam = new Camera3D { Position = new Vector3(0, 1.6f, 0), Current = true, PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off };
            _cam.CullMask &= ~OutlineOverlay.OutlineLayer;   // don't render the items' silhouette meshes in the main view (only the offscreen mask cam does)
            AddChild(_cam);
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
            if (CaptureMouse) Local = this;   // the interactive (mouse-captured) player -> explosions shake this camera

            _body = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));   // live 3rd-person body
            if (_body != null) { _body.Visible = false; CallDeferred(Node.MethodName.AddSibling, _body); }
            _viewmodel = new Viewmodel { GunName = _gunName };   // per-gun visuals
            AddChild(_viewmodel);
            _rng.Randomize();

            // the ported inventory + its dashboard. Demo-populate it (real items) so there's something to show.
            ItemCatalog.RegisterAll();
            Inventory = new PlayerInventory();
            PopulateDemoInventory();
            _invUI = new InventoryUI { Inv = Inventory, Player = this };
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
            // Inventory dashboard open -> EAT ALL game input except Tab (to close it) + Escape: no firing / world interactions /
            // reloading / look through the open UI. (The UI Controls still get their own clicks; those don't reach _UnhandledInput.) (master)
            if (_invUI != null && _invUI.IsOpen && !(@event is InputEventKey { Keycode: Key.Tab or Key.Escape })) return;
            // while driving, only E (exit) / V (cam) / L (lights) / Escape + LMB (horn) / RMB (lights) are live -- no fire, aim, reload, etc.
            if (_driving != null)
            {
                bool allowedKey = @event is InputEventKey { Pressed: true } dk && (dk.Keycode == Key.E || dk.Keycode == Key.H || dk.Keycode == Key.L || dk.Keycode == Key.Ctrl || dk.Keycode == Key.Escape);
                bool allowedMouse = @event is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right };
                bool camOrbit = @event is InputEventMouseMotion;   // mouse MOTION must pass through -> it orbits the 3rd-person chase cam (this guard was silently eating it, so the cam sat fixed) (strawberry 2026-07-15)
                if (!allowedKey && !allowedMouse && !camOrbit) return;
            }
            // clicks belong to an open UI (inventory / crate / dashboard) when the cursor's visible -- don't fire / honk / aim THROUGH them (master)
            if (@event is InputEventMouseButton && Input.MouseMode != Input.MouseModeEnum.Captured) return;
            if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                if (_driving != null && !_fp)   // driving in 3rd person: the mouse ORBITS the chase cam around the car instead of turning the driver (master)
                {
                    _driveCamYaw -= mm.Relative.X * MouseSensitivity;
                    _driveCamPitch = Mathf.Clamp(_driveCamPitch + mm.Relative.Y * MouseSensitivity, -25f, 70f);   // inverted Y: mouse up -> cam tilts down (strawberry)
                }
                else if (_driving == null)
                {
                    RotateY(Mathf.DegToRad(-mm.Relative.X * MouseSensitivity));
                    _pitchDeg = Mathf.Clamp(_pitchDeg - mm.Relative.Y * MouseSensitivity, -89f, 89f);
                    _cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
                }
            }
            else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                if (_driving != null) _driving.Honk();                 // LMB while driving: horn
                else if (_build != null && _build.Active) _build.Place();   // build mode: place a structure
                else if (HoldingDeployable) TryPlaceDeployable();       // holding a deployable: LMB plants it at the ghost
                else if (HoldingConsumable) StartConsume();             // holding a food/drink: LMB eats/drinks it
                else if (IsRepeatedMelee) { }                          // Repeated tool (blowtorch/chainsaw): LMB is a continuous HOLD driven by the use-tick (UpdateSalvage), never a swing/punch (source UseableMelee.startPrimary: isRepeated -> startSwing)
                else if (_melee != null) MeleeAttack(false);            // LMB with a normal melee = WEAK swing (source UseableMelee)
                else StartFire();
            }
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } rmb)
            {
                if (_driving != null) { if (rmb.Pressed) _driving.ToggleHeadlights(); }   // RMB while driving: toggle lights
                else if (HoldingDeployable) { if (rmb.Pressed) Dequip(); }   // RMB cancels placement entirely -> empty hands (strawberry)
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
                if (_driving != null) ExitVehicle();                       // hop out of the vehicle
                else if (TryToggleHitch()) { }                             // on foot at a trailer hitch: couple / uncouple
                else if (_focusItem != null) TryPickup();                  // looking at an item: pick it up
                else if (_focusVehicle != null && IsInstanceValid(_focusVehicle) && !_focusVehicle.IsWreck && !_focusVehicle.IsTrailer) EnterVehicle(_focusVehicle); // looking at a LIVE, drivable vehicle: get in (a wreck is salvaged with LMB; a trailer is towed, not driven)
                else if (CropManager.NearestGrown(GlobalPosition) is CropNode grownCrop) CropManager.Harvest(grownCrop, this);  // harvest a nearby fully-grown crop (source InteractableFarm harvest)
                else if (OpenNearestCrate()) { }                           // open a nearby storage crate
                else if (_melee != null) _viewmodel?.PlayMeleeInspect();   // nothing to interact with -> inspect (melee plays its own Inspect clip)
                else _viewmodel?.PlayInspect();                            // ...or the gun's own inspect
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
        void PopulateDemoInventory()
        {
            Inventory.wearBackpack(new Item(253));   // Alicepack -> backpack slot + 8x7 storage
            Inventory.wearPants(new Item(209));      // Cargo Pants -> pants slot + 6x3 storage
            Inventory.equipToSlot(0, new Item(4));     // Eaglefire -> primary
            Inventory.equipToSlot(1, new Item(363));   // Maplestrike -> secondary
            // items DON'T stack (Unturned is grid-based): each is its own single (amount-1) grid item.
            Inventory.items[2].tryAddItem(new Item(15));            // Medkit in pockets
            Inventory.items[2].tryAddItem(new Item(95));            // Bandage
            Inventory.items[2].tryAddItem(new Item(95));            // Bandage (separate slot -- no stacking)
            Inventory.items[2].tryAddItem(new Item(14));            // Bottled Water
            var bag = Inventory.items[PlayerInventory.BACKPACK];
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
            Inventory.items[PlayerInventory.PANTS].tryAddItem(new Item(13));  // Canned Beans in pants
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
                SpawnBullet(muzzle, dir * muzzleVel, steps, gravity, damage, vehDamage);
            }
            // AlertTool point-noise: an unsuppressed gunshot pulls zombies within earshot over to investigate. A silenced
            // barrel skips the alert ENTIRELY (source UseableGun ~936: only alert if barrel==null || !isSilenced) -> stealth.
            if (!(_viewmodel?.IsSuppressed ?? false)) SoundBus.Emit(GetTree(), GlobalPosition, SoundBus.Gunshot);   // Phase 3 sound bus: unsuppressed gunshot loudness (suppressed = silent)
            // bolt/pump: this shot needs the action cycled before the next one (source RechamberAfterShotCount -> needsRechamber)
            if (Gun != null && Gun.RechamberAfterShotCount > 0 && ++_shotCountForRechamber >= Gun.RechamberAfterShotCount)
            { _needsRechamber = true; _rechamberDelayTimer = Gun.RechamberAfterShotDelay; }
            SaveGunState();   // keep the backing item's ammo current so a drop/holster mid-fight preserves it (master)
            return true;   // shot fired; the actual hits/kills land later in StepBullets
        }

        // A simulated bullet (Unturned's BulletInfo): flies from the muzzle with a velocity, dropping under gravity,
        // stepped every physics tick; its tracer travels with it; it hits/despawns on contact or after its steps.
        sealed class Bullet { public Vector3 Pos, Vel, Origin; public int StepsLeft; public float Gravity, Damage, VehicleDamage; public MeshInstance3D Tracer; public Node3D RocketVis; }
        readonly System.Collections.Generic.List<Bullet> _bullets = new();

        void SpawnBullet(Vector3 pos, Vector3 vel, int steps, float gravity, float damage, float vehicleDamage)
        {
            var b = new Bullet { Pos = pos, Origin = pos, Vel = vel, StepsLeft = Mathf.Max(1, steps), Gravity = gravity, Damage = damage, VehicleDamage = vehicleDamage, Tracer = MakeTracer() };
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
                Vector3 next = b.Pos + b.Vel * 0.02f;
                var query = PhysicsRayQueryParameters3D.Create(b.Pos, next, (1u << 0) | (1u << 1) | (1u << 4) | (1u << 5) | (1u << 6) | (1u << 9)); // world + enemy + ragdoll + vehicle + props + water surface
                var hit = space.IntersectRay(query);
                if (hit.Count > 0)
                {
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
                        SpawnSurfaceImpact(point, hit["normal"].AsVector3(), sf);
                    }
                    if (Gun?.Action == "Rocket") { Explode(point, 9f, 250f, 200f, 300f); GD.Print("[rocket] launcher warhead detonated"); }   // rocket launcher: AoE blast on impact (vehicles hit hardest), reusing the grenade explode
                    RemoveBullet(i);
                    continue;
                }
                b.Pos = next;
                b.Vel += new Vector3(0f, b.Gravity * 0.02f, 0f);
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
            if (_interpReady && !_dead && _driving == null)   // RENDER INTERPOLATION (master): lerp the visual position between the last two 50Hz ticks so it doesn't step at 50Hz while rendering at 60+
                GlobalPosition = _interpPrev.Lerp(_interpCurr, (float)Engine.GetPhysicsInterpolationFraction());
            if (_driving != null && !_dead)   // driving: position the cam from the vehicle's Godot-INTERPOLATED visual transform, so cam + car mesh are both smooth + IN SYNC (master: godot smoothing for the car)
                PositionDriveCam(_driving.GetGlobalTransformInterpolated());
            { ulong _t = Time.GetTicksUsec(); UpdateLookFocus(); Prof.Add("lookat", _t); }   // eye-ray -> focus the item you're aiming at
            if (_showLookHulls) UpdateLookHullViz();                                          // I-toggle: rebuild the look-hull wireframes
            { ulong _t = Time.GetTicksUsec(); UpdateSalvage((float)delta); Prof.Add("salvage", _t); }   // wreck salvage prompt + blowtorch teardown
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
            if (_cam != null && !_dead && _driving == null)   // while driving, DriveVehicle (in _PhysicsProcess) owns the cam
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
            if (_viewmodel != null) _viewmodel.SetShown(_fp && _driving == null && !_dead);   // FP gun arms: first-person on foot only
            if (_body == null) return;
            _body.Visible = !_fp && !_dead;   // dead -> the corpse ragdoll handles the body
            if (_fp || _dead) { return; }
            if (_driving != null)   // in the driver seat (best-effort idle pose)
            {
                _body.GlobalTransform = _driving.GlobalTransform * new Transform3D(Basis.Identity, _driving.SeatOffset);   // per-vehicle driver seat (prefab Seat_0)
                _body.PlayLoop(_body.ClipLength("Idle_Drive") > 0f ? "Idle_Drive" : "Idle_Sit");   // seated DRIVING pose (hands on the wheel) instead of a standing idle (master)
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

        void EnterVehicle(Vehicle v)
        {
            _driving = v;
            _burstLeft = 0;                                    // entering a vehicle cancels an in-progress burst (no resume on exit)
            v.EngineOn = true;                                 // start burning fuel (source: engine on)
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
            _driving.Drive(throttle, steer, !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Space));
            GlobalPosition = _driving.GlobalPosition;   // ride along so exit + FP cam land at the vehicle (the cam is positioned in _Process from the vehicle's INTERPOLATED transform)
        }

        void PositionDriveCam(Transform3D vt)   // FP / chase cam from the (interpolated) vehicle transform. Full global transform atomically
        {                                        // (position + orientation): a LookAt updated pos but not rotation through turns -> car slid out of frame.
            if (_cam == null) return;
            var fwd = -vt.Basis.Z; fwd.Y = 0f;
            fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
            if (_fp)   // first-person from the driver's head, looking forward over the hood
            {
                var eyeL = _driving.DriverEyeLocal;   // per-vehicle: tall cabs sit higher so the view clears a long hood
                var eye = vt * eyeL;
                _cam.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(vt * (eyeL + new Vector3(0f, -0.6f, -3.9f)), Vector3.Up);
            }
            else            // third-person chase: ORBIT behind the car (mouse yaw/pitch), AUTO-ZOOMED for the vehicle's size (master)
            {
                float size = _driving.WorldMeshAabb().Size.Length();          // bounding diagonal -> bigger vehicle, further back
                if (_driving.CoupledTrailer != null && IsInstanceValid(_driving.CoupledTrailer))
                    size += _driving.CoupledTrailer.WorldMeshAabb().Size.Length() * 0.7f;   // towing -> pull the cam out further so the whole rig stays in frame (strawberry)
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
            if (!_dead && GlobalPosition.Y < -1030f) { GD.Print("[oob] fell below the map -> killed"); TakeDamage(9999f); }
            if (_driving != null) { _interpReady = false; DriveVehicle((float)delta); return; }   // driving: skip on-foot movement (+ pause the render-interp so exiting doesn't smear)
            if (_interpReady && !_dead) GlobalPosition = _interpCurr;   // render-interp (master): restore the TRUE physics position before moving (undoes the _Process visual smoothing)
            StepBullets();   // advance in-flight bullets (travel + drop) each 50 Hz tick — matches the source 0.02s step
            if (_bleedTimer > 0) { _bleedTimer -= delta; if (_bleedTimer <= 0) Bleeding = false; }
            if (_dead)
            {
                _deathTimer -= delta;
                Velocity = Vector3.Zero;
                if (_deathTimer <= 0) Respawn();
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
                else if (_firemode == FireMode.Auto && !UiInputBlocked && Input.IsMouseButtonPressed(MouseButton.Left)) Fire();
            }

            // Intertwined stance STATE MACHINE (master): X = crouch key, Z = prone key, moving between STAND/CROUCH/PRONE from ANY state.
            bool xNow = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.X);
            if (xNow && !_xHeld) _baseStance = (_baseStance == EPlayerStance.CROUCH) ? EPlayerStance.STAND : EPlayerStance.CROUCH;   // X: stand<->crouch, and prone->crouch
            _xHeld = xNow;
            bool zNow = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Z);
            if (zNow && !_zHeld) _baseStance = (_baseStance == EPlayerStance.PRONE) ? EPlayerStance.STAND : EPlayerStance.PRONE;    // Z: stand<->prone, and crouch->prone
            _zHeld = zNow;
            var wantStance = ScriptedStance ?? _baseStance;
            if (wantStance == EPlayerStance.STAND && !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Shift) && Stamina > 0.05f) wantStance = EPlayerStance.SPRINT;   // sprint overlays standing
            if (Broken && wantStance == EPlayerStance.SPRINT) wantStance = EPlayerStance.STAND;   // broken legs can't sprint (PlayerStance.cs:703)
            // can't rise into a ceiling: if the target stance is TALLER than the current capsule and there's no headroom, stay low (master)
            float wantH = PlayerMovementDef.HeightForStance(wantStance);
            if (wantH > _capStance + 0.01f && _capStance > 0f && !HeadroomFor(wantH))
                wantStance = _baseStance = (_capStance <= PlayerMovementDef.HEIGHT_PRONE + 0.01f) ? EPlayerStance.PRONE : EPlayerStance.CROUCH;   // blocked overhead -> stay in the stance that fits
            _move.Stance = wantStance;
            UpdateHitbox(_move.Stance);   // resize the collision capsule to match the stance (source HeightForStance)

            float forward, strafe;
            if (ScriptedInput.HasValue) { strafe = ScriptedInput.Value.x; forward = ScriptedInput.Value.y; }
            else if (UiInputBlocked) { forward = 0f; strafe = 0f; }   // menu open -> don't walk through it
            else
            {
                forward = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
                strafe  = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            }
            bool jump = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Space) && !Broken;   // broken legs can't jump (PlayerMovement.cs:1310)

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

            var v = _move.Step(new UnityEngine.Vector2(strafe, forward), jump, IsOnFloor(), (float)delta);
            Vector3 world = GlobalTransform.Basis * new Vector3(v.x, 0f, -v.z);
            bool wasAirborne = !IsOnFloor();                  // ground state going into this step
            Velocity = new Vector3(world.X, v.y, world.Z);
            StepUp((float)delta);   // climb small curbs/thresholds so we don't snag (master)
            MoveAndSlide();
            _interpPrev = _interpReady ? _interpCurr : GlobalPosition; _interpCurr = GlobalPosition; _interpReady = true;   // snapshot this tick's start/end for render interpolation (master)
            if (wasAirborne && IsOnFloor()) CheckFallDamage(v.y);   // just touched down -> fall damage on a hard landing
        }
    }
}
