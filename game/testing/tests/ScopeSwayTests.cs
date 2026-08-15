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
}
