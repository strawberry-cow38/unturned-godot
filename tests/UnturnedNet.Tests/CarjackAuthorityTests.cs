using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;
namespace UnturnedNet.Tests;
/// <summary>CommandCarjack (56) at core's choke point: the server shoves only for a
/// living sender who really has the tool, at an empty car in reach. The jack is a tool
/// (PlayerController.ApplyCarjack: "The jack is NOT consumed"), so it is never spent.</summary>
[TestFixture]
public sealed class CarjackAuthorityTests
{
    const ushort Jack = 277;
    [Test]
    public void OnlyALivingOwnerOfACarjackCanLaunchAnEmptyCarInReach()
    {
        TransactionalFixtures.RegisterAssets();
        Assets.add(new ItemAsset { id = Jack, itemName = "Carjack", size_x = 2, size_y = 1 });
        var h = new TransactionalHarness(7911).Connected("owner");
        var client = h.Clients[0];
        Assert.That(h.Server.Transactions.CarjackItemId, Is.EqualTo(Jack), "the host wires retail's Carjack id");
        var shoves = new List<(uint Car, ushort Sender)>();
        h.Server.VehicleHost.ApplyCarjackForce = (car, sender) => shoves.Add((car, sender));
        Assert.That(h.Server.Players.TryGetByOwner(client.PlayerId, out var me), Is.True);
        var car = h.Server.Ids.Mint();
        h.Server.Vehicles.ServerSpawn(car, 3, 0, me.Pos + new Vector3(3, 0, 0), h.Server.Session.CurrentTick);
        void Jack56() { client.SendCarjack(car.Value); h.Step(6); }

        Jack56();
        Assert.That(shoves, Is.Empty, "a forged 56 without a carjack in the bag");
        var tool = new Item(Jack);
        h.Grant(client.PlayerId, tool);
        Assert.That(h.Server.CombatState.TryGet(client.PlayerId, out var life), Is.True);
        life.Alive = false;
        Jack56();
        Assert.That(shoves, Is.Empty, "a dead sender");
        life.Alive = true;
        Jack56();
        Assert.That(shoves, Is.EqualTo(new[] { (car.Value, client.PlayerId) }), "alive, owns the tool, empty car in reach");
        Assert.That(h.Server.Inventories.TryGet(client.PlayerId, out var bag) && bag.Inventory.getItemCount(Jack) == 1, Is.True,
            "the jack is a tool: never spent");
    }
}
