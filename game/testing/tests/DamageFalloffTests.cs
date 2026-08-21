using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // DISTANCE DAMAGE FALLOFF (strawberry 2026-08-15: "i also dont want the range to be just a bullet nope wall
    // that deletes past it. the killer is the damage dropoff over distance").
    //
    // Before this a bullet dealt the same damage at 5 m and at its last metre, then vanished -- range was a cliff.
    // Now damage decays from Damage_Falloff_Start to Damage_Falloff_End and floors at Damage_Falloff_Min, and the
    // bullet keeps flying well past that (Ballistic_Steps pushed to ~450 m) so distance costs damage, not the shot.
    //
    // The pure-function checks below are cheap and prove almost nothing on their own: GunDef could compute a
    // perfect curve while the fire path ignores it entirely. The load-bearing part is the LIVE pair -- the same
    // dummy shot at two distances -- because that is the only thing that fails if the bullet never carries the
    // curve to the point of impact.
    public sealed class DamageFalloffTests : GameTest
    {
        public override string Name => "gun.damage_falloff";
        public override double TimeoutSimSeconds => 60;

        static GunDef Def(string gun) =>
            GunDef.FromDatText(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/") + gun + ".dat"));

        public override IEnumerable<Step> Run()
        {
            // ---- 1. the curve is declared and parsed
            var ef = Def("eaglefire");
            // FLOOR IS 0.65 SINCE 2026-08-21, and it is PER ROUND now (master: "id also like the damage dropoff
            // effect to be reduced slightly, so its not useless at range"). At 20 damage a 0.5 floor meant ten
            // shots to kill at distance; 0.65 makes it eight. The old assertion pinned 0.5 and fired when this
            // changed -- correctly. Asserted as a RANGE rather than the exact number so a later per-round retune
            // does not have to come back here, while a floor that collapses toward useless still trips it.
            T.Check($"the eaglefire declares a falloff window ({ef.FalloffStart}..{ef.FalloffEnd} m, floor {ef.FalloffMin})",
                ef.FalloffStart > 50f && ef.FalloffEnd > ef.FalloffStart && ef.FalloffMin >= 0.6f && ef.FalloffMin <= 0.75f);
            T.Check($"full damage inside the window ({ef.FalloffAt(50f)})", Mathf.IsEqualApprox(ef.FalloffAt(50f), 1f));
            T.Check($"floored past the end ({ef.FalloffAt(400f)})", Mathf.IsEqualApprox(ef.FalloffAt(400f), ef.FalloffMin));
            // Derived from the gun's OWN floor rather than a literal, or this pins the floor a second time in
            // disguise and every future retune fails here for the wrong reason.
            float mid = ef.FalloffAt((ef.FalloffStart + ef.FalloffEnd) * 0.5f);
            float midWant = 1f + (ef.FalloffMin - 1f) * 0.5f;
            T.Check($"half way through the window is half way down ({mid:0.###}, want {midWant:0.###} from floor {ef.FalloffMin:0.##})",
                Mathf.IsEqualApprox(mid, midWant, 0.01f));

            // THE DISABLE PATH, tested on the MECHANISM rather than on a specimen. This used to pick a real gun
            // that declared no falloff and assert it behaved as before -- first the colt, then the nailgun. Both
            // got swept, because master's 2026-08-21 pass is deliberately global: every cartridge now has a
            // dropoff, so "an untouched gun" is not a thing that exists any more and any control built on one is
            // a control with a shelf life. What must still hold is that FalloffStart <= 0 DISABLES falloff, since
            // that is the branch protecting anything added later that declares none. Built here rather than
            // borrowed from content, so no future balance pass can invalidate it.
            var none = GunDef.FromDatText("Useable Gun\nDamage 30\nRange 100\n");
            T.Check($"a gun declaring no falloff is unaffected at any range ({none.FalloffAt(500f)} at 500 m)",
                Mathf.IsZeroApprox(none.FalloffStart) && Mathf.IsEqualApprox(none.FalloffAt(500f), 1f));

            // ---- 2. the bullet must NOT die at the old range any more
            T.Check($"the eaglefire's bullet reaches ~450 m ({ef.MuzzleVelocity * 0.02f * ef.BallisticSteps:0} m)",
                ef.MuzzleVelocity * 0.02f * ef.BallisticSteps > 400f);

            // ---- 3. LIVE. Same dummy, two distances -- one dummy moved rather than two on the lane, because a
            // near target parked on the firing line eats the round meant for the far one and reads as "no damage".
            Rigs.Ground(World);
            var p = new PlayerController { CaptureMouse = false, Inventory = new SDG.Unturned.PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(40);
            p.EquipHeldGun("eaglefire");
            p.Ammo = 60;
            yield return Until(() => p.HeldItemReady, 6);
            // ADS matters enormously here: hip spread is ~1.4 deg (metres wide at 200 m), ADS is 0.07 deg.
            // It is re-asserted before EVERY shot below, not once -- the live code resumes ADS from a held RMB
            // after the shoot animation, so in a test without input it lapses back to hip after the first round.
            // Measured while debugging: shot two landed 5.5 m laterally at 292 m, exactly hip-cone width.

            var d = new TargetDummy { MaxHealth = 100000f, RespawnSeconds = 999f };
            World.AddChild(d);
            float torsoY = (Humanoid.TorsoMinY + Humanoid.HeadMinY) * 0.5f;

            float near = 0f, far = 0f;
            foreach (float range in new[] { 50f, 200f })
            {
                d.GlobalPosition = new Vector3(0f, 0f, -range);
                yield return Ticks(5);
                float eye = p.EyesWorld.Y;
                // Aim ABOVE the torso by the ballistic drop, or the round lands low and resolves as a leg hit
                // (x0.6) -- which would look exactly like falloff and quietly corrupt the comparison.
                float v = p.Gun.MuzzleVelocity, gg = 9.81f * p.Gun.GravityMultiplier;
                float drop = 0.5f * gg * (range / v) * (range / v);
                float pitch = Mathf.RadToDeg(Mathf.Atan2((torsoY + drop) - eye, range));
                // ZERO THE BODY YAW as well as the pitch. Recoil now folds into the aim and STAYS there (that is
                // the whole point of the camera-recoil change), so shot two leaves rotated by shot one's kick --
                // measured 1.09 deg of inherited yaw, which is 6.4 m of lateral miss at 347 m. Exactly the trap
                // net.shell_fire_zombie had, which I fixed earlier today and then rebuilt here.
                p.RotationDegrees = new Vector3(0f, 0f, 0f);
                p.DebugSetPitch(pitch);
                // ADS LAST, and fire with NOTHING in between. Aiming is re-driven from the RMB state every frame
                // in the live input path, so a test's ForceAim survives only until the next poll -- asserting it
                // ten ticks before the shot measured a state the bullet never saw (shot two left at 1.05 deg,
                // full hip cone, and landed 6.4 m wide at 347 m).
                p.ForceAim(true);
                yield return Ticks(30);
                T.Check($"ADS holds at the instant of the {range:0} m shot (alpha {p.CurrentAimAlpha:0.###})",
                    p.CurrentAimAlpha > 0.9f);
                float before = d.Health;
                T.Check($"fired at {range:0} m (pitch {pitch:0.##} deg, drop comp {drop:0.##} m)", p.Fire());
                for (int i = 0; i < 90 && Mathf.IsEqualApprox(d.Health, before); i++) yield return Ticks(1);
                T.Check($"...and hit it at {range:0} m (zone {d.LastZone}, {d.LastDamage:0.##} dmg)", d.Health < before);
                // Normalise the ZONE out. The subject here is falloff, not marksmanship -- a shot that lands in
                // the head deals 2x and would read as "falloff did not apply". Dividing by the zone multiplier
                // measures the number falloff actually scales, whichever band the round happened to find.
                float zmul = d.LastZone == TargetDummy.HitZone.Head ? Humanoid.HeadMult
                           : d.LastZone == TargetDummy.HitZone.Torso ? Humanoid.TorsoMult : Humanoid.LegMult;
                float baseDmg = d.LastDamage / zmul;
                if (range < 100f) near = baseDmg; else far = baseDmg;
            }

            // THE CHECK. Not "damage happened" -- the far shot must be strictly weaker, by the declared curve.
            float expected = 20f * ef.FalloffAt(200f);
            T.Check($"the far shot is weaker than the near one (base {near:0.##} at 50 m -> {far:0.##} at 200 m)", far < near - 0.5f);
            T.Check($"...by the declared curve, not some other reason ({far:0.##} vs {expected:0.##} expected)",
                Mathf.IsEqualApprox(far, expected, 0.6f));
            T.Check($"the near shot is still full damage ({near:0.##})", Mathf.IsEqualApprox(near, 20f, 0.2f));

            p.QueueFree();
            yield break;
        }
    }
}
