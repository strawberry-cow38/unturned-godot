using Godot;

namespace UnturnedGodot
{
    // ONE signal HEAD. Traffic_Light_0 is a mast arm carrying two of them, and strawberry's call is that each runs
    // its own timer -- so a prop spawns two of these, not one (ObjMesh.SplitTrafficLensesPerHead cuts the geometry).
    //
    // DELIBERATELY A DUMB PER-HEAD TIMER. Heads are NOT phase-locked, at a junction or even on the same mast, so you
    // will see green on crossing roads. That is the call: sync needs a cluster identity and buys realism the game
    // does not trade on. The offset is a deterministic hash of world position plus the head index, so every head
    // differs from its neighbours and every client agrees without replicating anything.
    //
    // Grid down -> slow breathing blink, not dark. Confirmed against real practice: a signal on backup flashes AMBER
    // to the main road (proceed with caution) and RED to the side road (stop, treat as a 4-way). Which one a mast
    // shows is the per-prop `SideRoad` flag; it ships unset, so everything breathes amber until a human assigns them.
    public partial class TrafficLight : Node3D
    {
        // Real US signal timings. Amber is the one that actually matters for feel -- it is a function of approach
        // speed and lands at 3-5s on real hardware, never the 2s placeholder this shipped with. The all-red overlap
        // is what stops two phase groups ever showing green together.
        public static float GreenSec  = 25f;
        public static float AmberSec  = 4f;
        public static float AllRedSec = 2f;
        public static float LensEmission = 3.0f;   // shaded emissive, so it reaches HDR and blooms (see StreetLight)
        public static float FlashHz = 0.5f;        // outage blink: one slow breath every ~2s

        /// <summary>Do signal cabinets have battery back-up at all? Dev console `toggleBbat`. With it OFF a grid
        /// failure kills every junction instantly instead of leaving them breathing amber for a couple of days --
        /// which is the whole difference between a town that looks abandoned and one that looks unpowered.</summary>
        public static bool BatteryBackup = true;

        /// <summary>How long the cabinet battery keeps the flash going after the grid dies, in in-game DAYS
        /// (strawberry: "force the power out blink state for like a couple in game days"). Finite, so the junction
        /// eventually goes dark. Recharges on a power restore.</summary>
        public static float BatteryDays = 2f;

        /// <summary>Is this signal on the SIDE road? Real junctions in flash mode show amber to the main road and RED
        /// to the side road. Per-prop rather than derived, because an independent timer has no junction to ask which
        /// road is major (strawberry). Ships unset on every signal -- see content/objects/traffic_side_roads.txt.</summary>
        public bool SideRoad;

        public enum Phase { Red, Amber, Green, FlashAmber, FlashRed, Off }

        MeshInstance3D _red, _amber, _green;
        Material[] _off = new Material[3];
        StandardMaterial3D[] _on = new StandardMaterial3D[3];
        OmniLight3D _glow;
        float _glowBase = 0.6f;
        float _offset;       // per-HEAD phase offset, deterministic from world position + head index
        bool _powered = true, _broken;
        Phase _phase = Phase.Red;
        float _batteryDeadAt = float.PositiveInfinity;   // clock seconds at which the backup battery gives out

        // Continuous brightness 0..1 on top of the discrete phase. Steady aspects sit at 1; the outage blink breathes
        // it (strawberry: "more of a slow fade in/out breathing effect", not a hard on/off); a dying battery stutters
        // it. Kept separate from Phase so the phase logic stays a pure function of the clock.
        float _level = 1f, _shownLevel = -1f;

        public Phase CurrentPhase => _phase;
        public bool PoweredForTest => _powered;
        public float OffsetForTest => _offset;
        public float LevelForTest => _level;

        /// <summary>Which lens is EMITTING right now: 0 red, 1 amber, 2 green, -1 none. The single source of truth --
        /// the flash phases have to map back onto the ordinary lenses, and when that mapping was written out
        /// separately in the material loop, the glow colour and the test accessor, two of the three copies quietly
        /// disagreed (FlashRed lit the red lens but threw GREEN light on the mast).</summary>
        public static int LensIndexFor(Phase p) => p switch
        {
            Phase.Red or Phase.FlashRed => 0,
            Phase.Amber or Phase.FlashAmber => 1,
            Phase.Green => 2,
            _ => -1,   // Phase.Off -- a drained battery, or a smashed head
        };

        // Individually shot-out lenses (strawberry: "add the ability to shoot out each light piece"). A dead lens
        // stays dark forever while the head keeps cycling around it -- a signal showing amber then nothing then green
        // is exactly what a junction with one blown aspect looks like, and is more useful than killing the whole head.
        readonly bool[] _lensOut = new bool[3];

        int LitIndex
        {
            get
            {
                if (_broken) return -1;
                int i = LensIndexFor(_phase);
                return i >= 0 && _lensOut[i] ? -1 : i;   // the phase still advances; this aspect just cannot show it
            }
        }

        /// <summary>Collider meta carrying this prop's heads, so a bullet landing on a Traffic_Light_0 can find them.
        /// An ARRAY, unlike the streetlight's single lamp: one prop is two independently-timed heads and the shot has
        /// to be resolved against whichever one it actually hit.</summary>
        public static readonly StringName HitMeta = "trafficlight";

        /// <summary>Which lens does this world point land on -- 0 red, 1 amber, 2 green, -1 none? The prop's collider
        /// is one trimesh over the whole mast, so a shot at a lens arrives indistinguishable from a shot at the pole;
        /// the lens's own bounds are what separate them. Deliberately NOT extra colliders on the lenses: the trimesh
        /// already covers those faces, so coincident boxes would just race it for the raycast hit.</summary>
        public int LensHit(Vector3 worldPoint)
        {
            for (int i = 0; i < 3; i++)
            {
                var mi = Lens((Phase)i);
                if (mi?.Mesh == null || !IsInstanceValid(mi)) continue;
                var local = mi.GlobalTransform.AffineInverse() * worldPoint;
                if (mi.Mesh.GetAabb().Grow(0.06f).HasPoint(local)) return i;   // bullets land ON the surface, i.e. exactly on the boundary
            }
            return -1;
        }

        /// <summary>Shoot out one aspect. Returns false if it was already dead, so the caller can let the shot fall
        /// through to the prop's health instead of silently eating it.</summary>
        public bool ShootOutLens(int i)
        {
            if (i < 0 || i > 2 || _lensOut[i]) return false;
            _lensOut[i] = true;
            Apply();
            return true;
        }

        public bool LensOutForTest(int i) => i >= 0 && i < 3 && _lensOut[i];

        /// <summary>Is this lens actually EMITTING? Not "visible" -- the lens geometry is always drawn, only its
        /// material changes, same as the streetlight bulb. Independent of _powered because flash mode lights amber
        /// while unpowered.</summary>
        public bool LitForTest(Phase p) => Lens(p) != null && LitIndex == (int)p;
        public bool DarkForTest => LitIndex < 0;
        /// <summary>Has the backup battery drained? Only meaningful while unpowered.</summary>
        public bool BatteryDeadForTest => ClockSeconds() >= _batteryDeadAt;

        /// <summary>One head's full cycle: green -> amber -> red, then round again.</summary>
        public static float CycleSec => GreenSec + AmberSec + AllRedSec;

        /// <param name="lenses">ONE head's red/amber/green meshes, split out of the prop by ObjMesh and already
        /// positioned by the placement basis. Kept parented to the prop -- same reason StreetLight adopts rather
        /// than reparents: the basis that puts them inside the housing lives there.</param>
        /// <param name="head">Which head on the mast. Feeds the offset hash so the two heads of one prop are out of
        /// step with each other (strawberry: "a moderate twiddle between each light so they dont all change at the
        /// same time") rather than flipping in lockstep, which reads as one big sign.</param>
        public static TrafficLight Make(Vector3 worldPos, float yawDeg, MeshInstance3D red, MeshInstance3D amber, MeshInstance3D green, int head = 0)
        {
            // Deterministic offset from world position + head index, so heads differ but every client computes the
            // same value. Same hash family StreetLight uses for its per-lamp brightness.
            float h = Mathf.Sin(worldPos.X * 12.9898f + worldPos.Z * 78.233f + head * 37.719f) * 43758.5453f;
            return new TrafficLight
            {
                Position = worldPos, TopLevel = true,
                _offset = (h - Mathf.Floor(h)) * CycleSec,
                _red = red, _amber = amber, _green = green,
            };
        }

        // FlashAmber must map to the amber lens, not fall through to green. Getting this wrong lights the wrong lens,
        // silently.
        MeshInstance3D Lens(Phase p) => p switch
        {
            Phase.Red => _red,
            Phase.Amber or Phase.FlashAmber => _amber,
            Phase.FlashRed => _red,
            Phase.Green => _green,
            _ => null,
        };

        public override void _Ready()
        {
            AddToGroup("traffic_lights");
            var cols = new[] { new Color(1f, 0.15f, 0.12f), new Color(1f, 0.68f, 0.10f), new Color(0.25f, 1f, 0.35f) };
            var lensCentre = Vector3.Zero; var lensFwd = Vector3.Zero; int seen = 0;
            for (int i = 0; i < 3; i++)
            {
                var mi = Lens((Phase)i);
                if (mi == null) continue;
                _off[i] = mi.MaterialOverride;              // the prop's own material = the UNLIT lens
                _on[i] = new StandardMaterial3D
                {
                    AlbedoColor = cols[i], EmissionEnabled = true, Emission = cols[i],
                    EmissionEnergyMultiplier = LensEmission, Metallic = 0f, Roughness = 0.4f,
                };
                mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                if (mi.Mesh != null)
                {
                    lensCentre += mi.GlobalTransform * mi.Mesh.GetAabb().GetCenter();
                    lensFwd += LensFacing(mi);
                    seen++;
                }
            }
            // THE EMITTER GOES AT THE LENSES (strawberry: "the actual point light emitter is at the prop's 0,0 not on
            // the actual light thing"). This node is TopLevel at the prop's BASE, so a child at local zero sits in the
            // road throwing colour on the tarmac while the head 6m up stays unlit. Ask the lens meshes where they are.
            _glow = new OmniLight3D { OmniRange = 3.2f, LightEnergy = _glowBase, ShadowEnabled = false, Visible = false };
            AddChild(_glow);
            // ...and it sits slightly IN FRONT of the glass rather than inside it (master: "move the actual light
            // emitter of traffic lights forward a little, floating in front of the actual emissive pieces"). An omni
            // buried in the lens quad lights the housing it is embedded in as much as the road, so the aspect reads as
            // a glowing box instead of a lamp throwing light forward.
            //
            // "Forward" is taken from the lens geometry's own NORMALS, not from the node -- this node is TopLevel with
            // an identity basis (Make sets Position only), so there is no transform to ask, and the mast-arm direction
            // is not the facing either: a head hangs off the arm and points back down the road, not along it.
            if (seen > 0)
            {
                var fwd = lensFwd / seen;
                _glow.GlobalPosition = lensCentre / seen
                                     + (fwd.LengthSquared() > 1e-6f ? fwd.Normalized() * GlowForward : Vector3.Zero);
            }
            Apply();
        }

        /// <summary>How far in front of the glass the emitter floats. Small on purpose -- far enough that the omni is
        /// outside the lens quad it would otherwise be embedded in, close enough that it still reads as coming from
        /// the aspect rather than hovering in the air in front of the signal.</summary>
        const float GlowForward = 0.25f;

        /// <summary>A lens quad's outward facing, averaged from its own vertex normals and taken into world space.
        /// The mesh knows which way it points; every other source here is a guess. Returns zero for a mesh with no
        /// normal array, which the caller treats as "no offset" rather than as a direction.</summary>
        static Vector3 LensFacing(MeshInstance3D mi)
        {
            if (mi?.Mesh == null || mi.Mesh.GetSurfaceCount() < 1) return Vector3.Zero;
            var arr = mi.Mesh.SurfaceGetArrays(0);
            var n = arr[(int)Mesh.ArrayType.Normal].AsVector3Array();
            if (n == null || n.Length == 0) return Vector3.Zero;
            var sum = Vector3.Zero;
            foreach (var v in n) sum += v;
            if (sum.LengthSquared() < 1e-9f) return Vector3.Zero;   // a closed box averages to nothing -- no usable facing
            return (mi.GlobalTransform.Basis.Orthonormalized() * sum.Normalized()).Normalized();
        }

        /// <summary>Pin the head to one aspect and stop the timer. For the --trafficlight harness: every state this
        /// thing has (each aspect, the breathing blink at full and at its trough, a drained battery) has to be
        /// renderable on demand, or the ones that pass in under a second never get looked at.</summary>
        public void ForcePhase(Phase p, float level = 1f) { Frozen = true; _phase = p; _level = level; Apply(); ApplyLevel(); }
        public bool Frozen;

        /// <summary>DELIBERATELY DOES NOTHING (strawberry: "should they flicker in brownouts? they are on a battery
        /// for a reason"). The opposite of the streetlight/lamp behaviour on purpose: the cabinet's BBS sits between
        /// the grid and the lamps, so a sag rides straight through it. A junction that stutters during a brownout
        /// would be advertising that its battery does not work. Kept as an explicit no-op rather than by omission so
        /// the next person sweeping grid consumers for FlickerPulse finds the reason instead of assuming an oversight.
        /// With BatteryBackup off there is nothing to ride it out, so it flickers like anything else.</summary>
        public void FlickerPulse(float durationSec = 0.6f)
        {
            if (BatteryBackup || _broken || LitIndex < 0) return;
            _dyingT = Mathf.Max(_dyingT, durationSec);
        }

        // Battery giving out: a stutter into darkness rather than a clean cut, matching the reaction-delay flicker
        // every other grid fixture does (strawberry: "they should flicker off once the battery backup runs out, with
        // the same flicker/reaction thing").
        float _dyingT, _stutterT;
        bool _stutterShow = true;

        public override void _Process(double delta)
        {
            if (_broken || Frozen) return;
            float t = ClockSeconds();
            Phase p;
            float lvl = 1f;
            if (_powered) p = PhaseAt(t, _offset);
            else if (!BatteryBackup || t >= _batteryDeadAt + DyingSec)
            {
                p = Phase.Off;   // no backup fitted, or the battery is long gone -- unsignalled junction
            }
            else if (t >= _batteryDeadAt)
            {
                // The battery is failing. Keep the aspect but stutter it out over DyingSec, odds ramping toward dark.
                p = SideRoad ? Phase.FlashRed : Phase.FlashAmber;
                _stutterT -= (float)delta;
                if (_stutterT <= 0f)
                {
                    float frac = Mathf.Clamp((t - _batteryDeadAt) / DyingSec, 0f, 1f);
                    _stutterShow = GD.Randf() > frac;                 // early: mostly lit; late: mostly dark
                    _stutterT = 0.04f + GD.Randf() * 0.08f;           // irregular ~8-25 Hz, same feel as GridLight
                }
                lvl = _stutterShow ? 1f : 0f;
            }
            else
            {
                // BREATHING blink (strawberry). A raised cosine rather than a square wave, smoothstepped so it holds
                // a little at the top and bottom instead of reading as a pure sine wobble.
                p = SideRoad ? Phase.FlashRed : Phase.FlashAmber;
                float u = 0.5f - 0.5f * Mathf.Cos(Mathf.Tau * FlashHz * t);
                lvl = u * u * (3f - 2f * u);
            }
            // A grid-loss flicker with no battery fitted: stutter, then settle wherever the phase logic says.
            if (_dyingT > 0f)
            {
                _dyingT -= (float)delta;
                _stutterT -= (float)delta;
                if (_stutterT <= 0f) { _stutterShow = GD.Randf() < 0.55f; _stutterT = 0.04f + GD.Randf() * 0.08f; }
                if (!_stutterShow) lvl = 0f;
            }
            if (p != _phase) { _phase = p; Apply(); }
            if (Mathf.Abs(lvl - _shownLevel) > 0.01f) { _level = lvl; ApplyLevel(); }
        }

        /// <summary>How long the battery takes to die once it starts, in clock seconds.</summary>
        public static float DyingSec = 2.5f;

        DayNightCycle _cyc; bool _cycLooked;
        /// <summary>Shared world clock in seconds. Derived from the day/night cycle's Day + Time so every peer
        /// computes the same value -- that is what makes the phase agree across a session with nothing replicated.
        /// Falls back to engine time when no cycle is in the scene (--trafficlight, L1 harness), where determinism
        /// across peers is not a concern because there is only one.</summary>
        float ClockSeconds()
        {
            if (!_cycLooked)
            {
                _cycLooked = true;
                var tree = GetTree();
                if (tree != null)
                    foreach (var n in tree.GetNodesInGroup("daynight"))
                        if (n is DayNightCycle d && IsInstanceValid(d)) { _cyc = d; break; }
            }
            if (_cyc != null && IsInstanceValid(_cyc)) return (_cyc.Day + _cyc.Time) * _cyc.DayLength;
            return Godot.Time.GetTicksMsec() / 1000f;
        }

        /// <summary>Pure: the phase this head shows at time t. Static and side-effect free so a test can assert the
        /// invariants that matter -- ordering, and that a powered head is never dark -- by sweeping the whole cycle
        /// rather than by watching a node and hoping.</summary>
        public static Phase PhaseAt(float t, float offset)
        {
            float u = Mathf.PosMod(t + offset, CycleSec);
            if (u < GreenSec) return Phase.Green;
            if (u < GreenSec + AmberSec) return Phase.Amber;
            return Phase.Red;
        }

        /// <summary>Grid power. Losing it drops the head to a breathing blink rather than dark -- what a real junction
        /// does on cabinet backup during a storm outage -- and starts the battery draining. Restoring it recharges.
        /// The deadline is stamped from the CLOCK rather than counted down per-frame so it survives a `dateset` jump
        /// and needs no per-frame work on the hundreds of heads a map can hold.</summary>
        public void SetPowered(bool on)
        {
            if (_powered == on) return;
            _powered = on;
            _batteryDeadAt = on ? float.PositiveInfinity
                                : ClockSeconds() + BatteryDays * DayLen;
            if (!on && !BatteryBackup) _dyingT = Mathf.Max(_dyingT, 0.8f);   // nothing to ride it out: flicker and die
            Apply();
        }

        /// <summary>Seconds per in-game day, from the cycle when there is one. The fallback matches DayNightCycle's
        /// own default so a harness without a cycle still drains over a sane interval instead of instantly.</summary>
        float DayLen => _cyc != null && IsInstanceValid(_cyc) ? _cyc.DayLength : 120f;

        /// <summary>Smashed prop: dark, and stays dark through a power restore until the rubble resets. Coming BACK
        /// alive (a rubble reset respawned the mast) also clears any shot-out aspects -- the prop was rebuilt, so
        /// leaving them dead would give you a fresh-looking signal with permanently blind lenses and no way to
        /// repair it short of smashing the whole thing again.</summary>
        public void SetBroken(bool broken)
        {
            if (_broken == broken) return;
            _broken = broken;
            if (!broken) System.Array.Clear(_lensOut, 0, _lensOut.Length);
            Apply();
        }

        void Apply()
        {
            int lit = LitIndex;
            for (int i = 0; i < 3; i++)
            {
                var mi = Lens((Phase)i);
                if (mi == null) continue;
                // The lens is real prop geometry, so it always renders -- only the MATERIAL changes. Hiding it would
                // punch a hole in the signal head, which is the bug the streetlight bulb had.
                mi.MaterialOverride = i == lit ? _on[i] : _off[i];
            }
            if (_glow != null)
            {
                // Driven off the SAME index as the lenses. Reading _phase here instead is what let FlashRed light the
                // red lens while throwing green light on the mast.
                _glow.Visible = lit >= 0;
                if (lit >= 0) _glow.LightColor = _on[lit].Emission;
            }
            _shownLevel = -1f;   // force the next ApplyLevel through: the material just changed under it
            ApplyLevel();
        }

        // The continuous part: scale the lit lens's emission and the omni's energy. Only the LIT lens is touched, so
        // a breathing amber does not quietly brighten the two dark aspects next to it.
        void ApplyLevel()
        {
            _shownLevel = _level;
            int lit = LitIndex;
            if (lit < 0) return;
            _on[lit].EmissionEnergyMultiplier = LensEmission * _level;
            if (_glow != null) _glow.LightEnergy = _glowBase * _level;
        }
    }
}
