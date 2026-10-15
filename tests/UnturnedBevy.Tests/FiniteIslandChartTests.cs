using Godot;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class FiniteIslandChartTests
{
    static string Root()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [TestCase(1,1,1234,false,"a20b08fb41229b8d6bc82d8088df868c7e94a0133f66bb119368bc9f11407c77","52d9f39760430fb70486700b1df520f4cbdf0917555c918c11cd91ca9782d5f9")]
    [TestCase(2,1,9876,true,"38632c6be12c59b91be9efa2714c20188e9a30e2f8a0984e6a5d62e31bd12b8f","34dc88b2d8ae5f6f0dd6026da54f47bbe4f989245c45ff74a5af3b86115e4887")]
    [TestCase(3,3,771177,false,"ebd6442a23dcfd234b6cd3fab5d2f7ac5a854eec576dad276700e403422afc89","8f430e459f4687f46b357a587f9055ad562bd885da2e00ef7e8f91bc5f4963f9")]
    public void CompleteOriginalChartDataAndNames(int x,int z,int seed,bool lakes,string rgb,string nodes)
    {
        string temporary=Path.Combine(Path.GetTempPath(),"finite-chart-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            File.Copy(Path.Combine(Root(),"tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"),Path.Combine(temporary,"Roads.dat"));
            var generated=FiniteIslandGeneration.Generate(Path.Combine(Root(),"game/content"),temporary,x,z,seed,lakes);
            var chart=FiniteIslandChart.Bake(generated.Surface,seed);
            Assert.Multiple(() => {
                Assert.That(chart.Width,Is.EqualTo(1024)); Assert.That(chart.Height,Is.EqualTo(1024));
                Assert.That(chart.Rgb,Has.Length.EqualTo(1024*1024*3));
                Assert.That(Hash(chart.Rgb),Is.EqualTo(rgb),"original generated RGB8 data, not a rendered-scene golden");
                Assert.That(Hash(Encoding.UTF8.GetBytes(chart.Nodes)),Is.EqualTo(nodes));
                Assert.That(chart.Size,Is.EqualTo(Math.Max(x,z)*1024));
                Assert.That(chart.Centre.X,Is.EqualTo(x*512)); Assert.That(chart.Centre.Y,Is.EqualTo(-z*512));
                Assert.That(chart.ImageName,Is.EqualTo($"island_{seed}_map.png"));
                Assert.That(chart.NodesName,Is.EqualTo($"nodes_island_{seed}.tsv"));
            });
            var labels=chart.Nodes.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>l.Split('\t')[0]).ToArray();
            Assert.That(labels.Distinct().Count(),Is.EqualTo(labels.Length),"source names drawn without replacement");
            if(seed==1234)
            {
                // Original seed-only chart cache bug: altered final paint must
                // redraw despite the same seed and unchanged town identities.
                generated.Surface.PaintSplat(512,-512,2000,0);
                var repainted=FiniteIslandChart.Bake(generated.Surface,seed);
                Assert.That(Hash(repainted.Rgb),Is.Not.EqualTo(rgb));
                Assert.That(repainted.Nodes,Is.EqualTo(chart.Nodes));
            }
        }
        finally { Directory.Delete(temporary,true); }
    }

    [Test]
    public void ReopenUsesExistingArtAndFrameWithoutInventingMissingTownData()
    {
        string temporary=Path.Combine(Path.GetTempPath(),"finite-rebind-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var surface=new FiniteIslandSurface(new FiniteIslandLayout(2,1));
            Assert.That(FiniteIslandChart.Rebind(null,77,temporary),Is.Null);
            Assert.That(FiniteIslandChart.Rebind(surface,77,temporary),Is.Null);
            string image=Path.Combine(temporary,"island_77_map.png"), nodes=Path.Combine(temporary,"nodes_island_77.tsv");
            byte[] preserved={0,1,17,255};
            File.WriteAllBytes(image,preserved); // Source Rebind checks existence; loading/decoding is the image provider's job.
            var noNodes=FiniteIslandChart.Rebind(surface,77,temporary);
            Assert.That(noNodes.NodesName,Is.Null,"missing sidecar preserves the previous caller nodes, as source");
            File.WriteAllText(nodes,"Saved Town\t10,20,-30\n");
            var binding=FiniteIslandChart.Rebind(surface,77,temporary);
            Assert.Multiple(() => {
                Assert.That(binding.ImageName,Is.EqualTo("island_77_map.png"));
                Assert.That(binding.NodesName,Is.EqualTo("nodes_island_77.tsv"));
                Assert.That(binding.Centre.X,Is.EqualTo(1024)); Assert.That(binding.Centre.Y,Is.EqualTo(-512));
                Assert.That(binding.Size,Is.EqualTo(2048));
                Assert.That(File.ReadAllBytes(image),Is.EqualTo(preserved));
                Assert.That(File.ReadAllText(nodes),Is.EqualTo("Saved Town\t10,20,-30\n"));
                Assert.That(surface.IslandRoutes,Is.Empty); Assert.That(surface.IslandTiles,Is.Empty);
                Assert.That(FiniteIslandChart.Bake(null,77),Is.Null);
            });
        }
        finally { Directory.Delete(temporary,true); }
    }
}
