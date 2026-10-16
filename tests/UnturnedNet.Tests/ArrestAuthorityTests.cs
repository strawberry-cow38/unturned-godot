using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;
namespace UnturnedNet.Tests;
/// <summary>Commands 52/53/54 at core's choke point (ServerTransactions). The source rules: only a
/// SURRENDERING target in reach is cuffed, the cuffs leave the captor's bag, a key in anyone's hand frees
/// and hands the cuffs back, and a struggle only counts when the lean side alternates. The Bevy port review
/// adds that the restraint or key must be in the sender's authoritative bag (HeldId is client-reported) and
/// that neither a dead captor/freer nor a dead target takes part.</summary>
[TestFixture]
public sealed class ArrestAuthorityTests
{
    TransactionalHarness _h;
    NetWorldClient _captor, _victim;
    PlayerCombatReplication.CombatEntity _c, _v;

    [SetUp]
    public void Setup()
    {
        TransactionalFixtures.RegisterAssets();
        Assets.add(new ItemAsset { id = ArrestDef.Handcuffs, itemName = "Handcuffs", size_x = 1, size_y = 1 });
        Assets.add(new ItemAsset { id = ArrestDef.HandcuffsKey, itemName = "Handcuffs Key", size_x = 1, size_y = 1 });
        Assets.add(new ItemAsset { id = ArrestDef.CableTie, itemName = "Cable Tie", size_x = 1, size_y = 1 });
        _h = new TransactionalHarness(4321).Connected("captor", "victim");
        _captor = _h.Clients[0]; _victim = _h.Clients[1];
        Assert.That(_h.Server.CombatState.TryGet(_captor.PlayerId, out _c) && _h.Server.CombatState.TryGet(_victim.PlayerId, out _v), Is.True);
    }

    int Count(ushort player, ushort id) => _h.Server.Inventories.TryGet(player, out var e) ? e.Inventory.getItemCount(id) : -1;
    bool Arrested => _v.Gesture == (byte)EPlayerGesture.ARREST_START && _h.Server.Transactions.IsArrested(_victim.PlayerId);
    void Arrest() { _captor.SendArrestPlayer(_victim.PlayerId); _h.Step(6); }

    [Test]
    public void OnlyALivingCaptorWhoOwnsTheHeldRestraintCuffsASurrenderingTarget()
    {
        _c.HeldId = ArrestDef.Handcuffs;
        _v.Gesture = (byte)EPlayerGesture.SURRENDER_START;
        Arrest();
        Assert.That(Arrested, Is.False, "a forged HeldId: no handcuffs in the captor's bag");
        _h.Grant(_captor.PlayerId, new Item(ArrestDef.Handcuffs));
        _v.Gesture = 0;
        Arrest();
        Assert.That(Arrested, Is.False, "UseableArrestStart: the target must be surrendering");
        _v.Gesture = (byte)EPlayerGesture.SURRENDER_START;
        _c.Alive = false; Arrest(); _c.Alive = true;
        Assert.That(Arrested, Is.False, "a dead captor");
        _v.Alive = false; Arrest(); _v.Alive = true;
        Assert.That(Arrested, Is.False, "a dead target");
        Arrest();
        Assert.That(Arrested, Is.True, "living, owned, surrendering, in reach");
        Assert.That(Count(_captor.PlayerId, ArrestDef.Handcuffs), Is.Zero, "the cuffs leave the captor's bag");
    }

    [Test]
    public void AKeyInAnyLivingHandFreesAndReturnsTheCuffsAndATieIsSixtyFourAlternatingStruggles()
    {
        _h.Grant(_captor.PlayerId, new Item(ArrestDef.Handcuffs));
        _c.HeldId = ArrestDef.Handcuffs; _v.Gesture = (byte)EPlayerGesture.SURRENDER_START;
        Arrest(); Assert.That(Arrested, Is.True);
        _c.HeldId = ArrestDef.HandcuffsKey;
        _captor.SendUnlockArrest(_victim.PlayerId); _h.Step(6);
        Assert.That(Arrested, Is.True, "a forged key (none in the bag) frees nobody");
        _h.Grant(_captor.PlayerId, new Item(ArrestDef.HandcuffsKey));
        _c.Alive = false; _captor.SendUnlockArrest(_victim.PlayerId); _h.Step(6); _c.Alive = true;
        Assert.That(Arrested, Is.True, "a dead freer");
        _captor.SendUnlockArrest(_victim.PlayerId); _h.Step(6);
        Assert.That(Arrested, Is.False);
        Assert.That(_v.Gesture, Is.Zero, "ARREST_STOP");
        Assert.That(Count(_captor.PlayerId, ArrestDef.Handcuffs), Is.EqualTo(1), "Recover: the cuffs come back to the freer");
        Assert.That(Count(_captor.PlayerId, ArrestDef.HandcuffsKey), Is.EqualTo(1), "the key is kept");

        _h.Grant(_captor.PlayerId, new Item(ArrestDef.CableTie));
        _c.HeldId = ArrestDef.CableTie; _v.Gesture = (byte)EPlayerGesture.SURRENDER_START;
        Arrest(); Assert.That(Arrested, Is.True);
        for (int n = 0; n < 63; n++) { _victim.SendStruggle((byte)(n & 1)); _h.Step(); }
        _victim.SendStruggle(0); _h.Step();   // same side as the last accepted one: refused
        _h.Step(4);
        Assert.That(Arrested, Is.True, "63 alternating struggles and one repeat are not 64");
        _victim.SendStruggle(1); _h.Step(4);
        Assert.That(Arrested, Is.False, "the 64th alternating lean breaks a cable tie");
        Assert.That(_v.Gesture, Is.Zero);
    }

    void Cuff()
    {
        _h.Grant(_captor.PlayerId, new Item(ArrestDef.Handcuffs));
        _c.HeldId = ArrestDef.Handcuffs; _v.Gesture = (byte)EPlayerGesture.SURRENDER_START;
        Arrest(); Assert.That(Arrested, Is.True);
    }

    /// <summary>Review: the hands-bound rule on the authority. A modded cuffed client's swing, and its select of
    /// anything but empty hands (command 71), are refused; the same swing lands once the cuffs are off.</summary>
    [Test]
    public void ACuffedPlayerCannotSwingOrTakeAnythingIntoTheHandOverTheWire()
    {
        _h.PumpCombatState = true;
        var selected = new List<ushort>();
        _h.Server.SelectHeldRequested = (owner, cmd) => selected.Add(cmd.Address.Id);
        long Swing() { long before = _h.Server.Combat.Diag.MeleeAccepted; _victim.SendMelee(false, 0f); _h.Step(30); return _h.Server.Combat.Diag.MeleeAccepted - before; }
        Cuff();
        Assert.That(Swing(), Is.EqualTo(0L), "a cuffed swing is refused");
        _victim.SendSelectHeld(2, 0, 0, 13, 99); _h.Step(6);
        Assert.That(selected, Is.Empty, "a cuffed select is refused");
        _victim.SendSelectHeld(2, 0, 0, 0, 0); _h.Step(6);
        Assert.That(selected, Is.EqualTo(new ushort[] { 0 }), "empty hands stay allowed");
        _h.Server.Transactions.ReleaseArrest(_victim.PlayerId);
        Assert.That(Swing(), Is.EqualTo(1L), "free again: the same swing lands");
    }

    /// <summary>Review: neither death nor leaving keeps the cuffs, so a respawn is free and a recycled id inherits
    /// nothing.</summary>
    [Test]
    public void DeathAndDisconnectReleaseTheCuffs()
    {
        Cuff();
        _h.Server.Combat.DamagePlayerExternal(_victim.PlayerId, 9999f);
        _h.Step(6);
        Assert.That(_v.Alive, Is.False);
        Assert.That((Arrested, _h.Server.Transactions.IsArrested(_victim.PlayerId), _v.Gesture), Is.EqualTo((false, false, (byte)0)), "death releases");
        _v.Alive = true;
        Cuff();
        _victim.Disconnect();
        _h.StepUntil(() => !_h.Server.Transactions.IsArrested(_victim.PlayerId), 400);
        Assert.That(_h.Server.Transactions.IsArrested(_victim.PlayerId), Is.False, "leaving releases");
    }
}
