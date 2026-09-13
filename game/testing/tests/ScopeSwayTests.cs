using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // SCOPE SWAY MOVES THE CAMERA, NOT THE VIEWMODEL (strawberry: "with scope sway, have it move the WHOLE camera
    // (and thus aim point) instead of just the scope viewmodel").
    //
    // The distinction is the entire point, and it is invisible to a screenshot: a viewmodel-only sway wobbles the
    // glass while the bullet keeps going exactly where it went before, so the player fights a crosshair that isn't
    // lying to them -- it's the gun that is. So this asserts on LookPitchDegrees, which is the value both the
    // camera and the firing basis read, rather than on anything the scope draws.
    public sealed class ScopeSwayTests : GameTest
    {
        public override string Name => "gun.scope_sway";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = new PlayerController { CaptureMouse = false, Inventory = new SDG.Unturned.PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(40);
            p.EquipHeldGun("timberwolf");
            yield return Until(() => p.HeldItemReady, 6);
            p.DebugSetPitch(0f);
            yield return Ticks(5);

            // ---- 1. THE GATE. No optic, no sway -- and no drift in the aim either.
            float restPitch = p.LookPitchDegrees;
            yield return Ticks(60);
            T.Check($"unscoped: no sway contribution ({p.DebugScopeSway})", p.DebugScopeSway.Length() < 0.0001f);
            T.Check($"...and the aim sits still ({p.LookPitchDegrees - restPitch:0.####} deg drift over 60 ticks)",
                Mathf.Abs(p.LookPitchDegrees - restPitch) < 0.01f);

            // ---- 2. SWAY DRIVES THE AIM. Sampled over a couple of seconds so the slower yaw term completes an arc.
            p.DebugForceScopeSway = true;
            float minP = 999f, maxP = -999f, minY = 999f, maxY = -999f;
            for (int i = 0; i < 200; i++)
            {
                yield return Ticks(1);
                float ap = p.LookPitchDegrees, ay = Mathf.RadToDeg(p.Rotation.Y);
                minP = Mathf.Min(minP, ap); maxP = Mathf.Max(maxP, ap);
                minY = Mathf.Min(minY, ay); maxY = Mathf.Max(maxY, ay);
            }
            float spanP = maxP - minP, spanY = maxY - minY;
            // The AIM itself swept an arc. A viewmodel-only sway leaves this at exactly 0.
            T.Check($"the aim PITCH sweeps a real arc ({spanP:0.###} deg)", spanP > 0.15f);
            T.Check($"...and the aim YAW too ({spanY:0.###} deg)", spanY > 0.2f);
            // Bounded: sway is a wobble, not a drift. If the delta bookkeeping were wrong it would accumulate and
            // this is the check that catches it -- 0.30 amplitude means well under 2 deg of total travel.
            T.Check($"pitch stays bounded, not accumulating ({spanP:0.###} deg)", spanP < 2f);
            T.Check($"yaw stays bounded ({spanY:0.###} deg)", spanY < 3f);
            T.Check($"sway is being tracked ({p.DebugScopeSway})", p.DebugScopeSway.Length() > 0.0001f);

            // ---- 3. IT UNWINDS. Turning it off must return what it borrowed, or every scoped moment leaves the
            // aim permanently nudged -- the failure mode of sharing _pitchDeg with recoil, which never returns.
            float before = p.LookPitchDegrees;
            p.DebugForceScopeSway = false;
            yield return Ticks(30);
            T.Check($"sway unwinds to zero ({p.DebugScopeSway})", p.DebugScopeSway.Length() < 0.0001f);
            T.Check($"...and the aim comes back within its own amplitude ({Mathf.Abs(p.LookPitchDegrees - restPitch):0.###} deg of rest)",
                Mathf.Abs(p.LookPitchDegrees - restPitch) < 0.5f);

            // ---- 4. PER-GUN STEADINESS actually reaches the live viewmodel (Scope_Sway_Scale).
            //
            // strawberry shipped-and-playtested 2026-08-15: "idk what u did for the scope sway reduction but i
            // dont think it worked. looks identical." It did not. The AUG/SG550 0.3 was parsed into GunDef, stored
            // on Viewmodel, read by the oscillator and folded into the camera -- and still did nothing, because
            // LoadGun pushed it onto the viewmodel that EquipHeldGun then freed and replaced. Every value was
            // correct at every point I inspected; the object holding them was thrown away.
            //
            // So this measures the AMPLITUDE THE CAMERA ACTUALLY SWEPT, per gun, in ONE run -- not that the field
            // parses (it always did) and not that some viewmodel holds 0.3 (one did). Both guns are swept back to
            // back so a slow frame or a different rest pose cannot masquerade as the effect.
            float[] arcs = new float[2];
            string[] guns = { "timberwolf", "augewehr" };
            for (int g = 0; g < 2; g++)
            {
                p.DebugForceScopeSway = false;
                p.EquipHeldGun(guns[g]);
                yield return Until(() => p.HeldItemReady, 6);
                p.DebugSetPitch(0f);
                yield return Ticks(5);
                p.DebugForceScopeSway = true;
                float lo = 999f, hi = -999f;
                for (int i = 0; i < 200; i++)
                {
                    yield return Ticks(1);
                    float ap = p.LookPitchDegrees;
                    lo = Mathf.Min(lo, ap); hi = Mathf.Max(hi, ap);
                }
                arcs[g] = hi - lo;
                p.DebugForceScopeSway = false;
                yield return Ticks(30);
            }
            // Both must actually sway, or the ratio below is 0/0 and passes for the wrong reason.
            T.Check($"the 1.0-scale gun sweeps a full arc ({arcs[0]:0.###} deg)", arcs[0] > 0.15f);
            T.Check($"the 0.3-scale gun still sways -- reduced, not disabled ({arcs[1]:0.###} deg)", arcs[1] > 0.02f);
            float ratio = arcs[1] / arcs[0];
            // THE CHECK. Under the bug this was 1.00 -- identical, which is exactly what strawberry saw.
            T.Check($"the AUG's declared 0.3 reaches the live viewmodel (ratio {ratio:0.###} of the 1.0 gun)",
                ratio > 0.2f && ratio < 0.45f);

            p.QueueFree();
            yield break;
        }
    }

    /// <summary>The two things about hold-breath-to-steady that shipped WRONG, both caught by strawberry on
    /// the running build rather than by any of the 21 green tests over the feature.
    ///
    /// ⚠ WHY THE GREEN SUITE MISSED BOTH, because it is the same reason twice. ScopeSteadySimTests drives
    /// `ScopeSteadySim.Step(wants, ref ox, dt)` with an oxygen variable IT owns, so the drain accumulates
    /// across ticks and the floor is reached -- production re-read the replicated bar into a fresh local
    /// every frame and discarded the result, so it could never fall at all. And ScopeSteadyWireTests posts
    /// `MoveInput.ButtonSteady` onto the wire directly, which proves the SERVER spends air for a bit it was
    /// handed, never that anything on this side produces the bit or spends anything locally.
    ///
    /// So this test presses the control the way a player does -- through PlayerController, on a real scoped
    /// gun -- and reads the two values a player actually sees: the bar, and where the sight is pointing.</summary>
    public sealed class ScopeSteadyBehavesTests : GameTest
    {
        public override string Name => "gun.scope_steady";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = new PlayerController { CaptureMouse = false, Inventory = new SDG.Unturned.PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(40);
            p.EquipHeldGun("timberwolf");
            yield return Until(() => p.HeldItemReady, 6);
            p.DebugSetPitch(0f);
            // The SYNTHETIC oscillator, because `ScopeZoom` is 90/Fov off a SubViewport camera that headless
            // does not create -- the real one cannot run here at all. It is driven by the same clock and the
            // same SwayRateScale, so it models the mechanic under test; what it does NOT cover is the real
            // oscillator's zoom and stance terms, which gun.scope_sway owns.
            p.DebugForceScopeSway = true;
            System.Environment.SetEnvironmentVariable("UG_STEADY", null);
            yield return Ticks(120);
            float offCentre = 0f; int waited = 0;
            while (offCentre < 0.004f && waited++ < 600) { yield return Ticks(1); offCentre = p.DebugScopeSway.Length(); }
            T.Check($"the sight has drifted off centre before we steady ({offCentre:0.####} deg)", offCentre >= 0.004f);

            float oxBefore = p.Oxygen;
            var atEngage = p.DebugScopeSway;

            System.Environment.SetEnvironmentVariable("UG_STEADY", "1");
            yield return Ticks(100);                       // 2 s of held breath

            // ---- 1. IT FREEZES, IT DOES NOT RECENTRE (strawberry: "it should just stop the swaying from
            // continuing", on the build where scaling the AMPLITUDE dragged the aim back to the middle).
            var held = p.DebugScopeSway;
            T.Check($"the sight did NOT walk back to centre ({atEngage.Length():0.####} -> {held.Length():0.####} deg)",
                    held.Length() > atEngage.Length() * 0.55f);

            // TRAVEL, as a RATIO against the same measurement unsteadied. An absolute threshold here was
            // calibrated against the REAL oscillator (0.75 rad/s) and applied to the SYNTHETIC one (3.33
            // rad/s, 4.4x faster) -- it failed at 0.084 deg/s and the code was correct. A ratio cancels the
            // carrier out, which is the only reason this number means anything on either oscillator.
            float steadiedTravel = 0f;
            var prev = p.DebugScopeSway;
            for (int i = 0; i < 50; i++)
            {
                yield return Ticks(1);
                steadiedTravel += (p.DebugScopeSway - prev).Length();
                prev = p.DebugScopeSway;
            }

            // The eased rate has landed by now, and this one IS exact -- it is the value the oscillator
            // multiplies its clock by, read after it has settled.
            T.Check($"the steadied rate reached the viewmodel ({p.VM.SteadyRateScale:0.###})",
                    Mathf.Abs(p.VM.SteadyRateScale - SDG.Unturned.ScopeSteadySim.SteadyRateScale) < 0.02f);

            // ---- 2. IT COSTS AIR, READ WHILE THE BREATH IS STILL HELD. The first cut of this read the bar
            // after the release and the baseline measurement -- 2.2 s at the 0.25/s refill, which restores
            // anything the drain had taken and reports "1 -> 1" for a drain that worked perfectly.
            float oxHeld = p.Oxygen;
            T.Check($"steadying spent oxygen ({oxBefore:0.###} -> {oxHeld:0.###})", oxHeld < oxBefore - 0.15f);
            T.Check($"and never past the reserve ({oxHeld:0.###})",
                    oxHeld >= SDG.Unturned.ScopeSteadySim.SteadyFloor - 0.01f);

            System.Environment.SetEnvironmentVariable("UG_STEADY", null);
            yield return Ticks(60);                        // let the release finish before measuring the baseline
            float freeTravel = 0f;
            prev = p.DebugScopeSway;
            for (int i = 0; i < 50; i++)
            {
                yield return Ticks(1);
                freeTravel += (p.DebugScopeSway - prev).Length();
                prev = p.DebugScopeSway;
            }
            float ratio = steadiedTravel / Mathf.Max(freeTravel, 1e-6f);
            T.Check($"...and it all but stopped travelling ({steadiedTravel:0.####} vs {freeTravel:0.####} deg/s, {ratio:0.##}x)",
                    ratio < 0.45f);
            T.Check($"...but is still creeping, not frozen solid ({ratio:0.###}x)", ratio > 0.02f);

            // ---- 2. IT COSTS AIR. The bug: the drain was computed and thrown away, so the bar never moved.
            // ---- 3. RELEASING RESTORES BOTH. The sway is covered by freeTravel above (measured AFTER the
            // release, and non-zero by the ratio bound); this is the bar, which the drain bug also froze.
            T.Check($"the bar refills once the breath is let go ({oxHeld:0.###} -> {p.Oxygen:0.###})",
                    p.Oxygen > oxHeld + 0.01f);
        }
    }
}
