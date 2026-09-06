using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE CHAINSAW IS A REPEATED TOOL THAT DAMAGES (strawberry 2026-09-04: "wire up the chainsaw melee weapon,
    // shakes while on (while held) and shakes more violently when attacking. deals damage like any other melee,
    // but at the same speed as the blowtorch").
    //
    // The data already drew the line and nothing read it. MeleeDef.Repair is documented as "the continuous action
    // REPAIRS the target (blowtorch) rather than damaging it" -- so a Repeated tool WITHOUT Repair is one that
    // damages, and the chainsaw is the only one. Its .dat has carried full melee damage all along; holding LMB
    // simply did nothing, because the Repeated branch only ever asked whether you were holding a blowtorch.
    //
    // Hits are counted through the real MP seam (NetMelee) rather than a bespoke counter: that is the branch a
    // networked game takes, and using it means the test cannot pass against a code path players never run.
    public sealed class ChainsawTests : GameTest
    {
        public override string Name => "melee.chainsaw";

        public override IEnumerable<Step> Run()
        {
            var p = new PlayerController { CaptureMouse = false };
            World.AddChild(p);
            yield return Ticks(2);

            // ---- the data distinction this whole feature rests on ----
            p.EquipHeldMelee("blowtorch");
            yield return Ticks(1);
            T.Check("the blowtorch is a Repeated tool", p.IsRepeatedMelee);
            T.Check("...that REPAIRS, so it is not a damaging one", p.HasBlowtorch && !p.IsRepeatedDamage);

            p.EquipHeldMelee("chainsaw");
            yield return Ticks(1);
            T.Check("the chainsaw is a Repeated tool", p.IsRepeatedMelee);
            T.Check("...with no Repair flag, so it DAMAGES", !p.HasBlowtorch && p.IsRepeatedDamage);

            // A Repeated tool has no swing (source ItemMeleeAsset: no strong attacks), so the ordinary swing path
            // must stay shut -- the saw's damage comes from the repeat, and if both fired it would double up.
            int swings = 0;
            p.NetMelee = (strong, yaw) => { swings++; };
            p.MeleeAttack(false);
            p.MeleeAttack(true);
            yield return Ticks(1);
            T.Check($"the swing path stays closed for a Repeated tool ({swings} swings)", swings == 0);

            // ---- SHAKE: while held, harder while cutting ----
            p.DebugTickChainsaw(0.016f, false);
            var idle = p.DebugSawShake;
            T.Check($"a held chainsaw shakes with the trigger RELEASED ({idle})", idle.Length() > 0f);

            p.DebugTickChainsaw(0.016f, true);
            var cutting = p.DebugSawShake;
            T.Check($"cutting shakes harder than idling ({cutting.Length():0.0000} > {idle.Length():0.0000})",
                    cutting.Length() > idle.Length());

            // A weapon that is not a saw must not shake at all -- otherwise "it shakes" is a property of the tick,
            // not of the chainsaw, and the two assertions above would pass holding an axe.
            p.EquipHeldMelee("axe");
            yield return Ticks(1);
            p.DebugTickChainsaw(0.016f, true);
            T.Check($"an axe does not shake ({p.DebugSawShake})", p.DebugSawShake.Length() == 0f);

            // ---- DAMAGE on a repeat, not per frame ----
            p.EquipHeldMelee("chainsaw");
            yield return Ticks(1);
            int hits = 0; bool anyStrong = false;
            p.NetMelee = (strong, yaw) => { hits++; anyStrong |= strong; };

            p.DebugTickChainsaw(0.016f, true);
            T.Check($"the first pull cuts immediately ({hits})", hits == 1);

            // DERIVED FROM THE INTERVAL, not from a frame count that happens to sit inside it. These were
            // hardcoded 16 and 14 frames chosen against a 0.45 s cadence; strawberry halved it to 0.225 s
            // (2026-09-06) and both numbers silently became assertions about a rule that no longer exists --
            // the first over-ran the interval and the saw legitimately cut twice. Deriving the counts means the
            // next retune cannot rot them.
            const float Dt = 0.016f;
            float interval = PlayerController.RepeatedHitIntervalForTest;
            int justUnder = Mathf.Max(1, Mathf.FloorToInt(interval / Dt) - 2);   // stay strictly inside the window
            for (int i = 0; i < justUnder; i++) p.DebugTickChainsaw(Dt, true);
            T.Check($"it does NOT hit every frame ({hits} after {justUnder + 1} frames inside a {interval:0.###}s interval)", hits == 1);

            // Past the interval it cuts again. Four frames clear of the boundary either way.
            for (int i = 0; i < 4; i++) p.DebugTickChainsaw(Dt, true);
            T.Check($"it cuts again past the interval ({hits})", hits == 2);

            T.Check("every cut is a WEAK hit -- a Repeated tool has no strong attack", !anyStrong);

            // Releasing must clear the cooldown, or a tap-tap-tap player is silently slower than a holder.
            p.DebugTickChainsaw(0.016f, false);
            int before = hits;
            p.DebugTickChainsaw(0.016f, true);
            T.Check($"releasing resets the cooldown, so the next pull cuts at once ({before} -> {hits})", hits == before + 1);

            p.QueueFree();
        }
    }
}
