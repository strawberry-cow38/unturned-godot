using NUnit.Framework;
using System.Text.Json;
using UnturnedBevy;
namespace UnturnedBevyTests;
/// <summary>The umbrella's fall-damage gate (PlayerLife:2399 via CheckFallDamage, UseableCloud's
/// wait for the equip clip) on the server-derived fall damage. A 30 m/s fall lands unhurt when an
/// umbrella from the bag was up at the landing (held at least Melee_Equip's 0.633 s, after which the
/// source caps the descent at 4.9 m/s), even when opened after the peak speed. A tap just before impact,
/// an umbrella claimed in the hand but absent from the bag, and a landing still faster than the cap all hurt.</summary>
[TestFixture, NonParallelizable]
public sealed class UmbrellaTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    const int Umbrella = 1103;
    const int EquipTicks = 32;   // Melee_Equip 0.633 s at 50 Hz
    static string Content() { for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return Path.Combine(d.FullName, "game/content"); throw new DirectoryNotFoundException(); }
    static JsonElement J(object v) => JsonSerializer.SerializeToElement(v, Options);
    static JsonElement Call(BridgeHost h, string op, object args = null) => J(h.Handle(op, J(args ?? new { })));

    /// <summary>Falls for 70 ticks, holding the umbrella from tick <paramref name="openAt"/> (-1 never). With
    /// <paramref name="glide"/> the descent drops to the source's 4.9 m/s cap once the equip clip is done, as
    /// PlayerMovementSim does; without it the claimed descent stays at 30 m/s.</summary>
    static int Fall(int openAt, bool inBag = true, bool glide = true)
    {
        using var host = new BridgeHost();
        Call(host, "init", new { contentRoot = Content(), spawn = new[] { 0f, 60f, 0f }, allowCheats = true });
        if (inBag) Call(host, "action", new { name = "console", args = new { text = $"give {Umbrella}" } });
        for (int n = 0; n < 20; n++) Call(host, "tick", new { input = new { position = new[] { 0f, 60f, 0f }, velocity = new[] { 0f, 0f, 0f }, grounded = true, heldItemId = 0 } });
        float y = 60f;
        for (int n = 0; n < 70; n++)
        {
            bool up = openAt >= 0 && n >= openAt;
            float speed = glide && up && n >= openAt + EquipTicks + 2 ? 4.905f : 30f;
            y -= speed * 0.02f;
            Call(host, "tick", new { input = new { position = new[] { 0f, y, 0f }, velocity = new[] { 0f, -speed, 0f }, grounded = false, heldItemId = up ? Umbrella : 0 } });
        }
        for (int n = 0; n < 10; n++) Call(host, "tick", new { input = new { position = new[] { 0f, y, 0f }, velocity = new[] { 0f, 0f, 0f }, grounded = true, heldItemId = openAt >= 0 ? Umbrella : 0 } });
        return J(host.State()).GetProperty("player").GetProperty("hp").GetInt32();
    }

    [Test]
    public void ALandingWithTheUmbrellaUpIsUnhurtAndEveryWayAroundTheGateIsNot()
    {
        int bare = Fall(-1);
        Assert.That(bare, Is.LessThan(100), "a 30 m/s landing hurts without an umbrella");
        Assert.That(Fall(10), Is.EqualTo(100), "opened after the peak speed, gliding at the landing: exempt");
        Assert.That(Fall(10, inBag: false), Is.EqualTo(bare), "an umbrella claimed in the hand but not in the bag");
        Assert.That(Fall(10, glide: false), Is.EqualTo(bare), "a landing faster than the umbrella's cap was not under it");
        Assert.That(Fall(66), Is.EqualTo(bare), "0.08 s before impact: the equip clip has not finished");
    }

    [Test]
    public void GravityMultiplierInputMustBeAFiniteFraction()
    {
        using var host = new BridgeHost();
        Call(host, "init", new { contentRoot = Content(), spawn = new[] { 0f, 1f, 0f } });
        Assert.DoesNotThrow(() => Call(host, "simulate", new { input = new { gravityMultiplier = 0.25f } }));
        Assert.Throws<ArgumentException>(() => Call(host, "simulate", new { input = new { gravityMultiplier = -1f } }));
        Assert.Throws<ArgumentException>(() => Call(host, "simulate", new { input = new { gravityMultiplier = 1.5f } }));
    }
}
