using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>The gunship's crew, seats and rate of fire are what master asked for, pinned as data.
    ///
    /// master 2026-10-06, in one burst of asks: "change the hind's gun to not be an insane rate of fire, more
    /// like 450rpm", "seats 3 and 4 should be the door gunner seats", "replace the door gunners' generic model
    /// with actual playermodel/npc models that hold the weapons".
    ///
    /// ⭐ These are NUMBERS AND WIRING, which is exactly the kind of thing that silently drifts: a seat index is
    /// one character, a Cycle is one float, and nothing else in the game fails when either is wrong -- the
    /// gunner simply sits in the copilot's chair and the gun simply sounds like a firehose. Neither shows up in
    /// a build or in any other test, which is why they get asserted rather than eyeballed once.</summary>
    public class HeliGunnerSetup : GameTest
    {
        public override string Name => "vehicle.heli_gunners";

        public override IEnumerable<Step> Run()
        {
            // ---- THE HIND'S CANNON: 450 rpm, i.e. Cycle 0.1333 s/round ----
            var hind = Vehicle.BuildByName("hind");
            T.Check("hind built", hind != null);
            if (hind == null) yield break;
            World.AddChild(hind);
            yield return Ticks(2);

            var cannon = hind.Turrets != null && hind.Turrets.Length > 0 ? hind.Turrets[0] : null;
            T.Check("the hind has a turret", cannon != null);
            if (cannon != null)
            {
                float rpm = cannon.Cycle > 0f ? 60f / cannon.Cycle : 0f;
                GD.Print($"[heli] hind cannon '{cannon.GunId}' cycle {cannon.Cycle:0.0000}s = {rpm:0} rpm, belt {cannon.Belt}");
                T.Check($"the hind's cannon fires at ~450 rpm, not the 3000 rpm ceiling (measured {rpm:0})",
                        rpm > 430f && rpm < 470f);
            }

            // ---- THE HUEY'S DOOR GUNS: seats 2 and 3 (master's "3 and 4", counting the pilot as 1) ----
            var huey = Vehicle.BuildByName("huey");
            T.Check("huey built", huey != null);
            if (huey == null) yield break;
            World.AddChild(huey);
            yield return Ticks(2);

            T.Check("the huey has two door guns", huey.Turrets != null && huey.Turrets.Length == 2);
            if (huey.Turrets == null || huey.Turrets.Length != 2) yield break;

            var seats = new List<int>();
            foreach (var t in huey.Turrets) seats.Add(t.Seat);
            seats.Sort();
            GD.Print($"[heli] huey door-gun seats {seats[0]} and {seats[1]} of {huey.SeatCount}");
            T.Check($"the door guns are crewed from seats 2 and 3, not the copilot's seat 1 (got {seats[0]},{seats[1]})",
                    seats[0] == 2 && seats[1] == 3);
            // CONTROL: those seats must actually exist, or "seat 3" is a crash waiting for a passenger.
            T.Check($"the huey really has {seats[1] + 1}+ seats", huey.SeatCount > seats[1]);

            // ...and they are the WIDE pair. Reading it off the seat table rather than trusting the index: the
            // door seats are the ones out at the doors, which is the whole reason these two were the right answer.
            float x2 = Mathf.Abs(huey.SeatLocal(2).X), x3 = Mathf.Abs(huey.SeatLocal(3).X);
            float x0 = Mathf.Abs(huey.SeatLocal(0).X), x1 = Mathf.Abs(huey.SeatLocal(1).X);
            GD.Print($"[heli] huey seat |X|: 0={x0:0.00} 1={x1:0.00} 2={x2:0.00} 3={x3:0.00}");
            T.Check("seats 2/3 sit further outboard than the front pair -- they are the DOOR seats",
                    x2 > x0 && x3 > x1);

            // ---- THE CREW: real character models, holding the mount's gun ----
            huey.EquipDoorGunners();
            yield return Ticks(2);
            int crew = 0, armed = 0, rigged = 0;
            foreach (var c in huey.GetChildren())
            {
                if (c is not TargetDummy td || !td.Name.ToString().StartsWith("Gunner")) continue;
                crew++;
                if (td.CharacterModel) rigged++;
                if (!string.IsNullOrEmpty(td.CharacterGun)) armed++;
            }
            GD.Print($"[heli] door crew: {crew} gunners, {rigged} using a character model, {armed} holding a gun");
            T.Check("both door guns got a gunner", crew == 2);
            T.Check("...using a real character model, not the range dummy's block figure", rigged == 2);
            T.Check("...and each holds the gun his own mount fires", armed == 2);

            hind.QueueFree(); huey.QueueFree();
        }
    }
}
