using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // MP_VITALS_PLAN P1: the server-side vitals sim. One authoritative PlayerVitalsSim per connected
    // player, stepped inside TickSimulation (after the player sim, before combat), fed server-derived
    // inputs (sprint from the replicated stance bits + a real entity position delta, drain from the
    // server flag, multipliers from the server's authoritative skills), all health writes routed through
    // the ONE write-through helper so HealthExact and the coarse Ceiling byte can never fork. Teeth:
    // every stepping/drain/death assertion here fails if the Vitals.Step insertion is removed from
    // NetWorldServer.TickSimulation. All deterministic MemTransport sims -- no sockets, no sleeps.
    [TestFixture]
    public class ServerVitalsTests
    {
        sealed class Harness
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public readonly List<NetWorldClient> Clients = new();

            public Harness(int seed)
            {
                Net = new MemNetwork(seed);
                Server = new NetWorldServer(new MemServerTransport(Net));
            }

            public NetWorldClient AddClient(string name)
            {
                var c = new NetWorldClient(new MemClientTransport(Net), name);
                Clients.Add(c);
                c.Connect();
                return c;
            }

            public void Step(System.Action perTickInputs = null)
            {
                perTickInputs?.Invoke();
                Net.Tick();
                foreach (var c in Clients) c.Tick();
                Server.TickSimulation();
                Server.TickReplication();
            }

            public void Step(int ticks, System.Action perTickInputs = null)
            {
                for (int i = 0; i < ticks; i++) Step(perTickInputs);
            }

            public bool StepUntil(System.Func<bool> cond, int maxTicks = 500)
            {
                for (int i = 0; i < maxTicks; i++) { Step(); if (cond()) return true; }
                return cond();
            }

            public Harness Connected(params string[] names)
            {
                foreach (var n in names) AddClient(n);
                Step(25);
                foreach (var c in Clients)
                    Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), $"client connected (seed={Net.Seed})");
                return this;
            }
        }

        // ---------------------------------------------------------------- lifecycle

        [Test]
        public void Join_CreatesFullVitalsEntry_DisconnectRemovesIt()
        {
            var h = new Harness(60101).Connected("a");
            var a = h.Clients[0];

            Assert.That(h.Server.Vitals.TryGet(a.PlayerId, out var e), Is.True, "join created the vitals entry");
            Assert.That(e.Sim.Health, Is.EqualTo(100f), "fresh health");
            Assert.That(e.Sim.Food, Is.EqualTo(1f).And.EqualTo(e.Sim.Water), "fresh food/water");
            Assert.That(e.Sim.Stamina, Is.EqualTo(1f), "fresh stamina");
            Assert.That(e.Sim.Infection, Is.EqualTo(0f), "no infection");
            Assert.That(e.Bleeding, Is.False.And.EqualTo(e.Broken), "no bleed/broken flags");

            a.Disconnect();
            h.Step(15);
            Assert.That(h.Server.Vitals.TryGet(a.PlayerId, out _), Is.False, "disconnect removed the entry");
        }

        // ---------------------------------------------------------------- stamina: sprint drain + regen

        [Test]
        public void SprintDrainsStamina_ThenRegensAfterTheHold()
        {
            var h = new Harness(60102).Connected("runner");
            var a = h.Clients[0];
            h.Server.Vitals.TryGet(a.PlayerId, out var e);

            // held-keys model: one SPRINT+forward input keeps integrating every tick -> entity moves ->
            // the server predicate (stance SPRINT && position delta) drains 0.22/s
            a.SendMoveInput(0f, 1f, 0f, MoveInput.PackStance(EPlayerStance.SPRINT));
            h.Step(100);   // 2 s of sprint
            Assert.That(e.Sim.Stamina, Is.LessThan(0.62f).And.GreaterThan(0.5f),
                        $"2 s sprint drained ~0.44 stamina (got {1f - e.Sim.Stamina:0.###}, seed={h.Net.Seed})");

            // stop: held zero input -> no movement -> the 1 s regen hold, then +0.33/s
            a.SendMoveInput(0f, 0f, 0f);
            float atStop = e.Sim.Stamina;
            h.Step(60);    // 1.2 s: the hold expires ~50 ticks in, regen barely starts
            h.Step(100);   // 2 s of regen at 0.33/s = +0.66 -> capped at 1
            Assert.That(e.Sim.Stamina, Is.GreaterThan(atStop + 0.4f),
                        $"stamina regenerated after the 1 s hold (was {atStop:0.###}, now {e.Sim.Stamina:0.###})");
        }

        [Test]
        public void SprintDrain_UsesTheServerSkillsMultipliers()
        {
            var h = new Harness(60103).Connected("skilled", "unskilled");
            var a = h.Clients[0];
            var b = h.Clients[1];
            long tick = h.Server.Session.CurrentTick;
            // EXERCISE maxed on A only: mastery 1 -> drain multiplier 0.5 (the SERVER's skills, never a
            // body's local defaults)
            Assert.That(h.Server.Skills.ServerSetSkillLevel(a.PlayerId, "exercise", 99, tick, out _, out _), Is.True);

            void SendBoth()
            {
                a.SendMoveInput(0f, 1f, 0f, MoveInput.PackStance(EPlayerStance.SPRINT));
                b.SendMoveInput(0f, 1f, 0f, MoveInput.PackStance(EPlayerStance.SPRINT));
            }
            SendBoth();
            h.Step(100);

            h.Server.Vitals.TryGet(a.PlayerId, out var ea);
            h.Server.Vitals.TryGet(b.PlayerId, out var eb);
            float drainedA = 1f - ea.Sim.Stamina, drainedB = 1f - eb.Sim.Stamina;
            Assert.That(drainedB, Is.GreaterThan(0.3f), "the unskilled runner drained at the base rate");
            Assert.That(drainedA, Is.EqualTo(drainedB * 0.5f).Within(0.03f),
                        $"EXERCISE mastery halved the drain ({drainedA:0.###} vs {drainedB:0.###})");
        }

        // ---------------------------------------------------------------- survival drain -> death -> respawn

        [Test]
        public void Starvation_KillsThroughKillPlayer_AndRespawnResetsTheSim()
        {
            var h = new Harness(60104).Connected("starved");
            var a = h.Clients[0];
            var died = new List<PlayerDiedEvent>();
            var respawned = new List<PlayerRespawnedEvent>();
            a.PlayerDied += e => died.Add(e);
            a.PlayerRespawned += e => respawned.Add(e);

            // the server's drain toggle via the server-gated console verb (§10 risk 9: the verb mutates
            // ONLY the server flag)
            Assert.That(h.Server.Transactions.RunConsole(a.PlayerId, "survival on"), Does.Contain("ENABLED"));
            Assert.That(h.Server.Vitals.SurvivalDrainEnabled, Is.True);

            // park the sim at the brink so the run is short: food about to bottom out, 2 HP left
            h.Server.Vitals.TryGet(a.PlayerId, out var e);
            e.Sim.Food = 0.001f;
            e.Sim.Health = 2f;

            Assert.That(h.StepUntil(() => died.Count > 0, 200), Is.True,
                        $"starvation killed within ~2 s (seed={h.Net.Seed})");
            Assert.That(died[0].Victim, Is.EqualTo(a.PlayerId));
            Assert.That(died[0].Killer, Is.EqualTo(0), "environment death carries no killer");
            h.Server.CombatState.TryGet(a.PlayerId, out var cs);
            Assert.That(cs.Alive, Is.False, "dead on the server");
            Assert.That(cs.Health, Is.EqualTo(0).And.EqualTo((int)cs.HealthExact), "0/0 through the mirror");
            Assert.That(cs.RespawnAtTick, Is.GreaterThan(h.Server.Session.CurrentTick),
                        "respawn scheduled at death + 175 ticks");
            Assert.That(cs.Deaths, Is.EqualTo(1));

            // the existing ServerCombat respawn honors the tick and resets the vitals sim (SP :1915-1916)
            Assert.That(h.StepUntil(() => respawned.Count > 0, 250), Is.True, "respawn fired");
            Assert.That(cs.Alive, Is.True, "alive again");
            Assert.That(cs.HealthExact, Is.EqualTo(100f).And.EqualTo(e.Sim.Health), "full health through the mirror");
            Assert.That(e.Sim.Food, Is.EqualTo(1f).Within(0.001f),
                        "fresh food after respawn (the sim was rebuilt; drain is still on, so within a tick of full)");
            // the respawn EVENT rides reliable and can beat the 25 Hz delta snapshot -- give the replica
            // a few ticks to catch the Alive/health flip
            Assert.That(h.StepUntil(() => a.CombatState.TryGet(a.PlayerId, out var replica)
                                       && replica.Alive && replica.Health == 100, 20),
                        Is.True, "the client replica saw the whole arc");
        }

        [Test]
        public void DeadPlayersAreNotStepped()
        {
            var h = new Harness(60105).Connected("corpse");
            var a = h.Clients[0];
            h.Server.Transactions.RunConsole(a.PlayerId, "survival on");
            h.Server.Vitals.TryGet(a.PlayerId, out var e);

            h.Server.Vitals.EnqueueDamage(a.PlayerId, 9999f, ServerVitals.CauseConsole);
            h.Step(2);
            h.Server.CombatState.TryGet(a.PlayerId, out var cs);
            Assert.That(cs.Alive, Is.False, "killed");

            float food = e.Sim.Food, water = e.Sim.Water;
            h.Step(100);   // well inside the 175-tick death window
            Assert.That(e.Sim.Food, Is.EqualTo(food), "a corpse's food does not drain");
            Assert.That(e.Sim.Water, Is.EqualTo(water), "a corpse's water does not drain");
        }

        // ---------------------------------------------------------------- the double-step rate pin (§10 risk 8)

        [Test]
        public void DrainRate_IsExactlyOneStepPerTick()
        {
            var h = new Harness(60106).Connected("pinned");
            var a = h.Clients[0];
            h.Server.Transactions.RunConsole(a.PlayerId, "survival on");
            h.Server.Vitals.TryGet(a.PlayerId, out var e);

            // reference: a lone sim stepped once per tick with the same inputs -- bit-identical floats.
            // A second (double-step) or missing (never-step) call per tick fails the exact comparison.
            var reference = new PlayerVitalsSim
            { Health = e.Sim.Health, Stamina = e.Sim.Stamina, Food = e.Sim.Food, Water = e.Sim.Water, Infection = e.Sim.Infection };
            const int N = 150;
            float dt = (float)SimClock.FixedDelta;
            for (int i = 0; i < N; i++) reference.Step(false, true, dt, PlayerVitalsSim.Multipliers.None);
            h.Step(N);

            Assert.That(e.Sim.Food, Is.EqualTo(reference.Food), "food stepped EXACTLY once per tick");
            Assert.That(e.Sim.Water, Is.EqualTo(reference.Water), "water stepped EXACTLY once per tick");
            Assert.That(e.Sim.Health, Is.EqualTo(reference.Health), "health stepped EXACTLY once per tick");
        }

        // ---------------------------------------------------------------- the damage/infection queue

        [Test]
        public void QueuedDamage_DrainsInEnqueueOrder_CreditsTheKillingEntry()
        {
            var h = new Harness(60107).Connected("a", "b", "victim");
            ushort attackerA = h.Clients[0].PlayerId;
            ushort attackerB = h.Clients[1].PlayerId;
            ushort victim = h.Clients[2].PlayerId;

            // two same-tick sources: A's 60 lands first (100 -> 40), B's 60 crosses zero -> B gets the kill
            h.Server.Vitals.EnqueueDamage(victim, 60f, ServerVitals.CauseZombie, attackerA);
            h.Server.Vitals.EnqueueDamage(victim, 60f, ServerVitals.CauseZombie, attackerB);
            h.Step(2);

            h.Server.CombatState.TryGet(victim, out var vs);
            h.Server.CombatState.TryGet(attackerA, out var asr);
            h.Server.CombatState.TryGet(attackerB, out var bsr);
            Assert.That(vs.Alive, Is.False, "two queued 60s killed");
            Assert.That(vs.Deaths, Is.EqualTo(1), "ONE death (the drain is idempotent past the kill)");
            Assert.That(asr.Kills, Is.EqualTo(0), "A's hit did not cross zero");
            Assert.That(bsr.Kills, Is.EqualTo(1), "B's hit crossed zero -> B credited");

            // late damage on the corpse is dropped, not double-killed
            h.Server.Vitals.EnqueueDamage(victim, 50f, ServerVitals.CauseZombie, attackerA);
            h.Step(2);
            Assert.That(vs.Deaths, Is.EqualTo(1), "a corpse takes no further damage");
            Assert.That(asr.Kills, Is.EqualTo(0));
        }

        [Test]
        public void QueuedInfection_AppliesTheServerImmunityMultiplier()
        {
            var h = new Harness(60108).Connected("immune", "plain");
            var a = h.Clients[0];
            var b = h.Clients[1];
            long tick = h.Server.Session.CurrentTick;
            Assert.That(h.Server.Skills.ServerSetSkillLevel(a.PlayerId, "immunity", 99, tick, out _, out _), Is.True,
                        "IMMUNITY maxed on A (mastery 1 -> infection halved)");

            h.Server.Vitals.EnqueueInfection(a.PlayerId, 0.4f);
            h.Server.Vitals.EnqueueInfection(b.PlayerId, 0.4f);
            h.Step(2);

            h.Server.Vitals.TryGet(a.PlayerId, out var ea);
            h.Server.Vitals.TryGet(b.PlayerId, out var eb);
            Assert.That(eb.Sim.Infection, Is.EqualTo(0.4f).Within(0.01f), "unskilled took the full dose (minus decay)");
            Assert.That(ea.Sim.Infection, Is.EqualTo(0.2f).Within(0.01f), "IMMUNITY mastery halved it");
        }

        [Test]
        public void BleedIcon_SetOnARealHit_ClearedByTheTimer()
        {
            var h = new Harness(60109).Connected("bleeder");
            var a = h.Clients[0];
            h.Server.Vitals.TryGet(a.PlayerId, out var e);

            h.Server.Vitals.EnqueueDamage(a.PlayerId, 0.5f, ServerVitals.CauseZombie);
            h.Step(2);
            Assert.That(e.Bleeding, Is.False, "a graze (<= 1 dmg) does not bleed (SP :1859 parity)");

            h.Server.Vitals.EnqueueDamage(a.PlayerId, 6f, ServerVitals.CauseZombie);
            h.Step(2);
            Assert.That(e.Bleeding, Is.True, "a real hit set the icon");
            h.Step(255);   // past the 5 s timer
            Assert.That(e.Bleeding, Is.False, "the icon timer cleared it");
        }

        // ---------------------------------------------------------------- the P3 death tail

        [Test]
        public void KillPlayer_IsIdempotent_DoubleKillSameTickCountsOnce()
        {
            var h = new Harness(60111).Connected("victim", "killerA", "killerB");
            ushort victim = h.Clients[0].PlayerId;
            ushort killerA = h.Clients[1].PlayerId;
            ushort killerB = h.Clients[2].PlayerId;
            long tick = h.Server.Session.CurrentTick;

            h.Server.Combat.KillPlayer(victim, killerA, tick);
            h.Server.Combat.KillPlayer(victim, killerB, tick);   // same-tick double kill: the !Alive guard eats it

            h.Server.CombatState.TryGet(victim, out var vs);
            h.Server.CombatState.TryGet(killerA, out var acs);
            h.Server.CombatState.TryGet(killerB, out var bcs);
            Assert.That(vs.Alive, Is.False);
            Assert.That(vs.Deaths, Is.EqualTo(1), "ONE death");
            Assert.That(vs.HealthExact, Is.EqualTo(0f), "health floored through the mirror");
            Assert.That((int)vs.Health, Is.EqualTo(0));
            Assert.That(acs.Kills, Is.EqualTo(1), "the FIRST kill credited");
            Assert.That(bcs.Kills, Is.EqualTo(0), "the second was a no-op");
        }

        [Test]
        public void DeathWhileDriving_ForceExitsTheCorpse()
        {
            var h = new Harness(60112).Connected("driver");
            var a = h.Clients[0];
            var exited = new List<VehicleExitedEvent>();
            a.VehicleExited += e => exited.Add(e);

            // a vehicle beside the spawn; the peer takes the seat over the wire
            var vid = h.Server.Ids.Mint();
            h.Server.Vehicles.ServerSpawn(vid, 0, 0, new Vector3(1f, 0f, 0f), h.Server.Session.CurrentTick, 20f);
            a.SendEnterVehicle(vid.Value);
            Assert.That(h.StepUntil(() => h.Server.VehicleHost.IsDriver(a.PlayerId), 50), Is.True,
                        $"seat taken (seed={h.Net.Seed})");

            // die in the seat (queued kill) -> ServerVehicles.Step's dead-driver sweep frees the seat
            h.Server.Vitals.EnqueueDamage(a.PlayerId, 9999f, ServerVitals.CauseConsole);
            Assert.That(h.StepUntil(() => !h.Server.VehicleHost.IsDriver(a.PlayerId), 20), Is.True,
                        "the corpse does not keep the seat");
            h.Server.CombatState.TryGet(a.PlayerId, out var cs);
            Assert.That(cs.Alive, Is.False, "dead");
            h.Server.Vehicles.TryGet(vid, out var ve);
            Assert.That(ve.DriverPlayerId, Is.EqualTo(0), "the seat is free");
            Assert.That(h.StepUntil(() => exited.Count > 0, 20), Is.True, "VehicleExited broadcast the eject");
        }

        // ---------------------------------------------------------------- the one write-through helper (§10 risk 6)

        [Test]
        public void CoarseByte_AlwaysEqualsCeilOfTheExactFloat()
        {
            var h = new Harness(60110).Connected("mirrored");
            var a = h.Clients[0];
            long tick = h.Server.Session.CurrentTick;
            h.Server.Vitals.TryGet(a.PlayerId, out var e);
            h.Server.CombatState.TryGet(a.PlayerId, out var cs);
            e.Sim.Food = 0.2f;   // close the regen gate so only explicit mutations move health

            void AssertMirror(string what)
            {
                Assert.That(cs.HealthExact, Is.EqualTo(e.Sim.Health), $"{what}: HealthExact IS the sim float");
                Assert.That((int)cs.Health, Is.EqualTo((int)System.MathF.Ceiling(e.Sim.Health)),
                            $"{what}: coarse byte == Ceiling(exact), the ONE convention");
            }

            h.Server.Vitals.ApplyDamageDirect(a.PlayerId, 33.33f, tick);   // combat-style damage
            AssertMirror("fractional damage");
            h.Server.Vitals.ApplyDamageDirect(a.PlayerId, 0.25f, tick);    // sub-1 graze
            AssertMirror("graze");
            h.Server.Vitals.ApplyHealDirect(a.PlayerId, 10.5f, tick);      // consume-style heal
            AssertMirror("heal");
            h.Server.Transactions.RunConsole(a.PlayerId, "survival on");
            e.Sim.Food = 0f;                                                // starvation ticks now drain health
            h.Step(50);
            AssertMirror("environmental drain");
            h.Server.Vitals.ApplyHealDirect(a.PlayerId, 500f, tick);       // over-heal clamps at MaxHealth
            Assert.That(e.Sim.Health, Is.EqualTo(100f));
            AssertMirror("clamped heal");
        }
    }
}
