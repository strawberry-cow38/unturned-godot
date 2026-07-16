using Godot;

namespace UnturnedGodot
{
    // A placed deployable in the world (the result of planting a held barricade). Mesh + a box collider + health/fuel,
    // in group "deployables". Look-at gets the same screen-space outline + info billboard (name / HP / fuel) as
    // vehicles, and the same damage lifecycle: smoke at low HP, fire + explosion at 0 HP, a burning wreck that cools
    // into a blowtorch-salvageable husk (src runtime InteractableGenerator + the shared vehicle explode/salvage path).
    public partial class Deployable : StaticBody3D
    {
        public DeployableDef Def;
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
        bool PowerSettled => Mathf.Abs(_powerLevel - (_powered ? 1f : 0f)) < 0.001f;
        bool OnFire => _deadTimer >= 0f || _exploded;   // catching fire at 0 HP (deadTimer) through the burning wreck -> a dead/dying generator, can't be run
        public bool CanTogglePower => !OnFire && Def != null && Def.Fuel > 0f && PowerSettled;   // only a fuelled, NOT-on-fire generator toggles, and only once the ramp has settled (buffer)
        public bool IsPowered => _powered;

        bool _lookFocused;
        System.Collections.Generic.List<MeshInstance3D> _outlineMeshes;
        Label3D _infoLabel;
        static readonly Color OutlineColor = new Color(0.82f, 0.83f, 0.90f);   // same neutral tint as vehicles (no per-deployable rarity yet)
        const float InfoH = 0.5f;   // billboard sits INSIDE the generator body (strawberry), not floating above it

        // Build the mesh + material for a def, returning the MeshInstance and its local AABB (in the flat
        // authored frame, before the -90 X stand-up). Shared by the placed object and the placement ghost.
        public static MeshInstance3D BuildMesh(DeployableDef def, out Aabb localAabb)
        {
            var mesh = def.LoadMesh();
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = def.MakeMaterial() };
            localAabb = mesh != null ? mesh.GetAabb() : new Aabb();
            return mi;
        }

        // `surface` = the ground contact point (the raycast hit); the model is lifted so its base sits there.
        public static Deployable Spawn(Node parent, DeployableDef def, Vector3 surface, float yawDeg)
        {
            var d = new Deployable { Def = def, Health = def.Health, HealthMax = def.Health, Fuel = def.Fuel, FuelMax = def.Fuel };
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

            // look-at info billboard (name / HP / fuel), TopLevel so it floats in world space above the object
            d._infoLabel = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, TopLevel = true, Visible = false,
                Modulate = OutlineColor, PixelSize = 0.0055f, NoDepthTest = true, FontSize = 52, OutlineSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            d.AddChild(d._infoLabel);
            parent.AddChild(d);
            foreach (var p in new Node3D[] { d._smoke, d._smoke0, d._fire, d._fireLight }) p.GlobalPosition = d._firePos;   // TopLevel: set world pos after entering the tree
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
            if (_infoLabel != null) _infoLabel.Visible = on;
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
            }
        }

        // src InteractableGenerator.use(): F toggles isPowered. Only a fuelled, non-wrecked, settled generator responds
        // (the buffer: you can't flip it again until the warmup/cooldown ramp finishes). The ramp itself runs in _Process.
        public void TogglePower() { if (CanTogglePower) _powered = !_powered; }

        void Explode()   // src explode: full fire, char the body, blast nearby, become a burning wreck
        {
            _exploded = true;
            _deadTimer = -1f;
            if (_fire != null) _fire.Emitting = true;
            if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 3f; }
            if (_mesh != null) _mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.05f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };   // charred husk
            _burnTime = 0f;
            ExplodeDamage();
        }

        // src generator/barricade explode: a smaller blast than a car (radius 5, ~120 dmg) -> hurts nearby
        // zombies/players/vehicles/deployables, chaining a row of gennies. Half a car's radius/damage.
        void ExplodeDamage()
        {
            const float R = 5f;
            Vector3 p = GlobalPosition;
            PlayerController.Local?.FlinchFromExplosion(p, 18f, 28f);
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    float d = z.GlobalPosition.DistanceTo(p);
                    if (d <= R) z.DamageHit(120f * (1f - d / R), z.GlobalPosition, (z.GlobalPosition - p).Normalized());
                }
            foreach (var n in GetTree().GetNodesInGroup("players"))
                if (n is PlayerController pl)
                {
                    float d = pl.GlobalPosition.DistanceTo(p);
                    if (d <= R) pl.TakeDamage(120f * (1f - d / R));
                }
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && !v.Exploded)
                {
                    float d = v.GlobalPosition.DistanceTo(p);
                    if (d <= R) v.TakeDamage(120f * (1f - d / R));
                }
            foreach (var n in GetTree().GetNodesInGroup("deployables"))
                if (n is Deployable dep && dep != this && !dep._exploded)
                {
                    float d = dep.GlobalPosition.DistanceTo(p);
                    if (d <= R) dep.TakeDamage(120f * (1f - d / R));   // chain: blow the next generator too
                }
        }

        // --- wreck salvage (mirror Vehicle): a burnt-out generator cools, then a blowtorch breaks it into scrap ---
        public bool IsWreck => _exploded;
        public bool WreckOnFire => _exploded && _burnTime >= 0f && _burnTime < 60f;   // still burning -> too hot to salvage
        public bool WreckSalvageable => _exploded && _burnTime >= 60f;                // fire's out -> blowtorch-salvageable
        public void SetSalvagePrompt(string line2, Color color) { if (_infoLabel != null) { _infoLabel.Text = $"{Def?.Name}\n{line2}"; _infoLabel.Modulate = color; } }

        public void Salvage()   // blowtorch teardown: the cold husk breaks into Metal Scrap (item 67), then despawns
        {
            var parent = GetParent();
            if (parent != null)
                for (int i = 0; i < 2; i++)   // a generator yields a couple of Metal Scrap (fewer than a car)
                    WorldItem.Spawn(parent, new SDG.Unturned.Item(67), GlobalPosition + new Vector3((i - 0.5f) * 0.6f, 0.5f, 0f));
            QueueFree();
        }

        // test-only: jump straight to a damage stage for the --deploytest render (smoke / heavy / fire / wreck)
        public void DebugStage(string s)
        {
            if (s == "smoke") Health = HealthMax * 0.4f;                 // light damage smoke
            else if (s == "heavy") Health = HealthMax * 0.15f;           // heavy smoke
            else if (s == "fire") { Health = 0f; _deadTimer = ExplodeDelay; if (_fire != null) _fire.Emitting = true; if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 1.2f; } }
            else if (s == "wreck") { Health = 0f; Explode(); }           // full burning charred husk
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
                else { QueueFree(); return; }
            }
            // smoke thresholds (src updateFires): light smoke while damaged/burning, heavy smoke when badly hurt or wrecked.
            if (_smoke != null) _smoke.Emitting = _burnTime < 60f && (_exploded || Health < HealthMax * SmokeFrac);
            if (_smoke0 != null) _smoke0.Emitting = _burnTime < 60f && (_exploded || Health < HealthMax * HeavyFrac);

            // power ramp: warmup toward on / cooldown toward off. The engine spin (pitch + volume fade) and the body
            // shake amplitude both follow _powerLevel, so turning on builds up and turning off winds down.
            float pTarget = (_powered && !OnFire && FuelMax > 0f && Fuel > 0f) ? 1f : 0f;   // an on-fire generator's engine is dead regardless of _powered
            if (_powerLevel < pTarget) _powerLevel = Mathf.Min(pTarget, _powerLevel + (float)delta / WarmupTime);
            else if (_powerLevel > pTarget) _powerLevel = Mathf.Max(pTarget, _powerLevel - (float)delta / CooldownTime);
            if (_engineAudio != null)
            {
                if (_powerLevel > 0.01f)
                {
                    if (!_engineAudio.Playing) _engineAudio.Play();
                    _engineAudio.PitchScale = 0.6f + 0.4f * _powerLevel;        // spin up 0.6 -> 1.0
                    _engineAudio.VolumeDb = Mathf.Lerp(-26f, -6f, _powerLevel); // fade in as it warms
                }
                else if (_engineAudio.Playing) _engineAudio.Stop();
            }
            if (_mesh != null)   // NON-source shake (src Engine node has no anim) -- ~6mm, scaled by the ramp
            {
                if (_powerLevel > 0.01f && !_exploded)
                {
                    _vibePhase += (float)delta * 90f;
                    _mesh.Position = new Vector3(Mathf.Sin(_vibePhase * 1.3f), Mathf.Sin(_vibePhase), Mathf.Sin(_vibePhase * 0.7f)) * 0.006f * _powerLevel;
                }
                else if (_mesh.Position != Vector3.Zero) _mesh.Position = Vector3.Zero;
            }

            if (!_lookFocused || _infoLabel == null) return;   // only the focused one keeps its billboard live (a wreck's prompt is set by PlayerController -- it knows the blowtorch)
            _infoLabel.GlobalPosition = GlobalPosition + Vector3.Up * InfoH;
            if (!_exploded)
            {
                _infoLabel.Modulate = OutlineColor;
                string fuelLine = FuelMax > 0f ? $"\nFuel {Fuel:0}/{FuelMax:0}" : "";
                // src checkHint: GENERATOR_OFF when on, GENERATOR_ON when off. _powered is the target, so this reads as
                // the next action even mid-ramp (strawberry: don't change the prompt during warmup/cooldown).
                string powerLine = Def != null && Def.Fuel > 0f && !OnFire ? $"\n[F] Turn {(_powered ? "Off" : "On")}" : "";   // no turn on/off prompt once it's on fire
                _infoLabel.Text = $"{Def?.Name}\nHP {Health:0}/{HealthMax:0}{fuelLine}{powerLine}";
            }
        }
    }
}
