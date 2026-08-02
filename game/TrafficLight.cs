using Godot;

namespace UnturnedGodot
{
    // Traffic_Light_0 -- a mast arm carrying TWO signal heads, each with red/amber/green (21 placed on PEI across
    // 6 junctions). Unlike a streetlight this is not an on/off fixture: it runs a cycle.
    //
    // DELIBERATELY A DUMB PER-PROP TIMER (strawberry). Signals at one junction are NOT phase-locked, so you will
    // see green on crossing roads. That is the call: junction sync needs a cluster identity and buys realism the
    // game does not trade on. The offset is still a deterministic hash of world position rather than a spawn
    // counter, so each signal differs from its neighbours and every client agrees without replicating anything.
    //
    // Grid down -> slow blink, not dark. Confirmed against real practice: a signal on backup flashes AMBER to the
    // main road (proceed with caution) and RED to the side road (stop, treat as a 4-way). Which one a given mast
    // shows is the per-prop `SideRoad` flag -- an independent timer has no junction to derive it from.
    public partial class TrafficLight : Node3D
    {
        // Retail-ish timings. Amber is deliberately short and the all-red overlap is what stops the two phase
        // groups ever showing green together -- with 2s of overlap a client whose clock is a frame off still
        // cannot see a double-green.
        public static float GreenSec  = 9f;
        public static float AmberSec  = 2f;
        public static float AllRedSec = 2f;
        public static float LensEmission = 3.0f;   // shaded emissive, so it reaches HDR and blooms (see StreetLight)
        public static float FlashHz = 0.8f;        // slow blink -- ~0.6s lit, 0.6s dark, the real-world cadence

        /// <summary>How long the cabinet battery keeps the flash going after the grid dies, in in-game DAYS
        /// (strawberry: "force the power out blink state for like a couple in game days"). Real signal cabinets carry
        /// a battery back-up system rather than grid power for exactly this -- and it is finite, so the junction goes
        /// properly dark once it drains. Recharges on a power restore.</summary>
        public static float BatteryDays = 2f;

        /// <summary>Is this signal on the SIDE road? Real junctions in flash mode show amber to the main road
        /// (proceed with caution) and RED to the side road (stop, treat as a 4-way). Per-prop rather than derived,
        /// because with an independent timer there is no junction to ask which road is major (strawberry).</summary>
        public bool SideRoad;

        public enum Phase { Red, Amber, Green, FlashAmber, FlashRed, Off }

        MeshInstance3D _red, _amber, _green;
        Material[] _off = new Material[3];
        StandardMaterial3D[] _on = new StandardMaterial3D[3];
        OmniLight3D _glow;
        float _offset;       // per-PROP phase offset -- strawberry's call: a dumb independent timer per signal,
                             // not a junction-synced one. Deterministic from world position so it is identical
                             // on every client and across reloads without anything being replicated.
        bool _powered = true, _broken;
        Phase _phase = Phase.Red;
        float _batteryDeadAt = float.PositiveInfinity;   // clock seconds at which the backup battery gives out

        public Phase CurrentPhase => _phase;
        public bool PoweredForTest => _powered;
        public float OffsetForTest => _offset;
        /// <summary>Which lens is EMITTING right now: 0 red, 1 amber, 2 green, -1 none. The single source of truth --
        /// the flash phases have to map back onto the ordinary lenses, and when that mapping was written out
        /// separately in the material loop, the glow colour and the test accessor, two of the three copies quietly
        /// disagreed (FlashRed lit the red lens but threw GREEN light on the mast).</summary>
        public static int LensIndexFor(Phase p) => p switch
        {
            Phase.Red or Phase.FlashRed => 0,
            Phase.Amber or Phase.FlashAmber => 1,
            Phase.Green => 2,
            _ => -1,   // Phase.Off -- the blink's dark beat, or a drained battery
        };

        int LitIndex => _broken ? -1 : LensIndexFor(_phase);

        /// <summary>Is this lens actually EMITTING? Not "visible" -- the lens geometry is always drawn, only its
        /// material changes, same as the streetlight bulb. Independent of _powered because flash mode lights amber
        /// while unpowered.</summary>
        public bool LitForTest(Phase p) => Lens(p) != null && LitIndex == (int)p;
        public bool DarkForTest => LitIndex < 0;
        /// <summary>Has the backup battery drained? Only meaningful while unpowered.</summary>
        public bool BatteryDeadForTest => ClockSeconds() >= _batteryDeadAt;

        /// <summary>One prop's full cycle: green -> amber -> red, then round again.</summary>
        public static float CycleSec => GreenSec + AmberSec + AllRedSec;

        /// <param name="lenses">red/amber/green meshes split out of the prop by ObjMesh, already positioned by the
        /// placement basis. Kept parented to the prop -- same reason StreetLight adopts rather than reparents.</param>
        public static TrafficLight Make(Vector3 worldPos, float yawDeg, MeshInstance3D red, MeshInstance3D amber, MeshInstance3D green)
        {
            // Deterministic per-prop offset from world position, so signals are out of step with each other but
            // every client computes the same value. Same hash StreetLight uses for its per-lamp brightness.
            float h = Mathf.Sin(worldPos.X * 12.9898f + worldPos.Z * 78.233f) * 43758.5453f;
            return new TrafficLight
            {
                Position = worldPos, TopLevel = true,
                _offset = (h - Mathf.Floor(h)) * CycleSec,
                _red = red, _amber = amber, _green = green,
            };
        }

        // FlashAmber must map to the amber lens, not fall through to green -- and the blink-off beat maps to
        // nothing at all. Getting this wrong lights the wrong lens, silently.
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
            }
            // one small omni so the lit lens throws a little colour onto the mast at night; cheap, short range
            _glow = new OmniLight3D { OmniRange = 3.2f, LightEnergy = 0.6f, ShadowEnabled = false, Visible = false };
            AddChild(_glow);
            Apply();
        }

        /// <summary>Pin the signal to one aspect and stop the timer. For the --trafficlight harness: every state this
        /// thing has (each aspect, the flash's lit beat AND its dark beat, a drained battery) has to be renderable on
        /// demand, or the ones that only occur for 0.6s every few minutes never get looked at.</summary>
        public void ForcePhase(Phase p) { Frozen = true; _phase = p; Apply(); }
        public bool Frozen;

        float _flickerT;
        /// <summary>A warning brownout: the aspect stutters briefly without the grid actually dropping.</summary>
        public void FlickerPulse(float durationSec = 0.6f) => _flickerT = Mathf.Max(_flickerT, durationSec);
        public bool FlickeringForTest => _flickerT > 0f;

        public override void _Process(double delta)
        {
            if (_broken || Frozen) return;
            float t = ClockSeconds();
            // Grid down -> slow blink rather than dark (strawberry). A smashed signal still goes fully dark:
            // no power AND no fixture is different from no power alone.
            Phase p;
            if (_powered) p = PhaseAt(t, _offset);
            else if (t >= _batteryDeadAt) p = Phase.Off;   // cabinet battery drained -- the junction is now unsignalled
            else
            {
                // flash mode: side roads blink RED (stop), main roads blink AMBER (caution). Off-beat is dark.
                bool on = Mathf.PosMod(t * FlashHz, 1f) < 0.5f;
                p = !on ? Phase.Off : SideRoad ? Phase.FlashRed : Phase.FlashAmber;
            }
            // Brownout stutter (master's TriggerGlobalBrownout). A signal is NOT a GridLight -- that base models a
            // fixture as binary on/off, while an unpowered signal is in an ACTIVE state (the backup flash), so it
            // cannot inherit `Lit` without corrupting it. But it is still a grid consumer, and being the one thing in
            // town that ignores a brownout reads as a bug. Fast irregular dropout, then straight back to the cycle.
            if (_flickerT > 0f)
            {
                _flickerT -= (float)delta;
                if (Mathf.PosMod(t * 11f, 1f) < 0.45f) p = Phase.Off;
            }
            if (p != _phase) { _phase = p; Apply(); }
        }

        DayNightCycle _cyc; bool _cycLooked;
        /// <summary>Shared world clock in seconds. Derived from the day/night cycle's Day + Time so every peer
        /// computes the same value -- that is what makes the phase agree across a session with nothing replicated.
        /// Falls back to engine time when no cycle is in the scene (--lighttest, L1 harness), where determinism
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

        /// <summary>Pure: the phase this group shows at time t. Static and side-effect free so a test can assert
        /// the invariant that matters -- the two groups are never green at the same instant -- by sweeping the
        /// whole cycle rather than by watching two nodes and hoping.</summary>
        public static Phase PhaseAt(float t, float offset)
        {
            float u = Mathf.PosMod(t + offset, CycleSec);
            if (u < GreenSec) return Phase.Green;
            if (u < GreenSec + AmberSec) return Phase.Amber;
            return Phase.Red;
        }

        /// <summary>Grid power. Losing it drops the signal to a slow blink rather than dark -- what a real junction
        /// does on cabinet backup during a storm outage -- and starts the battery draining. Restoring it recharges.
        /// The deadline is stamped from the CLOCK rather than counted down per-frame so it survives a `dateset` jump
        /// and needs no per-frame work on the hundreds of props a map can hold.</summary>
        public void SetPowered(bool on)
        {
            if (_powered == on) return;
            _powered = on;
            _batteryDeadAt = on ? float.PositiveInfinity
                                : ClockSeconds() + BatteryDays * DayLen;
            Apply();
        }

        /// <summary>Seconds per in-game day, from the cycle when there is one. The fallback matches DayNightCycle's
        /// own default so a harness without a cycle still drains over a sane interval instead of instantly.</summary>
        float DayLen => _cyc != null && IsInstanceValid(_cyc) ? _cyc.DayLength : 120f;

        /// <summary>Smashed prop: dark, and stays dark through a power restore until the rubble resets.</summary>
        public void SetBroken(bool broken) { if (_broken == broken) return; _broken = broken; Apply(); }

        void Apply()
        {
            int lit = LitIndex;
            for (int i = 0; i < 3; i++)
            {
                var mi = Lens((Phase)i);
                if (mi == null) continue;
                // The lens is real prop geometry, so it always renders -- only the MATERIAL changes. Hiding it
                // would punch a hole in the signal head, which is the bug the streetlight bulb had.
                mi.MaterialOverride = i == lit ? _on[i] : _off[i];
            }
            if (_glow != null)
            {
                // Driven off the SAME index as the lenses. Reading _phase here instead is what let FlashRed light
                // the red lens while throwing green light on the mast, and left the glow burning through the
                // blink's dark beat and a drained battery.
                _glow.Visible = lit >= 0;
                if (lit >= 0) _glow.LightColor = _on[lit].Emission;
            }
        }
    }
}
