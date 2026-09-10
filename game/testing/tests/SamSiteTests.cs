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
            // A ground plane at Y=0, because two of the rules below are ABOUT the ground: without a world layer
            // to ray against, "is it landed" answers no for everything and the check would certify nothing.
            Rigs.Ground(World);
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

            // ---- 3. THE LOCK IS A REAL WAIT. Sampled just inside and just outside it: a targetting time that
            // merely EXISTS is not the ask, it has to be long enough to fly out of.
            var near = Heli(new Vector3(0f, 70f, -140f));
            yield return Ticks(2);
            Drive(site, 0.05f);
            T.Check($"the warning starts the instant it acquires, before any launch (warnings {site.Warnings}, fired {site.Fired})",
                site.Warnings >= 1 && site.Fired == 0);

            Drive(site, SamSite.LockTime - 0.25f);
            T.Check($"nothing is launched during the {SamSite.LockTime:0.0}s lock (fired {site.Fired}, lock {site.LockProgress:0.00}, warnings {site.Warnings})",
                site.Fired == 0 && site.LockProgress > 0.7f && site.Warnings > 3);

            // ...and it RESETS when the target goes, so skimming the radius costs nothing. Teeth: with a decaying
            // or persistent lock this passes anyway, because the second pass would inherit the first one's credit.
            near.GlobalPosition = new Vector3(SamSite.Radius + 200f, 70f, 0f);
            Drive(site, 0.5f);
            T.Check($"losing the target zeroes the lock rather than banking it (lock {site.LockProgress:0.00})",
                site.LockProgress < 0.35f && site.Fired == 0);
            near.GlobalPosition = new Vector3(0f, 70f, -140f);

            // ---- 4. THE BARRAGE. Six missiles, one per ShotDelay, once the lock is earned.
            Drive(site, SamSite.LockTime + 0.05f);
            T.Check($"the first missile goes as soon as the lock completes (fired {site.Fired})", site.Fired == 1);

            // Just short of the sixth shot's due time: five away, one still in the rack. This is the check that
            // separates "fires six" from "fires until you stop looking" -- an unbounded loop passes a count-6
            // assertion too, if you only ever look after the sixth.
            //
            // FIVE delays, not four. Shots land at LockTime + k*ShotDelay, so with LockTime 1.8 and ShotDelay
            // 0.55 they fall at 1.80, 2.35, 2.90, 3.45, 4.00, 4.55. The first Drive above already puts the clock
            // at 1.85, so driving a further 4*ShotDelay - 0.05 lands on 4.00 -- EXACTLY the fifth shot's instant,
            // not just short of the sixth. Whether that tick has fired the fifth round comes down to how 240
            // additions of 1f/60f accumulate, and it lands under: the check read `fired 4` every run since it
            // was written. 5*ShotDelay - 0.05 reaches 4.50, which is what the comment above always meant.
            Drive(site, SamSite.ShotDelay * 5f - 0.05f);
            T.Check($"five are away and the sixth has not gone yet, {SamSite.ShotDelay * 5f - 0.05f:0.00}s after the first (fired {site.Fired})",
                site.Fired == 5 && site.Loaded == 1);

            Drive(site, 0.2f);
            T.Check($"the sixth completes the barrage and the rack is empty (fired {site.Fired}, loaded {site.Loaded}, mode {site.Mode})",
                site.Fired == SamSite.Rack && site.Loaded == 0 && site.Mode == SamSite.State.Reloading);

            // ---- 5. THE RELOAD IS THE LONGER DELAY, and it is a real wait rather than a formality. Sampled just
            // inside and just outside it, because a reload that is merely SHORTER than ShotDelay would still leave
            // the rack refilling "eventually" and the film would look fine.
            Drive(site, SamSite.ReloadDelay - 0.4f);
            T.Check($"nothing more is launched during the reload (fired {site.Fired} at t+{SamSite.ReloadDelay - 0.4f:0.0}s of {SamSite.ReloadDelay:0}s)",
                site.Fired == SamSite.Rack && site.Loaded == 0);

            Drive(site, 0.6f);
            T.Check($"after {SamSite.ReloadDelay:0}s the rack is back and it is shooting again (loaded {site.Loaded}, mode {site.Mode})",
                site.Loaded > 0 && site.Mode != SamSite.State.Reloading);

            // The second barrage has to earn its lock again, so it is LockTime + five gaps behind the reload.
            Drive(site, SamSite.LockTime + SamSite.ShotDelay * 6f + 0.2f);
            T.Check($"the second barrage is another {SamSite.Rack} (fired {site.Fired} total)",
                site.Fired == SamSite.Rack * 2);

            // Everything from here to the destructible section is about TRACKING, not shooting -- and a live site
            // holding one target for six seconds a bearing empties two full racks into it, which would eventually
            // blow the target up and fail these checks for a reason that has nothing to do with what they test.
            site.DebugHoldFire = true;

            // ---- 6. THE HEAD POINTS AT THE TARGET, MEASURED ON THREE BEARINGS. This is a sign check, not an
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

            // ---- 7. THE SEEKER. A missile launched off the bearing has to close on the target rather than fly
            // the heading it left the tube on. 45 deg rather than something heroic, because the seeker is now
            // g-limited and a large error at long range is genuinely beyond it -- which is the point of the
            // evasion check below, not a weakness to hide here. Driven at the same fixed dt, for the same reason.
            var m = new SamMissile { Target = near };
            World.AddChild(m);
            m.GlobalPosition = new Vector3(0f, 8f, 0f);
            var off = (near.GlobalPosition - m.GlobalPosition).Normalized().Rotated(Vector3.Up, Mathf.DegToRad(45f));
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
            // Detonating ON the airframe counts, and is in fact the better outcome -- the missile is swept against
            // the world now, so it goes off on the hull rather than flying into the middle of the model and
            // waiting for a 6.5 m proximity fuse. A Hind is ~17 m long, so a hull strike lands a good few metres
            // from the vehicle's own origin; measuring against the origin alone would score a direct hit as a
            // miss. `Spent` is what separates "went off on it" from "sailed past".
            // ⚠ CORRECTION, not a kill -- and the comment above already said so while the assertion demanded one.
            // "the seeker is now g-limited and a large error at long range is genuinely beyond it" is exactly
            // right, and then this required Spent from a 45 deg error at 140 m. The seeker closes it to ~12 m; the
            // fuse is 6.5 m, so it sails past, which is the correct outcome for a 127 m turn radius -- the SAME
            // constant the evasion check below depends on. Requiring a hit here and a miss there is asking one
            // number to be two things.
            //
            // The claim that survives is the one that discriminates: a missile that ignored its seeker would be
            // over 100 m wide at closest approach, so 12 m proves it turned. The kill is asserted right below, on
            // a shot the launcher would actually take.
            T.Check($"a missile launched 45 deg off the bearing hauls itself back onto the target (start {startDist:0} m, closest {best:0.0} m)",
                best <= 15f);

            // ...AND IT CAN ACTUALLY KILL. Without this the suite proves only that the seeker turns and that a
            // hard break beats it -- "it never hits anything" would satisfy both. On the bearing is what the site
            // itself fires: SamSite aims the tube, so a 45 deg error is a fabricated handicap and this is the real
            // operational shot.
            var straight = new SamMissile { Target = near };
            World.AddChild(straight);
            straight.GlobalPosition = new Vector3(0f, 8f, 0f);
            straight.Fire((near.GlobalPosition - straight.GlobalPosition).Normalized());
            float sStart = straight.GlobalPosition.DistanceTo(near.GlobalPosition), sBest = sStart;
            for (int i = 0; i < 600 && GodotObject.IsInstanceValid(straight); i++)
            {
                straight.HubProcess(Dt);
                if (!GodotObject.IsInstanceValid(straight)) break;
                sBest = Mathf.Min(sBest, straight.GlobalPosition.DistanceTo(near.GlobalPosition));
            }
            T.Check($"a missile launched ON the bearing detonates on it (start {sStart:0} m, closest {sBest:0.0} m, spent {straight.Spent})",
                straight.Spent && sBest <= SamMissile.FuseRadius + 1f);

            // ---- 8. AND IT CAN BE BEATEN (strawberry: "make it possible to evade the missiles"). A missile that
            // is always dodgeable is as bad as one that never is, so this is the paired claim to the check above:
            // the SAME seeker, given a target that breaks hard across its nose at close range, misses AND STAYS
            // MISSED. The break is 90 deg at 45 m -- inside the missile's own turn radius (v^2/a = 127 m at the
            // speed cap), which is the geometry that makes evasion a manoeuvre rather than a dice roll.
            near.GlobalPosition = new Vector3(0f, 40f, -300f);
            var m2 = new SamMissile { Target = near };
            World.AddChild(m2);
            m2.GlobalPosition = new Vector3(0f, 40f, 0f);
            m2.Fire(Vector3.Forward);
            float closest2 = float.MaxValue;
            bool broke = false;
            for (int i = 0; i < 900 && GodotObject.IsInstanceValid(m2); i++)
            {
                float d = m2.GlobalPosition.DistanceTo(near.GlobalPosition);
                // Break hard sideways once it is committed and close: the classic beam manoeuvre.
                if (!broke && d < 45f) { broke = true; near.GlobalPosition += new Vector3(70f, 0f, 0f); }
                m2.HubProcess(Dt);
                if (!GodotObject.IsInstanceValid(m2)) break;
                closest2 = Mathf.Min(closest2, m2.GlobalPosition.DistanceTo(near.GlobalPosition));
            }
            T.Check($"a hard break inside its turn radius defeats it (closest after the break {closest2:0.0} m vs {SamMissile.FuseRadius:0.0} m fuse)",
                broke && closest2 > SamMissile.FuseRadius);

            // ---- 9. WHAT IS NOT A TARGET (strawberry: "if target is grounded, or below the SAM, ignore it").
            // Both rules get their own heli rather than one moved twice, because they are separate reasons and a
            // single probe cannot tell which one did the work.
            near.QueueFree();
            yield return Ticks(2);

            var landed = Heli(new Vector3(40f, 3f, -60f));   // above the launcher's base, but 3 m over the ground
            yield return Ticks(2);
            Drive(site, SamSite.LockTime + 1f);
            T.Check($"a helicopter sitting on the ground is ignored (target {(site.Target == null ? "none" : "LOCKED")}, lock {site.LockProgress:0.00})",
                site.Target == null && site.LockProgress < 0.05f);
            landed.QueueFree();
            yield return Ticks(2);

            site.GlobalPosition = new Vector3(0f, 90f, 0f);   // put the launcher on a clifftop
            var below = Heli(new Vector3(30f, 45f, -60f));    // flying, but 45 m BELOW it
            yield return Ticks(2);
            Drive(site, SamSite.LockTime + 1f);
            T.Check($"a helicopter below the launcher is ignored (target {(site.Target == null ? "none" : "LOCKED")})",
                site.Target == null);
            below.QueueFree();
            yield return Ticks(2);

            // ---- 10. LINE OF SIGHT (strawberry: "make sam require LOS"). Both directions, because "never locks"
            // passes the blocked half on its own -- the ridge has to come away and the lock has to return.
            site.GlobalPosition = Vector3.Zero;
            var hidden = Heli(new Vector3(0f, 70f, -140f));
            var ridge = new StaticBody3D { CollisionLayer = 1 << 0 };
            ridge.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(120f, 120f, 4f) } });
            World.AddChild(ridge);
            ridge.GlobalPosition = new Vector3(0f, 35f, -70f);   // straddles the sightline halfway out
            yield return Ticks(2);
            Drive(site, SamSite.LockTime + 1f);
            T.Check($"a helicopter behind a ridge is not locked (target {(site.Target == null ? "none" : "LOCKED")}, lock {site.LockProgress:0.00})",
                site.Target == null && site.LockProgress < 0.05f);

            ridge.QueueFree();
            yield return Ticks(2);
            Drive(site, 0.4f);
            T.Check($"...and it is picked up again the moment the ridge is gone (target {(ReferenceEquals(site.Target, hidden) ? "LOCKED" : "none")})",
                ReferenceEquals(site.Target, hidden));

            // ---- 11. FLARES (strawberry: "add the ability to flare on rmb in military aircraft only. 1 minute
            // between flares. kills targetting for a full barrage").
            //
            // The blind TIMER is not exercised here on purpose: it counts down in Vehicle.PhysicsTick, and these
            // helicopters are ProcessMode.Disabled so the aim probes above are not smeared by a drifting airframe.
            // Clearing the flag by hand tests the GATE, which is the part with the logic in it; asserting the
            // countdown would only be re-testing subtraction.
            var civ = Vehicle.BuildByName("hummingbird");
            World.AddChild(civ);
            // civ.IsHeli is the TEETH: SpecFor falls through to the jeep on an unknown name, and a jeep is not
            // military either -- so without this the check would pass for entirely the wrong reason.
            T.Check($"only military airframes carry flares (hind {hidden.IsMilitary}, hummingbird heli={civ.IsHeli} military={civ.IsMilitary})",
                hidden.IsMilitary && civ.IsHeli && !civ.IsMilitary && !civ.FlaresReady);
            civ.QueueFree();

            T.Check($"a full barrage of blindness is the SAM's own numbers ({Flares.BlindSeconds:0.00}s = {SamSite.LockTime:0.0} lock + {SamSite.Rack} x {SamSite.ShotDelay:0.00})",
                Mathf.Abs(Flares.BlindSeconds - (SamSite.LockTime + SamSite.Rack * SamSite.ShotDelay)) < 0.001f);

            // A round in the air, then flares: the missile has to be broken by the salvo, not merely un-re-locked.
            var m3 = new SamMissile { Target = hidden };
            World.AddChild(m3);
            m3.GlobalPosition = new Vector3(0f, 60f, -60f);
            m3.Fire((hidden.GlobalPosition - m3.GlobalPosition).Normalized());
            m3.HubProcess(Dt);
            T.Check($"a live round is warning the aircraft before the flare ({SamMissile.AnyWarning(hidden)})", SamMissile.AnyWarning(hidden));

            T.Check($"flares deploy on a ready military heli (cooldown was {hidden.FlareCooldown:0}s)", Flares.Deploy(hidden));
            T.Check($"...and set a {Flares.CooldownSeconds:0}s cooldown that refuses a second salvo (cooldown {hidden.FlareCooldown:0}s)",
                Mathf.Abs(hidden.FlareCooldown - Flares.CooldownSeconds) < 0.01f && !Flares.Deploy(hidden));
            T.Check($"the salvo breaks the round already homing on it (still warning: {SamMissile.AnyWarning(hidden)})",
                !SamMissile.AnyWarning(hidden));

            Drive(site, 0.5f);
            T.Check($"and the site cannot lock a flared aircraft (target {(site.Target == null ? "none" : "LOCKED")}, lock {site.LockProgress:0.00})",
                site.Target == null && site.LockProgress < 0.05f);

            hidden.FlareBlind = 0f;   // the countdown would do this; here it is the GATE under test
            Drive(site, 0.3f);
            T.Check($"once the flares burn out it locks again (target {(ReferenceEquals(site.Target, hidden) ? "LOCKED" : "none")})",
                ReferenceEquals(site.Target, hidden));

            // ---- 12. DESTRUCTIBLE (strawberry: "give the sam site collision and make it destructable"). Two
            // halves worth separating: partial damage must NOT stop it -- a launcher that goes cold on the first
            // bullet is not destructible, it is fragile -- and a dead one must go completely quiet rather than
            // merely stop launching.
            site.DebugHoldFire = false;   // firing is the subject again: "a destroyed site launches nothing" has no
                                          // teeth against a site that was not going to launch anyway.
            T.Check($"a fresh site is alive and shootable ({site.Health:0}/{SamSite.MaxHealth:0} hp, collides on layer {site.CollisionLayer})",
                !site.Destroyed && site.Health > 0f && site.CollisionLayer != 0u);

            site.TakeDamage(SamSite.MaxHealth * 0.5f);
            Drive(site, 0.2f);
            T.Check($"half its health does not stop it (health {site.Health:0}, target {(site.Target == null ? "none" : "LOCKED")})",
                !site.Destroyed && ReferenceEquals(site.Target, hidden));

            int firedBefore = site.Fired;
            site.TakeDamage(SamSite.MaxHealth);
            Drive(site, SamSite.LockTime + SamSite.ShotDelay * 3f);
            T.Check($"a destroyed site goes cold -- no lock, no launch (destroyed {site.Destroyed}, target {(site.Target == null ? "none" : "LOCKED")}, fired {site.Fired} vs {firedBefore})",
                site.Destroyed && site.Target == null && site.Fired == firedBefore);
            T.Check($"...but the wreck keeps its collision (layer {site.CollisionLayer})", site.CollisionLayer != 0u);

            yield return Ticks(1);
        }
    }
}
