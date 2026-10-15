using NUnit.Framework;
using System.Security.Cryptography;
using System.Text.Json;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class FiniteIslandScatterTests
{
    static string Root()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Test] public void OriginalBakesMatchEveryInstanceAndRepaintInvalidatesSameSeed()
    {
        string content=Path.Combine(Root(), "game/content");
        string temporary=Path.Combine(Path.GetTempPath(), "finite-scatter-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            File.Copy(Path.Combine(Root(), "tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"), Path.Combine(temporary,"Roads.dat"));
            var sourceFiles = Directory.GetFiles(Path.Combine(content,"foliage"),"*.bin")
                .Concat(Directory.GetFiles(Path.Combine(content,"resources"),"*.bin"))
                .ToDictionary(p=>p,p=>Hash(File.ReadAllBytes(p)));
            var g=FiniteIslandGeneration.Generate(content,temporary,1,1,1234);
            Assert.That(FiniteIslandScatter.Bake(g.Surface,1234,"island_1234",content,temporary),Is.EqualTo("foliage_island_1234"));
            Assert.That(FiniteIslandScatter.BakeResources(g.Surface,1234,"island_1234",content,temporary),Is.EqualTo("resources_island_1234"));
            // These complete binary fixtures come from original algorithms using
            // actual GodotSharp. Foliage has12 floats/instance, resources9.
            using var expected=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(),"tests/UnturnedBevy.Tests/fixtures/finite-island-1234-scatter.json")));
            int types=0;
            foreach(var file in expected.RootElement.EnumerateObject())
            {
                byte[] bytes=File.ReadAllBytes(Path.Combine(temporary,file.Name));
                int count=BitConverter.ToInt32(bytes);
                Assert.That(count,Is.EqualTo(file.Value.GetProperty("count").GetInt32()),file.Name);
                Assert.That(bytes.Length,Is.EqualTo(4+count*(file.Name.StartsWith("foliage")?48:36)),file.Name);
                Assert.That(Hash(bytes),Is.EqualTo(file.Value.GetProperty("sha256").GetString()),file.Name);
                types++;
            }
            Assert.That(Directory.GetFiles(temporary,"*.bin",SearchOption.AllDirectories).Length,Is.EqualTo(types));
            Assert.That(File.ReadAllBytes(Path.Combine(temporary,"resources_island_1234/resources.txt")),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(content,"resources/resources.txt"))),
                "resource identity is source manifest row order, not sorted names");
            foreach(var pair in sourceFiles)
                Assert.That(Hash(File.ReadAllBytes(pair.Key)),Is.EqualTo(pair.Value),"retail positions must remain untouched");

            // Same seed, changed final ground: no cached old grass/tree survives.
            // This reproduces the source's old seed-only cache failure directly.
            g.Surface.PaintSplat(512,-512,2000,0);
            Assert.That(FiniteIslandScatter.Bake(g.Surface,1234,"island_1234",content,temporary),Is.Null);
            Assert.That(FiniteIslandScatter.BakeResources(g.Surface,1234,"island_1234",content,temporary),Is.Null);
            Assert.That(Directory.GetFiles(temporary,"*.bin",SearchOption.AllDirectories),Is.Empty);
        }
        finally { Directory.Delete(temporary,true); }
    }
}
