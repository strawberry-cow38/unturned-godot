using NUnit.Framework;
using System.Text.Json;
using UnturnedBevy;
namespace UnturnedBevyTests;
/// <summary>The umbrella's fall-damage gate (PlayerLife:2399 via CheckFallDamage, UseableCloud's
/// wait for the equip clip) on the server-derived fall damage: a 30 m/s fall lands unhurt with an
/// umbrella that was up (held at least Melee_Equip's 0.633 s) at the landing, even when it was
/// opened after the peak speed, and a tap just before impact is too late.</summary>
[TestFixture, NonParallelizable]
public sealed class UmbrellaTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    const int Umbrella = 1103;
    static string Content() { for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return Path.Combine(d.FullName, "game/content"); throw new DirectoryNotFoundException(); }
    static JsonElement J(object v) => JsonSerializer.SerializeToElement(v, Options);
    static JsonElement Call(BridgeHost h, string op, object args = null) => J(h.Handle(op, J(args ?? new { })));

    /// <summary>Falls 30 m/s for 60 ticks, holding the umbrella from tick <paramref name="openAt"/> (-1 never), then lands.</summary>
    static int Fall(int openAt)
    {
        using var host = new BridgeHost();
        Call(host, "init", new { contentRoot = Content(), spawn = new[] { 0f, 60f, 0f } });
        for (int n = 0; n < 20; n++) Call(host, "tick", new { input = new { position = new[] { 0f, 60f, 0f }, velocity = new[] { 0f, 0f, 0f }, grounded = true, heldItemId = 0 } });
        float y = 60f;
        for (int n = 0; n < 60; n++)
        {
            y -= 30f * 0.02f;
            int held = openAt >= 0 && n >= openAt ? Umbrella : 0;
            Call(host, "tick", new { input = new { position = new[] { 0f, y, 0f }, velocity = new[] { 0f, -30f, 0f }, grounded = false, heldItemId = held } });
        }
        for (int n = 0; n < 10; n++) Call(host, "tick", new { input = new { position = new[] { 0f, y, 0f }, velocity = new[] { 0f, 0f, 0f }, grounded = true, heldItemId = openAt >= 0 ? Umbrella : 0 } });
        return J(host.State()).GetProperty("player").GetProperty("hp").GetInt32();
    }

    [Test]
    public void ALandingWithTheUmbrellaUpIsUnhurtAndATapBeforeImpactIsNot()
    {
        int bare = Fall(-1);
        Assert.That(bare, Is.LessThan(100), "a 30 m/s landing hurts without an umbrella");
        Assert.That(Fall(0), Is.EqualTo(100), "the umbrella was up the whole way");
        Assert.That(Fall(20), Is.EqualTo(100), "opened after the peak speed: the gate reads the multiplier at the landing");
        Assert.That(Fall(55), Is.EqualTo(bare), "0.1 s before impact: the equip clip has not finished, full damage");
    }
}
