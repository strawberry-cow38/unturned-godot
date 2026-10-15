using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class EmptyFuelTests
{
    static NetPakReader Reader(byte[] bytes) { var reader = new NetPakReader(); reader.SetBufferSegment(bytes, bytes.Length); return reader; }
    static byte[] Pack(EmptyFuelCommand command) => NetMessagePak.Pack(1, command.Write)[1..];
    [Test]
    public void EmptyFuelWireIsBoundedAndTruncationNeverPartiallyPublishesAnAddress()
    {
        var c = new EmptyFuelCommand { Address = new(6, 13, 15, 28), Token = ulong.MaxValue };
        var bytes = Pack(c); Assert.That(EmptyFuelCommand.TryRead(Reader(bytes), out var actual), Is.True);
        Assert.That(actual.Address, Is.EqualTo(c.Address)); Assert.That(actual.Token, Is.EqualTo(c.Token));
        for (int n = 0; n < bytes.Length; n++) {
            Assert.That(EmptyFuelCommand.TryRead(Reader(bytes[..n]), out actual), Is.False);
            Assert.That(actual.Token, Is.Zero); Assert.That(actual.Address, Is.EqualTo(default(EquipmentAddress)));
        }
        foreach (var bad in new[] { new EmptyFuelCommand { Address = new(7, 0, 0, 28), Token = 1 },
            new EmptyFuelCommand { Address = new(2, 0, 0, 0), Token = 1 },
            new EmptyFuelCommand { Address = new(2, 0, 0, 28), Token = 0 } })
            Assert.That(EmptyFuelCommand.TryRead(Reader(Pack(bad)), out _), Is.False);
    }
}

[TestFixture]
public sealed class SalvageItemWireTests
{
    [Test]
    public void ExactSalvageDoesNotAcceptContainersZeroIdentityOrTruncatedPayload()
    {
        var c = new SalvageItemCommand { Address = new(2, 5, 7, 224), Token = 0x123456789abcdefUL };
        byte[] Pack(SalvageItemCommand value) => NetMessagePak.Pack(1, value.Write)[1..];
        bool Read(byte[] bytes, out SalvageItemCommand value) {
            var r = new NetPakReader(); r.SetBufferSegment(bytes, bytes.Length);
            return SalvageItemCommand.TryRead(r, out value);
        }
        byte[] bytes = Pack(c); Assert.That(Read(bytes, out var actual), Is.True);
        Assert.That(actual.Address, Is.EqualTo(c.Address)); Assert.That(actual.Token, Is.EqualTo(c.Token));
        for (int i = 0; i < bytes.Length; i++) {
            Assert.That(Read(bytes[..i], out actual), Is.False); Assert.That(actual.Token, Is.Zero);
            Assert.That(actual.Address, Is.EqualTo(default(EquipmentAddress)));
        }
        foreach (var bad in new[] { new SalvageItemCommand { Address = new(7, 0, 0, 224), Token = 1 },
            new SalvageItemCommand { Address = new(2, 0, 0, 0), Token = 1 },
            new SalvageItemCommand { Address = new(2, 0, 0, 224), Token = 0 } })
            Assert.That(Read(Pack(bad), out _), Is.False);
    }
}
