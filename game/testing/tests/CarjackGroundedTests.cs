using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>A carjack lifts a car that is sitting there, refuses one that is on its way up, and STILL lifts
    /// one that is hopelessly stuck.
    ///
    /// master 2026-10-05: "prevent spamming jacks on cars in middair (require grounded, but not super strict as
    /// its meant to free stuck cars, who may be bugged lol)".
    ///
    /// ⭐⭐ THE LAST CLAUSE IS THE HARD REQUIREMENT. The obvious implementation -- "refuse unless the wheels are
    /// down" -- breaks the tool's purpose, because a car wedged in geometry is exactly the one whose ground
    /// contact is least trustworthy. So Vehicle.JackableNow asks "is it FLYING", not "is it grounded".
    ///
    /// ⚠ TWO THINGS THIS TEST FOUND, both of which a prose argument had got wrong:
    ///  1. a freshly jacked jeep is only ~1.1 m up (the impulse lifts it about 0.8 m), so a 3 m "near the
    ///     ground" reach called it jackable the whole way -- reach alone cannot express the rule;
    ///  2. a launched car at the TOP of its arc has almost no velocity, so "slow right now = settled" handed
    ///     back a re-jack window at every apex. The settle test had to become a DWELL.
    /// Hence the rule is: settled for a while, OR not rising and near the ground.
    ///
    /// The states are SET rather than waited for. Driving them off the jack's own impulse made the test a timing
    /// puzzle about a 0.8 m hop, and the thing worth pinning is the rule, not the hop. Gated inside
    /// Vehicle.Carjack rather than PlayerController so VehicleNetSync -- the SERVER's path -- obeys it too.</summary>
    public class CarjackGrounded : GameTest
    {
        public override string Name => "vehicle.carjack_grounded";
        public override double TimeoutSimSeconds => 180;

        static Vehicle Spawn(Node w, Vector3 at)
        {
            var v = Vehicle.BuildByName("jeep");
            if (v == null) return null;
            w.AddChild(v);
            v.GlobalPosition = at;
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            // ⭐⭐ THE CLIP HAS TO ACTUALLY LOAD, AND THIS IS THE ONLY THING THAT CAN TELL. If Jack_Use failed to
            // resolve, TryCarjack falls back to a constant of almost exactly the same length (1.967 s, against the
            // sound's 2.02 s), so a missing animation would look IDENTICAL in play -- same delay, same sound, the
            // arms just would not move. A fallback that good is a fallback that hides its own failure.
            var vm = new Viewmodel { ConsumableEquipClip = "Jack_Equip", ConsumableUseClip = "Jack_Use" };
            World.AddChild(vm);
            yield return Ticks(2);
            float jackUse = vm.ConsumeUseLength();
            GD.Print($"[carjack] Jack_Use resolves to {jackUse:0.000}s");
            T.Check($"Jack_Use loads out of consumable_anims.json ({jackUse:0.000}s, ripped length 1.967)",
                    jackUse > 1.9f && jackUse < 2.05f);

            // CONTROL: an unknown clip name must NOT land on that same number, or the check above would pass on
            // whatever the generic fallback happens to be rather than on the carjack's own animation.
            var bogus = new Viewmodel { ConsumableUseClip = "Jack_NotARealClip" };
            World.AddChild(bogus);
            yield return Ticks(2);
            float fb = bogus.ConsumeUseLength();
            GD.Print($"[carjack] control: unknown clip resolves to {fb:0.000}s");
            T.Check($"control: an unknown clip name resolves to something ELSE ({fb:0.000}s), so 1.967 came from Jack_Use",
                    Mathf.Abs(fb - jackUse) > 0.05f);
            vm.QueueFree(); bogus.QueueFree();

            Rigs.Ground(World);
            var v = Spawn(World, new Vector3(0f, 1.5f, 0f));
            if (v == null) { T.Check("jeep built", false); yield break; }
            yield return Ticks(220);   // outlast the spawn grace AND bank the settle dwell

            // CONTROL FIRST: without this, every refusal below is indistinguishable from a carjack that never works.
            GD.Print($"[carjack] parked: y={v.GlobalPosition.Y:0.00} vel={v.LinearVelocity.Length():0.00}");
            T.Check("a parked car reads as jackable", v.JackableNow());
            T.Check("...and Vehicle.Carjack accepts it", v.Carjack(false));
            yield return Ticks(4);
            T.Check($"the launch actually moved it ({v.LinearVelocity.Length():0.00} m/s)", v.LinearVelocity.Length() > 1.5f);

            // ---- RISING: the anti-spam case. A car travelling upward has just been launched.
            var rising = Spawn(World, new Vector3(14f, 1.5f, 0f));
            if (rising == null) { T.Check("2nd jeep built", false); yield break; }
            yield return Ticks(220);
            T.Check("control: it is jackable before being launched", rising.JackableNow());
            rising.Wake();
            rising.LinearVelocity = new Vector3(0f, 8f, 0f);
            yield return Ticks(2);   // one tick for the dwell to notice it is moving again
            GD.Print($"[carjack] rising: y={rising.GlobalPosition.Y:0.00} vy={rising.LinearVelocity.Y:0.00}");
            T.Check("a car travelling UPWARD is not jackable", !rising.JackableNow());
            T.Check("...and Vehicle.Carjack refuses it", !rising.Carjack(false));

            // ---- APEX: moving slowly but well off the ground. This is the hole the dwell closed.
            var apex = Spawn(World, new Vector3(28f, 12f, 0f));
            if (apex == null) { T.Check("3rd jeep built", false); yield break; }
            // ⚠ PINNED EACH TICK, NOT Freeze = true. Setting Freeze on a Vehicle does not survive -- the car
            // manages its own (spawn grace / net-held), and the probe below caught it reading back False, so the
            // first version of this case was really just measuring a car falling out of the sky. Holding it in
            // place every tick is also a truer model of "wedged": something external is stopping it moving.
            Vector3 pin = new Vector3(28f, 12f, 0f);
            for (int i = 0; i < 2; i++) { apex.GlobalPosition = pin; apex.LinearVelocity = Vector3.Zero; apex.AngularVelocity = Vector3.Zero; yield return Ticks(1); }
            GD.Print($"[carjack] apex-like: y={apex.GlobalPosition.Y:0.00} vel={apex.LinearVelocity.Length():0.00} dwell={apex.JackSettledForTest:0.00}s");
            T.Check("a barely-moving car high off the ground is NOT jackable yet (apex, not settled)", !apex.JackableNow());

            // ...and the SAME car, once it has been still long enough, IS jackable. ⭐⭐ THE LENIENCY MASTER ASKED
            // FOR: 12 m up with nothing under it and no contact anywhere, which is the shape of a bugged car, and
            // freeing exactly that is what the jack is for. If anyone later "tightens" this into a contact check,
            // this is the assert that goes red.
            for (int i = 0; i < 60; i++) { apex.GlobalPosition = pin; apex.LinearVelocity = Vector3.Zero; apex.AngularVelocity = Vector3.Zero; yield return Ticks(1); }   // > JackSettledDwell
            GD.Print($"[carjack] stuck: y={apex.GlobalPosition.Y:0.00} dwell={apex.JackSettledForTest:0.00}s");
            T.Check("...but the same STUCK car is jackable once it has clearly settled (master: not super strict)",
                    apex.JackableNow());
            T.Check("...and Vehicle.Carjack accepts it, so the car can actually be freed", apex.Carjack(false));

            v.QueueFree(); rising.QueueFree(); apex.QueueFree();
        }
    }
}
