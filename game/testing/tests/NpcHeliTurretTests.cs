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
            v.EquipDoorGunners();   // an NPC brings its crew; the console `vehicle` command does not
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
            var shotFrames = new List<ulong>();
            var prevHook = NpcHeli.NpcShot;
            NpcHeli.NpcShot = (o, d, g) => { rounds++; shotFrames.Add(Engine.GetPhysicsFrames()); };

            gun.NoteAttackedFrom(victim.GlobalPosition);
            for (int i = 0; i < 900; i++) yield return Ticks(1);   // ~18 s: long enough for several bursts
            NpcHeli.NpcShot = prevHook;

            // Rebuild the bursts from the shot timestamps: a gap of more than a few cycles ends one.
            var bursts = new List<int>();
            for (int i = 0; i < shotFrames.Count; i++)
            {
                if (i == 0 || shotFrames[i] - shotFrames[i - 1] > 20) bursts.Add(1);
                else bursts[^1]++;
            }

            GD.Print($"[TURRET] {rounds} rounds in ~18 s, bursts [{string.Join(",", bursts)}]");
            T.Check($"it actually opens fire on a player it can see ({rounds} rounds)", rounds > 0);
            // BOUNDED ABOVE TOO. The belt cycles at 0.12 s, so unbroken fire is ~150 rounds in 18 s. A check with
            // no ceiling would pass just as happily on a gun that never stops, which is the thing "bursts" rules
            // out. Generous, because burst length and gap are both randomised now and the total moves run to run.
            T.Check($"...in BURSTS rather than one continuous stream ({rounds} rounds; unbroken fire would be ~150)",
                rounds < 110);
            // "the bursts should vary in length, and time between them" -- so ASSERT the variation, or the
            // randomisation is a claim in a comment. Distinct lengths, not just several bursts: a metronome would
            // produce plenty of bursts and every one the same size.
            var distinct = new HashSet<int>(bursts);
            T.Check($"burst LENGTHS vary rather than being a metronome ({bursts.Count} bursts, {distinct.Count} distinct lengths: [{string.Join(",", bursts)}])",
                bursts.Count >= 3 && distinct.Count >= 2);
            // And the GAPS. Measured between the last shot of one burst and the first of the next.
            var gaps = new List<ulong>();
            int seen = 0;
            for (int bi = 0; bi < bursts.Count - 1; bi++) { seen += bursts[bi]; gaps.Add(shotFrames[seen] - shotFrames[seen - 1]); }
            var gapSpread = gaps.Count > 1 ? gaps[0] : 0UL;
            bool gapsVary = false;
            foreach (var gp in gaps) if (gp != gapSpread) gapsVary = true;
            T.Check($"...and so do the GAPS between them (frames: [{string.Join(",", gaps)}])", gapsVary);
            victim.QueueFree(); gun.QueueFree(); gunAi.QueueFree();
            yield return Ticks(2);

            // ---- 2c. THE BRANCH I CHANGED, NOT JUST THE ONE I WROTE. Marking NPC rounds meant touching the
            // hitmarker path, which only runs for PLAYER shots -- and a scripted edit had rewritten the two
            // helpers into calling themselves, so any player hit recursed until the stack died. Every check above
            // passed straight through it, because they all fire NPC rounds where the guard short-circuits first.
            // So: fire a REAL player bullet at a real target and require the hit to resolve.
            var shootMe = Vehicle.BuildByName("jeep");
            World.AddChild(shootMe);
            shootMe.GlobalPosition = new Vector3(-200f, 0f, 0f);
            var shooter = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(shooter);
            shooter.GlobalPosition = new Vector3(-200f, 1.5f, 14f);
            yield return Ticks(3);
            float hpBefore = shootMe.Health;
            // AIM AT THE THING. Firing flat from eye height sails straight over a jeep whose roof is at 1.14 m,
            // which is how the first run of this check read "no damage" and looked like a product bug.
            Vector3 from = shooter.GlobalPosition + Vector3.Up;
            Vector3 aimPoint = shootMe.GlobalPosition + new Vector3(0f, 0.6f, 0f);
            shooter.DebugFireBullet(from, (aimPoint - from).Normalized(), 40f);
            for (int i = 0; i < 30; i++) yield return Ticks(1);

            GD.Print($"[TURRET] player shot: jeep {hpBefore:0} -> {shootMe.Health:0}");
            T.Check($"a PLAYER's shot still resolves through the hitmarker path (jeep {hpBefore:0} -> {shootMe.Health:0} hp)",
                shootMe.Health < hpBefore);
            shooter.QueueFree(); shootMe.QueueFree();
            yield return Ticks(2);

            // ---- 2d. IS THE TRIGGER CONNECTED TO ANYTHING? Every check above hooks NpcHeli.NpcShot with a stub,
            // which proves the AI pulls the trigger and nothing at all about what the trigger drives. The real
            // hook is installed by PlayerController._Ready, and the first version of the ownership fix added the
            // srcGun/npc parameters to SpawnBullet and then never passed them at that one call site -- so the
            // rounds were still the player's, the tracers still came off her muzzle, and the suite was green.
            // This drives the REAL delegate and reads the bullet back out.
            var owner = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(owner);
            owner.GlobalPosition = new Vector3(600f, 2f, 0f);
            yield return Ticks(3);
            NpcHeli.NpcShot?.Invoke(new Vector3(600f, 30f, -10f), Vector3.Forward, "hmg");
            yield return Ticks(1);

            GD.Print($"[TURRET] real hook -> npc={owner.DebugNewestBulletIsNpc}");
            T.Check($"the round the REAL hook spawns is owned by the heli, not the player (npc={owner.DebugNewestBulletIsNpc})",
                owner.DebugNewestBulletIsNpc);
            // NOT ALSO ASSERTING srcGun HERE, and the reason is worth writing down rather than quietly dropping
            // the check. The obvious probe is the bullet's falloff -- but HMG.dat declares no falloff fields, so
            // the honest reading is 0 either way, and this rig's player holds no weapon, which makes srcGun and
            // the held Gun BOTH null and the two paths indistinguishable by construction. A check here would have
            // passed whether or not srcGun was ever passed, which is the exact failure this whole section exists
            // to stop. Discriminating it needs a player holding a gun with different ballistics; until then the
            // npc flag above is what is actually verified, and srcGun is carried on the same line as it.
            // MUZZLE FLASH AND REPORT (strawberry: "turn up the volume and travel of the gunshot sounds from
            // helis, adding the muzzle flashes we already have on guns, scaling them up quite a bit"). The SIZE
            // is the deliverable, so the sizes are what get asserted -- "a flash exists" would pass on the 0.55 m
            // held-gun quad, which at the range you watch a gunship from is invisible, and on the UnitSize 5-8 /
            // MaxDistance 45-70 one-shots used elsewhere for doors and impacts.
            NpcHeli.NpcShot?.Invoke(new Vector3(600f, 30f, -12f), Vector3.Forward, "hmg");
            yield return Ticks(1);
            float flashQuad = 0f, sndDist = 0f, lightRange = 0f;
            foreach (var n in World.GetChildren())
            {
                if (n is Node3D fn && fn.Name.ToString() == "NpcMuzzleFlash")
                    foreach (var c in fn.GetChildren())
                    {
                        if (c is MeshInstance3D mi && mi.Mesh is QuadMesh qm) flashQuad = qm.Size.X;
                        if (c is OmniLight3D ol) lightRange = ol.OmniRange;
                    }
                if (n is AudioStreamPlayer3D ap) sndDist = ap.MaxDistance;
            }
            GD.Print($"[TURRET] fx: flash quad {flashQuad:0.0} m, light range {lightRange:0} m, report carries {sndDist:0} m");
            T.Check($"the muzzle flash is scaled for an aircraft, not a held rifle (quad {flashQuad:0.0} m vs the 0.55 m 1P/3P one)",
                flashQuad > 1.5f && lightRange > 8f);
            T.Check($"...and the report carries across a valley, not a room (MaxDistance {sndDist:0} m vs 45-70 for doors and impacts)",
                sndDist > 300f);
            owner.QueueFree();
            yield return Ticks(2);

            // ---- 3. NO MOUNT, NO FIGHT -- expressed as data (does it carry a turret) rather than a name check.
            // This used to assert that a HUEY does not fight, which was correct while "dont wire up the other
            // helis for attack behavior" stood. strawberry reversed that deliberately in the same session
            // (huey + orca get door gunners), so the check now pins the CURRENT rule and the Skycrane -- which
            // carries no mount at all -- is the airframe that must stay out of it.
            var (crane, crai) = Spawn("skycrane", new Vector3(900f, 40f, 0f));
            yield return Ticks(3);
            crane.NoteAttackedFrom(new Vector3(900f, 12f, -90f));
            yield return Ticks(5);
            T.Check($"shooting an unarmed airframe does NOT start a gunfight (skycrane armed={crai.Armed}, mode {crai.Mode})",
                !crai.Armed && crai.Mode == NpcHeli.Stance.Patrol);

            // ---- 3b. A PLAYER-SPAWNED AIRFRAME IS EMPTY (strawberry: "helis spawned with the vehicle command
            // shouldnt have gunners"). Same airframe, built the ordinary way, with nobody aboard -- so its door
            // guns are unmanned and it cannot fight.
            var bare = Vehicle.BuildByName("huey");
            World.AddChild(bare);
            bare.GlobalPosition = new Vector3(1200f, 40f, 0f);
            var bareAi = new NpcHeli { Heli = bare, Terr = null, Target = Vector3.Zero, TargetName = "T" };
            World.AddChild(bareAi);
            yield return Ticks(3);
            T.Check($"a Huey spawned by the vehicle command has mounts but NO gunners ({bare.Turrets.Length} mounts, {bareAi.DebugLiveMounts} manned)",
                bare.Turrets.Length == 2 && bareAi.DebugLiveMounts == 0);
            // AND NO GUNS EITHER. The Dragonfang belongs to the gunner, so an empty airframe must not be flying
            // around with two of them floating in its doorways.
            bool anyGunVisible = false;
            foreach (var ch in bare.GetChildren())
                if (ch is Node3D yawN && yawN.Name.ToString().StartsWith("TurretYaw"))
                    foreach (var pc in yawN.GetChildren())
                        if (pc is Node3D pitchN)
                            foreach (var g in pitchN.GetChildren())
                                if (g is MeshInstance3D gm && gm.Visible) anyGunVisible = true;
            T.Check($"...and its Dragonfangs are hidden too, not floating in the empty doorways (anyVisible={anyGunVisible})",
                !anyGunVisible);

            // NAV LIGHTS FOLLOW THE ENGINE, and go out with the aircraft.
            bare.EngineOn = false;
            yield return Ticks(3);
            bool offWhenCold = bare.DebugNavLightsOn;
            bare.EngineOn = true; bare.DebugInstantStart = true;
            yield return Ticks(3);
            bool onWhenRunning = bare.DebugNavLightsOn;
            GD.Print($"[TURRET] nav lights: engine off={offWhenCold} engine on={onWhenRunning}");
            T.Check($"heading lights are OUT with the engine off and LIT with it on (off={offWhenCold}, on={onWhenRunning})",
                !offWhenCold && onWhenRunning);
            bare.TakeDamage(bare.HealthMax * 2f);
            yield return Ticks(5);
            T.Check($"...and they go out when the aircraft is destroyed (lit={bare.DebugNavLightsOn}, hp {bare.Health:0})",
                !bare.DebugNavLightsOn);
            bare.QueueFree(); bareAi.QueueFree();
            yield return Ticks(2);

            // ---- 3c. THE GUNNER HAS TO BE SHOOTABLE (strawberry: "the hitbox of the helis overlap the hitboxes
            // of the gunners. so its impossible to hit them"). The crew sat at 0.62 of the cabin half-width with
            // a 0.42 m hit radius, i.e. entirely INSIDE the hull box, so a side-on shot always struck the
            // fuselage first and the gunner could not be killed at all -- which removes the whole point of them.
            // A door gunner stands IN the doorway with their gun, so their box now protrudes past the skin.
            // Asserted by casting the ray a player would: what does it hit FIRST?
            // held:false ON PURPOSE. A frozen aircraft (FreezeMode.Static + ProcessMode.Disabled) never flushes
            // its moved transform into the physics space in this rig, so the ray hit NOTHING AT ALL -- neither
            // hull nor gunner -- and the check would have "failed" for a reason unrelated to the bug.
            var (shootable, shAi) = Spawn("huey", new Vector3(2000f, 40f, 0f), held: false);
            yield return Ticks(3);
            var stbdCrew = shootable.FindChild($"Gunner{shootable.Turrets[1].Seat}", false, false) as Node3D;
            T.Check("the starboard gunner exists to be shot at", stbdCrew != null);
            if (stbdCrew != null)
            {
                Vector3 chest = stbdCrew.GlobalPosition + Vector3.Up * 1.1f;
                Vector3 shotFrom = chest + new Vector3(25f, 0f, 0f);   // straight in from abeam, the natural shot
                var sq = PhysicsRayQueryParameters3D.Create(shotFrom, chest + new Vector3(-0.2f, 0f, 0f));
                sq.CollisionMask = (1u << 0) | (1u << 5) | (1u << 6);   // the bullet's own mask
                var sres = shootable.GetWorld3D().DirectSpaceState.IntersectRay(sq);
                string firstHit = sres.Count > 0 ? sres["collider"].As<GodotObject>().GetType().Name : "NOTHING";
                GD.Print($"[TURRET] veh at {shootable.GlobalPosition} crew at {stbdCrew.GlobalPosition} chest {chest} from {shotFrom}");
                GD.Print($"[TURRET] shot from abeam hits '{firstHit}' first (want TargetDummy, not Vehicle)");
                T.Check($"a shot from abeam reaches the GUNNER before the hull (first hit: {firstHit})",
                    sres.Count > 0 && sres["collider"].As<GodotObject>() is TargetDummy);
            }
            shootable.QueueFree(); shAi.QueueFree();
            yield return Ticks(2);

            // ---- 4. DOOR GUNNERS. Two crewed mounts, and killing them takes the sides away one at a time.
            var (huey, hai) = Spawn("huey", new Vector3(300f, 40f, 0f));
            yield return Ticks(3);
            T.Check($"the Huey carries TWO crewed door guns ({huey.Turrets.Length} mounts, {hai.DebugLiveMounts} manned)",
                huey.Turrets.Length == 2 && hai.DebugLiveMounts == 2);
            T.Check($"...with a 90 deg cone on each beam, port and starboard (port {huey.Turrets[0].YawMin:0}..{huey.Turrets[0].YawMax:0}, stbd {huey.Turrets[1].YawMin:0}..{huey.Turrets[1].YawMax:0})",
                Mathf.IsEqualApprox(huey.Turrets[0].YawMax - huey.Turrets[0].YawMin, 90f)
                && Mathf.IsEqualApprox(huey.Turrets[1].YawMax - huey.Turrets[1].YawMin, 90f)
                && huey.Turrets[0].YawMin > 0f && huey.Turrets[1].YawMax < 0f);
            T.Check($"...and it uses the Dragonfang, the Orca the Nykorev (huey '{huey.Turrets[0].GunId}')",
                huey.Turrets[0].GunId == "dragonfang");

            huey.NoteAttackedFrom(new Vector3(300f, 12f, -90f));
            yield return Ticks(5);
            T.Check($"shooting a Huey now DOES start a gunfight (mode {hai.Mode})", hai.Mode == NpcHeli.Stance.Engaged);

            huey.DebugKillCrew(huey.Turrets[0].Seat);
            yield return Ticks(5);
            T.Check($"killing one gunner silences that side and leaves the other ({hai.DebugLiveMounts} manned, mode {hai.Mode})",
                hai.DebugLiveMounts == 1 && hai.Mode == NpcHeli.Stance.Engaged);

            huey.DebugKillCrew(huey.Turrets[1].Seat);
            yield return Ticks(5);
            T.Check($"killing BOTH sends it running ({hai.DebugLiveMounts} manned, mode {hai.Mode})",
                hai.DebugLiveMounts == 0 && hai.Mode == NpcHeli.Stance.Flee);

            // AND FLEEING HAS TO MEAN FLYING AWAY. The state was a label first: the flight code had no Flee
            // branch, so the aircraft kept patrolling while reporting Flee, and the check above passed on it --
            // the same inert-flag shape that has bitten this feature twice already. So measure the distance from
            // the contact, which must GROW.
            var (runner, rai) = Spawn("huey", new Vector3(1500f, 60f, 0f), held: false);
            runner.LinearVelocity = new Vector3(0f, 0f, -12f);
            yield return Ticks(3);
            Vector3 contact = new Vector3(1500f, 5f, -120f);
            runner.NoteAttackedFrom(contact);
            yield return Ticks(5);
            runner.DebugKillCrew(runner.Turrets[0].Seat);
            runner.DebugKillCrew(runner.Turrets[1].Seat);
            yield return Ticks(5);
            float dStart = runner.GlobalPosition.DistanceTo(contact);
            // 20 s, not 10: it enters Flee still closing at 12 m/s, so most of a short window goes on reversing
            // that rather than on opening the range. Lengthening the window tests the same claim; loosening the
            // threshold would have tested a weaker one.
            for (int i = 0; i < 1000; i++) yield return Ticks(1);   // ~20 s
            float dEnd = runner.GlobalPosition.DistanceTo(contact);
            GD.Print($"[TURRET] flee: mode={rai.Mode} range from contact {dStart:0} -> {dEnd:0} m");
            T.Check($"a fleeing Huey actually runs AWAY, not just reports it (mode {rai.Mode}, {dStart:0} -> {dEnd:0} m from the contact)",
                rai.Mode == NpcHeli.Stance.Flee && dEnd > dStart + 60f);
            runner.QueueFree(); rai.QueueFree();
            yield return Ticks(2);

            // ---- 5. SIDE STRAFE. Beam guns cannot bear on a target the aircraft is flying AT, so an engaged
            // door-gunner airframe has to hold the contact abeam. Implemented as an ORBIT of the contact rather
            // than an offset aim point -- an offset is recomputed from wherever the aircraft currently is, so it
            // rotates as it closes and collapses into a pursuit curve (measured: contact local X +57.6, and
            // flipping the sign moved it to +57.4, so the sign was never what drove it).
            //
            // Measured in VEHICLE-LOCAL X, the frame the cone is defined in: port barrels point -X.
            var (strafer, sai) = Spawn("huey", new Vector3(-1200f, 60f, 260f), held: false);
            yield return Ticks(3);
            Vector3 mark = new Vector3(-1200f, 6f, 0f);
            strafer.NoteAttackedFrom(mark);
            for (int i = 0; i < 900; i++) yield return Ticks(1);   // ~18 s: close, then settle onto the circle
            float xBoth = (strafer.GlobalTransform.AffineInverse() * mark).X;
            float rBoth = new Vector2(strafer.GlobalPosition.X - mark.X, strafer.GlobalPosition.Z - mark.Z).Length();
            GD.Print($"[TURRET] strafe both-up: contact local X {xBoth:0.0}, range {rBoth:0} m");
            // BOTH GUNNERS UP -> it favours PORT, whose barrels point -X, so the contact must sit at NEGATIVE
            // local X. Asserting the SIDE, not just "some side": the first version checked only |X| > 15 and a
            // sign flip, and passed happily while the aircraft presented the contact to the gunner who could not
            // see it. "It is abeam" and "it is abeam of the RIGHT gunner" are different claims.
            T.Check($"an engaged door-gunner heli holds the contact abeam on the LIVE gunner's side (port up -> contact local X {xBoth:0.0}, want negative, at {rBoth:0} m)",
                xBoth < -15f);

            strafer.DebugKillCrew(strafer.Turrets[0].Seat);   // kill PORT; the survivor is starboard
            for (int i = 0; i < 900; i++) yield return Ticks(1);
            float xStbd = (strafer.GlobalTransform.AffineInverse() * mark).X;
            GD.Print($"[TURRET] strafe port-dead: contact local X {xStbd:0.0} (want the opposite sign)");
            T.Check($"losing the port gunner swaps it to present the STARBOARD arc (local X {xBoth:0.0} -> {xStbd:0.0}, want positive)",
                xStbd > 15f);
            strafer.QueueFree(); sai.QueueFree();
            yield return Ticks(2);

            // ---- 6. ORBIT STRAFE, the Hind's version: "circles you facing you". The claim has TWO halves and
            // they fail independently, so both get measured -- an aircraft that merely flies past holds its nose
            // on you briefly, and one that circles without facing you is just the beam-gun strafe.
            var (pylon, pai) = Spawn("hind", new Vector3(4000f, 55f, 240f), held: false);
            yield return Ticks(3);
            Vector3 tgt = new Vector3(4000f, 6f, 0f);
            pylon.NoteAttackedFrom(tgt);
            for (int i = 0; i < 700; i++) yield return Ticks(1);   // ~14 s to close and settle onto the circle

            float minR = 9999f, maxR = 0f, worstOffBore = 0f;
            for (int i = 0; i < 600; i++)                            // then ~12 s of watching the turn itself
            {
                yield return Ticks(1);
                Vector2 rad = new Vector2(pylon.GlobalPosition.X - tgt.X, pylon.GlobalPosition.Z - tgt.Z);
                minR = Mathf.Min(minR, rad.Length()); maxR = Mathf.Max(maxR, rad.Length());
                Vector3 nose = -pylon.GlobalTransform.Basis.Z;
                Vector3 toT = (tgt - pylon.GlobalPosition);
                if (toT.LengthSquared() > 1f)
                    worstOffBore = Mathf.Max(worstOffBore, Mathf.RadToDeg(nose.AngleTo(toT.Normalized())));
            }
            GD.Print($"[TURRET] orbit strafe: range {minR:0}..{maxR:0} m, worst off-bore {worstOffBore:0} deg, banking={pai.OrbitStrafing} bankTarget={pai.DebugBankTarget:0.00}");

            // IT STAYS AROUND YOU rather than running in or sailing off. A pursuit curve collapses the range; a
            // flypast opens it without bound.
            // BOUNDS SET FROM THE MEASURED TURN, not picked loose. The first cut spiralled 27..95 m at 63 deg
            // off-bore and PASSED a 25..320 m / 75 deg check -- bounds wide enough to accept the bug they were
            // meant to catch. The fixed turn holds 49..64 m at 27 deg, so these are drawn to reject the spiral.
            T.Check($"the Hind holds a standoff circle rather than spiralling in ({minR:0}..{maxR:0} m over 12 s)",
                minR > 35f && maxR < 90f);
            // AND IT KEEPS YOU ON THE NOSE, which is the half that distinguishes this from the beam strafe. The
            // chin turret only depresses 60 deg, so the nose has to stay roughly on you for the gun to bear.
            T.Check($"...while keeping its nose on the contact throughout (worst off-bore {worstOffBore:0} deg)",
                worstOffBore < 40f);
            T.Check($"...and it is the BANK doing the turning, not the heading loop (orbitStrafing={pai.OrbitStrafing}, bankTarget {pai.DebugBankTarget:0.00})",
                pai.OrbitStrafing && Mathf.Abs(pai.DebugBankTarget) > 0.02f);
            pylon.QueueFree(); pai.QueueFree();
            yield return Ticks(2);

            // NO CHECK HERE FOR THE YAW DEPARTURE, DELIBERATELY, AND THIS IS THE HONEST STATE OF IT.
            //
            // strawberry: "orca and huey spin violently out of control yaw axis" -- introduced by putting crew
            // aboard. The likely cause is that TargetDummy is a StaticBody3D on collision layer 1 (the WORLD
            // layer) and a vehicle's mask includes the world, so two of them parented inside the fuselage are
            // immovable obstacles embedded in the hull. EquipDoorGunners now calls AddCollisionExceptionWith on
            // each, which is correct on its own terms: an aircraft should not collide with its own crew.
            //
            // BUT I COULD NOT REPRODUCE THE DEPARTURE IN THIS RIG. A crewed Orca flown here peaks at 0.69 rad/s
            // with the exception and 0.01 without it -- so a spin-rate check passes either way and would have been
            // a check that cannot fail on the bug it names. Writing one anyway would convert "unverified" into
            // "green", which is the exact failure this file has hit repeatedly tonight.
            //
            // So the fix ships unverified and is labelled as such, and reproducing the departure in a rig is the
            // outstanding work. Whatever reproduces it belongs here.
        }
    }
}
