using NUnit.Framework;
using System.Text.Json;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture]
public sealed class BuildingViewTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IncludeFields = true };
    static string Content()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "game/content"))) return Path.Combine(directory.FullName, "game/content");
        throw new DirectoryNotFoundException();
    }
    static JsonElement Call(BridgeHost host, string op, object input) =>
        JsonSerializer.SerializeToElement(host.Handle(op, JsonSerializer.SerializeToElement(input, Json)), Json);

    [Test] public void ReadOnlySourceViewPreservesGeometryMasksSavedTextAndAuthority()
    {
        using var host = new BridgeHost();
        string contentRoot = Content();
        // No session is needed to project authored data.
        var result = Call(host, "buildingView", new { contentRoot, spec = new { kind = "Residential", seed = 17, minFloors = 2, maxFloors = 2 } });
        Assert.That(result.GetProperty("readOnly").GetBoolean(), Is.True);
        Assert.That(result.GetProperty("version").GetInt32(), Is.EqualTo(1));
        Assert.That(result.GetProperty("walls").GetArrayLength(), Is.EqualTo(74));
        Assert.That(result.GetProperty("ramps").GetArrayLength(), Is.EqualTo(2));
        Assert.That(result.GetProperty("furniture").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(result.GetProperty("walls").EnumerateArray().Sum(w=>w.GetProperty("doorModels").GetArrayLength()),Is.GreaterThan(0));
        Assert.That(result.GetProperty("walls").EnumerateArray().Sum(w=>w.GetProperty("glassModels").GetArrayLength()),Is.GreaterThan(0));
        foreach (var wall in result.GetProperty("walls").EnumerateArray())
        {
            Assert.That(wall.GetProperty("solids").GetProperty("layer").GetUInt32(), Is.EqualTo(1));
            Assert.That(wall.GetProperty("trimSolids").GetProperty("layer").GetUInt32(), Is.EqualTo(64));
            foreach (string mesh in new[] { "front", "back", "trim" })
                Assert.That(wall.GetProperty(mesh).GetProperty("triangles").GetArrayLength() % 3, Is.Zero);
        }
        Call(host, "init", new { contentRoot, spawn = new[] { 0f, 5, 0 } });
        _ = host.State(false); // consume the existing one-shot recipe/catalog projection
        string state = JsonSerializer.Serialize(host.State(false), Json);
        var loaded = Call(host, "buildingView", new { contentRoot, text = result.GetProperty("text").GetString() });
        Assert.That(loaded.GetProperty("text").GetString(), Is.EqualTo(result.GetProperty("text").GetString()));
        Assert.That(loaded.GetProperty("walls").GetArrayLength(), Is.EqualTo(74));
        Assert.That(loaded.GetProperty("ramps").GetArrayLength(), Is.Zero);
        Assert.That(JsonSerializer.Serialize(host.State(false), Json), Is.EqualTo(state), "view cannot register gameplay entities or advance authority");
        Assert.Throws<ArgumentException>(() => Call(host, "buildingView", new { contentRoot, text = "", spec = new { seed = 1 } }));
    }
}
