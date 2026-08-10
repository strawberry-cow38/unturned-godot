using Godot;

namespace UnturnedGodot
{
    // Shared base for grid-powered on/off light fixtures (StreetLight, LampLight). Owns the ONE definition of "is
    // this light on" (Lit) plus the grid-follow reaction delay + brownout flicker, so every fixture agrees on the
    // state and gets the blackout/brownout drive for free. Subclasses supply the visual (BuildVisual + ApplyLit).
    //
    // Extracted from StreetLight so a second fixture (LampLight) reuses the exact power/flicker logic rather than a
    // second copy of "is it on" -- the class of bug that shipped the mote/lit desync (tinyclaw).
    public abstract partial class GridLight : Node3D
    {
        // per-fixture on/off delay = ReactionMax * (0.15..1.0); the spread staggers a whole street on a grid flip.
        public static float ReactionMax = 2.2f;
        protected float _worn = 1f;   // per-fixture brightness jitter (deterministic per position)
        float _reactionFrac;          // [0,1] deterministic per-fixture
        float _reaction, _transT, _flickT;
        bool _transitioning, _targetLit, _shownLit, _flickShow;

        protected bool _night, _broken, _bulbOut;
        protected bool _powered = true;   // grid feeding the fixture (default on until a grid says otherwise)

        // THE ONE definition of "on": powered, not dead (broken pole / shot-out bulb -- both "this fixture is dead",
        // NOT streetlight-specific: tinyclaw). Night-gating is a per-fixture flag -- a streetlight only lights at
        // night, an indoor lamp is on WHENEVER powered (master).
        protected virtual bool NightGated => true;
        protected bool Lit => _powered && !_broken && !_bulbOut && (!NightGated || _night);

        // Deterministic per-position jitter + reaction fraction (two independent hashes so brightness and delay
        // aren't correlated). Subclass factories call this once with the world position.
        protected void InitJitter(Vector3 pos)
        {
            float h = Mathf.Sin(pos.X * 12.9898f + pos.Z * 78.233f) * 43758.5453f;
            _worn = 0.95f + (h - Mathf.Floor(h)) * 0.10f;               // 0.95..1.05
            float h2 = Mathf.Sin(pos.X * 39.3468f + pos.Z * 11.7654f) * 24634.6345f;
            _reactionFrac = h2 - Mathf.Floor(h2);
        }

        protected virtual string LightGroup => "gridlights";   // DayNightCycle sweeps this group; StreetLight = "streetlights"
        protected abstract void BuildVisual();                 // build the fixture's own emitters (spot/lens/cone, or a lamp bulb)
        protected abstract void ApplyLit(bool lit);            // toggle those emitters on/off (the flicker drives this)
        protected virtual void PostRefresh() { }               // StreetLight folds in its motes here
        protected virtual void PrimeFlicker() { }              // StreetLight keeps its motes emitting through a flicker

        public override void _Ready()
        {
            AddToGroup(LightGroup);
            BuildVisual();
            var dn = GetTree().GetFirstNodeInGroup("daynight") as DayNightCycle;
            _night = dn == null || DayNightCycle.IsNightTime(dn.Time);   // no cycle in this mode -> default "night"
            _powered = PowerNet.MainsLive;                               // municipal grid feed (default on)
            Refresh();
            SetProcess(false);   // idle fixtures do NOT tick every frame -- _Process runs ONLY while a transition flickers
        }

        // Grid hook. animate=true (the DayNightCycle sweep) plays the reaction-delay + flicker; default snaps (tests, spawn).
        public void SetPowered(bool on, bool animate = false) { if (_powered == on) return; _powered = on; if (animate) BeginTransition(); else Refresh(); }
        public void SetNight(bool on, bool animate = false) { if (_night == on) return; _night = on; if (animate) BeginTransition(); else Refresh(); }

        // Smashed prop -> dead for good (until rubble reset). STATE, not a one-shot off: Refresh re-derives Lit on
        // every grid/night tick, so a merely-off fixture would relight itself.
        public void SetBroken(bool broken)
        {
            if (_broken == broken) return;
            _broken = broken;
            if (!broken) _bulbOut = false;   // a rubble reset rebuilds the prop with an intact bulb
            Refresh();
        }

        // Shoot the bulb out: dark for good, geometry stays. Distinct from SetBroken (rubble).
        public bool ShootOutBulb()
        {
            if (_bulbOut || _broken) return false;
            _bulbOut = true;
            Refresh();
            return true;
        }
        public bool BulbOutForTest => _bulbOut;
        public bool TransitioningForTest => _transitioning;   // still mid reaction-delay/flicker, not settled

        protected void Refresh()
        {
            if (_transitioning) { _transitioning = false; SetProcess(false); }   // a HARD state change cancels any in-flight flicker
            bool lit = Lit;
            _shownLit = lit;
            ApplyLit(lit);
            PostRefresh();
        }

        // Grid/day-night flipped this fixture and asked to ANIMATE: don't snap. Wait a per-fixture reaction delay
        // while blinking irregularly toward the new state (odds ramp 0->1 across the delay), then settle.
        void BeginTransition()
        {
            bool target = Lit;
            if (target == _shownLit && !_transitioning) return;   // no visible change and nothing in flight
            _targetLit = target;
            if (!_transitioning)
            {
                _transitioning = true;
                _transT = 0f; _flickT = 0f; _flickShow = _shownLit;
                _reaction = ReactionMax * (0.15f + 0.85f * _reactionFrac);
                PrimeFlicker();
                SetProcess(true);
            }
            // already mid-transition (grid flapped): keep the running delay, re-aim at the new target.
        }

        // A brownout FLICKER SIGNAL: stutter briefly, then settle back to the SAME lit state -- a visual dip, not a
        // power change. No-op on a dark fixture (gated on the authoritative Lit so a blink never resurrects a dead one).
        public void FlickerPulse(float durationSec = 0.6f)
        {
            if (!Lit) return;
            _targetLit = true; _transitioning = true; _transT = 0f; _flickT = 0f; _flickShow = true;
            _reaction = Mathf.Max(0.05f, durationSec);
            PrimeFlicker();
            SetProcess(true);
        }

        public override void _Process(double delta)
        {
            using var _prof = Prof.Scope("GridLight");
            if (!_transitioning) return;
            _transT += (float)delta;
            _flickT -= (float)delta;
            if (_flickT <= 0f)
            {
                float p = Mathf.Clamp(_transT / Mathf.Max(0.05f, _reaction), 0f, 1f);   // 0 at the start -> 1 at settle
                _flickShow = GD.Randf() < p ? _targetLit : !_targetLit;                  // early: mostly OLD state; late: mostly NEW
                _flickT = 0.04f + GD.Randf() * 0.08f;                                    // irregular ~8-25 Hz stutter
                ApplyLit(_flickShow);
            }
            if (_transT >= _reaction)   // settle to the clean final state
            {
                _transitioning = false;
                SetProcess(false);
                Refresh();
            }
        }
    }
}
