using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // Rope tow (strawberry 2026-07-19): tie a hemp rope from a tower's REAR tow node to a towed car's FRONT tow node;
    // a spring-tension pull drags the towed along, the tower drives a bit sluggish. These L1 tests exercise the SIM
    // side (Vehicle.AttachTow/DetachTow/UpdateTow + the Drive debuff) headless on a flat ground plane -- the tool UI
    // (PlayerController rope mode) is driven by input and lives outside the sim gate.

    // Attach/detach lifecycle + guards: tie sets both refs and a clamped-short rest length, untie clears them, and a
    // too-far tie is rejected.
    public class RopeTowAttachDetach : GameTest
    {
        public override string Name => "vehicle.rope_tow_attach_detach";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var tower = Vehicle.BuildByName("jeep"); World.AddChild(tower); tower.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            var towed = Vehicle.BuildByName("jeep"); World.AddChild(towed); towed.GlobalPosition = new Vector3(0f, 1.2f, 7f);   // behind the tower (+Z = back), gap in tie range
            yield return Ticks(50);   // settle onto the ground

            float gap = tower.RearTowWorld.DistanceTo(towed.FrontTowWorld);
            bool ok = tower.AttachTow(towed);
            T.Check($"tie succeeds (rear->front, gap {gap:0.0}m within reach)", ok);
            T.Check("tower.Towing points at the towed car", tower.Towing == towed);
            T.Check("towed.TowedBy points back at the tower", towed.TowedBy == tower);
            T.Check("a second tie on either end is rejected (one rope per car end)", !tower.AttachTow(towed) && !towed.AttachTow(tower));

            tower.DetachTow();
            T.Check("untie clears the tower ref", tower.Towing == null);
            T.Check("untie clears the towed ref", towed.TowedBy == null);
            T.Check("can re-tie after untie", tower.AttachTow(towed));
            towed.DetachTow();   // untie callable from the TOWED end too
            T.Check("untie from the towed end also clears both refs", tower.Towing == null && towed.TowedBy == null);

            // too far apart -> rejected
            towed.GlobalPosition = new Vector3(0f, 1.2f, 40f);
            yield return Ticks(2);
            T.Check("a tie beyond TowAttachReach is rejected", !tower.AttachTow(towed));
        }
    }

    // No snap-on-attach: tying at a gap near the attach reach must NOT immediately yank the cars together (rest length
    // == the current gap, so the rope starts slack/neutral, never in tension). Regression guard for the reach>restMax bug.
    public class RopeTowNoAttachYank : GameTest
    {
        public override string Name => "vehicle.rope_tow_no_attach_yank";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var tower = Vehicle.BuildByName("jeep"); World.AddChild(tower); tower.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            var towed = Vehicle.BuildByName("jeep"); World.AddChild(towed); towed.GlobalPosition = new Vector3(0f, 1.2f, 8.6f);   // node gap ~3.8m, near the 4.5 reach
            yield return Ticks(50);

            float gap0 = tower.RearTowWorld.DistanceTo(towed.FrontTowWorld);
            float towedZ0 = towed.GlobalPosition.Z;
            T.Check($"tie succeeds at a near-max gap ({gap0:0.0}m)", tower.AttachTow(towed));
            for (int i = 0; i < 30; i++) yield return Ticks(1);   // tower is NOT driven -> nothing should pull the towed
            float moved = Mathf.Abs(towed.GlobalPosition.Z - towedZ0);
            T.Check($"the towed car was NOT yanked on attach (moved {moved:0.00}m)", moved < 0.6f);
            T.Check("still tied (didn't snap from a phantom yank)", tower.Towing == towed);
        }
    }

    // The pull: a driven tower drags a passive towed car forward and holds it on the rope (no snap, no fly-off).
    public class RopeTowPulls : GameTest
    {
        public override string Name => "vehicle.rope_tow_pulls";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var tower = Vehicle.BuildByName("jeep"); World.AddChild(tower); tower.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            var towed = Vehicle.BuildByName("jeep"); World.AddChild(towed); towed.GlobalPosition = new Vector3(0f, 1.2f, 7f);
            yield return Ticks(50);   // settle

            float towedZ0 = towed.GlobalPosition.Z;
            T.Check("tie succeeds", tower.AttachTow(towed));

            // Move the tower forward (-Z) at a steady clip to isolate the ROPE's pull from the engine's traction tuning:
            // the spring in UpdateTow must drag the passive towed car along without snapping or letting it fall behind.
            float maxGap = 0f;
            for (int i = 0; i < 150; i++)   // ~3s at 3 m/s -> the tower travels ~9m
            {
                tower.LinearVelocity = new Vector3(0f, tower.LinearVelocity.Y, -3f);   // held forward, keep gravity on Y
                yield return Ticks(1);
                maxGap = Mathf.Max(maxGap, tower.RearTowWorld.DistanceTo(towed.FrontTowWorld));
            }

            T.Check("the rope never snapped -- still towing", tower.Towing == towed && towed.TowedBy == tower);
            T.Check($"the rope stayed within its break length throughout (max gap {maxGap:0.0} < {Vehicle.TowBreakLen})", maxGap < Vehicle.TowBreakLen);
            float towedAdvance = towedZ0 - towed.GlobalPosition.Z;
            T.Check($"the towed car got DRAGGED forward on the rope ({towedAdvance:0.0}m)", towedAdvance > 3f);
            T.Check($"the towed car kept up with the tower (final gap {tower.RearTowWorld.DistanceTo(towed.FrontTowWorld):0.0}m)",
                    tower.RearTowWorld.DistanceTo(towed.FrontTowWorld) < Vehicle.TowBreakLen);
        }
    }

    // The tower drives sluggish while hauling: Drive scales the engine force to 0.7x while Towing != null.
    public class RopeTowDebuff : GameTest
    {
        public override string Name => "vehicle.rope_tow_debuff";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var tower = Vehicle.BuildByName("jeep"); World.AddChild(tower); tower.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            var towed = Vehicle.BuildByName("jeep"); World.AddChild(towed); towed.GlobalPosition = new Vector3(0f, 1.2f, 5f);
            yield return Ticks(50);
            tower.EngineOn = true; tower.Fuel = tower.FuelMax > 0f ? tower.FuelMax : 100f;

            // baseline: full throttle at rest, NOT towing -> raw engine force
            tower.LinearVelocity = Vector3.Zero; tower.AngularVelocity = Vector3.Zero; tower.Wake();
            tower.Drive(1f, 0f, false);
            float baseForce = Mathf.Abs(tower.EngineForce);
            T.Check($"baseline engine force is non-zero ({baseForce:0})", baseForce > 1f);

            // now towing -> Drive should scale the engine force to 0.7x
            T.Check("tie succeeds", tower.AttachTow(towed));
            tower.LinearVelocity = Vector3.Zero; tower.AngularVelocity = Vector3.Zero;
            tower.Drive(1f, 0f, false);
            float towForce = Mathf.Abs(tower.EngineForce);
            T.Check($"towing debuffs the engine to ~0.7x ({towForce:0}/{baseForce:0} = {towForce / baseForce:0.00})",
                    baseForce > 0f && Mathf.Abs(towForce / baseForce - 0.7f) < 0.02f);
        }
    }

    // A6 (SP/MP-unify): the rope-tow RELATIONSHIP replicates. A host AttachTow on two real server jeep nodes
    // lands as entity.TowedNetId + TowRestLen on an OBSERVER client's replica, and the client's
    // VehicleReplicaView materializes exactly ONE cosmetic TowRope between the two puppets; a DetachTow clears
    // both fields and retires the rope. Physics stays host-authoritative (Vehicle.UpdateTow on the real
    // bodies); this proves only the publish + consume of the relationship. Teeth: with VehicleNetSync's
    // ServerPublishTow call reverted, the observer's TowedNetId never leaves 0 -> RopeCount stays 0 and the
    // "tow replicated" / "exactly one rope" checks fail.
    public class RopeTowReplicates : GameTest
    {
        public override string Name => "vehicle.rope_tow_replicates";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(76161);
            var observer = new NetWorldClient(new MemClientTransport(net), "observer", contentHash: NetContent.Hash);
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); observer.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // BEFORE the DedicatedServer -> server sim + vehicle sync + replicate stay LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);
            var view = new VehicleReplicaView { Client = observer };
            World.AddChild(view);
            observer.Connect();

            // two real jeep nodes on the server world, tower ahead (-Z is forward) and towed behind, gap in reach
            var tower = Vehicle.BuildByName("jeep"); World.AddChild(tower); tower.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            var towed = Vehicle.BuildByName("jeep"); World.AddChild(towed); towed.GlobalPosition = new Vector3(0f, 1.2f, 7f);

            yield return Until(() => observer.State == NetSessionState.Connected, 5);
            T.Check("observer joined", observer.State == NetSessionState.Connected);
            yield return Until(() => observer.Vehicles.Count == 2, 5);
            T.Check("both jeep nodes replicated (VehicleNetSync minted their entities)", observer.Vehicles.Count == 2);

            yield return Ticks(50);   // settle onto the fallback ground before tying
            bool tied = tower.AttachTow(towed);
            T.Check("host AttachTow succeeded (rear->front gap within reach)", tied);
            T.Check("tower.Towing points at the towed node", tower.Towing == towed);

            // the relationship must publish (ServerPublishTow) + replicate to the observer
            yield return Until(() =>
            {
                foreach (var e in observer.Vehicles.All) if (e.TowedNetId != 0) return true;
                return false;
            }, 5);

            VehicleReplication.VehicleEntity towerE = null, towedE = null;
            foreach (var e in observer.Vehicles.All)
                if (e.TowedNetId != 0) towerE = e;
            T.Check("the tow relationship replicated to the observer (some entity carries TowedNetId!=0)", towerE != null);
            if (towerE != null)
            {
                T.Check("the tower's TowedNetId points at another vehicle entity", observer.Vehicles.TryGet(towerE.TowedNetId, out towedE) && towedE != null);
                float wantRest = NetQuantization.QuantizeClampedFloat(tower.TowRestLenValue, VehicleReplication.TowRestIntBits, VehicleReplication.TowRestFracBits);
                T.Check($"the replicated TowRestLen matches the host's quantized rope rest length ({towerE.TowRestLen:0.000} vs {wantRest:0.000})",
                        Mathf.Abs(towerE.TowRestLen - wantRest) < 0.001f);
                T.Check("the towed end carries NO TowedNetId of its own (derived, not replicated)", towedE == null || towedE.TowedNetId == 0);
            }

            // the client view materializes exactly one cosmetic rope between the two puppets
            yield return Until(() => view.RopeCount == 1, 5);
            T.Check($"the observer's VehicleReplicaView drew exactly one TowRope ({view.RopeCount})", view.RopeCount == 1);
            T.Check("both puppets exist for the rope endpoints", towerE != null
                    && view.TryGetPuppet(towerE.NetIdValue, out _) && view.TryGetPuppet(towerE.TowedNetId, out _));

            // detach on the host -> the fields clear and the rope retires
            tower.DetachTow();
            yield return Until(() =>
            {
                foreach (var e in observer.Vehicles.All) if (e.TowedNetId != 0) return false;
                return view.RopeCount == 0;
            }, 5);
            bool cleared = true;
            foreach (var e in observer.Vehicles.All) if (e.TowedNetId != 0) cleared = false;
            T.Check("detach cleared TowedNetId on the replica", cleared);
            T.Check($"the cosmetic rope was retired ({view.RopeCount})", view.RopeCount == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            observer.Disconnect();
        }
    }

    // Overstretch snaps the rope: yank the towed car past the break length and the tow auto-detaches next tick.
    public class RopeTowBreaks : GameTest
    {
        public override string Name => "vehicle.rope_tow_breaks";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var tower = Vehicle.BuildByName("jeep"); World.AddChild(tower); tower.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            var towed = Vehicle.BuildByName("jeep"); World.AddChild(towed); towed.GlobalPosition = new Vector3(0f, 1.2f, 7f);
            yield return Ticks(50);
            T.Check("tie succeeds", tower.AttachTow(towed));

            // teleport the towed car far beyond the break length; the tower's UpdateTow snaps the rope on its next tick
            towed.GlobalPosition = new Vector3(0f, 1.2f, 30f);
            yield return Ticks(3);
            T.Check("overstretch snapped the rope (tower)", tower.Towing == null);
            T.Check("overstretch snapped the rope (towed)", towed.TowedBy == null);
        }
    }
}
