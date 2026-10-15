using NUnit.Framework;
using System.Text.Json.Nodes;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class FiniteIslandFilesTests
{
    static string Root()
    {
        for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)
            if(Directory.Exists(Path.Combine(d.FullName,"game/content"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
    [Test] public void GenerationAndReopenPreserveAuthoredFilesAndRebuildOnlyDerivedGeometry()
    {
        string temp=Path.Combine(Path.GetTempPath(),"finite-files-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string content=Path.Combine(Root(),"game/content"),destination=Path.Combine(temp,"map");
        try
        {
            File.Copy(Path.Combine(Root(),"tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"),Path.Combine(temp,"Roads.dat"));
            var generated=FiniteIslandFiles.Generate(content,temp,destination,"Island 1234",1234,1,1);
            Assert.That(generated.Generated,Is.Not.Null);
            Assert.That(File.Exists(Path.Combine(destination,"island_1234_map.rgb")),Is.True);
            Assert.That(File.Exists(Path.Combine(destination,"island_1234_map.png")),Is.False,"the native PNG encoder owns final image persistence");
            generated.Surface.SetHole(3,4,true);
            generated.Surface.Grid[8,9]+=.01f;
            generated.Surface.SaveHeightmap(generated.HeightPath);
            var authored=Directory.GetFiles(destination,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes);
            var reopened=FiniteIslandFiles.Open(content,destination);
            Assert.That(reopened.Generated,Is.Null,"source saved-map reopening does not reconstruct generation-only populations");
            Assert.That(reopened.Surface.IslandTiles,Is.Empty);
            Assert.That(reopened.Surface.Grid,Is.EqualTo(generated.Surface.Grid));
            Assert.That(reopened.Surface.HoleCount,Is.EqualTo(1));
            Assert.That(reopened.Roads.RoadCount,Is.EqualTo(generated.Roads.RoadCount));
            Assert.That(reopened.Objects.Placements.Count,Is.EqualTo(generated.Objects.Placements.Count));
            var players=new FiniteIslandPlayerSpawns(); players.Load(reopened.PlayersPath);
            Assert.That(players.Points.Count,Is.EqualTo(generated.Generated.Players.Count));
            Assert.That(players.Points.All(p=>!p.IsAlt),Is.True);
            reopened.PrepareRuntime();
            foreach(var (path,bytes) in authored) Assert.That(File.ReadAllBytes(path),Is.EqualTo(bytes),path);
            using(var reader=new BinaryReader(File.OpenRead(Path.Combine(destination,".native","chunk_0_0.bin"))))
            {
                Assert.That(reader.ReadUInt32(),Is.EqualTo(0x43464755u));Assert.That(reader.ReadInt32(),Is.EqualTo(1));
                Assert.That(reader.ReadInt32(),Is.EqualTo(49*49));Assert.That(reader.ReadInt32(),Is.EqualTo((48*48-1)*6));
            }
            Assert.Throws<IOException>(()=>FiniteIslandFiles.Generate(content,temp,destination,"Island 1234",1234,1,1));
            Assert.That(Directory.GetDirectories(temp,".island-*"),Is.Empty);
            var descriptor=JsonNode.Parse(File.ReadAllText(Path.Combine(destination,"finite.json")));
            descriptor["resources"]="../other";
            File.WriteAllText(Path.Combine(destination,"finite.json"),descriptor.ToJsonString());
            Assert.Throws<InvalidDataException>(()=>FiniteIslandFiles.Open(content,destination));
        }
        finally { Directory.Delete(temp,true); }
    }
    [Test] public void OriginalNameAndFailedGenerationCannotOverwriteExistingFiles()
    {
        string temp=Path.Combine(Path.GetTempPath(),"finite-invalid-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.Throws<ArgumentException>(()=>FiniteIslandFiles.Generate("",temp,Path.Combine(temp,"bad"),"../outside",1));
            Assert.Throws<ArgumentOutOfRangeException>(()=>FiniteIslandFiles.Generate("",temp,Path.Combine(temp,"bad"),"Island",1,0,1));
            Assert.That(Directory.GetDirectories(temp),Is.Empty);
        }
        finally { Directory.Delete(temp,true); }
    }
}
