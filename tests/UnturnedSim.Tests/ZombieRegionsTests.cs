using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests for the region partition (rewrite plan phase 0). Regions are the coarse switch behind
    // requirement 8 -- a level full of zombies costs nothing while the player is elsewhere, because only
    // regions with a player near them run the fast tiers. Retail does the same thing: ZombieManager
    // sizes its region array from LevelNavigation.bounds and its per-frame loop walks regionsWithPlayers.
    [TestFixture]
    public class ZombieRegionsTests
    {
        // Retail's level grid, for reference: Regions.WORLD_SIZE = 64, REGION_SIZE = 8192/64 = 128,
        // and a point maps to FloorToInt((point.x + 4096) / REGION_SIZE).
        static int RetailIndex(float x, float z) =>
            (int)System.MathF.Floor((z + 4096f) / 128f) * 64 + (int)System.MathF.Floor((x + 4096f) / 128f);

        [Test]
        public void Uniform_Grid_Maps_Points_The_Way_Retail_Does()
        {
            var r = ZombieRegions.UniformGrid();
            Assert.That(r.Count, Is.EqualTo(64 * 64));

            foreach (var p in new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(-4096f, 0f, -4096f), new Vector3(4095.9f, 0f, 4095.9f),
                new Vector3(-1f, 0f, 1f), new Vector3(127.9f, 0f, -128.1f), new Vector3(1000f, 0f, -2500f),
            })
                Assert.That(r.RegionOf(p), Is.EqualTo(RetailIndex(p.x, p.z)), $"point {p}");
        }

        [Test]
        public void Points_Outside_Every_Region_Are_Orphans_Not_Region_Zero()
        {
            var r = ZombieRegions.UniformGrid();
            Assert.That(r.RegionOf(new Vector3(-5000f, 0f, 0f)), Is.EqualTo(-1));
            Assert.That(r.RegionOf(new Vector3(0f, 0f, 9000f)), Is.EqualTo(-1));
        }

        [Test]
        public void The_Hint_Fast_Path_Never_Changes_The_Answer()
        {
            var r = ZombieRegions.UniformGrid();
            var p = new Vector3(613f, 0f, -212f);
            int truth = r.RegionOf(p);

            Assert.That(r.RegionOf(p, truth), Is.EqualTo(truth), "hint that is correct");
            Assert.That(r.RegionOf(p, 0), Is.EqualTo(truth), "hint that is stale");
            Assert.That(r.RegionOf(p, -1), Is.EqualTo(truth), "no hint");
            Assert.That(r.RegionOf(p, 999999), Is.EqualTo(truth), "hint out of range");
        }

        [Test]
        public void Only_Regions_Near_A_Player_Are_Hot()
        {
            var r = ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f);
            r.HotMargin = 0f;
            var players = new[] { new Vector3(100f, 0f, 100f) };
            r.MarkHot(players, 1);

            int home = r.RegionOf(players[0]);
            Assert.That(r.IsHot(home), Is.True);
            Assert.That(r.HotCount, Is.EqualTo(1), "zero margin should light exactly one region");
            Assert.That(r.IsHot(r.RegionOf(new Vector3(1800f, 0f, 1800f))), Is.False);
            Assert.That(r.IsHot(-1), Is.False, "orphan rows must never read as hot");
        }

        [Test]
        public void Hot_Margin_Reaches_Across_The_Region_Seam()
        {
            // A player standing 5 m from a region boundary: the neighbour must be hot too, otherwise a
            // zombie a metre the other side of an invisible line goes dumb while you are next to it.
            var r = ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f);
            r.HotMargin = 32f;
            var players = new[] { new Vector3(-5f, 0f, 100f) };   // 5 m left of the x = 0 seam
            r.MarkHot(players, 1);

            Assert.That(r.IsHot(r.RegionOf(new Vector3(-5f, 0f, 100f))), Is.True, "the region the player is in");
            Assert.That(r.IsHot(r.RegionOf(new Vector3(5f, 0f, 100f))), Is.True, "the region across the seam");
        }

        [Test]
        public void No_Players_Means_Nothing_Is_Hot()
        {
            var r = ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f);
            r.MarkHot(new[] { Vector3.zero }, 1);
            Assert.That(r.HotCount, Is.GreaterThan(0));

            r.MarkHot(System.Array.Empty<Vector3>(), 0);
            Assert.That(r.HotCount, Is.EqualTo(0));
            for (int i = 0; i < r.Count; i++) Assert.That(r.IsHot(i), Is.False, $"region {i} stayed hot with no players");
        }

        [Test]
        public void Hot_Set_Follows_The_Player_Instead_Of_Accumulating()
        {
            var r = ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f);
            r.HotMargin = 0f;
            var players = new Vector3[1];

            players[0] = new Vector3(-1500f, 0f, -1500f);
            r.MarkHot(players, 1);
            int first = r.RegionOf(players[0]);

            players[0] = new Vector3(1500f, 0f, 1500f);
            r.MarkHot(players, 1);
            Assert.That(r.IsHot(first), Is.False, "the region the player LEFT is still hot");
            Assert.That(r.IsHot(r.RegionOf(players[0])), Is.True);
            Assert.That(r.HotCount, Is.EqualTo(1));
        }
    }
}
