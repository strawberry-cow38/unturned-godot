using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // B5 (SP/MP-unify) -- server-authoritative fine vitals (food/water/stamina/infection + starvation death),
    // the owner-only SystemVitals(13) block. Fixes the shipped split-authority bug: HP was server-adopted but
    // the fine vitals ran the shell's LOCAL sim and the `died` result was DISCARDED, so you drained to Food=0
    // and never died -- the server ran no hunger sim. These prove the server now owns the sim and routes
    // starvation through the SAME ServerCombat sink weapons use (HP stays a single authority).
    [TestFixture]
    public class PlayerVitalsReplicationTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        // find the (page,x,y) of the first cell holding item `id` across every inventory page.
        static bool FindCell(PlayerInventory inv, ushort id, out byte page, out byte x, out byte y)
        {
            for (byte p = 0; p < inv.items.Length; p++)
            {
                var pg = inv.items[p];
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var j = pg.getItem(i);
                    if (j?.item != null && j.item.id == id) { page = p; x = j.x; y = j.y; return true; }
                }
            }
            page = x = y = 0; return false;
        }

        [Test]
        public void fine_vitals_replicate_to_owner_only()
        {
            var h = new TransactionalHarness(9070).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            h.Server.Vitals.SurvivalDrain = true;   // turn on hunger so food/water actually drain

            // drain long enough that food drops a wire quantum below 1.0 (0.005/s => ~1 quantum @ 8 bits per ~0.8 s)
            h.Step(120);

            Assert.That(a.Vitals.TryGet(a.PlayerId, out var mine) && mine.Sim.Food < 1f, Is.True,
                        $"the owner replica received the drain (food {(a.Vitals.TryGet(a.PlayerId, out var m2) ? m2.Sim.Food : 1f):0.000}, seed={h.Net.Seed})");

            // owner-only proof (§2.6 interest hook): B replicates its OWN vitals, never A's
            Assert.That(b.Vitals.TryGet(a.PlayerId, out _), Is.False, "A's vitals never entered B's replica");
            Assert.That(b.Vitals.TryGet(b.PlayerId, out _), Is.True, "B still sees its own vitals");

            // parity is exact on the QUANTIZED wire value -- freeze the drain + let the last owner block land,
            // then the server's StateHashFor must equal the owner replica's StateHash (no tolerance).
            h.Server.Vitals.SurvivalDrain = false;
            h.Step(10);
            Assert.That(a.Vitals.StateHash(), Is.EqualTo(h.Server.Vitals.StateHashFor(a.PlayerId)),
                        "owner parity: server StateHashFor == replica StateHash (quantized)");
        }

        [Test]
        public void starvation_routes_through_combat_sink_and_kills()
        {
            var h = new TransactionalHarness(9071).Connected("a");
            var a = h.Clients[0];
            h.Server.Vitals.SurvivalDrain = true;
            h.Step(5);

            // seed the server food to 0 and HP low so starvation bites within the step budget
            Assert.That(h.Server.Vitals.TryGet(a.PlayerId, out var ve), Is.True);
            ve.Sim.Food = 0f;
            Assert.That(h.Server.CombatState.TryGet(a.PlayerId, out var ce), Is.True);
            ce.HealthExact = 3f; ce.Health = 3;

            var myDeaths = new List<PlayerDiedEvent>();
            a.PlayerDied += myDeaths.Add;

            // HP must DROP (routed through DamagePlayerExternal -> ApplyPlayerDamage), not stay put
            Assert.That(h.StepUntil(() => ce.HealthExact < 3f, 60), Is.True,
                        $"starvation routed HP loss through the combat sink (seed={h.Net.Seed})");

            // ...and keep dropping to death. The death fact reaches the owner with Killer 0 (environment) --
            // proving ApplyPlayerDamage's death path, not a bare HealthExact write (the reliable event lands a
            // few ticks after the server flips Alive, so poll for it).
            bool SawKiller0()
            {
                foreach (var e in myDeaths) if (e.Victim == a.PlayerId && e.Killer == 0) return true;
                return false;
            }
            Assert.That(h.StepUntil(SawKiller0, 400), Is.True, "starved to death: PlayerDied(Killer=0) broadcast on the starvation kill");
            Assert.That(ce.Alive, Is.False, "the server owns the death (Alive false)");
            Assert.That(ce.HealthExact, Is.LessThanOrEqualTo(0f), "HealthExact drained to 0 via the sink");
            Assert.That(ce.Health, Is.EqualTo((byte)0), "coarse Health floored at 0");
        }

        [Test]
        public void consume_raises_server_food_water()
        {
            var h = new TransactionalHarness(9072).Connected("a");
            var a = h.Clients[0];
            // an MRE that raises BOTH food and water on Use (the .dat 0-100 grain)
            Assets.add(new ItemAsset { id = 700, itemName = "MRE", size_x = 1, size_y = 1, type = EItemType.FOOD, useFood = 40, useWater = 30 });
            h.Step(5);

            // seed the server vitals LOW so the raise is unambiguous, and stock one MRE in the SERVER grid
            Assert.That(h.Server.Vitals.TryGet(a.PlayerId, out var ve), Is.True);
            ve.Sim.Food = 0.2f; ve.Sim.Water = 0.2f;
            h.Grant(a.PlayerId, new Item(700));
            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var sInv), Is.True);
            Assert.That(FindCell(sInv.Inventory, 700, out byte pg, out byte cx, out byte cy), Is.True, "found the seeded MRE cell");

            long consumesBefore = h.Server.Transactions.Diag.ConsumesApplied;
            a.SendConsume(pg, cx, cy);

            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.ConsumesApplied == consumesBefore + 1, 60), Is.True,
                        $"the server consumed the MRE (seed={h.Net.Seed})");
            Assert.That(ve.Sim.Food, Is.GreaterThan(0.55f), "consume raised server Food (+0.40)");
            Assert.That(ve.Sim.Water, Is.GreaterThan(0.45f), "consume raised server Water (+0.30)");
        }

        [Test]
        public void regen_while_fed_heals_combat_hp()
        {
            var h = new TransactionalHarness(9073).Connected("a");
            var a = h.Clients[0];
            h.Server.Vitals.SurvivalDrain = true;   // the HP-delta routing (regen + starvation) is gated on survival
            h.Step(5);

            // fed + hydrated by default (food/water 1.0), but damaged: the sim regens HP while fed, routed
            // through the direct HealthExact raise (never a starvation sink)
            Assert.That(h.Server.CombatState.TryGet(a.PlayerId, out var ce), Is.True);
            ce.HealthExact = 50f; ce.Health = 50;

            Assert.That(h.StepUntil(() => ce.HealthExact > 51.5f, 120), Is.True,
                        $"regen while fed raised the combat HP (seed={h.Net.Seed})");
            Assert.That(ce.Alive, Is.True, "still alive -- this is regen, not damage");
            Assert.That(ce.HealthExact, Is.LessThanOrEqualTo(100f), "regen never overshoots MaxHealth");
        }
    }
}
