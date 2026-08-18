using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THE HIND'S GUN (strawberry 2026-08-18). Three claims worth separating, because they fail differently:
    //   1. the AIM MATHS points the barrel where it was told to -- this is a SIGN derivation, and three separate
    //      control-axis signs in NpcHeli.cs were derived confidently and were all inverted, so it gets measured
    //      rather than reasoned about;
    //   2. being SHOT is what starts a fight, and nothing else is;
    //   3. only an airframe with a mount fights at all.
    public sealed class NpcHeliTurretTests : GameTest
    {
        public override string Name => "vehicle.npc_heli_turret";
        public override double TimeoutSimSeconds => 120;

        // `held` freezes the airframe AND stops its script processing, which is what the aim probes need -- a
        // drifting attitude would smear the sign measurement. It cannot be used for the FIRING test: the turret's
        // cooldown is decremented in the vehicle's own _PhysicsProcess, so a held aircraft fires exactly one round
        // and then starves forever. That is a property of the rig, not of the gun, and it cost a confusing zero.
        (Vehicle v, NpcHeli ai) Spawn(string name, Vector3 at, bool held = true)
        {
            var v = Vehicle.BuildByName(name);
            World.AddChild(v);
            v.GlobalPosition = at;
            v.EngineOn = true; v.DebugInstantStart = true; v.SpawnRotorRunning();
            v.DebugNoTurbulence = true;
            if (held)
            {
                v.Freeze = true; v.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
                v.ProcessMode = Node.ProcessModeEnum.Disabled;   // hold the attitude still; the aim maths is the subject
            }
            var ai = new NpcHeli { Heli = v, Terr = null, Target = Vector3.Zero, TargetName = "T" };
            World.AddChild(ai);
            return (v, ai);
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var (hind, ai) = Spawn("hind", new Vector3(0f, 40f, 0f));
            yield return Ticks(3);

            T.Check($"the Hind is armed and the fleet's other airframes are not (turrets {hind.Turrets.Length})",
                ai.Armed && hind.Turrets.Length == 1);
            T.Check($"...and it uses the retail HMG, not the Nykorev (gun '{hind.Turrets[0].GunId}')",
                hind.Turrets[0].GunId == "hmg");

            // ---- 1. AIM SIGNS, MEASURED. Point the mount at a series of known world points and read back where
            // the BARREL actually ends up. A derivation that is inverted in yaw or pitch still produces plausible
            // magnitudes, which is exactly how the earlier sign bugs survived review.
            int seat = hind.Turrets[0].Seat;
            var probes = new (string label, Vector3 at)[]
            {
                ("dead ahead and below", new Vector3(0f, 20f, -60f)),
                ("ahead-left and below",  new Vector3(-40f, 20f, -50f)),
                ("ahead-right and below", new Vector3(40f, 20f, -50f)),
            };
            float worstDeg = 0f;
            foreach (var (label, at) in probes)
            {
                ai.DebugAimAt(at);
                yield return Ticks(2);
                Vector3 muzzle = hind.TurretMuzzle(seat) ?? hind.GlobalPosition;
                Vector3 barrel = hind.TurretBarrelDir(seat);
                float errDeg = Mathf.RadToDeg(barrel.AngleTo((at - muzzle).Normalized()));
                worstDeg = Mathf.Max(worstDeg, errDeg);
                GD.Print($"[TURRET] {label,-24} aim err {errDeg:0.0} deg");
            }
            // THREE BEARINGS, NOT ONE, AND THAT IS MEASURED RATHER THAN CAUTIOUS. Teeth-checked by inverting the
            // yaw sign: the off-axis probes blow out to 77.7 deg and fail, while "dead ahead" stays at 1.3 deg --
            // because on the centreline a yaw sign is unobservable. A single forward probe would have certified
            // an inverted turret as correct, which is the same shape as the other blind checks found today.
            //
            // Generous bound, because the mount CLAMPS (yaw +-120, pitch -60..+15) and the residual is the muzzle
            // sitting forward of the pivot -- it is here to catch an inverted axis, which lands 90-180 deg out,
            // not to certify arcsecond accuracy.
            T.Check($"the barrel points where the aim maths said it would (worst error {worstDeg:0.0} deg over 3 bearings)",
                worstDeg < 12f);

            // ---- 2. BEING SHOT IS THE TRIGGER, and only that.
            T.Check($"it is NOT hunting anybody before it is touched (mode {ai.Mode})", ai.Mode == NpcHeli.Stance.Patrol);
            hind.NoteAttackedFrom(new Vector3(0f, 12f, -90f));
            yield return Ticks(3);
            T.Check($"a hit puts it on the warpath, aimed at where the shot came from (mode {ai.Mode}, watching {ai.LastSeen})",
                ai.Mode == NpcHeli.Stance.Engaged && ai.LastSeen.IsEqualApprox(new Vector3(0f, 12f, -90f)));
            T.Check($"...and the grudge is the full five minutes ({ai.DebugLockLeftSec:0} s left of {NpcHeli.LockSeconds:0})",
                ai.DebugLockLeftSec > NpcHeli.LockSeconds - 5.0);

            // ---- 2b. DOES IT ACTUALLY SHOOT? Everything above proves it AIMS and that it decides to fight; none
            // of it proves a round ever leaves the gun. That gap is how a feature ends up wired, green and inert.
            // So: put a real player in front of it, in the mount's traverse, and count the rounds.
            var (gun, gunAi) = Spawn("hind", new Vector3(0f, 45f, 0f), held: false);   // must PROCESS, see Spawn
            var victim = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(victim);
            victim.GlobalPosition = new Vector3(0f, 2f, -55f);   // ahead and BELOW: a chin turret's actual arc
            yield return Ticks(2);

            // COUNT AFTER THE PLAYER EXISTS. PlayerController._Ready ASSIGNS NpcHeli.NpcShot to its own bullet
            // spawner, so a counter installed first is silently replaced and reads zero while the gun is firing
            // perfectly well. That is what the first run of this check actually measured.
            int rounds = 0;
            var prevHook = NpcHeli.NpcShot;
            NpcHeli.NpcShot = (o, d, g) => rounds++;

            gun.NoteAttackedFrom(victim.GlobalPosition);
            for (int i = 0; i < 260; i++) yield return Ticks(1);   // ~5 s: slew onto target, then bursts
            NpcHeli.NpcShot = prevHook;

            GD.Print($"[TURRET] rounds fired in ~5 s: {rounds}");
            T.Check($"it actually opens fire on a player it can see ({rounds} rounds in ~5 s)", rounds > 0);
            // BOUNDED ABOVE TOO. The belt cycles at 0.12 s, so an unbroken stream would be ~42 rounds in 5 s.
            // Bursts of 7 with a 1.5 s gap is roughly 21. A check with no ceiling would pass just as happily on
            // a gun that never stops, which is the thing "shoot bursts" exists to rule out.
            T.Check($"...in BURSTS rather than one continuous stream ({rounds} rounds; unbroken fire would be ~42)",
                rounds < 32);
            victim.QueueFree(); gun.QueueFree(); gunAi.QueueFree();
            yield return Ticks(2);

            // ---- 3. NO MOUNT, NO FIGHT. The rule is "only the Hind", but expressed as data (does it carry a
            // turret) rather than a name check, so a future gunship inherits it and a future transport does not.
            var (huey, hai) = Spawn("huey", new Vector3(300f, 40f, 0f));
            yield return Ticks(3);
            huey.NoteAttackedFrom(new Vector3(300f, 12f, -90f));
            yield return Ticks(5);
            T.Check($"shooting a Huey does NOT start a gunfight (armed={hai.Armed}, mode {hai.Mode})",
                !hai.Armed && hai.Mode == NpcHeli.Stance.Patrol);
        }
    }
}
