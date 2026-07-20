using Godot;

namespace UnturnedGodot
{
    // A placed deployable in the world (the result of planting a held barricade). Mesh + a box collider + health/fuel,
    // in group "deployables". Look-at gets the same screen-space outline + info billboard (name / HP / fuel) as
    // vehicles, and the same damage lifecycle: smoke at low HP, fire + explosion at 0 HP, a burning wreck that cools
    // into a blowtorch-salvageable husk (src runtime InteractableGenerator + the shared vehicle explode/salvage path).
    public partial class Deployable : StaticBody3D, IPowerDevice
    {
        public DeployableDef Def;
        public uint NetId;   // MP: the replicated entity this node mirrors (set by DeployableReplicaView); 0 = SP/local
        public readonly System.Collections.Generic.List<ConnectionPort> Ports = new();   // power connection cubes (output/consumer/passthrough)
        // IPowerDevice: how the power net sees this deployable (a gas pump implements the same interface w/o being a Deployable)
        public bool PowerProducing => IsPowered;
        public bool PowerOnFire => OnFire;
        public uint PowerNetId => NetId;
        public System.Collections.Generic.IReadOnlyList<ConnectionPort> PowerPorts => Ports;
        public float Health, HealthMax;
        public float Fuel, FuelMax;   // src InteractableGenerator: fuel drawn from Capacity; a fresh build starts FULL (matches vehicle spawn) until the refuel/power pass

        // --- damage / fire / wreck lifecycle (mirrors Vehicle) ---
        bool _exploded;
        float _deadTimer = -1f;   // >=0: counting down from 0 HP to the blast (src EXPLODE 4s)
        float _burnTime = -1f;    // seconds since the wreck caught fire: 0-40 full burn, 40-60 dying down, 60 out, 360 despawn
        CpuParticles3D _smoke, _smoke0, _fire;
        OmniLight3D _fireLight;
        MeshInstance3D _mesh;      // the body mesh (charred on explode)
        Vector3 _firePos;          // world-space fire/smoke origin (top of the object); particles are TopLevel so they rise in WORLD up despite the stood-up body basis
        const float ExplodeDelay = 4f, SmokeFrac = 0.45f, HeavyFrac = 0.22f;   // light smoke < 45% HP, heavy < 22% (vehicle uses ~200/100 of ~600)

        // --- power (src InteractableGenerator: F toggles isPowered; while on + fuelled the "Engine" node is active =
        //     the looping engine AudioSource). The src has NO vibration animation (the Engine node is just an
        //     AudioSource) -- the small shake here is a non-source touch (strawberry asked for it). ---
        bool _powered;             // target state (F toggles it)
        float _powerLevel;         // 0 = off .. 1 = running; ramps up over WarmupTime / down over CooldownTime -- the shake + engine spin-up follow it
        AudioStreamPlayer3D _engineAudio;
        float _vibePhase;
        const float WarmupTime = 1.3f, CooldownTime = 1.1f;   // spin-up / wind-down; doubles as the anti-spam buffer (can't re-toggle mid-ramp)
        public bool OnFire => _deadTimer >= 0f || _exploded;   // catching fire at 0 HP (deadTimer) through the burning wreck -> a dead/dying generator, can't be run (PowerNet reads this)
        float RunTarget => (_powered && !OnFire && FuelMax > 0f && Fuel > 0f) ? 1f : 0f;   // the engine's effective on/off: needs power ON, not on fire, and fuel left
        bool PowerSettled => Mathf.Abs(_powerLevel - RunTarget) < 0.001f;   // ramp reached its EFFECTIVE target (so a fuel-dry/on-fire gen still settles -> no toggle deadlock)
        public bool CanTogglePower => !OnFire && Def != null && Def.Fuel > 0f && PowerSettled;   // only a fuelled, NOT-on-fire generator toggles, and only once the ramp has settled (buffer)
        public bool IsPowered => Def != null && Def.IsBattery ? (Energy > 0f && !OnFire) : RunTarget > 0.5f;   // a battery's OUT produces while it has charge; a generator's while the engine runs -- PowerNet reads this
        public float Energy;   // battery: stored energy (watt-SECONDS); the OUT produces while > 0, the IN charges it up to Def.EnergyMax

        // --- consumer lamps (spotlight): src InteractableSpot.updateLights turns the "Spots" lights on when wired+powered ---
        readonly System.Collections.Generic.List<Light3D> _lamps = new();
        readonly System.Collections.Generic.List<float> _lampBase = new();   // per-lamp base energy (display = base * envelope * flicker)
        ConnectionPort _consumerPort, _outputPort;
        public float LoadFraction => _outputPort != null && GodotObject.IsInstanceValid(_outputPort) && _outputPort.Watts > 0f ? Mathf.Clamp(_outputPort.Draw / _outputPort.Watts, 0f, 1f) : 0f;   // generator: 0..1 of capacity currently drawn
        float _lampLevel;                    // 0..1 lamp envelope, ramps with power over WarmupTime/CooldownTime
        float _lampFlicker = 1f, _lampFlickerT;   // while the source spins up/down (mid-ramp) the lamp stutters (strawberry)
        static readonly bool DbgFlicker = System.Environment.GetEnvironmentVariable("UG_WIREFLICKER") == "1";

        bool _lookFocused;
        public float PickupProgress;   // 0 = idle; >0 = the hold-F pickup fraction (PlayerController drives it, the billboard shows "Picking up... X%")
        System.Collections.Generic.List<MeshInstance3D> _outlineMeshes;
        InfoBillboard _info;
        static readonly Color OutlineColor = new Color(0.82f, 0.83f, 0.90f);   // same neutral tint as vehicles (no per-deployable rarity yet)
        const float InfoH = 0.5f;   // billboard sits INSIDE the generator body (strawberry), not floating above it

        // Build the mesh + material for a def, returning the MeshInstance and its local AABB (in the flat
        // authored frame, before the -90 X stand-up). Shared by the placed object and the placement ghost.
        public static MeshInstance3D BuildMesh(DeployableDef def, out Aabb localAabb)
        {
            Mesh mesh = def.ProcBox ? new BoxMesh { Size = def.Size } : def.LoadMesh();   // splitter = a plain gray box, no .obj
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = def.MakeMaterial() };
            Basis mrot = def.MeshBasis();   // per-def model orientation fixup (battery's ripped mesh stands up upside-down + 180 off); identity for the rest
            if (mrot != Basis.Identity) mi.Basis = mrot;
            localAabb = mesh != null ? new Transform3D(mrot, Vector3.Zero) * mesh.GetAabb() : new Aabb();   // rotated aabb -> ground-lift + collider stay correct
            if (def.Id == 1450 && mesh != null)   // battery: a white rectangle label on the front face (master)
            {
                var lbl = new MeshInstance3D
                {
                    Mesh = new QuadMesh { Size = new Vector2(0.35f, 0.135f) },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                };
                lbl.Position = EnvVec3("UG_LBLP", new Vector3(0f, 0.179f, 0.0375f));   // on the +Y face of the raw mesh, slightly proud
                Vector3 eul = EnvVec3("UG_LBLR", new Vector3(-90f, 0f, 0f));           // face outward (+Z quad -> +Y)
                lbl.Basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(eul.X), Mathf.DegToRad(eul.Y), Mathf.DegToRad(eul.Z)));
                mi.AddChild(lbl);   // child of the mesh -> rides MeshBasis + the stand-up, stays on the face
            }
            return mi;
        }

        // Parse a "x,y,z" env var (runtime tuning for the battery label) or return the default.
        static Vector3 EnvVec3(string name, Vector3 dflt)
        {
            var e = System.Environment.GetEnvironmentVariable(name);
            if (e != null) { var p = e.Split(','); if (p.Length == 3 && float.TryParse(p[0], out var x) && float.TryParse(p[1], out var y) && float.TryParse(p[2], out var z)) return new Vector3(x, y, z); }
            return dflt;
        }

        // `surface` = the ground contact point (the raycast hit); the model is lifted so its base sits there.
        // `backing` = the inventory item being planted (null = fresh/console spawn); a picked-up deployable carries its
        // HP (item.quality %) + fuel (item.fuelLevel) so re-placing it restores them instead of resetting to full.
        public static Deployable Spawn(Node parent, DeployableDef def, Vector3 surface, float yawDeg, SDG.Unturned.Item backing = null)
        {
            var d = new Deployable { Def = def, HealthMax = def.Health, FuelMax = def.Fuel };
            d.Health = backing != null ? Mathf.Clamp(def.Health * backing.quality / 100f, 1f, def.Health) : def.Health;
            d.Fuel = (backing != null && backing.fuelLevel >= 0f) ? Mathf.Min(backing.fuelLevel, def.Fuel) : def.Fuel;
            var mi = BuildMesh(def, out Aabb ab);
            d._mesh = mi;
            d.AddChild(mi);
            // collider hugs the real mesh (in the same flat frame as the mesh, so it stands up with the node)
            d.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = ab.Size == Vector3.Zero ? def.Size : ab.Size },
                Position = ab.GetCenter(),
            });
            d.Position = surface + Vector3.Up * DeployableDef.GroundLift(ab);   // base sits on the surface
            d.Basis = DeployableDef.StandBasis(yawDeg);   // yaw + the stand-up
            d.AddToGroup("deployables");
            foreach (var pdef in def.Ports)   // power connection cubes (children -> stand up with the model)
            {
                var port = ConnectionPort.Create(d, pdef, def.Name);
                d.AddChild(port);
                d.Ports.Add(port);
                if (pdef.Kind == DeployableDef.PortKind.Consumer) d._consumerPort = port;   // this consumer's Powered flag lights the lamps
                else if (pdef.Kind == DeployableDef.PortKind.Output) d._outputPort = port;   // this output's Draw drives the load bar + vibration
            }
            foreach (var ldef in def.Lights)   // consumer lamps (spotlight): children in the flat frame -> stand up with the model, off until powered
            {
                Light3D lamp;
                if (ldef.Spot)
                {
                    var s = new SpotLight3D { SpotRange = ldef.Range, SpotAngle = ldef.AngleDeg };
                    s.Basis = Basis.LookingAt(ldef.Dir, Vector3.Back);   // -Z (Godot spot forward) points along the src beam dir (flat frame)
                    lamp = s;
                }
                else lamp = new OmniLight3D { OmniRange = ldef.Range };
                lamp.Position = ldef.Pos; lamp.LightColor = ldef.Color; lamp.LightEnergy = 0f; lamp.Visible = false;
                lamp.AddToGroup("dynlight");   // the lit beam spills onto the FP gun (light-scan), like the fire light
                d.AddChild(lamp);
                d._lamps.Add(lamp); d._lampBase.Add(ldef.Energy);
            }
            d._firePos = surface + Vector3.Up * Mathf.Max(0.6f, def.Size.Z * 1.4f);   // fire from the top of the object (Size.Z = flat-frame height that stands up)

            // fire/smoke rig (TopLevel = world space, so it rises straight up regardless of the stood-up body basis).
            // Smaller + fewer than a car's engine-bay plume -- a generator is a ~0.8m object.
            d._smoke  = Vehicle.MakeSmoke("veh_smoke_1.png", new Color(0.55f, 0.55f, 0.55f), 2.0f, 1.8f, 12, false, 0.8f, 1.6f);   // light damage smoke (< 45% HP)
            d._smoke0 = Vehicle.MakeSmoke("veh_smoke_0.png", new Color(0.30f, 0.29f, 0.27f), 2.6f, 2.2f, 16, false, 0.8f, 1.6f);   // heavy smoke (< 22% HP)
            d._fire   = Vehicle.MakeSmoke("veh_fire.png",   new Color(1f, 0.72f, 0.32f),    0.6f, 3.0f, 20, true,  0.6f, 1.3f);    // fire (0 HP + wreck)
            foreach (var p in new[] { d._smoke, d._smoke0, d._fire }) { p.TopLevel = true; d.AddChild(p); }
            d._fireLight = new OmniLight3D { TopLevel = true, OmniRange = 6f, LightColor = new Color(1f, 0.55f, 0.2f), LightEnergy = 0f, Visible = false };
            d._fireLight.AddToGroup("dynlight");   // a burning wreck spills onto the FP gun (light-scan)
            d.AddChild(d._fireLight);

            if (def.Fuel > 0f)   // generator: the looping engine sound (src Engine-node AudioSource), silent until powered on
            {
                var eng = PlayerController.LoadWavOneShot("res://content/sounds/generator_engine.wav", loop: true);
                if (eng != null) { d._engineAudio = new AudioStreamPlayer3D { Stream = eng, VolumeDb = -6f, UnitSize = 8f, MaxDistance = 45f }; d.AddChild(d._engineAudio); }
            }

            // look-at info billboard (name + HP/fuel BARS), TopLevel so it floats in world space at the object
            d._info = new InfoBillboard { TopLevel = true };
            d.AddChild(d._info);
            parent.AddChild(d);
            foreach (var p in new Node3D[] { d._smoke, d._smoke0, d._fire, d._fireLight }) p.GlobalPosition = d._firePos;   // TopLevel: set world pos after entering the tree
            if (d.GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)   // one PowerManager ticks the whole power net
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); parent.AddChild(pm); }
            return d;
        }

        static void CollectMeshes(Node n, System.Collections.Generic.List<MeshInstance3D> list)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is MeshInstance3D mi) list.Add(mi);
                CollectMeshes(c, list);
            }
        }

        // Look-at focus (same system as vehicles/items): put the mesh silhouette on OutlineLayer so the
        // OutlineOverlay draws a rim, and show/hide the info billboard.
        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (_outlineMeshes == null)
            {
                _outlineMeshes = new System.Collections.Generic.List<MeshInstance3D>();
                CollectMeshes(this, _outlineMeshes);
            }
            foreach (var mi in _outlineMeshes)
                if (IsInstanceValid(mi))
                    mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = OutlineColor;   // OutlineOverlay tints the rim with this
            _info?.SetActive(on);
        }

        // src askDamage: reduce health; at 0 the EXPLODE timer starts + a small fire lights immediately.
        public void TakeDamage(float amount)
        {
            if (_exploded || amount <= 0f || _deadTimer >= 0f) return;
            Health = Mathf.Max(0f, Health - amount);
            if (Health <= 0f)
            {
                _deadTimer = ExplodeDelay;
                _powered = false; _powerLevel = 0f;   // a dying generator cuts out INSTANTLY (no wind-down); the ramp tick stops the audio + settles the mesh
                if (_fire != null) _fire.Emitting = true;   // a small fire the moment it dies, before Explode() ramps the blaze
                if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 1.2f; }
                KillPowerHardware();   // destroyed -> snap its wires + retire its port cubes (also marks the net dirty)
            }
        }

        public bool Hurt => !OnFire && Health < HealthMax;                                  // alive (not on fire) + damaged -> a blowtorch can repair it (src isRepair)
        public void Repair(float amount) { if (!OnFire) Health = Mathf.Min(HealthMax, Health + amount); }   // blowtorch repair: heal HP up to max, same as a car

        // src InteractableGenerator.use(): F toggles isPowered. Only a fuelled, non-wrecked, settled generator responds
        // (the buffer: you can't flip it again until the warmup/cooldown ramp finishes). The ramp itself runs in _Process.
        public void TogglePower() { if (CanTogglePower) { _powered = !_powered; PowerNet.MarkDirty(); } }   // IsPowered flipped -> the net needs a recompute

        // MP replica apply (DeployableReplicaView): the server already validated this toggle, so it lands
        // without the local CanTogglePower interaction gating. The warmup/cooldown ramp still runs locally.
        public void NetSetPowered(bool on) { if (_powered != on) { _powered = on; PowerNet.MarkDirty(); } }
        public bool PoweredTarget => _powered;   // the F-toggle TARGET state (IsPowered adds fuel/fire gating) -- what an MP toggle request inverts

        void Explode()   // src explode: blast nearby, then either shatter into pieces (spotlight) or become a burning salvageable wreck (generator)
        {
            _exploded = true;
            _deadTimer = -1f;
            KillPowerHardware();   // covers the direct-explode path (DebugStage wreck); idempotent if 0-HP already ran it
            ExplodeDamage();
            if (Def != null && Def.ShatterOnDeath) { SpawnDebris(); QueueFree(); return; }   // spotlight: breaks into flying pieces + vanishes -- no husk, no salvage, no drop (strawberry)
            if (_fire != null) _fire.Emitting = true;
            if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 3f; }
            if (_mesh != null) _mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.05f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };   // charred husk
            _burnTime = 0f;
        }

        // ShatterOnDeath: fling a burst of small chunks that outlive the (removed) body, so it visibly breaks apart.
        void SpawnDebris()
        {
            var parent = GetParent();
            if (parent == null) return;
            var chunks = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Amount = 20, Lifetime = 2.6f, Explosiveness = 1f, TopLevel = true,
                Mesh = new BoxMesh { Size = Vector3.One * 0.12f },
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(0.4f, 0.7f, 0.4f),   // pieces originate ACROSS the body, then fall
                Direction = Vector3.Up, Spread = 80f, Gravity = new Vector3(0f, -9.8f, 0f),
                InitialVelocityMin = 0.3f, InitialVelocityMax = 1.5f,   // gentle scatter -> it COLLAPSES, gravity does the rest (not a launch)
                AngularVelocityMin = -180f, AngularVelocityMax = 180f,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.13f, 0.13f, 0.14f), Roughness = 1f },
            };
            parent.AddChild(chunks);
            chunks.GlobalPosition = GlobalPosition + Vector3.Up * 0.7f;
            var t = GetTree()?.CreateTimer(3f);
            if (t != null) t.Timeout += () => { if (IsInstanceValid(chunks)) chunks.QueueFree(); };
        }

        // src generator/barricade explode: a smaller blast than a car (radius 5, ~120 dmg) -> hurts nearby
        // zombies/players/vehicles/deployables, chaining a row of gennies. Half a car's radius/damage.
        void ExplodeDamage()
        {
            const float R = 5f;
            Vector3 p = GlobalPosition;
            PlayerRegistry.FlinchAllFromExplosion(p, 18f, 28f);   // every player's camera; distance-gated per player
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    float d = z.GlobalPosition.DistanceTo(p);
                    if (d <= R) z.DamageHit(SDG.Unturned.ExplosionMath.Linear(120f, d, R), z.GlobalPosition, (z.GlobalPosition - p).Normalized());
                }
            foreach (var n in GetTree().GetNodesInGroup("players"))
                if (n is PlayerController pl)
                {
                    float d = pl.GlobalPosition.DistanceTo(p);
                    if (d <= R) pl.TakeDamage(SDG.Unturned.ExplosionMath.Linear(120f, d, R));
                }
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && !v.Exploded)
                {
                    float d = v.GlobalPosition.DistanceTo(p);
                    if (d <= R) v.TakeDamage(SDG.Unturned.ExplosionMath.Linear(120f, d, R));
                }
            foreach (var n in GetTree().GetNodesInGroup("deployables"))
                if (n is Deployable dep && dep != this && !dep._exploded)
                {
                    float d = dep.GlobalPosition.DistanceTo(p);
                    if (d <= R) dep.TakeDamage(SDG.Unturned.ExplosionMath.Linear(120f, d, R));   // chain: blow the next generator too
                }
        }

        // --- wreck salvage (mirror Vehicle): a burnt-out generator cools, then a blowtorch breaks it into scrap ---
        public bool IsWreck => _exploded;
        public bool DebugLampsLit => _lamps.Count > 0 && IsInstanceValid(_lamps[0]) && _lamps[0].Visible;   // render-harness probe (UG_WIRETEST)
        public bool DebugConsumerPowered => _consumerPort != null && _consumerPort.Powered;
        public bool WreckOnFire => _exploded && _burnTime >= 0f && _burnTime < 60f;   // still burning -> too hot to salvage
        public bool WreckSalvageable => _exploded && _burnTime >= 60f;                // fire's out -> blowtorch-salvageable
        public void SetSalvagePrompt(string line2, Color color)   // a wreck: name + salvage prompt, no bars
        {
            if (_info == null) return;
            _info.SetName(Def?.Name, color);
            _info.SetBar(0, 0f, InfoBillboard.HealthColor, false); _info.SetBar(1, 0f, InfoBillboard.FuelColor, false);
            _info.SetPrompt(line2, color);
        }

        public void Salvage()   // blowtorch teardown: the cold husk breaks into Metal Scrap (item 67), then despawns
        {
            var parent = GetParent();
            if (parent != null)
                for (int i = 0; i < 2; i++)   // a generator yields a couple of Metal Scrap (fewer than a car)
                    WorldItem.Spawn(parent, new SDG.Unturned.Item(67), GlobalPosition + new Vector3((i - 0.5f) * 0.6f, 0.5f, 0f));
            DisconnectWires();
            QueueFree();
        }

        // Free any wire plugged into one of our ports (called before this deployable despawns) so wires don't dangle
        // in the scene + "wires" group with a dead endpoint (rendering, holding a collider, blocking a re-wire).
        void DisconnectWires()
        {
            var tree = GetTree();
            if (tree == null) return;
            foreach (var n in tree.GetNodesInGroup("wires"))
                if (n is Wire w && IsInstanceValid(w) && (Ports.Contains(w.Source) || Ports.Contains(w.Consumer)))
                    w.QueueFree();
            PowerNet.MarkDirty();
        }

        // Destroyed (0 HP / explode) -> snap the wires and retire the port cubes so neither survives on the corpse
        // (strawberry: a wrecked spotlight shouldn't keep its plugged-in wire + glowing connection cubes).
        void KillPowerHardware()
        {
            DisconnectWires();
            foreach (var p in Ports) if (IsInstanceValid(p)) p.Deactivate();
        }

        // Hold-F pickup (master): a LIVE placed deployable is returned to the bag -> free any wires plugged into it
        // (they disconnect on pickup) and despawn. The item grant is the caller's (PlayerController) job. A wreck is
        // blowtorch-salvaged instead, so this is gated to non-wrecks up in PlayerController.
        public void Pickup()
        {
            DisconnectWires();   // wires plugged into our ports vanish with us (strawberry) + marks the net dirty
            QueueFree();
        }

        // test-only: jump straight to a damage stage for the --deploytest render (smoke / heavy / fire / wreck)
        public void DebugStage(string s)
        {
            if (s == "smoke") Health = HealthMax * 0.4f;                 // light damage smoke
            else if (s == "heavy") Health = HealthMax * 0.15f;           // heavy smoke
            else if (s == "fire") { Health = 0f; _deadTimer = ExplodeDelay; if (_fire != null) _fire.Emitting = true; if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 1.2f; } KillPowerHardware(); }
            else if (s == "wreck") { Health = 0f; Explode(); }           // full burning charred husk (Explode calls KillPowerHardware)
            PowerNet.MarkDirty();
        }

        public override void _Process(double delta)
        {
            // damage/burn lifecycle runs ALWAYS (not just when focused): 0-HP explosion delay + the wreck fire arc.
            if (_deadTimer >= 0f) { _deadTimer -= (float)delta; if (_deadTimer <= 0f) Explode(); }
            if (_burnTime >= 0f)   // wreck fire: 0-40s full, 40-60s dying down, out at 60s (+ light killed), sits 5 min, then despawns
            {
                _burnTime += (float)delta;
                if (_burnTime < 40f) { if (_fireLight != null) _fireLight.LightEnergy = 3f; }
                else if (_burnTime < 60f)
                {
                    float f = 1f - (_burnTime - 40f) / 20f;   // 1 -> 0 fade
                    if (_fireLight != null) _fireLight.LightEnergy = 3f * f;
                }
                else if (_burnTime < 360f)
                {
                    if (_fire != null && _fire.Emitting) _fire.Emitting = false;
                    if (_fireLight != null && _fireLight.Visible) { _fireLight.Visible = false; _fireLight.LightEnergy = 0f; }
                }
                else { DisconnectWires(); QueueFree(); return; }   // wreck fully despawned -> take its wires with it
            }
            // smoke thresholds (src updateFires): light smoke while damaged/burning, heavy smoke when badly hurt or wrecked.
            if (_smoke != null) _smoke.Emitting = _burnTime < 60f && (_exploded || Health < HealthMax * SmokeFrac);
            if (_smoke0 != null) _smoke0.Emitting = _burnTime < 60f && (_exploded || Health < HealthMax * HeavyFrac);

            // power ramp: warmup toward on / cooldown toward off. The engine spin (pitch + volume fade) and the body
            // shake amplitude both follow _powerLevel, so turning on builds up and turning off winds down.
            float pTarget = RunTarget;   // an on-fire / fuel-dry generator's engine is dead regardless of the _powered toggle
            if (FuelMax > 0f && pTarget > 0.5f && Fuel > 0f)   // a RUNNING generator burns fuel, scaled by LOAD (master): idle sips ~20%, a fully-loaded base guzzles.
            {
                Fuel = Mathf.Max(0f, Fuel - DeployableDef.GenFuelBurnPerSec * (0.2f + 0.8f * LoadFraction) * (float)delta);
                if (Fuel <= 0f && _powered) { _powered = false; PowerNet.MarkDirty(); }   // ran DRY -> flip the toggle OFF (RunTarget was already 0 from the Fuel gate, so the cooldown ramp plays); needs a refuel + a manual [F] restart, NOT an auto-resume (master)
            }
            if (Def != null && Def.IsBattery)   // battery: the OUT discharges the stored Energy, the IN charges it (no engine/ramp/audio -- FuelMax 0 keeps RunTarget 0)
            {
                bool wasProducing = Energy > 0f;
                float outDraw = (_outputPort != null && IsInstanceValid(_outputPort)) ? _outputPort.Draw : 0f;
                if (outDraw > 0f) Energy = Mathf.Max(0f, Energy - outDraw * (float)delta);   // discharge to whatever's wired to the OUT
                if (_consumerPort != null && IsInstanceValid(_consumerPort) && _consumerPort.Powered && Energy < Def.EnergyMax)
                    Energy = Mathf.Min(Def.EnergyMax, Energy + Def.ChargeWatts * (float)delta);   // charge while the IN is fed by a source
                if ((Energy > 0f) != wasProducing) PowerNet.MarkDirty();   // crossed empty <-> charged -> the OUT starts/stops producing
            }
            if (_powerLevel < pTarget) _powerLevel = Mathf.Min(pTarget, _powerLevel + (float)delta / WarmupTime);
            else if (_powerLevel > pTarget) _powerLevel = Mathf.Max(pTarget, _powerLevel - (float)delta / CooldownTime);
            float load = LoadFraction;   // 0..1 of capacity drawn -> louder/deeper engine + harder shake under load (strawberry)
            if (_engineAudio != null)
            {
                if (_powerLevel > 0.01f)
                {
                    if (!_engineAudio.Playing) _engineAudio.Play();
                    _engineAudio.PitchScale = (0.6f + 0.4f * _powerLevel) - load * 0.22f * _powerLevel;   // spin up 0.6->1.0, then bog DOWN in pitch under load
                    _engineAudio.VolumeDb = Mathf.Lerp(-26f, -6f, _powerLevel) + load * 3.5f * _powerLevel; // and work louder
                }
                else if (_engineAudio.Playing) _engineAudio.Stop();
            }
            if (_mesh != null)   // NON-source shake (src Engine node has no anim) -- ~6mm at idle, up to ~3.5x harder + faster under full load
            {
                if (_powerLevel > 0.01f && !_exploded)
                {
                    _vibePhase += (float)delta * (90f + load * 70f);
                    _mesh.Position = new Vector3(Mathf.Sin(_vibePhase * 1.3f), Mathf.Sin(_vibePhase), Mathf.Sin(_vibePhase * 0.7f)) * 0.006f * _powerLevel * (1f + load * 2.5f);
                }
                else if (_mesh.Position != Vector3.Zero) _mesh.Position = Vector3.Zero;
            }

            // consumer lamps (spotlight): src updateLights -> on = wired && powered (PowerNet flags the consumer Powered
            // only when wired AND receiving >= its usage). The lamp rides a 0..1 ENVELOPE that ramps up/down with power;
            // while the envelope is mid-ramp -- i.e. the source is spinning up or winding down -- the lamp FLICKERS
            // (strawberry). Steady state is full brightness. Runs every frame (not just focused).
            if (_lamps.Count > 0)
            {
                bool energized = !OnFire && _consumerPort != null && IsInstanceValid(_consumerPort) && _consumerPort.Powered;
                float target = energized ? 1f : 0f;
                if (_lampLevel < target) _lampLevel = Mathf.Min(target, _lampLevel + (float)delta / WarmupTime);
                else if (_lampLevel > target) _lampLevel = Mathf.Max(target, _lampLevel - (float)delta / CooldownTime);
                bool ramping = _lampLevel > 0.02f && _lampLevel < 0.98f;   // source warming up / cooling down -> stutter
                if (ramping)
                {
                    _lampFlickerT -= (float)delta;
                    if (_lampFlickerT <= 0f) { _lampFlicker = GD.Randf() < 0.5f ? 0.2f + GD.Randf() * 0.5f : 1f; _lampFlickerT = 0.035f + GD.Randf() * 0.07f; }   // irregular dips ~15-45 Hz
                }
                else _lampFlicker = 1f;
                float disp = _lampLevel * _lampFlicker;
                bool vis = disp > 0.04f;
                for (int i = 0; i < _lamps.Count; i++)
                    if (IsInstanceValid(_lamps[i]))
                    {
                        if (_lamps[i].Visible != vis) _lamps[i].Visible = vis;
                        _lamps[i].LightEnergy = _lampBase[i] * disp;
                    }
                if (DbgFlicker) GD.Print($"[FLICK] lvl={_lampLevel:0.00} disp={disp:0.00} vis={vis}");
            }

            if (!_lookFocused || _info == null) return;   // only the focused one keeps its billboard live (a wreck's prompt is set by PlayerController -- it knows the blowtorch)
            _info.GlobalPosition = GlobalPosition + Vector3.Up * InfoH;
            if (!_exploded)
            {
                _info.SetName(Def?.Name, OutlineColor);
                _info.SetBar(0, HealthMax > 0f ? Health / HealthMax : 0f, InfoBillboard.HealthColor);   // HP bar (red)
                _info.SetBar(1, Def != null && Def.IsBattery ? (Def.EnergyMax > 0f ? Energy / Def.EnergyMax : 0f) : (FuelMax > 0f ? Fuel / FuelMax : 0f), InfoBillboard.FuelColor, (FuelMax > 0f) || (Def != null && Def.IsBattery));   // fuel / battery-CHARGE bar (yellow); hidden if neither
                _info.SetBar(2, LoadFraction, InfoBillboard.LoadColor, _outputPort != null);   // usage bar (cyan): load / capacity -- generators only
                // While the player HOLDS F to pick it up, show the progress; otherwise the interact hint. src checkHint:
                // GENERATOR_OFF when on, GENERATOR_ON when off (_powered is the target -> reads as the next action even
                // mid-ramp). A generator adds a tap-toggle in front of the hold-to-pick-up. No prompt once on fire.
                string prompt;
                if (PickupProgress > 0.01f) prompt = $"Picking up... {Mathf.Clamp((int)(PickupProgress * 100f), 0, 99)}%";
                else if (OnFire) prompt = "";
                else prompt = (Def != null && Def.Fuel > 0f ? $"[F] Turn {(_powered ? "Off" : "On")} · " : "") + "Hold [F]: pick up";
                _info.SetPrompt(prompt, OutlineColor);
            }
        }
    }
}
