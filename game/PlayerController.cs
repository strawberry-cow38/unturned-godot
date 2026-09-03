using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // First-person player: ported PlayerMovementSim on Godot's 50 Hz physics tick + mouse look + a hitscan
    // gun (raycast from the camera vs the zombie collision layer). Movement CONSTANTS are exact; feel goes
    // through Jolt. Builds its own camera + capsule collider so it can be spawned from code.
    // WASD move / Shift sprint / X crouch (C hold) / Z prone / Q,E lean / Space jump / LMB fire / G melee / H grenade / R reload / Esc release mouse.
    public partial class PlayerController : CharacterBody3D
    {
        readonly PlayerMovementSim _move = new PlayerMovementSim();
        readonly PlayerStanceSim _stance = new PlayerStanceSim();   // intertwined stance state machine (X = crouch, Z = prone), extracted to the engine-free sim-core (MP_PLAN §3.4)
        CapsuleShape3D _capsule; CollisionShape3D _hitbox; float _capStance = -1f;   // hitbox capsule, resized per stance (source HeightForStance)
        Camera3D _cam;
        Node3D _leanPivot;      // rolls for lean; the camera is its child (see the construction site for why it matters)

        // ---- LEANING (strawberry; ported from PlayerLook + PlayerAnimator) -------------------------------------------
        // Q left / E right. The angle is TWENTY degrees, not the ~45 that was assumed -- HumanAnimator.LEAN = 20,
        // consumed at PlayerLook.cs:744 as Quaternion.Euler(0, 0, lean * LEAN) lerped at 4*delta.
        public const float LeanDegrees = 20f;
        const float LeanLerp = 4f;             // source: the same 4*delta the eye-height and scope-sway lerps use

        // Obstruction geometry. Source isLeanSpaceEmpty sweeps a capsule from the EYES along the lean direction, and
        // because the sweep is (Reach - Radius) long with hemispherical caps of Radius, REACH IS THE WHOLE ANSWER: it
        // is the distance from your eye at which something starts refusing the lean. Radius only sets how much
        // clearance is wanted around that line -- vertically, and fore/aft.
        //
        // Retail is 1.2 / 0.4 (PlayerStance.RADIUS). We run looser (strawberry: "make leaning snap-out colliders a
        // little more lenient") -- 1.2 m demands roughly double the room the lean actually uses, so standing anywhere
        // near a doorframe or a parked car refused to lean at all.
        internal const float LeanReachRetail = 1.2f;
        internal const float LeanReach = 0.95f;
        const float LeanRadius = 0.3f;

        /// <summary>How far the eyes actually travel on a lean at a given eye height: the pivot is on the floor, so the
        /// head swings on an arc of that radius. This is the number the reach has to stay clear of.</summary>
        internal static float LeanPeek(float eyeHeight) => eyeHeight * Mathf.Sin(Mathf.DegToRad(LeanDegrees));

        /// <summary>The smallest reach that is still honest, at standing height: the peek itself plus room for the head
        /// that arrives there. Go under this and the check permits leans that put your face inside the wall, which is
        /// worse than the strictness it was loosened from -- it is the one direction "more lenient" must not go.</summary>
        internal const float LeanHeadRadius = 0.2f;

        /// <summary>Where the obstruction capsule sits along the lean direction, measured from the EYE: its centre and
        /// its total length. The near edge (Mid - Height/2) must be ZERO -- a capsule that starts behind the eye sweeps
        /// the side you are NOT leaning towards, and a wall touching that shoulder then blocks the lean away from it.
        /// Factored out so that invariant is assertable without a physics world, since no reachable wall position can
        /// tell the two shapes apart once the radius is small enough to hide the overhang.</summary>
        internal static (float Mid, float Height) LeanCapsuleSpan() => (LeanReach * 0.5f, LeanReach);
        int _lean;                             // +1 left, -1 right, 0 none -- source's own sign convention
        bool _leanQHeld, _leanEHeld;           // edge detection: the shoulder swap fires on PRESS, the lean on hold
        bool _leanObstructed;
        float _leanAngle;                      // current rolled degrees, lerped toward _lean * LeanDegrees
        Vector3 _interpPrev, _interpCurr; bool _interpReady;   // render interpolation: smooth the VISUAL position between the 50Hz physics ticks (master); rotation stays per-frame so the mouse is instant
        Viewmodel _viewmodel;
        public PlayerInventory Inventory;   // the ported 9-page inventory model
        InventoryUI _invUI;                 // the dashboard (Tab to open)
        public bool InventoryOpen => _invUI?.IsOpen ?? false;   // HUD hides the weapon/ammo readout while the bag is open (master 2026-08-26)
        CraftingMenu _craftMenu;            // the browsable recipe index (Y, or the inventory Craft tab)
        SkillsUI _skillsUI;                 // the skills menu (J to open) -- spend XP to level skills
        BuildTool _build;                   // B = build mode. C = construct, V = tier, LMB place, R salvage.

        /// <summary>Upgrade the aimed piece a tier in place. StructureManager.Upgrade had tests and no caller
        /// anywhere in the game -- the tier ladder existed only as an API, so nothing a player did could climb
        /// it. Same shape as the doors and beds that carried a TakeDamage nothing ever invoked: green tests
        /// proving a method works say nothing about whether it is reachable.</summary>
        void UpgradeAimedStructure()
        {
            var piece = AimedStructure();
            if (piece == null) return;
            var sm = StructureManager.Instance;
            int was = piece.Tier;
            if (sm.Upgrade(piece))
                GD.Print($"[build] upgraded {piece.Construct}: {StructureCatalog.TierAt(was).Name} -> {StructureCatalog.TierAt(piece.Tier).Name} ({piece.Health} hp)");
            else
                GD.Print($"[build] {piece.Construct} is already {StructureCatalog.TierAt(piece.Tier).Name} (top tier)");
        }

        /// <summary>The structure piece under the crosshair, or null. Shared by salvage/upgrade/melee so all
        /// three agree on WHICH piece you mean -- three separate raycasts drifting apart is how "salvage took
        /// the wrong wall" bugs start.</summary>
        StructureManager.Piece AimedStructure(float reach = -1f)
        {
            var sm = StructureManager.Instance;
            if (sm == null || _cam == null) return null;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return null;
            if (reach <= 0f) reach = StructureCatalog.MaxPlacementDistance;   // melee reaches less far than placement
            Vector3 from = _cam.GlobalPosition, dir = -_cam.GlobalTransform.Basis.Z;
            var q = PhysicsRayQueryParameters3D.Create(from, from + dir * reach);
            q.CollisionMask = 1u << 0;
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return null;
            // Resolve by the COLLIDER we actually hit, not by nearest-piece-within-3 m of the hit point. The
            // radius version picks by distance to each piece's ORIGIN, and an origin sits at the piece's base:
            // aim high on a wall and the floor tile beside it is nearer to the hit than the wall you are
            // looking at, so the swing lands on the floor -- or, past 3 m up, on nothing at all. Exactly the
            // mistake the barricade attach gate made, and worse here because all three callers destroy things.
            return sm.PieceForCollider(hit["collider"].As<Node>());
        }

        // Test seams for the three INPUT paths into the structure system. The manager-level Damage/Repair/
        // Salvage were covered while nothing verified that the crosshair path reached the piece you were
        // actually looking at -- the "logic tested, never called" gap, which had already bitten once (charges
        // did not touch structures at all). These drive the real methods, not copies of them.
        public BuildTool DebugBuildTool => _build;   // the BUILD INPUT path (B/C/V/LMB) had no coverage at all
        public StructureManager.Piece DebugAimedStructure(float reach = -1f) => AimedStructure(reach);
        public bool DebugMeleeStructure(float amount, float range) => MeleeStructure(amount, range);
        public void DebugSalvageAimed() => SalvageAimedStructure();
        public void DebugUpgradeAimed() => UpgradeAimedStructure();
        /// <summary>Aim the eye at a world point (the test stand-in for moving the mouse). Returns the eye
        /// transform actually in force, so a test can assert it took rather than assume it.
        ///
        /// Drives the SAME state real mouse input drives -- body yaw plus _pitchDeg -- rather than poking
        /// _cam.LookAt. A raw LookAt is transient: the player's own setup writes _cam.Rotation back and the aim
        /// silently reverts on the next frame, so a test that aimed, waited a tick, then acted was acting on a
        /// stale direction. That produced a suite where two different aims both reported forward = (0,0,-1) and
        /// a piece "placed at a tile centre" was really the no-hit fallback point.</summary>
        public Transform3D DebugLookAt(Vector3 target)
        {
            if (_cam == null) return Transform3D.Identity;
            Vector3 d = target - _cam.GlobalPosition;
            float horiz = new Vector2(d.X, d.Z).Length();
            if (d.LengthSquared() < 1e-6f) return _cam.GlobalTransform;
            RotationDegrees = new Vector3(0f, Mathf.RadToDeg(Mathf.Atan2(-d.X, -d.Z)), 0f);
            _pitchDeg = Mathf.Clamp(Mathf.RadToDeg(Mathf.Atan2(d.Y, horiz)), -89f, 89f);
            _cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
            return _cam.GlobalTransform;
        }
        public Transform3D DebugEye => _cam?.GlobalTransform ?? Transform3D.Identity;
        // The focus the look scan ACTUALLY resolved, so a test can assert which part of a car the crosshair
        // won rather than inferring it from what F happened to do. Reading the zone alone would not have caught
        // the first version of this: the zone maths was right and simply never ran.
        public Vehicle DebugFocusVehicle => _focusVehicle;
        public bool DebugFocusAccessValid => _focusAccessValid;
        public Vehicle.AccessZone DebugFocusAccess => _focusAccess;

        /// <summary>Swing at the structure piece under the crosshair. Returns true if one was hit, so the melee
        /// chain stops there rather than also swinging at whatever is behind it. A blowtorch repairs instead of
        /// hitting, matching how vehicles and deployables already behave.</summary>
        bool MeleeStructure(float amount, float range)
        {
            var piece = AimedStructure(range + 1.5f);
            if (piece == null) return false;
            var sm = StructureManager.Instance;
            bool metal = StructureCatalog.TierAt(piece.Tier).Name == "metal";
            if (HasBlowtorch)
            {
                int healed = sm.Repair(piece, Mathf.RoundToInt(amount));
                if (healed > 0) GD.Print($"[build] repaired {piece.Construct} +{healed}");
                return true;
            }
            var c = piece.Construct;
            int tier = piece.Tier;
            bool broke = sm.Damage(piece, Mathf.RoundToInt(amount));
            MeleeImpactFx(piece.Pos, false, metal ? Surf.Metal : Surf.Wood);
            GD.Print(broke
                ? $"[build] destroyed {StructureCatalog.TierAt(tier).Name} {c}"
                : $"[build] hit {c} for {amount:0} ({piece.Health}/{piece.MaxHealth})");
            return true;
        }

        // Chopping a tree: an eye-ray to the aimed tree trunk (the world-layer cylinder ResourceField gives each tree).
        // Called before the zombie/animal sweep so a swing fells the tree rather than an enemy standing behind it.
        bool MeleeTree(float amount, float range)
        {
            if (_cam == null) return false;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
            var rq = PhysicsRayQueryParameters3D.Create(from, from + fwd * (range + 1f));
            rq.CollisionMask = 1u << 0;   // world layer -- tree trunks live here
            rq.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(rq);
            if (hit.Count == 0) return false;
            var col = hit["collider"].As<GodotObject>();
            if (col is TreeTrunk tt && !tt.Felled)
            {
                var pt = (Vector3)hit["position"];
                tt.Chop(amount, pt, fwd);
                MeleeImpactFx(pt, false, Surf.Wood);
                GD.Print($"[melee] chopped tree for {amount:0}");
                return true;
            }
            if (col is OreRock ore && !ore.Mined)   // metal ore: only a PICKAXE (axe_pick) mines it -> Metal Scrap; other tools just clink
            {
                var pt = (Vector3)hit["position"];
                if (_heldMeleeName == "axe_pick") { float md = _melee?.ResourceDamage ?? amount; ore.Mine(md, pt, fwd); GD.Print($"[melee] mined ore for {md:0}"); }   // _heldItem is null for a melee; resources take Resource_Damage (pickaxe=100), not Zombie_Damage(34)
                MeleeImpactFx(pt, false, Surf.Metal);   // metal clink either way (feedback that you need a pickaxe)
                return true;
            }
            return false;
        }

        /// <summary>Salvage the structure piece under the crosshair. Uses the eye ray rather than the build
        /// ghost: the ghost sits at the slot you would BUILD into, which is next to the piece you are looking
        /// at, so salvaging off the ghost takes down the wrong thing (or nothing).</summary>
        void SalvageAimedStructure()
        {
            var piece = AimedStructure();
            if (piece == null) return;
            var c = piece.Construct;
            int tier = StructureManager.Instance.Salvage(piece);
            if (tier >= 0) GD.Print($"[build] salvaged {StructureCatalog.TierAt(tier).Name} {c}");
        }
        string _gunName = "eaglefire";   // gun folder name (eaglefire | maplestrike), derived from the .dat path
        float _pitchDeg;
        float _scopeSwayT, _swayAppliedP, _swayAppliedY;   // scope sway: phase clock + what it has already folded into the aim
        /// <summary>Scope sway's current contribution to the aim, degrees (pitch, yaw). Test seam: the claim is
        /// that sway moves the AIM, and a viewmodel-only sway would leave these at zero forever.</summary>
        public Vector2 DebugScopeSway => new Vector2(_swayAppliedP, _swayAppliedY);
        /// <summary>Test hook: sway as if a magnifying optic were mounted and aimed. See the note at the gate.</summary>
        public bool DebugForceScopeSway;
        /// <summary>The single recoil impulse rolled for the last shot, degrees (pitch, yaw). One roll, one
        /// destination -- if this is non-zero the AIM must have moved by it, and a viewmodel-only kick cannot.</summary>
        public Vector2 DebugLastRecoilKick;
        // LEARNABLE PATTERN: where we are in the current held burst. Resets after a gap, so tapping always
        // restarts at node 1 and a sustained hold walks the pattern. This is what makes tap-fire exact.
        int _patternShot; float _patternIdle;
        const float PatternResetSeconds = 0.35f;   // trigger gap that counts as a new burst
        public int DebugPatternShot => _patternShot;
        /// <summary>The viewmodel's rotational recoil, surfaced so a test can prove the gun stopped taking one.</summary>
        public Vector3 DebugViewmodelRecoilRot => _viewmodel?.DebugRecoilRot ?? Vector3.Zero;
        Train _ridingTrain;   // a boarded train (spline-follower, not a Vehicle) -- parallel low-risk ride path (master: "i cant get into the train")
        HarborCrane _ridingCrane;   // a boarded harbor crane (custom vehicle, same parallel ride path as the train)
        bool _jogWPrev, _jogSPrev;   // Ctrl+W/S edge-detect: jog the train exactly one carriage
        bool _craneMagPrev;   // Shift edge-detect: energise/de-energise the hoist magnet
        Vehicle _driving; bool _fp = true;   // vehicle being driven + camera mode: true = 1st person (spawn default, strawberry), false = 3rd; H toggles (on foot + driving)
        float _driveCamYaw, _driveCamPitch = 15f;   // 3rd-person driving orbit: mouse yaws/pitches the chase cam around the car (master)
        /// <summary>Seated look limits, taken from retail PlayerLook (clampYaw / clampPitch) rather than
        /// invented: a DRIVING seat clamps yaw to +/-160, any other seat to +/-90, and a seated pitch to
        /// MIN_ANGLE_SIT 60 / MAX_ANGLE_SIT 120 against a 0..180 scale where 90 is level -- +/-30 for us.</summary>
        const float DriverYawLimit = 160f, PassengerYawLimit = 90f, SeatedPitchLimit = 30f;
        // FP RIDE free-look (#37, MP only): mouse yaw/pitch of the view in VEHICLE-LOCAL space while seated on a
        // puppet in first person (real Unturned lets you look around while driving; the fixed forward gaze made the
        // default MP ride cam feel stuck). At (0, FpRideGazePitchDeg) this reproduces the SP fixed gaze exactly --
        // the old LookingAt(eyeL + (0,-0.6,-3.9)) target = atan(-0.6/3.9) below the vehicle's forward.
        float _rideLookYaw, _rideLookPitch = FpRideGazePitchDeg;
        const float FpRideGazePitchDeg = -8.75f;
        readonly bool _ugFp = System.Environment.GetEnvironmentVariable("UG_FP") == "1";   // render harness: force 1st-person to screenshot the FP viewmodel
        RiggedCharacter _body;        // live 3rd-person player model (RiggedCharacter), visible when !_fp
        /// <summary>Test seam: where the 3rd-person body actually ended up, in the driven vehicle's LOCAL frame.
        /// Null when there is no body or we are not driving. Asserting on the seat table instead would pass on a
        /// build that computes every seat correctly and still draws all of them on the driver's lap -- the
        /// function being right is not the same claim as the body using it.</summary>
        /// <summary>Test seam: is the 3rd-person body actually carrying a gun right now? Distinct from HasGunOut,
        /// which is about the PLAYER -- this is what everyone else sees, and the two disagreed for passengers.</summary>
        public bool DebugBodyHasGun => _bodyGunName != null && (_body?.GunLayerOn ?? false);
        /// <summary>Test seam: the looping seated clip the body is playing (driving mime vs plain sit).</summary>
        public string DebugBodyLoopClip => _body?.CurrentLoopClip ?? "";

        public Vector3? DebugSeatedBodyLocal =>
            _body != null && _driving != null ? _driving.ToLocal(_body.GlobalPosition) : (Vector3?)null;
        PlayerClothingController _clothing;   // P4 equip->visual wiring (drives shirt/pants paint + gear bone-attach off the worn slots)
        // Damage feedback, both source-exact and fired from TakeDamage: the red hurt flash (PlayerUI.painAlpha) and the
        // camera flinch (PlayerLook.flinchLocalRotation, an angular kick perpendicular to the hit that decays to level).
        public float PainAlpha;                     // PlayerUI.pain: red overlay alpha, set on hit, fades at 1/s
        Quaternion _flinch = Quaternion.Identity;   // PlayerLook.flinchLocalRotation: camera kick, recovers at 4/s

        public float MouseSensitivity => ControlsOptions.MouseSensitivity;   // reads the Controls-tab setting so every look path shares one number (was a 0.12f [Export])
        [Export] public float AdsSensScale = 0.65f;   // mouse-sens multiplier at full ADS for NON-scoped aim (iron sights); a scoped gun uses 1/zoom instead (master: reduce sens when adsing). Tunable.
        public int Ammo = 30;
        // infAmmo (master): the held gun's magazine refills after a short lull in firing. Deliberately NOT
        // instant -- topping up per-shot would hide every reload and make firerate the only limit, so the gun
        // still runs dry mid-burst and you still feel the mag. OFF by default; SP-local static like
        // Vehicle.InfiniteFuel, not networked.
        public static bool InfiniteAmmo;
        public const float InfAmmoIdle = 0.5f;   // seconds of not firing before the refill lands (master: "after 0.5s")
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

        // Use a consumable (ItemConsumeableAsset): apply its Health/Food/Water/bleeding effects to the vitals. `quality`
        // is the eaten instance's CONDITION (0-100, source player.equipment.quality) -- FOOD/WATER items ride it as
        // freshness; source scales food+water restored by quality/100 and, below 50, infects you (moldy food penalty).
        // Non-condition items spawn at quality 100 -> full effect, no penalty (byte-identical to the old behaviour).
        public void Consume(ItemAsset a, int quality = 100)
        {
            if (a == null) return;
            float qf = FoodSpoil.NutritionScale(quality);   // source: askEat/askDrink scale by player.equipment.quality / 100
            if (a.useHealth > 0) Health = Mathf.Min(MaxHealth, Health + a.useHealth);
            if (a.useFood  > 0) Food  = Mathf.Min(1f, Food  + a.useFood  / 100f * qf);
            if (a.useWater > 0) Water = Mathf.Min(1f, Water + a.useWater / 100f * qf);
            if (a.useEnergy > 0) Stamina = Mathf.Min(1f, Stamina + a.useEnergy / 100f);   // askRest: energy drinks/bars restore stamina
            if (a.useVirus > 0) Infect(a.useVirus / 100f);   // askInfect: raises infection (IMMUNITY skill cuts it, via Infect)
            if (a.useDisinfectant > 0) Infection = Mathf.Max(0f, Infection - a.useDisinfectant / 100f);   // askDisinfect: antibiotics/vaccine lower infection
            // Moldy penalty (source UseableConsumeable.performUseOnSelf): eating a FOOD/WATER item under 50% condition
            // infects you, scaled by how spoiled it is. This is the "food below 50% subtracts from your bar" mechanic --
            // raising infection = the bar drains (inverted). IMMUNITY cuts it (inside Infect).
            float moldy = FoodSpoil.MoldyInfection(a.useFood, a.useWater, quality);
            if (moldy > 0f) Infect(moldy);
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
        /// <summary>WHICH PART of it -- the door (and so which seat), the hood, or the trunk. strawberry:
        /// "kill the lookat for the whole car, change it for a collider on each 'door'... pressing f gets you
        /// in at that seat." Invalid means the ray hit the HULL but no zone, which is how a boat/heli/tank
        /// keeps the whole-vehicle behaviour it has always had.</summary>
        Vehicle.AccessZone _focusAccess;
        bool _focusAccessValid;
        Train _focusTrain;      // the train the player is LOOKING AT (loco outlined; F boards it) -- not a Vehicle, own scan
        Train _focusCouplerTrain; int _focusCouplerIdx = -1;   // the coupler the player is looking at (rope outlined; F uncouples there)
        Deployable _focusDeployable;  // the placed deployable (generator) the player is LOOKING AT (outlined + HP/fuel billboard)
        Door _focusDoor;              // the door being looked at -> F toggles it
        NoteBody _focusNote;          // the readable lore note being looked at -> F reads it
        NoteReader _noteReader;       // the note reading panel (created in _Ready alongside the map)
        ObjectDoor _focusObjectDoor;   // an openable prop door (fridge etc.) being looked at -> F toggles it
        Bed _focusBed;                // the bed being looked at -> F claims it as this player's respawn point
        /// <summary>Identity used for door/bed ownership. SP is a single local player; MP overwrites this
        /// per shell so a claim belongs to a person rather than to "the client".</summary>
        public ulong PlayerId = 1UL;
        public ulong GroupId;         // 0 = no group; a locked door opens for groupmates when set
        // Read-only views of what the production look-raycast resolved, so a test can assert the wiring
        // without a test-only code path deciding the answer.
        public Door DebugFocusDoor => _focusDoor;
        int _debugLookCandidates;
        /// <summary>Candidates the look passes produced on the last frame, BEFORE arbitration cut them to one.</summary>
        public int DebugLookCandidates => _debugLookCandidates;
        /// <summary>How many things the look system currently has focused. Master's rule is that this is NEVER above 1
        /// ("hard restriction of ONE item when ur looking at something"), and it is exposed as a COUNT rather than as a
        /// bool so a failure reports how badly -- two outlines and five are different bugs.</summary>
        public int DebugFocusCount
        {
            get
            {
                int n = 0;
                if (IsInstanceValid(_focusItem)) n++;
                if (IsInstanceValid(_focusVehicle)) n++;
                if (IsInstanceValid(_focusDeployable)) n++;
                if (_focusFluid != null && IsInstanceValid(_focusFluid)) n++;
                if (IsInstanceValid(_focusDoor)) n++;
                if (IsInstanceValid(_focusObjectDoor)) n++;
                if (IsInstanceValid(_focusBed)) n++;
                if (IsInstanceValid(_focusGasPump)) n++;
                if (IsInstanceValid(_focusGrid)) n++;
                if (IsInstanceValid(_focusTV)) n++;
                if (IsInstanceValid(_focusShelfItem)) n++;
                if (IsInstanceValid(_focusShelf)) n++;
                if (_focusPuppet is Node3D fp && IsInstanceValid(fp)) n++;
                return n;
            }
        }
        public Bed DebugFocusBed => _focusBed;
        /// <summary>Sim seconds, accumulated from the fixed tick -- NOT engine uptime. Door and bed
        /// cooldowns are sim rules, and wall-clock keeps running while the game is paused.</summary>
        double _interactClock;
        GasPump _focusGasPump;        // the gas pump being LOOKED AT (outline + fuel tooltip; RMB w/ a gas can extracts)
        TVDevice _focusTV;            // the TV being LOOKED AT -> F toggles it on/off
        HeartMonitor _focusMonitor;   // ...and the patient monitor, same deal
        GridPowerSource _focusGrid;   // the grid-power box being LOOKED AT (outline + "Grid Power - <name>: <watts>" tooltip)
        LampLight _focusLamp;         // the standing/desk lamp being LOOKED AT -> F toggles it on/off
        ElevatorButton _focusElevButton;   // the elevator floor-BUTTON being LOOKED AT -> F calls the car to that floor
        SDG.Unturned.Item _heldFuelItem;  // a gas can equipped in hand -> RMB a powered pump to fill it (master's fluids)
        SDG.Unturned.Item _heldFluidItem; // a fluid CONTAINER (water bottle / soda / cola / canteen) in hand -> RMB a tank to fill it, LMB to sip clean water (strawberry)
        // Fishing (UseableFisher port): a rod in hand -> hold LMB to charge the cast gauge, release to fling the
        // bobber into water, a fish bites, press LMB in the window to land it. _fishing owns the state/timing sim.
        FishingSim _fishing;
        SDG.Unturned.Item _heldFisherItem;
        Node3D _bobber;               // the floating bobber node while a line is deployed
        Vector3 _bobberVel;           // simple projectile integration until it hits the water surface
        MeshInstance3D _fishLine;     // rod-tip -> bobber line (ImmediateMesh, redrawn per frame)
        float _fishTockAccum;         // 50 Hz accumulator driving the strength-gauge Tock at a framerate-independent rate
        public bool HoldingFisher => _fishing != null;
        Deployable _fHeldDeploy;      // the deployable F is being HELD on (hold-F = pick it up; a quick tap = toggle, on release)
        float _deployPickupTimer;     // seconds F has been held on _fHeldDeploy
        const float DeployPickupTime = 1.0f;    // hold F this long over a deployable to pick it back up (wires disconnect)
        const float PickupBarDeadzone = 0.2f;   // hide the progress bar for the first 20% of the hold, so a quick tap-to-toggle doesn't flash it
        FluidContainer _focusFluid;   // the placed fluid device being LOOKED AT (hold-F pickup target)
        FluidContainer _fHeldFluid;   // the fluid device F is being HELD on (hold-F = pick it up; hoses/power wire disconnect)
        float _fluidPickupTimer;      // seconds F has been held on _fHeldFluid
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

        /// <summary>What the look system settled on. RayOther collapses every ray-claimed interactable into one case
        /// because the ray chain is else-if -- at most one of them exists, so which one never affects arbitration.</summary>
        internal enum Look { None, RayOther, Shelf, ShelfItem, Item, Vehicle, Puppet }

        /// <summary>Pick the ONE thing the player is looking at (master: "lookatradius should only choose ONE. hard
        /// restriction of ONE item when ur looking at something. cover all cases").
        ///
        /// The bug this exists to kill: the look system runs TWO passes that never spoke to each other. A ray picks
        /// one interactable (its chain is else-if, so that half was always singular), then a sphere at the ray's end
        /// sweeps for items/vehicles/puppets and arbitrated only among ITSELF. Nothing ever compared across the two,
        /// so a dropped item lying near where the ray landed lit up at the same time as the door or TV the ray hit.
        ///
        /// Worse than the double outline: F resolved the tie through a THIRD order -- its own else-if chain in the
        /// input handler, which puts items before doors -- so the thing highlighted and the thing you interacted with
        /// could genuinely differ. Fixing it HERE rather than in the F chain is deliberate: with one candidate left
        /// there is nothing for F to disagree with. Patching F would have left both outlines up and hidden the tell.
        ///
        /// The rules, in full:
        ///   - a ray claim on anything but a shelf is TERMINAL. Pointing at a thing beats whatever the assist radius
        ///     scraped up nearby; the sphere exists for things too small to hit precisely, not to override your aim.
        ///   - a ray claim on a SHELF still lets the sphere refine to a shelf ITEM, because picking one item off a
        ///     shelf you are looking at is the entire job of that radius. Nothing else survives a shelf.
        ///   - with no ray claim, the nearest sphere find wins. Distances are squared, from the ray's end.
        /// Ties resolve in the parameter order below, so the outcome is deterministic rather than dependent on which
        /// order the physics query happened to return overlaps in.</summary>
        internal static Look ResolveFocus(Look ray, float itemD, float vehD, float shelfItemD, float puppetD)
        {
            if (ray == Look.RayOther || ray == Look.ShelfItem) return ray;
            if (ray == Look.Shelf) return shelfItemD < float.MaxValue ? Look.ShelfItem : Look.Shelf;
            float best = Mathf.Min(Mathf.Min(itemD, vehD), Mathf.Min(shelfItemD, puppetD));
            if (best == float.MaxValue) return Look.None;
            if (shelfItemD <= best) return Look.ShelfItem;   // a display item is a deliberate target; it wins a tie
            if (itemD <= best) return Look.Item;
            if (vehD <= best) return Look.Vehicle;
            return Look.Puppet;
        }

        double _lookFocusT, _grassT;   // PERF: rate limiters (see ProcessTick)
        System.Collections.Generic.List<(float d2, Vector3 pos, float r)> _dispPrev;   // last uploaded displacer texels (see UpdateGrassDisplacement)
        void UpdateLookFocus()
        {
            WorldItem hitItem = null; Vehicle hitVeh = null; Deployable hitDeploy = null; GasPump hitGasPump = null; GridPowerSource hitGrid = null; FluidContainer hitFluid = null;
            // which door/hood/trunk of the focused vehicle the ray found
            Vehicle.AccessZone hitAccess = default; bool hitAccessValid = false;
            Door hitDoor = null; Bed hitBed = null; ObjectDoor hitObjectDoor = null; TVDevice hitTV = null; NoteBody hitNote = null;
            HeartMonitor hitMonitor = null;   // patient monitor under the ray -> F toggles it
            LampLight hitLamp = null;         // standing/desk lamp under the ray -> F on/off + outline
            ElevatorButton hitElevButton = null;   // elevator floor-button under the ray -> F sends the car to that floor
            ShelfItemBody hitShelfItem = null; StoreShelf hitShelf = null;   // shelf display item / its shelf under the look-sphere
            IPuppetFocusable hitPuppet = null;   // MP ONLY: nearest replicated car/item puppet under the look-sphere (SP hits real Vehicle/WorldItem instead)
            Train hitTrain = null;   // train loco under the look-ray (own scan; not in ResolveFocus)
            Train hitCT = null; int hitCI = -1;   // train + coupler index under the look-ray
            bool rayTerminal = false, rayShelfItem = false;   // did the RAY claim the target, and was it a shelf item? (see the arbitration below)
            // The captured-mouse gate is a gameplay condition: the scan only means anything while the player is
            // actually looking around. Headless REFUSES to capture (MouseMode stays Visible no matter what a test
            // sets), so L1 cannot reach this scan at all without a seam -- and the alternative, testing
            // Vehicle.ResolveAccess on its own, is precisely the check that passed against the build where the
            // zone code was never called. Default off; any suite that sets it must clear it (see l1 leaked globals).
            if (!_dead && _driving == null && _riding == null && _cam != null
                && (Input.MouseMode == Input.MouseModeEnum.Captured || DebugForceLookScan))
            {
                var space = GetWorld3D().DirectSpaceState;
                // THIRD person traces from the SHOULDER, straight down the look axis (strawberry) -- see ShoulderWorld
                // for why the camera is not a place a person can see from. FIRST person keeps the existing focus point
                // (strawberry: "in 1p use the exiating focus point"), and that split is the right one: in first person
                // the camera IS the eye, so the old origin was already correct, and moving it to the shoulder would
                // have bought nothing but 0.2 m of parallax between the crosshair and whatever lights up.
                var (from, fwd) = LookTrace();
                DebugLookOrigin = from; DebugLookDir = fwd;
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
                    if (rcol is Door rdoor && IsInstanceValid(rdoor)) hitDoor = rdoor;
                    else if (rcol is ObjectDoor rod && IsInstanceValid(rod))
                    {
                        if (ShelfOf(rod) is StoreShelf rodShelf) hitShelf = rodShelf;   // a CONTAINER's now-solid door leaf -> focus the whole shelf (F opens the inventory + whole-prop highlight), not the leaf alone
                        else hitObjectDoor = rod;                                        // a standalone doored prop -> the door itself (F toggles it)
                    }
                else if (rcol is Node bdn && bdn.HasMeta(Sn.objectdoor) && bdn.GetMeta(Sn.objectdoor).As<ObjectDoor>() is ObjectDoor bod && IsInstanceValid(bod)) hitObjectDoor = bod;   // issue 3: the PROP BODY collider (meta-linked by WorldBuilder.PlaceObject) resolves to its door -> look anywhere on a doored prop to toggle + whole-prop highlight, not just the leaf
                    else if (rcol is Bed rbed && IsInstanceValid(rbed)) hitBed = rbed;
                    else if (rcol is NoteBody rnote && IsInstanceValid(rnote)) hitNote = rnote;   // readable lore note (see-through layer) -> focus + F reads it
                    else if (rcol is Deployable dep && IsInstanceValid(dep)) hitDeploy = dep;
                    else if (rcol is FluidContainer fcr && IsInstanceValid(fcr)) hitFluid = fcr;   // a placed fluid device body (solid since batch A) -> hold-F pickup
                    else if (rcol is Node grn && grn.HasMeta(Sn.gaspump) && grn.GetMeta(Sn.gaspump).As<GasPump>() is GasPump gpn && IsInstanceValid(gpn)) hitGasPump = gpn;   // gas pump collider tagged in WorldBuilder -> the fixture
                    else if (rcol is Node grn2 && grn2.HasMeta(Sn.gridpower) && grn2.GetMeta(Sn.gridpower).As<GridPowerSource>() is GridPowerSource gsn && IsInstanceValid(gsn)) hitGrid = gsn;   // grid-power box collider tagged in SpawnEditorGridPower
                    else if (rcol is Node hmn && hmn.HasMeta(HeartMonitor.HitMeta) && hmn.GetMeta(HeartMonitor.HitMeta).As<HeartMonitor>() is HeartMonitor hmd && IsInstanceValid(hmd)) hitMonitor = hmd;   // patient monitor body -> its device (F toggles; the bullet path shoots the screen out)
                    else if (rcol is Node tvn && tvn.HasMeta(TVDevice.HitMeta) && tvn.GetMeta(TVDevice.HitMeta).As<TVDevice>() is TVDevice tvd && IsInstanceValid(tvd)) hitTV = tvd;   // TV body collider tagged in WorldBuilder -> its device (F toggles; the bullet path uses the same meta to find the screen)
                    else if (rcol is Node lmn && lmn.HasMeta(LampLight.LookMeta) && lmn.GetMeta(LampLight.LookMeta).As<LampLight>() is LampLight lmd && IsInstanceValid(lmd)) hitLamp = lmd;   // standing/desk lamp body tagged in WorldBuilder -> its LampLight (F on/off)
                    else if (rcol is ElevatorButton eb && IsInstanceValid(eb)) hitElevButton = eb;   // elevator floor button -> F sends the car to its floor (the whole car is no longer the interactable, master)
                    else if (rcol is ShelfItemBody sibr && IsInstanceValid(sibr)) hitShelfItem = sibr;   // ray hit an item on a shelf directly -> lock onto it (the orb is a backup)
                    else if (rcol is Node rn && ShelfOf(rn) is StoreShelf rshelf) hitShelf = rshelf;   // looked-at shelf -> whole-shelf outline + F-open (look-based, not proximity)
                    // Which pass claimed the target decides who wins below. The ray is you POINTING at something;
                    // the sphere is a forgiveness radius for things too small to hit precisely. A shelf item found by
                    // the ray is a terminal claim; one found by the sphere is a refinement of a looked-at shelf.
                    rayShelfItem = hitShelfItem != null;
                }
                // A ray claim on anything except a SHELF is terminal -- the shelf is the one case where the assist
                // sphere is still allowed to speak, because picking an individual item off a shelf you are looking at
                // is exactly what it is for. The ray chain above is else-if, so at most one of these is ever set.
                rayTerminal = hitDoor != null || hitObjectDoor != null || hitBed != null || hitDeploy != null
                           || hitFluid != null || hitGasPump != null || hitGrid != null || hitTV != null || hitMonitor != null || hitLamp != null || hitElevButton != null || hitNote != null || rayShelfItem;
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
                // ---- EXACTLY ONE TARGET (master: "lookatradius should only choose ONE. hard restriction of ONE item
                // when ur looking at something. cover all cases."). See ResolveFocus for the rules and the why.
                var won = ResolveFocus(rayTerminal ? (rayShelfItem ? Look.ShelfItem : Look.RayOther) : hitShelf != null ? Look.Shelf : Look.None,
                                       hitItem != null ? bestI : float.MaxValue,
                                       hitVeh != null ? bestV : float.MaxValue,
                                       hitShelfItem != null ? bestSI : float.MaxValue,
                                       hitPuppet != null ? bestP : float.MaxValue);
                // How many candidates existed BEFORE arbitration. Exposed purely so a test can prove it actually
                // reproduced the bug: "one thing is focused" passes trivially in a scene that only ever offered one,
                // and a vacuous pass on the exact case the fix exists for is worse than no test. Asserting
                // candidates >= 2 alongside focus == 1 is what gives the live check teeth.
                _debugLookCandidates = (rayTerminal ? 1 : 0) + (hitShelf != null ? 1 : 0)
                                     + (hitItem != null ? 1 : 0) + (hitVeh != null ? 1 : 0)
                                     + (hitShelfItem != null && !rayShelfItem ? 1 : 0) + (hitPuppet != null ? 1 : 0);
                if (won != Look.RayOther) { hitDoor = null; hitObjectDoor = null; hitBed = null; hitDeploy = null; hitFluid = null; hitGasPump = null; hitGrid = null; hitTV = null; hitMonitor = null; hitLamp = null; hitElevButton = null; }
                if (won != Look.Shelf) hitShelf = null;
                if (won != Look.ShelfItem) hitShelfItem = null;
                if (won != Look.Item) hitItem = null;
                if (won != Look.Vehicle) hitVeh = null;
                if (won != Look.Puppet) hitPuppet = null;
                if (won == Look.None)   // seats/steering seen through windows have no collider -> focus a car whose visual bounds the look-ray passes through (master). DISTANCE-CULLED so it isn't O(all vehicles) every frame (perf regression fix). Skipped entirely once something else already owns the frame -- correctness AND the O(vehicles) loop.
                {
                    float maxD = (LookReach + 6f) * (LookReach + 6f);
                    foreach (var node in Vehicle.Live)   // PERF: C# registry, not a marshalled group array every scan
                        if (node is Vehicle vv && IsInstanceValid(vv))
                        {
                            float d = vv.GlobalPosition.DistanceSquaredTo(from);
                            if (d >= maxD || d >= bestV) continue;   // cheap distance gate before the tight oriented-box tests
                            if (vv.LookRayHitsHull(from, _lookEnd)) { bestV = d; hitVeh = vv; }
                        }
                }
                // TRAIN look-focus (not in ResolveFocus -- a train is a lone rail vehicle): when nothing else won,
                // focus a train whose loco the look-ray passes through, so it outlines + F boards it like a car.
                if (_ridingTrain == null && _ridingCrane == null && hitVeh == null && hitItem == null && hitShelfItem == null && hitPuppet == null)
                {
                    float maxTrainD = (LookReach + 8f) * (LookReach + 8f);
                    foreach (var node in GetTree().GetNodesInGroup("trains"))
                        if (node is Train tr && tr.Loco != null && IsInstanceValid(tr.Loco)
                            && tr.Loco.GlobalPosition.DistanceSquaredTo(from) < maxTrainD && tr.LookRayHitsLoco(from, _lookEnd))
                        { hitTrain = tr; break; }
                    // coupler focus: nearest coupler whose gap the look-ray passes within ~1.1m of -> F uncouples it
                    float bestC = 1.1f * 1.1f;
                    foreach (var node in GetTree().GetNodesInGroup("trains"))
                        if (node is Train tc && IsInstanceValid(tc))
                            for (int ci = 0; ci < tc.CouplerCount; ci++)
                            { float d = SegPointDistSq(from, _lookEnd, tc.CouplerWorld(ci)); if (d < bestC) { bestC = d; hitCT = tc; hitCI = ci; } }
                    if (hitCT != null) hitTrain = null;   // a coupler in your sights beats boarding the engine behind it
                }
            }
            if (hitTrain != _focusTrain)
            {
                if (IsInstanceValid(_focusTrain)) _focusTrain.SetLookFocused(false);
                _focusTrain = hitTrain;
                _focusTrain?.SetLookFocused(true);
            }
            if (hitCT != _focusCouplerTrain || hitCI != _focusCouplerIdx)
            {
                if (_focusCouplerTrain != null && IsInstanceValid(_focusCouplerTrain)) _focusCouplerTrain.SetCouplerFocused(_focusCouplerIdx, false);
                _focusCouplerTrain = hitCT; _focusCouplerIdx = hitCI;
                if (_focusCouplerTrain != null) _focusCouplerTrain.SetCouplerFocused(_focusCouplerIdx, true);
            }
            if (_lookViz != null) { _lookViz.Visible = WorldItem.ShowLookSphere && !_dead && _driving == null; if (_lookViz.Visible) _lookViz.GlobalPosition = _lookEnd; }
            if (hitItem != _focusItem)
            {
                if (IsInstanceValid(_focusItem)) _focusItem.SetFocused(false);
                _focusItem = hitItem;
                _focusItem?.SetFocused(true);
            }
            // WHICH PART of the winning car, resolved once here rather than inside any one of the paths that can
            // win it. The first cut of this lived in the no-collider fallback loop below and so only ran when
            // NOTHING else won the frame -- but a car you are stood in front of has a real hull collider and wins
            // on the sphere probe above, so the zone code never ran and every press fell through to seat 0. The
            // vehicle is final by this point no matter which path found it; that is the only correct place to ask.
            // DebugLookOrigin is the `from` the scan above actually traced with -- the SHOULDER in 3rd person,
            // which is NOT the camera. Re-deriving it from _cam here would test a different ray to the one that won.
            hitAccessValid = hitVeh != null && IsInstanceValid(hitVeh) && hitVeh.ResolveAccess(DebugLookOrigin, _lookEnd, out hitAccess);
            _focusAccess = hitAccess; _focusAccessValid = hitAccessValid;   // updated every frame: same car, different door
            if (hitVeh != _focusVehicle)
            {
                if (IsInstanceValid(_focusVehicle)) { _focusVehicle.AccessHint = ""; _focusVehicle.SetLookFocused(false); }
                _focusVehicle = hitVeh;
                _focusVehicle?.SetLookFocused(true);
            }
            // Zone prompt. The whole point of splitting the hull into door/hood/trunk volumes is that the player
            // can tell which one they have BEFORE pressing the key, so the billboard names it every frame.
            if (_focusVehicle != null && IsInstanceValid(_focusVehicle)) _focusVehicle.AccessHint = AccessPrompt(_focusVehicle);
            if (hitDeploy != _focusDeployable)
            {
                if (IsInstanceValid(_focusDeployable)) _focusDeployable.SetLookFocused(false);
                _focusDeployable = hitDeploy;
                _focusDeployable?.SetLookFocused(true);
            }
            if (hitFluid != _focusFluid) _focusFluid = hitFluid;   // no outline shader on fluid bodies -> just track it for hold-F pickup
            if (hitDoor != _focusDoor)
            {
                if (IsInstanceValid(_focusDoor)) _focusDoor.SetLookFocused(false);
                _focusDoor = hitDoor;
                _focusDoor?.SetLookFocused(true);
            }
            if (hitObjectDoor != _focusObjectDoor)   // openable prop door (fridge etc.) look-focus, mirrors the _focusDoor block above
            {
                if (IsInstanceValid(_focusObjectDoor)) _focusObjectDoor.SetLookFocused(false);
                _focusObjectDoor = hitObjectDoor;
                _focusObjectDoor?.SetLookFocused(true);
            }
            // a barricaded door: flag its outline RED while looked at -- set per-frame because OutlineOverlay re-reads
            // WorldItem.FocusColor every frame, while SetLookFocused only claims White on a focus-CHANGE. The
            // "Door is barricaded" line fires on the F-press (below); the red rim is the passive "can't open this" tell.
            if (_focusObjectDoor != null && IsInstanceValid(_focusObjectDoor) && ObjectDoorBarricaded(_focusObjectDoor))
                WorldItem.FocusColor = Colors.Red;
            if (hitBed != _focusBed)
            {
                if (IsInstanceValid(_focusBed)) _focusBed.SetLookFocused(false);
                _focusBed = hitBed;
                _focusBed?.SetLookFocused(true);
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
            if (hitMonitor != _focusMonitor)   // patient monitor look-focus: same whole-prop white outline the TV gets
            {
                // This was a bare assignment. The monitor was the one focusable fixture that lit nothing when you
                // looked at it, so F worked and there was no affordance saying so -- an interaction with no outline
                // reads as "this prop is scenery", which is indistinguishable from the feature not existing.
                if (IsInstanceValid(_focusMonitor)) _focusMonitor.SetLookFocused(false);
                _focusMonitor = hitMonitor;
                _focusMonitor?.SetLookFocused(true);
            }
            if (hitTV != _focusTV)   // TV look-focus: whole-prop white outline (SetLookFocused claims WorldItem.FocusColor=white on gain)
            {
                if (IsInstanceValid(_focusTV)) _focusTV.SetLookFocused(false);
                _focusTV = hitTV;
                _focusTV?.SetLookFocused(true);
            }
            if (hitLamp != _focusLamp)   // standing/desk lamp look-focus: whole-lamp white outline, same pattern as the TV
            {
                if (IsInstanceValid(_focusLamp)) _focusLamp.SetLookFocused(false);
                _focusLamp = hitLamp;
                _focusLamp?.SetLookFocused(true);
            }
            if (hitElevButton != _focusElevButton)   // elevator floor-button look-focus: button white outline, same pattern as the lamp
            {
                if (IsInstanceValid(_focusElevButton)) _focusElevButton.SetLookFocused(false);
                _focusElevButton = hitElevButton;
                _focusElevButton?.SetLookFocused(true);
            }
            if (hitNote != _focusNote)   // readable note look-focus: white outline, F reads it
            {
                if (IsInstanceValid(_focusNote)) _focusNote.SetLookFocused(false);
                _focusNote = hitNote;
                _focusNote?.SetLookFocused(true);
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
        const float HoseReach = 5.5f, HosePlaceReach = 6f;   // look-at reach / node-place reach (mirror of the wire tool)
        const int MaxHoseNodes = 20; const float MaxHoseLen = 40f;   // mirror the wire tool's 20-node / 40m budget (strawberry)
        HosePort _hosePort;          // the fluid port currently looked at
        bool _hosing; HosePort _hoseSrc;   // mid-route: a start port is picked, waiting for the opposite-role end
        readonly System.Collections.Generic.List<Vector3> _hoseNodes = new();   // placed node points (world) between source + free end
        Hose _hosePreview;           // the live hose being routed (follows the look point)
        PhysicsRayQueryParameters3D _hoseRayQ, _hosePlaceRayQ;
        CanvasLayer _hoseHudLayer; Label _hoseHudLabel;
        // manage a hose by poking its PORT while not routing (mirror of the wire tool's _clearPort): hold RMB clears it, tap unplugs + re-routes
        HosePort _clearHosePort; float _hoseClearHold;
        const float HoseClearTime = 1.0f, HoseClickMax = 0.28f;   // hold this long over a hosed port to clear; release within this = a tap -> unplug
        bool _hoseArrowsOn;          // in/out port arrows currently shown (only while the hose tool is out)
        public HosePort HoseLookPort => _hosePort;   // L1 probe

        // scene wrapper over the engine-free FluidHoseRule for the two live ports (the type-lock rule is L0-tested in core)
        HoseVerdict CompletionVerdict(HosePort start, HosePort target)
        {
            if (!IsInstanceValid(start) || !IsInstanceValid(target) || !target.Usable) return HoseVerdict.None;
            // resolve each end's fluid type THROUGH tankless relay fittings (FluidNet.ResolveNetType): a splitter/pump/valve
            // has no tank of its own, so its raw EffectiveType is None -- but the type-lock must see the fluid its network
            // actually carries, else fuel would pipe into a water tank across a fitting.
            var st = FluidNet.ResolveNetType(GetTree(), start, new System.Collections.Generic.HashSet<FluidContainer>());
            var tt = FluidNet.ResolveNetType(GetTree(), target, new System.Collections.Generic.HashSet<FluidContainer>());
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
                // #11: a legal (Ok) connection that WON'T flow without a pump (uphill / no-head source, not already lifted)
                // paints ORANGE + warns, but stays connectable. FluidNet.WouldNeedPump reuses the real gravity/head gate.
                bool needsPump = false;
                if (v == HoseVerdict.Ok && IsInstanceValid(_hosePort))
                {
                    var (sp, cp) = FluidHoseRule.IsSourceSide(_hoseSrc.Kind) ? (_hoseSrc, _hosePort) : (_hosePort, _hoseSrc);
                    needsPump = FluidNet.WouldNeedPump(GetTree(), sp, cp);
                }
                if (IsInstanceValid(_hosePort) && _hosePort != _hoseSrc)
                    _hosePort.SetHighlight(v == HoseVerdict.Ok ? (needsPump ? HosePort.PortHi.HoseWarn : HosePort.PortHi.HoseOk) : HosePort.PortHi.HoseBad);
                Vector3 end = v == HoseVerdict.Ok ? _hosePort.GlobalPosition : HosePlacePoint();
                var pts = new System.Collections.Generic.List<Vector3> { _hoseSrc.GlobalPosition };
                pts.AddRange(_hoseNodes); pts.Add(end);
                float len = PolyLen(pts);
                bool overLimit = _hoseNodes.Count >= MaxHoseNodes || len > MaxHoseLen;
                _hosePreview?.SetPoints(pts, valid: v != HoseVerdict.Mismatch && !overLimit);
                if (v == HoseVerdict.Mismatch) HoseHudSet("cannot mix fluids");
                else if (overLimit) HoseHudSet($"nodes {_hoseNodes.Count}/{MaxHoseNodes}    {len:0.0}/{MaxHoseLen:0}m   -- LIMIT");
                else if (needsPump) HoseHudSet($"needs a pump (uphill / no gravity)    nodes {_hoseNodes.Count}/{MaxHoseNodes}   [LMB] connect anyway");
                else HoseHudSet($"nodes {_hoseNodes.Count}/{MaxHoseNodes}    {len:0.0}/{MaxHoseLen:0}m" + (v == HoseVerdict.Ok ? "    [LMB] connect" : ""));
            }
            else
            {
                if (IsInstanceValid(_hosePort)) _hosePort.SetHighlight(HosePort.PortHi.Focus);
                string hint = "";
                if (IsInstanceValid(_hosePort))
                {
                    if (_hosePort.Owner != null && _hosePort.Owner.Role == FluidRole.Valve) hint = "   ([RMB] open/close)";
                    else if (PortHosed(_hosePort)) hint = "   ([RMB] hold: clear · tap: unplug)";
                }
                HoseHudSet(_hosePort == null ? null : _hosePort.InfoLine(IsInstanceValid(_hosePort) && PortHosed(_hosePort)) + hint);
            }
        }

        // The free end / node drop = your look point (raycast to world/props), else max reach. Excludes the player + every
        // deployable AND fluid-device body so a hose routes STRAIGHT THROUGH them (mirror of WirePlacePoint) instead of
        // sticking to a tank/box face -- fluid bodies are solid colliders now (batch A).
        Vector3 HosePlacePoint()
        {
            if (_cam == null) return GlobalPosition;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
            _hosePlaceRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = (1u << 0) | (1u << 6) };
            _hosePlaceRayQ.From = from; _hosePlaceRayQ.To = from + fwd * HosePlaceReach;
            var exclude = new Godot.Collections.Array<Rid> { GetRid() };
            foreach (var n in GetTree().GetNodesInGroup("deployables"))
                if (n is Deployable dep && GodotObject.IsInstanceValid(dep)) exclude.Add(dep.GetRid());
            foreach (var n in GetTree().GetNodesInGroup("fluid_devices"))
                if (n is FluidContainer fc && GodotObject.IsInstanceValid(fc)) exclude.Add(fc.GetRid());
            _hosePlaceRayQ.Exclude = exclude;
            var hit = space.IntersectRay(_hosePlaceRayQ);
            return hit.Count > 0 ? (Vector3)hit["position"] : from + fwd * HosePlaceReach;
        }

        // LMB with the hose tool (mirror of WireLmb): start from a usable, unhosed port (either role), place a routing node,
        // or complete on a compatible opposite port -- completion + placement both gated by the 20-node/40m budget.
        void HoseLmb()
        {
            if (_dead) return;
            if (!_hosing)
            {
                if (IsInstanceValid(_hosePort) && _hosePort.Usable && !PortHosed(_hosePort))
                {
                    _hosing = true; _hoseSrc = _hosePort; _hoseNodes.Clear();
                    _hoseSrc.SetHighlight(HosePort.PortHi.Focus);
                    _hosePreview = new Hose(); GetParent().AddChild(_hosePreview);   // preview: null endpoints -> FluidNet skips it until committed
                    GD.Print($"[hose] started from {_hosePort.InfoLine()}");
                }
                return;
            }
            if (CompletionVerdict(_hoseSrc, _hosePort) == HoseVerdict.Ok)
            {   // complete on the compatible opposite-role port -- only if the finished hose fits the same node/length budget
                var cpts = new System.Collections.Generic.List<Vector3> { _hoseSrc.GlobalPosition }; cpts.AddRange(_hoseNodes); cpts.Add(_hosePort.GlobalPosition);
                if (_hoseNodes.Count <= MaxHoseNodes && PolyLen(cpts) <= MaxHoseLen) CompleteHose(_hosePort);
                return;
            }
            Vector3 lp = HosePlacePoint();   // else drop a routing node at the look point (a Mismatch target still just routes)
            var pts = new System.Collections.Generic.List<Vector3> { _hoseSrc.GlobalPosition }; pts.AddRange(_hoseNodes); pts.Add(lp);
            if (_hoseNodes.Count >= MaxHoseNodes || PolyLen(pts) > MaxHoseLen) return;   // hitting the limit blocks placing
            _hoseNodes.Add(lp);
        }

        // RMB with the hose tool while routing (mirror of WireRmb): undo the last node, or cancel+delete the hose if none.
        void HoseRmb()
        {
            if (!_hosing) return;
            if (_dead || _hoseNodes.Count == 0) CancelHose();
            else _hoseNodes.RemoveAt(_hoseNodes.Count - 1);
        }

        void CompleteHose(HosePort target)
        {
            if (_hosePreview == null || !IsInstanceValid(_hoseSrc)) { CancelHose(); return; }
            var (srcPort, consPort) = FluidHoseRule.IsSourceSide(_hoseSrc.Kind) ? (_hoseSrc, target) : (target, _hoseSrc);
            AdoptFluidType(srcPort, consPort);   // an empty tank adopts the fluid flowing in (strawberry) — port EffectiveType handles transformers
            _hosePreview.Source = srcPort.Node; _hosePreview.Consumer = consPort.Node;
            // the committed visual path keeps the routed nodes: start port -> each placed node -> end port
            var pts = new System.Collections.Generic.List<Vector3> { _hoseSrc.GlobalPosition }; pts.AddRange(_hoseNodes); pts.Add(target.GlobalPosition);
            _hosePreview.SetPoints(pts, valid: true);
            if (!_hosePreview.IsInGroup("hoses")) _hosePreview.AddToGroup("hoses");
            if (IsInstanceValid(_hoseSrc)) _hoseSrc.SetHighlight(HosePort.PortHi.None);
            if (IsInstanceValid(target)) target.SetHighlight(HosePort.PortHi.None);
            GD.Print($"[hose] connected {srcPort.Owner?.Role} -> {consPort.Owner?.Role} ({_hoseNodes.Count} nodes)");
            _hosePreview = null; _hosing = false; _hoseSrc = null; _hoseNodes.Clear();
        }

        void CancelHose()
        {
            _hosePreview?.QueueFree(); _hosePreview = null;
            if (IsInstanceValid(_hoseSrc)) _hoseSrc.SetHighlight(HosePort.PortHi.None);
            _hosing = false; _hoseSrc = null; _hoseNodes.Clear();
        }

        // --- Hose disconnect: mirror the wire tool's port-poke management (strawberry: reuse the wire UX, don't hardcode a
        // parallel one). While the tool is out + NOT routing, look at a hosed port: hold RMB clears the hose (% readout),
        // tap RMB unplugs it + picks it back up to re-route. RMB while routing stays undo (HoseRmb, event-driven). ---

        // the committed hose an endpoint of which is `p` (null if unhosed) -- mirror of WireOnPort
        Hose HoseOnPort(HosePort p)
        {
            if (p?.Node == null) return null;
            foreach (var n in GetTree().GetNodesInGroup("hoses"))
                if (n is Hose h && GodotObject.IsInstanceValid(h) && (h.Source == p.Node || h.Consumer == p.Node)) return h;
            return null;
        }

        // the HosePort wrapping a given data node (reverse of HosePort.Node), for re-routing an unplugged hose from its source
        HosePort HosePortForNode(FluidPortNode node)
        {
            if (node == null) return null;
            foreach (var n in GetTree().GetNodesInGroup("fluid_devices"))
                if (n is FluidContainer c)
                    foreach (var hp in c.PortNodes)
                        if (hp.Node == node) return hp;
            return null;
        }

        // RMB PRESS while NOT routing: arm a clear/unplug on the hosed port under the crosshair (mirror WireManageArm).
        void HoseManageArm()
        {
            if (_dead || _driving != null || !HoldingHoseTool || Input.MouseMode != Input.MouseModeEnum.Captured) return;
            if (HoseOnPort(_hosePort) != null) { _clearHosePort = _hosePort; _hoseClearHold = 0f; }
        }

        // Per-frame: drive an ARMED RMB hold on a hosed port -- held to HoseClearTime clears the hose; released quickly
        // (<= HoseClickMax) unplugs it; released mid-hold does nothing. Mirror of UpdateWireManage.
        void UpdateHoseManage(float delta)
        {
            if (_clearHosePort == null) return;
            bool active = HoldingHoseTool && !_hosing && !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured;
            Hose h = HoseOnPort(_clearHosePort);
            if (!active || _hosePort != _clearHosePort || h == null) { _clearHosePort = null; _hoseClearHold = 0f; return; }   // looked away / state changed / hose gone -> abort
            if (Input.IsMouseButtonPressed(MouseButton.Right))
            {
                _hoseClearHold += delta;
                if (_hoseClearHold >= HoseClearTime)   // held long enough -> clear the whole hose
                {
                    h.RemoveFromGroup("hoses"); h.QueueFree();   // drop the group THIS frame so flow + PortHosed update immediately
                    _clearHosePort = null; _hoseClearHold = 0f; HoseHudSet(null); return;
                }
                HoseHudSet($"clearing hose... {Mathf.Clamp((int)(_hoseClearHold / HoseClearTime * 100f), 0, 99)}%");
            }
            else { if (_hoseClearHold <= HoseClickMax) UnplugHose(h); _clearHosePort = null; _hoseClearHold = 0f; }   // released quick -> tap-unplug
        }

        // Unplug a hose: drop its consumer link + leave the "hoses" group, and pick it back up as a routing preview from its
        // source (all node points kept), so poking either endpoint re-picks it up to re-route. Mirror of UnplugWire.
        void UnplugHose(Hose hose)
        {
            if (hose == null || !IsInstanceValid(hose) || hose.Source == null) { hose?.QueueFree(); return; }
            var srcPort = HosePortForNode(hose.Source);
            if (srcPort == null) { hose.RemoveFromGroup("hoses"); hose.QueueFree(); return; }
            _hoseSrc = srcPort;
            _hoseNodes.Clear();
            for (int i = 1; i < hose.Points.Count - 1; i++) _hoseNodes.Add(hose.Points[i]);   // keep the node points; drop source[0] + consumer[last]
            hose.Consumer = null;
            hose.RemoveFromGroup("hoses");   // stop conducting immediately
            _hosePreview = hose; _hosing = true;
            GD.Print($"[hose] unplugged -> routing from source with {_hoseNodes.Count} kept nodes");
        }

        // In/out arrows on every fluid port while the hose tool is out (mirror UpdateWireArrows): blue where you can hose,
        // red where the port is occupied or on a clogged/closed device.
        void UpdateHoseArrows()
        {
            bool show = HoldingHoseTool && !_dead && _driving == null && Input.MouseMode == Input.MouseModeEnum.Captured;
            if (!show)
            {
                if (_hoseArrowsOn) { foreach (var n in GetTree().GetNodesInGroup("fluid_ports")) if (n is HosePort p && IsInstanceValid(p)) { p.Visible = false; p.SetArrowState(false, false); } _hoseArrowsOn = false; }
                return;
            }
            _hoseArrowsOn = true;
            foreach (var n in GetTree().GetNodesInGroup("fluid_ports"))
                if (n is HosePort p && IsInstanceValid(p))
                {   // the fluid IO cubes + arrows only show while the hose tool is out (strawberry) -- the collider stays live so the look-ray still finds them
                    p.Visible = true;
                    p.SetArrowState(true, p.Usable && !PortHosed(p));
                }
        }

        // an empty (None) tank adopts the fluid at the OTHER end of the hose on connect. Resolves the type THROUGH relay
        // fittings (ResolveNetType), so a tank fed via a pump/splitter from a fuel source adopts fuel -- not None -- and a
        // transformer's OutputType still propagates to the tank it feeds. Two set types were already type-locked equal.
        void AdoptFluidType(HosePort src, HosePort cons)
        {
            var srcType = FluidNet.ResolveNetType(GetTree(), src, new System.Collections.Generic.HashSet<FluidContainer>());
            var consType = FluidNet.ResolveNetType(GetTree(), cons, new System.Collections.Generic.HashSet<FluidContainer>());
            if (cons.Owner?.Tank != null && cons.Owner.Tank.Type == FluidType.None && srcType != FluidType.None) cons.Owner.Tank.Type = srcType;
            else if (src.Owner?.Tank != null && src.Owner.Tank.Type == FluidType.None && consType != FluidType.None) src.Owner.Tank.Type = consType;
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
            foreach (var n in GetTree().GetNodesInGroup("fluid_devices"))
                if (n is FluidContainer fc && GodotObject.IsInstanceValid(fc)) exclude.Add(fc.GetRid());   // route through fluid bodies too (solid colliders since batch A)
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
            bool fHeld = Input.MouseMode == Input.MouseModeEnum.Captured && Keybinds.Pressed(GameAction.Interact);
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

        // Hold F over a placed fluid device to pick it back up into the bag (mirror of UpdateDeployPickup): its hoses (and a
        // pump's power wire) disconnect. No tap-toggle -- a fluid device has no power state to flip.
        void UpdateFluidPickup(float delta)
        {
            if (_fHeldFluid == null) { FluidPickupHudSet(null); return; }
            bool fHeld = Input.MouseMode == Input.MouseModeEnum.Captured && Keybinds.Pressed(GameAction.Interact);
            if (!fHeld || !IsInstanceValid(_fHeldFluid) || _fHeldFluid != _focusFluid || _dead || _driving != null)
            {   // released, looked away, or can't pick up -> cancel the hold
                _fHeldFluid = null; _fluidPickupTimer = 0f; FluidPickupHudSet(null);
                return;
            }
            _fluidPickupTimer += delta;
            float frac = Mathf.Clamp(_fluidPickupTimer / DeployPickupTime, 0f, 1f);
            if (frac >= PickupBarDeadzone) FluidPickupHudSet($"picking up {_fHeldFluid.RoleLabel()}... {Mathf.Clamp((int)(frac * 100f), 0, 99)}%");   // deadzone: no readout for a quick tap
            if (_fluidPickupTimer >= DeployPickupTime)
            {
                var c = _fHeldFluid; _fHeldFluid = null; _fluidPickupTimer = 0f; FluidPickupHudSet(null);
                PickupFluid(c);
            }
        }

        // Hold F over a door you own to flip its lock (mirror of UpdateFluidPickup; a quick TAP still just
        // opens/closes it). Locking had no player-facing input at all before this -- DoorLogic.TrySetLocked
        // existed, was L0-tested, and nothing but a test ever called it, so an ownable lockable door was in
        // practice neither lockable nor unlockable. One seam, so it works the same in SP and MP.
        void UpdateDoorLockHold(float delta)
        {
            if (_fHeldDoor == null) return;
            bool fHeld = Input.MouseMode == Input.MouseModeEnum.Captured && Keybinds.Pressed(GameAction.Interact);
            if (!fHeld || !IsInstanceValid(_fHeldDoor) || _fHeldDoor != _focusDoor || _dead || _driving != null)
            {
                _fHeldDoor = null; _doorLockTimer = 0f;
                return;
            }
            _doorLockTimer += delta;
            float frac = Mathf.Clamp(_doorLockTimer / DeployPickupTime, 0f, 1f);
            if (frac >= PickupBarDeadzone)
                FluidPickupHudSet($"{(_fHeldDoor.IsLocked ? "unlocking" : "locking")} the door... {Mathf.Clamp((int)(frac * 100f), 0, 99)}%");
            if (_doorLockTimer >= DeployPickupTime)
            {
                var d = _fHeldDoor; _fHeldDoor = null; _doorLockTimer = 0f; FluidPickupHudSet(null);
                RequestSetDoorLocked(d, !d.IsLocked);
            }
        }

        Door _fHeldDoor;              // the door F is being held on -> hold to lock/unlock
        float _doorLockTimer;
        const float BarricadedMsgTime = 1.5f;   // how long "Door is barricaded" lingers after you try to open a barricaded door
        float _barricadedDoorMsg;                // seconds left to show that line (set on the F-press; ticked down in _Process)

        /// <summary>Lock or unlock a door as this player. Public for the same reason the other Request*
        /// helpers are: the hold path needs a captured mouse, which a headless test cannot have.</summary>
        public bool RequestSetDoorLocked(Door d, bool locked)
        {
            if (d == null || !IsInstanceValid(d)) return false;
            // Replicated door: the server owns the bolt, and the DoorState echo paints the result.
            if (d.NetId != 0 && NetSetDoorLocked != null) { NetSetDoorLocked(d.NetId, locked); return true; }
            if (d.TrySetLocked(PlayerId, locked))
            {
                FluidPickupHudSet(locked ? "locked" : "unlocked");
                return true;
            }
            FluidPickupHudSet("not your door");   // only the owner holds the key
            return false;
        }

        // Return a live placed fluid device to the bag: free its hoses/power wire + despawn, grant the item back (dropped at
        // its feet if the bag is full). SP-local for now (fluid MP replication is a fast-follow, like placement).
        void PickupFluid(FluidContainer c)
        {
            if (c == null || !IsInstanceValid(c)) return;
            ushort id = c.Def?.Id ?? 0;
            string name = c.Def?.Name;
            Vector3 pos = c.GlobalPosition;
            var item = id != 0 ? SDG.Unturned.Assets.makeLoot(id) : null;
            c.Pickup();   // frees its hoses + (a pump) its power wire, then despawns
            if (item != null)
            {
                bool handsFree = Unarmed;
                if (!(Inventory?.tryAddItem(item) ?? false)) DropWorldItem(item, pos + Vector3.Up * 1f);   // bag full -> drop where it stood
                else { _invUI?.Refresh(); if (handsFree) EquipItemAsset(item.GetAsset(), item); }   // hands free -> hold it (re-enters placement mode)
            }
            GD.Print($"[fluid] picked up #{id} ({name})");
        }

        // A center-screen pickup readout (fluid devices have no per-device progress billboard like a generator's).
        CanvasLayer _fluidPickupLayer; Label _fluidPickupLabel;
        void FluidPickupHudSet(string text)
        {
            if (string.IsNullOrEmpty(text)) { if (_fluidPickupLabel != null) _fluidPickupLabel.Visible = false; return; }
            if (_fluidPickupLabel == null)
            {
                _fluidPickupLayer = new CanvasLayer { Layer = 40 }; AddChild(_fluidPickupLayer);
                _fluidPickupLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
                _fluidPickupLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
                _fluidPickupLabel.AnchorLeft = 0.5f; _fluidPickupLabel.AnchorRight = 0.5f; _fluidPickupLabel.OffsetTop = 150f; _fluidPickupLabel.OffsetLeft = -300f; _fluidPickupLabel.OffsetRight = 300f;
                _fluidPickupLabel.AddThemeFontSizeOverride("font_size", 26);
                _fluidPickupLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
                _fluidPickupLabel.AddThemeConstantOverride("outline_size", 6);
                _fluidPickupLayer.AddChild(_fluidPickupLabel);
            }
            _fluidPickupLabel.Text = text; _fluidPickupLabel.Visible = true;
        }

        // Wreck salvage (master): a focused wreck shows a state prompt -- red "Too hot" while burning, red "Requires blowtorch"
        // if you have none, white "Hold LMB to salvage" with a blowtorch equipped. Holding LMB breaks it into scrap + despawns it.
        void UpdateSalvage(float delta)
        {
            var v = (_focusVehicle != null && IsInstanceValid(_focusVehicle)) ? _focusVehicle : null;
            bool lmb = Input.MouseMode == Input.MouseModeEnum.Captured && Keybinds.Pressed(GameAction.Fire) && !_dead && _driving == null && !(_invUI?.IsOpen ?? false);
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
            SaveGunState(); _heldItem = null; _heldConsumable = null; _heldFuelItem = null; _heldFluidItem = null; ClearDeployable();   // stash the outgoing gun's state; equipping a melee REPLACES any held consumable (not a layer)
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
        /// <summary>Is anything actually in hand? Fists are the unarmed state, not an item -- so this is false
        /// when unarmed, which is what makes "press an empty slot to put it away" a no-op rather than a
        /// pointless viewmodel rebuild every keypress.</summary>
        // Which holster page the held item came out of, or -1. Needed because "the held item left its slot" and
        // "a bag-bound consumable is no longer in the bag" are different events with different right answers --
        // without this, eating the last of a stack would also yank an unrelated weapon out of your hands.
        int _heldSlotPage = -1;

        public bool HasSomethingHeld => _heldItem != null || Gun != null || _heldConsumable != null
                                     || _heldFuelItem != null || _heldFluidItem != null || _deployable != null
                                     || (_heldMeleeName != null && _heldMeleeName != "fists");

        /// <summary>Record that what is now in hand came out of holster page `page` (-1 = not a holster). The
        /// inventory UI's equip path holsters an item and equips it in one gesture, so it has to say where it
        /// put it, or "take it out of the slot and it leaves your hands" would not fire for that route.</summary>
        public void NoteHeldFromSlot(int page) => _heldSlotPage = page;

        // WHERE the held item lives in the grid. Recorded so the held reference can be re-bound after an owner
        // echo -- see RebindHeldRefs, which is the fix for the held item going dangling. -1 page = not from the
        // grid (a world pickup held before it lands, fists, a tool).
        int _heldPage = -1; byte _heldX, _heldY;
        public void NoteHeldFrom(int page, byte x, byte y)
        {
            _heldPage = page; _heldX = x; _heldY = y;
            _heldSlotPage = page >= 0 && page < PlayerInventory.SLOTS ? page : -1;
        }

        /// <summary>Re-point the held-item references at the objects that are actually in the grid now.
        ///
        /// InventoryReplication.ReadSnapshot allocates a FRESH Item per jar on every snapshot and
        /// AdoptReplicatedInventory re-seats those into the shell's pages, but _heldItem was only ever assigned at
        /// equip time -- so one echo left it pointing at an object no longer in any page. Everything that writes
        /// gun state (SaveGunState is the sole writer of ammo/chamber/firemode/mag/attachments) then wrote into a
        /// dead object, and everything that compares identity silently disagreed:
        ///   - fire 25 of 30, holster, re-equip -> RestoreGunState reads gunAmmo -1 off the grid's object and
        ///     returns early, so LoadGun's defaults stand and the magazine is FULL again;
        ///   - IsHeld is a ReferenceEquals, so the gun in your hands offered "Equip" instead of "Dequip" and
        ///     DropSelected's wasHeld never fired -- dropping it left the viewmodel up.
        /// Re-binding by ADDRESS rather than by scanning for the id: two identical rifles are two different items,
        /// and picking the wrong one would be a quieter bug than the one being fixed. If the address no longer
        /// holds the same id the item genuinely moved or went away, and we leave the reference alone rather than
        /// guessing. Review 2026-08-16.</summary>
        void RebindHeldRefs()
        {
            if (_heldItem == null || _heldPage < 0 || Inventory == null) return;
            if (_heldPage >= Inventory.items.Length) return;
            var pg = Inventory.items[_heldPage];
            byte idx = pg?.getIndex(_heldX, _heldY) ?? byte.MaxValue;
            var live = idx == byte.MaxValue ? null : pg.getItem(idx)?.item;
            if (live == null || live.id != _heldItem.id || ReferenceEquals(live, _heldItem)) return;
            bool wasFuel = ReferenceEquals(_heldFuelItem, _heldItem), wasFluid = ReferenceEquals(_heldFluidItem, _heldItem);
            _heldItem = live;
            if (wasFuel) _heldFuelItem = live;
            if (wasFluid) _heldFluidItem = live;
            // Stamp the state the CLIENT owns onto the newly adopted object straight away. The server never learns
            // gunAmmo (no command carries it), so the fresh object arrives with the -1 default; without this the
            // rebind would swap a stale-but-populated object for a live-but-blank one and lose the magazine.
            SaveGunState();
        }

        public void EquipUnarmed()
        {
            SaveGunState(); ClearDeployable();
            _heldItem = null; Gun = null; _heldConsumable = null; _heldFuelItem = null; _heldFluidItem = null; _heldConsumableMesh = null;
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false;
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;
            _torchAnimOn = false; _pendingMeleeHit = -1f; _heldSlotPage = -1; _heldPage = -1;   // holding nothing -> no grid address to rebind to
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
        static int? HotbarSlot(InputEvent e) => Keybinds.HotbarSlot(e);   // shared logic lives in Keybinds so equip + bind-item read one key space

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
            // PRESSING AN EMPTY SLOT PUTS WHAT YOU ARE HOLDING AWAY (strawberry 2026-08-16: "if you are holding
            // an item, and you press a number key for an empty slot, de-equip that way too"). It used to return
            // silently, so an empty key was a no-op you could press forever.
            if (idx == byte.MaxValue) { if (HasSomethingHeld) EquipUnarmed(); return; }
            var j = pg.getItem(idx);
            // Pressing the key for what is ALREADY in hand PUTS IT AWAY (strawberry: "switching to the same slot you
            // currently have equipped will put away that item, leaving u unarmed"). Same toggle the inventory's
            // Equip<->Dequip button uses, so the hotbar and the UI agree on what "already held" means. Checked here
            // rather than inside EquipItemAsset so a genuine re-equip from elsewhere (revert-after-consumable) is
            // unaffected -- this is specifically the key-press-the-same-slot gesture.
            if (IsHeld(j.GetAsset(), j.item)) { EquipUnarmed(); return; }
            // A HOLSTER ITEM ONLY REACHES YOUR HANDS FROM ITS SLOT (strawberry: "guns can only be sent to the
            // hands if they are in the 1/2 slots"). The .dat's Slot key decides: a rifle is PRIMARY, a sidearm
            // SECONDARY, and neither can be equipped straight out of a bag page -- so a 3-9 bind on a backpack
            // cell stops acting as a third weapon slot. Everything else is NONE and unaffected, which is what
            // "binding items 3-9 still works for the equip path of all non-guns" means.
            var asset = j.GetAsset();
            if (page >= PlayerInventory.SLOTS && asset != null && !asset.slot.CanEquipFromBag())
            {
                HUD.Alert("Holster it first");
                return;
            }
            NoteHeldFrom(page, x, y);   // records the slot page AND the cell, so the held ref survives an echo
            EquipItemAsset(asset, j.item);
        }

        // Dispatch-equip an item into the hand by its asset type (gun / melee / consumable). True if it equipped.
        public bool EquipItemAsset(ItemAsset asset, SDG.Unturned.Item backing)
        {
            if (asset == null) return false;
            if (asset.gunName != null) { EquipHeldGun(asset.gunName, backing); return true; }
            if (asset.meleeName != null) { EquipHeldMelee(asset.meleeName); return true; }
            if (asset.IsFluidContainer) { EquipHeldFluidContainer(asset, backing); return true; }   // a water bottle / soda / cola / canteen: held as a CONTAINER (RMB a tank to fill, LMB sip) -- BEFORE the consumable path so it isn't drunk whole
            if (asset.IsConsumable) { EquipHeldConsumable(asset, asset.itemName?.ToLowerInvariant().Replace(" ", "_")); return true; }   // EquipHeldConsumable snapshots the revert target itself
            var deploy = DeployableDef.ById(asset.id);
            if (deploy != null) { EquipHeldDeployable(deploy, backing); return true; }   // generator/spotlight -> hold + placement ghost, LMB plants + consumes one from the bag
            var tool = ToolDef.ById(asset.id);
            if (tool != null) { EquipTool(tool, backing); return true; }   // Wire (65) / Rope (64) / future tools = data-driven (was hard-coded ids)
            if (asset.IsFuelContainer) { EquipHeldFuelCan(asset, backing); return true; }   // a gas can -> hold it, RMB a powered pump to fill it
            if (asset.type == EItemType.FISHER) { EquipHeldFisher(asset, backing); return true; }   // a fishing rod -> hold it, LMB casts (UseableFisher)
            return false;
        }

        // Equip a gas can into the hand (master's fluids): hold it, then RMB a powered gas pump to fill it. No extracted
        // carry model yet -> EmptyHands (invisible in-hand); the mechanic is what matters. HoldingWireTool clears itself
        // (it's derived from the viewmodel), and this replaces any gun/melee/consumable/deployable in hand.
        public void EquipHeldFuelCan(ItemAsset asset, SDG.Unturned.Item backing)
        {
            SaveGunState(); ClearDeployable();
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldConsumableMesh = null; _heldFluidItem = null;
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false;
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;
            _heldFuelItem = backing;
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { DeployableMesh = "gascan.txt", DeployableAlbedo = "gascan_albedo.png", NaturalHold = true };   // the ripped 1P gas-can model held with BOTH HANDS (NaturalHold -> plays the can's own two-handed Fuel_Equip carry anim, source animations.prefab); HoldingDeployable stays false (no _deployable) so RMB still extracts
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            GD.Print($"[fuel] holding {asset?.itemName} -- {FluidDef.Litres(backing != null ? Mathf.Max(0f, backing.fuelLevel) : 0f)}/{FluidDef.Litres(asset?.fuelCapacity ?? 0f)} (RMB a powered pump to fill)");
        }

        // Equip a fishing rod into the hand (UseableFisher). The rod mesh isn't ripped yet -> EmptyHands hold (the
        // mechanic + the bobber are what matter). _fishing is a fresh sim configured from the rod + the PEI table +
        // the caster's FISHING skill; LMB drives it (press to charge/cast/catch, release to fling).
        public void EquipHeldFisher(ItemAsset asset, SDG.Unturned.Item backing)
        {
            SaveGunState(); ClearDeployable();   // ClearDeployable tears down any prior rod/line before we set up the new one
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldConsumableMesh = null; _heldFuelItem = null;
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false;
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;
            _heldFisherItem = backing;
            _fishing = new FishingSim((int)(Time.GetTicksMsec() & 0x7fffffff));
            FishingContent.ConfigureForPei(_fishing, Skills.Level(EPlayerSupport.FISHING));
            _fishTockAccum = 0f;
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { EmptyHands = true };   // no rod mesh yet -> bare arms in the ready hold
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            GD.Print($"[fishing] holding {asset?.itemName} -- hold LMB to charge the cast, release to fling, LMB again on the bite to reel it in");
        }

        // Tear down the rod + any deployed line/bobber. Called by every other equip path (so switching items ends the cast)
        // and on dequip. Safe to call when not fishing.
        void ClearFisher()
        {
            _fishing = null;
            _heldFisherItem = null;
            _fishTockAccum = 0f;
            if (_bobber != null && IsInstanceValid(_bobber)) _bobber.QueueFree();
            _bobber = null;
            if (_fishLine != null && IsInstanceValid(_fishLine)) _fishLine.QueueFree();
            _fishLine = null;
        }

        // LMB with a rod out (UseableFisher.startPrimary): in Idle it starts the strength gauge; while a line's out
        // it attempts the catch -- a press inside the bite window lands the fish, otherwise it reels the line in empty.
        void FisherPrimary()
        {
            if (_fishing == null) return;
            var caught = _fishing.Press();
            if (caught.Success) GrantFish(caught);
        }

        // LMB released (UseableFisher.stopPrimary): lock in the charged strength and cast -- TickFishing spawns the bobber.
        void FisherRelease() => _fishing?.Release();

        // The rod's actual reward: the caught fish into the bag + fishing XP (UseableFisher.GrantRewards). A fish whose
        // id isn't registered in the catalog just no-ops the add (still pays XP), so a partial table can't crash a catch.
        void GrantFish(in FishingCatch caught)
        {
            var asset = SDG.Unturned.Assets.find(caught.ItemId);
            bool added = asset != null && Inventory != null && Inventory.tryAddItem(new SDG.Unturned.Item(caught.ItemId));
            Skills.AwardExperience((uint)caught.Experience);
            _invUI?.Refresh();
            GD.Print($"[fishing] caught {(asset?.itemName ?? $"#{caught.ItemId}")}{(added ? "" : " (no bag room)")} +{caught.Experience} fishing xp");
        }

        // Per-frame fishing update (UseableFisher.tock + simulate + UpdateBobber). Charges the gauge at a steady 50 Hz,
        // runs the server bite timer, flies the bobber until it hits water, and redraws the line. NetAvatar-safe (guarded
        // by HoldingFisher, which only the local owner sets).
        void TickFishing(float dt)
        {
            if (_fishing == null) return;

            // 50 Hz strength-gauge tock (framerate-independent), so the cast bar sweeps at the retail rate; the
            // catch-challenge minigame also steps here (UseableFisher.tock drives both)
            _fishTockAccum += dt;
            while (_fishTockAccum >= 0.02f) { _fishing.Tock(); _fishTockAccum -= 0.02f; }

            _fishing.Simulate(dt);

            if (_fishing.TryTakePendingCatch(out var challengeCatch)) GrantFish(challengeCatch);   // won the tracking minigame

            // cast just released -> fling the bobber out along the aim, scaled by the charged strength (retail
            // AddForce Lerp(500,1000,strength); here a launch speed the sim's projectile step integrates)
            if (_fishing.State == EFishingState.Casting && _bobber == null)
                SpawnBobber();

            if (_bobber != null && IsInstanceValid(_bobber))
            {
                if (_fishing.State == EFishingState.Casting)
                {
                    _bobberVel.Y -= 20f * dt;                                   // gravity until it splashes down
                    _bobber.GlobalPosition += _bobberVel * dt;
                    if (Terrain.HasWater && _bobber.GlobalPosition.Y <= Terrain.SeaLevelY)
                    {
                        if (BobberOverFishableWater(_bobber.GlobalPosition))
                        {
                            var p = _bobber.GlobalPosition; p.Y = Terrain.SeaLevelY; _bobber.GlobalPosition = p;   // snap to the surface
                            _bobberVel = Vector3.Zero;
                            _fishing.ConfirmBobberInWater();
                        }
                        else ClearFisher();   // splashed onto dry land / too-shallow water -> no cast (retail GetFishingVolume + minimumDepth)
                    }
                    else if (_bobber.GlobalPosition.Y < Terrain.SeaLevelY - 60f)
                        ClearFisher();   // fell into a dry gap / off-map -> abandon the cast
                }
                else if (_fishing.State == EFishingState.LineDeployed)
                {
                    // gentle bob on the surface; tug down while the fish is on the line (UpdateBobber)
                    var p = _bobber.GlobalPosition;
                    p.Y = Terrain.SeaLevelY + (_fishing.IsBiteWindowOpen ? -0.35f : Mathf.Sin(Time.GetTicksMsec() / 250f) * 0.06f);
                    _bobber.GlobalPosition = p;
                }
                else if (_fishing.State == EFishingState.CatchChallenge)
                {
                    // fish is fighting on the line -> the bobber stays yanked under the surface
                    var p = _bobber.GlobalPosition; p.Y = Terrain.SeaLevelY - 0.5f; _bobber.GlobalPosition = p;
                }
                UpdateFishLine();
            }

            // reeled back to Idle (caught or empty) -> pull the bobber + line
            if (_fishing.State == EFishingState.Idle && _bobber != null)
            {
                if (IsInstanceValid(_bobber)) _bobber.QueueFree();
                _bobber = null;
                if (_fishLine != null && IsInstanceValid(_fishLine)) { _fishLine.QueueFree(); _fishLine = null; }
            }
        }

        // Retail UseableFisher gates a cast on the bobber landing in an actual fishing WaterVolume AND >= minimumDepth(4m)
        // below the surface. The port has one global ocean plane, so "is there water here" = the terrain floor under the
        // bobber sits at least MinFishDepth below sea level. Without this you could fish on dry land (the flat Y-plane is
        // true everywhere below 25.6). Null terrain (test/fallback world) -> trust the plane so headless casts still land.
        bool BobberOverFishableWater(Vector3 pos)
        {
            var t = Terrain.Active;
            if (t == null) return true;
            return t.SampleHeight(pos.X, pos.Z) <= Terrain.SeaLevelY - Terrain.MinFishDepth;
        }

        void SpawnBobber()
        {
            Vector3 from = _cam != null ? _cam.GlobalPosition : GlobalPosition + Vector3.Up * 1.75f;
            Vector3 fwd = _cam != null ? -_cam.GlobalTransform.Basis.Z : -GlobalTransform.Basis.Z;
            _bobber = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.2f, 0.15f) },
            };
            GetTree().Root.AddChild(_bobber);
            _bobber.GlobalPosition = from + fwd * 0.6f;
            float speed = Mathf.Lerp(12f, 26f, _fishing.StrengthMultiplier);   // charged strength -> cast distance
            _bobberVel = fwd * speed + Vector3.Up * 3f;                        // a little arc
        }

        void UpdateFishLine()
        {
            if (_bobber == null || !IsInstanceValid(_bobber)) return;
            if (_fishLine == null || !IsInstanceValid(_fishLine))
            {
                _fishLine = new MeshInstance3D { Mesh = new ImmediateMesh(),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.85f, 0.8f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
                GetTree().Root.AddChild(_fishLine);
            }
            // rod tip ~ from the hand/camera; good enough without the rod mesh (bobber end is the real signal)
            Vector3 tip = _cam != null ? _cam.GlobalPosition + (-_cam.GlobalTransform.Basis.Z) * 0.5f + _cam.GlobalTransform.Basis.X * 0.25f - _cam.GlobalTransform.Basis.Y * 0.2f
                                       : GlobalPosition + Vector3.Up * 1.4f;
            var im = (ImmediateMesh)_fishLine.Mesh;
            im.ClearSurfaces();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            im.SurfaceAddVertex(tip);
            im.SurfaceAddVertex(_bobber.GlobalPosition);
            im.SurfaceEnd();
        }

        // --- test seams (headless GameTest can't drive the mouse) ---
        internal void EquipFisherForTest(ushort rodId, int seed)
        {
            _fishing = new FishingSim(seed);
            FishingContent.ConfigureForPei(_fishing, Skills != null ? Skills.Level(EPlayerSupport.FISHING) : (byte)0);
            _heldFisherItem = new SDG.Unturned.Item(rodId);
            _fishTockAccum = 0f;
        }
        internal FishingSim FisherSimForTest => _fishing;
        internal void FisherPrimaryForTest() => FisherPrimary();
        internal void FisherReleaseForTest() => FisherRelease();
        internal void TickFishingForTest(float dt) => TickFishing(dt);

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
                if (pulled > 0f) { _heldFuelItem.fuelLevel = canFuel + pulled; _invUI?.Refresh(); GD.Print($"[fuel] +{FluidDef.Litres(pulled)} from pump -> can {FluidDef.Litres(_heldFuelItem.fuelLevel)}/{FluidDef.Litres(asset.fuelCapacity)}"); }
            }
            else if (IsInstanceValid(_focusVehicle) && _focusVehicle.FuelMax > 0f)   // siphon fuel out of a car
            {
                float pulled = Mathf.Min(space, _focusVehicle.Fuel);
                if (pulled <= 0.01f) { GD.Print("[fuel] that vehicle is empty"); return; }
                _focusVehicle.Fuel -= pulled; _heldFuelItem.fuelLevel = canFuel + pulled; _invUI?.Refresh();
                GD.Print($"[fuel] siphoned {FluidDef.Litres(pulled)} from {_focusVehicle.DisplayName} -> can {FluidDef.Litres(_heldFuelItem.fuelLevel)}/{FluidDef.Litres(asset.fuelCapacity)}");
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
                GD.Print($"[fuel] poured {FluidDef.Litres(poured)} -> {_focusDeployable.Def?.Name} {FluidDef.Litres(_focusDeployable.Fuel)}/{FluidDef.Litres(_focusDeployable.FuelMax)}; can {FluidDef.Litres(_heldFuelItem.fuelLevel)} left");
            }
            else if (IsInstanceValid(_focusVehicle) && _focusVehicle.FuelMax > 0f)
            {
                float space = _focusVehicle.FuelMax - _focusVehicle.Fuel;
                if (space <= 0.01f) { GD.Print("[fuel] that tank is full"); return; }
                float poured = Mathf.Min(canFuel, space);
                _focusVehicle.Fuel += poured; _heldFuelItem.fuelLevel = canFuel - poured; _invUI?.Refresh();
                GD.Print($"[fuel] poured {FluidDef.Litres(poured)} -> {_focusVehicle.DisplayName} {FluidDef.Litres(_focusVehicle.Fuel)}/{FluidDef.Litres(_focusVehicle.FuelMax)}; can {FluidDef.Litres(_heldFuelItem.fuelLevel)} left");
            }
        }

        // Equip a fluid CONTAINER (water bottle / soda / cola / canteen) into the hand (strawberry 2026-07-23): RMB a
        // placed tank to fill it, LMB (aimed away from a tank) to sip clean water for hydration. Held with its real bottle
        // viewmodel (all four have ripped meshes). Replaces any gun/melee/consumable/fuel-can/deployable in hand.
        public void EquipHeldFluidContainer(ItemAsset asset, SDG.Unturned.Item backing)
        {
            SaveGunState(); ClearDeployable();
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldConsumableMesh = null; _heldFuelItem = null;
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false;
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;
            _heldFluidItem = backing;
            string mesh = FluidItem.HeldMesh(asset);   // most match the item name; the OJ/milk cartons map to box_orange/box_milk
            var an = ConsumableRegistry.Anims(mesh);   // reuse the drink archetype's equip/use clips so the bottle equips + a sip animates naturally
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { ConsumableMesh = $"{mesh}.txt", ConsumableAlbedo = $"{mesh}_albedo.png", ConsumableEquipClip = an.Equip, ConsumableUseClip = an.Use, ConsumableColor = ConsumableRegistry.FlatColor(mesh) };
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            GD.Print($"[fluid] holding {FluidItem.Label(backing, asset)}  ([LMB] sip · aim a tank + [RMB] to fill)");
        }

        // Test seams (headless L1): the fill/sip TRANSFER logic itself is pure (FluidItem.Fill/Sip on Item + FluidTank) and
        // is exercised directly by the fluid self-tests; these let a test drive the controller's focus + hand state if needed.
        internal void SetHeldFluidContainerForTest(SDG.Unturned.Item backing) => _heldFluidItem = backing;
        internal void SetFocusFluidForTest(FluidContainer c) => _focusFluid = c;

        // RMB with a fluid container in hand + aimed at a placed tank/source: pull as much as fits into the container
        // (type-locked, worst-quality-wins). One click, mirrors the gas-can fill.
        void TryFillContainer()
        {
            if (_heldFluidItem == null) return;
            var asset = _heldFluidItem.GetAsset();
            if (asset == null || !asset.IsFluidContainer) return;
            if (_focusFluid == null || !IsInstanceValid(_focusFluid) || _focusFluid.Tank == null) { FluidToast("aim at a tank to fill"); return; }
            float moved = FluidItem.Fill(_heldFluidItem, asset, _focusFluid.Tank, out string msg);
            if (moved <= 0f) { FluidToast(msg); return; }
            _invUI?.Refresh();
            FluidItem.Read(_heldFluidItem, asset, out var t, out var amt, out var q);
            FluidToast($"filled {FluidDef.Litres(moved)} {FluidDef.WaterName(t, q)}");
            GD.Print($"[fluid] filled {asset.itemName} +{FluidDef.Litres(moved)} -> {FluidDef.Litres(amt)} {FluidDef.WaterName(t, q)}");
        }

        // LMB with a fluid container in hand + NOT aimed at a tank: take a 50 mL sip. Only clean water / soda / cola are
        // drinkable (tainted/dirty water is refused); a sip restores hydration + plays the drink anim.
        void TryDrinkContainer()
        {
            if (_heldFluidItem == null) return;
            var asset = _heldFluidItem.GetAsset();
            if (asset == null || !asset.IsFluidContainer) return;
            if (_focusFluid != null && IsInstanceValid(_focusFluid) && _focusFluid.Tank != null) { FluidToast("aim away from the tank to drink  ([RMB] fills)"); return; }   // spec: drink while NOT looking at a container
            if (Water >= 0.999f) { FluidToast("not thirsty"); return; }   // don't waste a full bottle when already hydrated
            // equipped + LMB = CHUG the whole bottle at once (strawberry); the passive 50 mL sips are autodrink's job
            float drank = FluidItem.DrinkAll(_heldFluidItem, asset, out float hydration, out string msg);
            if (drank <= 0f) { FluidToast(msg); return; }
            Water = Mathf.Min(1f, Water + hydration);
            _invUI?.Refresh();
            _viewmodel?.PlayConsumeUse();   // drink animation (reuses the drink archetype's Use clip)
            FluidToast($"drank {FluidDef.Litres(drank)}  (+{hydration * 100f:0}% water)");
            GD.Print($"[fluid] chugged {FluidDef.Litres(drank)} from {asset.itemName} -> water {Water:0.00}");
        }

        // The held-container HUD: a persistent centered line while a fluid container is in hand (its contents + a hint),
        // briefly overridden by an action toast (filled / sipped / a refusal reason). Ticked each frame after UpdateFluidPickup.
        float _fluidToastTimer; string _fluidToast;
        void FluidToast(string msg) { _fluidToast = msg; _fluidToastTimer = 2.2f; }
        void UpdateFluidContainerHud(float delta)
        {
            if (_heldFluidItem == null || _heldFluidItem.GetAsset() is not ItemAsset a || !a.IsFluidContainer)
            {
                FluidContainerHudSet(null); _fluidToastTimer = 0f; return;
            }
            string text;
            if (_fluidToastTimer > 0f) { _fluidToastTimer -= delta; text = _fluidToast; }
            else
            {
                bool atTank = _focusFluid != null && IsInstanceValid(_focusFluid) && _focusFluid.Tank != null;
                text = FluidItem.Label(_heldFluidItem, a) + (atTank ? "     [RMB] fill from tank" : "     [LMB] sip");
            }
            FluidContainerHudSet(text);
        }
        CanvasLayer _fluidContainerLayer; Label _fluidContainerLabel;
        void FluidContainerHudSet(string text)
        {
            if (string.IsNullOrEmpty(text)) { if (_fluidContainerLabel != null) _fluidContainerLabel.Visible = false; return; }
            if (_fluidContainerLabel == null)
            {
                _fluidContainerLayer = new CanvasLayer { Layer = 40 }; AddChild(_fluidContainerLayer);
                _fluidContainerLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
                _fluidContainerLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
                _fluidContainerLabel.AnchorLeft = 0.5f; _fluidContainerLabel.AnchorRight = 0.5f; _fluidContainerLabel.OffsetTop = 185f; _fluidContainerLabel.OffsetLeft = -360f; _fluidContainerLabel.OffsetRight = 360f;
                _fluidContainerLabel.AddThemeFontSizeOverride("font_size", 24);
                _fluidContainerLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
                _fluidContainerLabel.AddThemeConstantOverride("outline_size", 6);
                _fluidContainerLayer.AddChild(_fluidContainerLabel);
            }
            _fluidContainerLabel.Text = text; _fluidContainerLabel.Visible = true;
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
        public bool Unarmed => Gun == null && _heldConsumable == null && _deployable == null && !HoldingWireTool && !HoldingHoseTool && !HoldingDetonatorTool && _heldFuelItem == null && _heldFluidItem == null && (_melee == null || _melee.Name == "fists");

        // Is this inventory item the one currently IN HAND? (drives the inventory's Equip<->Dequip toggle.)
        public bool IsHeld(ItemAsset asset, SDG.Unturned.Item item)
        {
            if (asset == null) return false;
            if (Gun != null && _melee == null && _heldConsumable == null && _deployable == null)
                return item != null ? ReferenceEquals(_heldItem, item) : (_heldItem != null && _heldItem.id == asset.id);
            if (_melee != null && _melee.Name != "fists") return asset.meleeName != null && asset.meleeName == _heldMeleeName;
            if (_heldConsumable != null) return _heldConsumable.id == asset.id;
            if (_heldFuelItem != null) return item != null ? ReferenceEquals(_heldFuelItem, item) : asset.IsFuelContainer;   // a held gas can -> dropping it goes unarmed (master)
            if (_heldFluidItem != null) return item != null ? ReferenceEquals(_heldFluidItem, item) : asset.IsFluidContainer;   // a held fluid container (bottle/canteen)
            if (_deployable != null) return _deployable.Id == asset.id;
            if (HoldingWireTool) return asset.id == 65;
            if (HoldingRopeTool) return asset.id == 64;
            if (HoldingHoseTool) return asset.id == 9118;
            if (HoldingDetonatorTool) return asset.id == 1240;
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
        public bool HoldingDetonatorTool => _viewmodel != null && _viewmodel.IsDetonatorViewmodel;   // Detonator (item 1240) in hand -> LMB fires all placed remote Charges; derived from the viewmodel (auto-clears on re-equip)
        DeployableDef _deployable;      // held deployable (null = none)
        SDG.Unturned.Item _deployItem;  // the backing inventory item (null = console `deploy`, i.e. infinite/no consume)
        BarricadePlacer _placer;        // the world-space ghost preview. BarricadePlacer is an API superset of the old
                                        // DeployablePlacer and behaves identically for Floor-mount defs, but also
                                        // accepts the WALL and CEILING faces the ground placer rejected outright.
        float _placeTimer;              // >0 while the brief place gesture runs; the object drops at 0
        Vector3 _placePoint; float _placeYaw;   // target FROZEN at click -> the object drops there even if you look away
        Vector3 _placeNormal = Vector3.Up;      // the surface normal frozen with them: a wall barricade's whole orientation
        WallSurface _placeWall; int _placeOpening = -1; int _placeFace;   // Window mount: the opening + face frozen at click (a window barricade spawns INTO the opening, not at a raw point)
        WindowOpeningMarker _placeMarker; Vector3 _placeWindowScale = Vector3.One;   // Window mount, baked-prop case: the marker + fitted panel scale frozen at click
                                                // hangs off it, and re-deriving it at drop time would read the surface the
                                                // player is looking at THEN, not the one they clicked
        const float PlaceTime = 0.45f;  // src UseableBarricade builds over the Use-clip length; a short stand-in here
        public bool HoldingDeployable => _deployable != null;

        // A gun is genuinely OUT only when one is loaded AND nothing else is in hand. A melee/held item is mutually
        // exclusive with the gun, so it fully disarms: no firing, no ammo HUD, no reload/firemode logic (master).
        public bool HasGunOut => Gun != null && _melee == null && _heldConsumable == null && _deployable == null;
        // Scope (PiP): the ScopeOverlay reads these each frame -- the look camera's ADS blend and the per-gun
        // magnification (>1 only for the augewehr for now; scoped view only renders while a scope gun is aimed).
        public float CurrentAimAlpha => _viewmodel?.AimAlpha ?? 0f;
        public float ScopeMag => HasGunOut ? (_viewmodel?.ScopeZoom ?? 0f) : 0f;   // the MOUNTED scope's real zoom (aug 4x, 8x, 16x...); 0 = iron/red-dot -> ADS sens drops as 1/zoom

        // --- bug reporter -------------------------------------------------------------------------------
        // The arms and the held item render in the viewmodel's OWN SubViewport, which GetViewport() on the
        // main tree cannot see -- a screenshot taken without compositing it comes out background-only, and a
        // background-only PNG is a VALID png, so that failure is indistinguishable from success. BugReporter
        // needs the live viewmodel to composite it in.
        public Viewmodel VM => _viewmodel;

        /// <summary>What is in the player's hands, in one string, for a bug report's context. Never null.</summary>
        public string EquippedNameForReport =>
            _deployable != null ? $"deployable:{_deployable.Name}" :
            _heldConsumable != null ? $"consumable:{_heldConsumableMesh ?? _heldConsumable.itemName}" :
            _melee != null ? $"melee:{_heldMeleeName ?? _melee.Name}" :
            Gun != null ? $"gun:{_gunName}" :
            "empty";

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
                ushort id = _heldConsumable.id;
                int eatenQuality = Inventory?.peekItemQuality(id) ?? 100;   // condition of the instance removeItemAmount will delete -> scores the moldy-food penalty against what's actually eaten
                Consume(_heldConsumable, eatenQuality);   // apply Health/Food/Water/etc. (MP too: vitals stay client-led until the vitals split; the server mirrors coarse health itself)
                var asset = _heldConsumable; string mesh = _heldConsumableMesh;
                GD.Print($"[consume] consumed {_heldConsumable.itemName}");
                _heldConsumable = null; _heldFuelItem = null; _heldFluidItem = null;             // one use per item: this one leaves the hand + is deleted (master)
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
        // test seam: drive a held fluid-container sip from a headless test (no look-ray / focus needed)
        public void DebugDrinkContainer() => TryDrinkContainer();

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
            ClearFisher();   // this equip path sets _deployable directly (doesn't go through ClearDeployable) -> reel in the rod here
            if (_deployable == null) _revertEquip = CaptureHeldForRevert();   // fresh switch INTO a deployable -> remember what to fall back to when the last one is placed
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldFuelItem = null; _heldFluidItem = null; _heldConsumableMesh = null;
            _reloading = false; _torchAnimOn = false;
            _deployable = def; _deployItem = backing; _placeTimer = 0f;
            _viewmodel?.QueueFree();
            _viewmodel = def.HoldMesh != null
                ? new Viewmodel { DeployableMesh = def.HoldMesh, DeployableAlbedo = def.HoldAlbedo }   // carry model in-hand + Deploy_Equip raise; LMB plays Deploy_Use
                : new Viewmodel { EmptyHands = true };   // no extracted carry model yet (spotlight) -> ghost-only feedback
            AddChild(_viewmodel);
            RelinkViewmodelLighting();
            _placer?.QueueFree();
            _placer = new BarricadePlacer();
            GetParent().AddChild(_placer);      // world space: the ghost stays put in the world, not glued to the player
            // Structures get a say in where a barricade may mount. Via the shared hook, NOT CanAttach directly:
            // CanAttach answers "is there a structure face here" and returns false on open terrain, which would
            // make every generator and crate unplaceable on the ground.
            _placer.CanAttach = StructureManager.BarricadeAttachHook;
            _placer.SetDef(def);            // carries the def's own mount family (Floor / Wall / Sticky)
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
            bool alreadyThisKind = def.IsRope ? HoldingRopeTool : def.IsHose ? HoldingHoseTool : def.IsDetonator ? HoldingDetonatorTool : HoldingWireTool;
            if (!alreadyThisKind) _revertEquip = CaptureHeldForRevert();   // remember what to fall back to
            _heldItem = null; Gun = null; _melee = null; _heldMeleeName = null; _heldConsumable = null; _heldFuelItem = null; _heldFluidItem = null; _heldConsumableMesh = null;
            _reloading = false; _torchAnimOn = false; ClearDeployable();
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { ToolMesh = def.HeldMesh, ToolColor = def.HeldColor, IsRopeTool = def.IsRope, IsHoseTool = def.IsHose, IsDetonatorTool = def.IsDetonator };
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
        public void EquipDetonator(SDG.Unturned.Item backing = null) => EquipTool(ToolDef.Detonator, backing);   // item 1240: the remote-charge trigger

        // Detonator (item 1240) LMB plunge: fire every placed remote Charge (SP: they're all yours). The source detonator
        // staggers ~1/tick + only fires SELECTED charges; the port blows them all at once (each is Proof_Explosion so no
        // early chain). A charge also self-detonates when shot -- this is the intended remote trigger.
        internal void TryDetonateCharges()
        {
            int n = Deployable.DetonateAllCharges(GetTree());
            GD.Print($"[detonator] plunge -> fired {n} charge(s)");
        }

        // Put the held deployable away (called whenever another item is equipped).
        void ClearDeployable()
        {
            ClearFisher();   // every equip-into-hand path funnels through here -> a switch away from the rod also reels in the line
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
            _placePoint = _placer.Point; _placeYaw = _placer.Yaw; _placeNormal = _placer.Normal;   // FROZEN at click (strawberry: don't drift with the mouse)
            _placeWall = _placer.SnappedWall; _placeOpening = _placer.SnappedOpening; _placeFace = _placer.SnappedFace;   // Window mount: freeze which opening + face we snapped to
            _placeMarker = _placer.SnappedMarker; _placeWindowScale = _placer.WindowScale;   // baked-prop case: freeze the marker + the fitted panel scale
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
                _placer.Freeze(_placePoint, _placeNormal, _placeYaw);   // normal too: a wall ghost frozen with an assumed up-normal snaps flat mid-gesture
                _placeTimer -= dt;
                if (_placeTimer <= 0f)
                {
                    if (_deployable.IsStorage)   // STORAGE device (fridge): singleplayer spawns a Refrigerator LOCALLY here
                    {
                        // ...but NOT in MP any more. The fridge is a replicated deployable now, so the server
                        // places it and DeployableReplicaView materializes it for everybody INCLUDING the
                        // placer. Spawning locally as well would leave the placer looking at two fridges in
                        // the same spot -- one real and shared, one a ghost only he can see and only he can
                        // open, since the server's crate is keyed to the replicated NetId.
                        if (NetPlaceDeployable == null) FridgeDeploy.SpawnFor(_deployable, GetParent(), _placePoint, _placeYaw);
                        PlayPlaceSound(_deployable.PlaceSound, _placePoint);
                        GD.Print($"[storage] placed {_deployable.Name} at {_placePoint}");
                        if (_deployItem != null && Inventory != null)
                        {
                            ushort id = _deployItem.id;
                            if (NetPlaceDeployable != null)
                            {   // net seam active (loopback/MP): the SERVER spends the item -- OnPlaceDeployable removes it,
                                // and now ALSO places the storage device for real and registers its grid (it is in the
                                // schema as of the fridge-replication change; it used to be filtered out and no-op).
                                // SKIP the local mutation (P1 invariant): else the owner-inventory re-adopt would restore the
                                // item (the dupe-on-any-inv-move bug fluid hit -- strawberry). Predict the echo.
                                NetPlaceDeployable(_deployable.Id, _placePoint, _placeYaw);
                                if (Inventory.getItemCount(id) <= 1) { (_revertEquip ?? EquipUnarmed)(); return; }   // last one just went over the wire -> revert
                            }
                            else
                            {
                                Inventory.removeItemAmount(id, 1);   // pure SP (no seam): consume locally
                                if (Inventory.getItemCount(id) <= 0) { (_revertEquip ?? EquipUnarmed)(); return; }
                            }
                        }
                        _viewmodel?.PlayDeployHold();
                        return;
                    }
                    // FLUID device, or a placeable DOOR: spawn LOCALLY (rides the ghost/place flow; device MP
                    // replication = fast-follow). Doors ride this branch rather than getting a third copy of it
                    // -- everything below the spawn call (place sound, the net-vs-SP item spend, the revert on
                    // the last one) is identical for all three, and the storage/fluid pair have ALREADY drifted
                    // slightly apart from each other. One more copy is one more thing to fix in three places.
                    if (_deployable.Fluid != null || _deployable.DoorProp != null)
                    {
                        bool isDoor = _deployable.DoorProp != null;
                        if (isDoor) DoorDeploy.SpawnFor(_deployable, GetParent(), _placePoint, _placeYaw);
                        else FluidDeploy.SpawnFor(_deployable, GetParent(), _placePoint, _placeYaw);
                        PlayPlaceSound(_deployable.PlaceSound, _placePoint);
                        GD.Print($"[{(isDoor ? "door" : "fluid")}] placed {_deployable.Name} at {_placePoint}");
                        if (_deployItem != null && Inventory != null)
                        {
                            ushort id = _deployItem.id;
                            if (NetPlaceDeployable != null)
                            {   // net seam active (loopback/MP): the SERVER spends the item -- OnPlaceDeployable removes it,
                                // then ServerPlace no-ops the fluid id (filtered from the schema) so NO phantom replica spawns.
                                // SKIP the local mutation (P1 invariant): else the owner-inventory re-adopt would restore the
                                // item (the "fluid dupes: gone on place, back on any inv move" bug -- strawberry). Predict the echo.
                                NetPlaceDeployable(_deployable.Id, _placePoint, _placeYaw);
                                if (Inventory.getItemCount(id) <= 1) { (_revertEquip ?? EquipUnarmed)(); return; }   // last one just went over the wire -> revert
                            }
                            else
                            {
                                Inventory.removeItemAmount(id, 1);   // pure SP (no seam): consume locally
                                if (Inventory.getItemCount(id) <= 0) { (_revertEquip ?? EquipUnarmed)(); return; }
                            }
                        }
                        _viewmodel?.PlayDeployHold();
                        return;
                    }
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
                    // A non-Floor def is a SURFACE barricade: it has to be re-seated against the frozen normal, and
                    // Deployable.Spawn only knows how to stand things up on the ground. Floor defs (every existing
                    // deployable -- generators, crates, charges) take the original path unchanged.
                    if (_deployable.Mount == BarricadeMount.Window && _placeMarker != null && IsInstanceValid(_placeMarker))
                        Barricade.PlaceInWindowMarker(_placeMarker, _placeFace, _placePoint, _placeYaw, _placeWindowScale, _deployable, backing: _deployItem);   // baked prop: snap onto the frozen marker
                    else if (_deployable.Mount == BarricadeMount.Window && _placeWall != null && IsInstanceValid(_placeWall))
                        Barricade.PlaceInWindow(_placeWall, _placeOpening, _placeFace, _deployable, backing: _deployItem);   // live wall: snap INTO the frozen opening + face
                    else if (_deployable.Mount != BarricadeMount.Floor)
                        Barricade.PlaceOnSurface(GetParent(), _deployable, _placePoint, _placeNormal, _placeYaw, backing: _deployItem);
                    else
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
        public void DebugFinishMagAnim() { _magSwapAnimTimer = 0; _magSwapAutoRack = false; _viewmodel?.SetReloading(false); }   // test: simulate the mag-swap / rack anim finishing so the cooldown clears (a follow-up mag action isn't blocked)
        public bool DebugUsesShells() => UsesShells;         // test: does the gun feed from loose shells
        public int DebugCountShells() => CountShells();      // test: shells of the gun's caliber carried
        /// <summary>The damage ONE projectile of the next shot carries: the loaded shell's override if it has
        /// one (slug 40 / beanbag 20), else the gun's cartridge Damage (12ga buckshot = 12 per pellet).</summary>
        float ShotDamage() => (UsesShells && ShellAsset != null && ShellAsset.damageOverride > 0f)
            ? ShellAsset.damageOverride
            : (Gun?.Damage ?? 34f);
        public float DebugShotDamage => ShotDamage();   // test hook -- the SAME call the fire path makes, not a copy

        public int DebugPellets() => UsesShells && ShellAsset != null ? System.Math.Max(1, ShellAsset.pellets) : System.Math.Max(1, Gun?.Pellets ?? 1);   // test: rays per shot (shotgun = shell pellets)
        /// <summary>Open the crafting index. Called by the inventory navbar's Craft tab and by Y.</summary>
        public void OpenCrafting()
        {
            _craftMenu?.Open();
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }

        // the inventory's quick-craft bar queues a craft into the SAME crafting queue (LMB = 1, RMB = 5).
        public void QuickCraft(BlueprintDef bp, int n) => _craftMenu?.QueueCraft(bp, n);

        // The crafting-station tags the player currently has access to (strawberry's mechanic): for each placed
        // deployable that PROVIDES crafting tags, grant them if the player is within its CraftingRange AND a single
        // line-of-sight raycast to it is clear. Empty set = no station nearby -> only craft-anywhere recipes.
        public System.Collections.Generic.HashSet<string> CraftingStationTags()
        {
            var tags = new System.Collections.Generic.HashSet<string>();
            var tree = GetTree();
            if (tree == null) return tags;
            Vector3 eye = _cam != null ? _cam.GlobalPosition : GlobalPosition + Vector3.Up * 1.5f;
            var space = GetWorld3D()?.DirectSpaceState;
            foreach (var n in tree.GetNodesInGroup("deployables"))
            {
                if (n is not Deployable d || d.Def?.CraftingTags == null || d.Def.CraftingTags.Length == 0) continue;
                Vector3 sp = d.GlobalPosition;
                if (eye.DistanceSquaredTo(sp) > d.Def.CraftingRange * d.Def.CraftingRange) continue;   // outside the radius
                if (space != null)   // ONE line-of-sight raycast eye -> station, ignoring the player + the station body
                {
                    var q = PhysicsRayQueryParameters3D.Create(eye, sp, 1u << 0);
                    q.Exclude = new Godot.Collections.Array<Rid> { GetRid(), d.GetRid() };
                    if (space.IntersectRay(q).Count > 0) continue;   // a wall between -> access denied
                }
                foreach (var t in d.Def.CraftingTags) tags.Add(t);
            }
            return tags;
        }

        public void DebugSetHeldItem(SDG.Unturned.Item it) => _heldItem = it;      // test: link a backing item to the held gun
        public void DebugSaveGunState() => SaveGunState();                          // test: mirror live gun state to the backing item
        public void DebugStartReload() => StartReload();                            // test: begin a real reload (timer + anim), so a swap can land MID-reload
        public bool DebugIsReloading => _reloading;                                 // test: is a reload still in flight?
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
        // Fire-selector / attachment click. Source: toggling a tactical or flipping the fire-selector both fire
        // EffectManager.TriggerFiremodeEffect -> the shared "Firemode" effect (GUID bc41e0fe...) whose AudioClip is
        // Firemode.mp3. Master wired this same click to firemode-cycle + putting on/taking off attachments in the
        // attach UI. A 2D one-shot (your own gun) like PlayConsumeSound.
        AudioStreamPlayer _selectorAudio;
        // ---- HANDHELD FLASHLIGHT (source: ItemMeleeAsset "Light" + UseableMelee) -------------------------------
        //
        // The torch is a MELEE item in retail, not a gun attachment: flashlight.dat is `Type Melee / Useable Melee /
        // Slot Secondary` with a bare `Light` key, and UseableMelee carries the whole implementation (its own
        // light hooks, its own net message, its own state byte). The gun-rail light is a DIFFERENT thing on a
        // different path -- ItemTacticalAsset + UseableGun.askInteractGun with state[12]. Same key, two systems;
        // wiring the handheld one through the gun path would be the wrong mechanism.
        //
        // Source toggle, UseableMelee.ReceiveInteractMelee():
        //     if equipment.isBusy -> return; if asset == null -> return; if !isLight -> return
        //     interact = !interact;  state[0] = interact ? 1 : 0;  sendUpdateState()
        //     EffectManager.TriggerFiremodeEffect(position)          <- the same click the fire selector makes
        // so the busy guard, the toggle, and the selector click below are all source shape, not invention.
        //
        // KEY: source binds this to ControlsSettings.tactical, default KeyCode.B (ControlsSettings.cs bind(TACTICAL,
        // KeyCode.B)) -- NOT F (that is INTERACT) and not RMB. B was already the port's build-mode toggle, so a held
        // light claims it and everything else falls through unchanged. That collision is real and worth knowing:
        // you cannot open build mode while holding a lit torch. Flagged rather than silently rebinding either one.
        public bool HoldingLight => _melee is { Light: true };
        public bool HeldLightOn => _heldLightOn;
        /// <summary>Is the held item actually IN hand — i.e. the equip (pull-out) animation has finished? This is
        /// the port's stand-in for source's `player.equipment.isBusy`, and it gates the light toggle. Exposed so a
        /// test can WAIT for it instead of guessing a tick count: the equip clip is roughly a second, and a fixed
        /// "yield 4 ticks" both fails today and passes for the wrong reason the day the clip gets shorter.</summary>
        public bool HeldItemReady => _viewmodel == null || _viewmodel.IsEquipComplete;
        bool _heldLightOn;
        SpotLight3D _heldLight;

        public void ToggleHeldLight()
        {
            if (!HoldingLight) return;
            // source guards on player.equipment.isBusy -- the port's equivalent is the equip animation still playing,
            // which is the same "you are mid-swap, the item isn't really in your hand yet" state.
            if (_viewmodel != null && !_viewmodel.IsEquipComplete) return;
            _heldLightOn = !_heldLightOn;
            ApplyHeldLight();
            PlaySelectorSwitchSound();   // source fires the firemode effect on this toggle, not a bespoke click
        }

        void ApplyHeldLight()
        {
            bool want = _heldLightOn && HoldingLight && _melee.SpotEnabled;
            if (!want) { if (IsInstanceValid(_heldLight)) _heldLight.Visible = false; return; }
            if (!IsInstanceValid(_heldLight))
            {
                _heldLight = new SpotLight3D
                {
                    // SpotAngle is Godot's HALF-angle; the .dat carries Unity's FULL cone. Halving is the whole
                    // difference between a 90-degree torch and a 180-degree floodlight.
                    SpotRange = _melee.SpotRange,
                    SpotAngle = _melee.SpotAngleFull * 0.5f,
                    LightColor = _melee.SpotColor,
                    LightEnergy = _melee.SpotIntensity,
                    SpotAngleAttenuation = 1.0f,
                    ShadowEnabled = false,   // a held light casting shadows re-renders the world every step; the streetlights don't either
                };
                _cam?.AddChild(_heldLight);   // rides the eye, so the beam points where you look
            }
            _heldLight.Visible = true;
        }

        public void PlaySelectorSwitchSound()
        {
            var stream = LoadWavOneShot("res://content/sounds/firemode.wav");
            if (stream == null) return;
            if (_selectorAudio == null || !IsInstanceValid(_selectorAudio)) { _selectorAudio = new AudioStreamPlayer(); AddChild(_selectorAudio); }
            _selectorAudio.Stream = stream;
            _selectorAudio.Play();
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
            if (IsSwimming || _swimMeleeGrace > 0f) return;   // no melee/punching while swimming (source PlayerEquipment: "No punching while swimming"; canUseUnderwater=false). The grace also blocks a swing for a beat AFTER surfacing: a Fire click the engine buffers during the water->land transition arrives the frame after IsSwimming clears, which used to sneak a "queued" punch through on exit (master, intermittent).
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
        float _swimMeleeGrace; const float SwimMeleeGraceTime = 0.2f;   // >0 = surfaced within the last SwimMeleeGraceTime s -> melee still blocked, so a transition-buffered click can't fire a punch on water exit
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
                else { _focusVehicle.TakeDamage((_melee?.VehicleDamage ?? 10f) * mult); MeleeImpactFx(_focusVehicle.GlobalPosition, false, Surf.Metal); GD.Print($"[melee] hit {_focusVehicle.DisplayName} for {(_melee?.VehicleDamage ?? 10f) * mult:0}"); }
                return;
            }
            if (_focusDeployable != null && IsInstanceValid(_focusDeployable) && !_focusDeployable.IsWreck
                && (_focusDeployable.GlobalPosition - GlobalPosition).Length() < range + 2f)   // looking at a placed generator: melee damages it (a blowtorch is for salvaging the wreck, not smashing)
            {
                if (HasBlowtorch) { if (_focusDeployable.Hurt) _focusDeployable.Repair(_melee?.VehicleDamage ?? 10f); }   // blowtorch repairs a hurt generator (continuous heal is in UpdateSalvage)
                else { _focusDeployable.TakeDamage((_melee?.VehicleDamage ?? 10f) * mult); MeleeImpactFx(_focusDeployable.GlobalPosition, false, Surf.Metal); GD.Print($"[melee] hit {_focusDeployable.Def?.Name} for {(_melee?.VehicleDamage ?? 10f) * mult:0}"); }
                return;
            }
            // Barricades take hits like a generator does. Without this doors and beds carried Health and a
            // TakeDamage nothing in the game ever called -- so "break a bed, its owner loses their spawn"
            // was unreachable while playing, and the tests that called TakeDamage directly proved only
            // that the method worked.
            if (_focusDoor != null && IsInstanceValid(_focusDoor)
                && (_focusDoor.GlobalPosition - GlobalPosition).Length() < range + 2f)
            {
                _focusDoor.TakeDamage((_melee?.VehicleDamage ?? 10f) * mult); MeleeImpactFx(_focusDoor.GlobalPosition, false, Surf.Wood);
                return;
            }
            if (_focusBed != null && IsInstanceValid(_focusBed)
                && (_focusBed.GlobalPosition - GlobalPosition).Length() < range + 2f)
            {
                _focusBed.TakeDamage((_melee?.VehicleDamage ?? 10f) * mult); MeleeImpactFx(_focusBed.GlobalPosition, false, Surf.Wood);
                return;
            }

            // Structures take melee too, or a placed base carries Health that nothing in the game can ever
            // reduce -- the same gap doors and beds had before their block above was added, where the tests
            // exercised TakeDamage directly and proved only that the method worked. Vulnerability is the
            // catalog's (retail's isVulnerable): metal shrugs off a hatchet, which is why the tier ladder is
            // worth climbing.
            if (MeleeStructure((_melee?.VehicleDamage ?? 10f) * mult, range)) return;

            float dmg = (_melee?.ZombieDamage ?? 45f) * mult * Skills.OverkillMeleeMultiplier();   // weapon .dat Zombie_Damage x OVERKILL skill
            Vector3 origin = GlobalPosition + Vector3.Up * 1.2f, fwd = -_cam.GlobalTransform.Basis.Z;
            if (MeleeTree(dmg, range)) return;   // an axe swing at a tree fells it, before the swing reaches a zombie/animal behind it
            foreach (var n in GetTree().GetNodesInGroup("zombies"))   // zombies take melee (one target per swing)
                if (n is ZombieBody z && !z.Dead)
                {
                    Vector3 to = z.GlobalPosition + Vector3.Up - origin;
                    if (to.Length() < range + 0.5f && to.Normalized().Dot(fwd) > 0.3f)   // in front, in reach
                    {
                        bool wasDead = z.Dead;
                        z.Damage(dmg, origin);
                        MeleeImpactFx(z.GlobalPosition + Vector3.Up, true);   // blood + flesh thunk + hitmarker
                        if (!wasDead && z.Dead) Kills++;
                        break;   // one target per swing
                    }
                }
            foreach (var n in GetTree().GetNodesInGroup("animals"))   // wildlife takes melee (one target per swing)
                if (n is AnimalAgent a && !a.Dead)
                {
                    Vector3 to = a.GlobalPosition + Vector3.Up * 0.5f - origin;
                    if (to.Length() < range + 0.5f && to.Normalized().Dot(fwd) > 0.3f)   // in front, in reach
                    {
                        a.DamageHit(dmg, a.GlobalPosition + Vector3.Up * 0.5f, fwd);
                        MeleeImpactFx(a.GlobalPosition + Vector3.Up * 0.5f, true);
                        break;
                    }
                }
        }

        // Melee HIT feedback (master: "wire up melee swing/hit sounds + unarmed damage"). The swing already DEALS damage
        // (MeleeAttack -> ApplyMeleeHit); this makes it REGISTER: a flesh hit sprays blood + plays impact_flesh.wav + pops a
        // hitmarker (SpawnFleshImpact already carries the audio); a structure hit plays the material thunk. Reuses the gun
        // impact infra. Unturned has NO swing whoosh (verified: no swing clip in the bundle, no AudioSource on melee prefabs),
        // so a MISS is silent -- source-accurate. Covers bare FISTS (they run the same ApplyMeleeHit path).
        void MeleeImpactFx(Vector3 point, bool flesh, Surf surf = Surf.Concrete)
        {
            if (flesh) { SpawnFleshImpact(point, -(_cam?.GlobalTransform.Basis.Z ?? Vector3.Back)); HitmarkerHUD.Instance?.Show(false); }
            else
            {
                string mb = GameAudio.MeleeSurface(surf);   // retail effects/physics/meleeimpact/<material> where the bank exists (metal, grass); the rest keep the material thunk
                var retail = mb != null ? GameAudio.Pick("meleeimpacts", mb) : null;
                PlayImpactSound(retail ?? ImpactSnd(surf), point);
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
            GameAudio.Explosion(this, point, radius);   // retail Bomb effect audio (effects/explosions/bomb_N/fire), ripped 2026-09-03
            foreach (var n in GetTree().GetNodesInGroup("zombies"))   // zombies caught in the blast: linear falloff + wall rule
                if (n is ZombieBody z && !z.Dead)
                {
                    float zr = z.GlobalPosition.DistanceTo(point);
                    if (zr > radius || ExplosionBlocked(point, z.GlobalPosition)) continue;
                    bool wasDead = z.Dead;
                    z.Damage(ExplosionMath.Linear(zombieDamage, zr, radius), point);
                    if (!wasDead && z.Dead) Kills++;
                }
            foreach (var n in GetTree().GetNodesInGroup("animals"))   // wildlife caught in the blast: same linear falloff + wall rule
                if (n is AnimalAgent a && !a.Dead)
                {
                    float range = a.GlobalPosition.DistanceTo(point);
                    if (range > radius) continue;
                    if (ExplosionBlocked(point, a.GlobalPosition)) continue;
                    a.DamageHit(ExplosionMath.Linear(zombieDamage, range, radius), a.GlobalPosition, (a.GlobalPosition - point).Normalized());
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
            if (Terrain.HasWater && point.Y <= Terrain.SeaLevelY + 2f)   // blast at/below the ocean -> a big water column (retail Explosions/water_0)
            {
                var wscene = GetTree().CurrentScene;
                if (wscene != null) SpawnWaterSplash(wscene, new Vector3(point.X, Terrain.SeaLevelY, point.Z), Mathf.Clamp(radius / 5f, 2f, 4f));
            }
            GD.Print($"[explode] r={radius} at {point}");
        }

        // Explosion line-of-sight (source ExplosionDamageParameters.LineOfSightTest): raycast from the blast to the target
        // on the WORLD/LOS-blocking layer -- a wall/terrain between them shields the target (no damage). Both ends raised to
        // chest height so the ray doesn't graze the ground; targets aren't on WorldLayer so only walls register.
        bool ExplosionBlocked(Vector3 point, Vector3 target)
        {
            Vector3 a = point + Vector3.Up * 0.8f, b = target + Vector3.Up * 0.8f;
            var q = PhysicsRayQueryParameters3D.Create(a, b, WorldLayers.World);
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
        StoreShelf _openDoorShelf;   // the doored container (fridge/wardrobe/counter) whose leaf is swung open by the current open inventory -- tracked apart from _openCrate/_openCrateNetId so the door shuts on BOTH the SP and MP close paths

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
            _openDoorShelf = crate as StoreShelf; _openDoorShelf?.SetDoorsOpen(true);   // a doored container (fridge/wardrobe/counter): swing its leaf open the instant you interact -- BEFORE the replicated early-return so it fires in SP + MP alike (local cosmetic)
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
            _invUI?.Open();      // Open() also scans the AREA (Nearby) page for dropped ground loot
            Input.MouseMode = Input.MouseModeEnum.Visible;
            return true;
        }

        // save the open crate's contents back and clear the STORAGE view (called when the dashboard closes)
        void CloseCrate()
        {
            _openDoorShelf?.SetDoorsOpen(false); _openDoorShelf = null;   // swing a doored container's leaf shut on close -- at the top so it covers BOTH the SP copy-back and the MP _openCrateNetId early-return below
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

        // Scan dropped ground items (WorldItems in the "worlditems" group) within a radius and pack them into the AREA /
        // "Nearby" page so the dashboard's Nearby bar shows real ground loot. Rescanned each time the bag opens. The UI
        // half (tinyclaw) already draws the bar + grid the moment the page is non-zero; drops route through the same _drop.
        /// <summary>Nearby/AREA radius, squared. 16 = 4 m, the source's value (see ScanNearbyItems).</summary>
        public const float NearbyRadiusSq = 16f;

        public void ScanNearbyItems()
        {
            if (Inventory == null || !IsInsideTree()) return;   // no-op in headless/test contexts without a live world
            var area = Inventory.items[PlayerInventory.AREA];
            area.clear();
            var found = new System.Collections.Generic.List<SDG.Unturned.Item>();
            // Source radius: onItemDropAdded rejects on `(model.position - eyesPosition).sqrMagnitude > 16`,
            // and findSimulatedItemsInRadius takes a **sqrRadius** (its parameter is literally named that) of
            // the same 16 -- so both agree on 4 m, measured from the EYES, not the feet. Ours was 6 m from the
            // body origin, which on a tall player is a materially different volume.
            const float rSq = NearbyRadiusSq;
            Vector3 eye = _cam != null ? _cam.GlobalPosition : GlobalPosition + Vector3.Up * 1.6f;
            foreach (var n in GetTree().GetNodesInGroup("worlditems"))
                if (n is WorldItem wi && wi.Item != null
                    && eye.DistanceSquaredTo(wi.GlobalPosition) < rSq
                    && wi.HasLineOfSightFrom(eye))     // don't list loot through a wall (source: Linecast, BLOCK_PICKUP)
                    found.Add(wi.Item);
            if (found.Count == 0) { area.loadSize(0, 0); return; }
            area.loadSize(6, 6);
            foreach (var it in found) area.tryAddItem(it);
        }

        // MP storage state (wired only by ClientWorldSession): the crate the SERVER says we have open.
        // Latched on the StorageOpened fact -- never on the request -- so the dashboard mirrors arbitration.
        uint _openCrateNetId;
        public bool DashboardOpen => _invUI?.IsOpen ?? false;   // L1 net tests: did the storage fact open the dashboard

        /// <summary>Is any UI up that wants the cursor? Asked before ANYTHING recaptures the mouse, because
        /// recapturing under an open panel is worse than leaving it free: every polled input in here gates on
        /// `MouseMode == Captured`, so the player starts walking and auto-firing while staring at a dashboard
        /// they can no longer click. Review 2026-08-16.</summary>
        public bool AnyBlockingUiOpen
            => (_invUI?.IsOpen ?? false) || (_skillsUI?.IsOpen ?? false) || (AmmoRadial?.IsOpen ?? false);
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
            // ANNOUNCE THE REBUILD, once, at the end -- when the page has its FINAL contents. clear() is silent by
            // design (a mid-rebuild "this page is empty" would de-equip the player's gun on every echo) and addItem
            // only fires when there is something to add, so a page the SERVER emptied raised no event whatsoever.
            // That is what made the de-equip-on-slot-emptied rule dead in the shipped game while its direct-path
            // test passed: the game empties slots through here, and the test called removeItem in-process.
            // Raising it unconditionally also covers the re-add case, where it is a harmless idempotent repaint.
            to.raiseStateUpdated();
        }

        /// <summary>MP (wired only by ClientWorldSession): copy the replicated owner-block grid INTO the
        /// shell's EXISTING Inventory instance -- never swap the reference (InventoryUI/CraftingMenu, the
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
            RebindHeldRefs();   // the jars are all new objects now -- re-point what the player is holding at them
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
        // The map's full regular-spawn set (pre-sampled to ground + facing yaw), handed in by WorldBuilder.
        // Death re-rolls a RANDOM one of these (source LevelPlayers.getSpawn runs on every respawn, not just the
        // first spawn); null/empty on fallback/no-map worlds -> respawn falls back to the single Spawn point.
        public System.Collections.Generic.List<(Vector3 pos, float yaw)> RespawnPoints;

        // A random regular spawn from RespawnPoints (facing its angle), or the single Spawn fallback if the map
        // had no spawn file. Used by Respawn(); the bed claim still overrides this when the player has one.
        Vector3 PickRandomSpawn()
        {
            if (RespawnPoints != null && RespawnPoints.Count > 0)
            {
                var rng = new RandomNumberGenerator(); rng.Randomize();
                var s = RespawnPoints[rng.RandiRange(0, RespawnPoints.Count - 1)];
                RotationDegrees = new Vector3(0f, s.yaw, 0f);   // face the spawn's angle, same as the initial spawn
                return s.pos;
            }
            return Spawn;
        }

        // Zombie sensing (AlertTool/PlayerStance): Agro increments once per zombie that starts hunting this
        // player -- it drives their approach path (every 3rd zombie RUSHes, the rest split left/right, so a
        // horde fans out to surround). Moving/Stance feed the stealth detection radius below.
        public int Agro;
        public bool Moving { get; private set; }
        public EPlayerStance Stance => _move.Stance;
        float _footNoiseT;   // Phase 3 hearing: throttle the continuous footstep-noise emit (~2.5x/s while moving)
        float _strideAcc;    // metres of ground covered since the last footstep sound
        /// <summary>Material under the feet for footstep/landing audio: water when wading, else the terrain splatmap or a
        /// prop's SurfMeta via a short downward ray. Concrete when nothing says otherwise.</summary>
        Surf FootSurfaceUnderFeet()
        {
            // A miss means the short ray found no floor. The local shell only asks while IsOnFloor(), so that is
            // a floor the ray failed to catch rather than thin air -- Concrete, as it always was. A PUPPET has no
            // IsOnFloor to lean on and reads the same miss as airborne, which is the whole reason the probe below
            // reports "nothing underfoot" separately instead of flattening it into a default surface.
            return TryFootSurfaceAt(this, GlobalPosition, GetRid(), out var s) ? s : Surf.Concrete;
        }

        /// <summary>The shared footstep/landing surface probe: water when wading, else the terrain splatmap or a
        /// prop's SurfMeta under <paramref name="pos"/>. Returns FALSE when nothing is underfoot at all. Static and
        /// position-taking so the remote puppets in RemotePlayers resolve ground the SAME way the local shell does
        /// -- one rule, so a floor that sounds like metal underfoot cannot sound like concrete to everyone else.</summary>
        public static bool TryFootSurfaceAt(Node3D ctx, Vector3 pos, Rid exclude, out Surf surf)
        {
            surf = Surf.Concrete;
            if (Terrain.HasWater && pos.Y < Terrain.SeaLevelY + 0.1f) { surf = Surf.Water; return true; }   // wading IS ground: you make noise on it
            var space = ctx?.GetWorld3D()?.DirectSpaceState; if (space == null) return false;
            var q = PhysicsRayQueryParameters3D.Create(pos + Vector3.Up * 0.3f, pos + Vector3.Down * 0.6f, 1u << 0, new Godot.Collections.Array<Rid> { exclude });
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return false;
            if (hit["collider"].As<GodotObject>() is Node n)
            {
                if (Terrain.Active != null && n.IsInGroup("terrain")) { surf = Terrain.Active.SurfAt(pos.X, pos.Z); return true; }
                if (n.HasMeta(SurfMeta)) { surf = (Surf)(int)n.GetMeta(SurfMeta); return true; }
            }
            return true;   // something solid, just unlabelled -- concrete
        }

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
        /// <summary>Is the player a PASSENGER? Exposed for the teleport report: a passenger's position is forced to the
        /// vehicle every physics tick, so TeleportTo moves them and the next tick drags them straight back -- a real
        /// way for a teleport to "not take" that is otherwise indistinguishable from any other.</summary>
        public bool IsRidingForDebug => _riding != null;
        public bool IsRiding => _riding != null;
        public VehiclePuppet RidingPuppet => _riding;
        public UnityEngine.Vector2 LastDriveInput;   // captured while riding: x=steer, y=throttle (the DriveVehicle axes)
        // HELICOPTER STICK (VoX 2026-08-15: "mouse movements to control the pitch and roll"). Mouse motion is a
        // RATE, not a position: the stick deflects while the mouse moves and self-centres when it stops, so the
        // airframe changes attitude as you move and HOLDS that attitude when you let go. That is the Rust feel,
        // and it is also the only mapping that works against a flight model driving angular VELOCITY -- a stick
        // that stayed deflected would just spin forever.
        float _heliStickP, _heliStickR;
        // Base mouse-pixels -> stick deflection, cut twice on playtest feedback (0.055 -> 0.034 -> 0.020,
        // strawberry: "lower the sensitivity of the joystick", then "lower the default sens for piloting too").
        // The player's own multiplier rides on top, so this stays the DESIGNER's number and 1.00x means it.
        const float HeliStickGainBase = 0.020f;
        static float HeliStickGain => HeliStickGainBase * ControlsOptions.HeliSensitivity;
        const float HeliStickDecay = 8.5f;     // self-centring, per second
        /// <summary>Cross-axis deadzone (strawberry 2026-08-16: "add a little deadzone between forward/back
        /// tilting and left/right tilting"). A mouse never moves on a perfectly straight axis, so a movement
        /// meant as pure pitch always carried a little roll with it and the airframe crabbed. Each axis is
        /// reduced by this fraction of the OTHER one's magnitude, so a mostly-horizontal movement is pure roll
        /// and a mostly-vertical one is pure pitch, while a genuine diagonal still gets through.</summary>
        const float HeliStickCrossDeadzone = 0.4f;
        /// <summary>How fast a held arrow key deflects the cyclic. Slower than a mouse flick on purpose -- a
        /// digital key has no magnitude, so the RAMP is the only thing standing in for how hard you pushed.</summary>
        const float ArrowStickRate = 2.2f;
        /// <summary>Test seam: the current virtual stick (pitch, roll) the pilot is holding.</summary>
        public UnityEngine.Vector2 DebugHeliStick => new UnityEngine.Vector2(_heliStickP, _heliStickR);
        public bool LastHandbrakeInput;
        public System.Action<uint, byte> NetEnterVehicle;  // wired by ClientWorldSession: F near a puppet asks the server for a seat (255 = any free one)
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
        public System.Action<byte, byte, byte, ushort> NetFitAttachment;   // (page,x,y,id) -> Client.SendFitAttachment (server spends the fitted item)
        // (magPage,magX,magY,magId, roundPage,roundX,roundY,roundId, unloading) -> Client.SendMagLoad.
        // Null in pure-direct singleplayer, where the client IS the authority and the local mutation is the
        // whole story. Non-null on a joined client and on the SP/MP loopback, which is where the local-only
        // version broke: the next inventory move echoed the server's untouched magazine back.
        public System.Action<byte, byte, byte, ushort, byte, byte, byte, ushort, bool> NetMagLoad;
        public System.Action<byte, byte, byte> NetConsume;           // (page,x,y) -> Client.SendConsume (server deletes the item; vitals stay client-led until the vitals split)
        /// <summary>(page,x,y,item) -> Client.SendGunState. The client is the ONLY writer of a gun's
        /// ammo/chamber/mag/firemode/attachments, and it used to be the only holder of them too: the server's
        /// copy of all nine fields sat at its constructor default forever, and the owner echo sent that default
        /// back over the top of the real one. Invisible until the grid moves, because a move is a request and
        /// the client repaints from the echo -- which is why "fire it, holster it, take it out again" was fine
        /// and "fire it, then drag it anywhere" handed back a full magazine.</summary>
        public System.Action<byte, byte, byte, SDG.Unturned.Item> NetGunState;
        public System.Action<byte, byte, byte, ushort, bool> NetSetAutoDrink;   // (page,x,y,id,on) -> Client.SendSetAutoDrink
        public System.Action<byte, byte, byte, ushort, byte> NetReloadSwap;   // (page,x,y, spentId,spentAmount) -> Client.SendReload (server spends the fresh mag + returns the spent one)
        public System.Action<byte, byte, byte, byte> NetWearClothing;     // (page,x,y, EItemType slot) -> Client.SendWearClothing (server does the whole swap)
        public System.Action<byte> NetUnwearClothing;                     // (EItemType slot) -> Client.SendUnwearClothing
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
        // SP/MP unify (doors + beds): same shape as the seams above -- wired ONLY by ClientWorldSession, so
        // they stay null in SP/loopback and the direct DoorLogic/BedClaims path is byte-identical. Nothing
        // is applied locally on send; DoorState/BedClaimed carry the server's answer back.
        public System.Action<uint> NetToggleDoor;                    // door NetId -> Client.SendToggleDoor
        public System.Action<uint, bool> NetSetDoorLocked;           // (door NetId, locked) -> Client.SendSetDoorLocked
        public System.Action<uint> NetClaimBed;                      // bed NetId -> Client.SendClaimBed

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
            // The door zone you are standing at names a seat, exactly as it does in singleplayer
            // (EnterVehicle(_focusVehicle, _focusAccess.Seat)); no zone means any free one, driver first.
            byte seat = (_focusAccessValid && _focusAccess.Seat >= 0 && _focusAccess.Seat < 255)
                        ? (byte)_focusAccess.Seat : (byte)255;
            NetEnterVehicle(p.NetId, seat);
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
        /// <summary>Ask the SERVER to spend the exact item object `want`, by locating its grid cell and routing a
        /// consume. Returns false if we are not on the wire (pure SP with no server) or the item is not in the bag.
        ///
        /// Exists because the bag is SERVER-OWNED on every path that matters -- singleplayer runs through the
        /// loopback server -- so a local removal the server never hears about is undone by the next owner echo,
        /// handing the item back. That is the attachment/magazine dupe.</summary>
        /// <summary>Ask the SERVER to spend `want` because it was just fitted to a gun. Distinct from a consume:
        /// the server's consume handler refuses anything inedible and applies food/health effects.</summary>
        public bool RequestFitAttachment(SDG.Unturned.Item want)
        {
            if (NetFitAttachment == null || want == null || Inventory == null) return false;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = Inventory.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i);
                    if (!ReferenceEquals(jar?.item, want)) continue;
                    NetFitAttachment(b, jar.x, jar.y, jar.item.id);
                    return true;
                }
            }
            return false;
        }

        public bool RequestConsumeInstance(SDG.Unturned.Item want)
        {
            if (NetConsume == null || want == null || Inventory == null) return false;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = Inventory.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i);
                    if (!ReferenceEquals(jar?.item, want)) continue;
                    NetConsume(b, jar.x, jar.y);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Is this player's inventory server-owned (MP or the SP loopback)? Then local mutations must be
        /// routed as intents, because the owner echo overwrites anything the server did not do itself.</summary>
        public bool InventoryIsServerOwned => NetConsume != null;   // the wire seams are wired together; consume is the sentinel

        public bool RequestMoveItem(byte page0, byte x0, byte y0, byte page1, byte x1, byte y1, byte rot1)
        {
            if (NetMoveItem == null) return false;
            FlushGunState(force: true);   // the server must own the gun state BEFORE it owns the move
            NetMoveItem(page0, x0, y0, page1, x1, y1, rot1);
            return true;
        }

        /// <summary>MP holster-to-slot (InventoryUI Equip): the server runs the same TryDrag into the
        /// hand slot; the in-hand viewmodel equip stays local at the call site.</summary>
        public bool RequestEquipItem(byte fromPage, byte x, byte y, byte slot)
        {
            if (NetEquipItem == null) return false;
            FlushGunState(force: true);   // the server must own the gun state BEFORE it owns the move
            NetEquipItem(fromPage, x, y, slot);
            return true;
        }

        /// <summary>Toggle autodrink on the item at (page,x,y). Routed through the server when the bag is
        /// server-owned, for the same reason every other grid mutation is: a local-only flip is handed straight
        /// back by the next owner echo. Returns false when there is no wire, so the caller does it locally.</summary>
        public bool RequestSetAutoDrink(byte page, byte x, byte y, ushort id, bool on)
        {
            if (NetSetAutoDrink == null || !InventoryIsServerOwned) return false;
            NetSetAutoDrink(page, x, y, id, on);
            return true;
        }

        /// <summary>MP drop (InventoryUI Drop): the server removes the jar + tosses the world item; the
        /// echo empties the cell and the item puppet renders the drop.</summary>
        public bool RequestDropItem(byte page, byte x, byte y)
        {
            if (NetDropItem == null) return false;
            FlushGunState(force: true);   // the server must own the gun state BEFORE it owns the move
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
                throttle = (Keybinds.Pressed(GameAction.MoveForward) ? 1f : 0f) - (Keybinds.Pressed(GameAction.MoveBack) ? 1f : 0f);
                steer = (Keybinds.Pressed(GameAction.MoveRight) ? 1f : 0f) - (Keybinds.Pressed(GameAction.MoveLeft) ? 1f : 0f);
            }
            LastDriveInput = new UnityEngine.Vector2(steer, throttle);
            LastHandbrakeInput = !UiInputBlocked && Keybinds.Pressed(GameAction.VehicleHandbrake);
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
        const float MinStepHeight = 0.07f;   // below this it is ground noise, not a threshold -- see StepUp
        // If the horizontal motion is blocked at foot level but clear a step higher, raise onto the step; FloorSnapLength then
        // pulls us back down onto it. Reused by both the player and zombies (source has stair/ledge handling in PlayerMovement).
        // Camera-only smoothing for the step (strawberry_cow 2026-08-24: "reads as a slope instead of a
        // teleport"). The BODY still moves instantly -- it has to, because the whole point of the step is to be
        // un-blocked before MoveAndSlide runs this tick, and easing the collider upward over several frames
        // would leave it clipped into the curb for those frames. What the player actually complains about is the
        // VIEW jumping, so the view is what gets eased: the camera keeps the old eye height and catches up.
        //
        // This is the standard stair-smoothing trick for exactly that reason. Physics is unchanged and
        // authoritative; only the thing looking at it lags.
        float _stepSmooth;                 // metres the camera is still BELOW the body after a step
        // CONSTANT RATE, not exponential decay (strawberry 2026-08-27: "a smooth ramp, not a sharp step").
        // Decay is asymptotic -- it never arrives, and worse, its SPEED scales with what is left, so a 5 cm
        // curb and a 50 cm crate both take the same time to settle and the small one reads as sluggish. At a
        // fixed m/s the settle time is proportional to the step, which is what "ramp" actually means.
        const float StepSmoothRate = 4f;   // m/s. 0.5 m clears in 0.125 s, a 5 cm lip in 12 ms.
        float _stepChatterCd;              // see StepUp: suppresses SMALL repeat steps only, never stair risers
        // Test observability. The defect being fixed is invisible from the outside -- a step that lifts 12.5 cm
        // for a 2 cm lip ends up at the same PLACE as one that lifts 2 cm, because FloorSnapLength drags it
        // back down. Only the rise itself distinguishes them, so the rise is what the tests read.
        public int StepUpCount;            // steps actually taken
        public float LastStepRise;         // metres of the last step

        void StepUp(float delta, bool grounded)
        {
            if (_stepChatterCd > 0f) _stepChatterCd -= delta;
            if (!grounded) return;
            Vector3 motion = new Vector3(Velocity.X, 0f, Velocity.Z) * delta;
            if (motion.LengthSquared() < 1e-6f) return;
            if (!TestMove(GlobalTransform, motion)) return;   // not blocked at foot level

            // MEASURE the step, don't search for it.
            //
            // The old code asked TestMove "what is the smallest lift that stops the sweep being blocked", by
            // sampling StepHeight/4. Two things were wrong with that. The sampling was coarse enough that its
            // smallest answer was 0.125 m, which made the `need < MinStepHeight` guard under it unreachable --
            // dead code guarding against a value the sampler could not produce. And the question itself is the
            // wrong one: a capsule has a ROUNDED bottom, so the sweep stops being blocked while the feet are
            // still below the obstacle's top. Lifting by that amount clears the sweep and then fails to mount
            // -- MoveAndSlide pushes back out and FloorSnapLength drops you, which is a jolt with no progress.
            //
            // So: cast down onto the surface AHEAD and rise to meet it. That gives the exact height, needs no
            // search, and answers "is there anything to stand on" and "how high is it" with one query.
            var space = GetWorld3D().DirectSpaceState;
            // Past the obstacle, not under our own feet -- and past it by the SWEEP's reach, not just the
            // capsule's. TestMove reports blocked for the swept motion, so contact happens up to |motion|
            // before the capsule surface touches; a probe offset by the radius alone lands INSIDE the thing
            // that blocked us and reads its top as the landing surface.
            Vector3 ahead = motion.Normalized() * (_capsule.Radius + motion.Length() + 0.05f);
            Vector3 probe = GlobalPosition + ahead;
            var down = PhysicsRayQueryParameters3D.Create(
                probe + Vector3.Up * (StepHeight + 0.05f), probe - Vector3.Up * 0.05f);
            down.CollisionMask = CollisionMask;
            down.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var landing = space.IntersectRay(down);
            if (landing.Count == 0) return;   // nothing to stand on within a step: a ledge, not a stair

            float need = ((Vector3)landing["position"]).Y - GlobalPosition.Y;
            if (need <= MinStepHeight || need > StepHeight) return;

            // A WALKABLE SLOPE IS NOT A STEP -- and the test has to be about SHAPE, not gradient.
            //
            // The obvious test is "is the average gradient steeper than FloorMaxAngle". It cannot work here.
            // FloorMaxAngle is 55 degrees, and the probe reaches ~0.47 m: a 0.30 m curb across that distance
            // is a 33-degree average, comfortably walkable. Every step is a walkable gradient when measured
            // over a step-sized distance, so gradient tells a curb and a ramp apart not at all. (An earlier
            // version of this compared `need` against |motion| * tan(FloorMaxAngle) -- a threshold measured
            // over one tick's travel against a rise measured over the probe distance. Two different baselines,
            // so the comparison was meaningless in whichever direction it happened to fall.)
            //
            // What separates them is the FACE we are walking into. Cast a short ray forward at ankle height:
            // a ramp's face there IS the ramp surface, whose normal is walkable; a curb's is a vertical riser.
            // This is the "check a surface you can name" fix -- the old guard used hit.GetNormal(), the normal
            // of the first thing the capsule TOUCHED, which on a capsule is usually an EDGE (a terrain triangle
            // boundary, the lip of a curb) whose normal is neither the ground nor the riser and reads steep on
            // ground you could have walked. That was the false-trigger source.
            Vector3 ankle = GlobalPosition + Vector3.Up * 0.05f;
            var faceQ = PhysicsRayQueryParameters3D.Create(
                ankle, ankle + motion.Normalized() * (_capsule.Radius + motion.Length() + 0.05f));
            faceQ.CollisionMask = CollisionMask;
            faceQ.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var face = space.IntersectRay(faceQ);
            if (face.Count == 0) return;   // nothing solid at ankle height: an overhang, or already clear
            float faceAngle = Mathf.Acos(Mathf.Clamp(((Vector3)face["normal"]).Dot(Vector3.Up), -1f, 1f));
            if (faceAngle <= FloorMaxAngle) return;   // walkable face: a slope, MoveAndSlide's job

            // And the surface we would land ON has to be one we can stand on -- the face test above only says
            // the thing in our way is a riser, not that its top is level enough to hold us.
            var landNormal = (Vector3)landing["normal"];
            if (Mathf.Acos(Mathf.Clamp(landNormal.Dot(Vector3.Up), -1f, 1f)) > FloorMaxAngle) return;

            // Finally: is the raised body actually free to move? Everything above describes the floor; this is
            // the only thing that rules out an overhang or a wall with a ledge painted on it.
            var raised = new Transform3D(GlobalTransform.Basis, GlobalPosition + Vector3.Up * (need + 0.02f));
            if (TestMove(raised, motion)) return;

            // CHATTER GUARD. Size-gated, not a flat cooldown: a flat one would have to be shorter than the time
            // to cross one stair tread (0.25 m at a 6 m/s sprint is 40 ms) to avoid breaking staircases, which
            // makes it useless. Real risers are 0.15-0.30 m and terrain chatter is small, so only SMALL repeat
            // steps are suppressed and a staircase never sees this.
            if (_stepChatterCd > 0f && need < 2f * MinStepHeight) return;
            _stepChatterCd = 0.1f;

            GlobalPosition += Vector3.Up * need;
            StepUpCount++; LastStepRise = need;
            _stepSmooth = Mathf.Min(StepHeight, _stepSmooth + need);   // clamped: a stair RUN must not stack into the camera sinking through the floor
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
        float _sinceShot;           // seconds since the last shot; drives the infAmmo refill (reset on every Fire)
        const float GunshotRadius = 48f;   // earshot of an unsuppressed shot (AlertTool noise); suppressors would cut it
        bool _reloading;            // reloading -> can't fire; magazine refills when the timer elapses
        double _reloadTimer;
        bool _unloading;            // shotgun UNLOAD state (master): plays the reload anim + ejects shells to the bag -- pump one-by-one, break-action all at once
        double _magSwapAnimTimer;   // mag-pie swap (LoadMagInstance / RemoveMagazine) plays the reload anim, but the ammo swap is INSTANT. Ticks the anim's length then calls SetReloading(false); without it, SetReloading(true) had no matching (false) so the gun stayed "reloading" forever -> ADS permanently blocked (master's "lose the ability to ADS after the pie" bug)
        bool _magSwapAutoRack;      // set when a mag is seated into an EMPTY chamber -> when the swap anim ends, play the rack (Hammer) anim to auto-chamber the first round (master: "auto rack")
        double _unloadTimer;
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
        bool _chambered;            // a round sits in the chamber: an EXPLICIT persistent state (master) replacing the old HasChamber&&Ammo>0 inference -- the rack fills it, remove-mag keeps it, the pie's rack ejects it
        string _chamberedAmmoType;  // the bullet TYPE of the loaded rounds / chamber (FMJ/AP/HP), from the loaded mag; persisted (master) -- opens the door for AP/HP/FMJ loads
        SDG.Unturned.Item _heldItem;   // the inventory/world Item backing the held gun -> where its ammo/firemode/mag PERSIST (master)
        // Mirror the held gun's live state onto its backing item so it survives hands<->inventory<->drop (source: equipment.state).
        void SaveGunState() { if (_heldItem != null && Gun != null) { _heldItem.gunAmmo = Ammo; _heldItem.gunChambered = _chambered; _heldItem.gunChamberedType = _chamberedAmmoType; _heldItem.gunFiremode = (int)_firemode; _heldItem.gunMagId = _loadedMagId; if (_viewmodel != null && _viewmodel.IsGunViewmodel) _heldItem.gunAttach = _viewmodel.GetAttachMask(); MarkGunStateDirty(); } }   // only save the attach mask from the GUN's own viewmodel -- a consumable/fists viewmodel returns 0 and would wipe the gun's attachments (strawberry)
        // Telling the server what the client just wrote. COALESCED, not sent per SaveGunState: SaveGunState runs
        // on every shot, and one reliable-ordered datagram per shot is exactly the head-of-line stutter v10 was
        // written to remove. A 0.25s floor bounds a firefight to 4 sends/sec, and every point where the state is
        // about to leave the client's hands -- any grid mutation request -- forces an immediate flush so the
        // move cannot race it. The item AND its address are captured at save time, not read at flush time:
        // holstering clears _heldItem, and the flush that matters most is the one right after that.
        const double GunStateFlushEvery = 0.25;
        bool _gunStateDirty;
        double _gunStateFlushCd;
        int _gunStatePage = -1; byte _gunStateX, _gunStateY;
        SDG.Unturned.Item _gunStateItem;

        /// <summary>Where the held item actually IS, found by object identity rather than read off _heldPage.
        ///
        /// _heldPage is not usable here. EquipFromLocation calls NoteHeldFrom(newPage,newX,newY) BEFORE
        /// EquipItemAsset, and EquipHeldGun's first act is SaveGunState() on the OUTGOING gun -- so at the moment
        /// the outgoing gun's state is saved, _heldPage already names the INCOMING gun's cell. Pairing the two
        /// would flush one gun's magazine onto another gun's address; with two identical rifles the server's id
        /// check would not even catch it. The grid is seven small pages, and this runs on a save, not a frame.</summary>
        bool TryFindItemAddress(SDG.Unturned.Item item, out int page, out byte x, out byte y)
        {
            page = -1; x = y = 0;
            if (item == null || Inventory == null) return false;
            for (int p = 0; p < Inventory.items.Length && p < PlayerInventory.PAGES; p++)
            {
                var pg = Inventory.items[p];
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i);
                    if (jar != null && ReferenceEquals(jar.item, item)) { page = p; x = jar.x; y = jar.y; return true; }
                }
            }
            return false;
        }

        void MarkGunStateDirty()
        {
            if (_heldItem == null) return;
            if (!TryFindItemAddress(_heldItem, out int page, out byte x, out byte y)) return;   // not in the grid (a world pickup in flight) -> no address to name
            // ONE pending slot, so a save for a DIFFERENT gun must push the current one out first or it is simply
            // lost. That is not a rare race: swapping weapons saves the outgoing gun and then, a few ticks later,
            // the incoming one -- inside the 0.25s coalescing window -- so the outgoing gun's magazine would be
            // dropped every single time you switched weapons, which is exactly when you have just spent it.
            // Coalescing is still doing its job: repeated saves for the SAME gun (every shot) collapse.
            if (_gunStateDirty && !ReferenceEquals(_gunStateItem, _heldItem)) FlushGunState(force: true);
            _gunStateItem = _heldItem; _gunStatePage = page; _gunStateX = x; _gunStateY = y;
            _gunStateDirty = true;
        }

        /// <summary>Push the pending gun state to the server. force skips the rate floor -- use it wherever a
        /// grid mutation is about to be requested, so the server applies the state BEFORE it moves the item.</summary>
        public void FlushGunState(bool force = false)
        {
            if (!_gunStateDirty || NetGunState == null || !InventoryIsServerOwned) return;
            if (!force && _gunStateFlushCd > 0) return;
            if (_gunStateItem == null || _gunStatePage < 0 || _gunStatePage >= PlayerInventory.PAGES) { _gunStateDirty = false; return; }
            NetGunState((byte)_gunStatePage, _gunStateX, _gunStateY, _gunStateItem);
            _gunStateDirty = false;
            _gunStateFlushCd = GunStateFlushEvery;
        }

        void TickGunStateFlush(double delta)
        {
            if (_gunStateFlushCd > 0) _gunStateFlushCd -= delta;
            FlushGunState();
        }

        public bool DebugGunStatePending => _gunStateDirty;   // test seam: did a save actually queue a send

        void RestoreGunState(SDG.Unturned.Item item)
        {
            if (item == null || item.gunAmmo < 0) return;   // a fresh gun with no saved state keeps its LoadGun defaults
            Ammo = item.gunAmmo;
            _chambered = item.gunChambered;
            _chamberedAmmoType = item.gunChamberedType;
            if (item.gunFiremode >= 0 && System.Enum.IsDefined(typeof(FireMode), item.gunFiremode)) _firemode = (FireMode)item.gunFiremode;
            if (item.gunMagId >= 0) _loadedMagId = item.gunMagId;
        }

        // Working magazines (increment 1: the Military STANAG). A gun uses mag ITEMS when its default Magazine is a
        // registered magazine (else the old whole-mag reload). A mag fits when its caliber matches the gun's.
        bool UsesMagItem => Gun != null && !Gun.ShellReload && (SDG.Unturned.Assets.find((ushort)Gun.MagazineId)?.IsMagazine ?? false);
        // +1 round in the chamber: a non-shell gun keeps its chambered round through a reload -> capacity is AmmoMax+1. Reloaded
        // from EMPTY (Ammo 0) it has to RACK a round out of the fresh mag (the Hammer clip) and tops out at AmmoMax (no bonus).
        // +1 chamber -- the rule lives on GunDef so the reload, the HUD and the tests all read ONE answer. Notably a
        // PUMP now gets it (ghost loading) where the old `!IsShotgun` test refused it, and a REVOLVER does not,
        // where the old test allowed it and gave the Ace 6+1. See GunDef.HasChamberRound.
        bool HasChamber => Gun?.HasChamberRound ?? false;
        // A gun's magazine capacity for a GIVEN mag. Normally the gun's Ammo_Max caps it -- a reload draws Min(mag, AmmoMax),
        // so a 12-round .45 mag in a 7-round 1911 still loads 7 (master's .45/.50 niche). A "reservoir" mag (the 100-round
        // Military Drum, magOverridesCapacity) OVERRIDES that: its own magCapacity becomes the gun's capacity, so the drum
        // actually holds 100 in the gun (master: "the 100 rounds should apply to the actual gun").
        int CapForMag(SDG.Unturned.ItemAsset a) => (a != null && a.magOverridesCapacity && a.magCapacity > 0) ? a.magCapacity : (Gun?.AmmoMax ?? 30);
        int LoadedMagCap => CapForMag(_loadedMagId > 0 ? SDG.Unturned.Assets.find((ushort)_loadedMagId) : null);   // capacity of the mag currently loaded
        public int MagCapacity => Gun != null ? LoadedMagCap : Ammo;   // HUD denominator: the loaded mag's capacity (drum = 100), gun-null falls back like the old Gun?.AmmoMax ?? Ammo
        int ChamberedCap => LoadedMagCap + (HasChamber ? 1 : 0);   // absolute max Ammo = a full (currently-loaded) mag plus the one in the chamber

        // infAmmo: refill the held gun once firing has been idle for InfAmmoIdle. Fills to ChamberedCap rather than
        // mirroring the reload's from-empty rule ((HasChamber && Ammo > 0) ? max+1 : max) on purpose -- that rule
        // depends on the CURRENT count, so a gun refilled from empty would land on max this tick and then creep to
        // max+1 on the next one, which reads as a bug. One fixed number every time is the honest cheat.
        // Nothing is consumed: this does not touch magazine items or loose shells, so it cannot quietly drain a bag.
        void TickInfiniteAmmo()
        {
            if (!InfiniteAmmo || Gun == null || !HasGunOut) return;
            if (_reloading || _sinceShot < InfAmmoIdle) return;   // never fights a reload already in flight
            if (Ammo >= ChamberedCap) return;
            Ammo = ChamberedCap;
            _chambered = HasChamber;
        }

        public bool DebugInfAmmoWouldFill => InfiniteAmmo && Gun != null && HasGunOut && !_reloading && Ammo < ChamberedCap;   // test seam
        public float DebugSinceShot => _sinceShot;
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
            int loaded = System.Math.Min(fresh.amount, CapForMag(SDG.Unturned.Assets.find(fresh.id)));    // rounds off the fresh mag (the drum overrides Ammo_Max; a normal mag is capped by it)
            byte returned = (byte)System.Math.Max(0, oldAmmo - (chambered ? 1 : 0));      // the outgoing mag MINUS the chambered round (it stayed in the gun)
            // SERVER-OWNED BAG: the swap is an INTENT. This whole block used to be a local removeItem +
            // tryAddItem, which the server never heard about -- so the next owner echo put the spare magazine
            // back at FULL and destroyed the partially-spent one that had been returned. One spare reloaded
            // forever. The client still moves its own Ammo (that is gun state, which no snapshot carries); only
            // the GRID edit goes to the server, and the echo brings the real bag back. Review 2026-08-16.
            var freshJar = Inventory.items[fb].getItem(fi);
            if (InventoryIsServerOwned && NetReloadSwap != null)
                NetReloadSwap(fb, freshJar.x, freshJar.y, (ushort)System.Math.Max(0, _loadedMagId), returned);
            else
            {
                Inventory.items[fb].removeItem(fi);                                      // take the fresh mag out of the bag
                Inventory?.tryAddItem(new Item((ushort)_loadedMagId, returned));         // old mag back
            }
            Ammo = loaded + (chambered ? 1 : 0);                                         // +1: the already-chambered round stays on top of the fresh mag
            _loadedMagId = fresh.id;
            // chamber type is independent of the mag (master): a tactical swap keeps the chambered round's type; a
            // reload from EMPTY chambers a fresh round from the new mag -> takes its type. (_chambered is set by the caller.)
            if (!chambered) _chamberedAmmoType = (HasChamber && Ammo > 0) ? SDG.Unturned.Assets.find(fresh.id)?.ammoType : null;
        }

        // Loose ammo (shotgun shells, master: real ammo types). A gun uses shells when a stackable isAmmo item matches its
        // caliber (12ga=113 -> caliber 8; 20ga=381 -> caliber 16). Reload CONSUMES shells from the stack (vs swapping a mag).
        // A GUN FEEDS LOOSE ROUNDS WHEN ITS OWN DECLARED MAGAZINE IS AMMO -- not merely when some item somewhere
        // shares its caliber. ShellAsset scans every asset for `isAmmo && magCaliber == Caliber`, so without this
        // gate, registering ANY loose round at a caliber silently converts every gun of that caliber to shell-fed
        // and bypasses the magazine path entirely. cow tools hit exactly that flipping item 5004 (5.56, caliber 1)
        // to isAmmo on my bad advice: the eaglefire, maplestrike and honeybadger all resolved it as their shell and
        // gun.drum_mag_capacity / gun.swap_mid_reload / gun.caliber_field broke. They worked around it with a
        // private caliber group; this removes the trap instead.
        //
        // NOT gated on ShellReload, which was the obvious-looking fix and is wrong: the ACE is deliberately
        // UsesShells with ShellReload FALSE (master: "reload from loose ammo ... but not one round at a time"),
        // so that gate would break the very feature this is here to serve.
        //
        // The caliber scan below still chooses WHICH loose round, so shotgun buckshot/slug/beanbag switching is
        // untouched -- this only decides IF the gun feeds loose at all.
        bool FeedsLooseRounds => Gun != null && SDG.Unturned.Assets.find((ushort)Gun.MagazineId)?.isAmmo == true;
        bool UsesShells => Gun != null && Gun.Caliber > 0 && FeedsLooseRounds && ShellAsset != null;
        // Shotgun ammo-TYPE selection (buckshot vs slug). A gauge can carry several loose-shell types (12ga: buckshot
        // 113 + slug 5000, same caliber 8); the player picks one via the R-hold radial or the attachment menu's
        // Magazine slot. Keyed by caliber so all guns of that gauge share the choice. 0 = unset -> the default
        // (first-registered match = buckshot), preserving the pre-feature behaviour.
        readonly System.Collections.Generic.Dictionary<int, ushort> _selectedShellByCaliber = new();
        ushort SelectedShellId
        {
            get => Gun != null && _selectedShellByCaliber.TryGetValue(Gun.Caliber, out var id) ? id : (ushort)0;
            set { if (Gun != null) _selectedShellByCaliber[Gun.Caliber] = value; }
        }
        SDG.Unturned.ItemAsset ShellAsset   // the loaded shell: the player's SELECTED type for this gauge, else the first registered caliber match (buckshot)
        {
            get
            {
                if (Gun == null) return null;
                ushort sel = SelectedShellId;
                if (sel != 0) { var s = SDG.Unturned.Assets.find(sel); if (s != null && s.isAmmo && s.magCaliber == Gun.Caliber) return s; }
                foreach (var a in SDG.Unturned.Assets.all()) if (a.isAmmo && a.magCaliber == Gun.Caliber) return a;
                return null;
            }
        }
        bool ShellMatches(SDG.Unturned.ItemAsset a)   // is this bag item a shell the gun loads right now? the selected type if one is chosen, else any of the gun's caliber
        {
            if (a == null || !a.isAmmo || Gun == null || a.magCaliber != Gun.Caliber) return false;
            ushort sel = SelectedShellId;
            return sel == 0 || a.id == sel;
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
                    if (ShellMatches(a)) n += jar.item.amount;
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
                    if (!ShellMatches(a)) continue;
                    int t = System.Math.Min(want - taken, jar.item.amount);
                    jar.item.amount = (byte)(jar.item.amount - t); taken += t;
                    if (jar.item.amount <= 0) pg.removeItem((byte)i);   // empty shell stack -> free the slot
                }
            }
            return taken;
        }
        // --- shotgun ammo-type picker API (R-hold radial + attachment menu Magazine slot) ---
        public bool CanChooseShellType => UsesShells;   // only loose-shell shotguns have an ammo type to pick
        public string LoadedShellName => UsesShells ? ShellAsset?.itemName : null;   // HUD: the shell type currently loaded (e.g. "12 Gauge Slug"), null for non-shotguns
        public static string PluralAmmo(string name, int count) => count > 1 && !string.IsNullOrEmpty(name) ? name + "s" : name;   // display only: "12 Gauge Slug" -> "12 Gauge Slugs" when >1 (master)

        // --- magazine-fed gun mag pie (master: R-hold on a STANAG/mag rifle) -------------------------------------------
        public bool CanChooseMag => UsesMagItem;   // mag-fed guns get the mag pie: spare mags + remove + rack
        public bool HasChamberedRound => HasChamber && Ammo > 0;   // mag pie: is there a round to rack out
        public bool HasMagLoaded => UsesMagItem && _loadedMagId > 0;   // mag pie: is there a mag to remove
        public bool CanOpenAmmoPie => CanChooseShellType || CanChooseMag;   // R-hold opens the pie for loose-shell shotguns OR mag guns
        public string LoadedAmmoType => string.IsNullOrEmpty(_chamberedAmmoType) ? "FMJ" : _chamberedAmmoType;   // the loaded/chambered bullet type (default FMJ) -- for HUD/pie display
        public bool GunHasChamber => HasChamber;   // does this gun have a chamber (mag guns / pumps) -> the HUD shows the mag + chamber split
        public int ChamberedRounds => (HasChamber && _chambered && Ammo > 0) ? 1 : 0;   // 0 or 1: for the HUD "mag +N / max" readout (master: ALWAYS show +1 when chambered, +0 when not)
        string MagAmmoType => _loadedMagId > 0 ? SDG.Unturned.Assets.find((ushort)_loadedMagId)?.ammoType : null;   // the currently-loaded mag's bullet type -- what a freshly-cycled round (fire / rack / reload-from-empty) becomes
        // Each spare magazine in the bag that fits the gun, per-INSTANCE (a 30/30 and a 12/30 are separate wedges).
        public System.Collections.Generic.List<(SDG.Unturned.ItemAsset asset, SDG.Unturned.Item item, byte page, byte idx)> SpareMags()
        {
            if (!UsesMagItem) return new();
            var all = AttachmentFit.InBagInstances(Inventory, "Magazine", Gun.Caliber);
            // exclude mags that FIT the gun (same caliber GROUP) but hold the WRONG round -- a .300 BLK mag for a 5.56
            // gun, or vice versa (master). magRound distinguishes them within a shared STANAG group.
            string round = Gun.CaliberName;
            if (!string.IsNullOrEmpty(round))
                all.RemoveAll(m => !string.IsNullOrEmpty(m.Asset.magRound) && m.Asset.magRound != round);
            return all;
        }
        // Swap in a SPECIFIC spare mag instance (a mag pie wedge), returning the current mag to the bag with its rounds;
        // the chambered round stays. Like DoMagSwap but for the chosen instance, not the fullest.
        public void LoadMagInstance(SDG.Unturned.Item mag)
        {
            if (mag == null || _reloading || _unloading || _dead || !UsesMagItem || Inventory == null || _magSwapAnimTimer > 0) return;   // cooldown: wait for any in-flight mag/rack/reload anim (master)
            bool removed = false;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2) && !removed; b++)
            { var pg = Inventory.items[b]; for (byte i = 0; i < pg.getItemCount(); i++) if (ReferenceEquals(pg.getItem(i)?.item, mag)) { pg.removeItem(i); removed = true; break; } }
            if (!removed) return;   // the exact mag vanished from under us -> do nothing
            bool chambered = HasChamber && Ammo > 0;
            int oldMag = Ammo - (chambered ? 1 : 0);
            if (_loadedMagId > 0) Inventory.tryAddItem(new SDG.Unturned.Item((ushort)_loadedMagId, (byte)System.Math.Max(0, oldMag)));
            Ammo = System.Math.Min(mag.amount, CapForMag(SDG.Unturned.Assets.find(mag.id))) + (chambered ? 1 : 0);
            _loadedMagId = mag.id;
            _chambered = HasChamber && Ammo > 0;
            // the chamber tracks its OWN round's type, independent of the mag (master): a tactical swap (a round was
            // already chambered) KEEPS that round + its type; only a reload from EMPTY chambers a fresh round from the
            // new mag, so the chamber takes the new mag's type then.
            if (!chambered) _chamberedAmmoType = _chambered ? SDG.Unturned.Assets.find(mag.id)?.ammoType : null;
            float sp = Skills.DexterityReloadSpeed();
            _viewmodel?.SetReloading(true, sp);   // play the swap anim (the instant swap already happened)...
            _magSwapAnimTimer = (_viewmodel?.ReloadLength ?? ReloadTime) / System.Math.Max(0.01f, sp);   // ...clear it when the anim ends so ADS/fire un-block (master's ADS bug)
            _magSwapAutoRack = !chambered && HasChamber && Ammo > 0;   // seated into an EMPTY chamber -> auto-rack the first round when the anim ends (master)
            SaveGunState();
        }
        // Remove the loaded magazine to the bag WITH its rounds, LEAVING the chambered round (master); mag-out anim.
        public void RemoveMagazine()
        {
            if (_reloading || _unloading || _dead || !UsesMagItem || _loadedMagId <= 0 || _magSwapAnimTimer > 0) return;   // cooldown (master)
            bool chambered = HasChamber && Ammo > 0;
            int inMag = Ammo - (chambered ? 1 : 0);
            var mag = new SDG.Unturned.Item((ushort)_loadedMagId, (byte)System.Math.Max(0, inMag));
            if (!(Inventory?.tryAddItem(mag) ?? false)) DropWorldItem(mag, GlobalPosition + Vector3.Up);
            Ammo = chambered ? 1 : 0;   // only the chambered round remains
            _chambered = chambered;
            _loadedMagId = 0;
            float sp = Skills.DexterityReloadSpeed();
            _viewmodel?.SetReloading(true, sp);
            _magSwapAnimTimer = (_viewmodel?.ReloadLength ?? ReloadTime) / System.Math.Max(0.01f, sp);   // clear the mag-out anim state when it ends so ADS/fire un-block (master)
            SaveGunState();
        }
        // Rack the gun: eject the chambered round as a 5.56 FMJ (5004) to the bag/ground, re-chamber from the mag; rack anim.
        public void RackGun()
        {
            if (_reloading || _unloading || _dead || !UsesMagItem || _magSwapAnimTimer > 0) return;   // cooldown (master)
            if (!(HasChamber && Ammo > 0)) return;   // nothing chambered
            var round = new SDG.Unturned.Item(5004, 1);   // eject the CHAMBERED round. Only FMJ (5004) has a loose-round item today; AP/HP round items map from _chamberedAmmoType here once they exist (the chamber's TYPE is tracked, so that item mapping is the only gap)
            if (!(Inventory?.tryAddItem(round) ?? false)) DropWorldItem(round, GlobalPosition + Vector3.Up);
            Ammo--;   // eject the chambered round; the next mag round auto-chambers
            _chambered = HasChamber && Ammo > 0;
            _chamberedAmmoType = _chambered ? MagAmmoType : null;   // the re-chambered round comes from the mag -> takes the mag's type (master)
            float sp = Skills.DexterityReloadSpeed();
            _viewmodel?.PlayHammer(sp);   // the rack animation
            _magSwapAnimTimer = (_viewmodel?.HammerLength ?? 0.4) / System.Math.Max(0.01f, sp);   // cooldown: block other mag actions until the rack anim finishes (master)
            SaveGunState();
        }
        int CountOfShell(ushort id)   // how many of ONE specific shell id the player carries across pages
        {
            if (Inventory == null || id == 0) return 0;
            int n = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = Inventory.items[b];
                for (byte i = 0; i < pg.getItemCount(); i++)
                { var jar = pg.getItem(i); if (jar?.item != null && jar.item.id == id) n += jar.item.amount; }
            }
            return n;
        }
        // The loose-shell types the player CARRIES that fit the gun's caliber, with count + which is selected. Drives
        // the radial pie + the attachment menu -- ONLY carried types get a segment (master), so an empty type shows nothing.
        public System.Collections.Generic.List<(SDG.Unturned.ItemAsset asset, int count, bool selected)> ShellTypeChoices()
        {
            var list = new System.Collections.Generic.List<(SDG.Unturned.ItemAsset, int, bool)>();
            if (Gun == null || Gun.Caliber <= 0) return list;
            ushort sel = SelectedShellId; if (sel == 0) { var d = ShellAsset; if (d != null) sel = (ushort)d.id; }
            foreach (var a in SDG.Unturned.Assets.all())
                if (a.isAmmo && a.magCaliber == Gun.Caliber)
                {
                    int cnt = CountOfShell((ushort)a.id);
                    if (cnt > 0) list.Add((a, cnt, (ushort)a.id == sel));   // carried only -- an empty type gets no segment
                }
            return list;
        }
        // Pick a shell type + reload into it (the whole point of the radial/menu). No-op unless it's a real shell of this
        // gun's caliber that the player actually carries. Pellets follow automatically (ShellAsset -> the fire loop).
        public void ChooseShellType(ushort id)
        {
            if (!UsesShells) return;
            var a = SDG.Unturned.Assets.find(id);
            if (a == null || !a.isAmmo || a.magCaliber != Gun.Caliber || CountOfShell(id) <= 0) return;
            // already loaded with THIS type and the tube is full -> there's nothing to load; don't replay the reload anim
            // for nothing (master, shotguns). picking a DIFFERENT type still falls through to the switch below.
            if (ShellAsset != null && (ushort)ShellAsset.id == id && Ammo >= (Gun?.AmmoMax ?? 0)) return;
            // switching to a DIFFERENT shell type while rounds are still loaded: eject the loaded (old-type) rounds back to
            // the bag FIRST. the loaded type is a single global (ShellAsset), so without this the tube's existing rounds get
            // silently reinterpreted as the new type -- a full buckshot tube would read as a full SLUG tube on switch, then
            // top up: the "1 slug -> full mag of slugs" dupe (master). ejecting first means the reload only loads shells you
            // truly carry, and the old rounds return to the bag as themselves rather than being converted.
            var loaded = ShellAsset;
            if (loaded != null && (ushort)loaded.id != id && Ammo > 0)
            {
                var back = new SDG.Unturned.Item((ushort)loaded.id, (byte)Ammo);
                if (!(Inventory?.tryAddItem(back) ?? false)) DropWorldItem(back, GlobalPosition + Vector3.Up);
                Ammo = 0;
            }
            SelectedShellId = id;
            StartReload();   // source: picking ammo IS a reload
        }
        public bool HasLoadedShells => UsesShells && Ammo > 0;   // radial: is there anything to unload?
        // Begin an animated UNLOAD (master: "trigger a new unload state, plays reload anim, shell count lowers 1 by 1
        // like reloading adds"). The per-tick ejection in _Process mirrors the RELOAD: a pump ejects one shell per
        // interval, a break-action (masterkey / quadbarrel) ejects all barrels at once. Rounds go back as their real
        // loaded type, so unloading never loses or converts ammo. Shotguns only.
        public void UnloadShells()
        {
            if (_reloading || _unloading || _dead || _needsRechamber || _rechambering || _magSwapAnimTimer > 0) return;   // cooldown (master)
            if (!UsesShells || Ammo <= 0) return;
            _unloading = true;
            float rspeed = Skills.DexterityReloadSpeed();
            _viewmodel?.SetReloading(true, rspeed);   // reuse the reload animation
            double full = (_viewmodel?.ReloadLength ?? ReloadTime) / rspeed;
            _unloadTimer = Gun?.ShellReload == true ? full / System.Math.Max(1, Ammo) : full;   // pump: per-shell; break: whole duration then eject all
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
            _lightScanCd = 5;   // PERF: 10 Hz (was ~17 Hz); each scan marshals the whole dynlight group
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

        // (a door has no ToggleFocusedDoor helper any more: F on a door starts a hold, so the TAP fires from
        // the key-release handler against the door the hold began on, not against whatever is focused now)
        void ClaimFocusedBed() => RequestClaimBed(_focusBed);

        /// <summary>Open/close a door as this player. Public for the same reason the other Request*
        /// helpers are: look-focus needs a captured mouse, which a headless test cannot have, so the F
        /// path and the tests drive ONE seam rather than the tests re-implementing the rule.
        /// A refusal is told to the player -- silence reads as a broken door.</summary>
        public bool RequestToggleDoor(Door d)
        {
            if (d == null || !IsInstanceValid(d)) return false;
            // A REPLICATED door (NetId != 0) is the server's to swing: send the intent and wait for the
            // DoorState echo. Swinging it locally first would look better exactly until the server
            // refused, and a door that swings back is worse than one that opens late. SP doors (NetId 0)
            // take the direct path below -- the same DoorLogic call either way.
            if (d.NetId != 0 && NetToggleDoor != null) { NetToggleDoor(d.NetId); return true; }
            if (d.TryToggle(PlayerId, GroupId, _interactClock)) return true;
            string why = d.LastRefusal switch
            {
                SDG.Unturned.DoorRefusal.Locked => "locked",
                SDG.Unturned.DoorRefusal.Obstructed => "something is in the way",
                SDG.Unturned.DoorRefusal.Cooldown => null,   // still swinging: saying so every frame would be noise
                _ => null,
            };
            if (why != null) FluidPickupHudSet($"the door is {why}");   // reuse the existing centre-screen line
            return false;
        }

        /// <summary>Open or close a prop door (ObjectDoor) as this player. Simpler than RequestToggleDoor: no
        /// SP/MP net branch (SP-local MVP, no NetId) and no lock/refusal messaging -- the ObjectDoor cooldown
        /// is the only thing that can refuse a tap, and refusing silently (no HUD line) is fine for a slow
        /// appliance door mid-swing.</summary>
        public bool RequestToggleObjectDoor(ObjectDoor d)
        {
            if (d == null || !IsInstanceValid(d)) return false;
            return d.Toggle();
        }

        // A placed door is BARRICADED if a window-barricade panel fills EITHER face of its opening -> it won't open
        // (F says "Door is barricaded") and its look-outline goes red. Only doored openings on a live WallSurface
        // qualify; a standalone deployable door (parented to the world, not a wall) never is. (master 2026-09-01)
        bool ObjectDoorBarricaded(ObjectDoor d)
        {
            if (d == null || !IsInstanceValid(d) || d.GetParent() is not Node3D host) return false;
            if (host.GetParent() is not WallSurface w) return false;
            int oi = w.OpeningIndexForDoorHost(host);
            return oi >= 0 && (BarricadePlacer.SlotFilled(w, oi, 1) || BarricadePlacer.SlotFilled(w, oi, -1));
        }

        /// <summary>Claim a bed as this player's respawn point, releasing whichever they held.</summary>
        public bool RequestClaimBed(Bed b)
        {
            if (b == null || !IsInstanceValid(b)) return false;
            // Replicated bed: the server owns who sleeps where (it also has to release whichever bed this
            // player held, which only it can see). The BedClaimed echo paints the ownership.
            if (b.NetId != 0 && NetClaimBed != null) { NetClaimBed(b.NetId); return true; }
            if (b.TryClaim(PlayerId, _interactClock)) { FluidPickupHudSet("you will respawn here"); return true; }
            if (b.Owner != 0UL && b.Owner != PlayerId) FluidPickupHudSet("someone else's bed");
            return false;
        }

        void Die()
        {
            _dead = true;
            _deathTimer = 3.5;
            if (!NetAvatar) MusicPlayer.Get(this)?.Sting(GameAudio.Clip("music", Terrain.MapDir?.ToLowerInvariant() + "_outro") != null ? Terrain.MapDir.ToLowerInvariant() + "_outro" : "death");   // retail: the map's outro on death, death.ogg where a map has none
            _burstLeft = 0;   // death cancels any in-progress burst (no resume after respawn)
            if (_wiring) CancelWire();   // death drops any in-progress wire (no stale preview / death-cam nodes)
            ClearFisher();               // death reels in any deployed line (no stale bobber/line surviving into respawn)
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
        /// <summary>Lift a vehicle-exit spot out of the ground it may have landed in.
        ///
        /// The raw spot is "2.4 m off the driver's side, 1 m up", which lands INSIDE the hill whenever the vehicle
        /// is parked across a slope with the rise on that side -- and the same block re-enables the player's
        /// collision shapes, so the body comes back interpenetrating. Both MP exit paths already clamp
        /// (ClientWorldSession samples the terrain, and the server has AdjustExitSpot); the two SP paths used the
        /// raw spot. Review 2026-08-16.
        ///
        /// Swept with a ray rather than a terrain height sample -- PlayerController has no Terrain reference, and
        /// a ray also catches the floor you are standing on inside a building, which a heightmap lookup cannot.</summary>
        Vector3 ClampExitSpot(Vector3 spot)
        {
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return spot;
            var q = PhysicsRayQueryParameters3D.Create(spot + Vector3.Up * 2f, spot - Vector3.Up * 3f, (1u << 0) | (1u << 4) | (1u << 5));
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return spot;
            float groundY = hit["position"].AsVector3().Y;
            return spot.Y < groundY + 0.1f ? new Vector3(spot.X, groundY + 0.5f, spot.Z) : spot;
        }

        void EjectFromVehicleOnDeath()
        {
            if (_driving == null && _riding == null) return;
            var v = _driving; _driving = null; _riding = null;
            // FREE THE SEAT. OccupiedSeats was only ever released by TrySwitchSeat and ExitVehicle, so dying in a
            // seat leaked it FOREVER: a single-seat vehicle (tractor, minicopter, skycrane, semi, tank) became
            // silently un-enterable for the rest of its life -- EnterVehicle's scan walks off the end, hits
            // _seatIndex >= SeatCount and returns with _driving null, no message, no prompt change. Dying in seat
            // 0 of a multi-seat vehicle lost the DRIVER's seat, so everyone after boarded as a passenger and
            // TrySwitchSeat(0) was refused: a jeep nobody can steer. The vehicle-explosion path never showed it
            // because DriveVehicle calls ExitVehicle() (which frees the seat) BEFORE applying the damage.
            // Review 2026-08-16.
            // NO Park (strawberry_cow 2026-08-24: "when exiting a vehicle, keep its momentum, dont apply any
            // brakes"). Leaving a moving car now leaves it MOVING -- it coasts, rolls downhill, and keeps
            // whatever the driver gave it. The engine is likewise untouched. Bailing out of a rolling truck is a
            // thing you can do to yourself on purpose now.
            if (v != null) { v.OccupiedSeats.Remove(_seatIndex); GlobalPosition = ClampExitSpot(v.GlobalPosition + v.GlobalTransform.Basis.X * 2.4f + Vector3.Up * 1.0f); }
            _seatIndex = 0;
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
            // A claimed bed IS your spawn -- that is the whole point of claiming one. No bed (or it was
            // destroyed while you were dead) falls back to the map spawn.
            if (reposition)
            {
                // P3a: the client-auth MP shell skips this -- the server's recov teleport owns the move to
                // SpawnPos (a GlobalPosition write would be overwritten by the next state claim).
                Vector3 target = Bed.TryGetSpawn(PlayerId, out var bedSpawn, out _) ? bedSpawn + Vector3.Up * 0.5f : PickRandomSpawn();   // no claimed bed -> a fresh RANDOM map spawn (strawberry 2026-08-23), not the fixed initial point
                GlobalPosition = target;
                // ...and reset the render-interp snapshots, for the reason TeleportTo documents: the next
                // 50 Hz tick restores GlobalPosition from _interpCurr, which still holds the pre-death spot,
                // so a bare write here is silently undone. Latent all along (respawning at the map spawn
                // from far away had the same bug); claiming a bed 47 m away is what finally showed it.
                _interpPrev = _interpCurr = target;
            }
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
            AutoDrinkTick(dt);   // passively sip a SAFE bottle to top up hydration BEFORE the drain/death check (strawberry)
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

        // AUTODRINK (strawberry): while hydration sits below the floor, passively sip 50 mL from a bag bottle whose
        // autodrink is ON and whose contents are SAFE (clean water / a beverage — never anything that'd make you sick).
        // No animation, no equip needed. Drains one bottle to empty before the next (first-found in a stable scan order).
        // A modest cooldown keeps it a steady top-up instead of frame-rate-fast draining. SP-local for now (MP routing =
        // fast-follow with the rest of fluid); only observable once thirst is actually dropping (survival drain ON).
        public const float AutoDrinkFloor = 0.5f;      // keep hydration at/above this
        const float AutoDrinkInterval = 0.7f;          // min seconds between auto-sips
        float _autoDrinkCd;
        void AutoDrinkTick(float dt)
        {
            if (_dead || Inventory == null) return;
            _autoDrinkCd -= dt;
            if (Water >= AutoDrinkFloor || _autoDrinkCd > 0f) return;
            // ONE active bottle at a time = the first ENABLED, safe, non-empty container; empties -> the next takes over,
            // a disabled bottle is skipped (strawberry). Same rule the inv icon marks, so drink + icon always agree.
            var active = FluidItem.ActiveAutoDrink(Inventory);
            if (active == null) return;
            FluidItem.Read(active, active.GetAsset(), out var t, out var amt, out var q);
            float sip = Mathf.Min(FluidItem.SipML, amt);
            FluidItem.Write(active, t, amt - sip, q);
            Water = Mathf.Min(1f, Water + sip * FluidItem.HydrationPerML);
            _autoDrinkCd = AutoDrinkInterval;
            _invUI?.Refresh();
        }
        // test seam: drive one autodrink evaluation from a headless test
        public void DebugAutoDrinkTick(float dt) => AutoDrinkTick(dt);

        // FOOD SPOILAGE (strawberry): once per in-game day (DayNightCycle.Day advancing over a midnight crossing) every
        // FOOD item in the bag loses a slice of its freshness (FoodSpoil.PerDay, by food type) unless `preserved` (fridge).
        // Driven off the day counter so a dev `timeAdd 48` fast-forwards two days of spoilage at once. The first frame just
        // syncs the baseline (no retroactive spoilage on spawn/load). SP-local; MP server-authoritative day sync = fast-follow.
        int _lastSpoilDay = -1;
        void FoodSpoilTick()
        {
            if (Inventory == null) return;
            if (GetTree().GetFirstNodeInGroup("daynight") is not DayNightCycle dnc) return;
            if (_lastSpoilDay < 0) { _lastSpoilDay = dnc.Day; return; }   // baseline on first observation -- don't spoil the moment you spawn
            while (_lastSpoilDay < dnc.Day) { FoodSpoil.TickDay(Inventory); FoodSpoil.TickDayCrates(GetTree()); _lastSpoilDay++; }
        }
        // test seam: drive one day of food spoilage from a headless test
        public void DebugFoodSpoilTick() => FoodSpoil.TickDay(Inventory);

        // Console `fill <fluid>[:<flag>] [amount]` / `empty` on the HELD fluid container (strawberry). amountMl < 0 = full.
        public bool FillHeldContainer(FluidType type, WaterQuality q, float amountMl)
        {
            var a = _heldFluidItem?.GetAsset();
            if (_heldFluidItem == null || a == null || !a.IsFluidContainer) return false;
            float amt = amountMl < 0f ? a.fluidCapacity : Mathf.Clamp(amountMl, 0f, a.fluidCapacity);
            FluidItem.Write(_heldFluidItem, type, amt, q);
            _invUI?.Refresh();
            return true;
        }
        public bool EmptyHeldContainer()
        {
            var a = _heldFluidItem?.GetAsset();
            if (_heldFluidItem == null || a == null || !a.IsFluidContainer) return false;
            FluidItem.Read(_heldFluidItem, a, out var t, out _, out var q);
            FluidItem.Write(_heldFluidItem, t, 0f, q);   // zero it, keep the type/quality
            _invUI?.Refresh();
            return true;
        }
        public bool HoldingFluidContainer => _heldFluidItem != null;
        // the placed fluid device currently LOOKED AT (a tank/source/consumer with a Tank; null for a fitting or nothing)
        public FluidContainer FocusedFluidTank => (_focusFluid != null && IsInstanceValid(_focusFluid) && _focusFluid.Tank != null) ? _focusFluid : null;
        public bool FillFocusedTank(FluidType type, WaterQuality q, float amountMl)
        {
            var c = FocusedFluidTank; if (c == null) return false;
            c.Tank.Type = type;
            c.Tank.Amount = amountMl < 0f ? c.Tank.Capacity : Mathf.Clamp(amountMl, 0f, c.Tank.Capacity);
            c.Tank.Quality = q;
            return true;
        }
        public bool EmptyFocusedTank()
        {
            var c = FocusedFluidTank; if (c == null) return false;
            c.Tank.Amount = 0f;
            return true;
        }

        public Camera3D Camera => _cam;

        // Load a real gun .dat (e.g. Eaglefire) through the ported UnturnedDat layer and equip it.
        // PUSH the equipped gun's per-viewmodel tuning onto the LIVE viewmodel. Must be called after any
        // `new Viewmodel`, not only after LoadGun.
        //
        // strawberry 2026-08-15: "idk what u did for the scope sway reduction but i dont think it worked. looks
        // identical." It didn't. LoadGun set ScopeSwayScale on the viewmodel that existed AT THAT MOMENT, and
        // EquipHeldGun then QueueFree'd it and built a replacement nine lines later -- so every gun ran on the
        // 1.0 default and the AUG/SG550 0.3 never reached anything. The field was parsed, stored, read by the
        // oscillator and applied to the camera; the one broken link was the object it landed on. Nothing logged,
        // nothing failed, and the value was present in every place I thought to look.
        //
        // A method rather than an extra line at each `new Viewmodel` because the next per-gun viewmodel field
        // rots the same way otherwise: the call sites are already two and the ordering hazard is invisible.
        void ApplyGunToViewmodel()
        {
            if (_viewmodel == null) return;
            _viewmodel.ScopeSwayScale = Gun?.ScopeSwayScale ?? 1f;   // per-gun optic steadiness
        }

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
            ApplyGunToViewmodel();   // per-gun viewmodel tuning; see the note there for why it is not inline
            _gunName = System.IO.Path.GetFileNameWithoutExtension(datPath);
            Ammo = Gun.AmmoMax;
            _chambered = HasChamber;   // a freshly-loaded gun starts with a round chambered
            _loadedMagId = Gun.MagazineId;   // the gun comes equipped with its default magazine loaded (its ammo = Ammo)
            _chamberedAmmoType = SDG.Unturned.Assets.find((ushort)Gun.MagazineId)?.ammoType;   // fresh gun -> its default mag's bullet type (master)
            _needsRechamber = false; _rechambering = false; _shotCountForRechamber = 0;   // fresh gun -> not mid-cycle
            _reloading = false; _reloadTimer = 0; _hammerActive = false; _hammerPending = false; _magSwapAnimTimer = 0; _magSwapAutoRack = false;   // switching weapons mid-reload aborts the reload (anim + logic) -- master
            _viewmodel?.SetReloading(false);
            // reset to a valid firemode for THIS gun — don't inherit the previous one (e.g. Auto carried onto the
            // semi-only shotgun would let it hold-fire full-auto). Prefer Semi, then Auto/Burst, else Safety.
            var modes = AvailableModes();
            _firemode = System.Array.IndexOf(modes, FireMode.Semi) >= 0 ? FireMode.Semi
                      : System.Array.IndexOf(modes, FireMode.Auto) >= 0 ? FireMode.Auto
                      : modes[0];
            _burstLeft = 0;
            GD.Print($"[gun] {Gun.Id}: dmg={Gun.Damage} vehicleDmg={Gun.VehicleDamage} range={Gun.Range} firerate={Gun.Firerate} mag={Gun.AmmoMax} pellets={Gun.Pellets} mode={_firemode}");
        }

        public string HeldGunName => _gunName;
        /// <summary>The inventory Item backing whatever is in hand -- the SINGLE home for a gun's ammo/firemode/mag/
        /// attachments. Exposed so gun.reequip_keeps_ammo can assert that a held gun always has one, rather than the
        /// invariant being a thing everyone assumes until a call site quietly stops passing it.</summary>
        public SDG.Unturned.Item HeldItemForTest => _heldItem;

        // Hold a specific gun by its content name: reload the GunDef + rebuild the per-gun viewmodel. Used by Q-switch
        // and by the inventory's Equip action (equipping a gun makes it the held weapon).
        public void EquipHeldGun(string gunName, SDG.Unturned.Item backingItem = null)
        {
            // AN UNKNOWN GUN IS REFUSED, LOUDLY (strawberry: "unknown gun should spit an error center screen and
            // fallback to unarmed"). Before, a gun with no guns_visual.tsv row silently rendered as an EAGLEFIRE --
            // it fired, reloaded, and looked like a finished weapon, so a missing row was indistinguishable from a
            // working port and only surfaced as "why does the dragonfang look like that" much later. Failing here
            // costs the player one hotbar press and tells them exactly what is wrong.
            //
            // Checked BEFORE SaveGunState so a refusal can't disturb the gun already in hand -- you end up unarmed
            // with your previous weapon's ammo intact, not with its state stashed against a gun that never equipped.
            if (!Viewmodel.IsKnownGun(gunName))
            {
                HUD.Alert($"Cannot equip {gunName ?? "<null>"}: no viewmodel data for this gun");
                EquipUnarmed();
                return;
            }
            SaveGunState();   // stash the OUTGOING gun's live state onto its item before we swap away
            LoadGun($"res://content/{gunName}.dat");   // sets Gun + _gunName + Ammo + firemode (fresh defaults)
            _heldItem = backingItem;
            RestoreGunState(backingItem);   // a gun coming from inventory/world remembers its ammo/firemode/mag
            // A stock gun arrives with its factory irons INSTALLED as a real item, so taking them off hands them to
            // you rather than deleting them (strawberry: "irons are their own item and can be installed across
            // weapons"). Only fills an UNSET slot, so it never overwrites what the player fitted.
            if (backingItem != null) AttachmentFit.SeedDefaults(backingItem, SDG.Unturned.Assets.find(backingItem.id)?.itemName);
            _melee = null; _heldConsumable = null; _heldFuelItem = null; _heldFluidItem = null; _heldMeleeName = null; ClearDeployable();   // equipping a gun REPLACES the held consumable/melee/deployable (not a layer) -- master
            _viewmodel?.QueueFree();
            _viewmodel = new Viewmodel { GunName = _gunName };
            AddChild(_viewmodel);
            ApplyGunToViewmodel();   // the replacement viewmodel starts on defaults -- re-push the gun's tuning
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
            TickProxy.Attach(this, ProcessTick, PhysicsTick);   // PERF: see TickProxy -- the engine dispatches to a 2-method node instead of walking this class's ~360-method table 4x per frame
            SetProcess(false); SetPhysicsProcess(false);         // the proxy child owns the engine callbacks; the overrides below stay for DIRECT callers (tests drive the controller with p._Process(dt))
            AddToGroup("players");     // so vehicle explosions (+ future area effects) can find nearby players
            // AN NPC HIND'S ROUNDS GO THROUGH THE REAL BULLET SYSTEM. NpcHeli raises a delegate rather than
            // calling in directly, so the AI does not have to know how a shot is drawn or resolved -- it gets
            // tracers, surface impacts, falloff and player damage for free, and stays testable without a
            // renderer. Wired here because this is where the bullet pool actually lives.
            NpcHeli.NpcShot = (origin, dir, gunId) =>
            {
                var g = TurretGunDef(gunId);
                float dmg = g?.PlayerDamage ?? 30f;
                float veh = g?.VehicleDamage ?? 40f;
                float obj = g?.ObjectDamage ?? 20f;
                float vel = g?.MuzzleVelocity ?? 120f;
                int steps = g != null ? Mathf.Max(1, (int)(g.Range / 2f)) : 125;
                // srcGun AND npc: BOTH have to be passed. Adding the parameters and leaving the call unchanged is
                // how the first attempt at this shipped completely inert -- the mechanism existed, nothing used it,
                // and the suite was green because it hooks this delegate with its own counter and never reaches
                // the real wiring at all.
                SpawnBullet(origin, dir * vel, steps, 0f, dmg, veh, obj, dmg, srcGun: g, npc: true);
                NpcTurretFx(origin, dir, gunId);
            };
            CollisionLayer = 1 << 3;   // player bit
            CollisionMask = (1 << 0) | (1 << 6) | (int)RemotePlayers.RemotePlayerLayer;    // walk on ground (bit 0) + collide with transparent props on bit 6 (see-through to the item LOS raycast but still solid for the player -- master) + OTHER PLAYERS on bit 14 (strawberry 2026-09-03 "player vs player collision"; RemotePlayers.RemotePlayerLayer explains why they are not simply on bit 0). The wall/floor queries below reuse this mask, so they pick it up for free.

            _capsule = new CapsuleShape3D { Height = PlayerMovementDef.HEIGHT_STAND, Radius = 0.35f };
            _hitbox = new CollisionShape3D { Shape = _capsule, Position = new Vector3(0, PlayerMovementDef.HEIGHT_STAND / 2f, 0) };
            AddChild(_hitbox);
            FloorMaxAngle = Mathf.DegToRad(55f);   // climb steeper slopes than Godot's 45 default (master)
            FloorSnapLength = 0.5f;                 // stay glued to the ground over small steps / undulations

            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;   // opt the PLAYER out of Godot's global physics interp -- on-foot uses MANUAL position-only interp so the mouse stays instant (master)
            // NetAvatar keeps the (non-Current) camera node -- look math (LookPoint, aim) reads it -- but a
            // Current camera per avatar would hijack the host viewport (L1 sandbox / any windowed server).
            // LEAN PIVOT (source PlayerLook: the camera's parent). It sits at the player's ORIGIN -- at the feet, not
            // the eyes -- and only ever rolls. That placement IS the mechanic: rolling a pivot at the feet while the
            // camera rides at eye height above it swings your head sideways as a CONSEQUENCE of the tilt. Roll the
            // camera in place instead and you get the tilt with no peek at all, which is the version that feels broken.
            // Retail says so out loud in GetEyesPositionWithoutLeaning: "child of another transform with zeroed
            // position which gets rotated according to the leaning angle".
            _leanPivot = new Node3D { PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off };
            AddChild(_leanPivot);
            _cam = new Camera3D { Position = new Vector3(0, 1.6f, 0), Current = !NetAvatar, PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off };
            _cam.CullMask &= ~OutlineOverlay.OutlineLayer;   // don't render the items' silhouette meshes in the main view (only the offscreen mask cam does)
            _leanPivot.AddChild(_cam);
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
            if (_body != null)
            {
                _body.Visible = false;
                // Opt the LOCAL 3P body out of Godot's global physics interpolation, exactly as the player node above
                // does. It is a SIBLING, not a child, so it never inherited that opt-out -- it sat on the project
                // default (physics_interpolation=true) while _Process writes its transform EVERY FRAME from our
                // already manually-interpolated GlobalPosition. Godot then interpolated that a second time, from the
                // last physics tick's snapshot, so the model juddered against a camera and a world that were both
                // smooth (master: "theres jitter on the model in 3p"). Double-smoothing reads as no smoothing.
                //
                // NOT on a NetAvatar. _Process returns immediately for one, so a remote player's body is never driven
                // per-frame at all -- it is placed straight from network snapshots in PlayerNetSync. Godot's
                // interpolation is the ONLY thing smoothing those, and turning it off here would have fixed our own
                // model by making every other player's stutter.
                if (!NetAvatar) _body.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;
                CallDeferred(Node.MethodName.AddSibling, _body);
            }
            _viewmodel = new Viewmodel { GunName = _gunName };   // per-gun visuals
            AddChild(_viewmodel);
            ApplyGunToViewmodel();
            _rng.Randomize();

            // the ported inventory + its dashboard. Demo-populate it (real items) so there's something to show.
            ItemCatalog.RegisterAll();
            Inventory = new PlayerInventory();
            // TAKE IT OUT OF THE SLOT, IT LEAVES YOUR HANDS (strawberry 2026-08-16: "if the item in your hands
            // was in a pri/sec slot, and you remove it from the pri/sec slot, de-equip the item from your
            // hands"). Driven off the inventory's own change event rather than every drag/move call site, so a
            // future path that empties a holster cannot forget to do it.
            Inventory.onPageChanged += page =>
            {
                if (_heldSlotPage < 0 || page != (byte)_heldSlotPage) return;
                if (Inventory.items[page].getItemCount() == 0) EquipUnarmed();
            };
            PopulateDemoInventory();
            // P4: dress the 3P body off the worn slots. The demo kit already wears Cargo Pants (209) + Alicepack (253);
            // add a starter shirt + hat, then Refresh() paints/attaches every worn slot so the player isn't bare skin.
            _clothing = new PlayerClothingController(_body, Inventory);
            ApplyDefaultOutfit();
            _invUI = new InventoryUI { Inv = Inventory, Player = this, Clothing = _clothing };   // P5: drop-to-slot equip drives the on-body visual through the same controller
            AddChild(_invUI);
            _noteReader = new NoteReader();   // F reads a looked-at lore note into this panel
            AddChild(_noteReader);
            _craftMenu = new CraftingMenu { Inv = Inventory, Player = this };
            AddChild(_craftMenu);
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
        public AmmoRadial AmmoRadial;       // R-hold ammo-type radial for loose-shell shotguns (wired beside AttachMenu); null in demos
        bool _rHolding; ulong _rHeldSince;  // R-hold tracking on a shotgun: a quick tap reloads, holding past AmmoRadialHoldMs opens the ammo radial
        const ulong AmmoRadialHoldMs = 220;

        public override void _UnhandledInput(InputEvent @event)
        {
            if (NetAvatar) return;   // a server avatar is driven ONLY through the Scripted* seams, never local input
            // Inventory dashboard open -> EAT ALL game input except Tab (to close it) + Escape: no firing / world interactions /
            // reloading / look through the open UI. (The UI Controls still get their own clicks; those don't reach _UnhandledInput.) (master)
            if (_invUI != null && _invUI.IsOpen && !(Keybinds.Matches(GameAction.Inventory, @event) || Keybinds.Matches(GameAction.Interact, @event) || @event is InputEventKey { Keycode: Key.Escape })) return;   // Inventory/Interact/Esc allowed through -> Interact also closes an open container inventory (handled at the top of the F branch), master
            // while driving, only E (exit) / V (cam) / L (lights) / Escape + LMB (horn) / RMB (lights) are live -- no fire, aim, reload, etc.
            // (riding a replicated puppet gates identically -- the vehicle-side keys just no-op below in v1)
            if (_ridingCrane != null)   // RIDING A CRANE: mouse orbits the 3P chase; F-exit + W/S/A/D/Q/E drive keys go through the normal chain
            {
                if (@event is InputEventMouseMotion cmm && Input.MouseMode == Input.MouseModeEnum.Captured)
                {
                    _driveCamYaw -= cmm.Relative.X * MouseSensitivity; _driveCamPitch = Mathf.Clamp(_driveCamPitch + cmm.Relative.Y * MouseSensitivity, -20f, 75f);
                    GetViewport().SetInputAsHandled(); return;
                }
            }
            if (_ridingTrain != null)   // RIDING A TRAIN: self-contained input (H = 1P/3P cam, mouse orbits the 3P chase). F-exit + rest use the normal chain below; no vehicle/MP paths touched.
            {
                if (Keybinds.JustPressed(GameAction.ToggleFirstPerson, @event)) { _fp = !_fp; GetViewport().SetInputAsHandled(); return; }
                // N = ignition, the SAME key as a car. Echo:false for the same reason: holding it must not flap
                // the engine. A train has one seat, so there is no driver check to make here. Stays LITERAL for
                // the same reason G/L/Ctrl do -- vehicle-aux, not in the v1 rebind set.
                if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.N }) { _ridingTrain.ToggleEngine(); GetViewport().SetInputAsHandled(); return; }
                if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) { if (mb.Pressed) _ridingTrain.Honk(); GetViewport().SetInputAsHandled(); return; }   // LMB = press-to-honk (one-shot, master)
                if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } rmbT) { if (rmbT.Pressed) _ridingTrain.ToggleHeadlights(); GetViewport().SetInputAsHandled(); return; }   // RMB = toggle headlights (master), like vehicles
                if (@event is InputEventMouseMotion tmm && Input.MouseMode == Input.MouseModeEnum.Captured)
                {
                    if (!_fp) { _driveCamYaw -= tmm.Relative.X * MouseSensitivity; _driveCamPitch = Mathf.Clamp(_driveCamPitch + tmm.Relative.Y * MouseSensitivity, -25f, 70f); }
                    GetViewport().SetInputAsHandled(); return;   // consume look while riding (orbit in 3P; cab is fixed-forward in 1P)
                }
            }
            if (_driving != null || _riding != null)
            {
                // SEAT SELECTION, handled ABOVE the allow-list below rather than added to it. That list exists to
                // stop the driving player firing/reloading through the windscreen, and threading twelve function
                // keys into it would mean the seat keys only work in the states someone remembered to allow.
                // F1 is the driver, F2.. the passengers, matching the seat indices as extracted.
                if (_driving != null && @event is InputEventKey { Pressed: true, Echo: false } sk
                    && sk.Keycode >= Key.F1 && sk.Keycode <= Key.F12)
                {
                    TrySwitchSeat((int)(sk.Keycode - Key.F1));
                    GetViewport().SetInputAsHandled();
                    return;
                }
                bool allowedKey = @event is InputEventKey { Pressed: true } dk && (Keybinds.Matches(GameAction.Interact, @event) || Keybinds.Matches(GameAction.ToggleFirstPerson, @event) || dk.Keycode == Key.G || dk.Keycode == Key.L || dk.Keycode == Key.Ctrl || dk.Keycode == Key.N || dk.Keycode == Key.Escape);   // Interact = exit; ToggleFirstPerson = cam; G = landing gear (retract-gear planes); L lights, Ctrl siren, N ignition, Esc pause. G/L/Ctrl/N stay literal -- vehicle-aux, hardcoded in v1. (ROOT CAUSE of "G does nothing while flying": this allow-list gated G out before the gear handler saw it -- master 2026-08-18)
                bool allowedMouse = @event is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right };
                bool camOrbit = @event is InputEventMouseMotion;   // mouse MOTION must pass through -> it orbits the 3rd-person chase cam (this guard was silently eating it, so the cam sat fixed) (strawberry 2026-07-15)
                if (!allowedKey && !allowedMouse && !camOrbit) return;
            }
            // clicks belong to an open UI (inventory / crate / dashboard) when the cursor's visible -- don't fire / honk / aim THROUGH them (master)
            if (@event is InputEventMouseButton && Input.MouseMode != Input.MouseModeEnum.Captured) return;
            if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                if (_driving != null && (_driving.IsHeli || _driving.IsPlane))
                {
                    // FLYING (heli OR plane): the mouse is the stick (roll on X, pitch on Y), not a camera orbit.
                    //
                    // PITCH SIGN IS A SETTING, not a decision (ControlsOptions.InvertHeliPitch). Godot's
                    // Relative.Y is negative when the mouse moves forward, and the flight model takes pitch
                    // POSITIVE = nose up. Regular (default) wants forward -> nose down -> fly forward, so the
                    // raw delta passes through; Inverted wants forward -> nose up, like a real cyclic, so it
                    // is negated. This shipped as nose-up-on-forward, which VoX reported as flying backwards
                    // and strawberry liked -- they were both describing the same behaviour and disagreeing
                    // about it, which is what a toggle is for.
                    _heliStickR = Mathf.Clamp(_heliStickR + mm.Relative.X * HeliStickGain, -1f, 1f);
                    bool invertFly = _driving.IsPlane ? ControlsOptions.InvertPlanePitch : ControlsOptions.InvertHeliPitch;   // the plane has its OWN invert-Y toggle, separate from the heli's (master)
                    float pitchDelta = invertFly ? -mm.Relative.Y : mm.Relative.Y;
                    _heliStickP = Mathf.Clamp(_heliStickP + pitchDelta * HeliStickGain, -1f, 1f);
                }
                else if ((_driving != null || _riding != null) && !_fp)   // driving in 3rd person: the mouse ORBITS the chase cam around the car instead of turning the driver (master)
                {
                    _driveCamYaw -= mm.Relative.X * MouseSensitivity;
                    _driveCamPitch = Mathf.Clamp(_driveCamPitch + mm.Relative.Y * MouseSensitivity, -25f, 70f);   // inverted Y: mouse up -> cam tilts down (strawberry)
                }
                else if (_riding != null || _driving != null)   // FP free-look: the mouse turns the VIEW while the vehicle steers (retail). Now includes the SP DRIVER, who used to be the one seat pinned facing the hood.
                {
                    // WRAPPED to (-180, 180]. The camera consumes this through a Basis, which is periodic and so
                    // never cared -- but AimTurret CLAMPS it against the mount's traverse limits, and an unwrapped
                    // accumulator makes those limits meaningless: one full spin right leaves the value near -360,
                    // the camera facing forward again and the turret pinned at -120 deg, firing 120 deg away from
                    // the crosshair (TryTurretFire deliberately shoots along the barrel, not the look ray).
                    // Recovering needed ~240 deg of mouse travel before the gun moved at all. Review 2026-08-16.
                    _rideLookYaw = Mathf.Wrap(_rideLookYaw - mm.Relative.X * MouseSensitivity, -180f, 180f);
                    _rideLookPitch = Mathf.Clamp(_rideLookPitch - mm.Relative.Y * MouseSensitivity, -89f, 89f);   // same Y convention as on-foot look: mouse up -> look up

                    // RETAIL SEATED LOOK LIMITS (PlayerLook.clampYaw / clampPitch), read from the source rather
                    // than guessed. Retail stores pitch as 0..180 with 90 level and clamps a seated player to
                    // MIN_ANGLE_SIT 60 / MAX_ANGLE_SIT 120, i.e. +/-30 from level; our pitch is already
                    // level-relative, so it is a straight +/-30.
                    //
                    // Yaw differs by SEAT, which is the detail that makes it feel right: a DRIVER gets +/-160
                    // (you can look back over either shoulder, but never quite straight behind), a passenger
                    // +/-90. A turret seat is exempt -- its own traverse limits already own the yaw, and
                    // clamping here would fight them.
                    // A TURRET SEAT IS EXEMPT: AimTurret already clamps to the mount's own traverse limits, and a
                    // second clamp here would fight them -- the gunner's view would stop before the gun did.
                    bool turretSeat = false;
                    if (_driving?.Turrets != null)
                        foreach (var td in _driving.Turrets) if (td != null && td.Seat == _seatIndex) { turretSeat = true; break; }
                    if (!turretSeat)
                    {
                        float yawLim = (_driving != null && _seatIndex == 0) ? DriverYawLimit : PassengerYawLimit;
                        _rideLookYaw = Mathf.Clamp(_rideLookYaw, -yawLim, yawLim);
                        _rideLookPitch = Mathf.Clamp(_rideLookPitch, -SeatedPitchLimit, SeatedPitchLimit);
                    }
                }
                else if (_driving == null && _riding == null)
                {
                    // Reduce mouse sensitivity while ADS'ing (master): scale toward AdsSensScale for iron sights, or 1/zoom for a
                    // scoped gun (a 4x scope magnifies the view 4x, so the same mouse move should turn 4x less to stay controllable).
                    float aim = _viewmodel?.AimAlpha ?? 0f;
                    float sens = MouseSensitivity;
                    if (aim > 0f) { float mag = ScopeMag; sens *= Mathf.Lerp(1f, mag > 1f ? 1f / mag : AdsSensScale, aim); }
                    RotateY(Mathf.DegToRad(-mm.Relative.X * sens));
                    _pitchDeg = Mathf.Clamp(_pitchDeg + (ControlsOptions.InvertLookY ? mm.Relative.Y : -mm.Relative.Y) * sens, -89f, 89f);   // InvertLookY (Controls tab): off = mouse up -> look up
                    _cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
                    // Feed the viewmodel the LOOK DELTA for its inertia roll (PlayerAnimator's
                    // rotationInputViewmodelRoll). Source drives that spring off per-frame input delta, not off
                    // camera angle, so it has to be sampled here where the delta exists -- by _Process the
                    // information is gone. Post-sensitivity on purpose: the source scales by the same option, so an
                    // ADS'd scope leans LESS for the same hand movement, which is what makes a high-zoom optic feel
                    // heavy rather than twitchy.
                    _viewmodel?.AddLookDelta(mm.Relative.Y * sens, mm.Relative.X * sens);
                }
            }
            else if (Keybinds.JustPressed(GameAction.Fire, @event))
            {
                // A GUNNER fires the mount; only the DRIVER honks. This used to be an unconditional
                // `if (_driving != null) _driving.Honk();` with no seat check, which made FireTurret unreachable
                // through input entirely: Fire() is called from StartFire (the on-foot else-branch below) and from
                // the auto poll, so a Hind nose-gunner could aim the chin gun perfectly, click, and honk the
                // helicopter. HeliPartsTests calls TryTurretFire on the Vehicle directly, so the routing was never
                // covered. Fire() already short-circuits to FireTurret for a turret seat. Review 2026-08-16.
                if (_driving != null) { if (_seatIndex != 0 && _driving.HasTurret(_seatIndex)) Fire(); else _driving.Honk(); }
                else if (_riding != null) { }                          // riding a replicated vehicle: no net horn in v1
                else if (HoldingWireTool) WireLmb();                    // wire tool: pick output / place node / complete on a consumer
                else if (HoldingHoseTool) HoseLmb();                    // hose tool: pick a fluid port / complete on the opposite-role port
                else if (HoldingRopeTool) RopeLmb();                    // rope tool: pick a rear tow node / complete on a front tow node
                else if (HoldingDetonatorTool) TryDetonateCharges();    // detonator: LMB plunge -> fire all placed remote charges
                else if (_build != null && _build.Active) _build.Place();   // build mode: place a structure
                else if (HoldingDeployable) TryPlaceDeployable();       // holding a deployable: LMB plants it at the ghost
                else if (HoldingConsumable) StartConsume();             // holding a food/drink: LMB eats/drinks it
                else if (_heldFluidItem != null) TryDrinkContainer();   // holding a fluid container: LMB (aimed away from a tank) sips clean water for hydration (strawberry)
                else if (_heldFuelItem != null) TryDepositFuel();       // holding a gas can: LMB POURS fuel into the generator/vehicle you're aimed at (master)
                else if (HoldingFisher) FisherPrimary();                // holding a rod: LMB press starts the cast gauge / lands the fish on the bite (UseableFisher.startPrimary)
                else if (IsRepeatedMelee) { }                          // Repeated tool (blowtorch/chainsaw): LMB is a continuous HOLD driven by the use-tick (UpdateSalvage), never a swing/punch (source UseableMelee.startPrimary: isRepeated -> startSwing)
                else if (_melee != null) MeleeAttack(false);            // LMB with a normal melee = WEAK swing (source UseableMelee)
                else StartFire();
            }
            else if (Keybinds.JustReleased(GameAction.Fire, @event))
            {
                if (HoldingFisher) FisherRelease();   // LMB release with a rod: lock in the charge and fling the bobber (UseableFisher.stopPrimary)
            }
            else if (Keybinds.Matches(GameAction.Aim, @event) && @event is not InputEventKey { Echo: true })
            {
                if (_driving != null) { if (Keybinds.IsDown(@event)) _driving.ToggleHeadlights(); }   // RMB while driving: toggle lights
                else if (_riding != null) { }                                             // riding: no net light toggle in v1
                else if (HoldingWireTool) { if (Keybinds.IsDown(@event)) { if (_wiring) WireRmb(); else WireManageArm(); } }   // routing: undo/cancel; else: arm a completed-wire clear/unplug (phase 5)
                else if (HoldingHoseTool) { if (Keybinds.IsDown(@event)) { if (_hosing) HoseRmb(); else if (IsInstanceValid(_hosePort) && _hosePort.Owner != null && _hosePort.Owner.Role == FluidRole.Valve) _hosePort.Owner.ToggleValve(); else HoseManageArm(); } }   // routing: undo/cancel node; else: RMB a valve port toggles it, else arm a hosed-port clear/unplug (mirror the wire tool)
                else if (HoldingRopeTool) { if (Keybinds.IsDown(@event)) { if (_roping) CancelRope(); else RopeManageArm(); } }   // rope tool: cancel a pending tie; else arm a clear/disconnect (hold RMB clears the rope, tap disconnects that side) -- mirrors the wire tool
                else if (HoldingDetonatorTool) { }   // detonator has no RMB action (LMB plunges) -- swallow so it doesn't fall through to ADS
                else if (HoldingDeployable) { if (Keybinds.IsDown(@event)) Dequip(); }   // RMB cancels placement entirely -> empty hands (strawberry)
                else if (_heldFluidItem != null) { if (Keybinds.IsDown(@event)) TryFillContainer(); }   // fluid container in hand: RMB a placed tank/source to fill it (LMB sips) (strawberry)
                else if (_heldFuelItem != null) { if (Keybinds.IsDown(@event)) TryExtractFuel(); }   // gas can in hand: RMB a powered PUMP to SUCK fuel into the can (LMB pours it out into a gen/vehicle) (master)
                else if (_melee != null) { if (Keybinds.IsDown(@event) && !IsRepeatedMelee) MeleeAttack(true); }   // RMB = STRONG swing on a normal melee; a Repeated tool (blowtorch/chainsaw) has NO strong attack (source startSecondary: if(!isRepeated)) and no ADS
                else _viewmodel?.SetAiming(Keybinds.IsDown(@event));   // hold RMB to ADS -- GUNS only (a melee weapon has no sights)
            }
            else if (Keybinds.Matches(GameAction.Reload, @event) && @event is not InputEventKey { Echo: true })
            {
                if (HoldingDeployable && _placer != null) { if (Keybinds.IsDown(@event)) _placer.YawOffset += 90f; }   // R rotates the deployable ghost 90 deg (strawberry)
                else if (HasGunOut && CanOpenAmmoPie)   // shotgun / mag gun: quick TAP = reload, HOLD = ammo radial (master)
                {
                    if (Keybinds.IsDown(@event)) { if (!_rHolding) { _rHolding = true; _rHeldSince = Time.GetTicksMsec(); } }
                    else   // release
                    {
                        _rHolding = false;
                        if (AmmoRadial != null && AmmoRadial.IsOpen) { AmmoRadial.ConfirmAndClose(); Input.MouseMode = Input.MouseModeEnum.Captured; }   // held long enough -> pick the highlighted ammo, then recapture the look
                        else StartReload();   // quick tap -> normal reload
                    }
                }
                else if (Keybinds.IsDown(@event) && HasGunOut) StartReload();   // any other gun: instant reload on press (unchanged)
            }
            // Echo: false, like R and F above. The hotbar keys were the only equip input without it, so HOLDING a
            // number key ran the equip/de-equip toggle at the OS key-repeat rate -- each pass frees the viewmodel
            // and builds a new one, so the weapon strobed and ~30 Viewmodel nodes a second were constructed and
            // thrown away while the key was down. Review 2026-08-16.
            else if (Keybinds.IsDown(@event) && @event is not InputEventKey { Echo: true } && HotbarSlot(@event) is int hbSlot)
                EquipHotbar(hbSlot);   // hotbar keys (bag CLOSED): 1/2 = primary/secondary, 3-9 = bound item. Bindable Hotbar1..Hotbar9 (default 1..9). Binding (RMB item + 3-9) is handled in InventoryUI while the bag's open.
            else if (Keybinds.JustPressed(GameAction.Firemode, @event))
            {
                // Gated on build mode the same way C already splits crouch-vs-cycle-structure: while the build
                // ghost is up, firemode is meaningless and the tier selector is what you want.
                if (_build != null && _build.Active) _build.CycleTier();
                else if (_driving == null && HasGunOut) CycleFiremode();   // V on foot: cycle firemode (only with a gun out)
            }
            else if (Keybinds.JustPressed(GameAction.ToggleFirstPerson, @event))
                _fp = !_fp;   // ToggleFirstPerson (default K): 3rd/1st person camera. Moved off H so Grenade(H) is no longer dead code.
            // (Q weapon-switch removed -- master: we have the inventory + spawn commands to test weapons now)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.L } && _driving != null)
            {
                if (_driving != null) _driving.ToggleHeadlights();         // L while driving: toggle headlights
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Ctrl } && _driving != null)
            {
                if (_driving != null && _driving.HasSiren) _driving.ToggleSiren();   // Ctrl while driving an emergency vehicle: toggle siren/lightbar (master)
            }
            // N = IGNITION (strawberry_cow 2026-08-24). DRIVER ONLY: a passenger reaching over and killing the
            // engine is not a feature. Echo:false so holding N cannot flap the engine on and off at key-repeat.
            else if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.N })
            {
                if (_driving != null && _seatIndex == 0) _driving.ToggleEngine();
            }
            else if (Keybinds.JustPressed(GameAction.Interact, @event))   // Interact (default F, moved off E): exit/hitch/pickup/enter/harvest/open-crate; nothing to interact -> inspect the held weapon. Echo:false so HOLDING it can't double-fire the hitch toggle.
            {
                if (_noteReader != null && _noteReader.IsOpen) _noteReader.Close();   // F while a note is open -> close it first (same as Esc)
                else if (_invUI != null && _invUI.IsOpen) { SaveGunState(); CloseCrate(); _invUI.Close(); Input.MouseMode = Input.MouseModeEnum.Captured; }   // F while a container inventory is open -> CLOSE it (CloseCrate swings the door shut too), same as Escape (master)
                else if (_driving != null && !DrivingPredicted) ExitVehicle();  // hop out (SP direct exit; a Part A predicted drive falls through to the server REQUEST below)
                else if (_ridingTrain != null) ExitTrain();                     // hop out of a boarded train (parallel ride path)
                else if (_ridingCrane != null) ExitCrane();                     // hop out of a boarded crane
                else if (RequestExitPuppet()) { }                          // riding a replicated vehicle: ask the server to free the seat (C6)
                else if (TryToggleHitch()) { }                             // on foot at a trailer hitch: couple / uncouple
                else if (_focusShelfItem != null || _focusItem != null) TryPickup();   // looking at a SHELF item or a dropped item: grab it (shelf item takes priority in TryPickup)
                else if (RequestPickupFocusedPuppet()) { }                 // MP: looking at a REPLICATED dropped item -> ask the server for it (like SP, a focused item wins over a nearby vehicle)
                else if (_focusVehicle != null && IsInstanceValid(_focusVehicle) && !_focusVehicle.IsWreck && !_focusVehicle.IsTrailer)
                {
                    // WHAT you are aiming at now decides what F does, instead of one action for the whole car.
                    if (_focusAccessValid && _focusAccess.Kind == Vehicle.AccessKind.Trunk) OpenVehicleTrunk(_focusVehicle);
                    else if (_focusAccessValid && _focusAccess.Kind == Vehicle.AccessKind.Hood) OpenVehicleHood(_focusVehicle);
                    else EnterVehicle(_focusVehicle, _focusAccessValid ? _focusAccess.Seat : -1);   // a door -> THAT seat; no zone -> the old first-free behaviour
                }
                else if (RequestEnterNearestPuppet()) { }                  // MP shell near a REPLICATED vehicle: ask the server for the seat (C6; false in SP -- no puppets)
                else if (_focusCouplerTrain != null && IsInstanceValid(_focusCouplerTrain)) _focusCouplerTrain.Uncouple(_focusCouplerIdx);   // LOOKING at a coupler: F splits the train there (master)
                else if (_focusTrain != null && IsInstanceValid(_focusTrain)) BoardTrain(_focusTrain);   // LOOKING at a train loco: board it (outlined affordance, master)
                else if (NearestTrain() is Train nt) BoardTrain(nt);       // fallback: stood next to a train, board it
                else if (NearestCrane() is HarborCrane nc) BoardCrane(nc);   // stood next to a harbor crane, board it
                else if (_focusDeployable != null && IsInstanceValid(_focusDeployable))
                {   // looking at a placed deployable: F starts a HOLD -> pick it up (UpdateDeployPickup); a quick TAP toggles
                    // a generator's power (fired on release). Consume F so it doesn't fall through to open a nearby crate.
                    _fHeldDeploy = _focusDeployable; _deployPickupTimer = 0f;
                }
                else if (_focusFluid != null && IsInstanceValid(_focusFluid)) { _fHeldFluid = _focusFluid; _fluidPickupTimer = 0f; }   // hold F on a placed fluid device -> pick it up (UpdateFluidPickup)
                else if (_focusDoor != null && IsInstanceValid(_focusDoor)) { _fHeldDoor = _focusDoor; _doorLockTimer = 0f; }   // looking at a door: F starts a HOLD -> lock/unlock (UpdateDoorLockHold); a quick TAP opens/closes it (fired on release)
                else if (_focusObjectDoor != null && IsInstanceValid(_focusObjectDoor))   // looking at an openable prop door (fridge / doorway door): F toggles it directly (no hold/lock semantics, unlike a building Door)
                {
                    if (ObjectDoorBarricaded(_focusObjectDoor)) _barricadedDoorMsg = BarricadedMsgTime;   // boards on either face -> blocked; flash "Door is barricaded" (asserted below, after UpdateFluidPickup clears the shared HUD each frame) (master 2026-09-01)
                    else RequestToggleObjectDoor(_focusObjectDoor);
                }
                else if (_focusTV != null && IsInstanceValid(_focusTV)) _focusTV.Toggle();   // looking at a TV: F toggles it on/off (per-TV state)
                else if (_focusLamp != null && IsInstanceValid(_focusLamp)) _focusLamp.Toggle();   // looking at a standing/desk lamp: F toggles it on/off
                else if (_focusElevButton != null && IsInstanceValid(_focusElevButton)) _focusElevButton.Press();   // looking at a floor button: F sends the car to that floor (the button panel is the interactable now, not the car)
                else if (_focusMonitor != null && IsInstanceValid(_focusMonitor)) _focusMonitor.Toggle();   // ...same for a patient monitor
                else if (_focusNote != null && IsInstanceValid(_focusNote)) _noteReader?.Show(_focusNote);   // looking at a readable note: F reads it
                else if (_focusBed != null && IsInstanceValid(_focusBed)) ClaimFocusedBed();       // looking at a bed: claim it as your respawn point
                else if (RequestHarvestNearestCrop()) { }                  // MP shell near a GROWN replicated crop: ask the server to harvest it (A4; false in SP -- no NetHarvestCrop seam)
                else if (CropManager.NearestGrown(GlobalPosition) is CropNode grownCrop) CropManager.Harvest(grownCrop, this);  // harvest a nearby fully-grown crop (source InteractableFarm harvest)
                else if (_focusShelf != null && IsInstanceValid(_focusShelf) && OpenCrate(_focusShelf)) { }   // looking at a shelf/container -> open it (look-based, not proximity)
                else if (OpenNearestCrate()) { }                           // open a nearby storage crate
                else if (_melee != null) _viewmodel?.PlayMeleeInspect();   // nothing to interact with -> inspect (melee plays its own Inspect clip)
                else _viewmodel?.PlayInspect();                            // ...or the gun's own inspect
            }
            else if (Keybinds.JustReleased(GameAction.Interact, @event) && _fHeldDeploy != null)
            {   // released F over a deployable: a quick TAP toggles a generator (a long hold already picked it up in UpdateDeployPickup)
                if (IsInstanceValid(_fHeldDeploy) && _deployPickupTimer < DeployPickupTime && _fHeldDeploy.CanTogglePower)
                {
                    if (!RequestToggleDeployable(_fHeldDeploy)) _fHeldDeploy.TogglePower();
                }
                if (IsInstanceValid(_fHeldDeploy)) _fHeldDeploy.PickupProgress = 0f;
                _fHeldDeploy = null; _deployPickupTimer = 0f;
            }
            else if (Keybinds.JustReleased(GameAction.Interact, @event) && _fHeldFluid != null)
            {   // released F over a fluid device: a quick TAP on a VALVE opens/closes it (a long hold already picked it up in
                // UpdateFluidPickup) -- mirrors the generator tap-toggle, so a valve is toggled the SAME way as a power switch
                // (strawberry: "valve cannot be interacted with?" -- the hose-tool-port RMB still works too).
                if (IsInstanceValid(_fHeldFluid) && _fluidPickupTimer < DeployPickupTime && _fHeldFluid.Role == FluidRole.Valve)
                    _fHeldFluid.ToggleValve();
                _fHeldFluid = null; _fluidPickupTimer = 0f;
            }
            else if (Keybinds.JustReleased(GameAction.Interact, @event) && _fHeldDoor != null)
            {   // released F over a door: a quick TAP opens/closes it (a long hold already flipped the lock
                // in UpdateDoorLockHold) -- the same tap/hold split the generator and the valve use
                if (IsInstanceValid(_fHeldDoor) && _doorLockTimer < DeployPickupTime) RequestToggleDoor(_fHeldDoor);
                _fHeldDoor = null; _doorLockTimer = 0f;
            }
            else if (Keybinds.JustPressed(GameAction.Flashlight, @event))
                ToggleHeldLight();    // Flashlight (default B, source TACTICAL key): the held flashlight. Self-guards on actually holding one.
            // BUILD MODE HAS NO KEY (strawberry 2026-08-12: "remove build mode toggle for now. just the hotkey").
            // B used to toggle it and now belongs to the torch, which is the source binding. BuildTool itself is
            // untouched and intact -- Toggle/CycleType/Place/Spawn all still work -- but nothing calls Toggle(),
            // so `Active` can never become true and the tool is UNREACHABLE in game, not merely unbound. Say that
            // plainly rather than leaving a dead C-cycles-structure branch reading like a live feature.
            // Restoring it is one line here, on whichever key it should have.
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.C } && (_build?.Active ?? false))
                _build?.CycleType();  // cycle the structure type (floor/wall/pillar/rampart/roof)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.R } && (_build?.Active ?? false))
                SalvageAimedStructure();   // R while building: take the aimed piece back down (reload is meaningless here)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Y } && (_build?.Active ?? false))
                UpgradeAimedStructure();   // Y while building: wood -> brick -> metal in place
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.G } && _driving != null && _driving.HasRetractGear)
                { GD.Print("[GEAR] G-input -> retract branch"); _driving.ToggleGear(); }   // G while flying a retract-gear plane: toggle the landing gear (debounced in Vehicle) (master 2026-08-18)
            else if (Keybinds.JustPressed(GameAction.Melee, @event))
                MeleeAttack();        // dedicated melee swing (default G) at a zombie in reach
            else if (Keybinds.JustPressed(GameAction.Grenade, @event))
                ThrowGrenade();       // UNBOUND by default (strawberry 2026-08-24) -- JustPressed is false for an unbound
                                      // action, so this is dormant rather than deleted, and binding it in the menu revives it
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.P, Echo: false })
            {
                WorldItem.ShowLabels = !WorldItem.ShowLabels;                       // P: toggle ALL item ESP name tags
                GetTree().CallGroup("esp_labels", "set_visible", WorldItem.ShowLabels);
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.O, Echo: false })
                WorldItem.ShowLookSphere = !WorldItem.ShowLookSphere;               // O: toggle the look-END sphere visualizer (master)
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.I, Echo: false })
            { _showLookHulls = !_showLookHulls; if (_lookHullViz != null) _lookHullViz.Visible = _showLookHulls; }   // I: toggle the look-focus HULL wireframes for every vehicle (strawberry)
            else if (Keybinds.Matches(GameAction.AttachMenu, @event) && @event is not InputEventKey { Echo: true })
            {
                if (AttachMenu != null)   // AttachMenu (default T, hold): show the weapon-attachment menu while held, release to close
                {
                    // attachments are gun-only: no menu for melee/fists/consumable/deployable (strawberry)
                    if (Keybinds.IsDown(@event) && !AttachMenu.IsOpen && _viewmodel != null && _viewmodel.IsGunViewmodel)
                    {
                        AttachMenu.VM = _viewmodel;
                        AttachMenu.Player = this;   // the menu draws its quick-attach options from THIS bag; bound here beside VM so a new call site can't wire one and forget the other
                        AttachMenu.Open();
                        Input.MouseMode = Input.MouseModeEnum.Visible;
                    }
                    else if (!Keybinds.IsDown(@event) && AttachMenu.IsOpen)
                    {
                        AttachMenu.Close();
                        Input.MouseMode = Input.MouseModeEnum.Captured;
                    }
                }
            }
            else if (Keybinds.JustPressed(GameAction.Inventory, @event))
            {
                if (_viewmodel != null && _viewmodel.InAttachView) return;   // no inventory while the attachment menu is up
                SaveGunState();   // capture the held gun's live state (ammo/mag/firemode/attachments) so dropping/moving it in the inventory keeps it (master)
                if (_invUI != null && _invUI.IsOpen) CloseCrate();   // closing the dashboard saves an open crate
                _invUI?.Toggle();   // open/close the inventory dashboard, freeing the mouse while it's open
                Input.MouseMode = (_invUI != null && _invUI.IsOpen) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
            // Y opens the crafting index -- the inventory navbar has advertised "Craft [Y]" all along while
            // nothing listened for it outside build mode. The build-mode Y handler above still wins when active.
            else if (Keybinds.JustPressed(GameAction.Craft, @event) && !(_build?.Active ?? false))
            {
                _craftMenu?.Toggle();
                Input.MouseMode = (_craftMenu != null && _craftMenu.IsOpen) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
            else if (Keybinds.JustPressed(GameAction.Skills, @event))
            {
                _skillsUI?.Toggle();   // Skills (default J): open/close the skills menu (spend XP to level skills)
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
                else if (_craftMenu != null && _craftMenu.IsOpen)
                {
                    _craftMenu.Close(); Input.MouseMode = Input.MouseModeEnum.Captured;
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

        public void OpenInventory() { _invUI?.Open(); Input.MouseMode = Input.MouseModeEnum.Visible; }   // Open() scans Nearby itself now
        /// <summary>L1 seam: the G-keybind path exactly as _Input runs it (`_invUI?.Toggle()`), so a test can
        /// exercise the entry point players actually use rather than the convenient OpenInventory one.</summary>
        public void DebugToggleInventory() => _invUI?.Toggle();
        public void DemoSelect(byte page, byte x, byte y) { _invUI?.DebugSelect(page, x, y); Input.MouseMode = Input.MouseModeEnum.Visible; }
        public void DemoEquip(byte page, byte x, byte y) => _invUI?.DebugEquip(page, x, y);

        // seed the inventory with real items: wear the Alicepack (8x7) + Cargo Pants (6x3) so those pages open up,
        // put both guns in the hand slots, and scatter medical/food/water across pockets + backpack to show packing
        void PopulateDemoInventory() => PopulateSpawnKit(Inventory);   // what a PLAYER spawns with; the full demo kit is an MP/test fixture now

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
        /// <summary>What a player SPAWNS with (strawberry 2026-08-03: "no mag no bandage no backpack. shirt n pants.
        /// no eaglefire"). The old kit was two rifles, four magazines, two shell stacks, three medkits, food, water, a
        /// knife, a sledgehammer and a blowtorch -- a bag you emptied before you could use it, when everything is
        /// spawnable from the console anyway.
        ///
        /// The clothes stay because they ARE the grid: Resize() sizes the shirt and pants pages off the worn item, so
        /// a player wearing neither has nowhere at all to put what they spawn in. No backpack, so the space is
        /// deliberately small rather than accidentally zero.</summary>
        public static void PopulateSpawnKit(PlayerInventory inv)
        {
            inv.wearShirt(new Item(3));     // Orange Hoodie -> shirt slot + its grid
            inv.wearPants(new Item(209));   // Cargo Pants   -> pants slot + 6x3 grid
        }

        /// <summary>The FULL demo loadout. No longer what anyone spawns with -- it is now a FIXTURE: the MP join
        /// seeding and four netcode tests use it to stand up a player who already owns things (a gun in the primary
        /// slot, bandages and medkits on the server grid) so they can assert adoption, consumption and the server's
        /// shot-validation profile.
        ///
        /// Kept whole on purpose. Those tests currently treat "the shell spawns holding the Eaglefire" as a given, so
        /// emptying this is a behaviour change to MP, not a loadout tweak -- see PopulateSpawnKit for what a player
        /// actually gets.</summary>
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
            if (_reloading || _unloading || _dead || _magSwapAnimTimer > 0) return;   // busy: mid-reload/unload/mag-swap-anim -> wait (master: cooldown)
            if (_needsRechamber || _rechambering) return;   // must finish cycling the bolt/pump first (source: reload gated by needsRechamber)
            int max = Gun?.AmmoMax ?? 30;
            if (Ammo >= ChamberedCap) return;   // already topped off (full mag + the round in the chamber)
            if (UsesMagItem && (FindBestMag()?.item.amount ?? 0) <= 0) { _viewmodel?.PlayDryFire(); return; }   // no spare mag WITH ROUNDS -> can't reload; dry-fire instead of refilling from an empty/absent mag (master)
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
            if (IsSwimming) return;   // no firing while swimming -- guns are canUseUnderwater=false (source PlayerEquipment: submerged/SWIM + !canUseUnderwater blocks the use)
            if (!HasGunOut) return;   // no gun in hand (fists / melee / held item) -> no firing at all (master: gun & held item mutually exclusive)
            if (_reloading) { if (Gun?.ShellReload == true && Ammo > 0) { _reloading = false; _viewmodel?.SetReloading(false); } else return; }   // shell-fed shotgun: firing CANCELS the shell-by-shell reload (shoot what's loaded); other guns ignore fire mid-reload (master)
            if (_unloading) { if (Ammo > 0) { _unloading = false; _viewmodel?.SetReloading(false); } else return; }   // firing INTERRUPTS an unload -> keep what's still loaded (master: the pie reopens only once the action is finished/interrupted)
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
            var next = modes[(i + 1) % modes.Length];
            bool changed = next != _firemode;
            _firemode = next;
            _burstLeft = 0;
            if (changed) PlaySelectorSwitchSound();   // fire-selector click (source: firemode effect / Firemode.mp3)
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
            // A GUNNER fires the MOUNT, not what they are carrying. Checked before every held-weapon gate below,
            // because those gates are about a rifle in your hands -- reload state, chambering, swimming, the
            // viewmodel's equip animation -- and none of them describe a belt-fed gun bolted to an airframe.
            if (_driving != null && _seatIndex != 0 && _driving.HasTurret(_seatIndex))
                return FireTurret();

            if (_fireCd > 0f || Ammo <= 0 || _reloading || _unloading || _magSwapAnimTimer > 0 || _needsRechamber || _rechambering || _cam == null || _dead || _ridingTrain != null || _ridingCrane != null || (_driving != null && (_seatIndex == 0 || !_fp))
                || !HasGunOut || IsSwimming || (_invUI?.IsOpen ?? false)) return false;   // IsSwimming: guns are canUseUnderwater=false -> no shot while swimming, incl. the polled AUTO/burst tick (source PlayerEquipment). !HasGunOut: no gun in hand (melee/held item disarm it) -> no shot, even from the polled auto/burst tick after switching away mid-fire (master)
            // -- also while the bolt/pump still needs cycling -- kills a queued burst the frame we die (the tick calls Fire()) + ignores death-screen clicks (master). _driving guard fixes the "stray tracer flies straight south" bug: the auto/burst tick (_PhysicsProcess) calls Fire() on held-LMB WITHOUT a driving check, and while driving _cam is TopLevel (detached chase cam) -> aim = the chase cam's fixed heading, not the player's look. LMB honks while driving anyway.
            if (AmmoRadial?.IsOpen ?? false) return false;   // no firing while the ammo radial is up -- you're picking ammo, not shooting
            if (_viewmodel != null && (!_viewmodel.IsEquipComplete || _viewmodel.IsInspecting || _viewmodel.InAttachView)) return false;   // no firing until equip finishes, or during inspect / attachment menu (source canFire gates)
            // ONE damage field now; the target applies its own zone/limb multiplier. A loaded shell may override it
            // (slug 40 / beanbag 20 vs the gun's per-pellet buckshot 12) -- same gun, different cartridge in the tube.
            float damage = ShotDamage();   // range/travel are encoded in the bullet's steps + velocity
            float vehDamage = Gun?.VehicleDamage ?? 40f;   // bullets hurt vehicles less than zombies (source Vehicle_Damage)
            float objDamage = Gun?.ObjectDamage ?? 25f;    // bullets vs destructible props (source Object_Damage)
            _fireCd = Gun != null ? (Gun.Firerate + 1) / 50f : 0.1f;   // interval = firerate+1 ticks: source fires when clock-lastFire > firerate (STRICT >, UseableGun.tockShoot), so the real gap is firerate+1. Off-by-one made fast guns (zube firerate 4: 750rpm vs correct 600) fire ~25% too hot -- master's "very high ROF"
            Ammo--;
            _chambered = HasChamber && Ammo > 0;   // the action auto-cycles the next round into the chamber; the last shot leaves it empty
            _chamberedAmmoType = _chambered ? MagAmmoType : null;   // the freshly-cycled round takes the MAG's type (master: the chamber follows the mag as rounds feed)
            _sinceShot = 0f;   // infAmmo waits out a lull, so every shot restarts the clock
            // fire feedback + the gun's real per-shot viewmodel shake (Shake_Min/Max_*); zero if no gun loaded
            float stanceMul = StanceRecoilMul();   // crouch/prone recoil steadier once settled -- scales the kick + the aim-climb below (master)
            float sharp = Skills.SharpshooterRecoilMultiplier();   // SHARPSHOOTER: up to -40% recoil + spread at max level (source UseableGun)
            // RECOIL MOVES THE CAMERA, NOT THE GUN (strawberry: "making recoil move the whole camera instead of
            // just the gun. same thing as the scope sway fix u just did, but for recoil impulse").
            //
            // This was the sway bug again, and worse. TWO paths existed: `rvPitch/rvYaw` rotated the viewmodel via
            // Kick, and `_recoilPending` folded a separately-scaled kick into the aim. Each called RandfRange on
            // its OWN roll, so the gun and the camera kicked by different random amounts on the same shot -- the
            // weapon climbing one way while your aim went another. Not a magnitude mismatch, two different shots.
            //
            // Now: ONE roll, and it lands on the aim. The gun's rotational kick is gone (Kick takes 0/0), so the
            // muzzle climb you fight is the same motion the bullet answers to.
            //
            // MAGNITUDE CHANGED, deliberately: the aim used to receive the roll scaled by Recover_Y/X (0.3-0.75),
            // with the unscaled remainder going to the gun. With the gun no longer taking any, the aim takes the
            // whole impulse -- so felt recoil is roughly 1.4-3.3x the old CAMERA figure depending on the gun,
            // and the numbers in tonight's balance pass now mean what they say rather than being pre-multiplied
            // by a recovery rate. Dial Recoil_Max_Y if any gun reads hot.
            //
            // Positional Shake_* is left on the viewmodel. That is the weapon physically jolting in the hands, a
            // different channel from where it is pointed, and removing it too would leave a gun that does not
            // react to its own discharge at all.
            if (Gun != null)
            {
                float kickP, kickY;
                if (Gun.PatternClimb > 0f)
                {
                    // WALK THE PATTERN. The stored curve is cumulative, so this shot's impulse is the step
                    // between the previous node and this one -- which is why node spacing IS the felt recoil.
                    _patternShot++; _patternIdle = 0f;
                    var prev = Gun.PatternAt(_patternShot - 1);
                    var cur = Gun.PatternAt(_patternShot);
                    kickP = (cur.V - prev.V) * sharp * stanceMul;
                    kickY = (cur.H - prev.H) * sharp * stanceMul;
                }
                else
                {   // legacy random roll for the guns that declare no pattern (everything outside the 5.56 pass)
                    kickP = _rng.RandfRange(Gun.RecoilMinY, Gun.RecoilMaxY) * sharp * stanceMul;
                    kickY = _rng.RandfRange(Gun.RecoilMinX, Gun.RecoilMaxX) * sharp * stanceMul
                          * (_rng.Randf() < 0.5f ? -1f : 1f);
                }
                _recoilPending += kickP;
                _recoilYawPending += kickY;
                _viewmodel?.Kick(new Vector3(Gun.ShakeMinX, Gun.ShakeMinY, Gun.ShakeMinZ) * stanceMul,
                                 new Vector3(Gun.ShakeMaxX, Gun.ShakeMaxY, Gun.ShakeMaxZ) * stanceMul, 0f, 0f);
                DebugLastRecoilKick = new Vector2(kickP, kickY);
            }
            else _viewmodel?.Kick(Vector3.Zero, Vector3.Zero, 0f, 0f);

            // FROM THE EYES, not the camera (strawberry: "make our bullet raycasts come from the PM's eyes, not the
            // camera middle"). Source: `bullet.origin = player.look.aim.position` (UseableGun.cs:1001). In first person
            // the camera sits at the eyes and this changes nothing; in third person the camera is 2 m back and a metre
            // to one side, so the old origin fired from behind your own shoulder -- through anything between it and you.
            Vector3 from = EyesWorld;
            // Aim from the player's AUTHORITATIVE look (body yaw + camera pitch), NOT the camera's live GLOBAL basis.
            // Reading _cam.GlobalTransform.Basis meant a shot could inherit a transiently-bad camera axis -- flinch/
            // hit-shake (line 1223 sets _cam.Basis = flinch*look) or a frame where the cam basis wasn't the player's
            // -- firing the bullet off in a FIXED world direction regardless of where you aimed (the "stray tracer
            // flies straight south, any gun, any time" bug). Recoil is preserved (it drains into Rotation.Y/_pitchDeg).
            Basis cb = new Basis(Vector3.Up, Rotation.Y) * new Basis(Vector3.Right, Mathf.DegToRad(_pitchDeg));  // X=right, Y=up, -Z=forward
            Vector3 aim = -cb.Z;                                            // undeviated shot axis, from the real look angles
            // ...but in third person the eyes and the crosshair are in different places, so firing straight down the
            // look axis misses whatever the reticle is over by however far the camera is offset. Source converges them
            // (UseableGun.cs:962-977): trace from the CAMERA to find the target, then aim the eyes AT that point.
            if (!_fp) aim = ThirdPersonAim(from, aim);
            float aimA = _viewmodel?.AimAlpha ?? 0f;
            // muzzle: hip sits lower-right (where the barrel is); ADS pulls the gun onto the camera axis, so the
            // muzzle centres (X offset -> 0) as you aim -> the bullet + tracer keep originating from the barrel.
            // Test seams. TWO origins, because conflating them hid a bug in a green test for months:
            // `from` is the EYES, which is what the aim/convergence maths is built on; the bullet actually leaves
            // from `muzzle`, computed below as an offset from it. ShotOriginTests asserted "the shot starts at the
            // eyes" against this variable and passed -- while the projectile it was describing spawned 40cm away.
            // A seam that reports a value the production path does not use is worse than no seam: it is a green
            // check standing exactly where a real one should be.
            DebugLastShotOrigin = from; DebugLastShotDir = aim;
            // THE RAYCAST IS DEAD CENTRE. ALWAYS. (strawberry: "the raycast is always meant to be dead center, the
            // tracer launches from the muzzle and then converges gradually onto the raycast" / "raycast != muzzle".)
            //
            // The projectile leaves the EYE, straight down the sight line, with no lateral, vertical or forward
            // offset. Anything else is a height-over-bore the player cannot zero out: the direction here is the raw
            // look axis, so an offset origin never converges -- it just runs parallel, permanently beside or under
            // the crosshair. The old 0.035 m down term did exactly that (4.3 cm low at 10 m on the eaglefire).
            //
            // The gun-shaped LOOK is the tracer's job and always was -- SpawnBullet anchors the tracer's near end at
            // the viewmodel muzzle and bends it onto the real trajectory, and the comment there already said the
            // bullet "fires from the EYE". It just wasn't true any more: these offsets had been added underneath a
            // system built assuming they did not exist. This restores the split the tracer code documents.
            Vector3 bulletOrigin = from;
            Vector3 fxMuzzle = from + cb.X * (0.12f * (1f - aimA)) - cb.Y * 0.035f + aim * 0.4f;   // visual only: flash/light. NEVER the projectile.
            DebugLastBulletOrigin = bulletOrigin;   // where the PROJECTILE is actually spawned (SpawnBullet + NetFire both use this)
            DebugLastFxMuzzle = fxMuzzle;           // and the VISUAL-ONLY point, so a test can prove the two are still split
            Vector3? bodyMuzzle = (!_fp && _body != null) ? _body.MuzzleWorld : null;   // 3P: fire effects come off the 3P gun's OWN muzzle, not the camera-relative point
            SpawnMuzzleLight(bodyMuzzle ?? fxMuzzle);   // once per shot — the Muzzle_0 flash lights the world
            if (bodyMuzzle.HasValue) _body.FlashMuzzle();   // 3P: the visible flash quad on the gun's muzzle

            // Ballistics: each pellet is a SIMULATED PROJECTILE (travel + drop), not an instant ray. Velocity =
            // dir * MuzzleVelocity; it steps every physics tick (0.02s) in StepBullets, dropping under gravity, its
            // tracer flying with it, hits/damage landing when it arrives. (source: BulletInfo + UseableGun.cs:1539.)
            float spread = Gun != null && Gun.SpreadAngleDegrees > 0f
                ? Mathf.DegToRad(Gun.SpreadAngleDegrees) * Mathf.Lerp(1f, Gun.SpreadAim, aimA) * sharp : 0f;
            // A patterned gun uses PER-AXIS scatter instead of one symmetric cone: a vertical grip kills
            // sideways movement without touching climb, which a single cone cannot express.
            float scatH = 0f, scatV = 0f;
            if (Gun != null && Gun.PatternClimb > 0f)
            {
                float aimScale = Mathf.Lerp(1f, Gun.SpreadAim, aimA) * sharp;
                scatH = Mathf.DegToRad(Gun.ScatterAt(_patternShot, true)) * aimScale;
                scatV = Mathf.DegToRad(Gun.ScatterAt(_patternShot, false)) * aimScale;
                // The per-axis scatter sits ON TOP of the cone; it does not replace it. This line used to read
                // `spread = 0f;   // superseded`, which threw away the base cone computed just above for every
                // patterned gun -- so pattern scatter alone (0.04-0.59 deg) was the ONLY inaccuracy in the game.
                // Eaglefire hipfire measured 3.1 cm at 25 m against retail's 124.6 cm, a 40x error on the first
                // round; dragonfang 1.13 cm against 247 cm. Retail applies both (UseableGun.cs:5049 computes the
                // cone unconditionally, and the pattern is added by ApplyRecoil), and 2573cde5 removed BLOOM --
                // not the accuracy floor -- when it added learnable patterns. Review 2026-08-16.
            }   // SHARPSHOOTER tightens spread too (source UseableGun:5055)
            int pellets = UsesShells && ShellAsset != null ? Mathf.Max(1, ShellAsset.pellets) : Mathf.Max(1, Gun?.Pellets ?? 1);   // shotgun buckshot: pellets come from the LOADED shell (source ItemMagazineAsset.pellets) -- 12ga=6, 20ga=8
            float muzzleVel = Gun?.MuzzleVelocity ?? 500f;
            int steps = Gun?.BallisticSteps ?? 20;
            float gravity = -9.81f * (Gun?.GravityMultiplier ?? 4f);
            for (int i = 0; i < pellets; i++)
            {
                Vector3 dir = spread > 0.0001f ? DeviateInCone(aim, spread) : aim;
                if (scatH > 0.0001f || scatV > 0.0001f)
                {   // independent H/V jitter about the aim axis
                    Basis sb = Basis.LookingAt(aim, Vector3.Up);
                    dir = (aim + sb.X * Mathf.Tan(_rng.RandfRange(-scatH, scatH))
                               + sb.Y * Mathf.Tan(_rng.RandfRange(-scatV, scatV))).Normalized();
                }
                SpawnBullet(bulletOrigin, dir * muzzleVel, steps, gravity, damage, vehDamage, objDamage, damage);   // player + zombie both key off the same number
            }
            // AlertTool point-noise: an unsuppressed gunshot pulls zombies within earshot over to investigate. A silenced
            // barrel skips the alert ENTIRELY (source UseableGun ~936: only alert if barrel==null || !isSilenced) -> stealth.
            if (!Suppressed) SoundBus.Emit(GetTree(), GlobalPosition, SoundBus.Gunshot);   // Phase 3 sound bus: unsuppressed gunshot loudness (suppressed = silent)
            // bolt/pump: this shot needs the action cycled before the next one (source RechamberAfterShotCount -> needsRechamber)
            if (Gun != null && Gun.RechamberAfterShotCount > 0 && ++_shotCountForRechamber >= Gun.RechamberAfterShotCount)
            { _needsRechamber = true; _rechamberDelayTimer = Gun.RechamberAfterShotDelay; }
            SaveGunState();   // keep the backing item's ammo current so a drop/holster mid-fight preserves it (master)
            NetFire?.Invoke(bulletOrigin, aim);   // D1: the UNDEVIATED aim ray over the wire -- the server spawns the authoritative bullet (spread is client fx; the bullets above went cosmetic in SpawnBullet)
            return true;   // shot fired; the actual hits/kills land later in StepBullets
        }

        // A simulated bullet (Unturned's BulletInfo): flies from the muzzle with a velocity, dropping under gravity,
        // stepped every physics tick; its tracer travels with it; it hits/despawns on contact or after its steps.
        // Cosmetic (D1): true on every bullet an MP shell fires locally -- it flies + tracers exactly like SP
        // for responsiveness, but on contact it just vanishes: NO damage, NO Kills++, NO hitmarker, NO impact
        // decals/blood. The server's bullet is the authority; impact fx render from the broadcast ImpactFx
        // event (single fx authority -- otherwise the shooter would render both its local impact AND the echo)
        // and the hitmarker moves to HitConfirmed so it only ever tells the truth. Never set in SP.
        const float TracerBaseW = 0.065f;   // 5.56's tracer half-width; every other cartridge is this times GunDef.TracerScale
        sealed class Bullet { public bool Npc;   // fired by an AI, NOT by the local player: no viewmodel anchor, no hitmarker
            public Vector3 Pos, Vel, Origin; public int StepsLeft; public float Gravity, Damage, VehicleDamage, ObjectDamage, PlayerDamage;
            public float FalloffStart, FalloffEnd, FalloffMin = 1f;
            public int Pierced;   // WALLBANG: surfaces this round has already punched through (capped at MaxPierce)
            /// <summary>Damage multiplier for THIS bullet at an impact point, from distance actually flown.</summary>
            public float FalloffAt(Vector3 impact)
            {
                if (FalloffStart <= 0f || FalloffEnd <= FalloffStart) return 1f;
                float m = impact.DistanceTo(Origin);
                if (m <= FalloffStart) return 1f;
                if (m >= FalloffEnd) return FalloffMin;
                return 1f + (FalloffMin - 1f) * ((m - FalloffStart) / (FalloffEnd - FalloffStart));
            } public bool Cosmetic; public MeshInstance3D Tracer; public Node3D RocketVis; public Vector3 MuzzleAnchor; public bool HasAnchor; public float TracerW = TracerBaseW;
            // The warhead travels WITH the round, like every other per-shot property here. It used to be read off
            // the live `Gun` at impact instead, so the blast belonged to whatever was in the player's hands when
            // the round landed rather than to the gun that fired it: take a turret seat holding the rocket
            // launcher and Fire() short-circuits to FireTurret BEFORE the held-weapon gates, leaving Gun as the
            // launcher -- so every nykorev round detonated a 9 m warhead, including into the airframe the gunner
            // is sitting in. Review 2026-08-16.
            public float BlastRadius, BlastZombieDamage, BlastPlayerDamage, BlastVehicleDamage; }
        readonly System.Collections.Generic.List<Bullet> _bullets = new();

        // playerDamage is carried SEPARATELY from `damage` (which is the zombie number the SP path has always
        // used). A player-shaped target resolves through the humanoid zone table, a zombie through its own limb
        // model -- one field could not serve both without silently reporting the wrong model on one of them.
        /// <summary>Fire the turret this seat operates. The shot leaves the MUZZLE along the BARREL, not from the
        /// camera along the look ray -- the mount clamps its traverse and the view does not, so a camera-sourced
        /// shot would fire straight through the limits that make it a chin turret.</summary>
        bool FireTurret()
        {
            if (_dead || (_invUI?.IsOpen ?? false)) return false;
            if (!_driving.TryTurretFire(_seatIndex, out var origin, out var dir, out var gunId)) return false;

            var def = TurretGunDef(gunId);
            float dmg = def?.PlayerDamage ?? 40f;
            float veh = def?.VehicleDamage ?? 10f;
            float obj = def?.ObjectDamage ?? 25f;
            float muzzleVel = def?.MuzzleVelocity ?? 120f;
            int steps = def != null ? Mathf.Max(1, (int)(def.Range / 2f)) : 75;
            SpawnBullet(origin, dir * muzzleVel, steps, 0f, dmg, veh, obj, dmg);
            return true;
        }

        static ImageTexture _npcFlashTex; static bool _npcFlashTexTried;
        static readonly System.Collections.Generic.Dictionary<string, AudioStream> _npcShotSnd = new();

        /// <summary>Report and muzzle flash for an NPC turret shot (strawberry: "turn up the volume and travel of
        /// the gunshot sounds from helis, adding the muzzle flashes we already have on guns, scaling them up
        /// quite a bit").
        ///
        /// SCALE IS THE WHOLE POINT HERE. The positional one-shots elsewhere in this file run UnitSize 5-8 and
        /// MaxDistance 45-70, which is right for a door closing and completely wrong for a belt-fed gun on an
        /// aircraft: a helicopter shooting at you is heard across a valley, and by the time it is close enough
        /// to be audible on those numbers it is already on top of you. Likewise the 1P/3P muzzle flash is a
        /// 0.55 m quad seen from arm's length -- at the range you watch a gunship from, that is invisible.
        ///
        /// Sounds and the flash texture are cached: a burst is seven to twenty two rounds and neither a
        /// per-shot file read nor a per-shot PNG decode is acceptable at that rate.</summary>
        void NpcTurretFx(Vector3 origin, Vector3 dir, string gunId)
        {
            // ---- REPORT. The HMG ships no loose audio (retail keeps it in the bundle), so it falls back the way
            // the viewmodel does -- but to the Nykorev rather than the Eaglefire, since a .50 belt gun should not
            // crack like an assault rifle.
            if (!_npcShotSnd.TryGetValue(gunId ?? "", out var snd))
            {
                snd = NpcLoadOgg($"res://content/{gunId}_shoot.ogg") ?? NpcLoadOgg("res://content/nykorev_shoot.ogg");
                _npcShotSnd[gunId ?? ""] = snd;
            }
            var host = GetParent();
            if (snd != null && host != null)
            {
                var ap = new AudioStreamPlayer3D { Stream = snd, UnitSize = 34f, MaxDistance = 650f, VolumeDb = 9f };
                host.AddChild(ap);
                ap.GlobalPosition = origin;   // world position is only meaningful once it is in the tree
                ap.Play();
                ap.Finished += ap.QueueFree;
            }

            // ---- FLASH. Same Muzzle_0 star the held guns use, on the same shader, scaled well up.
            if (!_npcFlashTexTried)
            {
                _npcFlashTexTried = true;
                string fp = ProjectSettings.GlobalizePath("res://content/muzzleflash.png");
                if (System.IO.File.Exists(fp)) { var im = Image.LoadFromFile(fp); if (im != null) _npcFlashTex = ImageTexture.CreateFromImage(im); }
            }
            if (host == null) return;
            var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/muzzleflash.gdshader") };
            if (_npcFlashTex != null) mat.SetShaderParameter("tex", _npcFlashTex);
            mat.SetShaderParameter("roll", GD.Randf() * 6.28318f);   // the star spins per shot, as the 1P one does
            var flash = new Node3D { Name = "NpcMuzzleFlash" };
            flash.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(2.6f, 2.6f) }, MaterialOverride = mat });
            flash.AddChild(new OmniLight3D { OmniRange = 18f, LightColor = new Color(0.941f, 0.756f, 0.152f), LightEnergy = 7f, ShadowEnabled = false });
            host.AddChild(flash);
            flash.GlobalPosition = origin + dir.Normalized() * 0.35f;   // just off the muzzle, not inside the barrel
            GetTree().CreateTimer(0.05).Timeout += () => { if (IsInstanceValid(flash)) flash.QueueFree(); };
        }

        static AudioStream NpcLoadOgg(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            return System.IO.File.Exists(p) ? AudioStreamOggVorbis.LoadFromFile(p) : null;
        }

        static readonly System.Collections.Generic.Dictionary<string, GunDef> _turretGuns = new();
        /// <summary>The mount's gun, loaded once per id and cached. A turret firing every frame must not re-parse
        /// a .dat every shot.</summary>
        static GunDef TurretGunDef(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_turretGuns.TryGetValue(id, out var g)) return g;
            using var f = Godot.FileAccess.Open($"res://content/{id}.dat", Godot.FileAccess.ModeFlags.Read);
            var txt = f?.GetAsText();
            g = !string.IsNullOrEmpty(txt) ? GunDef.FromDatText(txt) : null;
            _turretGuns[id] = g;
            return g;
        }

        /// <summary>Somebody else's trigger pull, arriving over the wire (EventPlayerFired). Draws the streak
        /// and plays the report exactly the way an NPC turret's shot already does -- that path was built for
        /// "a gun that is not the local viewmodel" and is the same problem.
        ///
        /// ZERO DAMAGE, deliberately. The server has already resolved this shot analytically against its own
        /// positions and unicast the hit confirm; a client re-simulating it would be a second, disagreeing
        /// opinion about who got hit. This is a picture and a noise, nothing more.</summary>
        public void RemoteShotFx(Vector3 origin, Vector3 dir, string gunId)
        {
            if (dir.LengthSquared() < 1e-6f) return;
            dir = dir.Normalized();
            var g = TurretGunDef(gunId);
            float vel = g?.MuzzleVelocity ?? 500f;
            int steps = g != null ? Mathf.Max(1, (int)(g.Range / 2f)) : 125;
            SpawnBullet(origin, dir * vel, steps, 0f, 0f, 0f, 0f, 0f, srcGun: g, npc: true);
            NpcTurretFx(origin, dir, gunId);
        }

        /// <summary>`srcGun` is the gun that actually FIRED this round. It defaults to the player's held weapon,
        /// which is right for every player shot and wrong for every other kind: an NPC turret's rounds were taking
        /// their falloff, blast and tracer width from whatever the player happened to be carrying. `npc` marks the
        /// round as somebody else's, which suppresses the viewmodel muzzle anchor and the hitmarker -- both of
        /// those say "YOU hit that", and strawberry saw exactly that: "the tracers come from my gun's muzzle
        /// point. the damage counts as coming from ME, as i get hitmarkers for what the heli hit."</summary>
        // "the damage counts as coming from ME, as i get hitmarkers for what the heli hit" -- a hitmarker is a
        // first-person claim of authorship, so it is gated on the round being the local player's.
        void Hitmark(Bullet b, bool head) { if (!b.Npc) HitmarkerHUD.Instance?.Show(head); }
        void HitmarkCircle(Bullet b) { if (!b.Npc) HitmarkerHUD.Instance?.ShowCircle(); }

        void SpawnBullet(Vector3 pos, Vector3 vel, int steps, float gravity, float damage, float vehicleDamage, float objectDamage, float playerDamage = 0f, GunDef srcGun = null, bool npc = false)
        {
            var g = srcGun ?? Gun;
            var b = new Bullet { Npc = npc, Pos = pos, Origin = pos, Vel = vel, StepsLeft = Mathf.Max(1, steps), Gravity = gravity, Damage = damage, VehicleDamage = vehicleDamage, ObjectDamage = objectDamage, PlayerDamage = playerDamage,
                                 FalloffStart = g?.FalloffStart ?? 0f, FalloffEnd = g?.FalloffEnd ?? 0f, FalloffMin = g?.FalloffMin ?? 1f, Cosmetic = NetFire != null && !npc, Tracer = (Suppressed && !npc) ? null : MakeTracer(),   // a suppressed shot draws no streak; every tracer use site is already null-guarded
                                 TracerW = TracerBaseW * GunDef.TracerScale(g?.CaliberName),   // .22 thin, .50 BMG fat; buckshot deliberately tiny (each pellet is its own bullet, so a shot draws 8 of these)
                                 BlastRadius = g?.BlastRadius ?? 0f, BlastZombieDamage = g?.BlastZombieDamage ?? 0f,
                                 BlastPlayerDamage = g?.BlastPlayerDamage ?? 0f, BlastVehicleDamage = g?.BlastVehicleDamage ?? 0f };
            // LOCAL first-person only: anchor the tracer's near end at the VIEWMODEL MUZZLE (screen-bridged to the world
            // via the viewmodel cam -> world cam), so it looks like it leaves the barrel; it then BENDS onto the real
            // trajectory (which fires from the EYE). Remote/3p bullets have no on-screen viewmodel muzzle, so they keep
            // the straight-from-origin streak (HasAnchor stays false; a point behind the cam fails the guard).
            if (npc) { }   // somebody else's gun: straight-from-origin streak, never the player's muzzle
            else if (!NetAvatar && !_fp && _body != null && _body.MuzzleWorld is Vector3 _bmz)   // 3P: anchor the tracer at the 3P gun's OWN muzzle (position); it still flies to the converged aim, so it tracks the bullet
            { b.MuzzleAnchor = _bmz; b.HasAnchor = true; }
            else if (!NetAvatar && _viewmodel != null && _cam != null && _viewmodel.TryMuzzleScreenPos(out var _mpx))
            { b.MuzzleAnchor = _cam.ProjectPosition(_mpx, 1.5f); b.HasAnchor = true; }   // a bit down the barrel line: the muzzle reference for the tracer's teardrop axis
            if (b.Tracer != null) { GetTree().CurrentScene?.AddChild(b.Tracer); UpdateTracer(b); }
            if (g?.Action == "Rocket") b.RocketVis = SpawnRocketVis(pos);   // launcher: the rocket is a VISIBLE flying projectile, not an invisible bullet
            _bullets.Add(b);
        }

        /// <summary>Test seam: is the newest bullet in flight flagged as somebody else's? The ONLY way to check
        /// that the NPC path is wired end to end -- a test that calls NpcHeli.NpcShot with its own stub proves the
        /// AI pulls the trigger and nothing whatsoever about what the trigger is connected to.</summary>
        public bool DebugNewestBulletIsNpc => _bullets.Count > 0 && _bullets[^1].Npc;
        /// <summary>Test seam: the newest bullet's falloff start, which comes from the FIRING gun. If the NPC
        /// path is not passing srcGun this reads back the player's held weapon instead.</summary>
        public float DebugNewestBulletFalloff => _bullets.Count > 0 ? _bullets[^1].FalloffStart : -1f;

        /// <summary>Test seam: put a real bullet in flight. Deliberately only FIRES -- StepBullets still
        /// does the raycast and decides what was hit and for how much, so a test using this exercises the
        /// production damage dispatch instead of standing in for it.</summary>
        public void DebugFireBullet(Vector3 from, Vector3 dir, float damage = 40f)
            => SpawnBullet(from, dir.Normalized() * 300f, 60, 0f, damage, damage, damage);

        /// <summary>Third-person shot direction: the eyes aim at whatever the CAMERA is pointing at, so the crosshair
        /// still means something despite the camera sitting off to one side (UseableGun.cs:962-977).
        ///
        /// Two fallbacks, and they are not the same one. Nothing hit -> aim at a point 512 m down the camera axis, which
        /// keeps the shot parallel-ish to the view. Something hit but BEHIND the eyes (the dot test) -> leave the aim
        /// alone entirely; converging on it would swing the muzzle backwards through the player to shoot at a wall the
        /// camera can see and the character cannot.</summary>
        Vector3 ThirdPersonAim(Vector3 eyes, Vector3 fallback)
        {
            if (_cam == null) return fallback;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return fallback;
            Vector3 camPos = _cam.GlobalPosition, camFwd = -_cam.GlobalBasis.Z;
            const float Reach = 512f;
            var q = PhysicsRayQueryParameters3D.Create(camPos, camPos + camFwd * Reach,
                (1u << 0) | (1u << 1) | (1u << 4) | (1u << 5) | (1u << 6));   // what a bullet would stop on (no water)
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(q);
            Vector3 target;
            if (hit.Count > 0)
            {
                target = hit["position"].AsVector3();
                if ((target - eyes).Dot(camFwd) <= 0f) return fallback;   // it is behind us -- do not turn round and shoot it
            }
            else target = camPos + camFwd * Reach;
            var dir = target - eyes;
            return dir.LengthSquared() < 1e-6f ? fallback : dir.Normalized();
        }

        /// <summary>Test seam (UG_TRACERANGLE firetest): fire a tracer at an angle ACROSS the view so the stretched
        /// streak is seen side-on. First-person centred fire is end-on, which foreshortens the stretch to a dot.</summary>
        public void DebugFireAngled(float yawDeg)
        {
            if (_cam == null) return;
            var basis = _cam.GlobalTransform.Basis;
            SpawnBullet(_cam.GlobalPosition, (-basis.Z) * 90f, 220, 0f, 40f, 40f, 40f);   // DEAD forward -> tracer streaks muzzle -> down-range aim (visible via the muzzle offset)
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
                // (sim-zombie analytic bullet path removed 2026-08-25 -- master: rip out everything zombie)
                if (hit.Count > 0)
                {
                    if (b.Cosmetic)
                    {   // MP: damage AND impact fx are the server's (ImpactFx/HitConfirmed). But the tracer must still
                        // FOLLOW the server's round through a pierceable surface -- stopping it at the waterline while
                        // the authoritative bullet carries on is a visible desync of the one thing the client owns here.
                        if (PierceCost(hit["collider"].As<GodotObject>(), b, out float _cvk, out float _cdk))
                        {
                            Pierce(b, hit["position"].AsVector3(), _cvk, _cdk);
                            if (--b.StepsLeft <= 0) RemoveBullet(i);
                            continue;
                        }
                        RemoveBullet(i); continue;
                    }
                    Vector3 point = hit["position"].AsVector3();
                    Vector3 hdir = b.Vel.Normalized();
                    var collider = hit["collider"].As<GodotObject>();
                    if (collider is ZombieBody zb) { SpawnFleshImpact(point, hdir); bool wd = zb.Dead; zb.Damage(b.Damage * b.FalloffAt(point), point - hdir * 2f); if (!wd && zb.Dead) Kills++; Hitmark(b, false); }   // zombie: flesh spray + hitmarker
                    else if (collider is AnimalAgent a && !a.Dead) { SpawnFleshImpact(point, hdir); a.DamageHit(b.Damage * b.FalloffAt(point), point, hdir); Hitmark(b, false); }   // wildlife: flesh spray + body hitmarker (no limb zones)
                    else if (collider is TreeTrunk tt && !tt.Felled) { tt.Chop(b.Damage * b.FalloffAt(point), point, hdir); SpawnSurfaceImpact(point, hit["normal"].AsVector3(), Surf.Wood, tt); }   // chop a tree with gunfire -> wood splinters
                    else if (collider is TargetDummy dummy)
                    {   // playground target: PLAYER damage through the humanoid zones, floating number, hitmarker
                        float dealt = dummy.TakeHit(b.PlayerDamage * b.FalloffAt(point), point);
                        SpawnFleshImpact(point, hdir);
                        Hitmark(b, dummy.LastZone == TargetDummy.HitZone.Head);
                    }
                    else if (collider is PhysicalBone3D pb) { SpawnFleshImpact(point, hdir); pb.ApplyImpulse(hdir * 7f, point - pb.GlobalPosition); }
                    else if (collider is Vehicle veh)
                    {
                        // A helicopter routes the hit by WHERE it landed: the hub boxes at each mast are the
                        // rotors' bullet colliders, everything else is airframe. Rotor damage does NOT also
                        // damage the hull -- shooting a tail rotor off should ground the machine by breaking
                        // the thing that keeps it straight, not by chipping away at its HP.
                        // GLASS FIRST: a round through a window breaks the pane and stops there. Resolved from the
                        // impact point rather than a per-pane collider -- the panes sit inside the hull's own
                        // collider, so adding six more bodies would put them in every physics query the car takes
                        // part in for a hit the bullet path can already locate.
                        int gpane = veh.ResolveHitGlass(point);
                        bool tookGlass = gpane >= 0 && veh.BreakGlass(gpane);
                        if (tookGlass)
                        {
                            // NOT an early return: this runs inside StepBullets' `for (i = _bullets.Count-1; ...)`,
                            // so returning would abandon every other bullet in flight this frame -- a shotgun would
                            // lose its remaining pellets the moment one pellet found a window.
                            GD.Print($"[glass] {veh.DisplayName} {Vehicle.GlassPaneDisplay(veh.GlassLabel(gpane))} shattered");
                        }
                        else
                        {
                            // LAMPS next, same idea and the same no-collider reason as the glass above. Tried
                            // after glass so a round through a window is never stolen by a lamp behind it; the
                            // two barely overlap in space, but the greenhouse is the bigger target and should win
                            // the tie. A lamp takes the round instead of the hull, so headlights are worth aiming
                            // at rather than just another way to chip HP.
                            int lamp = veh.ResolveHitLamp(point);
                            int tire = lamp >= 0 ? -1 : veh.ResolveHitTire(point);
                            if (lamp >= 0 && veh.BreakLamp(lamp))
                            {
                                GD.Print($"[lamp] {veh.DisplayName} {Vehicle.LampDisplay(veh.LampLabel(lamp))} shot out");
                            }
                            else if (tire >= 0 && veh.PopTire(tire))
                            {
                                // The tire eats the round like glass and lamps do. Wheels sit low and outboard,
                                // well away from the lamp lenses, so the order between them rarely decides
                                // anything -- but lamps are checked first because their tolerance is a flat
                                // radius while a tire's scales with the wheel, and a bus wheel's would otherwise
                                // reach up and swallow shots aimed at the headlight above it.
                                GD.Print($"[tire] {veh.DisplayName} {Vehicle.TireDisplay(tire, veh.TireCount)} blown out");
                            }
                            else
                            {
                                var part = veh.ResolveHitPart(point);
                                if (part == Vehicle.HeliPart.MainRotor) veh.DamageMainRotor(b.VehicleDamage);
                                else if (part == Vehicle.HeliPart.TailRotor) veh.DamageTailRotor(b.VehicleDamage);
                                else veh.TakeDamage(b.VehicleDamage);
                            }
                        }
                        // WHERE THE SHOT CAME FROM, not where it landed (strawberry: "it will track the position
                        // that you shot it from"). b.Origin is the muzzle this round actually left, which is the
                        // answer to "where were you standing" -- the impact point is on the aircraft itself and
                        // would have it turning to face its own hull. Recorded on ANY hit including the rotors,
                        // because shooting the tail off is still being shot at.
                        veh.NoteAttackedFrom(b.Origin);
                        SpawnSurfaceImpact(point, hit["normal"].AsVector3(), Surf.Metal, veh); HitmarkCircle(b);   // source Vehicle_Damage (35) + metal sparks, hole follows the car; circle hitmarker (master)
                    }
                    else if (collider is Deployable dep && !dep.IsWreck) { dep.TakeDamage(b.VehicleDamage); SpawnSurfaceImpact(point, hit["normal"].AsVector3(), Surf.Metal); HitmarkCircle(b); }   // gunfire damages a placed generator (metal sparks) -- Vehicle_Damage; circle hitmarker
                    else if (collider is Door bdoor) { bdoor.TakeDamage(b.VehicleDamage); SpawnSurfaceImpact(point, hit["normal"].AsVector3(), Surf.Wood); HitmarkCircle(b); }   // you can shoot a door open the hard way; circle hitmarker
                    else if (collider is Bed bbed) { bbed.TakeDamage(b.VehicleDamage); SpawnSurfaceImpact(point, hit["normal"].AsVector3(), Surf.Wood); HitmarkCircle(b); }   // circle hitmarker
                    else if (collider is GlassPane gpane) { gpane.TakeDamage(b.ObjectDamage); HitmarkCircle(b); }   // glass pane -> shatter; the pane's own Glass_0 shards ARE the impact (no surface burst)
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
                        // SHOOT THE BULB OUT (strawberry): a hit on the lens kills that lamp and leaves the post
                        // standing. Checked BEFORE the destructible routing and consuming the hit, so a bulb shot
                        // does not also chip the prop's health -- you are breaking the light, not the pole.
                        // `glassShot` rather than `bulbShot`: three different fixtures now claim a hit this way (lamp
                        // bulb, signal aspect, TV screen) and the flag means "the breakable part ate this shot".
                        bool glassShot = false;
                        if (collider is Node sln && sln.HasMeta(StreetLight.HitMeta)
                            && sln.GetMeta(StreetLight.HitMeta).As<StreetLight>() is StreetLight slamp
                            && IsInstanceValid(slamp) && slamp.IsBulbHit(point))
                        {
                            glassShot = slamp.ShootOutBulb();
                            sf = Surf.Metal;   // no glass surface in the impact set; metal reads closest for a fixture
                        }
                        // Same for a traffic signal, per ASPECT (strawberry: "add the ability to shoot out each light
                        // piece"). The meta is an array because one mast is two independently-timed heads; the first
                        // head whose lens bounds contain the impact owns the shot. A dead aspect stays dark while the
                        // head keeps cycling, so you can shoot out just the green and leave the rest working.
                        if (!glassShot && collider is Node tln && tln.HasMeta(TrafficLight.HitMeta))
                            foreach (var e in tln.GetMeta(TrafficLight.HitMeta).AsGodotArray())
                                if (e.As<TrafficLight>() is TrafficLight sig && IsInstanceValid(sig))
                                {
                                    int li = sig.LensHit(point);
                                    if (li < 0) continue;
                                    glassShot = sig.ShootOutLens(li);   // false if already dead -> the shot falls through to the mast's health
                                    sf = Surf.Metal;
                                    break;
                                }
                        // A TELEVISION's screen dies in ONE shot and leaves the cabinet standing (master: "make the
                        // tvs take 1 shot to destroy the visual screen +cone and a few to destroy the actual prop").
                        // Identical shape to the bulb, including the part that matters: ShootOutScreen returns false
                        // once the glass is already gone, so the FIRST bullet buys the screen and every one after it
                        // falls through to the cabinet's health. Without that the set would be bulletproof after one
                        // hit, which is the exact opposite of the ask.
                        if (!glassShot && collider is Node tvbn && tvbn.HasMeta(TVDevice.HitMeta)
                            && tvbn.GetMeta(TVDevice.HitMeta).As<TVDevice>() is TVDevice tvb
                            && IsInstanceValid(tvb) && tvb.IsScreenHit(point))
                        {
                            glassShot = tvb.ShootOutScreen();
                            sf = Surf.Metal;
                        }
                        // ...and the patient monitor, same contract. No IsScreenHit equivalent: its display is a flat
                        // overlay on a small prop, so any hit on the body kills it -- which is also why it must return
                        // false once dead, or the stand would go bulletproof after the first shot.
                        if (!glassShot && collider is Node hmbn && hmbn.HasMeta(HeartMonitor.HitMeta)
                            && hmbn.GetMeta(HeartMonitor.HitMeta).As<HeartMonitor>() is HeartMonitor hmb
                            && IsInstanceValid(hmb))
                        {
                            glassShot = hmb.ShootOutScreen();
                            if (glassShot) sf = Surf.Metal;
                        }
                        if (!glassShot && collider is Node dn && dn.HasMeta(DestructibleField.MetaKey))
                        {
                            // TOASTER (strawberry): the first shot it SURVIVES may throw bread out the top. Rolled
                            // BEFORE the damage call, because that is the moment we know the prop was still standing --
                            // in the loopback the health is server-owned and the alive bit mirrors back a tick later, so
                            // asking afterwards would read a stale answer on exactly the shot that matters.
                            if (dn.HasMeta(Toaster.HitMeta) && dn.GetMeta(Toaster.HitMeta).As<Toaster>() is Toaster tst
                                && IsInstanceValid(tst)) tst.OnShot();
                            NetDamageObject?.Invoke((int)dn.GetMeta(DestructibleField.MetaKey), b.ObjectDamage);
                            HitmarkerHUD.Instance?.ShowCircle();   // circle hitmarker: you hit a destructible prop (master)
                        }
                        SpawnSurfaceImpact(point, hit["normal"].AsVector3(), sf);
                    }
                    // An IMPACT BLAST if this round carries one -- driven by the gun's own .dat rather than by a
                    // hardcoded `Action == "Rocket"` branch with the radius and all three damages as literals. Same
                    // numbers for the launcher (they moved into launcher_rocket.dat unchanged), and any other round
                    // that wants to detonate now says so in data instead of adding a second copy of this branch.
                    // ...carried on the BULLET, not read off the live Gun -- see the Bullet fields.
                    if (b.BlastRadius > 0f)
                    {
                        Explode(point, b.BlastRadius, b.BlastZombieDamage, b.BlastPlayerDamage, b.BlastVehicleDamage);
                        GD.Print($"[blast] warhead detonated (r={b.BlastRadius})");
                    }
                    // WALLBANG. Checked AFTER the impact fx and damage above, so a pierced surface still splashes,
                    // sparks and takes its hit -- the round carries on behind it rather than the surface being
                    // ignored. StepsLeft is still spent here: piercing costs the round a step like any other, or a
                    // wallbang would hand it a free tick of extra range.
                    if (PierceCost(collider, b, out float _vk, out float _dk))
                    {
                        Pierce(b, point, _vk, _dk);
                        if (--b.StepsLeft <= 0) RemoveBullet(i);
                        continue;
                    }
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

        // surface materials for bullet impacts (a slice of the source EPhysicsMaterial set). Tagged on colliders via
        // SetMeta("surf", (int)Surf) -- terrain = Grass, vehicles = Metal, untagged (buildings/props) = Concrete.
        public enum Surf { Concrete, Grass, Dirt, Metal, Wood, Sand, Water }
        public const string SurfMeta = "surf";

        // WALLBANG (strawberry 2026-08-21: "projectile hits surface, loses x velocity and damage, hits behind").
        // A round punches through the water surface and through any prop collider tagged ThinMeta, arriving behind
        // it slower and weaker instead of stopping dead. Everything else still eats the round exactly as before --
        // this is opt-IN per surface, so an untagged wall is unchanged and the marking pass can land separately.
        public const string ThinMeta = "thin";   // set on a prop's collider body -> bullets wallbang through it
        const int MaxPierce = 2;                 // stop a round tunnelling an entire building; two surfaces is a wallbang, five is a bug
        const float WaterVelKeep = 0.45f, WaterDmgKeep = 0.50f;   // water is thick: a round entering the sea loses over half its speed
        const float ThinVelKeep  = 0.75f, ThinDmgKeep  = 0.70f;   // sheet metal / plywood barely slows it
        const float PierceExit   = 0.06f;        // metres past the hit point to resume from, so the same face cannot be re-hit next step

        /// <summary>WALLBANG test: may this round punch through what it just hit, and at what cost? Opt-in per
        /// surface via meta, so anything untagged returns false and behaves exactly as it did before.</summary>
        static bool PierceCost(GodotObject collider, Bullet b, out float velKeep, out float dmgKeep)
        {
            velKeep = 1f; dmgKeep = 1f;
            if (b.Pierced >= MaxPierce) return false;
            if (b.BlastRadius > 0f) return false;            // a warhead DETONATES on contact; it does not wallbang
            if (collider is not Node n) return false;
            if (n.HasMeta(ThinMeta)) { velKeep = ThinVelKeep; dmgKeep = ThinDmgKeep; return true; }
            if (n.HasMeta(SurfMeta) && (Surf)(int)n.GetMeta(SurfMeta) == Surf.Water) { velKeep = WaterVelKeep; dmgKeep = WaterDmgKeep; return true; }
            return false;
        }

        /// <summary>Move the round just past the surface it pierced, slowed and weakened. Damage is scaled on ALL
        /// four channels: scaling only Damage would leave a round that punched through a wall doing full damage to
        /// a vehicle or a prop behind it, which is the same bug the per-bullet warhead fields were added to fix.</summary>
        void Pierce(Bullet b, Vector3 point, float velKeep, float dmgKeep)
        {
            b.Pierced++;
            Vector3 dir = b.Vel.Normalized();
            b.Vel *= velKeep;
            b.Damage *= dmgKeep; b.PlayerDamage *= dmgKeep; b.VehicleDamage *= dmgKeep; b.ObjectDamage *= dmgKeep;
            b.Pos = point + dir * PierceExit;
            UpdateTracer(b);
        }
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
        // Ripped out + reimplemented as our own culling-fixed ImpactFx (master 2026-08-08). This stays the call-site entry.
        void SpawnSurfaceImpact(Vector3 point, Vector3 normal, Surf surf, Node3D attachTo = null)
            => ImpactFx.Spawn(GetTree().CurrentScene, point, normal, surf, attachTo);

        // The retail water splash (Effects/Impacts/water_static for a bullet hit, Effects/Explosions/water_0 for a blast).
        // Retail renders these on Unity's Standard shader (Specular) in CUTOUT mode (_Mode=1, _Cutoff 0.5, ZWrite on) --
        // i.e. a LIT, alpha-cutout billboard, not an additive/unshaded sprite. scale=1 = a bullet-impact plip (~10 droplets,
        // 45deg cone, 3-6 m/s); scale>=2 = an explosion column (tight upward jet, many fast droplets). Extracted params +
        // the pale water sprite (impact_water_static_0.png). Droplets shrink over life + tumble; VisibilityAabb guards the
        // fast particles from the auto-AABB frustum cull (same lesson as the rubble chips).
        void SpawnWaterSplash(Node scene, Vector3 point, float scale) => ImpactFx.WaterSplash(scene, point, scale);

        // Impact SOUND — each source impact effect carries its own audio (Effects/Impacts/<mat>/<mat>.mp3), extracted to WAV.
        // A 3D one-shot at the hit point, cached per surface. grass=foliage, dirt/sand=gravel, else same-named.
        static readonly System.Collections.Generic.Dictionary<Surf, AudioStream> _impactSnd = new System.Collections.Generic.Dictionary<Surf, AudioStream>();
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

        // The traveling tracer: a CROSSED QUAD (two perpendicular teardrop planes sharing the flight axis, so it reads solid
        // from ANY angle -- never edge-on flat) textured with the soft circle sprite, riding the bullet along its velocity.
        MeshInstance3D MakeTracer()
        {
            if (!_tracerTexTried)
            {
                _tracerTexTried = true;
                string p = ProjectSettings.GlobalizePath("res://content/tracer.png");
                if (!System.IO.File.Exists(p)) p = ProjectSettings.GlobalizePath("res://content/bullet.png");
                if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) _tracerTex = ImageTexture.CreateFromImage(img); }
            }
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(1.9f, 0.85f, 0.2f),    // ORANGE HDR -> additive orange glow, blooms warm (R>G>>B keeps it orange, not white)
            };
            if (_tracerTex != null) mat.AlbedoTexture = _tracerTex;
            return new MeshInstance3D { Mesh = new ImmediateMesh(), MaterialOverride = mat };
        }

        // The tracer: a CROSSED QUAD (two perpendicular planes sharing the flight axis, so it reads solid from ANY angle --
        // never edge-on flat) whose geometry is a TEARDROP: round fat nose at the bullet, tapering to a point at the tail,
        // textured with the soft circle sprite. Rides the round along its velocity; starts at the muzzle while young.
        void UpdateTracer(Bullet b)
        {
            if (b.Tracer == null || b.Tracer.Mesh is not ImmediateMesh im) return;
            im.ClearSurfaces();
            const float MaxLen = 40f, tHead = 0.55f;   // teardrop: pointed tail (muzzle) -> round nose (bullet)
            float MaxW = b.TracerW;                    // per-CALIBER width (GunDef.TracerScale); was a flat 0.09 for every round
            Vector3 head = b.Pos;                                      // round fat nose = the bullet, leading
            Vector3 muzzle = b.HasAnchor ? b.MuzzleAnchor : b.Origin;
            Vector3 seg = head - muzzle;
            float dist = seg.Length();
            if (dist < 0.1f) { b.Tracer.Visible = false; return; }
            b.Tracer.Visible = true;
            Vector3 dir = seg / dist;                                  // muzzle->bullet axis: OFF the view axis, so the crossed quad never sits fully edge-on
            float len = Mathf.Min(MaxLen, dist);
            Vector3 tail = head - dir * len;                           // pointed tail toward the muzzle
            // two FIXED perpendicular axes -> the crossed quad (camera-independent, so it never collapses edge-on)
            Vector3 aux = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
            Vector3 perp1 = dir.Cross(aux).Normalized();
            Vector3 perp2 = dir.Cross(perp1).Normalized();
            const int N = 16;
            for (int plane = 0; plane < 2; plane++)
            {
                Vector3 perp = plane == 0 ? perp1 : perp2;
                im.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
                for (int i = 0; i <= N; i++)
                {
                    float t = (float)i / N;                            // 0 = tail point, 1 = nose tip
                    float w = t < tHead
                        ? MaxW * (t / tHead)                                                            // taper up from the pointed tail
                        : MaxW * Mathf.Sqrt(Mathf.Max(0f, 1f - ((t - tHead) / (1f - tHead)) * ((t - tHead) / (1f - tHead))));  // round nose cap
                    Vector3 p = tail.Lerp(head, t);
                    Vector3 off = perp * w;
                    im.SurfaceSetUV(new Vector2(t, 0f)); im.SurfaceAddVertex(p - off);
                    im.SurfaceSetUV(new Vector2(t, 1f)); im.SurfaceAddVertex(p + off);
                }
                im.SurfaceEnd();
            }
        }

        // Flesh impact — the REAL source Flesh_Dynamic effect (impact ID 5), extracted texture + params: a 16-particle
        // burst of the 4-frame blood sprite, size 0.5-1.0m, 3-6 m/s, gravityModifier 1, ~1s life, sprayed back out of the
        // wound (-dir). One-shot GpuParticles3D at the world hit point, auto-freed. (Was a flat-red placeholder quad @ 24
        // particles / 0.1m — now the real blood texture at source counts/sizes.)
        void SpawnFleshImpact(Vector3 point, Vector3 dir) => ImpactFx.Blood(GetTree().CurrentScene, point, dir);

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

        // The grass-shader globals + their data texture live in GrassDisplacers (registered there BEFORE any grass
        // material is built -- see GrassDisplacers.EnsureGlobals; registering them AFTER a material links them invalid
        // ("removed at some point"), which silently kills ALL grass displacement). This just keeps the gather buffer.
        static System.Collections.Generic.List<(float d2, Vector3 pos, float r)> _dispScratch;
        static Vector3 _grassSmooth; static bool _grassSmoothInit;   // the grass point's OWN smoothing (master): lerp toward the player each frame so the flatten glides instead of stepping

        /// <summary>Drive the grass-displacement shader each frame: retail's local-player point at (x, y+0.5, z) exactly
        /// as GrassDisplacement.cs, plus the master extension -- the nearest extended displacers (vehicles, dropped
        /// items, remote players) packed into the data texture. The +0.5 is the source's own offset (reads as a shin
        /// pushing through the blades, not the ground shoving them from below).</summary>
        void UpdateGrassDisplacement(double delta)
        {
            GrassDisplacers.EnsureGlobals();   // idempotent; grass materials already did this at build -- belt-and-suspenders (+ owns DispImg/DispTex)
            // TARGET = the player's interpolated visual position when the render-interp is active, else GlobalPosition.
            var pTarget = (_interpReady && !_dead && _driving == null && _ridingTrain == null && _ridingCrane == null)
                ? _interpPrev.Lerp(_interpCurr, (float)Engine.GetPhysicsInterpolationFraction())
                : GlobalPosition;
            // GRASS'S OWN INTERP (master 2026-08-25 "does it need its own interp?"): the loopback path can still feed a
            // stepped position, so smooth HERE -- lerp the grass point toward the target each frame (frame-rate-
            // independent) so the flatten + wake glide instead of ticking at 50Hz. One vector lerp/frame; local to grass.
            // Snap on a big jump (respawn/teleport) so the point doesn't slide across the map.
            if (!_grassSmoothInit || _grassSmooth.DistanceSquaredTo(pTarget) > 100f) { _grassSmooth = pTarget; _grassSmoothInit = true; }
            else _grassSmooth = _grassSmooth.Lerp(pTarget, 1f - Mathf.Exp(-25f * (float)delta));
            var p = _grassSmooth;
            // RETAIL: the local player, one point at (x, y+0.5, z), w unused -- exactly GrassDisplacement.cs.
            RenderingServer.GlobalShaderParameterSet(GrassDisplacers.PointParam, new Vector4(p.X, p.Y + 0.5f, p.Z, 0f));
            var wd = WindField.WindXZ(p);   // FOLIAGE WIND SWAY: xy = direction, z = strength at the player (a representative gust for the whole view)
            RenderingServer.GlobalShaderParameterSet(GrassDisplacers.WindParam, new Vector4(wd.X, wd.Y, WindField.SampleWind(p), 0f));

            // WAKE (master): the local player + moving vehicles leave a fading flattened trail. Age the trail + drop the
            // player's breadcrumb here; the gather below drops vehicle breadcrumbs + adds the whole fading trail as texels.
            ulong nowMs = Time.GetTicksMsec();
            GrassDisplacers.AgeWake(nowMs);
            GrassDisplacers.DropWake(GetInstanceId(), p, GrassDisplacers.PlayerWakeRadius, nowMs);

            // EXTENDED DISPLACERS (master): gather the grass_displacer group (vehicles, dropped items, remote players)
            // within grass render range, keep the nearest Max to the camera, and pack (world pos + radius) into the
            // data texture. Grass renders only within ~160m (FoliageField CullRange), so anything past it is skipped.
            _dispScratch ??= new System.Collections.Generic.List<(float, Vector3, float)>();
            _dispScratch.Clear();
            const float range = 5f * 32f;                 // = FoliageField CullRange (retail's ULTRA foliage draw distance)
            float range2 = range * range;
            var live = GrassDisplacers.Live;   // PERF: C#-side registry (see GrassDisplacers) -- one GlobalPosition read per displacer, no group marshalling
            for (int li = live.Count - 1; li >= 0; li--)
            {
                var e = live[li];
                if (!GodotObject.IsInstanceValid(e.Node)) { live.RemoveAt(li); continue; }   // freed (picked-up item, despawned car, left player)
                if (!e.Node.IsInsideTree()) continue;
                var gp = e.Node.GlobalPosition;
                float dx = gp.X - p.X, dz = gp.Z - p.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 > range2) continue;                // out of grass render range -> displaces nothing visible
                float r = e.Radius;
                _dispScratch.Add((d2, gp, r));
                if (r > 1.0f) GrassDisplacers.DropWake(e.Id, gp, r, nowMs);   // vehicles (big r) leave a wake; items + remote players (small r) don't
            }
            GrassDisplacers.GatherWake(_dispScratch, p, range2, nowMs);   // add the fading wake breadcrumbs as extra (weaker, shrinking) texels behind the movers
            _dispScratch.Sort(static (a, b) => a.d2.CompareTo(b.d2));   // nearest first -> the Max that survive are the ones the player can actually see
            int cnt = System.Math.Min(_dispScratch.Count, GrassDisplacers.Max);
            // Upload ONLY when the packed texel set changed (idle = nothing moves = no upload). Uploading every frame was
            // both a per-frame GPU update for nothing and, under the separate render thread, a texture update racing the
            // renderer (4 "empty image" errors per load).
            bool dispChanged = _dispPrev == null || _dispPrev.Count != cnt;
            if (!dispChanged) for (int i = 0; i < cnt; i++) if (_dispPrev[i] != _dispScratch[i]) { dispChanged = true; break; }
            if (dispChanged)
            {
                _dispPrev ??= new System.Collections.Generic.List<(float d2, Vector3 pos, float r)>();
                _dispPrev.Clear();
                for (int i = 0; i < cnt; i++)
                {
                    var e = _dispScratch[i];
                    _dispPrev.Add(e);
                    GrassDisplacers.DispImg.SetPixel(i, 0, new Color(e.pos.X, e.pos.Y, e.pos.Z, e.r));   // stale tail texels beyond cnt are never read (loop is count-bounded)
                }
                GrassDisplacers.DispTex.Update(GrassDisplacers.DispImg);   // re-upload the mutated texels; the global sampler still points at this same RID
            }
            RenderingServer.GlobalShaderParameterSet(GrassDisplacers.CountParam, cnt);
        }

        // Kept as real overrides so direct calls keep working (NetTests: `p._Process(0.016)` steps the ride cam synchronously);
        // the engine never invokes them because _Ready turns processing off on this node (the TickProxy child ticks it).
        // Dropping the overrides silently turned those calls into Node's empty base method -> net.ride_freelook regressed.
        public override void _Process(double delta) => ProcessTick(delta);
        public override void _PhysicsProcess(double delta) => PhysicsTick(delta);
        public void ProcessTick(double delta)   // PERF: engine callback taken by a TickProxy child (see TickProxy); body unchanged
        {
            if (NetAvatar) return;   // per-frame work is all client-side (render interp, look focus, recoil drain, cam) -- none of it on a server avatar
            // R-HOLD ammo radial (shotguns): open the picker once R is held past the threshold (frees the mouse so its
            // cursor angle selects a wedge). PlayerController owns the close + mouse recapture, so a gun swap while it's
            // up can't strand the freed cursor.
            if (_magSwapAnimTimer > 0)   // mag-pie swap anim playing: clear the viewmodel's reload state when it ends so ADS/fire work again (master's ADS bug)
            {
                _magSwapAnimTimer -= delta;
                if (_magSwapAnimTimer <= 0)
                {
                    _magSwapAnimTimer = 0;
                    _viewmodel?.SetReloading(false);
                    if (_magSwapAutoRack)   // seated into an empty chamber -> rack the first round automatically (master); PlayHammer self-times + blocks ADS through the rack
                    {
                        _magSwapAutoRack = false;
                        _viewmodel?.PlayHammer(Skills.DexterityReloadSpeed());
                    }
                    else if (Input.MouseMode == Input.MouseModeEnum.Captured && Keybinds.Pressed(GameAction.Aim) && HasGunOut && _melee == null)
                        _viewmodel?.SetAiming(true);   // resume ADS if RMB is still held when the anim finishes
                }
            }
            if (AmmoRadial != null && AmmoRadial.IsOpen && (!CanOpenAmmoPie || !HasGunOut))
            { AmmoRadial.Close(); Input.MouseMode = Input.MouseModeEnum.Captured; _rHolding = false; }
            else if (_rHolding && CanOpenAmmoPie && HasGunOut && !_reloading && !_unloading && _magSwapAnimTimer <= 0
                     && (AmmoRadial == null || !AmmoRadial.IsOpen)
                     && Time.GetTicksMsec() - _rHeldSince >= AmmoRadialHoldMs)
            {
                if (AmmoRadial != null) { AmmoRadial.Open(this); if (AmmoRadial.IsOpen) Input.MouseMode = Input.MouseModeEnum.Visible; else _rHolding = false; }
                else _rHolding = false;
            }
            // Source kills the held light on unequip (UseableMelee -> player.disableItemSpotLight()). There are
            // EIGHT places that drop the held melee and more will appear, so this is DERIVED from what's in hand
            // rather than cleared at each of them -- patching all eight is how the ninth ends up leaving a torch
            // burning in your pocket. Costs one bool test per frame and cannot go stale.
            if (_heldLightOn && !HoldingLight) { _heldLightOn = false; ApplyHeldLight(); }
            if ((_grassT += delta) >= 1.0 / 60.0) { UpdateGrassDisplacement(_grassT); _grassT = 0; }   // PERF: 60 Hz -- the lerp takes the accumulated delta, the bend is identical
            if (_interpReady && !_dead && _driving == null && _ridingTrain == null && _ridingCrane == null)   // RENDER INTERPOLATION (master): lerp the visual position between the last two 50Hz ticks so it doesn't step at 50Hz while rendering at 60+
                GlobalPosition = _interpPrev.Lerp(_interpCurr, (float)Engine.GetPhysicsInterpolationFraction());
            if (_driving != null && !_dead)   // driving: position the cam from the vehicle's Godot-INTERPOLATED visual transform, so cam + car mesh are both smooth + IN SYNC (master: godot smoothing for the car)
                PositionDriveCam(_driving.GetGlobalTransformInterpolated());
            if (_ridingTrain != null && !_dead && _cam != null)   // riding a train: 1P cab (H=fp) or 3P chase behind the loco (mouse-orbited)
            {
                if (_fp) _cam.GlobalTransform = _ridingTrain.DriverEyeWorld;   // cab, looking forward down the rail
                else
                {
                    var lt = _ridingTrain.Loco != null ? _ridingTrain.Loco.GetGlobalTransformInterpolated() : _ridingTrain.GlobalTransform;
                    var tfwd = -lt.Basis.Z; tfwd.Y = 0f; tfwd = tfwd.LengthSquared() > 0.001f ? tfwd.Normalized() : Vector3.Forward;
                    float tdist = 18f, tpitch = Mathf.DegToRad(_driveCamPitch);   // long loco -> hold the cam well back
                    Vector3 tdir = new Basis(Vector3.Up, Mathf.DegToRad(_driveCamYaw)) * (-tfwd);   // behind the heading, mouse-orbited
                    var teye = lt.Origin + tdir * (tdist * Mathf.Cos(tpitch)) + Vector3.Up * (tdist * Mathf.Sin(tpitch) + 4f);
                    _cam.GlobalTransform = new Transform3D(Basis.Identity, teye).LookingAt(lt.Origin + Vector3.Up * 2.5f, Vector3.Up);
                }
            }
            if (_ridingCrane != null && !_dead && _cam != null)   // riding a crane: 3P chase orbit, held well back (the gantry is big)
            {
                var ct = _ridingCrane.GetGlobalTransformInterpolated();   // INTERPOLATED visual transform, not raw physics -> cam + gantry both smooth + IN SYNC (like the train/car cam); fixes the gantry jitter
                var cfwd = -ct.Basis.Z; cfwd.Y = 0f; cfwd = cfwd.LengthSquared() > 0.001f ? cfwd.Normalized() : Vector3.Forward;
                float cdist = 62f, cpitch = Mathf.DegToRad(_driveCamPitch);
                Vector3 cdir = new Basis(Vector3.Up, Mathf.DegToRad(_driveCamYaw)) * (-cfwd);
                var ceye = ct.Origin + cdir * (cdist * Mathf.Cos(cpitch)) + Vector3.Up * (cdist * Mathf.Sin(cpitch) + 20f);
                _cam.GlobalTransform = new Transform3D(Basis.Identity, ceye).LookingAt(ct.Origin + Vector3.Up * 10f, Vector3.Up);
            }
            if (_riding != null && !_dead && IsInstanceValid(_riding))   // C6 riding: chase the dead-reckoned puppet (it moves per-FRAME in VehicleReplicaView, no physics interp to sample)
                PositionRideCam(_riding.GlobalTransform);
            OutlineOverlay.DrivingSuppress = _driving != null || _riding != null;   // in a vehicle: nothing focusable -> kill the outline overlay's per-frame 2nd cull + dilate (the 3p-cam POI fps drop, strawberry)
            if ((_lookFocusT += delta) >= 1.0 / 30.0) { _lookFocusT = 0; UpdateLookFocus(); }   // PERF: 30 Hz is plenty for a highlight/prompt (was every frame: a ray + a sphere query + a marshalled vehicles group at 450 fps)   // eye-ray -> focus the item you're aiming at
            UpdateWireLook();                                                                 // wire tool: look at a connection cube -> highlight + info readout
            UpdateHoseLook();                                                                 // hose tool: look at a fluid port -> highlight + info + drive the route preview
            UpdateRopeLook();                                                                 // rope tool: look at a vehicle tow node -> highlight + drive the tie preview
            UpdateRopeManage((float)delta);                                                   // rope tool: poke a roped node -> hold RMB clear / tap RMB disconnect (mirrors the wire tool)
            UpdateWireManage((float)delta);                                                   // wire tool: poke a wired port -> hold RMB clear / tap RMB unplug
            UpdateHoseManage((float)delta);                                                   // hose tool: poke a hosed port -> hold RMB clear / tap RMB unplug (mirror)
            UpdateWireArrows();                                                               // wire tool: show in/out arrows on every connection point (blue avail / red occupied)
            UpdateHoseArrows();                                                               // hose tool: show in/out arrows on every fluid port (mirror)
            if (_showLookHulls) UpdateLookHullViz();                                          // I-toggle: rebuild the look-hull wireframes
            UpdateSalvage((float)delta);   // wreck salvage prompt + blowtorch teardown
            UpdateDeployPickup((float)delta);   // hold-F to pick a placed deployable back up (its wires disconnect)
            UpdateFluidPickup((float)delta);    // hold-F to pick a placed fluid device back up (its hoses/power wire disconnect)
            UpdateDoorLockHold((float)delta);   // hold-F on a door you own to lock/unlock it (a tap opens/closes)
            if (_barricadedDoorMsg > 0f) { _barricadedDoorMsg -= (float)delta; if (_fHeldFluid == null && _fHeldDoor == null) FluidPickupHudSet("Door is barricaded"); }   // re-assert AFTER UpdateFluidPickup (which blanks the shared HUD each idle frame) so the "can't open" line stays up for its window
            UpdateFluidContainerHud((float)delta);   // held fluid container: show its contents + [LMB] sip / [RMB] fill hint (strawberry)
            if (HoldingFisher) TickFishing((float)delta);   // rod out: charge gauge + bite timer + bobber flight/bob + line

            // Additive recoil (master): drain the pending kick INTO the real aim over a couple frames (a smooth climb),
            // then leave it there -- the view stays kicked up and the player pulls the mouse back down. Never recovers on its own.
            if (_patternShot > 0)
            {
                _patternIdle += (float)delta;
                if (_patternIdle >= PatternResetSeconds) { _patternShot = 0; _patternIdle = 0f; }
            }
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
            // SCOPE SWAY, and it moves the CAMERA -- which is to say the aim -- not the viewmodel (strawberry:
            // "with scope sway, have it move the WHOLE camera (and thus aim point) instead of just the scope
            // viewmodel"). It rides `_pitchDeg` and the body yaw for the same reason recoil does: those two are
            // the single source both the camera (`_cam.RotationDegrees`) and the firing basis read, so aim and
            // view cannot drift apart. Sway the viewmodel instead and the crosshair wanders while the bullet
            // doesn't, which is the bug being fixed.
            //
            // Applied as a DELTA against what it applied last frame, so it oscillates and unwinds instead of
            // accumulating. Recoil accumulates on purpose and never returns; sway must return, and sharing
            // `_pitchDeg` with something that doesn't is only safe because of this bookkeeping.
            //
            // The oscillator itself is the Viewmodel's -- see below. This block only decides where its output
            // LANDS, which is the whole of strawberry's correction: on the camera, not on the arms.
            {
                // ONE oscillator, and it is the Viewmodel's. It already carries the source's amplitude
                // (1 - 1/zoom, exactly 0 at 1x so irons and red dots get none of it from the formula rather than
                // a special case), the stance scaling and the SteadyAccuracy breath term. Duplicating that here
                // with my own sines -- which is what the first cut of this did -- gives two swings of different
                // shape and silently doubles the amplitude.
                Vector2 sway = _viewmodel?.ScopeSwayDegrees ?? Vector2.Zero;
                if (DebugForceScopeSway)   // headless: no SubViewport optic, so synthesise the same shape
                {
                    // Scaled by the SAME per-gun ScopeSwayScale the real oscillator uses (Viewmodel line ~1289).
                    // Without this the synthetic amplitude is a constant, so a test driving this path measures an
                    // identical swing for every gun -- its PASS would look exactly like its FAILURE, which is how
                    // the 0.3 on the AUG/SG550 shipped broken in the first place. It still does NOT cover the real
                    // oscillator's zoom/stance/breath terms; only that the gun's number arrives and scales.
                    _scopeSwayT += (float)delta;
                    float ss = _viewmodel?.ScopeSwayScale ?? 1f;
                    sway = new Vector2(Mathf.Sin(_scopeSwayT * 3.33f) * 0.30f * ss, Mathf.Sin(_scopeSwayT * 1.95f + 1.3f) * 0.42f * ss);
                }
                float tgtP = sway.X, tgtY = sway.Y;
                if (tgtP != _swayAppliedP || tgtY != _swayAppliedY)
                {
                    _pitchDeg = Mathf.Clamp(_pitchDeg + (tgtP - _swayAppliedP), -89f, 89f);
                    RotateY(Mathf.DegToRad(tgtY - _swayAppliedY));
                    _swayAppliedP = tgtP; _swayAppliedY = tgtY;
                }
            }
            PainAlpha = Mathf.Max(0f, PainAlpha - (float)delta);                 // hurt flash fades at 1/s (PlayerUI line 1835)
            // flinch recovers to level at 4/s (PlayerLook line 1330). GUARD: a degenerate hit can leave _flinch NaN or
            // denormalized, and Godot's Slerp/Basis assert IsNormalized -> that was the "Quaternion is not normalized" spam.
            if (!_flinch.IsFinite() || _flinch.LengthSquared() < 1e-6f) _flinch = Quaternion.Identity;
            _flinch = _flinch.Normalized().Slerp(Quaternion.Identity, 4f * (float)delta);
            ApplyLean((float)delta);
            // The eye height is lerped whether or not the first-person camera is the one being drawn: in third person
            // nothing reads _cam.Position any more, but the BULLETS still come out of the eyes, so it has to keep up.
            _eyeHeight = Mathf.Lerp(_eyeHeight, EyeHeight, Mathf.Min(1f, 4f * (float)delta));
            // Exponential decay, not MoveToward: a linear catch-up arrives with a visible corner where it stops,
            // which is the same abruptness in a different place. This eases out.
            _stepSmooth = Mathf.MoveToward(_stepSmooth, 0f, StepSmoothRate * (float)delta);
            if (_cam != null && !_dead && _driving == null && _riding == null && _ridingTrain == null && _ridingCrane == null)   // while driving/riding, the drive cam above owns the view
            {
                if (_ugFp) _fp = true;   // render harness (UG_FP=1): force 1st-person so the FP viewmodel is captured
                if (_fp)
                {
                    // FP: the camera SITS at the eyes (PlayerLook.heightLook 1.75/1.2/0.35, lerped 4/s), pitched by the mouse
                    _cam.Position = new Vector3(0f, _eyeHeight - _stepSmooth, 0f);   // sit where the eyes WERE, catching up over ~0.13 s
                    var look = Basis.FromEuler(new Vector3(Mathf.DegToRad(_pitchDeg), 0f, 0f), EulerOrder.Yxz);   // flinch left-multiplies the look
                    _cam.Basis = new Basis(_flinch) * look;
                }
                else
                {
                    StepThirdPersonCam((float)delta);
                }
            }
            UpdateBody(delta);
        }

        // live 3rd-person body: shown when !_fp; stands at the player (facing the body yaw, animated by ground speed) or sits in the driver seat
        void UpdateBody(double delta)
        {
            if (_viewmodel != null)
            {
                _viewmodel.SetShown(_fp && _driving == null && _riding == null && !_dead);   // FP gun arms: first-person on foot only
                _viewmodel.LeanRoll = _leanAngle;   // 1P lean tilt: hand the already-lerped/obstruct-snapped roll to the viewmodel (its SubViewport can't inherit the camera pivot's roll)
            }
            if (_body == null) return;
            _body.Visible = !_fp && !_dead;   // dead -> the corpse ragdoll handles the body
            if (_fp || _dead) { return; }
            if (_driving != null)   // in the driver seat (best-effort idle pose)
            {
                // The seat you are ACTUALLY in (strawberry 2026-08-16: "make the different seats actually move the
                // player's seated position") -- previously every occupant was drawn in the driver's seat, so
                // switching seats moved the camera and left the body behind the wheel.
                // INTERPOLATED, not the raw physics transform (strawberry: "apply interp to the seated player
                // position"). The vehicle is a rigid body stepped at the physics rate; reading GlobalTransform
                // here samples whatever the last physics tick left, so at any framerate above the physics rate
                // the body sat still for some frames and jumped on others -- while the car MESH beside it was
                // being interpolated by Godot and moving smoothly. The occupant juddered against his own
                // vehicle.
                //
                // The driving camera already reads the interpolated transform for exactly this reason. Using
                // the same source here is what puts the body, the car and the view in one frame of reference
                // rather than two.
                _body.GlobalTransform = _driving.GetGlobalTransformInterpolated() * new Transform3D(Basis.Identity, _driving.SeatBodyLocal(_seatIndex));
                // The DRIVER mimes a wheel; a passenger must not, or the back seats all sit there steering an
                // invisible car -- and it reads worse now they are holding a rifle while doing it.
                _body.PlayLoop(_seatIndex == 0
                    ? (_body.ClipLength("Idle_Drive") > 0f ? "Idle_Drive" : "Idle_Sit")
                    : (_body.ClipLength("Idle_Sit") > 0f ? "Idle_Sit" : "Idle_Drive"));
            }
            else if (_riding != null && IsInstanceValid(_riding))   // C6: same seated pose on the replicated puppet's seat
            {
                _body.GlobalTransform = _riding.GlobalTransform * new Transform3D(Basis.Identity, _riding.SeatOffset);
                _body.PlayLoop(_body.ClipLength("Idle_Drive") > 0f ? "Idle_Drive" : "Idle_Sit");
            }
            else   // on foot: at the player's feet, facing the body yaw, locomotion by horizontal speed
            {
                _body.GlobalPosition = GlobalPosition;
                _body.Rotation = new Vector3(0f, Rotation.Y, 0f);   // yaw only -- the LEAN goes on the spine, not here
                // The character leans too, or the muzzle every 3P effect is sourced from stays bolt upright while the
                // camera tilts away from it. Fed the same smoothed angle the camera rides, so body and view agree.
                _body.LeanDeg = _leanAngle;
                // ...and pitches with the look, or a character aiming at the sky stands there level (master: "can we
                // get pitch tilt for the spine when aiming up/down in 3p"). Retail feeds the raw look pitch and lets
                // HumanAnimator split it half to the spine, half to the skull.
                _body.PitchDeg = _pitchDeg;
                _body.SetLocomotion(new Vector2(Velocity.X, Velocity.Z).Length(), Stance);   // crouch/prone anims by stance (master)
            }
            UpdateBodyGun();   // attach + pose the held gun on the 3P body (detaches when driving/dead/unarmed)
            _body.Tick(delta);
        }

        // 3P held gun: attach the gun mesh + drive the upper-body gun layer (equip/hold/reload + ADS blend) on the live
        // body, so in 3rd person you see yourself holding + animating the weapon over a walking lower body. The gun clips
        // play ONLY on the arms/spine (RiggedCharacter's overlay), so the legs keep their locomotion.
        string _bodyGunName;                                   // gun currently attached to _body (null = unarmed)
        string _bodyAimClip, _bodyReloadClip, _bodyEquipClip, _bodyHammerClip;  // resolved per-gun clip names for the overlay
        bool _bodyReloading3p, _bodyHammer3p;                  // edge-detect the reload + the rack so each plays once
        void UpdateBodyGun()
        {
            // A PASSENGER keeps their gun on the 3rd-person body (strawberry: "passengers can hold weapons").
            // The old test was `_driving == null`, which stripped the gun from everyone aboard -- so a passenger
            // who could draw, aim and fire showed empty hands to everybody else.
            bool wantGun = HasGunOut && !IsDriver && _riding == null && !_dead;
            if (!wantGun)
            {
                if (_bodyGunName != null) { _body.DetachGun(); _body.DisableGunLayer(); _bodyGunName = null; _bodyReloading3p = false; }
                return;
            }
            if (_gunName != _bodyGunName)                       // just drew or swapped a gun
            {
                string capGun = char.ToUpper(_gunName[0]) + _gunName.Substring(1);
                _body.AttachGun(_gunName);
                MountBody3PAttachments();   // sights/scope/mag/barrel on the 3P gun, from the held item's installed ids
                _bodyAimClip    = _body.ClipLength(capGun + "_Aim")    > 0f ? capGun + "_Aim"    : "Gun_Aim";
                _bodyReloadClip = _body.ClipLength(capGun + "_Reload") > 0f ? capGun + "_Reload" : "Gun_Reload";
                _bodyEquipClip  = _body.ClipLength(capGun + "_Equip")  > 0f ? capGun + "_Equip"  : "Gun_Equip";
                _bodyHammerClip = _body.ClipLength(capGun + "_Hammer") > 0f ? capGun + "_Hammer" : null;   // rechamber rack (empty-reload 2nd half); null = gun has none, mirrors Viewmodel._hammerClip
                if (!_body.GunLayerOn) _body.EnableGunLayer(_bodyAimClip); else _body.RebakeAim(_bodyAimClip);
                _body.SetGunOverlay(_bodyEquipClip, 1f, loop: false);   // play the equip pull-out; it holds its end (the ready hold)
                _bodyGunName = _gunName;
                _bodyReloading3p = false; _bodyHammer3p = false;
            }
            if (_reloading && !_bodyReloading3p)                     // reload just started -> play it once at the real reload speed
                _body.SetGunOverlay(_bodyReloadClip, _reloadSpeed, loop: false);
            else if (!_reloading && _bodyReloading3p)               // reload finished -> snap back to the ready hold
                _body.SnapGunOverlay(_bodyEquipClip);
            _bodyReloading3p = _reloading;
            // The rack: after an EMPTY reload the mag swap finishes and _hammerActive goes true while the bolt cycles
            // (source: the reload's 2nd half). Play the body's {Gun}_Hammer clip once over it, mirroring the 1P; when
            // the rack ends _reloading also clears the same tick, so the snap-to-hold above returns the arms to ready.
            if (_hammerActive && !_bodyHammer3p && _bodyHammerClip != null)
                _body.SetGunOverlay(_bodyHammerClip, _reloadSpeed, loop: false);
            _bodyHammer3p = _hammerActive;
            _body.AimBlend = _viewmodel?.AimAlpha ?? 0f;            // ADS: same eased 0..1 the 1P arms use
        }

        // 3P attachments: mount the gun's installed sight/scope + magazine + barrel onto the 3P gun mesh, mirroring the
        // viewmodel attach loop (same meshes / hook positions / materials). Installed ids come off the held Item via
        // AttachmentFit; falls back to the gun's factory iron sight + default magazine when nothing's fitted.
        void MountBody3PAttachments()
        {
            var gv = Viewmodel.VisualForTest(_gunName);
            int sid = _heldItem != null ? AttachmentFit.InstalledId(_heldItem, "Sight") : 0;
            string sightTxt = sid > 0 ? AttachmentFit.MeshFor((ushort)sid) : gv.Sight;
            if (!string.IsNullOrEmpty(sightTxt) && ContentProvider.ParseObj($"res://content/{sightTxt}") is Mesh sm)
                _body.MountGunAttachment("Sight", sm, gv.SightPos != Vector3.Zero ? gv.SightPos : new Vector3(0f, 0.1312f, -0.118f), gv.SightColor.A > 0f ? gv.SightColor : new Color(0.3f, 0.3f, 0.3f));
            int mid = _heldItem != null ? AttachmentFit.InstalledId(_heldItem, "Magazine") : 0;
            string magTxt = mid > 0 ? AttachmentFit.MeshFor((ushort)mid) : gv.Mag;
            if (!string.IsNullOrEmpty(magTxt) && ContentProvider.ParseObj($"res://content/{magTxt}") is Mesh mm)
                _body.MountGunAttachment("Magazine", mm, new Vector3(0f, 0.0166f, 0.0238f), new Color(0.07f, 0.07f, 0.08f));
            int bid = _heldItem != null ? AttachmentFit.InstalledId(_heldItem, "Barrel") : 0;   // barrel only when one's fitted (guns ship bare)
            if (bid > 0 && AttachmentFit.MeshFor((ushort)bid) is string bt && ContentProvider.ParseObj($"res://content/{bt}") is Mesh bm)
                _body.MountGunAttachment("Barrel", bm, new Vector3(0f, 0.7307f, -0.0818f), new Color(0.05f, 0.05f, 0.055f));
        }

        // --- Vehicle enter/exit (source: InteractableVehicle). F enters the nearest vehicle's driver seat / exits. ---
        // Seat point to eye. The prefab seat empties sit at seat-PAN height (they are where the body goes), so a
        // camera placed at one looks out of the upholstery; DriverEyeLocal already bakes an equivalent rise in.
        const float PassengerEyeRise = 1.05f;
        int _seatIndex;   // which seat of _driving we occupy; 0 = driver. Passengers ride, they do not steer.
        /// <summary>Seat we occupy in the current vehicle, 0 = driver.</summary>
        public int SeatIndex => _seatIndex;
        /// <summary>Are we actually in control? Seat 0 only -- every other seat is a passenger, and a passenger
        /// pressing W must not drive the vehicle from the back seat.</summary>
        public bool IsDriver => _driving != null && _seatIndex == 0;
        /// <summary>Riding someone else's vehicle in a non-driver seat -- free to look around and use a weapon.</summary>
        public bool IsPassenger => _driving != null && _seatIndex != 0;
        public bool IsDriving => _driving != null;
        public Vehicle Driving => _driving;   // the vehicle being driven (for zombies to swipe at, source targetPassengerVehicle)
        public void SetSuppressor(bool on) => _viewmodel?.SetSlotAttached("Barrel", on);   // test hook: toggle the silenced barrel

        /// <summary>Is the shot I am about to fire suppressed -- by a fitted can OR by the gun being one? Single
        /// source of truth, because the two consumers must not disagree: a gun that is silent to zombies while
        /// still drawing a bright streak back to my position is worse than either behaviour alone.
        /// (strawberry: "hide tracers when using a suppressed weapon. pdw and matamorez are integrally
        /// suppressed, other guns can have the suppressed flag set via attachments".)</summary>
        /// <summary>How many live bullets currently carry a tracer. The tracer is the thing being suppressed, so
        /// this is what a test has to count -- "no streak on screen" and "no bullet at all" are the same picture
        /// otherwise.</summary>
        public int DebugTracerCount { get { int n = 0; foreach (var b in _bullets) if (b.Tracer != null) n++; return n; } }
        public int DebugBulletCount => _bullets.Count;

        public bool Suppressed => (_viewmodel?.IsSuppressed ?? false) || (Gun?.IntegrallySuppressed ?? false);
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
        /// <summary>Nearest boardable train's loco within reach (trains aren't Vehicles, so the vehicle finder
        /// misses them). Generous radius -- the loco is ~11m long, so the cab can sit several metres off centre.</summary>
        static float SegPointDistSq(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a; float t = ab.LengthSquared() > 1e-9f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            return (a + ab * t - p).LengthSquared();
        }

        Train NearestTrain()
        {
            Train best = null; float bestD = 10f * 10f;
            foreach (var n in GetTree().GetNodesInGroup("trains"))
                if (n is Train t && t.Loco != null)
                {
                    float d = GlobalPosition.DistanceSquaredTo(t.Loco.GlobalPosition);
                    if (d < bestD) { bestD = d; best = t; }
                }
            return best;
        }

        /// <summary>Board a train: hide + free the camera exactly as EnterVehicle does, but with no seat/MP
        /// bookkeeping -- a train is a lone spline-follower, so this ride path never touches the vehicle/MP logic.</summary>
        void BoardTrain(Train t)
        {
            _ridingTrain = t;
            if (Hud != null) Hud.Train = t;   // drive HUD: title + speedo box (master: vehicle UI on the train)
            t.SetOccupied(true);   // start the engine loop (base plays only while occupied, master)
            t.MarkBoarded();       // control from whichever engine the player looked at (master)
            if (_focusTrain != null) { if (IsInstanceValid(_focusTrain)) _focusTrain.SetLookFocused(false); _focusTrain = null; }   // drop the look-outline once aboard
            _driveCamYaw = 0f; _driveCamPitch = 15f;   // 3P chase starts squarely behind the loco
            _viewmodel?.SetShown(false);
            if (_cam != null) _cam.TopLevel = true;
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = true;
            Visible = false;
            Velocity = Vector3.Zero;
        }

        void ExitTrain()
        {
            var t = _ridingTrain; _ridingTrain = null;
            if (Hud != null) Hud.Train = null;   // hide the drive HUD box
            if (t != null) t.SetOccupied(false);   // stop the engine loops + horn (base only runs while occupied, master)
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = false;
            Visible = true;
            _viewmodel?.SetShown(true);
            if (_cam != null) _cam.TopLevel = false;
            if (t?.Loco != null) GlobalPosition = t.Loco.GlobalPosition + t.Loco.GlobalTransform.Basis.X * 3f + Vector3.Up * 1.5f;   // step out beside the cab
        }

        /// <summary>Advance the boarded train along its rail from W/S (no steering -- the spline steers), then
        /// ride the loco so the exit spot + camera track it.</summary>
        void DriveTrain(float delta)
        {
            bool ctrl = !UiInputBlocked && Input.IsKeyPressed(Key.Ctrl);
            bool w = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.W);
            bool sk = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.S);
            if (ctrl && w && !_jogWPrev) _ridingTrain.Jog(+1);   // Ctrl+W: advance EXACTLY one carriage forward
            if (ctrl && sk && !_jogSPrev) _ridingTrain.Jog(-1);  // Ctrl+S: back one carriage
            _jogWPrev = ctrl && w; _jogSPrev = ctrl && sk;
            float throttle = ctrl ? 0f : ((w ? 1f : 0f) - (sk ? 1f : 0f));   // plain W/S = continuous throttle; Ctrl held = jog only
            if (Mathf.Abs(throttle) > 0.01f) _ridingTrain.TryStartEngine();   // reaching for the throttle starts it, same as a car; self-gates so this is a no-op once running
            _ridingTrain.Drive(throttle, delta);
            if (_ridingTrain.Loco != null) GlobalPosition = _ridingTrain.Loco.GlobalPosition;
        }

        // ---- harbor crane ride path (master 2026-08-19): board/drive/exit, mirroring the train ----
        HarborCrane NearestCrane()
        {
            HarborCrane best = null; float bestD = 45f * 45f;   // the gantry is huge -> board from farther
            foreach (var n in GetTree().GetNodesInGroup("cranes"))
                if (n is HarborCrane c && IsInstanceValid(c))
                {
                    float d = GlobalPosition.DistanceSquaredTo(c.GlobalPosition);
                    if (d < bestD) { bestD = d; best = c; }
                }
            return best;
        }
        void BoardCrane(HarborCrane c)
        {
            _ridingCrane = c;
            _driveCamYaw = 0f; _driveCamPitch = 20f;
            _viewmodel?.SetShown(false);
            if (_cam != null) _cam.TopLevel = true;
            foreach (var ch in FindChildren("*", "CollisionShape3D", true, false))
                if (ch is CollisionShape3D cs) cs.Disabled = true;
            Visible = false; Velocity = Vector3.Zero;
        }
        void ExitCrane()
        {
            var c = _ridingCrane; _ridingCrane = null;
            foreach (var ch in FindChildren("*", "CollisionShape3D", true, false))
                if (ch is CollisionShape3D cs) cs.Disabled = false;
            Visible = true; _viewmodel?.SetShown(true);
            if (_cam != null) _cam.TopLevel = false;
            if (c != null) GlobalPosition = c.GlobalPosition + c.GlobalTransform.Basis.Z * 12f + Vector3.Up * 1.5f;   // step out beside the gantry
        }
        // W/S = drive on the wheels; A/D = slide the gantry trolley along the beam; Q/E = winch the hoist up/down (master)
        void DriveCrane(float delta)
        {
            float throttle = UiInputBlocked ? 0f : (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
            float trolley  = UiInputBlocked ? 0f : (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            float hoist    = UiInputBlocked ? 0f : (Input.IsPhysicalKeyPressed(Key.E) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.Q) ? 1f : 0f);
            bool mag = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Shift);   // Shift toggles the hoist electromagnet (grab/release a container)
            if (mag && !_craneMagPrev) _ridingCrane.ToggleMagnet();
            _craneMagPrev = mag;
            _ridingCrane.Drive(throttle, trolley, hoist, delta);
            GlobalPosition = _ridingCrane.GlobalPosition;
        }

        /// <summary>Open the boot. Its grid is created on first open and lives on the vehicle, so what you
        /// leave in a car is still there when you come back to it.</summary>
        void OpenVehicleTrunk(Vehicle v)
        {
            var trunk = v.EnsureTrunk();
            if (trunk == null) return;   // no boot on this hull -- the zone would not exist, but belt and braces
            OpenCrate(trunk);
        }

        /// <summary>Open the bonnet. A DUMMY mechanics panel for now (strawberry: "hood opens a dummy
        /// 'mechanics' ui") -- it reads the vehicle's real state rather than inventing numbers, so when it
        /// grows into repair/parts it is already pointed at the right data.</summary>
        void OpenVehicleHood(Vehicle v)
        {
            _mechanicsUI ??= new MechanicsPanel();
            if (_mechanicsUI.GetParent() == null) AddChild(_mechanicsUI);
            _mechanicsUI.Show(v);
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        MechanicsPanel _mechanicsUI;

        // Prompt line for the access zone currently under the crosshair. Mirrors the F dispatch exactly --
        // if this says "open trunk", F opens the trunk. A hull hit with no zone falls back to the plain enter
        // prompt, which is what boats/helis/tanks/trailers (no zones built) always show.
        string AccessPrompt(Vehicle v)
        {
            if (_driving != null || _riding != null) return "";
            string key = Keybinds.Get(GameAction.Interact).Label;
            if (!_focusAccessValid) return v.SeatCount > 0 ? $"[{key}] enter" : "";
            switch (_focusAccess.Kind)
            {
                case Vehicle.AccessKind.Trunk: return $"[{key}] open trunk";
                case Vehicle.AccessKind.Hood:  return $"[{key}] open hood";
                default:
                    int seat = _focusAccess.Seat;
                    string who = seat == 0 ? "driver" : $"seat {seat + 1}";
                    return v.SeatFree(seat) ? $"[{key}] enter ({who})" : $"{who} occupied";
            }
        }

        public void EnterVehicle(Vehicle v, int seat = -1)
        {
            if (v.NetDriverId != 0) return;   // MP §3.6: a remote player holds the seat (single driver) -- never set in pure SP, so the direct path is unchanged
            if (v.NetClientPredicted) { _rideLookYaw = 0f; _rideLookPitch = FpRideGazePitchDeg; }   // Part A free-look starts at the classic forward gaze, like EnterPuppet (#37)
            _driving = v;
            // Take the driver's seat when it is free, otherwise the first seat that is. Entering a full vehicle
            // is refused above; this only picks WHICH seat, so walking up to a car someone is already driving
            // puts you beside them rather than bouncing you off it.
            // AIMED AT A DOOR -> THAT SEAT, if it is free and real. Otherwise the old rule: the driver's seat
            // when it is free, else the first that is. The fallback matters -- aiming at an occupied door
            // should still put you in the car rather than bouncing you off it.
            _seatIndex = (seat >= 0 && seat < v.SeatCount && v.SeatFree(seat)) ? seat : 0;
            while (_seatIndex < v.SeatCount && !v.SeatFree(_seatIndex)) _seatIndex++;
            if (_seatIndex >= v.SeatCount) { _driving = null; return; }   // every seat taken
            v.OccupiedSeats.Add(_seatIndex);
            _burstLeft = 0;                                    // entering a vehicle cancels an in-progress burst (no resume on exit)
            // ENTERING NO LONGER STARTS IT (strawberry_cow 2026-08-24): the engine is its own state now, so a
            // car you climb into is however you left it. N / throttle / the speedo click are the ignition.
            if (Hud != null) Hud.Vehicle = v;                  // show the vehicle status box (fuel/health/battery)
            // Passengers KEEP their weapon (strawberry 2026-08-16: "passengers can hold weapons") -- only the
            // driver has their hands full.
            if (_seatIndex == 0) _viewmodel?.SetShown(false);
            if (_cam != null) _cam.TopLevel = true;            // free the camera into world space
            foreach (var c in FindChildren("*", "CollisionShape3D", true, false))
                if (c is CollisionShape3D cs) cs.Disabled = true;   // stop the player body fighting the vehicle
            Visible = false;
            Velocity = Vector3.Zero;
        }

        /// <summary>Move to another seat of the vehicle we are already in. Refuses a seat that does not exist or
        /// is taken, and returns whether it moved -- silently doing nothing and reporting success would make a
        /// full vehicle indistinguishable from a working switch.</summary>
        public bool TrySwitchSeat(int want)
        {
            var v = _driving;
            if (v == null || want < 0 || want >= v.SeatCount || want == _seatIndex) return false;
            if (!v.SeatFree(want)) return false;

            v.OccupiedSeats.Remove(_seatIndex);
            v.OccupiedSeats.Add(want);
            bool wasDriver = _seatIndex == 0;
            _seatIndex = want;

            // Vacating the driver's seat shuts it down and brakes, exactly as stepping out does: nobody is
            // holding the wheel, and a car that keeps its throttle while the driver climbs into the back is a
            // runaway rather than a feature. Taking the seat starts it again.
            // moving to a passenger seat no longer brakes it either -- same rule, and a car that stopped dead
            // because you climbed into the back would be stranger than one that keeps rolling.
            // Deliberately does NOT Wake() the vehicle. I added that here and on entry, reasoning that a settled
            // car would be frozen solid -- it is not: Drive/DriveHeli clear the parked flag on any input and the
            // settle rule releases the freeze the next physics frame, so waking here bought nothing. It cost
            // something, though. `_parked` also gates the residual-jitter DAMPING and the easier settle
            // threshold, so clearing it the instant someone sits down leaves an occupied, stationary vehicle
            // permanently live -- floaty and bouncing, worst on something heavy. strawberry reported exactly
            // that on the tank within the hour.
            // (sliding into the driver's seat does not start it either -- same reason as entering)

            // Hands full in the driver's seat; a passenger gets their weapon back (strawberry: "passengers can
            // hold weapons").
            _viewmodel?.SetShown(want != 0);
            return true;
        }

        void ExitVehicle()
        {
            var v = _driving; _driving = null;
            if (v != null) v.OccupiedSeats.Remove(_seatIndex);
            // Only the driver leaving shuts it down. A passenger hopping out of a moving car must not kill the
            // engine and park it underneath the person still driving.
            // no Park here either: momentum is the driver's to leave behind (see ExitVehicle)
            _seatIndex = 0;
            if (Hud != null) Hud.Vehicle = null;               // hide the vehicle status box
            if (v != null) GlobalPosition = ClampExitSpot(v.GlobalPosition + v.GlobalTransform.Basis.X * 2.4f + Vector3.Up * 1.0f);
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
            if (v != null) v.OccupiedSeats.Remove(_seatIndex);   // free the seat -- see EjectFromVehicleOnDeath. Dying does not reach over and turn the key either
            _seatIndex = 0;
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
        /// <summary>The third-person camera is live: on foot (or the puppet), not first-person, not dead. The HUD shows
        /// a centre crosshair here (master) since there is no viewmodel reticle to mark where the shot goes; the 3P view
        /// toes in on the aim so screen-centre IS the converged aim point.</summary>
        public bool ThirdPersonActive => !_fp && !_dead && _driving == null && _riding == null;
        /// <summary>Test hook: aim the view without a mouse. Clamped like the real look, so a test cannot ask for a
        /// pitch the player could never reach and get an answer that does not apply in play.</summary>
        public void DebugSetPitch(float deg) => _pitchDeg = Mathf.Clamp(deg, -89f, 89f);
        public float DebugPitch => _pitchDeg;
        public void EnterNearestVehicle() { var v = NearestVehicle(); if (v != null) EnterVehicle(v); }

        // While any menu/cursor UI is up (F1 dev console, inventory, craft, skills, map) the mouse is un-captured. Gate
        // all POLLED gameplay input on this so the menu is MODAL -- no walking/steering/firing/stance through it. Look +
        // single-fire already gate on Captured; this closes the movement/auto-fire/driving/stance gaps. Scripted
        // (harness) input bypasses -- it sets Scripted* directly. (strawberry 2026-07-15)
        bool UiInputBlocked => Input.MouseMode != Input.MouseModeEnum.Captured;

        void DriveVehicle(float delta)
        {
            if (_driving.Exploded) { ExitVehicle(); TakeDamage(150f); return; }   // caught in the blast -> ejected + killed (source explode kills passengers)
            // PASSENGERS RIDE, THEY DO NOT STEER (strawberry 2026-08-16: "only F1 is the drivers seat"). Bail
            // before any input is read, so a passenger holding W is not merely ignored by the vehicle but never
            // reaches it -- LastDriveInput is the MP fallback axes, and a back-seat passenger filling those in
            // would have every rider fighting the driver for the same channel.
            if (_seatIndex != 0)
            {
                // A gunner's LOOK aims the turret. Fed the same vehicle-local free-look angles the camera uses,
                // so the barrel and the view cannot drift apart; the mount clamps them to its own traverse, which
                // is why the camera is NOT clamped to match -- you can look past where the gun can point, and
                // seeing the gun stop is the feedback that tells you so.
                _driving.AimTurret(_seatIndex, _rideLookYaw, _rideLookPitch);
                GlobalPosition = _driving.GlobalPosition;   // still ride along, so the exit spot and cam track the vehicle
                return;
            }
            float throttle, steer;
            if (ScriptedDrive.HasValue) { steer = ScriptedDrive.Value.X; throttle = ScriptedDrive.Value.Y; }
            else if (UiInputBlocked) { throttle = 0f; steer = 0f; }   // menu open -> don't steer/accelerate through it
            else
            {
                throttle = (Keybinds.Pressed(GameAction.MoveForward) ? 1f : 0f) - (Keybinds.Pressed(GameAction.MoveBack) ? 1f : 0f);
                steer = (Keybinds.Pressed(GameAction.MoveRight) ? 1f : 0f) - (Keybinds.Pressed(GameAction.MoveLeft) ? 1f : 0f);
            }
            bool handbrake = !UiInputBlocked && Keybinds.Pressed(GameAction.VehicleHandbrake);

            // THROTTLE STARTS IT (strawberry_cow 2026-08-24): reaching for the gas on a dead car turns the key.
            //
            // ScriptedDrive counts. The first version of this excluded it, reasoning that a test rig or the MP
            // input path "must not silently hot-wire a car nobody started" -- which sounded careful and was
            // wrong: on those paths ScriptedDrive IS the player holding W, relayed from a client or a harness,
            // not a synthetic bypass. Excluding it meant the shell held full throttle against a dead engine and
            // five MP driving tests failed with "the car didn't move".
            //
            // TryStartEngine self-gates on EngineOn, the flat battery and OnFire, so this runs every physics tick
            // while the throttle is held and is a no-op on all but the first.
            if (_seatIndex == 0 && Mathf.Abs(throttle) > 0.01f) _driving.TryStartEngine();

            // FIXED WING (master 2026-08-17): W/S throttle, A/D tail rudder, mouse L/R = roll, mouse up/down =
            // pitch (the SAME virtual stick the heli uses, captured in _Input with the plane's own invert-Y
            // toggle). Hold Ctrl -> ground/taxi mode: lift is cut so it drops onto its floats/wheels and drives
            // like a car (also for wheeled helis later). Arrow keys mirror the mouse stick.
            if (_driving.IsPlane)
            {
                if (!UiInputBlocked)
                {
                    float arrowP = (Input.IsPhysicalKeyPressed(Key.Down) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.Up) ? 1f : 0f);
                    float arrowR = (Input.IsPhysicalKeyPressed(Key.Right) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.Left) ? 1f : 0f);
                    if (ControlsOptions.InvertPlanePitch) arrowP = -arrowP;
                    if (arrowP != 0f) _heliStickP = Mathf.MoveToward(_heliStickP, arrowP, ArrowStickRate * delta);
                    if (arrowR != 0f) _heliStickR = Mathf.MoveToward(_heliStickR, arrowR, ArrowStickRate * delta);
                }
                float psp = _heliStickP, psr = _heliStickR;
                float pfp = Mathf.Max(0f, Mathf.Abs(psp) - HeliStickCrossDeadzone * Mathf.Abs(psr)) * Mathf.Sign(psp);
                float pfr = Mathf.Max(0f, Mathf.Abs(psr) - HeliStickCrossDeadzone * Mathf.Abs(psp)) * Mathf.Sign(psr);
                _driving.PlaneGroundMode = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Ctrl);   // hold Ctrl -> drop + taxi like a car (master)
                _driving.DrivePlane(throttle, steer, pfp, pfr, delta);
                LastDriveInput = new UnityEngine.Vector2(steer, throttle);   // MP fallback axes (throttle/rudder); attitude rides the reported transform
                LastHandbrakeInput = false;
                float pk = Mathf.Exp(-HeliStickDecay * delta);   // self-centre the stick (same reasoning as the heli)
                _heliStickP *= pk; _heliStickR *= pk;
                GlobalPosition = _driving.GlobalPosition;
                return;
            }
            // ROTARY WING: the same W/S/A/D keys mean different things in the air. W/S is the collective (a
            // sticky throttle -- see Vehicle.DriveHeli), A/D is the pedals, and pitch/roll come off the mouse
            // stick captured in _Input. Handbrake has no meaning on a helicopter and is dropped.
            if (_driving.IsHeli)
            {
                // Cross-axis deadzone, applied to what the FLIGHT MODEL sees rather than to the stored stick, so
                // the stick keeps decaying smoothly and a diagonal that grows past the threshold blends in
                // instead of popping.
                // ARROW KEYS MIRROR THE CYCLIC (strawberry 2026-08-16: "mirror the mouse heli controls onto
                // the arrow keys too, making sure they respect the inverted toggle"). Fed into the SAME virtual
                // stick rather than a parallel path, so the cross-axis deadzone, the decay and the invert
                // setting all apply once and cannot drift apart from the mouse.
                //
                // Up = the same as pushing the mouse forward, which under Regular is nose-DOWN, so the sign
                // matches the mouse branch in _Input and flips with the identical toggle.
                if (!UiInputBlocked)
                {
                    float arrowP = (Input.IsPhysicalKeyPressed(Key.Down) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.Up) ? 1f : 0f);
                    float arrowR = (Input.IsPhysicalKeyPressed(Key.Right) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.Left) ? 1f : 0f);
                    if (ControlsOptions.InvertHeliPitch) arrowP = -arrowP;
                    if (arrowP != 0f) _heliStickP = Mathf.MoveToward(_heliStickP, arrowP, ArrowStickRate * delta);
                    if (arrowR != 0f) _heliStickR = Mathf.MoveToward(_heliStickR, arrowR, ArrowStickRate * delta);
                }
                // SHIFT = the sky-crane's electromagnet (strawberry 2026-08-17). Free in the cockpit -- Shift is
                // sprint on foot, which has no meaning while flying. Edge-triggered: it TOGGLES, so a held key does
                // not chatter the coil on and off every frame, and de-energising is how you set the load down.
                bool magNow = !UiInputBlocked && Input.IsPhysicalKeyPressed(Key.Shift);
                if (magNow && !_slingShiftPrev) _driving.ToggleSlingMagnet();
                _slingShiftPrev = magNow;
                float sp = _heliStickP, sr = _heliStickR;
                float fp = Mathf.Max(0f, Mathf.Abs(sp) - HeliStickCrossDeadzone * Mathf.Abs(sr)) * Mathf.Sign(sp);
                float fr = Mathf.Max(0f, Mathf.Abs(sr) - HeliStickCrossDeadzone * Mathf.Abs(sp)) * Mathf.Sign(sr);
                _driving.DriveHeli(throttle, steer, fp, fr, delta);
                LastDriveInput = new UnityEngine.Vector2(steer, throttle);   // MP fallback axes (collective/yaw); attitude rides the reported transform
                LastHandbrakeInput = false;
                // Self-centre the stick. Done HERE rather than in _Input because input events only arrive when
                // the mouse actually moves -- decaying there would leave the stick stuck at full deflection the
                // instant the player stopped moving it, which is the difference between "holds its attitude"
                // and "keeps rolling until it hits the ground".
                float k = Mathf.Exp(-HeliStickDecay * delta);
                _heliStickP *= k; _heliStickR *= k;
                GlobalPosition = _driving.GlobalPosition;
                return;
            }
            _driving.Drive(throttle, steer, handbrake);
            LastDriveInput = new UnityEngine.Vector2(steer, throttle);   // Part A: the session's VehicleState carries the axes as wheel/light dressing (inert in SP -- nothing reads these outside MP)
            LastHandbrakeInput = handbrake;
            GlobalPosition = _driving.GlobalPosition;   // ride along so exit + FP cam land at the vehicle (the cam is positioned in _Process from the vehicle's INTERPOLATED transform)
        }

        bool _slingShiftPrev;   // Shift edge for the sky-crane magnet toggle (see the heli branch above)

        void PositionDriveCam(Transform3D vt)   // SP driving: the cam math below, fed by the driven Vehicle's eye + size
        {
            float size = 0f;
            if (!_fp)
            {
                size = _driving.WorldMeshAabb().Size.Length();          // bounding diagonal -> bigger vehicle, further back
                if (_driving.CoupledTrailer != null && IsInstanceValid(_driving.CoupledTrailer))
                    size += _driving.CoupledTrailer.WorldMeshAabb().Size.Length() * 0.7f;   // towing -> pull the cam out further so the whole rig stays in frame (strawberry)
            }
            // Driver keeps the tuned DriverEyeLocal (tall cabs sit higher to clear the hood); a passenger's eye
            // is their OWN seat plus the same rise, or everyone in the bus looks out of the driver's window.
            var eye = _seatIndex == 0 ? _driving.DriverEyeLocal
                                      : _driving.SeatLocal(_seatIndex) + new Vector3(0f, PassengerEyeRise, 0f);
            eye += DriverPeekOffset();
            PositionVehicleCam(vt, eye, size);
        }

        /// <summary>The driver's eye slides sideways as they look around -- retail's "peek out of the window".
        ///
        /// strawberry was right and my first read was wrong: I went looking for a LEAN, found PlayerAnimator
        /// zeroing lean in DRIVING/SITTING, and concluded retail had nothing. It is not a lean -- it is a
        /// camera OFFSET DRIVEN BY YAW, in PlayerLook, and only for the DRIVING stance:
        ///
        ///     if (yaw &gt; 0) localPosition -> up*(heightLook+vOff) - left*(yaw/360)
        ///     else          localPosition -> up*(heightLook+vOff) - left*(yaw/240)
        ///     ...both Lerped at 4*dt
        ///
        /// ASYMMETRIC ON PURPOSE, and that asymmetry is the whole feel: looking LEFT offsets by yaw/240 and
        /// looking right only by yaw/360, so the driver leans much further out of their own window than across
        /// the cab. At retail's +/-160 yaw limit that is 0.67 m left against 0.44 m right.
        ///
        /// Our yaw sign is the mirror of retail's -- MEASURED, not assumed: a Basis about +Y is CCW, so
        /// _rideLookYaw = +90 points the view down -X, which is LEFT. Hence the negated offset and the swapped
        /// divisors. Getting that backwards would put the driver's head out of the passenger window, and it
        /// would look deliberate.</summary>
        Vector3 DriverPeekOffset()
        {
            float target = 0f;
            if (_fp && _driving != null && _seatIndex == 0)
            {
                // _rideLookYaw > 0 is looking LEFT for us, which is retail's yaw < 0 branch: the wider /240.
                float div = _rideLookYaw > 0f ? PeekDivisorOwnSide : PeekDivisorAcrossCab;
                target = -_rideLookYaw / div;
            }
            // Lerp at 4/s, retail's own smoothing rate for this, so the head eases out rather than snapping.
            _peekX = Mathf.Lerp(_peekX, target, Mathf.Min(1f, 4f * (float)GetProcessDeltaTime()));
            return Mathf.Abs(_peekX) < 0.001f ? Vector3.Zero : new Vector3(_peekX, 0f, 0f);
        }
        float _peekX;                              // metres of lateral eye offset, vehicle-local (+X = right)
        const float PeekDivisorOwnSide = 240f;     // looking out of the driver's OWN window: the bigger lean
        const float PeekDivisorAcrossCab = 360f;   // ...and across the cab: less

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
                if (_riding != null || DrivingPredicted || IsPassenger)   // MP ride, Part A predicted driving, or a PASSENGER seat: FREE-LOOK -- yaw/pitch in vehicle-local space; (0, FpRideGazePitchDeg) == the fixed gaze below
                {
                    var look = vt.Basis.Orthonormalized() * new Basis(Vector3.Up, Mathf.DegToRad(_rideLookYaw)) * new Basis(Vector3.Right, Mathf.DegToRad(_rideLookPitch));
                    _cam.GlobalTransform = new Transform3D(look, eye);
                }
                else   // SP driving: the classic fixed forward gaze over the hood
                    // FP: same rule as the chase cam -- in a helicopter the view IS the airframe's orientation,
                    // so take its basis outright rather than looking at a point with an up hint.
                    _cam.GlobalTransform = _driving != null && (_driving.IsHeli || _driving.IsPlane)
                        ? new Transform3D(vt.Basis.Orthonormalized(), eye)   // flying: the cockpit view IS the airframe's orientation -> it rolls/pitches with the plane
                        : new Transform3D(Basis.Identity, eye).LookingAt(vt * (eyeL + new Vector3(0f, -0.6f, -3.9f)), Vector3.Up);
            }
            else if (_driving != null && (_driving.IsHeli || _driving.IsPlane))
            {
                // FLYING: the camera BECOMES the airframe. VoX 2026-08-16, first "I want the players view to
                // tilt with the copter's role and pitch", then exactly: "the player's view should exacly match
                // the direction the minicopter is facing".
                //
                // So the camera's basis IS the vehicle's basis -- not a LookingAt with the vehicle's up passed
                // as a hint, which only approximates it and drifts as soon as the machine is near vertical.
                // Every other vehicle keeps a world-level chase cam because a car's roll is noise; on a
                // helicopter the roll IS the control input, and a level camera hides the thing being steered.
                // No mouse orbit either: while flying, the mouse is the cyclic, not a camera.
                float dist = _driving.IsPlane ? Mathf.Clamp(size * 0.62f, 6.5f, 20f) : Mathf.Clamp(size * 1.1f, 6.5f, 34f);   // planes: the long fuselage+wingspan inflate the AABB diagonal (~14.7 jet -> 16m) -> pull the chase cam IN (master 2026-08-18: jet 3p was way too far); helis unchanged (tinyclaw)
                if (_driving.IsPlane)
                {
                    // WORLD-STABLE chase for the PLANE (level horizon). The airframe-locked cam below swung the
                    // whole view around during rolls/loops -> master 2026-08-18 "the 3p camera keeps getting messed up".
                    var pf = -vt.Basis.Z; pf.Y = 0f; pf = pf.LengthSquared() > 0.001f ? pf.Normalized() : Vector3.Forward;   // flattened heading
                    var peye = vt.Origin - pf * (dist * 0.9f) + Vector3.Up * (dist * 0.34f + size * 0.05f);
                    _cam.GlobalTransform = new Transform3D(Basis.Identity, peye).LookingAt(vt.Origin + Vector3.Up * 0.4f, Vector3.Up);
                }
                else
                {
                    Basis vb = vt.Basis.Orthonormalized();   // HELI: the camera BECOMES the airframe (roll IS the control input -- VoX)
                    var eye = vt.Origin + vb.Z * (dist * 0.86f) + vb.Y * (dist * 0.16f + size * 0.06f);
                    _cam.GlobalTransform = new Transform3D(vb, eye);
                }
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

        public void PhysicsTick(double delta)   // PERF: engine callback taken by a TickProxy child (see TickProxy); body unchanged
        {
            // BEFORE every early return below (driving, riding, NetHold): you can hold a gun in a car, and a
            // pending gun state that only flushes while on foot is a pending gun state that is sometimes lost.
            if (!NetAvatar) TickGunStateFlush(delta);
            if (_pdieTest > 0) { _pdieTest -= delta; if (_pdieTest <= 0) { _pdieTest = -1; TakeDamage(9999f); } }
            // below-map kill: Unturned Level.isPointWithinValidHeight = y in [-1024,1024]; fall past the map floor -> die + respawn (covers driving too)
            if (!NetAvatar && !_dead && GlobalPosition.Y < -1030f) { GD.Print("[oob] fell below the map -> killed"); TakeDamage(9999f); }   // NetAvatar: TakeDamage is a no-op (invulnerable) -- gate here too so a pathological fall can't spam the log every tick
            if (NetHold) return;   // mp-clientauth-foot: a follower body never moves itself -- the entity owns the transform, PlayerNetSync teleports this body onto it
            StepLean((float)delta);   // BEFORE the driving/riding returns below: those bail out of the tick entirely, so a lean
                                      //  polled after them would freeze at whatever it was when you got into the car and stay there.
            if (_ridingCrane != null) { _interpReady = false; LastMoveInput = UnityEngine.Vector2.zero; LastJumpInput = false; DriveCrane((float)delta); return; }   // riding a crane: skip on-foot movement, drive the gantry
            if (_ridingTrain != null) { _interpReady = false; LastMoveInput = UnityEngine.Vector2.zero; LastJumpInput = false; DriveTrain((float)delta); return; }   // riding a train: skip on-foot movement, drive the rail
            if (_driving != null) { _interpReady = false; LastMoveInput = UnityEngine.Vector2.zero; LastJumpInput = false; DriveVehicle((float)delta); return; }   // driving: skip on-foot movement (+ pause the render-interp so exiting doesn't smear)
            if (_riding != null) { _interpReady = false; LastMoveInput = UnityEngine.Vector2.zero; LastJumpInput = false; RidePuppet(); return; }   // C6 ride mode: same freeze -- capture drive intent only, the SERVER drives
            if (_interpReady && !_dead) GlobalPosition = _interpCurr;   // render-interp (master): restore the TRUE physics position before moving (undoes the _Process visual smoothing)
            StepBullets();   // advance in-flight bullets (travel + drop) each 50 Hz tick — matches the source 0.02s step
            _interactClock += delta;   // sim seconds for the door/bed cooldowns (see _interactClock)
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
            _sinceShot += (float)delta;
            TickInfiniteAmmo();
            if (_meleeCd > 0f) _meleeCd -= (float)delta;
            if (IsSwimming) _swimMeleeGrace = SwimMeleeGraceTime; else if (_swimMeleeGrace > 0f) _swimMeleeGrace -= (float)delta;   // hold the grace full while swimming; decay it after surfacing (see MeleeAttack's swim gate)
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
                    _chambered = HasChamber && Ammo > 0;   // reload done -> the chamber state reflects the loaded gun
                    SaveGunState();   // reload finished -> mirror the new ammo/mag onto the backing item (master persistence)
                }
            }
            if (_unloading)   // shotgun UNLOAD (master): eject shells back to the bag, pump one-per-tick / break all at once, mirroring the reload
            {
                if (!UsesShells || !HasGunOut || _dead) { _unloading = false; _viewmodel?.SetReloading(false); }   // gun swapped/holstered mid-unload -> drop the state
                else
                {
                    _unloadTimer -= delta;
                    if (_unloadTimer <= 0)
                    {
                        var a = ShellAsset;
                        if (Gun?.ShellReload == true)   // pump: eject ONE shell per interval (the count lowers 1 by 1)
                        {
                            if (Ammo > 0 && a != null) { Inventory?.tryAddItem(new Item((ushort)a.id, 1)); Ammo--; }
                            if (Ammo <= 0) { _unloading = false; _viewmodel?.SetReloading(false); }
                            else _unloadTimer = (_viewmodel?.ReloadLength ?? ReloadTime) / System.Math.Max(1, Gun?.AmmoMax ?? 1);
                        }
                        else   // break-action (masterkey / quadbarrel): eject ALL barrels at once
                        {
                            if (a != null && Ammo > 0) Inventory?.tryAddItem(new Item((ushort)a.id, (byte)Ammo));
                            Ammo = 0;
                            _unloading = false; _viewmodel?.SetReloading(false);
                        }
                        SaveGunState();
                    }
                }
            }
            TickRechamber(delta);   // bolt/pump: run the post-shot bolt-cycle timer -> the Hammer clip, then re-enable firing
            // burst rounds + full-auto hold fire on cooldown (Fire() still enforces ammo/reload/cd)
            // A GUNNER holding LMB keeps firing: a belt-fed mount is automatic by nature, and the mount's own
            // TurretCycle governs the rate inside TryTurretFire. Deliberately OUTSIDE the _fireCd/_reloading gate
            // below -- those describe the rifle in the gunner's hands, which has nothing to do with the gun bolted
            // to the airframe, and _firemode (Semi by default, and unreachable while seated) must not gate it
            // either. Without this, a gunner got one shot per click at best. Review 2026-08-16.
            if (_driving != null && _seatIndex != 0 && _driving.HasTurret(_seatIndex)
                && !NetAvatar && !UiInputBlocked && Keybinds.Pressed(GameAction.Fire)) Fire();
            if (_fireCd <= 0f && !_reloading)
            {
                if (_burstLeft > 0) { if (Fire()) { _burstLeft--; if (_burstLeft == 0) _burstCd = 0.2f; } else _burstLeft = 0; }
                else if (_firemode == FireMode.Auto && !NetAvatar && !UiInputBlocked && Keybinds.Pressed(GameAction.Fire)) Fire();   // NetAvatar: never poll global input (a windowed L1 host's held mouse must not fire server avatars)
            }

            // Stance FSM: the shell polls the keys, the engine-free PlayerStanceSim owns the state machine
            // (X = crouch, Z = prone, sprint overlay, broken-legs demotion, headroom gate -- MP_PLAN §3.4).
            // NetAvatar never polls the keys -- PlayerNetSync forces ScriptedStance from the MoveInput
            // stance bits instead, so the avatar integrates at the stance the client shell predicted at.
            bool xNow = !NetAvatar && !UiInputBlocked && Keybinds.Pressed(GameAction.CrouchToggle);
            bool zNow = !NetAvatar && !UiInputBlocked && Keybinds.Pressed(GameAction.Prone);
            bool sprintNow = !NetAvatar && !UiInputBlocked && Keybinds.Pressed(GameAction.Sprint);
            bool cHeld = !NetAvatar && !UiInputBlocked && !(_build?.Active ?? false) && Keybinds.Pressed(GameAction.Crouch);   // C = HOLD-to-crouch (master): forces CROUCH while held; CrouchToggle (X) stays the stand<->crouch TOGGLE. build mode keeps its own C as cycle-structure
            StepStanceOnce(xNow, zNow, sprintNow, cHeld ? EPlayerStance.CROUCH : ScriptedStance);   // C-hold forces crouch via scriptedStance -> _move.Stance + the MP stance bits both follow (hold-to-crouch)
            if (_move.Stance == _recoilStance) _recoilStanceTime += (float)delta; else { _recoilStance = _move.Stance; _recoilStanceTime = 0f; }   // stance-settle timer for the recoil bonus (reset on any change) -- master

            float forward, strafe;
            if (ScriptedInput.HasValue) { strafe = ScriptedInput.Value.x; forward = ScriptedInput.Value.y; }
            else if (UiInputBlocked) { forward = 0f; strafe = 0f; }   // menu open -> don't walk through it
            else
            {
                forward = (Keybinds.Pressed(GameAction.MoveForward) ? 1f : 0f) - (Keybinds.Pressed(GameAction.MoveBack) ? 1f : 0f);
                strafe  = (Keybinds.Pressed(GameAction.MoveRight) ? 1f : 0f) - (Keybinds.Pressed(GameAction.MoveLeft) ? 1f : 0f);
            }
            bool jump = (ScriptedJump ?? (!NetAvatar && !UiInputBlocked && Keybinds.Pressed(GameAction.Jump))) && !Broken;   // broken legs can't jump (PlayerMovement.cs:1310); ScriptedJump = the wire's MoveInput v2 jump bit (C2)

            LastMoveInput = new UnityEngine.Vector2(strafe, forward);   // shell-captured axes for the MP input command
            LastJumpInput = jump;   // the wire jump bit is the HELD key the sim consumed (post-Broken) -- C3 reverted the F1 takeoff-edge encoding: a mispredicted takeoff is corrected by rewind+replay, not by wire gymnastics

            // feed the viewmodel its locomotion so the walk bob picks the right SPEED_*/BOB_* + gates on movement
            bool moving = Mathf.Abs(forward) > 0.01f || Mathf.Abs(strafe) > 0.01f;
            Moving = moving;                                  // exposed for zombie stealth detection
            _viewmodel?.SetLocomotion(moving, _move.Stance, _firemode == FireMode.Safety, LastMoveInput.x, LastMoveInput.y);   // safety firemode -> lowered "safe" carry pose; move axes drive the viewmodel sway tilt
            UpdateVitals(moving, (float)delta);
            FoodSpoilTick();             // once per in-game day: spoil the food in the bag (freshness -> moldy)
            TickConsume((float)delta);   // eat/drink timer -> applies the held consumable's effects
            TickDeploy((float)delta);    // deployable: follow the aim with the ghost + finish a pending place
            if (_viewmodel != null && _worldSun != null && _viewmodel.WorldSun == null) RelinkViewmodelLighting();   // safety: any viewmodel created before/without a link (Drive PEI timing, vehicle exit) still takes the world lighting
            ScanWorldLights();   // mirror nearby dynamic world lights (muzzle/headlights/flares) onto the gun

            // Phase 3 hearing: moving on foot makes FOOTSTEP noise the zombies can hear, loudness = the source stealth
            // detection radius by stance/speed (sprint 20 loud .. prone 3 near-silent). Throttled; a motionless player
            // makes no sound (must be SEEN instead). Zombies within earshot path to it via SoundBus.Hear.
            _footNoiseT -= (float)delta;
            // FOOTSTEP AUDIO (retail effects/physics/footstep/<surface>_<walk|run>): a step every stride-length of ground
            // covered, surface from the splatmap / prop SurfMeta under the feet, run bank above walking pace, water bank
            // when wading. Local player only here; puppets step in RemotePlayers. Crouch/prone are quieter and slower.
            if (!NetAvatar && !_dead && _driving == null && _riding == null && _ridingTrain == null && _ridingCrane == null && IsOnFloor() && !IsSwimming)
            {
                float hsp = new Vector2(Velocity.X, Velocity.Z).Length();
                if (moving && hsp > 0.3f)
                {
                    float stride = _move.Stance switch { EPlayerStance.SPRINT => 2.0f, EPlayerStance.CROUCH => 1.0f, EPlayerStance.PRONE => 0.9f, _ => 1.5f };
                    _strideAcc += hsp * (float)delta;
                    if (_strideAcc >= stride)
                    {
                        _strideAcc = 0f;
                        var sf = FootSurfaceUnderFeet();
                        bool run = _move.Stance == EPlayerStance.SPRINT || hsp > 4.5f;
                        if (_viewmodel != null) _viewmodel.CasingSurface = sf switch { Surf.Metal => "metal", Surf.Wood => "wood", Surf.Sand => "sand", Surf.Water => "water", _ => "general" };
                        var clip = GameAudio.PickFootstep(sf, run);   // surface_gait -> surface_walk -> concrete: a missing gait must not change the MATERIAL
                        float vol = _move.Stance switch { EPlayerStance.PRONE => -14f, EPlayerStance.CROUCH => -8f, EPlayerStance.SPRINT => 0f, _ => -3f };
                        GameAudio.PlayAt(this, clip, GlobalPosition, vol, 4f, 30f, _rng.RandfRange(0.94f, 1.06f));
                    }
                }
                else _strideAcc = Mathf.Min(_strideAcc, 0.6f * 1.5f);   // a stop mid-stride keeps most of the stride so the next step isn't instant
            }
            if (moving && _footNoiseT <= 0f)
            {
                _footNoiseT = 0.4f;
                float loud = GetStealthDetectionRadius() * Skills.SneakyBeakyNoiseMultiplier();   // SNEAKYBEAKY quiets footsteps -> zombies hear you from less far (source PlayerMovement:791)
                if (loud > 2f) SoundBus.Emit(GetTree(), GlobalPosition, loud);
            }

            StepMoveOnce(strafe, forward, jump, (float)delta, out bool wasAirborne, out float vy, out bool groundedEntering);
            LastGroundedInput = groundedEntering;   // the grounded the sim consumed -- state-stream dressing
            _interpPrev = _interpReady ? _interpCurr : GlobalPosition; _interpCurr = GlobalPosition; _interpReady = true;   // snapshot this tick's start/end for render interpolation (master)
            if (wasAirborne && IsOnFloor())
            {
                CheckFallDamage(vy);   // just touched down -> fall damage on a hard landing
                if (!NetAvatar && vy < -2.5f)   // retail bipedland/<surface>: a real drop, not a curb -- louder the harder
                    GameAudio.PlayAt(this, GameAudio.Pick("landing", GameAudio.LandSurface(FootSurfaceUnderFeet())), GlobalPosition, Mathf.Clamp(-9f + (-vy - 2.5f) * 1.2f, -9f, 2f), 5f, 40f);
            }
        }

        // ---- the movement kernel: the ONE deterministic movement step, split in two halves because
        // the live tick interleaves per-tick client work (viewmodel locomotion, vitals, footstep
        // noise) between the stance decision and the move. Everything physics-relevant lives HERE. ----

        // Stance-based recoil (master): crouched/prone steadies the gun, but ONLY after being fully settled in the
        // stance for a beat (StanceSettle) -- so spam-crouch / crawl mid-burst can't cheese it. Shell-side (recoil is a
        // local feel thing, separate from the MP-replicated PlayerStanceSim).
        EPlayerStance _recoilStance = EPlayerStance.STAND; float _recoilStanceTime;
        const float StanceSettle = 0.35f;
        float StanceRecoilMul() => _recoilStanceTime < StanceSettle ? 1f
            : _move.Stance switch { EPlayerStance.CROUCH => 0.85f, EPlayerStance.PRONE => 0.7f, _ => 1f };   // subtler than 0.6/0.35 -- a flat mult scales hardest on the punchiest guns, keep it gentle (master/tinyclaw)

        // ---- water / swim state (retail PlayerStance probes; the port's ocean is a single global plane at
        // Terrain.SeaLevelY, so submersion is a Y test). Player origin = feet; eye = feet+1.75 in SWIM. ----
        /// <summary>The feet+1.25m body probe is under the surface -> SWIM (PlayerStance.cs:636 isBodyUnderwater).</summary>
        bool BodyUnderwater => Terrain.HasWater && GlobalPosition.Y + 1.25f < Terrain.SeaLevelY;
        /// <summary>The eye probe (feet+1.75) is under -> submerged: free-swim in look dir + oxygen drains (areEyesUnderwater).</summary>
        public bool EyesUnderwater => Terrain.HasWater && GlobalPosition.Y + 1.75f < Terrain.SeaLevelY;
        /// <summary>The feet probe is under -> in the shallows: wading blocks crouch/prone (PlayerStance _inShallows).</summary>
        bool FeetUnderwater => Terrain.IsPointUnderwater(GlobalPosition.Y);
        /// <summary>Currently in the SWIM stance (deep enough that the body probe is submerged).</summary>
        public bool IsSwimming => _move.Stance == EPlayerStance.SWIM;

        /// <summary>Stance half: one stance-FSM step + the capsule resize (source HeightForStance).</summary>
        /// <summary>Who may lean, and which way. Engine-free so the RULES are testable without a physics world -- the
        /// obstruction results come in as booleans rather than being raycast in here.
        ///
        /// Returns the source's own sign convention: +1 LEFT, -1 RIGHT (PlayerAnimator.simulate). Obstructed is a
        /// separate outcome from "not leaning", because retail treats it differently at the other end: a lean that ends
        /// normally lerps back upright, one that is blocked SNAPS (PlayerLook.cs:738) so you cannot smear yourself
        /// through a wall on the way out.</summary>
        internal static int LeanFrom(bool leftKey, bool rightKey, EPlayerStance stance, bool leftClear, bool rightClear, out bool obstructed)
        {
            obstructed = false;
            // Stance gate, from the source verbatim. Note what is NOT here: CROUCH and PRONE lean fine, which is the
            // whole point -- leaning out of cover from a crouch is the move.
            if (stance is EPlayerStance.CLIMB or EPlayerStance.SPRINT or EPlayerStance.DRIVING or EPlayerStance.SITTING) return 0;
            // Nelson, 2025-01-20, on holding both: "Left==Right will stop lean when no input and when both input."
            // Holding Q+E stands you up rather than silently preferring one side.
            if (leftKey == rightKey) return 0;
            if (leftKey) { if (leftClear) return 1; obstructed = true; return 0; }
            if (rightClear) return -1;
            obstructed = true; return 0;
        }

        PhysicsShapeQueryParameters3D _leanQ;

        /// <summary>Is there room to put your head out that way? A capsule occupying exactly [eye, eye + dir*LeanReach]:
        /// swept from the EYES (not the feet) along the lean direction, and strictly on that side of you. LeanReach
        /// alone decides how close something has to be to refuse the lean.</summary>
        bool LeanSpaceEmpty(Vector3 dir, float eyeHeight)
        {
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return true;   // no world (bare unit test): nothing to be blocked by
            if (_leanQ == null)
                _leanQ = new PhysicsShapeQueryParameters3D
                {
                    // Godot's capsule is Y-axis with Height counting the CAPS, so Height == LeanReach puts the whole
                    // shape in [eye, eye + dir*LeanReach] -- and, critically, NOTHING BEHIND THE EYE.
                    //
                    // The source sweeps from the eye with a hemisphere still hanging back off it, which reaches
                    // PlayerStance.RADIUS (0.4) backwards. That is harmless in retail because the player's own body is
                    // the same 0.4 wide, so a wall flush against your shoulder sits exactly tangent to it. Our body is
                    // 0.35, narrower than the source's stance radius -- so that rear cap poked out past our own
                    // shoulder and a wall touching your LEFT side blocked leaning RIGHT (strawberry, in game). The
                    // per-side logic was right the whole time; the query was looking the wrong way.
                    Shape = new CapsuleShape3D { Radius = LeanRadius, Height = LeanCapsuleSpan().Height },
                    // What blocks a lean is what blocks the player, plus vehicles -- the source's BLOCK_LEAN is
                    // BLOCK_STANCE, which is ground/environment/props/structures/vehicles/clip.
                    CollisionMask = (1u << 0) | (1u << 5) | (1u << 6),
                    Exclude = new Godot.Collections.Array<Rid> { GetRid() },   // built once: this runs twice a tick while a lean key is held
                };
            var mid = GlobalPosition + Vector3.Up * eyeHeight + dir * LeanCapsuleSpan().Mid;
            // Stand the capsule along the lean direction: its local +Y becomes dir.
            var y = dir.Normalized();
            var x = y.Cross(Vector3.Up);
            if (x.LengthSquared() < 1e-6f) x = y.Cross(Vector3.Forward);
            x = x.Normalized();
            _leanQ.Transform = new Transform3D(new Basis(x, y, x.Cross(y).Normalized()), mid);
            return space.IntersectShape(_leanQ, 1).Count == 0;
        }

        internal float EyeHeight => Stance switch { EPlayerStance.CROUCH => 1.2f, EPlayerStance.PRONE => 0.35f, _ => 1.75f };
        float _eyeHeight = 1.6f;   // the lerped one; EyeHeight is the target

        /// <summary>Where the player is actually looking FROM -- source's `player.look.aim.position`. Under the lean
        /// pivot, so a lean carries it sideways with the head.
        ///
        /// This is NOT the camera. In first person the two coincide and the difference never shows; in third person
        /// the camera is 2 m back and a metre off to one side, so anything fired from it leaves from behind your own
        /// shoulder. Source is explicit about the split: the camera decides WHAT you are pointing at, the eyes are
        /// where the bullet starts (UseableGun.cs:1001 `bullet.origin = player.look.aim.position`).</summary>
        /// <summary>What the last real shot used. Recorded inside Fire() rather than recomputed by a test, so a test
        /// cannot quietly agree with a broken origin by deriving it the same wrong way.</summary>
        public Vector3 DebugLastShotOrigin { get; private set; }
        /// <summary>Where the bullet is REALLY spawned — the muzzle point, not the eyes. Assert bullet-position
        /// claims against this; DebugLastShotOrigin is the eye basis the aim maths uses.</summary>
        public Vector3 DebugLastBulletOrigin { get; private set; }
        public Vector3 DebugLastShotDir { get; private set; }
        /// <summary>The muzzle point used for FLASH/LIGHT only, never the projectile. Exposed so a test can assert
        /// the split survives: collapse this onto the bullet origin and the flash silently moves to the player's
        /// eye, which no other check here would notice.</summary>
        public Vector3 DebugLastFxMuzzle { get; private set; }

        /// <summary>Where the interaction trace actually started this frame. Recorded in UpdateLookFocus for the same
        /// reason as the shot seam: a test that recomputes the origin agrees with a wrong one. Only written while the
        /// mouse is captured, since that is the only time UpdateLookFocus runs -- use LookTrace() to ask directly.</summary>
        public Vector3 DebugLookEnd => _lookEnd;   // where the eye-ray actually stopped, so a test can see what it aimed at
        public static bool DebugForceLookScan;   // L1 only: stand in for a captured mouse, which headless will not grant
        public Vector3 DebugLookOrigin { get; private set; }
        public Vector3 DebugLookDir { get; private set; }

        /// <summary>Where the interaction ray starts and which way it points. THE production selector, called by
        /// UpdateLookFocus -- exposed so a test can ask it rather than restate it, because a restated rule agrees with
        /// itself whichever one of them is wrong.</summary>
        public (Vector3 From, Vector3 Dir) LookTrace()
            => _fp && _cam != null ? (_cam.GlobalPosition, -_cam.GlobalTransform.Basis.Z)
                                   : (ShoulderWorld, LookAxis);

        // ---- THE SHOULDER the interaction trace comes off (strawberry: "base the interaction lookatradius sphere off a
        // straight line based off the relevant lean shoulder (right shoulder is default if none held)").
        //
        // The point is line of sight from the BODY. The camera is not a place a person can see from -- in third person
        // it floats 2 m behind you and reaches through walls you are stood against, and even in first person it is a
        // point in the middle of your skull. Tracing from the shoulder you are actually peeking with means leaning
        // round a corner is what buys you the interaction, exactly as it buys you the shot.
        internal const float ShoulderOutX = 0.2f;    // lateral from the centreline
        internal const float ShoulderDropY = 0.25f;  // below the eyes -- a shoulder is not level with your eyeline

        /// <summary>-1 = left shoulder, +1 = right. Follows the LEAN, and defaults to the right when you are not
        /// leaning (strawberry). Note _lean is +1 for LEFT, source's convention, so this is not a passthrough.</summary>
        internal static int ShoulderSideFor(int lean) => lean > 0 ? -1 : 1;
        public int ShoulderSide => ShoulderSideFor(_lean);

        /// <summary>The active shoulder in world space. Under the lean pivot, so it swings out with you -- which is the
        /// entire reason for using it rather than the eyes.</summary>
        public Vector3 ShoulderWorld
        {
            get
            {
                var local = new Vector3(ShoulderSide * ShoulderOutX, _eyeHeight - ShoulderDropY, 0f);
                return _leanPivot != null ? _leanPivot.GlobalTransform * local : GlobalTransform * local;
            }
        }

        /// <summary>The straight look axis: body yaw + look pitch, with no camera in it. Same construction the fire
        /// path uses, and for the same reason -- the camera's live basis carries flinch and, in third person, sits
        /// somewhere the player is not.</summary>
        public Basis LookBasis => new Basis(Vector3.Up, Rotation.Y) * new Basis(Vector3.Right, Mathf.DegToRad(_pitchDeg));
        public Vector3 LookAxis => -LookBasis.Z;

        public Vector3 EyesWorld => _leanPivot != null
            ? _leanPivot.GlobalTransform * new Vector3(0f, _eyeHeight, 0f)
            : GlobalPosition + Vector3.Up * _eyeHeight;

        void StepLean(float delta)
        {
            bool blocked = NetAvatar || UiInputBlocked || _dead || _driving != null || _riding != null || _ridingTrain != null || _ridingCrane != null;   // train/crane too -- StepLean runs BEFORE their _PhysicsProcess returns, so Lean (an OnFoot action) must not stay live while seated on them
            bool q = !blocked && Keybinds.Pressed(GameAction.LeanLeft);
            bool e = !blocked && Keybinds.Pressed(GameAction.LeanRight);
            if (ScriptedLean.HasValue) { q = ScriptedLean.Value > 0; e = ScriptedLean.Value < 0; }

            // The same two keys own the third-person shoulder (PlayerAnimator.cs:1319-1336). On PRESS they set the
            // side; in third person the lean is then withheld for ShoulderTapWindow, so a tap swaps shoulders alone and
            // a hold swaps and leans. The stamp is SHARED between the two keys, exactly as in source, so tapping the
            // other side restarts the wait.
            _sideInputAge += delta;
            if (q && !_leanQHeld) { _camOnLeftSide = true; _sideInputAge = 0f; }
            if (e && !_leanEHeld) { _camOnLeftSide = false; _sideInputAge = 0f; }
            _leanQHeld = q; _leanEHeld = e;
            #pragma warning disable CS0162
            if (ShoulderTapSuppressesLean && !_fp && _sideInputAge <= ShoulderTapWindow) { q = false; e = false; }
            #pragma warning restore CS0162

            // Only pay for the shape query on the side actually being asked for -- and only when a key is down at all.
            float eye = EyeHeight;
            bool leftClear = !q || LeanSpaceEmpty(-GlobalTransform.Basis.X, eye);
            bool rightClear = !e || LeanSpaceEmpty(GlobalTransform.Basis.X, eye);
            _lean = LeanFrom(q, e, Stance, leftClear, rightClear, out _leanObstructed);
        }

        /// <summary>Test/demo override: +1 lean left, -1 right, 0 upright, null = read the keyboard.</summary>
        public int? ScriptedLean;

        /// <summary>Degrees the lean pivot is currently rolled, and where the camera ends up because of it.</summary>
        public float DebugLeanAngle => _leanAngle;
        public int DebugLean => _lean;
        public bool DebugLeanObstructed => _leanObstructed;

        /// <summary>Roll the pivot toward the current lean. Per-FRAME, matching PlayerLook.Update -- the state machine
        /// above runs at 50 Hz, the visible tilt is smooth.</summary>
        void ApplyLean(float delta)
        {
            if (_leanPivot == null) return;
            float target = _lean * LeanDegrees;
            // Obstructed now LERPS back upright (target 0), not the source's instant SNAP (PlayerLook.cs:738-741) --
            // master wants a SMOOTH blocked snap-back. (The source snapped to avoid ~0.25s of head-in-wall on the way
            // out; the lerp eases toward centre i.e. OUT of the obstruction, so the clip is brief -- master's override.)
            _leanAngle = Mathf.Lerp(_leanAngle, _leanObstructed ? 0f : target, Mathf.Min(1f, LeanLerp * delta));
            var r = _leanPivot.Rotation; r.Z = Mathf.DegToRad(_leanAngle); _leanPivot.Rotation = r;
        }

        // ---- THIRD PERSON (strawberry: "fix the 3rd person camera to be source accurate. Q & E switch which shoulder
        // of OTS cam", "3p just.. sucks", "we tilt the cam as if our cam is our head, not focusing on the playermodel").
        //
        // That last line names the old bug exactly. The chase cam sat at a FIXED offset behind the player and pitched
        // in place, so looking up or down rotated the view about a stationary point and slid the character out of
        // frame. Source computes the offset in the CAMERA's own frame -- so pitching down swings the camera up and
        // back, and the player stays in shot. The camera orbits; it does not merely tilt.
        //
        //     direction = normalize(fwd*-1.5 + up*0.25 + right*shoulder)      PlayerLook.cs:1799
        //     origin    = playerPos + up * thirdPersonEyeHeight
        //     position  = spherecast(origin, direction, 2.0, r=0.39, BLOCK_PLAYERCAM)
        internal const float TpBack = 1.5f, TpUp = 0.25f, TpSide = 1.0f;   // the unnormalised offset blend
        internal const float TpLength = 2.0f;                              // how far along it the camera sits
        internal const float TpSweepRadius = 0.39f;                        // NEAR_CLIP_SWEEP_RADIUS, "// PlayerStance.RADIUS"
        internal const float TpToeInDeg = 5.0f;                            // shoulder * -5 yaw, so the view converges on the aim
        const float ShoulderLerp = 8f;                                     // PlayerAnimator.cs:1545
        /// <summary>Source suppresses the lean for 75 ms after a lean-key PRESS in third person
        /// (PlayerAnimator.cs:1336), so a TAP swaps shoulders without leaning while a HOLD swaps and then leans.
        /// strawberry: "do it the src way, tap supression. not tap/hold state".
        ///
        /// Worth being precise about what this is NOT: there is no tap-versus-hold MODE, and nothing latches. The key
        /// is polled every tick exactly as before; the only thing the window does is withhold the lean for 75 ms after
        /// a press, in third person, so that a quick tap resolves as a shoulder swap alone. Keep holding and the lean
        /// arrives on its own with no second input.</summary>
        internal const float ShoulderTapWindow = 0.075f;
        internal const bool ShoulderTapSuppressesLean = true;

        bool _camOnLeftSide;        // source `side`: true = over the LEFT shoulder
        float _shoulder = 1f;       // lerped toward side ? -1 : +1; signs the camera's sideways offset
        float _sideInputAge = 99f;  // seconds since the last lean-key PRESS, for the tap window
        float _tpEyeHeight = 1.6f;

        public bool DebugCamOnLeftSide => _camOnLeftSide;
        public float DebugShoulder => _shoulder;
        /// <summary>The point the third-person camera orbits: the clamped pivot, in world space. Distances have to be
        /// measured from HERE, not from the player's feet -- the pivot is 1.6 m up, so a 2 m camera is 2.6 m from the
        /// soles and a check written against the feet reads as "too far" while the camera is exactly right.</summary>
        public Vector3 DebugTpOrigin => GlobalPosition + Vector3.Up * _tpEyeHeight;
        /// <summary>Last camera sweep result: the safe fraction of the 2 m reach, or -1 if the query returned nothing.
        /// A sweep that silently returns "clear" and one that is never run both leave the camera at full distance.</summary>
        public float DebugTpSweepFraction { get; private set; } = -1f;

        /// <summary>The 3P pivot height. Source clamps the stance eye height into the collision capsule so the sweep
        /// sphere cannot start poking out of the top or bottom of you (PlayerLook.cs:1238-1240) -- which means a
        /// STANDING third-person pivot is 1.605, not the 1.75 the first-person eye sits at.</summary>
        internal static float ThirdPersonPivot(float eyeHeight, float capsuleHeight)
            => Mathf.Clamp(eyeHeight, TpSweepRadius + 0.005f, capsuleHeight - TpSweepRadius - 0.005f);

        /// <summary>The camera offset direction, in CAMERA space. Normalised, so the three weights set the ANGLE the
        /// camera sits at and TpLength alone sets the distance.</summary>
        internal static Vector3 ThirdPersonOffsetLocal(float shoulder)
            => new Vector3(TpSide * shoulder, TpUp, TpBack).Normalized();   // Godot: -forward is +Z, right is +X

        PhysicsShapeQueryParameters3D _tpQ;

        void StepThirdPersonCam(float delta)
        {
            _shoulder = Mathf.Lerp(_shoulder, _camOnLeftSide ? -1f : 1f, Mathf.Min(1f, ShoulderLerp * delta));
            float eye = EyeHeight;
            _tpEyeHeight = Mathf.Lerp(_tpEyeHeight, ThirdPersonPivot(eye, PlayerMovementDef.HeightForStance(Stance)), Mathf.Min(1f, 4f * delta));

            // Rotation FIRST: the offset below is expressed in this frame, so the order is load-bearing rather than
            // stylistic -- computing the direction off last frame's basis lags the camera behind every mouse movement.
            var look = Basis.FromEuler(new Vector3(Mathf.DegToRad(_pitchDeg), Mathf.DegToRad(_shoulder * -TpToeInDeg), 0f), EulerOrder.Yxz);
            _cam.Basis = new Basis(_flinch) * look;

            var dirLocal = ThirdPersonOffsetLocal(_shoulder);
            var dir = (_cam.GlobalBasis * dirLocal).Normalized();
            var origin = GlobalPosition + Vector3.Up * _tpEyeHeight;
            _cam.GlobalPosition = origin + dir * SweepCamera(origin, dir, TpLength);
        }

        /// <summary>How far the camera can go before it would be inside something. Source sphereCastCamera takes the
        /// CLOSEST hit along a sphere sweep, so the camera pulls in against a wall instead of clipping through it.
        ///
        /// Stepped-and-bisected rather than a single CastMotion, because THIS PROJECT RUNS JOLT and Jolt's cast_motion
        /// returns a clear fraction of 1 even with a wall squarely in the path -- verified here with a plain ray that
        /// hits the same wall the sweep says is not there. A sweep that silently reports "clear" and a sweep that never
        /// runs leave the camera in exactly the same place, so this is worth not re-simplifying.
        ///
        /// The step is capped at the sphere RADIUS on purpose: sample any coarser and a wall thinner than the gap slips
        /// between two samples, which is the same silent "clear" in a different costume.</summary>
        float SweepCamera(Vector3 origin, Vector3 dir, float length)
        {
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) { DebugTpSweepFraction = 1f; return length; }
            _tpQ ??= new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = TpSweepRadius },
                // BLOCK_PLAYERCAM: ground, environment, structures, vehicles. Deliberately NOT the item/port layers --
                // a dropped can must not shove the camera.
                CollisionMask = (1u << 0) | (1u << 5) | (1u << 6),
                Exclude = new Godot.Collections.Array<Rid> { GetRid() },
            };

            bool Blocked(float d)
            {
                _tpQ.Transform = new Transform3D(Basis.Identity, origin + dir * d);
                return space.IntersectShape(_tpQ, 1).Count > 0;
            }

            int steps = Mathf.Max(2, Mathf.CeilToInt(length / TpSweepRadius));
            float clear = 0f, hit = -1f;
            for (int i = 1; i <= steps; i++)
            {
                float d = length * i / steps;
                if (Blocked(d)) { hit = d; break; }
                clear = d;
            }
            if (hit < 0f) { DebugTpSweepFraction = 1f; return length; }
            for (int i = 0; i < 5; i++)   // bisect the last clear/blocked pair -- 5 rounds is ~2 cm at this range
            {
                float mid = 0.5f * (clear + hit);
                if (Blocked(mid)) hit = mid; else clear = mid;
            }
            DebugTpSweepFraction = clear / length;
            return clear;
        }

        void StepStanceOnce(bool crouchKey, bool proneKey, bool sprintKey, EPlayerStance? scriptedStance)
        {
            // ADS WITHHOLDS THE SPRINT STANCE (strawberry: "make ads-ing and sprinting states mutually exclusive").
            // Source does not cancel the aim when you press sprint -- PlayerStance.cs:701 folds
            // `gunAsset.canAimDuringSprint || !gun.isAiming` into the gate that ENTERS the sprint stance, so a
            // shouldered gun simply means the sprint never starts. Cancelling the aim instead would look similar and
            // feel nothing like it: you would lose your sights every time a sprint key was brushed.
            bool equipmentAllowsSprint = _viewmodel == null || !_viewmodel.IsAiming;
            _move.Stance = _stance.Step(crouchKey, proneKey, sprintKey, Stamina, Broken, scriptedStance, _capStance, HeadroomFor, equipmentAllowsSprint);
            // A ladder outranks everything, including water -- retail's simulate() runs the ladder block first
            // and RETURNS out of the stance function while attached, so a ladder in the shallows keeps you
            // climbing rather than dropping you into a swim.
            if (!NetAvatar && StepLadder()) _move.Stance = EPlayerStance.CLIMB;
            // Water overrides the key-driven stance. NetAvatars hold the replicated stance (NetHoldPose), so
            // only a locally-simulated shell decides here.
            else if (!NetAvatar && BodyUnderwater)
                _move.Stance = EPlayerStance.SWIM;   // feet+1.25 body probe submerged -> swim (PlayerStance.cs:636-673)
            else if (!NetAvatar && FeetUnderwater && (_move.Stance == EPlayerStance.CROUCH || _move.Stance == EPlayerStance.PRONE))
                _move.Stance = EPlayerStance.STAND;  // wading (feet wet, not deep enough to swim) blocks crouch/crawl (PlayerStance.cs:340-346, 865-869)
            UpdateHitbox(_move.Stance);   // resize the collision capsule to match the stance (source HeightForStance)
        }

        PhysicsShapeQueryParameters3D _ladderFitQ;
        // ARE WE ALREADY ON THE LADDER? Tracked HERE and not read off _move.Stance, which cannot answer it:
        // PlayerStanceSim.Step only ever returns STAND/CROUCH/PRONE/SPRINT, so `_move.Stance == CLIMB` inside
        // StepLadder was ALWAYS false and retail's "already attached, don't re-snap" branch never once ran.
        // Every tick of every climb was therefore a fresh ENTRY: the capsule fit re-tested and the player
        // TELEPORTED back onto the ladder's centre line 50 times a second. Retail snaps once, on entry
        // (PlayerStance.simulate: `if (stance != EPlayerStance.CLIMB)`), and lets movement carry you after.
        // That per-tick re-snap is why a ladder felt like it grabbed from too far and would not let go
        // (strawberry) -- you were being pulled back onto it faster than you could walk away.
        bool _climbing;
        /// <summary>Test seam: is the ladder attachment currently held? (Not the same question as the stance.)</summary>
        public bool DebugClimbing => _climbing;

        // RE-ATTACH HYSTERESIS. Detach and re-attach were the SAME threshold -- you come off when the 0.5m-up
        // probe clears the ladder's top, and you can grab again the instant it doesn't. At the top of a ladder
        // that is a knife edge: step off, gravity pulls you 2cm, the probe re-acquires, you are back on. Measured
        // 17 detach/re-attach pairs in 1.2s (ladder.top_exit), which is exactly strawberry's "i keep snapping
        // back onto it". Retail never needs this because its ladders meet a ledge you land on; ours are placed
        // from the same data but the player still has to be GIVEN the moment of control to step forward onto it.
        // So: after coming off a ladder, refuse to re-grab briefly. Long enough to walk clear, short enough that
        // deliberately re-grabbing after a slip still feels immediate.
        const float LadderReattachCooldown = 0.45f;
        float _ladderCd;

        /// <summary>Ladder attach/hold/detach, once per tick. True = we are on a ladder this frame.
        ///
        /// Retail (PlayerStance.simulate): probe forward 0.75 m from 0.5 m above the feet; the hit must be a
        /// ladder, must be its front/back FACE rather than an edge, and the ladder must be upright. First
        /// attach snaps you to the ladder's centre line. Losing the probe drops you off, which is what makes
        /// stepping sideways off a ladder work without any explicit dismount.</summary>
        bool StepLadder()
        {
            if (_ladderCd > 0f) _ladderCd -= (float)GetPhysicsProcessDeltaTime();
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return LadderDetach();

            // ENTRY IS STRICT, HOLDING IS LOOSE -- and they are different questions. The probe starts 0.5 m up,
            // so while attached the highest a player's FEET can get is (ladderTop - 0.5): they are handed off
            // half a metre below the ladder's top. Retail gets away with the same 0.5 m because its ladders
            // poke well above whatever you step onto. Ours are the mesh's own extent, so on a roof flush with
            // the ladder top the player detaches BELOW the surface, beside the edge, with nothing underfoot --
            // and then falls the whole way back down (ladder.top_flush_roof measured exactly that: off at
            // 6.25, roof at 6.75, landed at 0.00).
            // Dropping the origin only while ALREADY climbing lets you ride to the actual top without making
            // the ladder one millimetre easier to grab in the first place, which is the complaint I do NOT
            // want to make worse.
            float probeH = _climbing ? Ladder.HoldProbeHeight : Ladder.ProbeHeight;
            var from = GlobalPosition + Vector3.Up * probeH;
            var fwd = -GlobalTransform.Basis.Z;                       // body facing, not the camera's
            var q = PhysicsRayQueryParameters3D.Create(from, from + fwd * Ladder.ProbeDist);
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(q);
            // Any of these failing means "not on a ladder", and the caller turns that into STAND -- which is
            // the whole dismount mechanism: step sideways, the probe misses, you are walking again.
            if (hit == null || hit.Count == 0 || !hit.ContainsKey("collider")) return LadderDetach();
            if (hit["collider"].As<GodotObject>() is not Node3D body || !body.HasMeta(Ladder.Meta)) return LadderDetach();
            if (!Ladder.IsClimbable((Vector3)hit["normal"], Ladder.FaceAxis(body))) return LadderDetach();

            _ladderBody = body;             // remembered for the CLIMB carry: a ladder bolted to a SHIP moves
            if (_climbing) return true;     // already on it; nothing to re-snap (retail snaps on ENTRY only)
            if (_ladderCd > 0f) return false;   // just came off one: give the player their moment to walk away

            var target = Ladder.ClimbPoint(body.GlobalPosition, (Vector3)hit["position"], (Vector3)hit["normal"]);
            // Refuse if the destination is occupied. Tested with IntersectShape AT the target rather than a
            // motion cast on purpose: a sweep reports CLEAR through a wall in this engine (that cost a day on
            // the barricade work), and it also cannot see a collider we are already overlapping -- which is the
            // exploit retail's second CheckCapsule exists to close.
            // Inset from the feet by FloorInset. The climb point sits at roughly FOOT level -- hitY is
            // taken 0.5 m above the feet and the point drops 0.5 back down -- so a capsule starting exactly
            // at `target` grazes the very ground the ladder is standing on and reports "blocked" at the foot
            // of every ladder in the world. The check wants HEADROOM, not "is there a floor here".
            const float FloorInset = 0.12f;
            _ladderFitQ ??= new PhysicsShapeQueryParameters3D
            {
                Shape = new CapsuleShape3D { Height = PlayerMovementDef.HEIGHT_STAND - FloorInset * 1.5f, Radius = 0.28f },
                CollisionMask = 1u << 0,
            };
            _ladderFitQ.Transform = new Transform3D(Basis.Identity,
                target + Vector3.Up * (FloorInset + (PlayerMovementDef.HEIGHT_STAND - FloorInset * 1.5f) * 0.5f));
            _ladderFitQ.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            if (space.IntersectShape(_ladderFitQ, 1).Count > 0) return LadderDetach();   // blocked -> stay off, keep walking

            GlobalPosition = target;
            _climbing = true;
            return true;
        }

        /// <summary>Leave the ladder, and arm the re-grab cooldown ONLY if we were actually on one -- otherwise
        /// simply walking near a ladder would keep re-arming it and the first attach would never happen.</summary>
        Node3D _ladderBody;   // the ladder we are on, or null. See the CLIMB branch of StepMoveOnce.

        bool LadderDetach()
        {
            if (_climbing) _ladderCd = LadderReattachCooldown;
            _climbing = false;
            _ladderBody = null;
            return false;
        }

        /// <summary>Movement half: grounded resolve -> sim Step -> StepUp -> MoveAndSlide.
        /// groundedEntering = the grounded state the sim consumed;
        /// verticalVel = this step's sim vertical velocity (fall damage).
        /// (The v9 note: the MP DeterministicGround fork -- det spherecast ground + snap, the F6
        /// real-step StepUp gate -- is deleted with the two-body model; every body runs the SP path.)</summary>
        void StepMoveOnce(float strafe, float forward, bool jump, float delta,
                          out bool wasAirborne, out float verticalVel, out bool groundedEntering)
        {
            if (_move.Stance == EPlayerStance.SWIM)
            {
                SwimStep(strafe, forward, jump);   // no gravity, buoyancy/free-swim; own velocity path
                if (!NetAvatar)   // retail effects/physics/swim/<light|medium|heavy>wading: a stroke every ~1.4 m, heavier with speed
                {
                    float ssp = new Vector2(Velocity.X, Velocity.Z).Length();
                    if (ssp > 0.4f && (_strideAcc += ssp * delta) >= 1.4f)
                    {
                        _strideAcc = 0f;
                        string gait = ssp > 3.6f ? "heavywading" : ssp > 2.0f ? "mediumwading" : "lightwading";
                        GameAudio.PlayAt(this, GameAudio.Pick("swim", gait), GlobalPosition, -4f, 5f, 40f, _rng.RandfRange(0.95f, 1.05f));
                    }
                }
                wasAirborne = false;               // swimming is never airborne -> the caller skips fall damage (retail: SWIM branch never onLanded)
                verticalVel = Velocity.Y;
                groundedEntering = false;
                return;
            }
            if (_move.Stance == EPlayerStance.CLIMB)
            {
                // Purely vertical: PlayerMovement's CLIMB branch is `velocity = (0, move.z * speed * 0.5, 0)`.
                // No horizontal term at all, which is what keeps you glued to the rungs while you look around.
                //
                // ...unless the LADDER is moving. Climbing is its own stance, so neither the deck carry nor
                // CharacterBody3D's own moving-floor handling reaches it -- both need you standing on something.
                // And StepLadder deliberately does not re-snap while climbing ("retail snaps on ENTRY only"), so
                // on a ship under way the player would rise straight up in WORLD space, the hull would sail out
                // from under them, and the probe would miss within a tick or two and drop them in the sea.
                var climbCarry = Vector3.Zero;
                if (DeckCarryEnabled && _ladderBody != null && IsInstanceValid(_ladderBody)
                    && _ladderBody.GetParent() is Vehicle lv && lv.CarriesRiders)
                {
                    climbCarry = lv.DeckPointVelocity(GlobalPosition);
                    if (Mathf.Abs(lv.DeckYawRate) > 1e-5f) RotateY(lv.DeckYawRate * delta);
                }
                Velocity = new Vector3(climbCarry.X, Ladder.ClimbVelocity(forward), climbCarry.Z);
                MoveAndSlide();
                wasAirborne = false;    // retail forces isGrounded while climbing -> stepping off a ladder is never a fall
                verticalVel = 0f;       // ...and so must never book fall damage
                groundedEntering = true;
                return;
            }
            bool grounded = IsOnFloor();
            groundedEntering = grounded;
            var v = _move.Step(new UnityEngine.Vector2(strafe, forward), jump, grounded, delta);
            Vector3 world = GlobalTransform.Basis * new Vector3(v.x, 0f, -v.z);
            wasAirborne = !grounded;                     // ground state going into this step
            Velocity = new Vector3(world.X, v.y, world.Z);
            StepUp(delta, grounded);   // climb small curbs/thresholds so we don't snag (master)

            // MOVING DECK -- ROTATION ONLY, and the "only" is the whole point. The obvious implementation is
            // to add the deck's velocity around MoveAndSlide; it is also WRONG, because CharacterBody3D's floor
            // handling already carries the capsule along a moving floor by itself. Measured: with the added
            // velocity the player covered 88.6 m while the ship made 66.0 and ended jammed against the bow rail;
            // with it removed the player held station to 0.1 m over 72 m unaided. Translation needs no help.
            // Facing does -- nothing rotates the capsule with the hull, so without this a 180 leaves you looking
            // over the side of a ship you are still standing squarely on.
            RideDeckRotation(grounded, delta);
            MoveAndSlide();
            verticalVel = v.y;
        }

        PhysicsRayQueryParameters3D _deckRayQ;
        Godot.Collections.Array<Rid> _deckRayExclude;

        /// <summary>Turn with the deck under our feet. Rotation only -- CharacterBody3D already carries the
        /// capsule along a moving floor, so TRANSLATION here would be a second copy of a thing already happening
        /// (measured: 88.6 m travelled against the hull's 66.0, ending pinned against the bow rail). Nothing
        /// rotates it though, so a hull that turns underneath you leaves you facing where you started in WORLD
        /// terms -- a 180 ends with you looking over the rail of a ship you are stood squarely on.
        ///
        /// The floor is found with a short ray straight down onto the VEHICLE layer rather than by reading slide
        /// collisions, because standing perfectly still does not reliably produce a slide collision every tick
        /// and the answer would flicker. Masked to bit5 alone, so standing on ordinary ground costs nothing --
        /// vehicles carry bit0 too, which is why the capsule collides with them in the first place.</summary>
        void RideDeckRotation(bool grounded, float delta)
        {
            DebugOnDeck = null;
            if (!grounded || !DeckCarryEnabled) return;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return;
            _deckRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = 1u << 5 };   // vehicles only
            _deckRayQ.From = GlobalPosition + Vector3.Up * 0.3f;
            _deckRayQ.To = GlobalPosition + Vector3.Down * 0.6f;
            _deckRayQ.Exclude = _deckRayExclude ??= new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(_deckRayQ);
            if (hit.Count == 0) return;
            if (hit["collider"].As<GodotObject>() is not Vehicle deck || !deck.CarriesRiders) return;
            float yawRate = deck.DeckYawRate;
            if (Mathf.Abs(yawRate) > 1e-5f) RotateY(yawRate * delta);
            DebugOnDeck = deck;
        }

        /// <summary>Test seam: the vessel we are being carried by this tick, or null.</summary>
        public Vehicle DebugOnDeck;

        /// <summary>Test seam: turn the deck carry off, so a test can measure the SAME run with and without it.
        /// "the player stayed on the ship" is not evidence on its own -- a ship that barely moved would produce
        /// it too.</summary>
        public static bool DeckCarryEnabled = true;

        /// <summary>Swim movement (PlayerMovement.cs:1134-1164): no gravity. Submerged (or look-down + push
        /// forward) = free-swim following the 3D aim, space swims UP at 3 m/s. At the surface = horizontal in
        /// the body frame + a buoyancy bob that floats the eyes just above water. Base speed 4.5 m/s (SPEED_SWIM
        /// 3 x the branch's 1.5). Client-side only (the shell has the camera); NetAvatars hold their pose.</summary>
        void SwimStep(float strafe, float forward, bool jump)
        {
            const float SwimSpeed = PlayerMovementDef.SPEED_SWIM * 1.5f;   // 4.5 m/s (PlayerMovement.cs:1143/1163)
            const float SwimUp = 3f;                                        // vertical swim/ascend constant (PlayerMovement.cs:104)
            var local = new Vector3(strafe, 0f, -forward);
            if (local.LengthSquared() > 1f) local = local.Normalized();     // move.normalized: unit on diagonals, digital single-axis stays 1

            // The LOOK basis, not the camera's. These were the same thing when third person was a fixed chase cam
            // roughly down the look axis -- but the third-person camera now sits 2 m back, a metre to one side, and
            // carries a 5 degree toe-in, so swimming off _cam.Basis would drift you sideways relative to where you are
            // actually aiming. Same reason the fire path stopped reading the camera. First person is unaffected: there
            // the camera IS this basis.
            Basis aim = LookBasis;
            Vector3 lookFwd = -aim.Z;
            bool diving = lookFwd.Y < -0.25f && forward > 0.1f;             // look down + forward -> submerge (retail look.pitch>110 & move.z>0.1)

            Vector3 vel;
            if (EyesUnderwater || diving)
            {
                vel = (aim * local) * SwimSpeed;   // 3D velocity follows where you look (dive / ascend)
                if (jump) vel.Y = SwimUp;          // space always climbs
            }
            else
            {
                Vector3 horiz = GlobalTransform.Basis * local;   // surface: horizontal follows body yaw
                float buoy = (Terrain.SeaLevelY - 1.275f - GlobalPosition.Y) / 8f;   // float feet toward surface-1.275 (eyes above water)
                vel = new Vector3(horiz.X * SwimSpeed, buoy, horiz.Z * SwimSpeed);
            }
            Velocity = vel;
            MoveAndSlide();
            _move.Velocity = new UnityEngine.Vector3(strafe * SwimSpeed, vel.Y, forward * SwimSpeed);   // keep the sim velocity coherent for consumers
        }
    }
}
