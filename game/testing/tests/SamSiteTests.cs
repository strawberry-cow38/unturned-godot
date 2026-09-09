using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE SAM SITE (strawberry 2026-09-09: "locks onto any heli within its radius and launches a barrage of rockets
    // that home onto the heli. launches 6 missiles, delay between each one and a longer delay while it reloads").
    //
    // The spec is entirely about TIMING and COUNT, and neither survives a screenshot. A film shows missiles leaving
    // the rail; it cannot tell you there were six rather than five, that the gap between them is the shot delay
    // rather than whatever the frame rate happened to be, or that the pause afterwards is the reload rather than a
    // lost lock. So the site is driven HERE at a fixed dt through its own HubProcess -- the same entry point TickHub
    // calls -- and the launches are counted. That also makes it independent of the render frame rate, which a test
    // stepping physics ticks has no control over.
    public sealed class SamSiteTests : GameTest
    {
        public override string Name => "vehicle.sam_site";
        public override double TimeoutSimSeconds => 60;

        const float Dt = 1f / 60f;

        // Step the site by hand rather than letting the engine tick it: the cadence is the subject, so the clock
        // has to be the test's.
        static void Drive(SamSite s, float seconds)
        {
            int n = Mathf.RoundToInt(seconds / Dt);
            for (int i = 0; i < n; i++) s.HubProcess(Dt);
        }

        Vehicle Heli(Vector3 at)
        {
            var v = Vehicle.BuildByName("hind");
            World.AddChild(v);
            v.GlobalPosition = at;
            v.Freeze = true; v.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
            v.ProcessMode = Node.ProcessModeEnum.Disabled;   // a drifting target would smear the range checks
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            var site = new SamSite();
            World.AddChild(site);
            site.GlobalPosition = Vector3.Zero;
            yield return Ticks(2);

            // ---- 1. NOTHING TO SHOOT AT. A launcher that fires at an empty sky is worse than one that never
            // fires: it would empty its rack the moment the world loaded.
            Drive(site, 3f);
            T.Check($"with no helicopter in range it holds fire (mode {site.Mode}, fired {site.Fired}, loaded {site.Loaded})",
                site.Fired == 0 && site.Loaded == SamSite.Rack && site.Mode == SamSite.State.Idle);

            // ---- 2. OUT OF RANGE IS NOT A TARGET. Teeth: without the radius test this passes anyway, because a
            // nearest-target search with no bound always finds something.
            var far = Heli(new Vector3(SamSite.Radius + 120f, 60f, 0f));
            yield return Ticks(2);
            Drive(site, 3f);
            T.Check($"a helicopter {SamSite.Radius + 120f:0} m out (radius {SamSite.Radius:0}) is not locked (fired {site.Fired})",
                site.Fired == 0 && site.Target == null);
            far.QueueFree();
            yield return Ticks(2);

            // ---- 3. THE BARRAGE. Six missiles, one per ShotDelay.
            var near = Heli(new Vector3(0f, 70f, -140f));
            yield return Ticks(2);

            // Just short of the sixth shot's due time: five away, one still in the rack. This is the check that
            // separates "fires six" from "fires until you stop looking" -- an unbounded loop passes a count-6
            // assertion too, if you only ever look after the sixth.
            Drive(site, SamSite.ShotDelay * 5f - 0.05f);
            T.Check($"five missiles are away and the sixth has not gone yet at t={SamSite.ShotDelay * 5f - 0.05f:0.00}s (fired {site.Fired})",
                site.Fired == 5 && site.Loaded == 1);

            Drive(site, 0.2f);
            T.Check($"the sixth completes the barrage and the rack is empty (fired {site.Fired}, loaded {site.Loaded}, mode {site.Mode})",
                site.Fired == SamSite.Rack && site.Loaded == 0 && site.Mode == SamSite.State.Reloading);

            // ---- 4. THE RELOAD IS THE LONGER DELAY, and it is a real wait rather than a formality. Sampled just
            // inside and just outside it, because a reload that is merely SHORTER than ShotDelay would still leave
            // the rack refilling "eventually" and the film would look fine.
            Drive(site, SamSite.ReloadDelay - 0.4f);
            T.Check($"nothing more is launched during the reload (fired {site.Fired} at t+{SamSite.ReloadDelay - 0.4f:0.0}s of {SamSite.ReloadDelay:0}s)",
                site.Fired == SamSite.Rack && site.Loaded == 0);

            Drive(site, 0.6f);
            T.Check($"after {SamSite.ReloadDelay:0}s the rack is back and it is shooting again (loaded {site.Loaded}, mode {site.Mode})",
                site.Loaded > 0 && site.Mode != SamSite.State.Reloading);

            Drive(site, SamSite.ShotDelay * 6f + 0.1f);
            T.Check($"the second barrage is another {SamSite.Rack} (fired {site.Fired} total)",
                site.Fired == SamSite.Rack * 2);

            // ---- 4b. THE HEAD POINTS AT THE TARGET, MEASURED ON THREE BEARINGS. This is a sign check, not an
            // accuracy check: a yaw derivation that is inverted produces a launcher facing the MIRROR bearing,
            // which is completely plausible in any single screenshot and is what I wrote first (atan2(x, -z)
            // rather than atan2(-x, -z)). Off-axis probes are the teeth -- straight ahead, a yaw sign is
            // unobservable, so a forward-only test certifies a backwards turret as correct.
            float worstDeg = 0f;
            foreach (var (label, at) in new (string, Vector3)[]
            {
                ("ahead",       new Vector3(0f, 60f, -120f)),
                ("hard left",   new Vector3(-120f, 60f, -30f)),
                ("hard right",  new Vector3(120f, 60f, -30f)),
                ("behind",      new Vector3(0f, 60f, 130f)),
            })
            {
                near.GlobalPosition = at;
                Drive(site, 6f);   // long enough for the rate-limited head to get there from any bearing
                float err = Mathf.RadToDeg(site.AimDirection.AngleTo((at - site.GlobalPosition).Normalized()));
                worstDeg = Mathf.Max(worstDeg, err);
                GD.Print($"[SAM] head aim {label,-11} err {err:0.0} deg");
            }
            T.Check($"the head points where it was aimed on four bearings (worst {worstDeg:0.0} deg)", worstDeg < 6f);

            // ---- 5. THE SEEKER. A missile launched 60 deg off the bearing has to close on the target rather
            // than fly the heading it left the tube on. Driven at the same fixed dt, for the same reason.
            var m = new SamMissile { Target = near };
            World.AddChild(m);
            m.GlobalPosition = new Vector3(0f, 8f, 0f);
            var off = (near.GlobalPosition - m.GlobalPosition).Normalized().Rotated(Vector3.Up, Mathf.DegToRad(60f));
            m.Fire(off);
            float startDist = m.GlobalPosition.DistanceTo(near.GlobalPosition);
            float best = startDist;
            // GodotObject.IsInstanceValid, not the Node method -- a GameTest is not a Node. And QueueFree only
            // takes effect at the end of a frame, which this loop never reaches, so a detonated missile stays
            // "valid" here: HubProcess early-outs on _spent, best stops moving, and the check still reads right.
            for (int i = 0; i < 600 && GodotObject.IsInstanceValid(m); i++)
            {
                m.HubProcess(Dt);
                if (!GodotObject.IsInstanceValid(m)) break;
                best = Mathf.Min(best, m.GlobalPosition.DistanceTo(near.GlobalPosition));
            }
            // The missile FREES itself on detonation, so "gone" is the pass condition and the closest approach is
            // the diagnostic. Boosting straight off a 60 deg error would leave it well over 100 m wide.
            T.Check($"a missile launched 60 deg off the bearing still homes to the fuse (start {startDist:0} m, closest {best:0.0} m, fuse {SamMissile.FuseRadius:0.0} m)",
                best <= SamMissile.FuseRadius + 0.5f);

            yield return Ticks(1);
        }
    }
}
