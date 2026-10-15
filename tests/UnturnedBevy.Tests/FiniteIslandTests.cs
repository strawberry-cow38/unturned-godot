using Godot;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text.Json;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class FiniteIslandTests
{
    [Test]
    public void GodotAngleAndSmoothStepAdaptersPreserveOriginalFloatResults()
    {
        // GodotSharp 4.6 source uses a once-rounded decimal constant, not 180/float PI.
        Assert.That(Mathf.RadToDeg(MathF.PI / 2), Is.EqualTo(90f));
        Assert.That(Mathf.RadToDeg(MathF.PI), Is.EqualTo(180f));
        Assert.That(Mathf.RadToDeg(MathF.Atan2(-1, -1)), Is.EqualTo(-135f));
        Assert.That(Mathf.RadToDeg(1), Is.EqualTo(57.29578f));
        Assert.That(Mathf.SmoothStep(2f, MathF.BitIncrement(2f), 3f), Is.EqualTo(2f));
        Assert.That(Mathf.SmoothStep(10f, 20f, 15f), Is.EqualTo(.5f));
        Assert.That(Mathf.Sign(0), Is.Zero);
    }

    [Test]
    public void GodotEpsilonBoundariesAndReversedEdgesRetainSourceSemantics()
    {
        // Differentially verified against actual GodotSharp by --math-only.
        Assert.That(Mathf.IsEqualApprox(0, MathF.BitDecrement(1e-6f)), Is.True);
        Assert.That(Mathf.IsEqualApprox(0, 1e-6f), Is.False);
        Assert.That(Mathf.IsEqualApprox(0, 1e-5f), Is.False);
        Assert.That(Mathf.IsEqualApprox(100, 100.00005f), Is.True);
        Assert.That(Mathf.IsEqualApprox(100, 100.00011f), Is.False);
        Assert.That(Mathf.IsEqualApprox(-100, -100.00005f), Is.True);
        Assert.That(Mathf.IsEqualApprox(-100, -100.00011f), Is.False);
        Assert.That(Mathf.SmoothStep(-2, -2, 3), Is.EqualTo(-2));
        Assert.That(Mathf.SmoothStep(2, -2, 0), Is.EqualTo(.5f));
        Assert.That(Mathf.SmoothStep(2, -2, -3), Is.EqualTo(1));
        Assert.That(Mathf.SmoothStep(2, -2, 3), Is.Zero);
        Assert.Throws<ArithmeticException>(() => Mathf.Sign(float.NaN));
    }

    // Numerical source fixtures produced by verify_island_source.py using the
    // unchanged generator and real GodotSharp, not values from the bridge shim.
    // They cover every grid sample and all authored road/building/camp records.
    [TestCase(1, 1, 1234, false,
        "0BB3138C692AF50EF4C2C97F5A39B7FF4AAA8546D5E86FB8C997646A06498982",
        "E3FF8146D7CA12B12AB834C33D2840063321144F6B43E38F3C5741BEC2F7892B")]
    [TestCase(2, 1, 9876, true,
        "C49F5CB45D1C368F3BF715DAAD0025D6D1574CAF2101E5029A1D8AA39C59B70D",
        "83E2779072A06BDFED65514D7E3EC49E59A1D7D83AB517F7C7D3EAA4B0D9B741")]
    [TestCase(3, 3, 771177, false,
        "A732D5A13DEE56E2612726BAA86FC456FC0C35BE14C9BFCE1FE5E564701D0D6F",
        "55155756FA52C8901AFEF0CB468A150818175AEFA2296B6F5839116F5B03F88A")]
    public void CompleteLayoutMatchesOriginalGodotMath(int x, int z, int seed, bool lakes,
        string heights, string graph)
    {
        var layout = new FiniteIslandLayout(x, z);
        var first = Fingerprint(layout, x, z, seed, lakes);
        Assert.That(first, Is.EqualTo((heights, graph)));
        if (x == 1)
        {
            // Generate again on the same holder after a different seed: source
            // lists/static town-pad cache must not retain the preceding layout.
            layout.GenerateIsland(555, true);
            Assert.That(Fingerprint(layout, x, z, seed, lakes), Is.EqualTo(first));
        }
    }

    static (string, string) Fingerprint(FiniteIslandLayout layout, int x, int z, int seed, bool lakes)
    {
        var pois = layout.GenerateIsland(seed, lakes);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(layout.Width); writer.Write(layout.Height);
        for (int gx=0; gx<layout.Width; gx++)
            for (int gz=0; gz<layout.Height; gz++) writer.Write(layout.Grid[gx,gz]);
        var data = new { x, z, seed, lakes, pois, layout.Links, layout.Connectors,
            layout.Tiles, layout.Buildings, layout.Routes, layout.Trails,
            layout.Camps, layout.Landmarks };
        var json = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { IncludeFields = true });
        return (Convert.ToHexString(SHA256.HashData(stream.ToArray())),
            Convert.ToHexString(SHA256.HashData(json)));
    }

    [TestCase(0, 1), TestCase(1, 0), TestCase(-1, 1), TestCase(1, 9)]
    public void InvalidDimensionsFailBeforeAllocating(int x, int z) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FiniteIslandLayout(x, z));
}
