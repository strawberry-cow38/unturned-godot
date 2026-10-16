using Godot;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text.Json;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class FiniteIslandConsumerTests
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    string temporary;
    static string Root()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
    [SetUp] public void Setup()
    {
        temporary = Path.Combine(Path.GetTempPath(), "finite-island-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        File.Copy(Path.Combine(Root(), "tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"), Path.Combine(temporary, "Roads.dat"));
    }
    [TearDown] public void Cleanup() => Directory.Delete(temporary, true);
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // Full source output fixtures, produced by the differential verifier using
    // original methods and actual GodotSharp. Covers every prop basis/material,
    // spawned point, painted cell, conformed height and serialized road joint.
    [TestCase(1, 1, 1234, false, 759,
        "0F6A761A1423ACDA683A5CFF3651F11358B33BB41F5901014B50537760B3BE83",
        "8BD0475941E62A1FC15A1AD33F96D09563868CC338E59CF37230310B7D7BA847",
        "442037667A662CD59D892311B86702C0DF5FB831BD2992779DB749C80D414417",
        "31C6438811677F911AF4DB79E8B1BE7CF4DF321836009B7399A1F28FA5DC7535")]
    [TestCase(2, 1, 9876, true, 794,
        "D9613CCA35C09AB44BB1228CE03D1E82B129F09B6C86904631B0CCF53F181B3A",
        "EB068CE67B5C45B74094E4E11DCB9AD6B1577E9F07551744006CFE151FAD05E5",
        "39025ECA94B2370103B24A618EE482AB6E82867E3AF46CF7F4A61C992DA0EBA8",
        "03014CB6D312756FBC210EC02D200312A1F523D78ECE2F61FD159A4E6C2E2075")]
    [TestCase(3, 3, 771177, false, 2355,
        "3AFF496899F1285A066B59410C73EEBA91EEFF9E06925FFCF9DA3B9189AC8A60",
        "15B046981EA0AFD566CF633F5364CF4DF51DF8F3911A83ED47383B95200594EB",
        "E2285DB1CAA76B656C0EB7F6C6DAB9FC8975F364AAA2BEBA694D3470558CCB0F",
        "FF64B0A58B962BDBCF3C1EEEF8C9DA316CC04A1CE47454D6B31750A2A54B4495")]
    public void CompleteConsumersMatchOriginalSource(int x, int z, int seed, bool lakes,
        int objectCount, string objectsHash, string groundHash, string roadsHash, string vehiclesHash)
    {
        var g = FiniteIslandGeneration.Generate(Path.Combine(Root(), "game/content"), temporary, x,z,seed,lakes);
        Assert.That(g.Objects.Placements.Count, Is.EqualTo(objectCount));
        Assert.That(g.Spawned.missing, Is.Zero, "original named art must exist");
        var data = new { spawned=g.Spawned, players=g.Players, spawns=g.Spawns,
            objects=g.Objects.Placements.Select(o => new { o.Name, o.Position, o.Material,
                X=o.Basis.X, Y=o.Basis.Y, Z=o.Basis.Z }).ToArray() };
        Assert.That(Hash(JsonSerializer.SerializeToUtf8Bytes(data, Json)), Is.EqualTo(objectsHash));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(g.Surface.Width); writer.Write(g.Surface.Height);
        for (int gx=0; gx<g.Surface.Width; gx++)
            for (int gz=0; gz<g.Surface.Height; gz++) writer.Write(g.Surface.Grid[gx,gz]);
        for (int gx=0; gx<g.Surface.Width-1; gx++)
            for (int gz=0; gz<g.Surface.Height-1; gz++) writer.Write(g.Surface.Layers[gx,gz]);
        Assert.That(Hash(stream.ToArray()), Is.EqualTo(groundHash));
        string paths=Path.Combine(temporary, "Paths.dat");
        Assert.That(g.Roads.SavePaths(paths), Is.True);
        Assert.That(Hash(File.ReadAllBytes(paths)), Is.EqualTo(roadsHash));
        Assert.That(Hash(JsonSerializer.SerializeToUtf8Bytes(g.Vehicles, Json)), Is.EqualTo(vehiclesHash));
        Assert.That(g.Players, Is.Not.Empty);
        Assert.That(g.Spawns.Loot.All(p => p.Table == 21), Is.True);
    }

    [Test] public void MissingPropDoesNotFabricateAPlacement()
    {
        var objects = new FiniteIslandObjects(temporary);
        Assert.That(objects.Place("not-a-source-prop", new Vector3(1,2,3),
            FiniteIslandObjects.FromEuler(270, 90, 0)), Is.Null);
        Assert.That(objects.Placements, Is.Empty);
    }

    [Test] public void SourceBasisKeepsHandednessTiltAndWorldScale()
    {
        var b = FiniteIslandObjects.FromEuler(270, 90, 0);
        var meshY = b * Vector3.Up;
        Assert.That(meshY.X, Is.EqualTo(-1).Within(1e-6));
        Assert.That(MathF.Abs(meshY.Y)+MathF.Abs(meshY.Z), Is.LessThan(1e-6));
        var tilted = new Basis(new Vector3(1,0,0), .4f) * b;
        var scaled = tilted.Scaled(new Vector3(2,3,4));
        Assert.That(scaled.X.X, Is.EqualTo(tilted.X.X*2));
        Assert.That(scaled.X.Y, Is.EqualTo(tilted.X.Y*3));
        Assert.That(scaled.X.Z, Is.EqualTo(tilted.X.Z*4));
        Assert.That(b.X.Cross(b.Y).Dot(b.Z), Is.EqualTo(1).Within(1e-6));
    }
}
