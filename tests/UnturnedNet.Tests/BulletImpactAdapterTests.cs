using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

public class BulletImpactAdapterTests
{
    sealed class Harness
    {
        readonly MemNetwork net = new(983);
        public readonly NetWorldServer Server;
        public readonly NetWorldClient Client;
        public Harness()
        {
            Server = new(new MemServerTransport(net)); Server.SpawnProvider = _ => Vector3.zero;
            Client = new(new MemClientTransport(net), "Warhead"); Client.Connect(); Step(25);
            Assert.That(Client.State, Is.EqualTo(NetSessionState.Connected));
        }
        public void Step(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (Client.State == NetSessionState.Connected)
                    Client.SendPlayerState(Vector3.zero, 0, 0, Vector3.zero, 0, true, 0);
                net.Tick(); Client.Tick(); Server.TickSimulation(); Server.TickReplication();
            }
        }
    }

    [Test]
    public void Accepted_round_captures_payload_before_use_and_emits_once_after_direct_damage()
    {
        var h = new Harness(); var combat = h.Server.Combat;
        combat.DefaultGun.MuzzleVelocity = 10; combat.DefaultGun.GravityMultiplier = 0; combat.DefaultGun.BallisticSteps = 100;
        object selected = "original warhead";
        combat.CaptureBulletContext = _ => selected;
        combat.ShotAccepted = (_, _) => selected = "empty weapon";
        var order = new List<string>(); var contacts = new List<CombatBulletImpact>();
        combat.WorldRayDetailed = (Vector3 from, Vector3 to, out CombatWorldHit hit) => {
            hit = new(new Vector3(0, 1.5f, -5), 7, normal: new Vector3(0, 0, 1));
            return from.z >= -5 && to.z <= -5;
        };
        combat.DamageObject = (_, _, _) => { order.Add("direct"); return false; };
        combat.BulletImpact = hit => { order.Add("impact"); contacts.Add(hit); };
        h.Client.SendFire(new(100, 1.5f, 0), new(0, 0, -1)); h.Step(5);
        Assert.That(contacts, Is.Empty); Assert.That(combat.Diag.ShotsAccepted, Is.Zero);
        h.Client.SendFire(new(0, 1.5f, 0), new(0, 0, -1)); h.Step(5);
        selected = "switched rifle"; h.Step(60);
        Assert.That(order, Is.EqualTo(new[] { "direct", "impact" }));
        Assert.That(contacts, Has.Count.EqualTo(1));
        Assert.That(contacts[0].Context, Is.EqualTo("original warhead"));
        Assert.That(contacts[0].Normal, Is.EqualTo(new Vector3(0, 0, 1)));
        Assert.That(contacts[0].Velocity.z, Is.EqualTo(-10));
        Assert.That(combat.LiveBullets, Is.Zero);
    }

    [Test]
    public void Weapon_blast_shares_damage_and_host_adapter_without_fabricating_grenade_event()
    {
        var h = new Harness(); int grenadeFacts = 0; CombatBlast? host = null;
        h.Client.GrenadeExploded += _ => grenadeFacts++;
        h.Server.Combat.DamageHostBlast = blast => host = blast;
        h.Server.Combat.ServerDetonateWeaponAt(new(0, 0, -2), 10, 100, 70, 55, h.Client.PlayerId, h.Server.Session.CurrentTick);
        h.Step(10);
        Assert.That(h.Client.CombatState.TryGet(h.Client.PlayerId, out var life), Is.True);
        Assert.That(life.Health, Is.EqualTo(4)); // source squared falloff: 1 - (2/10)^2
        Assert.That(host.HasValue, Is.True); Assert.That(host.Value.VehicleDamage, Is.EqualTo(55));
        Assert.That(host.Value.AnimalDamage, Is.EqualTo(70)); Assert.That(host.Value.Attacker, Is.EqualTo(h.Client.PlayerId));
        Assert.That(grenadeFacts, Is.Zero);
        h.Server.Combat.ServerDetonateAt(new(0, 0, -100), 1, 1, h.Server.Session.CurrentTick);
        h.Step(10); Assert.That(grenadeFacts, Is.EqualTo(1), "existing grenade/microwave fact remains unchanged");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Optional_continuation_spends_steps_reduces_damage_and_skips_gravity_on_contact(bool enabled)
    {
        var h = new Harness(); var combat = h.Server.Combat;
        combat.DefaultGun.MuzzleVelocity = 100; combat.DefaultGun.BallisticSteps = 2;
        combat.DefaultGun.GravityMultiplier = 10;
        var contacts = new List<CombatBulletImpact>(); var damage = new List<float>();
        combat.WorldRayDetailed = (Vector3 from, Vector3 to, out CombatWorldHit hit) => {
            int plane = from.z > -1 ? 1 : from.z > -2 ? 2 : 3;
            hit = new(new Vector3(0, 1.5f, -plane), plane, normal: new Vector3(0, 0, 1), thin: true);
            return to.z <= -plane;
        };
        combat.BulletImpact = contacts.Add;
        combat.DamageObject = (_, amount, _) => { damage.Add(amount); return false; };
        if (enabled) combat.ContinueBullet = _ => new CombatBulletContinuation(.75f, .7f, .06f);
        h.Client.SendFire(new(0, 1.5f, 0), new(0, 0, -1)); h.Step(10);
        Assert.That(contacts.Count, Is.EqualTo(enabled ? 2 : 1));
        Assert.That(combat.LiveBullets, Is.Zero);
        Assert.That(damage[0], Is.EqualTo(25));
        if (enabled)
        {
            Assert.That(damage[1], Is.EqualTo(17.5f));
            Assert.That(contacts[1].Velocity.z, Is.EqualTo(-75));
            Assert.That(contacts[1].Velocity.y, Is.Zero, "source piercing spends a step without applying gravity");
            Assert.That(contacts[1].Pierced, Is.EqualTo(1));
            Assert.That(combat.Diag.BulletsExpired, Is.EqualTo(1));
        }
    }
}
