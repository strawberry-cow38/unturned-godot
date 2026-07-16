using Godot;
using SDG.Unturned;   // ItemAsset.RarityColorUI + EItemRarity (vehicle look-at outline colour)

namespace UnturnedGodot
{
    // Drivable vehicle. Source: InteractableVehicle + VehicleAsset (WheelCollider rig -> Godot VehicleBody3D +
    // VehicleWheel3D 1:1). Meshes ripped by tools/extract_vehicle_mesh.py; params + real _PaintColor from the .dat.
    public partial class Vehicle : VehicleBody3D
    {
        float _engineForce = 600f;                  // acceleration feel (calibrated: Unity WheelCollider torque doesn't map 1:1)
        float _steerMax = 28f, _steerMin = 14f;      // Steer_Max (at rest) .. Steer_Min (at full speed), degrees -- source .dat
        float _speedMax = 12.5f, _speedMin = -7f;    // Speed_Max fwd / Speed_Min reverse, m/s -- source .dat (directly usable)
        float _brakeForce = 32f;                     // Brake -- source .dat value
        float _steerTarget, _steerAngle, _steerTurnSpeed = 70f;   // steering smoothing: MoveTowards target at deg/s. LOWERED for a weighty/laggy feel -- the wheels float behind the input, slow to turn AND slow to re-center (master)
        bool _parked, _handbraking; float _spawnGrace = 2.5f; Vector3 _velAvg, _angAvg;   // -> STATIC freeze once majority-grounded + the LOW-PASSED velocity/spin are low (jitter-immune, d9588d3); _spawnGrace lets a fresh car DROP to terrain first
        float _prevSpeed;   // last frame's speed, to detect a sudden drop = a crash (collision/ram damage)
        float _deadTimer = -1f; bool _exploded, _husk; CpuParticles3D _smoke, _smoke0, _fire; OmniLight3D _fireLight;
        float _burnTime = -1f;   // seconds since the wreck caught fire (master lifecycle): <40 full, 40-60 dying down, 60 out+light killed, 360 despawn
        CpuParticles3D[] _wheelDust;   // per-WHEEL dust from the ground contact point (src Wheel.cs TireMotionEffectInstance is per-wheel); tinted by the Surf under each wheel
        PlayerController.Surf[] _wheelSurf; float _dustCheckT, _dustLogT;   // cached ground material per wheel (raycast, throttled); _dustLogT throttles UG_DUSTDEBUG
        MeshInstance3D _bodyMesh; AudioStreamPlayer3D _explosionAudio; Vector3 _firePos;   // damage/explosion (source askDamage/explode); _husk = settled wreck, sim killed; _firePos = engine-bay local offset
        const float ExplodeDelay = 4f, SmokeHealth = 200f, HeavySmokeHealth = 100f;   // source EXPLODE=4s, SMOKE_1<200, SMOKE_0<100
        const float FootBrakeScale = 6f, HandbrakeScale = 13f;   // Godot Brake calibration (raw .dat Brake too weak, but 15/35 flipped the car onto its nose -- master); S foot-brake vs Space handbrake bite
        public bool Exploded => _exploded;
        VehicleWheel3D[] _wNodes; MeshInstance3D[] _wMeshes;   // wheels: VehicleWheel3D auto-rolls its node (mesh child inherits it), so no manual spin. _wMeshes kept for debris/hide.
        Mesh _wheelMeshRef; Material _wheelMatRef; float _wheelR;   // kept so the wheels can fly off as debris on explode
        public static float GlobalMass = 900f;   // all vehicles share one mass (the source does: Rigidbody mass = 2.0 for every vehicle)
        float[] _gears; float _reverseGear, _shiftUpRpm; float _engineRpm = 1000f; int _gear = 1;   // engine RPM + gear sim
        AudioStreamPlayer3D _engineAudio; float _idlePitch = 1f, _maxPitch = 2f, _idleVol = 0.75f, _maxVol = 1f;   // EngineRPMSimple sound
        const float EngineVolumeBoost = 1.5f;   // every engine loop +50% louder (strawberry 2026-07-15) -- amplitude x1.5 = +3.5 dB
        const float IdleRpm = 1000f, MaxRpm = 6000f;   // source EngineIdleRPM / EngineMaxRPM
        public float EngineRpm => _engineRpm;
        public string GearLabel => LinearVelocity.LengthSquared() < 0.25f ? "N" : (LinearVelocity.Dot(-GlobalTransform.Basis.Z) < -0.5f ? "R" : $"G{_gear}");   // N stopped / R reversing / G<n>
        public float EngineRpmNorm => Mathf.Clamp((_engineRpm - IdleRpm) / (MaxRpm - IdleRpm), 0f, 1f);
        public int Gear => _gear;
        // vehicle status for the HUD (source InteractableVehicle): fuel drains while the engine's on; health = damage; battery = accessories
        public float Fuel, FuelMax, Health, HealthMax, Battery;
        public bool EngineOn; public string DisplayName; public Vector3 SeatOffset;   // per-vehicle driver-seat spot for the 3rd-person body
        public Vector3 DriverEyeLocal = new Vector3(-0.4f, 1.85f, 0.4f);   // FP driving eye (local); tall cabs override higher so the view clears the hood

        // --- trailer hitch (master steer: back the cab under the trailer, hop out, walk to the hitch, F to couple; then
        // the trailer swings behind on the pin like a real rig). A PinJoint3D pins the cab's fifth-wheel to the trailer
        // kingpin -> a ball joint that lets the trailer articulate (yaw through turns) around the coupling point. ---
        public Vector3 FifthWheelLocal, KingpinLocal;   // local coupling points (cab plate / trailer kingpin); Zero = none
        public bool CanTow => FifthWheelLocal != Vector3.Zero;
        public bool IsTrailer => KingpinLocal != Vector3.Zero;
        public Vehicle CoupledTrailer, CoupledCab;       // partner when hitched (cab -> trailer, trailer -> cab)
        CollisionShape3D _landingGear;                   // trailer's front landing-leg support: enabled (down) when parked, disabled (retracted) while towed
        MeshInstance3D _landingLegMesh;                  // trailer's landing-leg VISUAL (split out of the body mesh) -> hidden while coupled so the legs vanish, shown when parked (mirrors _landingGear)
        PinJoint3D _hitch;                               // the coupling constraint (owned by the cab; freed on uncouple)
        readonly System.Collections.Generic.List<CollisionShape3D> _extraShapes = new();  // the Spec.ExtraBoxes hulls (cab: the low rear frame; trailer: headboard + gooseneck) -- kept SOLID; a towed trailer ghosts vs the cab by a layer swap, not by disabling shapes (would hole the player)
        uint _baseCollisionLayer;                        // the un-ghosted body layer (bit0|bit5); a towed/backing-under trailer swaps bit0->bit6 so the cab (mask bit0) phases it while the player (mask bit6) still collides
        uint _baseCollisionMask;                         // the un-ghosted body mask; a ghosted trailer also adds bit6 so a towing cab's separate sleeper hull (layer bit6) still blocks it
        StaticBody3D _sleeperHull;                        // tow-cab only: a copy of the roof hull on a SEPARATE body (layer bit6), so the sleeper blocks the coupled trailer even though the whole cab body is excepted from it (anti-clip)
        public const float CoupleReach = 1.6f;           // max fifth-wheel<->kingpin world gap to allow a couple (back it under)
        public const float ApproachReach = 6f;           // start phasing the cab through a trailer once its fifth wheel is this close to the kingpin (so you can back all the way under to CoupleReach)
        public const float HitchReach = 3.5f;            // on-foot: how close the PLAYER must stand to the kingpin to connect/disconnect (also gates the billboard prompt)
        const float JackknifeLimit = 90f;                // trailer yaw is clamped to +-this many degrees of the cab heading (no folding into the cab)
        const float RollDisconnectDeg = 50f;             // cab OR trailer tipped past this from upright -> drop the trailer
        float _ripTimer;                                 // cab: how long the trailer's velocity has diverged hard from ours (clipped something -> yank it off)
        float _engineNoiseT;   // Phase 3 hearing: throttle the moving-car engine-noise emit
        public Vector3 BodyExtents, BodyCenter;   // BoxCollider half-size + centre (local) -> zombies reach for the body SURFACE, not the centre
        const float FuelBurnRate = 2.05f, BatteryMax = 10000f;   // EEngine.CAR default fuelBurnRate/sec; battery full = 10000
        public float FuelNorm => FuelMax > 0f ? Fuel / FuelMax : 0f;
        public float HealthNorm => HealthMax > 0f ? Health / HealthMax : 0f;
        public float BatteryNorm => Battery / BatteryMax;
        Node3D _headlights; bool _headlightsOn; StandardMaterial3D _headlightMat;   // headlights ('L'): source "Headlights" node (2 spot + 1 omni) + emission + battery burn
        Node3D _taillights; bool _taillightsOn; StandardMaterial3D _taillightMat;   // running taillights: red glow while driven (source synchronizeTaillights = isDriven && canTurnOnLights)
        bool _braking;   // cab: is the brake being applied this frame (hand/foot) -> passed through to the trailer's brake lights while towing
        StandardMaterial3D _sirenMat0, _sirenMat1; OmniLight3D _sirenLight0, _sirenLight1; bool _sirenOn; float _sirenFlash;   // emergency lightbar (police/fire/ambulance): ctrl toggles; red + blue lenses alternate every 0.33s (source UpdateSirenVisuals) + cast real colored light from each side
        AudioStreamPlayer3D _hornAudio; float _hornCd;   // horn (LMB): one-shot the .dat HornAudioClip, 0.5s cooldown (source canUseHorn)
        bool _alarmed; float _alarmTimer, _alarmBlip, _alarmCheckT = 0.3f; bool _alarmLit;   // "alarmed" car (5% of spawns): proximity (player/zombie) or damage sets off a ~30s honk+lights blip loop that lures zombies (master)
        AudioStreamPlayer3D _sirenAudio;   // looping siren clip while the emergency lightbar's on (master)
        Node3D _steerPivot; Vector3 _steerAxis;   // steering wheel model (source Objects/Steer): rotates by the steer angle around the disc normal
        const float BatteryBurnRate = 20f;   // source batteryBurnRate default (headlights drain while on, EBatteryMode.Burn)
        // Bumper roadkill (source Bumper.OnTriggerEnter + VehicleAsset ParseFloat defaults): a moving vehicle damages a
        // character its front bumper touches. dmg = floor(baseDamage * speed); speed = clamp(fwdVel * mult, -10, 10),
        // ignored below the threshold. None of the stock vehicles override these in their .dat, so the defaults hold.
        const float BumperMult = 1f, BumperThreshold = 3f, BumperZombieDmg = 15f, BumperPlayerDmg = 10f, BumperSelfMult = 1f;
        const float HornAlertRadius = 32f;   // source InteractableVehicle.tellHorn: AlertTool.alert(pos, 32) -> zombies within earshot investigate
        public bool HeadlightsOn => _headlightsOn;

        // look-at focus (master): same system as items -- a screen-space outline + an info billboard (name/HP/fuel/battery)
        bool _lookFocused; System.Collections.Generic.List<MeshInstance3D> _outlineMeshes; Label3D _infoLabel;
        Color _outlineColor = new Color(0.82f, 0.83f, 0.90f);   // vehicle outline/label tint (no per-vehicle rarity in the port yet)
        const float InfoH = 2.35f;

        // source's 2nd body BoxCollider (a slab at roof height, Godot space -- Z already negated) = the roof/frame
        // collision the port was missing (master). Jeep/Quad/Tractor are open-top -> null.
        static (Vector3 size, Vector3 center)? RoofBox(string name) => name switch
        {
            "Sedan" or "Police" => (new Vector3(2.5f, 0.254f, 2.320f), new Vector3(0f, 2.0f, 0.195f)),
            "Hatchback"         => (new Vector3(2.5f, 0.254f, 2.675f), new Vector3(0f, 2.0f, 0.723f)),
            "Humvee"            => (new Vector3(2.5f, 0.254f, 2.815f), new Vector3(0f, 2.0f, 0.050f)),
            "Roadster"          => (new Vector3(2.5f, 0.254f, 1.367f), new Vector3(0f, 2.0f, 0.672f)),
            "Bus"               => (new Vector3(3.0f, 0.512f, 7.834f), new Vector3(0f, 2.130f, 0.346f)),
            "Ambulance"         => (new Vector3(2.5f, 0.254f, 4.815f), new Vector3(0f, 2.0f, 0.087f)),
            "Firetruck"         => (new Vector3(2.5f, 0.262f, 6.803f), new Vector3(0f, 2.256f, 0.104f)),
            "Ural"              => (new Vector3(2.5f, 0.255f, 3.169f), new Vector3(0f, 2.257f, 1.570f)),
            "Semi Truck"        => (new Vector3(2.5f, 2.34f, 3.95f), new Vector3(0f, 2.67f, -0.605f)),   // tall cab+sleeper, tightened to the mesh (X±1.25, Y 1.5..3.84, Z -2.58..1.37): was ±1.59 wide + poking forward over the hood + stopping short of the sleeper back; the rest of the length stays the low frame box so a trailer interlocks over the rear
            _ => null,
        };

        struct Spec
        {
            public string Body, Wheel, WheelTex, Palette;   // Palette = paintable palette; WheelTex = wheel albedo
            public string[] DefaultPaints;   // source .dat DefaultPaintColors (random on spawn); null + !RandomHueGray = unpainted white
            public bool RandomHueGray;       // source RandomHueOrGrayscale mode (quad/sedan/hatchback)
            public float WheelRadius, Engine, SteerMax, SteerMin, SpeedMax, SpeedMin, Brake;
            public float[] WheelRadii;   // optional per-wheel radius (tractor: small front, big rear); null = uniform WheelRadius
            public Vector3 BoxSize, BoxCenter;   // source BoxCollider (Godot space: center Z negated)
            public float[] ForwardGears;   // .dat ForwardGearRatios (engine RPM = wheelRPM * ratio)
            public float ReverseGear, ShiftUpRpm;   // .dat ReverseGearRatio + GearShift_UpThresholdRPM
            public string Sound;   // engine loop ogg basename (source: the prefab's AudioSource m_audioClip)
            public float IdlePitch, MaxPitch, IdleVolume, MaxVolume;   // .dat EngineSound (EngineRPMSimple)
            public float Fuel, Health;   // .dat Fuel / Health capacities (HUD gauges)
            public EItemRarity Rarity;   // .dat Rarity (default COMMON) -> look-at outline colour (master)
            public string Name;   // display name (English.dat) for the HUD title
            public Vector3[] SpotPos; public Vector3 OmniPos;   // headlight spot beams + omni fill (prefab "Headlights", Godot space); null = no lights yet
            public Vector3[] TailPos;   // taillight spot positions (prefab "Taillights", rear, Godot space); null = emission-only
            public Vector3[] TaillightMesh;   // red taillight/brake LAMP boxes (rear) -> red running glow while driven, flare on brake; captured as _taillightMat. null = none
            public string Horn;   // .dat HornAudioClip ogg (one-shot on LMB)
            public Vector3 SteerPivot, SteerAxis;   // steering wheel model pivot (centroid) + rotation axis (disc normal); Zero = don't rotate
            public Vector3 DriverEye;   // FP driving eye offset (local); Zero = the shared default (-0.4,1.85,0.4). Tall cabs (semi) sit HIGHER so you see over the hood
            public string SeatModelFile, SteerModel;   // REAL ripped interior models re-centred into the cab (props whose body mesh has no interior sub-objects, e.g. semi). SteerModel turns via SteerPivot/SteerAxis
            public Vector3 SeatModel;   // world-target for the seat model's AABB centre (the mesh is baked at its source vehicle -> translated here)
            public (float x, float y, float z, bool steer)[] Wheels;
            public (string txt, Color color)[] Parts;   // detail meshes (root-relative) with their real solid colours
            public Vector3 FifthWheel;   // tow vehicle: local fifth-wheel coupling point (behind the cab); Zero = can't tow
            public Vector3 Kingpin;      // trailer: local kingpin point (front); Zero = not a trailer
            public Vector3 LandingGearSize, LandingGearCenter;   // trailer: front landing-leg support box (holds the nose up when parked); toggled OFF while coupled. Zero size = none
            public Vector3 LandingLegZoneMin, LandingLegZoneMax;  // trailer: mesh-space AABB enclosing the landing-leg triangles -> split them into a toggleable MeshInstance so they VANISH when coupled. Min==Max = no split
            public Vector3 HeadlightZoneMin, HeadlightZoneMax;    // AABB enclosing the baked-in headlight LENS triangles -> split them into their own mesh with an emissive material so the REAL lenses glow on 'L' (semi). Min==Max = no split
            public Vector3 TaillightZoneMin, TaillightZoneMax;    // LEFT AABB (right = X-mirror) enclosing the baked-in RED taillight triangles -> split into an emissive _taillightMat mesh so the REAL baked lights glow (trailer). Min==Max = no split
            public float LandingLegScaleY, LandingLegPivotY;      // trailer: vertically STRETCH the split-out leg mesh (scale about PivotY) so the feet reach the ground at the nose-up parked height. ScaleY 0/1 = no stretch
            public (Vector3 size, Vector3 center)[] ExtraBoxes;   // extra fixed collision boxes beyond the main box + RoofBox (e.g. the trailer's kingpin/gooseneck, the cab's low rear fifth-wheel deck) -> match the model geometry
        }

        static AudioStreamWav LoadWav(string resPath)   // load a PCM wav at runtime (no ffmpeg on the box) as a looping stream for the siren
        {
            byte[] b = System.IO.File.ReadAllBytes(ProjectSettings.GlobalizePath(resPath));
            int channels = System.BitConverter.ToInt16(b, 22), rate = System.BitConverter.ToInt32(b, 24), bits = System.BitConverter.ToInt16(b, 34);
            int dataSize = System.BitConverter.ToInt32(b, 40); byte[] pcm = new byte[dataSize]; System.Array.Copy(b, 44, pcm, 0, dataSize);
            return new AudioStreamWav { Data = pcm, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = channels == 2,
                                        LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopEnd = dataSize / (channels * bits / 8) };
        }
        static StandardMaterial3D SolidMat(Color c) =>
            new() { AlbedoColor = c, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        // billboarded smoke/fire burst using the REAL source particle texture (veh_smoke_0/veh_smoke_1/veh_fire,
        // ripped from the vehicle prefab's ParticleSystemRenderer). smoke = grey rising; fire = additive orange.
        static CpuParticles3D MakeSmoke(string texName, Color c, float life, float vel, int amount, bool fire, float sizeMin, float sizeMax)
        {
            var mat = new StandardMaterial3D
            {
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
                AlbedoColor = new Color(1f, 1f, 1f, fire ? 0.95f : 0.7f),
                BlendMode = fire ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            };
            string tp = ProjectSettings.GlobalizePath($"res://content/{texName}");
            // GenerateMipmaps: a runtime Image.LoadFromFile texture has NO mipmaps, so the default Linear-mipmap filter
            // samples BLACK once the sprite MINIFIES (small/dense particles) -> the "stationary black smoke cluster" at
            // the engine (same root cause as the old guns-render-black bug). Mips make minified particles sample grey.
            if (System.IO.File.Exists(tp)) { var img = Image.LoadFromFile(tp); if (img != null) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); } }
            if (fire)   // veh_fire.png is a 4-frame flipbook (64x16 = 4x16^2) -> animate the frames, don't stretch all 4 onto one quad (master)
            {
                mat.EmissionEnabled = true; mat.Emission = new Color(1f, 0.4f, 0.05f); mat.EmissionEnergyMultiplier = 2.5f;
                mat.ParticlesAnimHFrames = 4; mat.ParticlesAnimVFrames = 1; mat.ParticlesAnimLoop = true;
            }
            var ps = new CpuParticles3D
            {
                Emitting = false, Amount = amount, Lifetime = life, Direction = Vector3.Up, Spread = 25f,
                InitialVelocityMin = vel * 0.6f, InitialVelocityMax = vel, Gravity = new Vector3(0f, 1.5f, 0f),
                ScaleAmountMin = sizeMin, ScaleAmountMax = sizeMax, Color = c, Mesh = new QuadMesh { Size = Vector2.One, Material = mat },   // Size 1 -> ScaleAmount = the particle diameter in metres (src startSize)
            };
            if (fire) { ps.AnimOffsetMax = 1f; ps.AnimSpeedMin = 5f; ps.AnimSpeedMax = 9f; }   // random start frame + flicker through the 4
            else { ps.AngleMin = -180f; ps.AngleMax = 180f; ps.AngularVelocityMin = -35f; ps.AngularVelocityMax = 35f; }   // SMOKE (not fire): random per-puff rotation + slow tumble (master)
            return ps;
        }

        // Ground material under a wheel (raycast down from the wheel to read the collider's "surf" tag). Drives the
        // per-wheel dust tint + gate. Untagged ground defaults to grass (PEI terrain).
        PlayerController.Surf WheelSurf(VehicleWheel3D w)
        {
            var from = w.GlobalPosition;
            var to = from + Vector3.Down * (w.WheelRadius + w.SuspensionTravel + 0.4f);
            var q = PhysicsRayQueryParameters3D.Create(from, to, 1u << 0);
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is Node n && n.HasMeta(PlayerController.SurfMeta))
                return (PlayerController.Surf)(int)n.GetMeta(PlayerController.SurfMeta);
            return PlayerController.Surf.Grass;
        }

        // source Bumper.OnTriggerEnter: the front bumper roadkills a character it drives into. Damage scales with impact
        // speed (clamped at 10) x the base BumperZombieDamage; the vehicle takes a little self-damage per hit too.
        public void Wake() { Freeze = false; _parked = false; }   // resume dynamic physics (rammed or re-driven)
        void OnVehicleContact(Node body) { if (body is Vehicle v && v != this && !v._husk) v.Wake(); }   // ram a frozen parked car -> wake it (a dead husk stays put)
        public bool HasSiren => _sirenMat0 != null;   // only emergency vehicles (police/fire/ambulance) have a lightbar
        public void ToggleSiren() { if (HasSiren) _sirenOn = !_sirenOn; }   // master: ctrl toggles the siren/lightbar while driving

        void OnBumperHit(Node3D body)
        {
            if (_exploded || _parked) return;
            if (body is ZombieController z && !z.Dead)
            {
                float fwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);       // signed forward speed (front = -Z)
                float speed = Mathf.Clamp(fwd * BumperMult, -10f, 10f);
                if (speed < BumperThreshold) return;                            // too slow to hurt (source threshold gate)
                float dmg = Mathf.Floor(BumperZombieDmg * speed);               // source DamageTool: floor(damage * times)
                z.DamageHit(dmg, z.GlobalPosition, -GlobalTransform.Basis.Z);   // knock the ragdoll forward
                TakeDamage(2f * BumperSelfMult);                                // source takeCrashDamage(2)
                GD.Print($"[ROADKILL] speed={speed:0.0} dmg={dmg} -> zombie HP {z.Health:0}");
            }
            // NOTE: source Bumper also roadkills Players ("Player" tag -> BumperPlayerDamage) and Animals (Animal on the
            // "Agent" tag -> BumperAnimalDamage) the same way. No player/animal targets share a scene in the port yet,
            // so only the zombie path is wired + tested here; add those branches when those entities co-exist.
        }

        public void TakeDamage(float amount)   // source askDamage: reduce health; at 0 the EXPLODE timer starts
        {
            if (_exploded || amount <= 0f) return;
            Health = Mathf.Max(0f, Health - amount);
            TriggerAlarm();   // damaging an alarmed car sets off its alarm (master)
            if (Health <= 0f && _deadTimer < 0f)
            {
                _deadTimer = ExplodeDelay;
                EngineOn = false;   // engine dies AT 0 HP: cuts engine POWER (Drive gates on EngineOn) + the engine SOUND (audio goes silent when !EngineOn). Velocity is untouched -> the car keeps its momentum and coasts to a stop (master)
                // a SMALL fire starts the moment it hits 0 HP (master), before Explode() (4s later) ramps it to the full blaze
                if (_smoke != null) _smoke.Emitting = true;
                if (_smoke0 != null) _smoke0.Emitting = true;
                if (_fire != null) _fire.Emitting = true;
                if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 1.2f; }   // dim glow now; Explode() takes it to 3
            }
        }

        void Explode()   // source explode: launch up + spin, fire on, char the body, disable
        {
            if (CoupledTrailer != null || CoupledCab != null) Uncouple();   // a blown-up cab or trailer drops its partner so the wreck doesn't fling the whole rig (strawberry)
            _exploded = true;
            Freeze = false;   // unfreeze the parked/kinematic car so the wreck flies + tumbles
            foreach (var w in _wNodes) { w.SuspensionStiffness = 0.5f; w.SuspensionMaxForce = 0f; }   // KILL the suspension -> the hulk collapses flush onto its body instead of perching on ghost-wheels (master "kill it completely")
            ApplyCentralImpulse(Vector3.Up * 18000f);         // source min/maxExplosionForce straight up; boosted for a dramatic chassis fling against the 3x gravity (master: much higher)
            ApplyTorqueImpulse(new Vector3(2800f, 0f, 0f));   // source AddTorque(16,0,0)
            EngineOn = false;
            SetHeadlights(false); SetTaillights(false);   // a corpse's lamps go dark -- kill the head + tail lights (master)
            if (_fire != null) _fire.Emitting = true;
            if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 3f; }
            _burnTime = 0f;   // start the fire lifecycle (dies down at 40s, out at 60s, despawns 5 min later)
            _explosionAudio?.Play();
            if (_bodyMesh != null) _bodyMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.05f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };   // charred wreck
            SpawnWheelDebris();
            ExplodeDamage();
        }

        // source InteractableVehicle explode: DamageTool.explode(pos, radius 8, playerDmg 200, zombieDmg 200, vehicleDmg 500).
        // The 500 vehicle damage easily blows a neighbouring car too -> a staggered chain reaction; 200 wipes a nearby horde.
        void ExplodeDamage()
        {
            const float R = 8f;
            Vector3 p = GlobalPosition;
            PlayerController.Local?.FlinchFromExplosion(p, 32f, 45f);   // big vehicle blast -> strong camera shake (src Bomb_0-like: radius 32 / mag 45)
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    float d = z.GlobalPosition.DistanceTo(p);
                    if (d <= R) z.DamageHit(200f * (1f - d / R), z.GlobalPosition, (z.GlobalPosition - p).Normalized());
                }
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && v != this && !v.Exploded)
                {
                    float d = v.GlobalPosition.DistanceTo(p);
                    if (d <= R) v.TakeDamage(500f * (1f - d / R));   // chain: 500 easily blows the next car too
                }
            foreach (var n in GetTree().GetNodesInGroup("players"))
                if (n is PlayerController pl)
                {
                    float d = pl.GlobalPosition.DistanceTo(p);
                    if (d <= R) pl.TakeDamage(200f * (1f - d / R));
                }
        }

        void SpawnWheelDebris()   // source canExplode: the wheels fly off when the vehicle blows up
        {
            if (_wNodes == null || _wheelMeshRef == null) return;
            Node scene = GetTree()?.CurrentScene ?? GetParent();
            if (scene == null) return;
            var rng = new RandomNumberGenerator(); rng.Randomize();
            for (int i = 0; i < _wNodes.Length; i++)
            {
                var pos = _wMeshes[i].GlobalPosition;
                var mat = (StandardMaterial3D)_wheelMatRef.Duplicate();   // per-debris material so the 10s fade doesn't touch the car's own wheels
                var rb = new WheelDebris { Mass = 18f, Mat = mat };       // lives ~10s, fades its last second, then despawns (master)
                rb.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = _wheelR } });
                rb.AddChild(new MeshInstance3D { Mesh = _wheelMeshRef, MaterialOverride = mat, Scale = _wMeshes[i].Scale });
                scene.AddChild(rb);
                rb.GlobalPosition = pos;
                var outward = pos - GlobalPosition; outward.Y = 0f;
                outward = outward.LengthSquared() > 0.01f ? outward.Normalized() : Vector3.Right;
                rb.ApplyCentralImpulse(outward * 45f + Vector3.Up * 55f + new Vector3(rng.RandfRange(-15f, 15f), 0f, rng.RandfRange(-15f, 15f)));
                rb.AngularVelocity = new Vector3(rng.RandfRange(-12f, 12f), rng.RandfRange(-12f, 12f), rng.RandfRange(-12f, 12f));
                _wMeshes[i].Visible = false;   // hide the wheel still on the car
            }
        }

        // Unturned paintable shading: body samples the palette, paintable texels tinted by _PaintColor.
        static ShaderMaterial PaintMat(string palette, Color paint)
        {
            var sh = new Shader { Code = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/vehicle_paint.gdshader")) };
            var m = new ShaderMaterial { Shader = sh };
            var img = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{palette}"));
            m.SetShaderParameter("palette", ImageTexture.CreateFromImage(img));
            var lin = paint.SrgbToLinear();   // ALBEDO is linear; the palette texels already come through source_color (sRGB->linear), but the raw paint Vector3 did not -> #437c44 rendered as a washed-out light green. Convert so it shows true deep forest (master: "our render is diff")
            m.SetShaderParameter("paint_color", new Vector3(lin.R, lin.G, lin.B));
            return m;
        }

        // Source paint on spawn (VehicleAsset.getDefaultPaintColor): unpainted -> white; LIST mode -> a random pick from
        // the .dat's DefaultPaintColors; RandomHueOrGrayscale -> the REAL HSV roll (10% grayscale, else random hue with
        // saturation 0.15-0.7 + value 0.15-0.9, from Sedan.dat's RandomPaintColorConfiguration). Seeded by the spawn's
        // deterministic variant so each instance keeps a stable colour. (Was a hand-picked "curated" set -- not src-accurate.)
        static Color SpawnPaint(Spec s, int variant)
        {
            if (s.RandomHueGray)
            {
                var rng = new System.Random(unchecked(variant * 486187739 + 1150833019));
                float R() => (float)rng.NextDouble();
                if (R() < 0.1f) { float v = 0.15f + R() * 0.75f; return new Color(v, v, v); }   // grayscaleChance 0.1
                return Color.FromHsv(R(), 0.15f + R() * 0.55f, 0.15f + R() * 0.75f);              // hue / sat .15-.7 / val .15-.9
            }
            if (s.DefaultPaints != null && s.DefaultPaints.Length > 0)
                return new Color(s.DefaultPaints[variant % s.DefaultPaints.Length]);
            return Colors.White;   // no paint slot -> unpainted white
        }

        // Driver seat position per vehicle (prefab Seats/Seat_0, Godot space Z-negated) + a small body rise so the 3rd-person
        // driver sits in the right spot -- cars sit LEFT, the quad is CENTRED, the bus is far-left + way back (master).
        static Vector3 SeatOf(string name) => name switch
        {
            "Sedan" => new Vector3(-0.50f, -0.04f, -0.566f),
            "Hatchback" => new Vector3(-0.50f, -0.04f, -0.239f),
            "Humvee" => new Vector3(-0.50f, 0.07f, -0.480f),
            "Roadster" => new Vector3(-0.50f, -0.04f, 0.390f),
            "Bus" => new Vector3(-0.80f, -0.03f, -2.558f),
            "Quad" => new Vector3(0.00f, 0.26f, 0.557f),
            "Ambulance" => new Vector3(-0.50f, 0.12f, -1.40f),
            "Firetruck" => new Vector3(-0.50f, 0.29f, -2.40f),
            "Tractor" => new Vector3(0.00f, 0.69f, 1.10f),
            "Ural" => new Vector3(-0.50f, 0.10f, -1.21f),
            "Police" => new Vector3(-0.50f, 0.02f, -0.63f),
            _ => new Vector3(-0.50f, 0.10f, -0.024f),   // Jeep + fallback
        };

        // Jeep.dat: Speed 12.5, steer 28, front-steered, torque 2.8. Godot space (front = -Z): X +-1.30, front Z -1.40.
        static readonly Spec _jeep = new()
        {
            Body = "jeep_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "jeep_palette.png",
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // src .dat DefaultPaintColors = the 4 faction paints (#475e83 Coalition / #a69884 Desert / #437c44 Forest / #495631 Russia), random pick per spawn
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 12.5f, SpeedMin = -7f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),   // source BoxCollider
            ForwardGears = new[] { 20f, 13.7f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Medium)
            Fuel = 2000f, Health = 600f, Name = "Jeep", Horn = "carhorn_04.ogg",
            SpotPos = new[] { new Vector3(-0.979f, 0.746f, -2.49f), new Vector3(0.979f, 0.746f, -2.49f) }, OmniPos = new Vector3(0f, 0.878f, -2.47f),   // source prefab Headlights (Z negated)
            TailPos = new[] { new Vector3(-0.979f, 0.746f, 2.48f), new Vector3(0.979f, 0.746f, 2.48f) },   // source prefab Taillights (rear, Z negated)
            SteerPivot = new Vector3(-0.464f, 1.018f, -0.922f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // steering wheel centroid + disc normal (PCA)
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("jeep_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // seats: dark grey (real _Color)
                ("jeep_steer.txt", new Color(0.28f, 0.23f, 0.14f)),        // steering wheel: dark brown
                ("jeep_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // headlights: cream
                ("jeep_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // taillights: red
            },
        };

        // Semi truck cab (Semi_0 prop -> driveable, master). Model_0 = cab + chassis, 3.2w x 7.1L x 3.7h. Heavy: slow
        // steer, low top speed, big engine, tandem rear drive axles. Colours from the prop's own 4x2 palette (blue cab).
        static readonly Spec _semi = new()
        {
            Body = "semi_0.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "semi_palette.png",   // semi_palette = semi_0_albedo with texel0 (the blue body) flagged PAINTABLE (alpha 0) so the cab recolours like every other vehicle -- only the blue panels; metal/red/cream/exhaust stay fixed (strawberry)
            RandomHueGray = true,   // paintable cab -> random civilian colours per spawn (like sedan/quad/hatchback)
            WheelRadius = 0.55f, Engine = 550f, SteerMax = 22f, SteerMin = 10f, SpeedMax = 14f, SpeedMin = -4f, Brake = 34f,   // Engine 950->550: a semi accelerates SLOW + heavy (nerfed further while towing, see Drive) (strawberry 2026-07-15)
            // Cab hull matches the mesh (semi_0.txt): the CAB half (front, mesh Z -2.58..2.0) stands Y 0..1.5 with
            // the tall cab+sleeper as RoofBox("Semi Truck") on top; the REAR chassis (mesh Z 2.0..4.5) is the low
            // BLACK frame, only Y 0..0.96 -- so the trailer's deck overhangs it and the fifth wheel is exposed. The
            // old single Y0..1.5 box ran the full 7.08 length, making the black rear frame 0.54 too tall. (strawberry 2026-07-15)
            BoxSize = new Vector3(3.18f, 1.35f, 4.08f), BoxCenter = new Vector3(0f, 0.825f, -0.54f),   // cab BODY only, Z -2.58..1.5 (front face stays Z -2.58); floor raised 0->0.15 to the mesh underside (was hanging 0.15 below the chassis); behind the cab is all the low frame so the trailer nose can nestle down
            ExtraBoxes = new (Vector3, Vector3)[] { (new Vector3(2.5f, 0.76f, 3.0f), new Vector3(0f, 0.58f, 3.0f)) },   // low black rear frame (Y 0.2..0.96, Z 1.5..4.5) -- floor raised 0->0.2 to the rear-chassis underside; carries the fifth wheel, kept LOW so the coupled trailer sits on it, not over a tall box
            ForwardGears = new[] { 22f, 15f, 10f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_large.ogg", IdlePitch = 0.8f, MaxPitch = 1.5f, IdleVolume = 0.85f, MaxVolume = 1.0f,   // engine_large = the SOURCE heavy/truck engine (bus uses it); low pitch = diesel rumble (strawberry 2026-07-15)
            Fuel = 3000f, Health = 1000f, Name = "Semi Truck", Horn = "carhorn_03.ogg",   // CarHorn_03 = the SOURCE heavy-truck horn (Ural/Firetruck/Ambulance use it in vanilla; deepest of the ripped horns) (strawberry 2026-07-15)
            SpotPos = new[] { new Vector3(-1.175f, 0.86f, -2.60f), new Vector3(1.175f, 0.86f, -2.60f) }, OmniPos = Vector3.Zero,   // beam sources CENTERED on the real headlight lenses (X±1.175, Y0.86, front face Z-2.58); no middle omni fill (strawberry)
            TailPos = new[] { new Vector3(-0.82f, 0.65f, 4.45f), new Vector3(0.82f, 0.65f, 4.45f) },   // red spot sources centered on the cab taillight blocks (strawberry)
            HeadlightZoneMin = new Vector3(-1.44f, 0.66f, -2.63f), HeadlightZoneMax = new Vector3(-0.92f, 1.05f, -2.20f),   // LEFT headlight = the CREAM-texel geometry X[-1.40,-0.95] Y[0.71,1.01] near the fender (NOT the grey trim by the grille I was wrongly lighting). Verified: zone catches exactly the 20 cream tris, nothing else. right = auto X-mirror (strawberry)
            TaillightMesh = new[] { new Vector3(-0.82f, 0.65f, 4.45f), new Vector3(0.82f, 0.65f, 4.45f) },   // red brake/tail blocks on the rear frame; moved closer together again (1.035->0.82) (strawberry). Cab has NO baked taillights so these blocks ARE the cab's
            SeatModelFile = "roadster_seats.txt", SeatModel = new Vector3(0f, 2.2f, 0.3f),   // REAL ripped seats (single 2-seat row) back near the cab rear wall (strawberry: use src, not proc-gen)
            SteerModel = "jeep_steer.txt", SteerPivot = new Vector3(-0.5f, 2.1f, -0.45f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // REAL ripped steering wheel in front of the driver (back a hair -0.55->-0.45); turns 1:1 with the wheels (strawberry)
            DriverEye = new Vector3(-0.5f, 2.5f, 0.05f),   // eye above the seat, looking forward over the hood (floor ~Y1.5, roof ~Y3.85)
            WheelRadii = new[] { 0.65f, 0.65f, 0.65f, 0.65f, 0.65f, 0.65f },   // big semi tyres (mesh scales 1.24x). Axle Y kept at 0.55 so the taller tyre LIFTS the truck (ride height = radius+restLen-axleY). tandem axles spaced >1.5 apart so the fat tyres don't overlap
            Wheels = new (float, float, float, bool)[]
            {
                (-1.46f, 0.55f, -1.62f, true),  (1.46f, 0.55f, -1.62f, true),    // front axle (steered): out 1.28->1.46 (under the fender) + back -1.95->-1.62, central in the wheel-well arch (strawberry: "back + wider just a touch" more)
                (-1.28f, 0.55f,  1.90f, false), (1.28f, 0.55f,  1.90f, false),   // rear axle 1 (drive)
                (-1.28f, 0.55f,  3.70f, false), (1.28f, 0.55f,  3.70f, false),   // rear axle 2 (tandem, drive) -- moved back 3.5->3.7 (strawberry)
            },
            Parts = new (string, Color)[] { },   // Model_0 is the whole cab; no separate seat/steer/light parts
            FifthWheel = new Vector3(0f, 0.62f, 3.0f),   // over the rear tandem (moved back from 2.6 -> pivot sits further back on the cab, more trailer clearance). Y matched to the trailer kingpin's Y (0.62) so the coupled trailer rides LEVEL. (strawberry 2026-07-15)
        };

        // Semi trailer (semi_1 prop -> towable). TOWED, not driven: no engine/steer/drive (Engine=0 -> _engineForce=0
        // so its traction wheels apply no force); it's dragged behind the cab by a fifth-wheel PinJoint hitch (see
        // BuildTrailer). semi_0 (cab) + semi_1 (trailer) are one authoring set and SHARE the flat blue _MainTex
        // (verified via UnityPy: same texture path_id), so it reuses semi_0_albedo.png. Bbox 3.0w x 2.5h x 16.1L.
        // Wheels = a rear tandem bogie only; the front of the trailer rests on the cab's fifth wheel (no front axle).
        // NOTE: orientation from the rip is UNVERIFIED (catboy's flip-check rendered edge-on) -- render + eyeball
        // behind the cab; if inverted, roll the mesh 180 deg about Z (x->-x, y->(minY+maxY)-y) and re-ground.
        static readonly Spec _trailer = new()
        {
            Body = "trailer_0.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "semi_0_albedo.png",
            DefaultPaints = new[] { "#3a5a78" },   // shares the cab's blue palette
            WheelRadius = 0.55f, Engine = 0f, SteerMax = 0f, SteerMin = 0f, SpeedMax = 0f, SpeedMin = 0f, Brake = 0f,   // towed: no drive/steer/brake of its own
            // Flatbed hull matches the mesh (trailer_0.txt): the DECK is Y 0.15..1.25 (top surface ~1.25, underside
            // ~0.15) running the main bed Z -4.25..8.1 -- NOT the old Y 1.5..2.5 slab, which floated ~1.3 above the
            // real deck. The front steps down to a narrow gooseneck (±0.95) + kingpin coupler, capped by the tall
            // front headboard wall (Y up to 2.5). ExtraBoxes carry those front features. (strawberry 2026-07-15)
            BoxSize = new Vector3(3.0f, 1.10f, 12.35f), BoxCenter = new Vector3(0f, 0.70f, 1.9f),   // main flatbed deck (Y 0.15..1.25, Z -4.25..8.1)
            ExtraBoxes = new (Vector3, Vector3)[]
            {
                (new Vector3(3.0f, 1.5f, 0.5f), new Vector3(0f, 1.75f, -7.75f)),     // front headboard wall -- tightened to the MODEL: X±1.5, Y 1.0..2.5, Z -8..-7.5 (was Y0.15..2.5 Z-8..-7, too tall+deep vs the mesh) (strawberry 2026-07-15)
                (new Vector3(1.9f, 1.10f, 3.6f), new Vector3(0f, 0.70f, -5.7f)),     // gooseneck + kingpin coupler in ONE box (narrow ±0.95, Z -7.5..-3.9) -> the coupling area is a single clean hull, not a pile of overlapping boxes
            },
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,   // unused (no engine) but non-null for the drive logic
            Sound = null,   // no engine -> no engine loop
            Fuel = 1f, Health = 600f, Name = "Semi Trailer",   // Fuel=1 (never driven; >0 avoids a fuel-fraction div-by-zero); Health = design call
            TailPos = new[] { new Vector3(-1.13f, 1.0f, 8.0f), new Vector3(1.13f, 1.0f, 8.0f) },   // red spot sources centered on the trailer's baked taillights (X±1.13, Y1.0, Z8.0) (strawberry)
            TaillightZoneMin = new Vector3(-1.42f, 0.84f, 7.85f), TaillightZoneMax = new Vector3(-0.84f, 1.17f, 8.15f),   // split the REAL baked red taillights (X[0.88,1.38] Y[0.88,1.13] Z[7.90,8.10]) out -> emissive, driven by the cab pass-through. NO added blocks (was duping the baked ones) (strawberry)
            SteerPivot = Vector3.Zero, SteerAxis = Vector3.Zero,
            WheelRadii = new[] { 0.65f, 0.65f, 0.65f, 0.65f },   // big trailer tyres to match the cab. Axle Y kept at 0.55 so the taller tyre lifts the bed (matches the cab's lift, so the coupled deck rises level)
            Wheels = new (float, float, float, bool)[]
            {
                (-1.30f, 0.55f, 5.4f, false), (1.30f, 0.55f, 5.4f, false),   // rear tandem bogie -- both axles moved forward 0.3 (5.7->5.4, 7.3->7.0) (strawberry)
                (-1.30f, 0.55f, 7.0f, false), (1.30f, 0.55f, 7.0f, false),
            },
            Parts = new (string, Color)[] { },   // Model_0 is the whole trailer box; no separate parts
            Kingpin = new Vector3(0f, 0.62f, -6.6f),   // centered on the round coupler plate under the gooseneck (was a guessed 0.4,-7.5 which sat forward+low of it)
            // Front landing legs: a ground-to-deck support so the nose sits LEVEL when parked (rigid body on rear
            // wheels + this = level). Placed at Z -4.5, BEHIND where the cab's rear frame reaches under the front
            // (~Z -5.6 at couple), so the cab can still back all the way under. Toggled OFF the instant it couples.
            // Landing gear extended DOWN to Y-0.5 (box Y-0.5..1.5): parked, it props the nose ~0.5 above the body
            // origin so the connection side sits HIGHER than the coupled fifth-wheel height -> the trailer visibly
            // DROPS onto the cab when hitched (legs then retract). The leg VISUAL is stretched to match (see Build).
            LandingGearSize = new Vector3(2.24f, 1.63f, 0.5f), LandingGearCenter = new Vector3(0f, 0.315f, -4.13f),   // matches the STRETCHED leg mesh (X±1.12, Y-0.5..1.13, Z-4.38..-3.88); top capped at 1.13 so it no longer pokes above the flatbed top (1.25) (strawberry 2026-07-15)
            // the landing-leg triangles live in a clean mesh band at Z -4.5..-3.8 (feet Y0 up to the deck underside),
            // between the gooseneck (Z -5.7) and the deck front (Z -1) -> split them out so they hide when coupled
            LandingLegZoneMin = new Vector3(-1.25f, -0.05f, -4.55f), LandingLegZoneMax = new Vector3(1.25f, 1.16f, -3.75f),
            LandingLegScaleY = 1.44f, LandingLegPivotY = 1.13f,   // stretch the legs down ~0.5 (anchored at the deck ~Y1.13) so they reach the ground with the nose propped up 0.5
        };

        // Quad.dat: Speed 13.5, steer 32, front-steered, torque 4.8. X +-0.50, front Z -0.39 / rear 1.44, Y 0.20.
        static readonly Spec _quad = new()
        {
            Body = "quad_body.txt", Wheel = "quad_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "quad_palette.png",
            RandomHueGray = true,   // source RandomHueOrGrayscale -> our curated CarColors list
            WheelRadius = 0.45f, Engine = 520f, SteerMax = 32f, SteerMin = 16f, SpeedMax = 13.5f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.0f, 0.777f, 3.581f), BoxCenter = new Vector3(0f, 0.478f, 0.407f),   // source BoxCollider
            ForwardGears = new[] { 20f, 10f }, ReverseGear = 8f, ShiftUpRpm = 3000f,
            Sound = "engine_small.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Small)
            Fuel = 1000f, Health = 450f, Name = "Quad", Horn = "carhorn_01.ogg",
            SteerPivot = new Vector3(0f, 1.00f, -0.32f), SteerAxis = new Vector3(0f, 1f, 0f),   // handlebars: pivot at the prefab Steer node, yaw around vertical
            Wheels = new (float, float, float, bool)[]
            { (-0.50f, 0.20f, -0.39f, true), (0.50f, 0.20f, -0.39f, true), (-0.50f, 0.20f, 1.44f, false), (0.50f, 0.20f, 1.44f, false) },
            Parts = new (string, Color)[]
            {
                ("quad_steer.txt", new Color(0.15f, 0.15f, 0.15f)),        // handlebars: dark metal/rubber (turns with steering)
                ("quad_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("quad_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Bus.dat: Speed 12, steer 24->12, front-steered, torque 2.5. Long 4-wheeler, 10 seats.
        static readonly Spec _bus = new()
        {
            Body = "bus_body.txt", Wheel = "bus_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "bus_palette.png",
            DefaultPaints = new[] { "#d4d4d4" },   // source .dat: single near-white default
            WheelRadius = 0.6f, Engine = 780f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 12f, SpeedMin = -6f, Brake = 24f,
            BoxSize = new Vector3(3.0f, 1.018f, 7.964f), BoxCenter = new Vector3(0f, 0.361f, 0.281f),   // source BoxCollider
            ForwardGears = new[] { 20f, 14.6f }, ReverseGear = 12f, ShiftUpRpm = 4000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Large; bus MaxPitch 1.8)
            Fuel = 2250f, Health = 700f, Rarity = EItemRarity.UNCOMMON, Name = "Bus", Horn = "carhorn_04.ogg",
            Wheels = new (float, float, float, bool)[]
            { (-1.50f, 0.08f, -1.52f, true), (1.50f, 0.08f, -1.52f, true), (-1.50f, 0.08f, 2.69f, false), (1.50f, 0.08f, 2.69f, false) },
            Parts = new (string, Color)[]
            {
                ("bus_seats.txt", new Color(0.25f, 0.25f, 0.25f)),         // 10 grey seats
                ("bus_steer.txt", new Color(0.28f, 0.23f, 0.14f)),         // steering wheel brown
                ("bus_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),    // cream
                ("bus_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),    // red
            },
        };

        // Sedan.dat: Speed 16.5 (fastest so far), steer 28->14, front-steered, RandomHueOrGrayscale. 4-seat road car, ~6m long.
        static readonly Spec _sedan = new()
        {
            Body = "sedan_body.txt", Wheel = "sedan_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "sedan_palette.png",
            RandomHueGray = true,   // source RandomHueOrGrayscale -> our curated CarColors
            WheelRadius = 0.6f, Engine = 700f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 16.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.656f), BoxCenter = new Vector3(0f, 0.548f, -0.063f),   // source BoxCollider (Z negated)
            ForwardGears = new[] { 14f, 8.75f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1500f, Health = 600f, Name = "Sedan", Horn = "carhorn_02.ogg",
            SpotPos = new[] { new Vector3(-0.765f, 0.708f, -2.969f), new Vector3(0.765f, 0.708f, -2.969f) }, OmniPos = new Vector3(0f, 0.841f, -2.945f),   // prefab Headlights (Z neg)
            TailPos = new[] { new Vector3(-0.979f, 0.688f, 2.841f), new Vector3(0.979f, 0.688f, 2.841f) },   // prefab Taillights (rear, Z neg)
            SteerPivot = new Vector3(-0.464f, 0.894f, -1.416f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // steer centroid + disc normal (PCA)
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.62f, true), (1.30f, 0.25f, -1.62f, true), (-1.30f, 0.25f, 1.38f, false), (1.30f, 0.25f, 1.38f, false) },   // X +-1.30, front Z -1.62, rear 1.38
            Parts = new (string, Color)[]
            {
                ("sedan_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // 4 grey seats
                ("sedan_steer.txt", new Color(0.28f, 0.23f, 0.14f)),        // steering wheel brown
                ("sedan_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("sedan_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Hatchback.dat: Speed 15, steer 24->12, front-steered, RandomHueOrGrayscale. Compact 4-seat car (~5.5m).
        static readonly Spec _hatchback = new()
        {
            Body = "hatchback_body.txt", Wheel = "hatchback_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "hatchback_palette.png",
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 680f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 15f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.261f), BoxCenter = new Vector3(0f, 0.548f, -0.003f),
            ForwardGears = new[] { 14f, 8.75f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1500f, Health = 650f, Name = "Hatchback", Horn = "carhorn_01.ogg",
            SpotPos = new[] { new Vector3(-0.765f, 0.571f, -2.679f), new Vector3(0.765f, 0.571f, -2.679f) }, OmniPos = new Vector3(0f, 0.703f, -2.655f),
            TailPos = new[] { new Vector3(-0.979f, 0.738f, 2.677f), new Vector3(0.979f, 0.738f, 2.677f) },
            SteerPivot = new Vector3(-0.464f, 0.894f, -1.089f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.41f, true), (1.30f, 0.25f, -1.41f, true), (-1.30f, 0.25f, 1.39f, false), (1.30f, 0.25f, 1.39f, false) },
            Parts = new (string, Color)[]
            {
                ("hatchback_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("hatchback_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("hatchback_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("hatchback_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Humvee.dat: Speed 14, steer 24->12, front-steered, faction DefaultPaints (military, like the jeep). Heavy 4x4, brake 40.
        static readonly Spec _humvee = new()
        {
            Body = "humvee_body.txt", Wheel = "humvee_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "humvee_palette.png",
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // src .dat DefaultPaintColors = the 4 faction paints (#475e83 Coalition / #a69884 Desert / #437c44 Forest / #495631 Russia), random pick per spawn
            WheelRadius = 0.6f, Engine = 680f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 14f, SpeedMin = -6f, Brake = 40f,
            BoxSize = new Vector3(2.5f, 1.032f, 5.029f), BoxCenter = new Vector3(0f, 0.605f, -0.018f),
            ForwardGears = new[] { 20f, 12.56f }, ReverseGear = 8f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 2000f, Health = 550f, Name = "Humvee", Horn = "carhorn_03.ogg",
            SpotPos = new[] { new Vector3(-0.979f, 0.741f, -2.511f), new Vector3(0.979f, 0.741f, -2.511f) }, OmniPos = new Vector3(0f, 0.873f, -2.487f),
            TailPos = new[] { new Vector3(-0.979f, 0.738f, 2.548f), new Vector3(0.979f, 0.738f, 2.548f) },
            SteerPivot = new Vector3(-0.464f, 0.94f, -1.27f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("humvee_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("humvee_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("humvee_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("humvee_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Roadster.dat: Speed 19 (fastest!), steer 28->14, RandomHueOrGrayscale, its OWN horn. Fragile 2-seat sports car (Health 500).
        static readonly Spec _roadster = new()
        {
            Body = "roadster_body.txt", Wheel = "roadster_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "roadster_palette.png",
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 760f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 19f, SpeedMin = -5f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.656f), BoxCenter = new Vector3(0f, 0.548f, -0.063f),
            ForwardGears = new[] { 14f, 8f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1250f, Health = 500f, Rarity = EItemRarity.RARE, Name = "Roadster", Horn = "roadster_horn.ogg",
            SpotPos = new[] { new Vector3(-0.765f, 0.708f, -2.969f), new Vector3(0.765f, 0.708f, -2.969f) }, OmniPos = new Vector3(0f, 0.841f, -2.945f),
            TailPos = new[] { new Vector3(-0.979f, 0.688f, 2.841f), new Vector3(0.979f, 0.688f, 2.841f) },
            SteerPivot = new Vector3(-0.464f, 0.894f, -0.46f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.225f, -1.62f, true), (1.30f, 0.225f, -1.62f, true), (-1.30f, 0.225f, 1.38f, false), (1.30f, 0.225f, 1.38f, false) },
            Parts = new (string, Color)[]
            {
                ("roadster_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // 2 grey seats
                ("roadster_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("roadster_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("roadster_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Ambulance.dat: Speed 15.5, steer 28->14, front-steered 4-wheel van, white DefaultPaint, Health 600, CarHorn_03.
        static readonly Spec _ambulance = new()
        {
            Body = "ambulance_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "ambulance_palette.png",
            DefaultPaints = new[] { "#e8e8e8" },   // white ambulance
            WheelRadius = 0.6f, Engine = 700f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 15.5f, SpeedMin = -6.5f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 2.0f, 5.0f), BoxCenter = new Vector3(0f, 1.0f, 0f),   // tall van (compound BoxCollider -> one encompassing box)
            ForwardGears = new[] { 14f, 8f }, ReverseGear = 8f, ShiftUpRpm = 4500f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 2000f, Health = 600f, Rarity = EItemRarity.UNCOMMON, Name = "Ambulance", Horn = "carhorn_03.ogg",
            SpotPos = new[] { new Vector3(-0.71f, 0.74f, -2.58f), new Vector3(0.71f, 0.74f, -2.58f) }, OmniPos = new Vector3(0f, 0.87f, -2.56f),
            TailPos = new[] { new Vector3(-0.95f, 0.71f, 2.59f), new Vector3(0.95f, 0.71f, 2.59f) },
            SteerPivot = new Vector3(-0.47f, 0.99f, -2.21f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("ambulance_seats.txt", new Color(0.25f, 0.25f, 0.25f)),   // seats (Seat_0/Seat_1 extracted -- were missing, master)
                ("ambulance_steer.txt", new Color(0.15f, 0.15f, 0.15f)),   // steering wheel: dark
                ("ambulance_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("ambulance_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
                ("ambulance_siren0.txt", new Color(0.5f, 0.08f, 0.08f)),   // roof lightbar (left) red lens -- flashes with the siren
                ("ambulance_siren1.txt", new Color(0.08f, 0.12f, 0.5f)),   // roof lightbar (right) blue lens
            },
        };

        // Firetruck.dat: Speed 14.5, steer 48->24 (big), 6-wheel, red DefaultPaint, Health 700, CarHorn_03.
        static readonly Spec _firetruck = new()
        {
            Body = "firetruck_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "firetruck_palette.png",
            DefaultPaints = new[] { "#b81c1c" },   // red firetruck
            WheelRadius = 0.6f, Engine = 800f, SteerMax = 48f, SteerMin = 24f, SpeedMax = 14.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 2.0f, 7.0f), BoxCenter = new Vector3(0f, 1.0f, 0f),
            ForwardGears = new[] { 20f, 12f }, ReverseGear = 8f, ShiftUpRpm = 4000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 2250f, Health = 700f, Rarity = EItemRarity.UNCOMMON, Name = "Firetruck", Horn = "carhorn_03.ogg",
            SpotPos = new[] { new Vector3(-0.69f, 0.89f, -3.59f), new Vector3(0.69f, 0.89f, -3.59f) }, OmniPos = new Vector3(0f, 1.02f, -3.57f),
            TailPos = new[] { new Vector3(-0.98f, 0.55f, 3.64f), new Vector3(0.98f, 0.55f, 3.64f) },
            SteerPivot = new Vector3(-0.47f, 1.16f, -3.20f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            {
                (-1.30f, 0.25f, -2.33f, true), (1.30f, 0.25f, -2.33f, true),    // front (steered)
                (-1.30f, 0.25f, 0.80f, false), (1.30f, 0.25f, 0.80f, false),    // mid
                (-1.30f, 0.25f, 2.24f, false), (1.30f, 0.25f, 2.24f, false),    // rear
            },
            Parts = new (string, Color)[]
            {
                ("firetruck_seats.txt", new Color(0.25f, 0.25f, 0.25f)),   // seats (Seat_0/Seat_1 extracted -- were missing, master)
                ("firetruck_steer.txt", new Color(0.15f, 0.15f, 0.15f)),
                ("firetruck_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("firetruck_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
                ("firetruck_siren0.txt", new Color(0.5f, 0.08f, 0.08f)),   // roof lightbar (left) red lens -- flashes with the siren
                ("firetruck_siren1.txt", new Color(0.08f, 0.12f, 0.5f)),   // roof lightbar (right) blue lens
            },
        };

        // Tractor_0.dat: Speed 10 (slow), steer 24->12, front-steered, big-rear/small-front wheels, green, Health 700, CarHorn_03.
        static readonly Spec _tractor = new()
        {
            Body = "tractor_body.txt", Wheel = "tractor_wheel_front.txt", WheelTex = "tractor_wheel_albedo.png", Palette = "tractor_palette.png",
            DefaultPaints = new[] { "#3f7d2f" },   // green tractor
            WheelRadius = 0.90f, WheelRadii = new[] { 0.90f, 0.90f, 1.05f, 1.05f },   // src Tractor_0 Tire WheelCollider radii: 0.90 front / 1.05 rear (the real yellow tractor wheel model)
            Engine = 620f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 10f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.5f, 1.8f, 4.78f), BoxCenter = new Vector3(0f, 0.72f, -0.12f),
            ForwardGears = new[] { 20f, 12f }, ReverseGear = 8f, ShiftUpRpm = 3000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 2000f, Health = 700f, Name = "Tractor", Horn = "carhorn_03.ogg",
            SpotPos = new[] { new Vector3(-0.40f, 1.26f, -2.65f), new Vector3(0.40f, 1.26f, -2.65f) }, OmniPos = new Vector3(0f, 1.40f, -2.62f),
            TailPos = new[] { new Vector3(0.70f, 1.08f, 2.45f), new Vector3(-0.70f, 1.08f, 2.45f) },
            SteerPivot = new Vector3(0f, 1.56f, -0.29f), SteerAxis = new Vector3(0f, 0.5f, 0.866f),   // upright tractor column
            Wheels = new (float, float, float, bool)[]
            { (-0.903f, 0.450f, -1.545f, true), (0.903f, 0.450f, -1.545f, true), (-1.505f, 0.525f, 1.359f, false), (1.505f, 0.525f, 1.359f, false) },   // src Tire WheelCollider positions (Z-flipped): front y0.45 / rear y0.525
            Parts = new (string, Color)[]
            {
                ("tractor_steer.txt", new Color(0.15f, 0.15f, 0.15f)),
                ("tractor_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("tractor_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Ural.dat: Speed 14.5, steer 48->24, 6-wheel military truck, forest DefaultPaint, Health 700, CarHorn_03.
        static readonly Spec _ural = new()
        {
            Body = "ural_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "ural_palette.png",
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // src 4 faction paints (Coalition/Desert/Forest/Russia)
            WheelRadius = 0.6f, Engine = 800f, SteerMax = 48f, SteerMin = 24f, SpeedMax = 14.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 2.0f, 6.6f), BoxCenter = new Vector3(0f, 1.0f, 0f),
            ForwardGears = new[] { 20f, 12f }, ReverseGear = 8f, ShiftUpRpm = 4000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 2500f, Health = 700f, Rarity = EItemRarity.RARE, Name = "Ural", Horn = "carhorn_03.ogg",
            SpotPos = new[] { new Vector3(-0.97f, 0.78f, -3.12f), new Vector3(0.97f, 0.78f, -3.12f) }, OmniPos = new Vector3(0f, 0.91f, -3.10f),
            TailPos = new[] { new Vector3(-0.98f, 0.73f, 3.30f), new Vector3(0.98f, 0.73f, 3.30f) },
            SteerPivot = new Vector3(-0.47f, 1.03f, -2.11f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            {
                (-1.30f, 0.25f, -2.32f, true), (1.30f, 0.25f, -2.32f, true),    // front (steered)
                (-1.30f, 0.25f, 0.80f, false), (1.30f, 0.25f, 0.80f, false),    // mid
                (-1.30f, 0.25f, 2.20f, false), (1.30f, 0.25f, 2.20f, false),    // rear
            },
            Parts = new (string, Color)[]
            {
                ("ural_steer.txt", new Color(0.15f, 0.15f, 0.15f)),
                ("ural_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("ural_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Police.dat: Speed 17, steer 28->14, front-steered cruiser, paintable livery, Health 600, CarHorn_02.
        static readonly Spec _police = new()
        {
            Body = "police_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "police_palette.png",
            DefaultPaints = new[] { "#d4d4d4" },   // source Police.dat DefaultPaintColors = #d4d4d4 (white body; the palette's black livery = a black/white cruiser)
            WheelRadius = 0.6f, Engine = 720f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 17f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.656f), BoxCenter = new Vector3(0f, 0.548f, -0.063f),
            ForwardGears = new[] { 14f, 8f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1750f, Health = 600f, Rarity = EItemRarity.UNCOMMON, Name = "Police", Horn = "carhorn_02.ogg",
            SpotPos = new[] { new Vector3(-0.77f, 0.71f, -2.97f), new Vector3(0.77f, 0.71f, -2.97f) }, OmniPos = new Vector3(0f, 0.84f, -2.95f),
            TailPos = new[] { new Vector3(-0.98f, 0.69f, 2.84f), new Vector3(0.98f, 0.69f, 2.84f) },
            SteerPivot = new Vector3(-0.47f, 0.90f, -1.42f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.60f, true), (1.30f, 0.25f, -1.60f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("sedan_seats.txt", new Color(0.25f, 0.25f, 0.25f)),   // police reuses the sedan seats (no separate Seat node; sedan-class body) -- were missing, master
                ("police_steer.txt", new Color(0.15f, 0.15f, 0.15f)),
                ("police_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("police_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
                ("police_siren0.txt", new Color(0.5f, 0.08f, 0.08f)),   // roof lightbar (left) red lens -- flashes with the siren
                ("police_siren1.txt", new Color(0.08f, 0.12f, 0.5f)),   // roof lightbar (right) blue lens
            },
        };

        // Off_Roader.dat: Speed -7..12.5, steer 12->24, AWD 4-wheel buggy, RandomHueOrGrayscale, Health 600, CarHorn_04.
        // Shares the jeep chassis: identical wheel/headlight/taillight/steer layout (source vehicle.prefab positions match).
        static readonly Spec _offroader = new()
        {
            Body = "offroad_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "offroad_palette.png",
            RandomHueGray = true,   // source DefaultPaintColor_Mode RandomHueOrGrayscale -> random civilian colour per spawn
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 12.5f, SpeedMin = -7f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),   // jeep-chassis BoxCollider
            ForwardGears = new[] { 20f, 13.7f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 2000f, Health = 600f, Name = "Off_Roader", Horn = "carhorn_04.ogg",
            SpotPos = new[] { new Vector3(-0.979f, 0.746f, -2.49f), new Vector3(0.979f, 0.746f, -2.49f) }, OmniPos = new Vector3(0f, 0.878f, -2.47f),   // source Headlights (Z negated)
            TailPos = new[] { new Vector3(-0.979f, 0.746f, 2.48f), new Vector3(0.979f, 0.746f, 2.48f) },   // source Taillights (Z negated)
            SteerPivot = new Vector3(-0.465f, 1.022f, -0.923f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // source Steer node centroid + disc normal
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("offroad_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // seats: dark grey
                ("offroad_steer.txt", new Color(0.28f, 0.23f, 0.14f)),        // steering wheel: dark brown
                ("offroad_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // headlights: cream
                ("offroad_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // taillights: red
            },
        };

        // Truck.dat: Speed -6..13.5, steer 12->24, AWD 4-wheel pickup, RandomHueOrGrayscale, Health 550, CarHorn_01. Jeep chassis; round headlights.
        static readonly Spec _truck = new()
        {
            Body = "truck_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "truck_palette.png",
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 13.5f, SpeedMin = -6f, Brake = 40f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),
            ForwardGears = new[] { 20f, 14.2f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1750f, Health = 550f, Name = "Truck", Horn = "carhorn_01.ogg",
            SpotPos = new[] { new Vector3(-0.979f, 0.741f, -2.511f), new Vector3(0.979f, 0.741f, -2.511f) }, OmniPos = new Vector3(0f, 0.873f, -2.487f),
            TailPos = new[] { new Vector3(-0.979f, 0.738f, 2.548f), new Vector3(0.979f, 0.738f, 2.548f) },
            SteerPivot = new Vector3(-0.465f, 1.027f, -1.384f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("truck_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("truck_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("truck_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("truck_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Van.dat: Speed -5..14.5, steer 12->24, AWD 4-wheel van, RandomHueOrGrayscale, Health 600, CarHorn_01. Jeep chassis; round headlights.
        static readonly Spec _van = new()
        {
            Body = "van_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "van_palette.png",
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 14.5f, SpeedMin = -5f, Brake = 35f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),
            ForwardGears = new[] { 20f, 14.4f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1500f, Health = 600f, Name = "Van", Horn = "carhorn_01.ogg",
            SpotPos = new[] { new Vector3(-0.979f, 0.741f, -2.511f), new Vector3(0.979f, 0.741f, -2.511f) }, OmniPos = new Vector3(0f, 0.873f, -2.487f),
            TailPos = new[] { new Vector3(-0.979f, 0.815f, 2.548f), new Vector3(0.979f, 0.815f, 2.548f) },
            SteerPivot = new Vector3(-0.465f, 1.027f, -1.523f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("van_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("van_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("van_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("van_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // VW_Golf.dat: Speed -6..16.5 (fast), steer 14->28, FWD 4-wheel hatch, RandomHueOrGrayscale, Health 600, CarHorn_02. Rect headlights. Curated vehicle: 256x256 Albedo_Base (alpha-0 body regions paint via the shared shader). COMMAND-ONLY (no natural PEI spawn).
        static readonly Spec _golf = new()
        {
            Body = "golf_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "golf_palette.png",
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 16.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),
            ForwardGears = new[] { 14f, 8.75f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 1500f, Health = 600f, Name = "VW_Golf", Horn = "carhorn_02.ogg",
            SpotPos = new[] { new Vector3(-0.765f, 0.708f, -2.588f), new Vector3(0.765f, 0.708f, -2.588f) }, OmniPos = new Vector3(0f, 0.841f, -2.564f),
            TailPos = new[] { new Vector3(-0.765f, 0.787f, 2.424f), new Vector3(0.765f, 0.787f, 2.424f) },
            SteerPivot = new Vector3(-0.465f, 0.897f, -1.180f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.62f, true), (1.30f, 0.25f, -1.62f, true), (-1.30f, 0.25f, 1.38f, false), (1.30f, 0.25f, 1.38f, false) },
            Parts = new (string, Color)[]
            {
                ("golf_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("golf_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("golf_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("golf_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        public static Vehicle BuildJeep(int variant = 0) => Build(_jeep, variant);
        public static Vehicle BuildQuad(int variant = 0) => Build(_quad, variant);
        public static Vehicle BuildBus(int variant = 0) => Build(_bus, variant);
        public static Vehicle BuildSedan(int variant = 0) => Build(_sedan, variant);
        public static Vehicle BuildSemi(int variant = 0) => Build(_semi, variant);
        public static Vehicle BuildTrailer(int variant = 0) => Build(_trailer, variant);
        public static Vehicle BuildHatchback(int variant = 0) => Build(_hatchback, variant);
        public static Vehicle BuildHumvee(int variant = 0) => Build(_humvee, variant);
        public static Vehicle BuildRoadster(int variant = 0) => Build(_roadster, variant);
        public static Vehicle BuildAmbulance(int variant = 0) => Build(_ambulance, variant);
        public static Vehicle BuildFiretruck(int variant = 0) => Build(_firetruck, variant);
        public static Vehicle BuildTractor(int variant = 0) => Build(_tractor, variant);
        public static Vehicle BuildUral(int variant = 0) => Build(_ural, variant);
        public static Vehicle BuildPolice(int variant = 0) => Build(_police, variant);
        public static Vehicle BuildOffRoader(int variant = 0) => Build(_offroader, variant);
        public static Vehicle BuildTruck(int variant = 0) => Build(_truck, variant);
        public static Vehicle BuildVan(int variant = 0) => Build(_van, variant);
        public static Vehicle BuildGolf(int variant = 0) => Build(_golf, variant);
        public static Vehicle BuildByName(string name, int variant = 0) => name switch { "quad" => BuildQuad(variant), "bus" => BuildBus(variant), "sedan" => BuildSedan(variant), "hatchback" => BuildHatchback(variant), "humvee" => BuildHumvee(variant), "roadster" => BuildRoadster(variant), "ambulance" => BuildAmbulance(variant), "firetruck" => BuildFiretruck(variant), "tractor" => BuildTractor(variant), "ural" => BuildUral(variant), "police" => BuildPolice(variant), "semi" => BuildSemi(variant), "trailer" => BuildTrailer(variant), "offroader" => BuildOffRoader(variant), "off_roader" => BuildOffRoader(variant), "truck" => BuildTruck(variant), "van" => BuildVan(variant), "golf" => BuildGolf(variant), "vw_golf" => BuildGolf(variant), _ => BuildJeep(variant) };
        public static readonly string[] SpecNames = { "jeep", "quad", "bus", "sedan", "hatchback", "humvee", "roadster", "ambulance", "firetruck", "tractor", "ural", "police", "semi", "trailer", "offroader", "truck", "van", "golf" };   // F1 dev-console autocomplete + validation ("golf" = VW_Golf, command-only, no natural spawn)

        static Vehicle Build(Spec s, int variant)
        {
            var v = new Vehicle { Mass = GlobalMass };   // source uses one constant mass (2.0) for ALL vehicles -> one global Godot mass
            v.CollisionLayer |= 1u << 5;   // bit 5 = "vehicle" so player bullets can raycast-hit it (see PlayerController.StepBullets)
            v._baseCollisionLayer = v.CollisionLayer;   // remember the un-ghosted layer (bit0|bit5) so a towed trailer can swap bit0->bit6 and restore it
            v._baseCollisionMask = v.CollisionMask;      // and the un-ghosted mask, so a ghosted trailer can add bit6 (to hit the cab's sleeper hull) and restore it
            v.AddToGroup("vehicles");      // so NearestVehicle + explosion damage (grenades) find every vehicle, not just harness-grouped ones
            v.ContactMonitor = true; v.MaxContactsReported = 6; v.BodyEntered += v.OnVehicleContact;   // wake a frozen parked car when another vehicle rams it (master)
            v._engineForce = s.Engine; v._steerMax = s.SteerMax; v._steerMin = s.SteerMin;
            v._speedMax = s.SpeedMax; v._speedMin = s.SpeedMin; v._brakeForce = s.Brake;
            v.FifthWheelLocal = s.FifthWheel; v.KingpinLocal = s.Kingpin;   // trailer-hitch coupling points (Zero = neither)
            v._steerTurnSpeed = s.SteerMax * 2f;   // master: ramp to full lock a LOT longer than source (source default = SteerMax*5 deg/s) -> slower turn-in
            v._gears = s.ForwardGears; v._reverseGear = s.ReverseGear; v._shiftUpRpm = s.ShiftUpRpm;
            v._idlePitch = s.IdlePitch; v._maxPitch = s.MaxPitch; v._idleVol = s.IdleVolume; v._maxVol = s.MaxVolume;
            v.FuelMax = v.Fuel = s.Fuel; v.HealthMax = v.Health = s.Health; v.Battery = BatteryMax; v.DisplayName = s.Name; v.SeatOffset = SeatOf(s.Name);
            if (s.DriverEye != Vector3.Zero) v.DriverEyeLocal = s.DriverEye;   // tall-cab override (semi); else keep the shared default
            v._outlineColor = ItemAsset.RarityColorUI(s.Rarity);   // real vehicle rarity -> look-at outline/label colour (master)
            v._infoLabel = new Label3D   // look-at info billboard (name/HP/fuel/battery), TopLevel so it floats above in world space
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, TopLevel = true, Visible = false,
                Modulate = v._outlineColor, PixelSize = 0.0055f, NoDepthTest = true, FontSize = 52, OutlineSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            v.AddChild(v._infoLabel);

            var paint = SpawnPaint(s, variant);   // the source spawn paint by variant: default-list / curated car colour / white
            Material bodyMat = s.Palette != null
                ? PaintMat(s.Palette, paint)
                : new StandardMaterial3D { AlbedoColor = paint, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            ArrayMesh bodyMesh; ArrayMesh legMesh = null, hlMesh = null, tlMesh = null;
            // baked taillight zone pair (LEFT + its X-mirror), when the body has REAL red taillights to split out (trailer)
            (Vector3, Vector3)[] tlZones = s.TaillightZoneMin != s.TaillightZoneMax
                ? new[] { (s.TaillightZoneMin, s.TaillightZoneMax),
                          (new Vector3(-s.TaillightZoneMax.X, s.TaillightZoneMin.Y, s.TaillightZoneMin.Z), new Vector3(-s.TaillightZoneMin.X, s.TaillightZoneMax.Y, s.TaillightZoneMax.Z)) }
                : null;
            if (s.LandingLegZoneMin != s.LandingLegZoneMax && tlZones != null)   // trailer: peel BOTH the landing legs AND the baked taillights in one pass
                (bodyMesh, legMesh, tlMesh) = ContentProvider.ParseObjSplit2($"res://content/{s.Body}", new[] { (s.LandingLegZoneMin, s.LandingLegZoneMax) }, tlZones);
            else if (s.LandingLegZoneMin != s.LandingLegZoneMax)   // split the baked-in landing legs into their own mesh so they can vanish on couple
                (bodyMesh, legMesh) = ContentProvider.ParseObjSplitByZone($"res://content/{s.Body}", s.LandingLegZoneMin, s.LandingLegZoneMax);
            else if (s.HeadlightZoneMin != s.HeadlightZoneMax)   // split the baked-in headlight LENSES out (LEFT zone + its X-mirror) so the REAL geometry emits on 'L'; two zones keep the grille strip BETWEEN the lights out of the split (strawberry)
            {
                var lz = (s.HeadlightZoneMin, s.HeadlightZoneMax);
                var rz = (new Vector3(-s.HeadlightZoneMax.X, s.HeadlightZoneMin.Y, s.HeadlightZoneMin.Z), new Vector3(-s.HeadlightZoneMin.X, s.HeadlightZoneMax.Y, s.HeadlightZoneMax.Z));
                (bodyMesh, hlMesh) = ContentProvider.ParseObjSplitByZone($"res://content/{s.Body}", new[] { lz, rz });
            }
            else
                bodyMesh = ContentProvider.ParseObj($"res://content/{s.Body}");
            v._bodyMesh = new MeshInstance3D { Name = "Body", Mesh = bodyMesh, MaterialOverride = bodyMat };
            v.AddChild(v._bodyMesh);
            if (legMesh != null)   // the landing legs as a sibling MeshInstance sharing the body material -> toggled with the coupling (visible when parked, hidden when towed)
            {
                v._landingLegMesh = new MeshInstance3D { Name = "LandingLegs", Mesh = legMesh, MaterialOverride = bodyMat };
                if (s.LandingLegScaleY > 0f && s.LandingLegScaleY != 1f)   // vertically stretch the legs (about the deck pivot) so the feet reach the ground at the nose-up parked stance
                {
                    v._landingLegMesh.Scale = new Vector3(1f, s.LandingLegScaleY, 1f);
                    v._landingLegMesh.Position = new Vector3(0f, s.LandingLegPivotY * (1f - s.LandingLegScaleY), 0f);
                }
                v.AddChild(v._landingLegMesh);
            }
            if (hlMesh != null)   // the REAL baked headlight lenses as their own mesh -> cream, and emit on 'L' like a car (SetHeadlights drives _headlightMat)
            {
                var hlMat = new StandardMaterial3D { AlbedoColor = new Color(0.94f, 0.89f, 0.73f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                v.AddChild(new MeshInstance3D { Name = "Headlights", Mesh = hlMesh, MaterialOverride = hlMat });
                v._headlightMat = hlMat;
            }
            if (tlMesh != null)   // the REAL baked RED taillights as their own mesh -> _taillightMat, so they glow while driven / on brake (trailer: driven by the cab pass-through). No added blocks -> no dupe (strawberry)
            {
                var tlMat = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.06f, 0.06f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                v.AddChild(new MeshInstance3D { Name = "Taillights", Mesh = tlMesh, MaterialOverride = tlMat });
                v._taillightMat = tlMat;
            }

            // source BoxCollider hull (Godot space), not the mesh AABB (which wrongly included the roll bar)
            v.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = s.BoxSize }, Position = s.BoxCenter });
            var roof = RoofBox(s.Name);   // source 2nd body box (roof slab): the port only had the main box, so the roof had no collision (master); jeep/quad/tractor are open, no roof
            if (roof.HasValue)
            {
                v.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = roof.Value.size }, Position = roof.Value.center });
                if (v.CanTow)   // a tow-cab excepts its WHOLE body from the coupled trailer (CoupleTo) so the low coupling area doesn't fight the pin joint -- which also lets the trailer phase through the sleeper. Put a COPY of the roof hull on a SEPARATE static body (layer bit6) so the sleeper still blocks the trailer deck/headboard (anti-clip). The coupled trailer scans bit6 (SetTowGhost), so it hits this; the cab (mask bit0) never scans bit6, so it can't fight its own child hull. (strawberry 2026-07-16)
                {
                    var sleeper = new StaticBody3D { Name = "SleeperHull", CollisionLayer = 1u << 6, CollisionMask = 0 };
                    sleeper.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = roof.Value.size }, Position = roof.Value.center });
                    v.AddChild(sleeper);
                    v._sleeperHull = sleeper;
                }
            }
            if (s.ExtraBoxes != null) foreach (var (size, center) in s.ExtraBoxes)   // fixed extra hull boxes matching model geometry (trailer flatbed deck/headboard/gooseneck+kingpin, cab's low black rear frame)
            {
                var cs = new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = center };
                v.AddChild(cs); v._extraShapes.Add(cs);   // tracked so a towing cab can drop its rear frame to back under a trailer
            }
            if (s.LandingGearSize != Vector3.Zero)   // trailer front landing legs -> holds the nose level when parked; CoupleTo disables it (retracts) while towed
            {
                v._landingGear = new CollisionShape3D { Name = "LandingGear", Shape = new BoxShape3D { Size = s.LandingGearSize }, Position = s.LandingGearCenter };
                v.AddChild(v._landingGear);
            }
            v.BodyExtents = s.BoxSize * 0.5f; v.BodyCenter = s.BoxCenter;   // for the zombie swipe-reach

            // front bumper trigger (source Bumper): a forward volume that roadkills characters (enemy layer bit 1) the
            // vehicle drives into. Trigger only -- the body's own mask ignores the enemy layer, so it plows through.
            var bumper = new Area3D { CollisionLayer = 0, CollisionMask = 1u << 1 };
            float frontZ = s.BoxCenter.Z - s.BoxSize.Z * 0.5f;   // front face (forward = -Z)
            bumper.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(s.BoxSize.X, s.BoxSize.Y, 0.8f) }, Position = new Vector3(s.BoxCenter.X, s.BoxCenter.Y, frontZ - 0.2f) });
            v.AddChild(bumper);
            bumper.BodyEntered += v.OnBumperHit;

            var wheelMesh = ContentProvider.ParseObj($"res://content/{s.Wheel}");
            Material wheelMat;
            if (s.WheelTex != null)   // real wheel albedo (tyre + rim), nearest-sampled like the game
            {
                var wimg = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{s.WheelTex}"));
                wheelMat = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(wimg), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            }
            else
                wheelMat = new StandardMaterial3D { AlbedoColor = new Color(0.09f, 0.09f, 0.10f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            int nw = s.Wheels.Length;
            v._wheelMeshRef = wheelMesh; v._wheelMatRef = wheelMat; v._wheelR = s.WheelRadius;   // for explosion debris
            v._wNodes = new VehicleWheel3D[nw]; v._wMeshes = new MeshInstance3D[nw];
            for (int i = 0; i < nw; i++)
            {
                var (x, y, z, steer) = s.Wheels[i];
                float wr = s.WheelRadii != null ? s.WheelRadii[i] : s.WheelRadius;   // per-wheel radius (tractor dual sizes)
                float wscale = wr / s.WheelRadius;                                   // scale the shared wheel mesh to match
                var w = new VehicleWheel3D
                {
                    Position = new Vector3(x, y, z), UseAsSteering = steer, UseAsTraction = s.Kingpin == Vector3.Zero,   // a TRAILER's wheels are passive rollers, NOT traction -- traction wheels on a towed body resist the pull
                    WheelRadius = wr, WheelRestLength = 0.25f, SuspensionTravel = 0.25f,
                    // stiffer + higher max force so 900kg doesn't compress the suspension into a permanent SQUAT; more
                    // damping to settle without bounce; higher friction slip = more TRACTION (was sliding/understeering).
                    // Trailer = low friction so the wheels free-roll behind the cab instead of gripping/dragging.
                    SuspensionStiffness = 55f, SuspensionMaxForce = 12000f, DampingCompression = 3.5f, DampingRelaxation = 4.2f, WheelFrictionSlip = s.Kingpin != Vector3.Zero ? 1.5f : 6.0f,
                };
                // left wheels: flip the mesh so the tread faces outward
                var mi = new MeshInstance3D { Mesh = wheelMesh, MaterialOverride = wheelMat, Scale = new Vector3((x < 0 ? -1f : 1f) * wscale, wscale, wscale) };
                w.AddChild(mi);
                v.AddChild(w);
                v._wNodes[i] = w; v._wMeshes[i] = mi;
            }

            // Drop the centre of mass to just below the axle line so the car stops rolling on turns and pitching onto its
            // nose under braking (master). Godot's auto COM sat at the body-box centre (~0.6m up) -> top-heavy + tippy.
            float comY = 0f; foreach (var wl in s.Wheels) comY += wl.y; comY = comY / s.Wheels.Length - 0.2f;
            v.CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom;
            v.CenterOfMass = new Vector3(0f, comY, 0f);

            if (s.Parts != null)   // detail meshes with their real solid colours (seats grey, lights, steering brown)
                foreach (var (txt, color) in s.Parts)
                {
                    var pm = SolidMat(color);
                    var mi = new MeshInstance3D { Mesh = ContentProvider.ParseObj($"res://content/{txt}"), MaterialOverride = pm };
                    if (txt.Contains("seat") || txt.Contains("steer")) mi.SetMeta("no_outline", true);   // interior parts -> keep OUT of the look-at outline so it's ONE silhouette, not the seats/wheel showing through the windows (master)
                    if (txt.Contains("steer") && s.SteerAxis != Vector3.Zero)   // wrap the steering wheel in a pivot at its centre so it can turn
                    {
                        v._steerPivot = new Node3D { Position = s.SteerPivot };
                        mi.Position = -s.SteerPivot;   // baked world verts render in place once the pivot sits at the centre
                        v._steerPivot.AddChild(mi);
                        v.AddChild(v._steerPivot);
                        v._steerAxis = s.SteerAxis.Normalized();
                    }
                    else v.AddChild(mi);
                    if (txt.Contains("headlight")) v._headlightMat = pm;   // capture so the lamp glows when the headlights are on
                    if (txt.Contains("taillight")) v._taillightMat = pm;   // capture so the taillight glows red while driving
                    if (txt.Contains("siren0")) { v._sirenMat0 = pm; v._sirenLight0 = AddSirenLight(mi, new Color(1f, 0.05f, 0.05f)); }   // red lens: glow the material + cast a real red light from that side (master)
                    if (txt.Contains("siren1")) { v._sirenMat1 = pm; v._sirenLight1 = AddSirenLight(mi, new Color(0.2f, 0.3f, 1f)); }      // blue lens: material glow + real blue light from the other side
                }
            if (s.TaillightMesh != null)   // red lamp boxes at the rear -> red running glow while driven + brake flare; captured for the brake-light logic
            {
                var tlMat = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.06f, 0.06f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                var tlBox = new BoxMesh { Size = new Vector3(0.34f, 0.28f, 0.14f) };
                foreach (var p in s.TaillightMesh)
                {
                    var mi = new MeshInstance3D { Mesh = tlBox, Position = p, MaterialOverride = tlMat };
                    mi.SetMeta("no_outline", true);
                    v.AddChild(mi);
                }
                v._taillightMat = tlMat;
            }
            if (s.SeatModelFile != null)   // driver seat: the REAL ripped seat mesh, translated so its AABB centre lands at SeatModel (baked at its source vehicle) (strawberry: use src, not proc-gen)
            {
                var seatMesh = ContentProvider.ParseObj($"res://content/{s.SeatModelFile}");
                var mi = new MeshInstance3D { Mesh = seatMesh, MaterialOverride = SolidMat(new Color(0.22f, 0.22f, 0.24f)), Position = s.SeatModel - seatMesh.GetAabb().GetCenter() };
                mi.SetMeta("no_outline", true); v.AddChild(mi);
            }
            if (s.SteerModel != null && s.SteerAxis != Vector3.Zero)   // steering wheel: the REAL ripped wheel mesh, re-centred on the steer pivot so it turns 1:1 with the wheels about SteerAxis
            {
                var wMesh = ContentProvider.ParseObj($"res://content/{s.SteerModel}");
                v._steerPivot = new Node3D { Position = s.SteerPivot };
                v._steerAxis = s.SteerAxis.Normalized();
                var mi = new MeshInstance3D { Mesh = wMesh, MaterialOverride = SolidMat(new Color(0.13f, 0.11f, 0.08f)), Position = -wMesh.GetAabb().GetCenter() };
                mi.SetMeta("no_outline", true);
                v._steerPivot.AddChild(mi); v.AddChild(v._steerPivot);
            }
            if (v._sirenMat0 != null)   // emergency vehicle -> looping siren audio (master), silent until the lightbar's toggled on
            {
                v._sirenAudio = new AudioStreamPlayer3D { Stream = LoadWav("res://content/siren.wav"), UnitSize = 14f, MaxDistance = 120f, VolumeDb = 2f };
                v.AddChild(v._sirenAudio);
            }

            if (s.SpotPos != null)   // headlights: source "Headlights" node -- 2 warm spot beams + 1 omni fill at the front, off until 'L'
            {
                var warm = new Color(0.97f, 0.96f, 0.83f);
                v._headlights = new Node3D { Visible = false };
                foreach (var p in s.SpotPos)
                {
                    var hs = new SpotLight3D { Position = p, SpotRange = 45f, SpotAngle = 25f, SpotAngleAttenuation = 1.3f, LightColor = warm, LightEnergy = 9f };
                    hs.AddToGroup("dynlight");   // spills onto the FP gun (light-scan)
                    v._headlights.AddChild(hs);
                }
                if (s.OmniPos != Vector3.Zero)   // omni fill is OPTIONAL (OmniPos Zero = spots only) -- the semi drops it, its center glow read as a weird third headlight (strawberry)
                {
                    var hfill = new OmniLight3D { Position = s.OmniPos + Vector3.Up * 0.5f, OmniRange = 28f, LightColor = warm, LightEnergy = 0.8f };   // dim soft fill (raised above the seats so it doesn't glare)
                    hfill.AddToGroup("dynlight");
                    v._headlights.AddChild(hfill);
                }
                v.AddChild(v._headlights);
            }

            if (s.TailPos != null)   // running taillights: dim red spots at the rear (aim +Z, backward), on while driving
            {
                var red = new Color(0.996f, 0f, 0f);
                v._taillights = new Node3D { Visible = false };
                foreach (var p in s.TailPos)
                    v._taillights.AddChild(new SpotLight3D { Position = p, RotationDegrees = new Vector3(0f, 180f, 0f), SpotRange = 3f, SpotAngle = 72f, SpotAngleAttenuation = 0.6f, LightColor = red, LightEnergy = 2.2f });   // WIDE + SHORT diffuse red glow, not a focused red-headlight beam (SpotRange 6->3, SpotAngle 35->72, soft edge) (strawberry)
                v.AddChild(v._taillights);
            }

            if (s.Horn != null)   // horn: one-shot the .dat HornAudioClip (a shared CarHorn) on LMB
            {
                var hogg = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{s.Horn}"));
                v._hornAudio = new AudioStreamPlayer3D { Stream = hogg, UnitSize = 12f, MaxDistance = 90f, VolumeDb = 4f };
                v.AddChild(v._hornAudio);
            }

            if (s.Sound != null)   // EngineRPMSimple: a looping engine clip (the prefab AudioSource) whose pitch + volume ride the RPM
            {
                var ogg = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{s.Sound}"));
                ogg.Loop = true;
                v._engineAudio = new AudioStreamPlayer3D { Stream = ogg, UnitSize = 10f, MaxDistance = 80f, PitchScale = s.IdlePitch, VolumeDb = Mathf.LinearToDb(s.IdleVolume * EngineVolumeBoost), Autoplay = true };
                v.AddChild(v._engineAudio);   // Autoplay starts the loop when the vehicle enters the scene tree
            }

            // damage smoke + explosion fire from the engine bay (source: smoke_0/1 at health thresholds, fire + Fire light on explode)
            var firePos = new Vector3(0f, 1.24f, -1.70f);   // source Fire node (0,1.238,1.703), Z negated
            v._firePos = firePos;   // remembered so the explosion plume can emit from the engine bay in world-space
            v._smoke  = MakeSmoke("veh_smoke_1.png", new Color(0.55f, 0.55f, 0.55f), 2.2f, 2.2f, 20, false, 2.0f, 4.0f);   // light damage smoke (hp<200); src startSize 2-4m
            v._smoke0 = MakeSmoke("veh_smoke_0.png", new Color(0.30f, 0.29f, 0.27f), 2.9f, 2.9f, 28, false, 2.0f, 4.0f);   // heavy smoke (hp<100); src startSize 2-4m
            v._fire   = MakeSmoke("veh_fire.png",   new Color(1f, 0.72f, 0.32f),    0.7f, 4.5f, 30, true,  1.0f, 2.0f);   // explosion fire; src startSize 1-2m
            v._smoke.Position = firePos; v._smoke0.Position = firePos; v._fire.Position = firePos;
            v.AddChild(v._smoke); v.AddChild(v._smoke0); v.AddChild(v._fire);
            // Per-WHEEL tire dust (source Wheel.cs TireMotionEffectInstance): one emitter per wheel, spawned at that wheel's
            // ground CONTACT point, aimed UP at low speed -> tilting ~45deg backward at speed, only while grounded + moving.
            // NOTE: vanilla assigns NO TireMotionEffect to any physics material (the whole system is WIP "WipDoNotUse"), so
            // vanilla actually kicks up NOTHING -- this is an ENHANCEMENT driven by our Surf tag: soft ground (grass/dirt/sand)
            // puffs a tinted cloud; road/metal/wood stay clean.
            v._wheelDust = new CpuParticles3D[nw];
            v._wheelSurf = new PlayerController.Surf[nw];
            for (int i = 0; i < nw; i++)
            {
                var d = MakeSmoke("veh_smoke_1.png", new Color(0.55f, 0.50f, 0.40f), 0.55f, 1.4f, 8, false, 0.2f, 0.55f);
                d.Spread = 22f; d.Gravity = new Vector3(0f, -3f, 0f);   // fall back to the ground quickly
                v.AddChild(d);
                v._wheelDust[i] = d;
                v._wheelSurf[i] = PlayerController.Surf.Grass;
            }
            v._fireLight = new OmniLight3D { Position = firePos, OmniRange = 8f, LightColor = new Color(1f, 0.55f, 0.2f), LightEnergy = 0f, Visible = false };
            v._fireLight.AddToGroup("dynlight");   // a burning wreck spills onto the FP gun (light-scan)
            v.AddChild(v._fireLight);
            v._explosionAudio = new AudioStreamPlayer3D { Stream = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath("res://content/explosion.ogg")), UnitSize = 20f, MaxDistance = 200f, VolumeDb = 6f };   // boom on explode
            v.AddChild(v._explosionAudio);
            v.Brake = s.Brake * HandbrakeScale; v._parked = true;   // spawns parked: brake on + freezes once settled so it holds ride height without jitter (released once driven)
            v._alarmed = GD.Randf() < 0.05f;   // 5% of spawned cars are "alarmed" -- proximity/damage sets off the alarm loop (master)
            return v;
        }

        // throttle/brake/steer in [-1,1]; applies the source .dat handling: hard Speed_Max/Min caps + speed-dependent
        // steering (Steer_Max at rest -> Steer_Min at full speed), so the observable handling matches the game.
        public void Drive(float throttle, float steer, bool handbrake)
        {
            if (_exploded) { EngineForce = 0f; Steering = 0f; Brake = 0f; return; }   // a wrecked vehicle can't be driven
            _parked = false;
            if (!EngineOn) throttle = 0f;   // dead/off engine (e.g. 0 HP): no drive power, but the car keeps its momentum and can still steer + brake -> coasts to a stop instead of freezing (master)
            float speed = LinearVelocity.Length();   // m/s (horizontal-ish while driving)
            float fwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);   // signed forward speed (front = -Z)
            // S while rolling FORWARD (or W while rolling backward) = a foot BRAKE, not an instant reverse -- real pedal feel
            bool footBrake = (throttle < 0f && fwd > 0.6f) || (throttle > 0f && fwd < -0.6f);
            bool neutral = handbrake && speed < 0.5f;   // near-stop + handbrake -> NEUTRAL: cut engine force so a slow reverse doesn't fight the brake + jitter (master)
            float eng = (footBrake || neutral) ? 0f : throttle * _engineForce;
            if (CoupledTrailer != null) eng *= 0.5f;   // towing a loaded trailer halves the pull -> even slower accel while hooked up (strawberry 2026-07-15)
            if (throttle > 0f && speed >= _speedMax) eng = 0f;    // cap forward at Speed_Max (12.5)
            if (throttle < 0f && speed >= -_speedMin) eng = 0f;   // cap reverse at -Speed_Min (7)
            EngineForce = -eng;   // NEGATE: Godot drives this rig +Z for positive force, so W(throttle+1) was going backward
            float t = _speedMax > 0f ? Mathf.Clamp(speed / _speedMax, 0f, 1f) : 0f;   // guard div-by-0 for a towed body (_speedMax=0) -> NaN steer target; matches ForwardSpeedPct's _speedMax<=0 guard
            // target steer angle (deg); NEGATE because Godot VehicleBody3D steers LEFT for positive (D(+1)=right). 28deg at rest -> 14 at full speed.
            _steerTarget = -steer * Mathf.Lerp(_steerMax, _steerMin, t);   // smoothed toward in _PhysicsProcess (not snapped) via the AnimatedSteeringAngle-style ramp -- master confirmed the raw angle is fine
            // SPACE = handbrake (locks hard); S-into-forward-motion = foot brake. Both far stronger than the old raw .dat Brake.
            _handbraking = handbrake;   // remembered so the car freezes (no jitter) when stopped with the handbrake held
            bool coasting = Mathf.Abs(throttle) < 0.05f && !footBrake;   // no throttle + no brake input -> engine braking drags it down FASTER than pure friction (master: slow faster on its own)
            Brake = handbrake ? _brakeForce * HandbrakeScale : (footBrake ? _brakeForce * FootBrakeScale : (coasting ? _brakeForce * FootBrakeScale * 0.35f : 0f));
            _braking = handbrake || footBrake;   // remembered for the trailer brake-light pass-through (UpdateCoupled)
            if (_taillightMat != null && _taillightsOn) _taillightMat.EmissionEnergyMultiplier = _braking ? 6f : 2f;   // brake lights flare brighter while braking (master); running taillights sit at 2x
        }

        public void Park()   // driver left: smoothly damp to a stop + straighten (no hard-brake judder), then hold
        {
            _parked = true;
            EngineForce = 0f;
            _steerTarget = 0f;
            AngularVelocity = Vector3.Zero;
        }

        // --- Trailer hitch: couple/uncouple. Called from the on-foot E interaction (PlayerController). ---
        public Vector3 FifthWheelWorld => ToGlobal(FifthWheelLocal);
        public Vector3 KingpinWorld => ToGlobal(KingpinLocal);

        // an uncoupled cab whose fifth wheel is within CoupleReach of THIS trailer's kingpin -> it's backed under, ready to hitch (drives the "[F] connect trailer" billboard prompt)
        bool CabBackedUnder()
        {
            var kp = KingpinWorld;
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && v != this && v.CanTow && v.CoupledTrailer == null && v.FifthWheelWorld.DistanceSquaredTo(kp) <= CoupleReach * CoupleReach) return true;
            return false;
        }

        // Couple THIS cab to a trailer: pin the fifth-wheel to the kingpin so the trailer swings behind on the joint.
        public bool CoupleTo(Vehicle trailer)
        {
            if (!CanTow || trailer == null || !trailer.IsTrailer || CoupledTrailer != null || trailer.CoupledCab != null) return false;
            if (FifthWheelWorld.DistanceTo(trailer.KingpinWorld) > CoupleReach) return false;   // must be backed under the kingpin
            // MAGNETIZE: snap the trailer so its kingpin sits exactly under the fifth wheel -> pivot perfectly centered.
            // A pin joint can't PULL two offset anchors together (it just holds the offset), so the only real way to
            // center is to align them here. Do it on a WOKEN, zero-velocity body so the teleport adds no jolt -- the
            // jolt (not the alignment) is what locked driving when this ran on a frozen/moving body before. (strawberry)
            trailer.Wake(); trailer.LinearVelocity = Vector3.Zero; trailer.AngularVelocity = Vector3.Zero;
            trailer.GlobalPosition += FifthWheelWorld - trailer.KingpinWorld;
            var joint = new PinJoint3D { Name = "Hitch" };
            GetParent().AddChild(joint);                       // sibling of the two bodies in the world
            joint.GlobalPosition = FifthWheelWorld;            // the coupling point (kingpin now coincident with it)
            joint.NodeA = joint.GetPathTo(this);
            joint.NodeB = joint.GetPathTo(trailer);
            joint.SetParam(PinJoint3D.Param.Bias, 0.4f);       // holds the centered pivot; the pin's free rotation gives the vertical flex over bumps
            _hitch = joint; CoupledTrailer = trailer; trailer.CoupledCab = this;
            AddCollisionExceptionWith(trailer); trailer.SetTowGhost(true);   // ghost the cab<->trailer pair ONLY: the exception makes the two BODIES ignore each other (both directions -> no coupling fight, no clip), the layer swap keeps the cab's rear WHEELS off the trailer front hulls (no ride-up). Every hull stays SOLID vs the player/world -- no shape disabling, no holes (strawberry 2026-07-15)
            _approachGhost = trailer;   // remember it so Uncouple tears down the same pair
            if (trailer._landingGear != null) trailer._landingGear.Disabled = true;   // RETRACT the landing legs -> the cab's fifth wheel now carries the nose, legs would just drag
            if (trailer._landingLegMesh != null) trailer._landingLegMesh.Visible = false;   // and hide their VISUAL -> legs vanish on hookup
            Sleeping = false; trailer.Wake();                  // wake both; trailer.Wake() also clears its spawn `_parked` so it won't damp/freeze-static and anchor the tow (was the 2mph stall)
            return true;
        }

        // Uncouple: works called on either the cab or the trailer.
        public void Uncouple()
        {
            var cab = CanTow ? this : CoupledCab;
            if (cab == null || cab.CoupledTrailer == null) return;
            var trailer = cab.CoupledTrailer;
            if (cab._hitch != null && IsInstanceValid(cab._hitch)) cab._hitch.QueueFree();
            cab._hitch = null; cab.CoupledTrailer = null;
            if (trailer != null)
            {
                trailer.CoupledCab = null;
                cab.RemoveCollisionExceptionWith(trailer); trailer.SetTowGhost(false);   // restore cab<->trailer collision + the trailer's solid layer (UpdateTrailerApproach re-ghosts if the cab is still lined up under it, so it can drive out clean)
                cab._approachGhost = null;
                if (trailer._landingGear != null) trailer._landingGear.Disabled = false;   // DEPLOY the landing legs -> hold the nose level now that the cab's gone (fixes the "front sinks into the ground")
                if (trailer._landingLegMesh != null) trailer._landingLegMesh.Visible = true;   // and show their VISUAL again
                trailer.DriveTrailerLights(false, false);   // cab no longer drives them -> kill the trailer's brake/tail lights (its own logic resumes now CoupledCab is null)
                trailer.Park();   // re-park so a dropped trailer settles + freezes in place instead of free-rolling off on its low-friction wheels
            }
        }

        // Swap this trailer's body layer bit0->bit6 while a cab is coupled/backing under. This is ONLY for the cab's
        // rear-WHEEL raycasts (which ignore collision exceptions but DO respect the cab's collision_mask=bit0): off bit0,
        // the wheels stop riding up the trailer's front hulls. Body-vs-body ghosting is the exception's job (below). The
        // player (mask bit6) still collides, so no hole. Idempotent. (strawberry 2026-07-15)
        public void SetTowGhost(bool ghost)
        {
            uint wantLayer = ghost ? (_baseCollisionLayer & ~(1u << 0)) | (1u << 6) : _baseCollisionLayer;
            if (CollisionLayer != wantLayer) CollisionLayer = wantLayer;
            // Also SCAN bit6 while ghosted so the towing cab's separate sleeper hull (layer bit6) still blocks this
            // trailer -> the deck/headboard can't phase through the sleeper (anti-clip). The cab body never scans bit6,
            // so ghosting the two bodies from each other is untouched. (strawberry 2026-07-16)
            uint wantMask = ghost ? _baseCollisionMask | (1u << 6) : _baseCollisionMask;
            if (CollisionMask != wantMask) CollisionMask = wantMask;
        }

        Vehicle _approachGhost;   // cab-side: the uncoupled trailer this cab is currently ghosting itself against to back under

        // Cab-side, every physics frame while uncoupled: find a trailer we're backing under (fifth wheel within
        // ApproachReach of its kingpin) and GHOST ourselves against it -- a symmetric collision exception (cab body <->
        // trailer body ignore each other, BOTH directions, so the low deck+legs don't wall the cab off) PLUS the trailer
        // layer swap (kills the rear-wheel ride-up). Both are cab<->trailer ONLY: the player/world still hit both, no
        // holes. Dropped when we leave range. (strawberry 2026-07-15)
        void UpdateTrailerApproach()
        {
            if (!CanTow || CoupledTrailer != null) return;   // coupled -> CoupleTo owns the exception+ghost; leave it
            Vehicle near = null; float best = ApproachReach * ApproachReach;
            var fw = FifthWheelWorld;
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && v != this && v.IsTrailer && v.CoupledCab == null && fw.DistanceSquaredTo(v.KingpinWorld) < best) { near = v; break; }
            if (near == _approachGhost) return;
            if (_approachGhost != null && IsInstanceValid(_approachGhost) && _approachGhost.CoupledCab != this)
            { RemoveCollisionExceptionWith(_approachGhost); _approachGhost.SetTowGhost(false); }   // left the one we were lining up under
            _approachGhost = near;
            if (near != null) { AddCollisionExceptionWith(near); near.SetTowGhost(true); }
        }

        // Cab-side, every physics frame while COUPLED: keep the rig sane -- drop the trailer on a rollover or a hard
        // clip, and clamp the jackknife so the trailer can't fold into the cab. (strawberry 2026-07-15)
        void UpdateCoupled(Vehicle trailer, float delta)
        {
            // rollover: cab or trailer tipped past RollDisconnectDeg from upright -> drop the trailer
            if (TiltDegrees() > RollDisconnectDeg || trailer.TiltDegrees() > RollDisconnectDeg) { Uncouple(); return; }
            // clipped something: the trailer's SPEED drops hard vs ours while we're moving -> the coupling can't hold it,
            // so yank it off. Use speed MAGNITUDE difference (not the velocity vector) so hard turns -- where cab+trailer
            // move at the same speed in different directions -- don't false-rip. Persist ~0.15s so a bump doesn't rip it.
            float mismatch = Mathf.Abs(LinearVelocity.Length() - trailer.LinearVelocity.Length());
            if (LinearVelocity.Length() > 3f && mismatch > 7f) _ripTimer += delta; else _ripTimer = 0f;
            if (_ripTimer > 0.15f) { _ripTimer = 0f; Uncouple(); return; }
            ClampJackknife(trailer);
            trailer.DriveTrailerLights(EngineOn && Battery > 0f, _braking);   // pass the cab's running + brake state through to the trailer's brake lights
        }

        // total tilt (roll+pitch) of this body from upright: angle between its up axis and world up, in degrees.
        float TiltDegrees() => Mathf.RadToDeg(GlobalTransform.Basis.Y.AngleTo(Vector3.Up));

        // Clamp the trailer's yaw to +-JackknifeLimit of the cab heading. The PinJoint allows free rotation, so when the
        // relative yaw exceeds the limit we rotate the trailer back to it about the kingpin (keeps the pin satisfied) and
        // kill the angular velocity that pushed past -- a wall the trailer can't fold through into the cab.
        void ClampJackknife(Vehicle trailer)
        {
            Vector3 cabF = -GlobalTransform.Basis.Z; cabF.Y = 0f;
            Vector3 trlF = -trailer.GlobalTransform.Basis.Z; trlF.Y = 0f;
            if (cabF.LengthSquared() < 1e-4f || trlF.LengthSquared() < 1e-4f) return;
            cabF = cabF.Normalized(); trlF = trlF.Normalized();
            float yaw = cabF.SignedAngleTo(trlF, Vector3.Up);
            float lim = Mathf.DegToRad(JackknifeLimit);
            if (Mathf.Abs(yaw) <= lim) return;
            float excess = yaw - Mathf.Sign(yaw) * lim;
            Vector3 pivot = trailer.KingpinWorld;
            var rot = new Basis(Vector3.Up, -excess);
            var xf = trailer.GlobalTransform;
            xf.Origin = pivot + rot * (xf.Origin - pivot);
            xf.Basis = (rot * xf.Basis).Orthonormalized();
            trailer.GlobalTransform = xf;
            var av = trailer.AngularVelocity;
            if (Mathf.Sign(av.Y) == Mathf.Sign(yaw)) { av.Y = 0f; trailer.AngularVelocity = av; }   // stop pushing further past the limit
        }

        public float ForwardSpeedPct()   // source GetReplicatedForwardSpeedPercentageOfTargetSpeed: forward speed / top speed (0..1) for the DRIVING stealth radius
        {
            if (_speedMax <= 0f) return 0f;
            float fwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);   // signed: reversing clamps to 0 (quiet)
            return Mathf.Clamp(fwd / _speedMax, 0f, 1f);
        }

        public void Honk()   // source tellHorn: one-shot the horn; 0.5s cooldown (canUseHorn) + needs battery charge
        {
            if (_hornCd > 0f || Battery <= 0f || _hornAudio == null || _alarmTimer > 0f) return;   // can't manually honk while the alarm's blaring (master)
            DoHorn();
            _hornCd = 0.5f;
        }
        void DoHorn()   // the actual honk: a pitch-varied one-shot (master: slight variation per honk) + the zombie noise alert
        {
            if (_hornAudio == null) return;
            _hornAudio.Play();
            SoundBus.Emit(GetTree(), GlobalPosition, SoundBus.Horn);   // Phase 3 sound bus: horn loudness (source tellHorn AlertTool.alert(pos,32))
        }
        void TriggerAlarm() { if (_alarmed && _alarmTimer <= 0f) { _alarmTimer = 30f; _alarmBlip = 0f; } }   // start the ~30s honk+lights alarm loop (master)

        public void ToggleHeadlights() { if (_alarmTimer > 0f) return; SetHeadlights(!_headlightsOn); }   // source tellHeadlights; blocked while the alarm owns the lights (master)
        void SetHeadlights(bool on)
        {
            _headlightsOn = on && Battery > 0f;   // a dead battery can't power the lights
            if (_headlights != null) _headlights.Visible = _headlightsOn;
            if (_headlightMat != null)   // source: lamp emission = colour*2 when lit, off otherwise
            {
                _headlightMat.EmissionEnabled = _headlightsOn;
                if (_headlightsOn) { _headlightMat.Emission = new Color(0.97f, 0.96f, 0.83f); _headlightMat.EmissionEnergyMultiplier = 2f; }
            }
        }

        void SetTaillights(bool on)   // running taillights: red glow while driven (source: emission = colour*2)
        {
            _taillightsOn = on;
            if (_taillights != null) _taillights.Visible = on;
            if (_taillightMat != null)
            {
                _taillightMat.EmissionEnabled = on;
                if (on) { _taillightMat.Emission = new Color(0.56f, 0.13f, 0.13f); _taillightMat.EmissionEnergyMultiplier = 2f; }
            }
        }

        // Cab drives the TRAILER's tail/brake lights while coupled (the trailer has no engine of its own, so its own
        // synchronizeTaillights never fires). running = cab powered; braking = cab on the brake -> flare. The trailer skips
        // its own taillight logic while CoupledCab != null so the two don't fight. (strawberry: brake-light pass-through)
        public void DriveTrailerLights(bool running, bool braking)
        {
            if (_taillightsOn != running) SetTaillights(running);
            if (_taillightMat != null && running) _taillightMat.EmissionEnergyMultiplier = braking ? 6f : 2f;
        }

        // A real colored light cast from a lightbar lens (source Siren_0/Siren_1 are GameObjects with Unity Lights).
        // Placed at the lens mesh's centre so red emits from one side + blue from the other; off until it flashes.
        static OmniLight3D AddSirenLight(MeshInstance3D mi, Color c)
        {
            var center = mi.Mesh != null ? mi.Mesh.GetAabb().GetCenter() : Vector3.Zero;
            var light = new OmniLight3D { Position = center, OmniRange = 12f, LightColor = c, LightEnergy = 0f, ShadowEnabled = false, OmniAttenuation = 1.5f };
            light.AddToGroup("dynlight");   // spills onto the FP gun via the viewmodel light-scan (master)
            mi.AddChild(light);
            return light;
        }

        // look-at focus (master): the eye-sphere is on this vehicle -> screen-space outline (add the outline layer to every
        // vehicle mesh so OutlineOverlay's mask cam picks them up) + the info billboard. E enters (PlayerController).
        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (on || _outlineMeshes == null)   // (re)collect on FOCUS -- a settled wreck dropped its wheels, so a stale cached list would hold FREED refs
            {
                _outlineMeshes = new System.Collections.Generic.List<MeshInstance3D>();
                CollectMeshes(this, _outlineMeshes);
            }
            foreach (var mi in _outlineMeshes)
                if (IsInstanceValid(mi))   // guard freed husk meshes -- else the loop threw + aborted, leaving later meshes stuck ON the layer (outline "never reset", master)
                    mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = _outlineColor;   // OutlineOverlay tints the rim with this
            if (_infoLabel != null) _infoLabel.Visible = on;
        }

        static void CollectMeshes(Node n, System.Collections.Generic.List<MeshInstance3D> list)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is MeshInstance3D mi) list.Add(mi);   // ALL meshes incl. seats + steering wheel -> they're part of the one combined silhouette outline now (master)
                CollectMeshes(c, list);
            }
        }

        // Union of every mesh's AABB (incl. seats/steering) in WORLD space -> the look-at can focus the whole visual
        // bounds, so looking at a seat/wheel through a window still selects the car even though they have no collider (master).
        Aabb _localMeshAabb; bool _localAabbCached;
        public Aabb WorldMeshAabb()
        {
            if (!_localAabbCached)   // the mesh set is fixed after build -> compute the VEHICLE-LOCAL union ONCE (walking the tree every frame was the look-at perf regression)
            {
                var list = new System.Collections.Generic.List<MeshInstance3D>();
                CollectMeshes(this, list);
                var inv = GlobalTransform.AffineInverse();
                Aabb acc = default; bool any = false;
                foreach (var mi in list)
                {
                    if (!IsInstanceValid(mi) || mi.Mesh == null) continue;
                    var lb = mi.Mesh.GetAabb(); var rel = inv * mi.GlobalTransform;
                    for (int i = 0; i < 8; i++)
                    {
                        var c = rel * (lb.Position + lb.Size * new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1));
                        if (!any) { acc = new Aabb(c, Vector3.Zero); any = true; } else acc = acc.Expand(c);
                    }
                }
                _localMeshAabb = any ? acc.Grow(0.1f) : new Aabb(-Vector3.One, Vector3.One * 2f);
                _localAabbCached = true;
            }
            var xf = GlobalTransform; Aabb w2 = default; bool a2 = false;   // transform the cached local box into world (8 corners -- cheap)
            for (int i = 0; i < 8; i++)
            {
                var c = xf * (_localMeshAabb.Position + _localMeshAabb.Size * new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1));
                if (!a2) { w2 = new Aabb(c, Vector3.Zero); a2 = true; } else w2 = w2.Expand(c);
            }
            return w2;
        }

        // --- Look-focus HULLS (strawberry 2026-07-15): the loose WorldMeshAabb union ballooned for long/rotated vehicles
        // -- a diagonal 16 m trailer's WORLD-AXIS box engulfs the airspace over the flatbed AND overlaps the cab's box,
        // so you'd focus empty air / the wrong half. These helpers use the vehicle's REAL box collision hulls, tested
        // ORIENTED (in each box's own frame), so the focus volume hugs the silhouette at any heading. ---
        System.Collections.Generic.List<CollisionShape3D> _lookHulls;
        System.Collections.Generic.List<CollisionShape3D> LookHulls()
        {
            if (_lookHulls == null)
            {
                _lookHulls = new();
                foreach (var ch in GetChildren())   // DIRECT box CollisionShape3D children = the body hulls (main/roof/ExtraBoxes/landing gear); the bumper's shape is an Area3D grandchild + wheels are VehicleWheel3D, so both are excluded
                    if (ch is CollisionShape3D cs && cs.Shape is BoxShape3D) _lookHulls.Add(cs);
            }
            return _lookHulls;
        }

        // Does the look segment from..to cross any box hull? Each box is tested in its OWN local frame (segment pushed
        // through the shape's inverse world xf), so the AABB test is exact for an oriented box -- no world-axis bloat.
        // NOTE: does NOT skip .Disabled hulls -- look-focus tracks the VISUAL footprint, not physics. A coupled trailer
        // physics-disables its front hulls (the cab-rear-wheel fix), but the nose is still visibly there + is exactly
        // where you stand to disconnect, so it must stay look-focusable. (strawberry 2026-07-15)
        public bool LookRayHitsHull(Vector3 from, Vector3 to)
        {
            foreach (var cs in LookHulls())
            {
                if (!IsInstanceValid(cs) || cs.Shape is not BoxShape3D box) continue;
                var inv = cs.GlobalTransform.AffineInverse();
                var half = box.Size * 0.5f;
                if (new Aabb(-half, box.Size).IntersectsSegment(inv * from, inv * to)) return true;
            }
            return false;
        }

        // Look-hull boxes as (world transform, size) -- feeds the debug wireframe overlay (PlayerController "I" toggle).
        // Includes physics-disabled hulls, matching LookRayHitsHull (the look region == the visual footprint).
        public System.Collections.Generic.IEnumerable<(Transform3D xf, Vector3 size)> LookHullBoxes()
        {
            foreach (var cs in LookHulls())
                if (IsInstanceValid(cs) && cs.Shape is BoxShape3D box)
                    yield return (cs.GlobalTransform, box.Size);
        }

        // --- Wreck salvage (master): a burnt-out car can be broken down with a blowtorch into scrap metal ---
        public bool IsWreck => _exploded;
        public bool WreckOnFire => _exploded && _burnTime >= 0f && _burnTime < 60f;   // still burning -> too hot to salvage
        public bool WreckSalvageable => _exploded && _burnTime >= 60f;                // fire's out -> can be salvaged (with a blowtorch)
        // Set the look-at prompt for a focused wreck (name + salvage line) with a state colour; PlayerController drives it (it knows the blowtorch).
        public void SetSalvagePrompt(string line2, Color color)
        {
            if (_infoLabel != null) { _infoLabel.Text = $"{DisplayName}\n{line2}"; _infoLabel.Modulate = color; }
            if (_lookFocused) WorldItem.FocusColor = color;   // recolour the screen-space outline (red = can't, white = salvageable)
        }
        public bool Hurt => !_exploded && Health < HealthMax;   // alive-but-damaged -> a blowtorch can repair it (source isRepair, master)
        public void Repair(float amount) { if (!_exploded) Health = Mathf.Min(HealthMax, Health + amount); }   // blowtorch repair: heal HP up to max (source: isRepair heals instead of damaging)
        public void Salvage()   // blowtorch teardown: the cold wreck breaks apart into scrap metal on the ground, then despawns
        {
            var parent = GetParent();
            if (parent != null)
                for (int i = 0; i < 3; i++)   // a wreck yields a few Metal Scrap (item 67)
                    WorldItem.Spawn(parent, new SDG.Unturned.Item(67), GlobalPosition + new Vector3((i - 1) * 0.6f, 0.5f, 0f));
            QueueFree();
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_lookFocused && _infoLabel != null)   // keep the info billboard above the car + live (before any perf early-return)
            {
                _infoLabel.GlobalPosition = GlobalPosition + Vector3.Up * InfoH;
                if (!_exploded)   // alive car: HP/fuel/battery. A WRECK's salvage prompt is set by PlayerController (it knows the blowtorch).
                {
                    if (IsTrailer)   // a trailer has no engine -> no fuel/battery; show HP + a clear hitch state (connected / can connect / can't connect) instead
                    {
                        // only surface the connect/disconnect prompt when the player is actually standing in the hitch region (strawberry)
                        bool inHitchRange = PlayerController.Local != null && IsInstanceValid(PlayerController.Local)
                            && PlayerController.Local.GlobalPosition.DistanceTo(KingpinWorld) <= HitchReach;
                        string hint = !inHitchRange ? ""
                            : CoupledCab != null ? "\n[F] disconnect trailer"
                            : (CabBackedUnder() ? "\n[F] connect trailer" : "\ncan't connect - back a cab under");   // explicit can/can't feedback
                        _infoLabel.Text = $"{DisplayName}\nHP {Health:0}/{HealthMax:0}{hint}";
                    }
                    else
                        _infoLabel.Text = $"{DisplayName}\nHP {Health:0}/{HealthMax:0}\nFuel {Fuel:0}/{FuelMax:0}   Battery {Battery / BatteryMax * 100f:0}%";
                }
            }
            if (_burnTime >= 0f)   // wreck fire lifecycle (master): 0-40s full burn, 40-60s dying down, out at 60s (+ light killed), sits 5 min, then despawns
            {
                _burnTime += (float)delta;
                if (_burnTime < 40f) { if (_fireLight != null) _fireLight.LightEnergy = 3f; }
                else if (_burnTime < 60f)   // die down over 20s: flames + smoke fade out, fire light dims to nothing
                {
                    float f = 1f - (_burnTime - 40f) / 20f;   // 1 -> 0
                    if (_fireLight != null) _fireLight.LightEnergy = 3f * f;
                    if (_fire != null) _fire.Transparency = 1f - f;
                    if (_smoke != null) _smoke.Transparency = 1f - f;
                    if (_smoke0 != null) _smoke0.Transparency = 1f - f;
                }
                else if (_burnTime < 360f)   // EXTINGUISHED at 60s: flames+smoke off, fire light killed; stays a cold wreck for 5 min
                {
                    if (_fire != null && _fire.Emitting) _fire.Emitting = false;
                    if (_smoke != null && _smoke.Emitting) _smoke.Emitting = false;
                    if (_smoke0 != null && _smoke0.Emitting) _smoke0.Emitting = false;
                    if (_fireLight != null && _fireLight.Visible) { _fireLight.Visible = false; _fireLight.LightEnergy = 0f; }
                }
                else { QueueFree(); return; }   // 5 min after extinguishing -> despawn the wreck
            }
            if (_wNodes == null || _husk) return;   // a settled wreck is a dead husk -- no per-frame sim at all (master, perf)
            if (CanTow && CoupledTrailer != null) UpdateCoupled(CoupledTrailer, (float)delta);   // coupled: rollover/clip disconnect + jackknife clamp
            else if (CanTow) UpdateTrailerApproach();     // ghost this cab vs a trailer it's backing under (exception + layer swap) so it phases the low deck+legs; solid vs the player throughout
            if (Freeze && _deadTimer < 0f && !_alarmed)   // a frozen parked car off-screen -> skip the settle sim (but NOT an alarmed one -- its alarm keeps watching/looping); particles render on their own (master, perf)
            {
                var cam = GetViewport().GetCamera3D();
                if (cam != null && (cam.IsPositionBehind(GlobalPosition) || cam.GlobalPosition.DistanceSquaredTo(GlobalPosition) > 90000f)) return;
            }
            if (_spawnGrace > 0f) _spawnGrace -= (float)delta;   // spawn/world-init: stay DYNAMIC ~2.5s so a fresh car drops to fit terrain first
            // Freeze a settled car (source isKinematic) -- but ONLY once it's GROUNDED + fully stopped. No fixed exit-timer (that kept the
            // car dynamic ~1s -> braking jitter) and full velocity incl. vertical (so a falling/braking car never freezes mid-air). (master)
            int groundedCount = 0; foreach (var w in _wNodes) if (w.IsInContact()) groundedCount++;
            bool mostlyGrounded = groundedCount * 2 > _wNodes.Length;   // MAJORITY of wheels down = sitting level (not teetering on 1 wheel, not airborne) -- master
            bool anyGrounded = groundedCount > 0;                        // at least touching -- a wreck must be grounded to freeze so it can't stick at its own fling-apex (master "stuck in the air")
            _velAvg = _velAvg.Lerp(LinearVelocity, 0.12f);    // LOW-PASS velocity + spin (master's "check above the jitter freq"): the jitter's rapid back-and-forth
            _angAvg = _angAvg.Lerp(AngularVelocity, 0.12f);   // cancels to ~0 in the running average, but a real roll / handbrake nose-dive REBOUND (sustained,
            // directional) survives the filter -- so we wait for the suspension to normalize yet never deadlock on the jitter. Reverted to the CLEAN
            // d9588d3 low-pass (no dwell, no raised thresholds) per master. The wreck branch keeps the no-wheel-contact check (killed suspension).
            bool towed = CoupledCab != null;   // a trailer being PULLED by a cab: never let the settle/park logic freeze-static or damp it -- that would anchor the whole rig (the 2mph stall)
            bool wantHold = !towed && _angAvg.LengthSquared() < 0.03f && (_exploded ? (anyGrounded && _velAvg.LengthSquared() < 1.0f)
                                                                          : mostlyGrounded && (_parked ? (_spawnGrace <= 0f && _velAvg.LengthSquared() < 1.0f)
                                                                                                       : (_handbraking && _velAvg.LengthSquared() < 0.06f)));
            if (wantHold && !Freeze)
            {
                LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero; FreezeMode = RigidBody3D.FreezeModeEnum.Static; Freeze = true;   // STATIC not kinematic (kinematic vanished the car)
                if (_exploded) { _husk = true; foreach (var w in _wNodes) w.QueueFree(); }   // a settled wreck becomes a pure static HUSK: drop the wheels + kill the whole sim (master, perf -- lots of wrecks)
            }
            else if (!wantHold && Freeze) Freeze = false;
            bool damping = !towed && (_parked || _handbraking) && !Freeze && LinearVelocity.LengthSquared() < 2.0f;   // slowing to a stop -> DAMP the residual jitter OUT (spring + brake oscillation) instead of just waiting it out (master's "other idea"). A towed trailer never damps -- it'd anchor the tow.
            LinearDamp = damping ? 6f : 0f; AngularDamp = damping ? 6f : 0f;
            if (_parked && !Freeze && !towed) Brake = _brakeForce * HandbrakeScale;   // brake a rolling parked car down until it freezes (never brake a towed trailer)
            // NO manual wheel spin: Godot's VehicleWheel3D already bakes the ROLL (+ suspension + steering) into its own
            // node transform every physics tick, and the wheel MESH is a child that inherits it. An old manual
            // _wMeshes[i].Rotation added an equal+opposite roll that CANCELLED the node's auto-roll in world space -> the
            // wheels looked frozen (the local rotation changed, but the world basis was pinned). Verified: node world-Y
            // rolls full circle, and once the manual spin is gone the mesh world-Y rolls with it. (fable diagnosis)
            // engine RPM + gears (source InteractableVehicle): rpm = |avg wheel rpm| * gear ratio, idle-floored, then auto-shift
            float sum = 0f; foreach (var w in _wNodes) sum += Mathf.Abs(w.GetRpm());
            float avgWheelRpm = _wNodes.Length > 0 ? sum / _wNodes.Length : 0f;
            float ratio = (_gears != null && _gear >= 1 && _gear <= _gears.Length) ? _gears[_gear - 1] : 20f;
            float target = Mathf.Clamp(avgWheelRpm * ratio, IdleRpm, MaxRpm);
            _engineRpm = Mathf.Lerp(_engineRpm, target, Mathf.Min(1f, 8f * (float)delta));
            if (_gears != null && _gears.Length > 0)   // gear from SPEED band -> guaranteed clean shifts (master: never left 1st; src RPM model never redlines in gear 1 so it never shifted). RPM still sawtooths per gear via the ratio.
            {
                float fwd = Mathf.Abs(LinearVelocity.Dot(-GlobalTransform.Basis.Z));
                int newGear = Mathf.Clamp(1 + (int)(Mathf.Clamp(fwd / _speedMax, 0f, 0.999f) * _gears.Length), 1, _gears.Length);
                if (newGear != _gear && !_exploded && !_husk && fwd > 1.5f)   // gear change while moving -> a brief CLUTCH JOLT.
                {
                    // A fore-aft impulse dipped the speed under the shift point -> instant re-downshift -> STUCK shifting
                    // (master caught the loop). So the jolt is a VERTICAL hitch + pitch nod you FEEL but that doesn't
                    // touch the gear-selecting fore-aft speed.
                    ApplyCentralImpulse(Vector3.Up * Mass * 0.22f);
                    ApplyTorqueImpulse(GlobalTransform.Basis.X * Mass * 0.5f);
                }
                _gear = newGear;
            }
            if (_engineAudio != null)   // EngineRPMSimple: pitch + volume by RPM while running; silent when off (exited)
            {
                if (EngineOn)
                {
                    float n = EngineRpmNorm;
                    _engineAudio.PitchScale = Mathf.Lerp(_idlePitch, _maxPitch, n);
                    _engineAudio.VolumeDb = Mathf.LinearToDb(Mathf.Lerp(_idleVol, _maxVol, n) * EngineVolumeBoost);
                }
                else _engineAudio.VolumeDb = -80f;   // engine off -> kill the noise
            }
            // Phase 3 hearing: a running, MOVING car makes engine/tire noise zombies hear -- source DRIVING stealth
            // radius DETECT_FORWARD(48) x forward-speed% (parked/idling ~silent since speed~0). Throttled like footsteps.
            _engineNoiseT -= (float)delta;
            if (EngineOn && _engineNoiseT <= 0f)
            {
                _engineNoiseT = 0.4f;
                float loud = 48f * ForwardSpeedPct();
                if (loud > 2f) SoundBus.Emit(GetTree(), GlobalPosition, loud);
            }
            if (EngineOn && Fuel > 0f)   // source simulateBurnFuel: burn fuelBurnRate/sec while the engine runs
                Fuel = Mathf.Max(0f, Fuel - FuelBurnRate * (float)delta);
            if (_headlightsOn)   // source: headlights burn the battery (EBatteryMode.Burn); die when it's empty
            {
                Battery = Mathf.Max(0f, Battery - BatteryBurnRate * (float)delta);
                if (Battery <= 0f) SetHeadlights(false);
            }
            if (_alarmed)   // "alarmed" car (master): proximity (player/zombie) or damage sets off a ~30s honk+lights blip loop that lures zombies
            {
                if (_alarmTimer <= 0f)   // idle -> watch for a proximity trigger (throttled)
                {
                    _alarmCheckT -= (float)delta;
                    if (_alarmCheckT <= 0f)
                    {
                        _alarmCheckT = 0.3f;
                        var acam = GetViewport().GetCamera3D();
                        bool near = acam != null && acam.GlobalPosition.DistanceSquaredTo(GlobalPosition) < 49f;   // player within ~7m
                        if (!near) foreach (var z in GetTree().GetNodesInGroup("zombies")) if (z is Node3D zn && zn.GlobalPosition.DistanceSquaredTo(GlobalPosition) < 36f) { near = true; break; }   // a zombie within ~6m
                        if (near) TriggerAlarm();
                    }
                }
                else   // ALARMING: blip 0.5s on / 0.5s off for ~30s
                {
                    _alarmTimer -= (float)delta; _alarmBlip += (float)delta;
                    bool on = (_alarmBlip % 1.0f) < 0.5f;
                    if (on && !_alarmLit) { DoHorn(); SetHeadlights(true); SetTaillights(true); }   // rising edge -> honk + head+tail lights ON in sync (master); the honk lures zombies like a real horn
                    else if (!on && _alarmLit) { SetHeadlights(false); SetTaillights(false); }     // falling edge -> all lights off, NO honk
                    _alarmLit = on;
                    if (_alarmTimer <= 0f) { SetHeadlights(false); SetTaillights(false); _alarmLit = false; _alarmed = false; }   // alarm done -> killed for good, never alarms again (master)
                }
            }
            if (_sirenMat0 != null)   // emergency lightbar: alternate the red + blue lenses while the siren's on (master: ctrl toggles). Dead on a wreck.
            {
                if (_sirenOn && !_exploded)
                {
                    if (_sirenAudio != null && !_sirenAudio.Playing) _sirenAudio.Play();
                    _sirenFlash += (float)delta;
                    bool red = (_sirenFlash % 0.66f) < 0.33f;   // source UpdateSirenVisuals: sirenState flips only every 0.33s (lastWeeoo gate) -> each lens lit 0.33s; mine was toggling every 0.1s (~3x too fast, master caught it)
                    _sirenMat0.EmissionEnabled = true; _sirenMat0.Emission = new Color(1f, 0.05f, 0.05f); _sirenMat0.EmissionEnergyMultiplier = red ? 4f : 0f;
                    _sirenMat1.EmissionEnabled = true; _sirenMat1.Emission = new Color(0.1f, 0.15f, 1f); _sirenMat1.EmissionEnergyMultiplier = red ? 0f : 4f;
                    if (_sirenLight0 != null) _sirenLight0.LightEnergy = red ? 5f : 0f;   // real red light from the left lens
                    if (_sirenLight1 != null) _sirenLight1.LightEnergy = red ? 0f : 5f;   // real blue light from the right lens (master)
                }
                else { _sirenMat0.EmissionEnabled = false; _sirenMat1.EmissionEnabled = false; if (_sirenLight0 != null) _sirenLight0.LightEnergy = 0f; if (_sirenLight1 != null) _sirenLight1.LightEnergy = 0f; if (_sirenAudio != null && _sirenAudio.Playing) _sirenAudio.Stop(); }
            }
            if (_alarmTimer <= 0f && CoupledCab == null)   // the alarm owns the taillights while blaring (master); a COUPLED trailer's lights are driven by the cab (DriveTrailerLights) so it skips its own logic
            {
                bool tailWant = ((EngineOn && Battery > 0f) || _headlightsOn) && CoupledTrailer == null;   // source synchronizeTaillights = isDriven && canTurnOnLights; master ADDS headlights->tail. While TOWING the cab's own tail is off -> the trailer carries the lights (pass-through)
                if (tailWant != _taillightsOn) SetTaillights(tailWant);
            }
            if (_hornCd > 0f) _hornCd -= (float)delta;
            // collision/ram damage (source isVulnerableToBumper): a sudden horizontal deceleration = a crash. Horizontal only, so the spawn drop doesn't count.
            float curSpeed = new Vector2(LinearVelocity.X, LinearVelocity.Z).Length();
            float decel = _prevSpeed - curSpeed;
            if (!_parked && !_exploded && _prevSpeed > 5f && decel > 200f * (float)delta) TakeDamage(decel * 20f);   // >200 m/s^2 = a crash (braking is ~8); full-speed hit ~250 dmg
            _prevSpeed = curSpeed;
            if (_smoke != null) _smoke.Emitting = _burnTime < 60f && (_exploded || Health < SmokeHealth);   // source updateFires: smoke_1 at health < 200 (or exploded); OFF once the wreck fire is out at 60s (master)
            if (_smoke0 != null) _smoke0.Emitting = _exploded || Health < HeavySmokeHealth;   // source updateFires: smoke_0 (heavy) at health < 100 (or exploded)
            if (_wheelDust != null)   // per-wheel dust at each wheel's ground contact (source structure; vanilla ships none -> our Surf-driven enhancement)
            {
                float spd = new Vector2(LinearVelocity.X, LinearVelocity.Z).Length();
                bool moving = spd > 3f && !_exploded;
                // aim UP at low speed, tilt ~45deg toward backward (+Z local) approaching top speed (src blendWeight = speed% * 0.5)
                float blend = Mathf.Clamp(spd / Mathf.Max(1f, _speedMax), 0f, 1f) * 0.5f;
                var dir = new Vector3(0f, 1f, 0f).Lerp(new Vector3(0f, 0f, 1f), blend).Normalized();
                bool recheck = moving && (_dustCheckT -= (float)delta) <= 0f;   // throttle the per-wheel surface raycast
                if (recheck) _dustCheckT = 0.12f;
                for (int i = 0; i < _wNodes.Length; i++)
                {
                    var w = _wNodes[i]; var d = _wheelDust[i];
                    if (w == null || d == null) continue;
                    bool contact = moving && w.IsInContact();
                    if (contact)
                    {
                        d.Position = ToLocal(w.GetContactPoint());   // spawn at the ground hit like the source
                        if (recheck) _wheelSurf[i] = WheelSurf(w);
                    }
                    var sf = _wheelSurf[i];
                    bool soft = sf == PlayerController.Surf.Grass || sf == PlayerController.Surf.Dirt || sf == PlayerController.Surf.Sand;   // only loose ground kicks up
                    d.Direction = dir;
                    if (soft) d.Color = PlayerController.SurfDust(sf);
                    d.Emitting = contact && soft;
                }
                if (System.Environment.GetEnvironmentVariable("UG_DUSTDEBUG") == "1" && moving && (_dustLogT -= (float)delta) <= 0f)
                {
                    _dustLogT = 1f;
                    bool anyEmit = false; foreach (var d in _wheelDust) if (d != null && d.Emitting) { anyEmit = true; break; }
                    GD.Print($"[wheeldust] spd={spd:0.0} surf0={_wheelSurf[0]} anyEmit={anyEmit}");
                }
            }
            if (_exploded)   // master: explosion smoke/fire emits from the ENGINE bay (like the hurt smoke) but rises STRAIGHT UP -- world-space so the plume doesn't tilt with the tumbling wreck
            {
                var enginePos = ToGlobal(_firePos);   // engine-bay world position (rides the wreck); plume forced world-up via Rotation=0
                if (_smoke  != null) { _smoke.TopLevel  = true; _smoke.GlobalPosition  = enginePos; _smoke.Rotation  = Vector3.Zero; }
                if (_smoke0 != null) { _smoke0.TopLevel = true; _smoke0.GlobalPosition = enginePos; _smoke0.Rotation = Vector3.Zero; }
                if (_fire   != null) { _fire.TopLevel   = true; _fire.GlobalPosition   = enginePos; _fire.Rotation   = Vector3.Zero; }
            }
            if (_deadTimer > 0f) { _deadTimer -= (float)delta; if (_deadTimer <= 0f) Explode(); }   // source EXPLODE: 4s after health 0

            // steering smoothing (source: AnimatedSteeringAngle = MoveTowards(target, SteeringAngleTurnSpeed*dt)) -- no instant snap
            _steerAngle = Mathf.MoveToward(_steerAngle, _steerTarget, _steerTurnSpeed * (float)delta);
            Steering = Mathf.DegToRad(_steerAngle);
            if (_steerPivot != null) _steerPivot.Basis = new Basis(_steerAxis, Mathf.DegToRad(_steerAngle));   // steering wheel model turns 1:1 with the steer angle (source line 4020, AnimatedSteeringAngle)
        }
    }
}
