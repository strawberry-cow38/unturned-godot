using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE GUN PLAYGROUND (strawberry 2026-08-15): player-shaped dummies that take a gun's damage through the
    // humanoid zone table, so a cartridge change can be felt somewhere.
    //
    // The thing worth testing is not "a dummy loses health" -- it is that the dummy and the SERVER resolve the
    // same shot the same way. The zone multipliers exist TWICE (Humanoid in game/, ServerGunProfile in core/,
    // because core cannot reference game), and a silent drift between them would mean the playground reports one
    // number while PvP deals another. That is exactly the class of bug the playground was built to rule out, so
    // it gets an explicit mirror check rather than trust.
    public sealed class PlaygroundDummyTests : GameTest
    {
        public override string Name => "gun.playground_dummy";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            // ---- 1. THE MIRROR. Two copies of one table; assert they agree.
            var prof = new UnturnedGodot.Net.ServerGunProfile();
            T.Check($"head multiplier matches the server's ({Humanoid.HeadMult} vs {prof.HeadMult})",
                Mathf.IsEqualApprox(Humanoid.HeadMult, prof.HeadMult));
            T.Check($"torso matches ({Humanoid.TorsoMult} vs {prof.TorsoMult})",
                Mathf.IsEqualApprox(Humanoid.TorsoMult, prof.TorsoMult));
            T.Check($"legs match ({Humanoid.LegMult} vs {prof.LegMult})",
                Mathf.IsEqualApprox(Humanoid.LegMult, prof.LegMult));

            // ---- 2. ZONES. Each band takes its own multiplier off ONE base number.
            Rigs.Ground(World);
            var d = new TargetDummy { MaxHealth = 1000f, RespawnSeconds = 1f };   // big pool so the zone shots don't drop it
            World.AddChild(d);
            d.GlobalPosition = new Vector3(-30f, 0f, -5f);   // OFF the firing lane: section 5 shoots down -Z from the origin
            yield return Ticks(5);

            float head = d.TakeHit(20f, d.GlobalPosition + new Vector3(0f, 1.60f, 0f));
            T.Check($"a head hit takes the head multiplier ({head} = 20 x {Humanoid.HeadMult})",
                Mathf.IsEqualApprox(head, 20f * Humanoid.HeadMult) && d.LastZone == TargetDummy.HitZone.Head);
            float torso = d.TakeHit(20f, d.GlobalPosition + new Vector3(0f, 1.10f, 0f));
            T.Check($"a torso hit takes the torso multiplier ({torso} = 20 x {Humanoid.TorsoMult})",
                Mathf.IsEqualApprox(torso, 20f * Humanoid.TorsoMult) && d.LastZone == TargetDummy.HitZone.Torso);
            float leg = d.TakeHit(20f, d.GlobalPosition + new Vector3(0f, 0.40f, 0f));
            T.Check($"a leg hit takes the leg multiplier ({leg} = 20 x {Humanoid.LegMult})",
                Mathf.IsEqualApprox(leg, 20f * Humanoid.LegMult) && d.LastZone == TargetDummy.HitZone.Legs);
            // ...and they are actually DIFFERENT. Three equal multipliers would satisfy every check above.
            T.Check($"the three zones differ ({head}/{torso}/{leg})", head > torso && torso > leg);

            // ---- 3. THE BAND EDGE. 1.45 is the head cut: just under must NOT be a headshot.
            d.TakeHit(1f, d.GlobalPosition + new Vector3(0f, Humanoid.HeadMinY - 0.02f, 0f));
            T.Check("just below the head band is a torso hit", d.LastZone == TargetDummy.HitZone.Torso);
            d.TakeHit(1f, d.GlobalPosition + new Vector3(0f, Humanoid.HeadMinY + 0.02f, 0f));
            T.Check("just above it is a headshot", d.LastZone == TargetDummy.HitZone.Head);

            // ---- 4. DOWN + RESPAWN on a timer.
            var d2 = new TargetDummy { MaxHealth = 100f, RespawnSeconds = 0.5f };
            World.AddChild(d2);
            d2.GlobalPosition = new Vector3(-24f, 0f, -5f);   // likewise clear of the lane
            yield return Ticks(5);
            d2.TakeHit(200f, d2.GlobalPosition + new Vector3(0f, 1.10f, 0f));
            T.Check($"an overkill hit drops it (health {d2.Health}, downed {d2.TimesDowned})", d2.Down && d2.TimesDowned == 1);
            yield return Ticks(10);
            T.Check("a downed dummy stays down before its timer", d2.Down);
            yield return Ticks(30);   // 0.5 s at 50 Hz = 25 ticks
            T.Check($"...and comes back at full health ({d2.Health})", !d2.Down && Mathf.IsEqualApprox(d2.Health, 100f));

            // ---- 5. THE LIVE FIRE PATH. The checks above call TakeHit directly, which would pass even if no
            // bullet could ever reach a dummy -- the branch in StepBullets is the part that was actually missing.
            var p = new PlayerController { CaptureMouse = false, Inventory = new SDG.Unturned.PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(40);
            p.EquipHeldGun("eaglefire");
            p.Ammo = 30;
            yield return Until(() => p.HeldItemReady, 6);

            var d3 = new TargetDummy { MaxHealth = 1000f, RespawnSeconds = 60f };
            World.AddChild(d3);
            d3.GlobalPosition = new Vector3(0f, 0f, -12f);   // ON the floor -- a floating target was the first cut's bug
            yield return Ticks(10);
            float hp0 = d3.Health;
            // MEASURE the eye, then aim DOWN at the torso rather than assuming a level shot lands on it. The
            // player is 12 m away with its eye ~1.75 m up and the torso band centre is ~1.1 m, so a level shot
            // sails over the shoulder -- which is exactly how the first version of this failed (1000 -> 1000).
            float eye = p.EyesWorld.Y;
            float torsoY = (Humanoid.TorsoMinY + Humanoid.HeadMinY) * 0.5f;   // middle of the torso band
            float dz = Mathf.Abs(d3.GlobalPosition.Z - p.GlobalPosition.Z);
            float pitch = Mathf.RadToDeg(Mathf.Atan2(torsoY - eye, dz));
            T.Check($"the shot is aimed into the torso band (eye {eye:0.##} m, torso {torsoY:0.##} m at {dz:0.#} m -> pitch {pitch:0.##} deg)",
                pitch < 0f && pitch > -20f);
            p.DebugSetPitch(pitch);
            yield return Ticks(5);
            T.Check("the rifle fired at the dummy", p.Fire());
            for (int i = 0; i < 60 && Mathf.IsEqualApprox(d3.Health, hp0); i++) yield return Ticks(1);
            // ...and it must be THIS dummy. The first cut of this test parked the zone-check dummy at (0,0,-5),
            // dead on the firing lane 7 m short of the target, so the round struck that one instead and d3 read
            // 1000 -> 1000. "no damage" and "damaged the wrong target" look identical from d3's side.
            T.Check($"the near dummies are clear of the lane (x {d.GlobalPosition.X:0} / {d2.GlobalPosition.X:0})",
                Mathf.Abs(d.GlobalPosition.X) > 2f && Mathf.Abs(d2.GlobalPosition.X) > 2f);
            T.Check($"a real bullet damaged it ({hp0} -> {d3.Health}, zone {d3.LastZone})", d3.Health < hp0);
            // The eaglefire is 5.56 = 20 damage; a torso hit is x1. If the bullet ever carried the ZOMBIE number
            // (the field the SP path used before the merge) this would read 99, so the value IS the check.
            T.Check($"...for the cartridge's damage, not the old zombie number ({d3.LastDamage})",
                Mathf.IsEqualApprox(d3.LastDamage, 20f * Humanoid.TorsoMult));

            p.QueueFree();
            yield break;
        }
    }
}
