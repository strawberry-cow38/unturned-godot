using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using NUnit.Framework;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture]
public sealed class BuildingDecorTests
{
    static string Root()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
    static float[] V(Vector3 value) => [value.X, value.Y, value.Z];
    static string Hash(IEnumerable<float> values)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            foreach (float value in values) writer.Write(value);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }
    [Test]
    public void OriginalGodotCurtainAndRugFactoriesKeepEveryRawAttributeAndRgbaByte()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests/UnturnedBevy.Tests/fixtures/building-decor-godot.json")));
        int cases = 0;
        foreach (var row in oracle.RootElement.EnumerateArray())
        {
            BuildingDecor.ClearCache();
            var input = row.GetProperty("input");
            float width = input.GetProperty("width").GetSingle(), height = input.GetProperty("height").GetSingle();
            bool curtain = input.GetProperty("kind").GetString() == "curtain";
            BuildingDecorMesh mesh;
            if (curtain)
            {
                var t = input.GetProperty("tint");
                Color? tint = t.ValueKind == JsonValueKind.Null ? null : new Color(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle(), t[3].GetSingle());
                mesh = BuildingDecor.CreateCurtainMesh(width, height, input.GetProperty("open").GetSingle(), tint);
                Assert.That(BuildingDecor.CurtainRailY(width, height), Is.EqualTo(row.GetProperty("railY").GetSingle()));
                Assert.That(BuildingDecor.CurtainClothTopY(width, height), Is.EqualTo(row.GetProperty("clothTopY").GetSingle()));
            }
            else mesh = BuildingDecor.CreateRugMesh(width, height, Enum.Parse<BuildingDecor.RugStyle>(input.GetProperty("style").GetString()));
            string label = $"case {cases++}: {input}";
            Assert.That(mesh.Vertices.Length, Is.EqualTo(row.GetProperty("vertices").GetInt32()), label);
            Assert.That(Hash(mesh.Vertices.SelectMany(V)), Is.EqualTo(row.GetProperty("vertexHash").GetString()), label + " vertices");
            Assert.That(Hash(mesh.Normals.SelectMany(V)), Is.EqualTo(row.GetProperty("normalHash").GetString()), label + " normals");
            Assert.That(Hash(mesh.Uv.SelectMany(p => new[] { p.X, p.Y })), Is.EqualTo(row.GetProperty("uvHash").GetString()), label + " UV");
            Assert.That(Hash(mesh.Colors.SelectMany(c => new[] { c.R, c.G, c.B, c.A })), Is.EqualTo(row.GetProperty("colorHash").GetString()), label + " colors");
            var texture = row.GetProperty("texture");
            if (texture.ValueKind == JsonValueKind.Null) Assert.That(mesh.Material.AlbedoTexture, Is.Null);
            else
            {
                var image = mesh.Material.AlbedoTexture;
                Assert.That(image.Width, Is.EqualTo(texture.GetProperty("width").GetInt32()));
                Assert.That(image.Height, Is.EqualTo(texture.GetProperty("height").GetInt32()));
                Assert.That(Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant(), Is.EqualTo(texture.GetProperty("hash").GetString()), label + " actual RGBA8");
            }
            Assert.That(mesh.Material.VertexColorUseAsAlbedo, Is.True);
            Assert.That(mesh.Material.Roughness, Is.EqualTo(1));
            Assert.That(mesh.Material.DoubleSided, Is.EqualTo(row.GetProperty("doubleSided").GetBoolean()), label + " cull mode");
            Assert.That(mesh.Material.NearestFilter, Is.EqualTo(texture.ValueKind != JsonValueKind.Null), label + " texture filter");
        }
        Assert.That(cases, Is.EqualTo(36));
    }

    [Test]
    public void SourceDecorCacheRetainsLiveValuesAndRejectsInvalidSavedDimensions()
    {
        BuildingDecor.ClearCache();
        var first = BuildingDecor.CreateCurtainMesh(2, 2, false);
        var vertices = first.Vertices.ToArray();
        Assert.That(BuildingDecor.CreateCurtainMesh(2, 2, false), Is.SameAs(first));
        for (int i = 0; i < 129; i++) BuildingDecor.CreateRugMesh(1 + i * .05f, 2);
        Assert.That(BuildingDecor.CreateCurtainMesh(2, 2, false), Is.Not.SameAs(first));
        Assert.That(first.Vertices, Is.EqualTo(vertices), "eviction changes cache ownership, not a live mesh");
        foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => BuildingDecor.CreateCurtainMesh(bad, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingDecor.CreateCurtainMesh(1, 1, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingDecor.CreateRugMesh(1, 1, (BuildingDecor.RugStyle)99));
    }
}
