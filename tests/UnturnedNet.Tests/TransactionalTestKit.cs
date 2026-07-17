using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// Shared kit for the Phase 6 L0 batteries (MP_PLAN §4 Phase 6): the full NetWorldServer/NetWorldClient
    /// stack over deterministic MemTransport (the ServerCombatTests harness shape), plus the item/def/
    /// blueprint fixtures both sides register -- in production the content-hash handshake guarantees the
    /// tables match; in tests we simply install the same fixtures on every host.
    /// </summary>
    sealed class TransactionalHarness
    {
        public readonly MemNetwork Net;
        public readonly NetWorldServer Server;
        public readonly List<NetWorldClient> Clients = new List<NetWorldClient>();

        public TransactionalHarness(int seed, FaultyLinkConfig clientToServer = null, FaultyLinkConfig serverToClient = null)
        {
            Net = new MemNetwork(seed);
            if (clientToServer != null) Net.ClientToServer = clientToServer;
            if (serverToClient != null) Net.ServerToClient = serverToClient;
            Server = new NetWorldServer(new MemServerTransport(Net));
            TransactionalFixtures.RegisterSchema(Server.Deployables.Schema);
            Server.Transactions.Blueprints = TransactionalFixtures.Blueprints;
        }

        public NetWorldClient AddClient(string name)
        {
            var c = new NetWorldClient(new MemClientTransport(Net), name);
            TransactionalFixtures.RegisterSchema(c.Deployables.Schema);
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

        public bool StepUntil(System.Func<bool> condition, int maxTicks = 400)
        {
            for (int i = 0; i < maxTicks; i++)
            {
                if (condition()) return true;
                Step();
            }
            return condition();
        }

        public TransactionalHarness Connected(params string[] names)
        {
            foreach (var n in names) AddClient(n);
            // adverse-link harnesses need real handshake headroom (loss + latency + jitter)
            StepUntil(() =>
            {
                foreach (var c in Clients)
                    if (c.State != NetSessionState.Connected) return false;
                return true;
            }, 600);
            foreach (var c in Clients)
                Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), $"client connected (seed={Net.Seed})");
            return this;
        }

        /// <summary>Server-side item grant (the authority seeding its own state, not a command).</summary>
        public void Grant(ushort playerId, Item item)
        {
            Assert.That(Server.Inventories.TryGet(playerId, out var e), Is.True, "player has a server inventory");
            Assert.That(e.Inventory.tryAddItem(item), Is.True, "grant fit the grid");
        }

        public uint FindDeployable(NetWorldClient c, ushort defId)
        {
            foreach (var e in c.Deployables.All)
                if (e.DefId == defId) return e.NetIdValue;
            return 0;
        }
    }

    static class TransactionalFixtures
    {
        // ids mirror the real content so the fixtures read like the game: 458 generator, 459 spotlight,
        // 67 metal scrap, 13 canned beans (consumable), 4 eaglefire (4x2), 900/901 craft-fixture items
        public const ushort GeneratorId = 458;
        public const ushort SpotlightId = 459;
        public const ushort ScrapId = 67;
        public const ushort BeansId = 13;
        public const ushort RifleId = 4;
        public const ushort LogId = 900;
        public const ushort PlankId = 901;

        /// <summary>Reset + register the item table (the STATIC Assets registry is process-wide -- every
        /// test fixture re-seeds it in SetUp so no test depends on another's leftovers).</summary>
        public static void RegisterAssets()
        {
            Assets.clear();
            Assets.add(new ItemAsset { id = GeneratorId, itemName = "Generator", size_x = 2, size_y = 2 });
            Assets.add(new ItemAsset { id = SpotlightId, itemName = "Spotlight", size_x = 2, size_y = 2 });
            Assets.add(new ItemAsset { id = ScrapId, itemName = "Metal Scrap", size_x = 1, size_y = 1 });
            Assets.add(new ItemAsset { id = BeansId, itemName = "Canned Beans", size_x = 1, size_y = 1, type = EItemType.FOOD, useHealth = 10, useFood = 55 });
            Assets.add(new ItemAsset { id = RifleId, itemName = "Eaglefire", size_x = 4, size_y = 2, type = EItemType.GUN });
            Assets.add(new ItemAsset { id = LogId, itemName = "Log", size_x = 1, size_y = 1, guid = "fixture-log" });
            Assets.add(new ItemAsset { id = PlankId, itemName = "Plank", size_x = 1, size_y = 1, guid = "fixture-plank" });
        }

        /// <summary>The PowerSolverTests devices as net defs: a 4000 W generator (one Output) and a 250 W
        /// spotlight (Consumer + Passthrough) -- §4 Phase 6 "reusing the PowerSolverTests fixtures".</summary>
        public static void RegisterSchema(DeployableSchema schema)
        {
            schema.Register(new DeployableNetDef
            {
                DefId = GeneratorId, Health = 450f, FuelCapacity = 2000f, Range = 4f,
                Ports = new[] { new DeployablePortSpec { Kind = (byte)PowerPortKind.Output, Watts = 4000f } },
                SalvageItemId = ScrapId, SalvageCount = 2,
            });
            schema.Register(new DeployableNetDef
            {
                DefId = SpotlightId, Health = 300f, FuelCapacity = 0f, Range = 4f,
                Ports = new[]
                {
                    new DeployablePortSpec { Kind = (byte)PowerPortKind.Consumer, Watts = 250f },
                    new DeployablePortSpec { Kind = (byte)PowerPortKind.Passthrough, Watts = 0f },
                },
            });
        }

        /// <summary>Blueprint 0: 2 logs -> 1 plank (no gates). Blueprint 1: the same but gated on
        /// CRAFTING level 1 (Skill "Craft" maps to SUPPORT/CRAFTING in Crafting.MeetsSkill).</summary>
        public static IReadOnlyList<BlueprintDef> Blueprints
        {
            get
            {
                var open = new BlueprintDef { Name = "plank", Operation = "Craft" };
                open.Inputs.Add(new BlueprintDef.Ingredient { Guid = "fixture-log", Amount = 2, Consume = true });
                open.Outputs.Add(new BlueprintDef.Ingredient { Guid = "fixture-plank", Amount = 1 });
                var gated = new BlueprintDef { Name = "plank (skilled)", Operation = "Craft", Skill = "Craft", SkillLevel = 1 };
                gated.Inputs.Add(new BlueprintDef.Ingredient { Guid = "fixture-log", Amount = 2, Consume = true });
                gated.Outputs.Add(new BlueprintDef.Ingredient { Guid = "fixture-plank", Amount = 1 });
                return new List<BlueprintDef> { open, gated };
            }
        }
    }
}
