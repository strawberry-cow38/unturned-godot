using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // A wrecked car has to go DARK and QUIET. Explode() always killed the head/tail lights directly, so the
    // bug was invisible at the moment of explosion -- what brought them back was the "alarmed" car's blip
    // loop, which re-lit the lamps and honked every 0.5s on a burning hulk. The loop had no _exploded guard.
    //
    // Worse than cosmetic: once the wreck settles it becomes a _husk and the per-frame sim early-returns
    // permanently, so whatever the blip left behind is frozen there for good. Land in the lit half of the
    // cycle and the headlights stay on for the rest of the session, on a charred corpse.
    //
    // The test therefore has to keep ticking well PAST the explosion. Asserting "lights off" on the
    // explosion frame passes against the bug, because Explode() genuinely turns them off -- it is the next
    // blip that undoes it. That is exactly the trap this test exists to avoid re-introducing.
    public sealed class VehicleWreckGoesDarkTests : GameTest
    {
        public override string Name => "vehicle.wreck_goes_dark_and_stays_dark";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var car = Vehicle.BuildByName("jeep");
            World.AddChild(car);
            car.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            car.AlarmedForTest = true;   // spawn rolls this at 5%; force it so the test is deterministic
            yield return Ticks(5);

            // Damage to 0 HP: TakeDamage trips TriggerAlarm, then starts the 4s fuse to Explode().
            car.TakeDamage(car.HealthMax * 2f);
            yield return Ticks(10);

            T.Check("damaging the alarmed car set its alarm going", car.AlarmActiveForTest);
            bool sawLit = false;
            for (int i = 0; i < 60 && !sawLit; i++)   // the blip is a ~1s cycle; catch it in its lit half
            {
                if (car.HeadlightsOn) sawLit = true;
                yield return Ticks(1);
            }
            T.Check("the alarm actually drives the lamps before the wreck (pre-condition)", sawLit);

            // Ride out the 4s explosion fuse.
            yield return Until(() => car.Exploded, 12);
            T.Check("the car exploded", car.Exploded);

            // THE REGRESSION: keep ticking past several blip cycles. Pre-fix the alarm loop is still live and
            // re-lights the lamps within ~0.5s, so a single sample right after Explode() would miss it.
            bool relit = false;
            float worstTick = -1f;
            for (int i = 0; i < 300; i++)
            {
                if (car.HeadlightsOn || car.TaillightsOn) { relit = true; worstTick = i; break; }
                yield return Ticks(1);
            }
            T.Check($"a wreck never re-lights its lamps ({(relit ? $"lit again {worstTick} ticks after exploding" : "stayed dark for 300 ticks")})", !relit);
            T.Check("the alarm is dead on a wreck, not merely quiet", !car.AlarmActiveForTest);
            T.Check("the wreck is not still flagged as an alarmed car", !car.AlarmedForTest);
            T.Check("the siren is off on a wreck", !car.SirenOn);

            // Damage landing on a corpse must not re-arm anything either -- wrecks stay damageable.
            car.AlarmedForTest = true;
            car.TakeDamage(50f);
            yield return Ticks(40);
            T.Check("damaging a corpse cannot start a new alarm", !car.AlarmActiveForTest);
            T.Check("and cannot re-light it", !car.HeadlightsOn && !car.TaillightsOn);
        }
    }
}
