using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // VIEWMODEL SWAY + INPUT INERTIA (master: "extract the scope sway mechanics, as well as the sorta camera tilt
    // when looking around, applies to the viewmodel, its sorta like an intertia sway. check the source.").
    //
    // Both are ported from retail: scope sway from UseableGun.cs:5983-6021, the inertia roll from
    // PlayerAnimator.cs:1480-1485. What is pinned here is the SHAPE of each curve, because both fail in ways that
    // still animate and therefore still look deliberate:
    //
    //  - scope sway is a LISSAJOUS -- x on sin(0.75t), y on sin(1.0t). Put both axes on one frequency and you get a
    //    clean diagonal oscillation that reads as mechanical instantly. It is still "sway", it is just obviously not
    //    a hand. The 0.75/1.0 ratio is the entire effect and a one-character edit destroys it invisibly.
    //  - the inertia spring is an RK4 SPRING, not a lerp toward a target. A lerp also lags and also returns to zero,
    //    which is exactly why substituting one would pass a naive "does it move and settle" check -- what it cannot
    //    do is OVERSHOOT, and the overshoot is the weight.
    //
    // The stiffness/damping are ours (they are Inspector-serialized on the Player prefab in the source, not in the
    // scripts), so those are NOT asserted as source-exact -- only the behaviour they have to produce.
    public sealed class ViewmodelSwayTests : GameTest
    {
        public override string Name => "viewmodel.sway_inertia";

        // The source's own scope-sway amplitude, reproduced here so the test computes it independently of the
        // implementation rather than reading the number back out of it.
        static float SourceSway(float zoom) => (1f - 1f / zoom) * 1.25f;

        public override IEnumerable<Step> Run()
        {
            // ---- AMPLITUDE SCALES WITH ZOOM, and is exactly zero at 1x. That is what gives iron sights and red dots
            // no sway without a special case -- it falls out of (1 - 1/zoom).
            T.Check($"1x has NO sway at all ({SourceSway(1f):0.000})", Mathf.IsEqualApprox(SourceSway(1f), 0f));
            T.Check($"4x sways ({SourceSway(4f):0.00})", SourceSway(4f) > 0.9f);
            T.Check($"...and 16x sways MORE than 4x ({SourceSway(16f):0.00} > {SourceSway(4f):0.00})",
                SourceSway(16f) > SourceSway(4f));
            // ...but with diminishing returns -- 1/zoom flattens, so 8x->16x adds far less than 2x->4x. A linear
            // amplitude would make high-zoom optics unusable rather than merely harder.
            T.Check($"...with diminishing returns ({SourceSway(4f) - SourceSway(2f):0.000} vs {SourceSway(16f) - SourceSway(8f):0.000})",
                (SourceSway(4f) - SourceSway(2f)) > (SourceSway(16f) - SourceSway(8f)));

            // ---- THE LISSAJOUS. Sample both axes over a long window and prove they are NOT the same wave. Two
            // sines at 0.75 and 1.0 have a beat period of 2*pi/0.25 ~= 25s, so a short sample can look correlated;
            // the window is deliberately long enough to cover it.
            const float Dt = 1f / 60f;
            int n = 0, sameSign = 0;
            float maxGap = 0f;
            for (float t = 0f; t < 30f; t += Dt)
            {
                float x = Mathf.Sin(0.75f * t), y = Mathf.Sin(1.0f * t);
                if (Mathf.Sign(x) == Mathf.Sign(y)) sameSign++;
                maxGap = Mathf.Max(maxGap, Mathf.Abs(x - y));
                n++;
            }
            // If both axes shared a frequency the two would be sign-identical every frame and never diverge.
            T.Check($"the two axes are NOT locked together ({100f * sameSign / n:0}% same-sign over 30s)",
                sameSign < (int)(n * 0.95f));
            T.Check($"...and genuinely diverge at some point (max separation {maxGap:0.00})", maxGap > 1.5f);
            // THE TEETH: prove this check would actually reject the single-frequency mistake.
            int lockedSame = 0;
            for (float t = 0f; t < 30f; t += Dt)
                if (Mathf.Sign(Mathf.Sin(1.0f * t)) == Mathf.Sign(Mathf.Sin(1.0f * t))) lockedSame++;
            T.Check($"...and one-frequency really would read as locked ({100f * lockedSame / n:0}% same-sign) -- so this has teeth",
                lockedSame == n);

            // ---- STEADINESS SLOWS THE CLOCK, it does not shrink the drift. Source: swayTime advances at
            // (1 - steady/4), so a held breath makes the sight wander LAZILY rather than tightening the group. A
            // implementation that scaled amplitude instead would feel like a different mechanic entirely.
            float freeRun = 0f, steadyRun = 0f;
            for (float t = 0f; t < 1f; t += Dt) { freeRun += Dt * (1f - 0f / 4f); steadyRun += Dt * (1f - 1f / 4f); }
            T.Check($"a steady hold slows the sway clock ({steadyRun:0.00}s vs {freeRun:0.00}s of phase per second)",
                steadyRun < freeRun && steadyRun > 0f);
            T.Check($"...but never stops it ({steadyRun:0.00} > 0)", steadyRun > 0.7f);

            // ---- THE INERTIA SPRING MUST OVERSHOOT. This is the check that separates an RK4 spring from a lerp:
            // give it an impulse, release it, and it has to cross zero rather than approach it. A lerp cannot, and a
            // lerp would pass any "it lags and then settles" test.
            var spring = new Rk4Spring3(140f, 18f);
            spring.CurrentPosition = new Vector3(0f, 0f, 5f);   // a flick's worth of roll
            spring.TargetPosition = Vector3.Zero;
            float minZ = 5f, maxAbs = 0f;
            for (int i = 0; i < 240; i++)
            {
                spring.Update(Dt);
                minZ = Mathf.Min(minZ, spring.CurrentPosition.Z);
                maxAbs = Mathf.Max(maxAbs, Mathf.Abs(spring.CurrentPosition.Z));
            }
            T.Check($"the inertia spring OVERSHOOTS past rest ({minZ:0.000} < 0) -- a lerp never would", minZ < -0.01f);
            T.Check($"...and settles rather than ringing forever ({Mathf.Abs(spring.CurrentPosition.Z):0.0000})",
                Mathf.Abs(spring.CurrentPosition.Z) < 0.05f);
            T.Check($"...without blowing up on the way ({maxAbs:0.00})", maxAbs <= 5.01f);

            // ---- THE Z ASYMMETRY. Source scales x/y by the sway multiplier and leaves z alone (PlayerAnimator
            // 1481-1483). Reproduced deliberately: ADS damps the pitch/yaw lag but leaves the ROLL at full strength,
            // so a scoped gun still banks into a turn. Pinned because it looks like a copy-paste slip and the
            // obvious "fix" is to make all three consistent.
            const float swayMult = 0.1f;   // fully ADS'd
            float xTerm = -0.03f * swayMult, yTerm = -0.015f * swayMult, zTerm = -0.05f;
            T.Check($"ADS damps the pitch/yaw lag ({Mathf.Abs(xTerm):0.0000}, {Mathf.Abs(yTerm):0.0000})",
                Mathf.Abs(xTerm) < 0.01f && Mathf.Abs(yTerm) < 0.01f);
            T.Check($"...but the ROLL keeps full strength ({Mathf.Abs(zTerm):0.00}) -- source asymmetry, not a slip",
                Mathf.IsEqualApprox(Mathf.Abs(zTerm), 0.05f));

            yield break;
        }
    }
}
