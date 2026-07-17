using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // MP_PLAN §4 Phase 5, the L0 combat battery: the Fire command carries the client's aim ray and the
    // SERVER steps the bullet against server positions with the zone multipliers -- so abuse (fake muzzle
    // origins, fire-rate spam, empty-mag firing) is REJECTED at the validation choke point, kills and
    // credit are server-decided facts every client agrees on (kill-credit sync), melee lands on a
    // server-side deferred timer, and grenades are server-spawned entities that snap while flying and
    // explode by event. All deterministic MemTransport sims -- no sockets, no sleeps.
    [TestFixture]
    public class ServerCombatTests
    {
        sealed class Harness
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public readonly List<NetWorldClient> Clients = new();

            public Harness(int seed)
            {
                Net = new MemNetwork(seed);
                Server = new NetWorldServer(new MemServerTransport(Net));
            }

            public NetWorldClient AddClient(string name)
            {
                var c = new NetWorldClient(new MemClientTransport(Net), name);
                Clients.Add(c);
                c.Connect();
                return c;
            }

            // one 50 Hz tick in §2.5 order: inputs (caller), transport, client sessions, server sim
            // (receive/input-apply/player-sim/combat), replication LAST
            public void Step(System.Action perTickInputs = null)
            {
                perTickInputs?.Invoke();
                Net.Tick();
                foreach (var c in Clients) c.Tick();
                Server.TickSimulation();
                Server.TickReplication();
            }

            public void Step(int ticks, System.Action perTickInputs = null)
            {
                for (int i = 0; i < ticks; i++) Step(perTickInputs);
            }

            public Harness Connected(params string[] names)
            {
                foreach (var n in names) AddClient(n);
                Step(25);
                foreach (var c in Clients)
                    Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), $"client connected (seed={Net.Seed})");
                return this;
            }
        }

        sealed class MockZombieHost : IZombieHost
        {
            public readonly Dictionary<uint, float> Hp = new();
            public int Hits;

            public bool DamageZombie(uint zombieNetId, float damage, Vector3 point, Vector3 dir, ushort attackerPlayerId, bool headshot)
            {
                if (!Hp.TryGetValue(zombieNetId, out float hp) || hp <= 0f) return false;
                Hits++;
                hp -= damage;
                Hp[zombieNetId] = hp;
                return hp <= 0f;
            }
        }

        static Vector3 Eye(Vector3 feet) => feet + new Vector3(0f, 1.5f, 0f);
        static Vector3 AimAt(Vector3 from, Vector3 at) => (at - from).normalized;

        // ---------------------------------------------------------------- validation (the abuse gates)

        [Test]
        public void Fire_FakeMuzzleOrigin_IsRejected_OutOfRange()
        {
            var h = new Harness(50101).Connected("shooter");
            var a = h.Clients[0];
            int ammo0 = h.Server.Combat.AmmoOf(a.PlayerId);

            a.SendFire(new Vector3(50f, 1.5f, 0f), new Vector3(0f, 0f, 1f));   // muzzle claimed 50 m from the avatar
            h.Step(10);

            Assert.That(h.Server.Combat.Diag.ShotsRejectedRange, Is.EqualTo(1), "teleported-muzzle shot rejected");
            Assert.That(h.Server.Combat.Diag.ShotsAccepted, Is.EqualTo(0));
            Assert.That(h.Server.Combat.AmmoOf(a.PlayerId), Is.EqualTo(ammo0), "a rejected shot costs no ammo");

            // a legitimate origin (at the avatar's eye) fires fine afterwards
            h.Server.Players.TryGetByOwner(a.PlayerId, out var pe);
            a.SendFire(Eye(pe.Pos), new Vector3(0f, 0f, 1f));
            h.Step(10);
            Assert.That(h.Server.Combat.Diag.ShotsAccepted, Is.EqualTo(1), "honest shot accepted");
        }

        [Test]
        public void Fire_RateAbuse_IsRejected_AtTheFireratePlusOneGap()
        {
            var h = new Harness(50102).Connected("spammer");
            var a = h.Clients[0];
            h.Server.Players.TryGetByOwner(a.PlayerId, out var pe);
            var origin = Eye(pe.Pos);

            h.Step(30, () => a.SendFire(origin, new Vector3(0f, 0f, 1f)));   // trigger held EVERY tick
            h.Step(10);

            var d = h.Server.Combat.Diag;
            // Firerate 4 -> the SP rule (min gap = Firerate+1 ticks) allows ~1 shot per 5 ticks of the burst
            Assert.That(d.ShotsAccepted, Is.InRange(5, 7), $"~30/5 shots pass ({d.ShotsAccepted})");
            Assert.That(d.ShotsRejectedRate, Is.GreaterThanOrEqualTo(20), $"the spam is rejected ({d.ShotsRejectedRate})");
            Assert.That(d.ShotsAccepted + d.ShotsRejectedRate, Is.EqualTo(30), "every command was adjudicated");
        }

        [Test]
        public void Fire_EmptyMagazine_IsRejected_UntilAServerTimedReload()
        {
            var h = new Harness(50103);
            h.Server.Combat.DefaultGun = new ServerGunProfile { MagCapacity = 3 };   // before connect: spawn ammo = 3
            h.Connected("shooter");
            var a = h.Clients[0];
            h.Server.Players.TryGetByOwner(a.PlayerId, out var pe);
            var origin = Eye(pe.Pos);
            var dir = new Vector3(0f, 0f, 1f);

            int tick = 0;
            h.Step(30, () => { if (tick++ % 7 == 0) a.SendFire(origin, dir); });   // 5 well-spaced trigger pulls
            Assert.That(h.Server.Combat.Diag.ShotsAccepted, Is.EqualTo(3), "the 3-round magazine empties");
            Assert.That(h.Server.Combat.Diag.ShotsRejectedAmmo, Is.EqualTo(2), "dry-fire spam is rejected");
            Assert.That(h.Server.Combat.AmmoOf(a.PlayerId), Is.EqualTo(0));

            a.SendReload();
            h.Step(10);
            a.SendFire(origin, dir);   // still mid-reload -> rejected
            h.Step(5);
            Assert.That(h.Server.Combat.Diag.ShotsRejectedReloading, Is.EqualTo(1), "firing mid-reload is rejected");

            h.Step(h.Server.Combat.DefaultGun.ReloadTicks);   // let the server-timed reload finish
            Assert.That(h.Server.Combat.AmmoOf(a.PlayerId), Is.EqualTo(3), "server refilled the magazine after ReloadTicks");
            a.SendFire(origin, dir);
            h.Step(5);
            Assert.That(h.Server.Combat.Diag.ShotsAccepted, Is.EqualTo(4), "firing works again after the reload");
        }

        // ---------------------------------------------------------------- kill credit (the §4 Phase 5 test)

        [Test]
        public void TwoClients_KillCredit_BothAgreeOnDeathAndCredit_ThenRespawn()
        {
            var h = new Harness(50104).Connected("shooter", "victim");
            var a = h.Clients[0];
            var b = h.Clients[1];

            var aConfirms = new List<HitConfirmEvent>();
            var aDeaths = new List<PlayerDiedEvent>();
            var bDeaths = new List<PlayerDiedEvent>();
            var aRespawns = new List<PlayerRespawnedEvent>();
            var bRespawns = new List<PlayerRespawnedEvent>();
            a.HitConfirmed += aConfirms.Add;
            a.PlayerDied += aDeaths.Add;
            b.PlayerDied += bDeaths.Add;
            a.PlayerRespawned += aRespawns.Add;
            b.PlayerRespawned += bRespawns.Add;

            h.Server.Players.TryGetByOwner(a.PlayerId, out var ape);
            h.Server.Players.TryGetByOwner(b.PlayerId, out var bpe);
            var origin = Eye(ape.Pos);
            var torso = bpe.Pos + new Vector3(0f, 1.0f, 0f);   // 0.78..1.45 band -> the 1.0x zone
            var bSpawn = bpe.Pos;

            // 40 dmg to the torso -> exactly 3 server-resolved hits kill (100 -> 60 -> 20 -> dead)
            int tick = 0, shots = 0;
            h.Step(40, () => { if (tick++ % 7 == 0 && shots < 3) { a.SendFire(origin, AimAt(origin, torso)); shots++; } });

            Assert.That(h.Server.CombatState.TryGet(b.PlayerId, out var bState), Is.True);
            Assert.That(bState.Alive, Is.False, "server: victim died");
            Assert.That(bState.Deaths, Is.EqualTo(1));
            Assert.That(bState.Health, Is.EqualTo(0));
            Assert.That(h.Server.CombatState.TryGet(a.PlayerId, out var aState), Is.True);
            Assert.That(aState.Kills, Is.EqualTo(1), "server: shooter credited");

            Assert.That(aConfirms.Count, Is.EqualTo(3), "every server-resolved hit confirmed to the shooter");
            Assert.That(aConfirms[0].Damage, Is.EqualTo(40f).Within(0.1f), "torso zone = 1.0x Player_Damage");
            Assert.That(aConfirms[0].Killed, Is.False);
            Assert.That(aConfirms[2].Killed, Is.True, "the third hit reports the kill");
            Assert.That(aConfirms[2].TargetKind, Is.EqualTo((byte)HitTargetKind.Player));
            Assert.That(aConfirms[2].TargetId, Is.EqualTo((uint)b.PlayerId));

            // BOTH clients agree on the death + credit -- exact replica parity, plus the reliable fact
            Assert.That(aDeaths.Count, Is.EqualTo(1), "shooter saw the death event");
            Assert.That(bDeaths.Count, Is.EqualTo(1), "victim saw its own death event");
            Assert.That(aDeaths[0].Victim, Is.EqualTo(b.PlayerId));
            Assert.That(aDeaths[0].Killer, Is.EqualTo(a.PlayerId));
            Assert.That(a.CombatState.StateHash(), Is.EqualTo(h.Server.CombatState.StateHash()), "A's combat replica == server");
            Assert.That(b.CombatState.StateHash(), Is.EqualTo(h.Server.CombatState.StateHash()), "B's combat replica == server");
            Assert.That(a.CombatState.TryGet(a.PlayerId, out var aOnA), Is.True);
            Assert.That(aOnA.Kills, Is.EqualTo(1), "A sees its own credit");
            Assert.That(b.CombatState.TryGet(a.PlayerId, out var aOnB), Is.True);
            Assert.That(aOnB.Kills, Is.EqualTo(1), "B agrees on A's credit");
            Assert.That(b.CombatState.TryGet(b.PlayerId, out var bOnB), Is.True);
            Assert.That(bOnB.Alive, Is.False, "B agrees it is dead");

            // server-owned respawn: 3.5 s later the victim is back at its spawn with full health, everywhere
            h.Step(ServerCombat.RespawnDelayTicks + 20);
            Assert.That(bState.Alive, Is.True, "server respawned the victim");
            Assert.That(bState.Health, Is.EqualTo(100));
            h.Server.Players.TryGetByOwner(b.PlayerId, out var bAfter);
            Assert.That((bAfter.Pos - bSpawn).magnitude, Is.LessThan(0.01f), "respawn teleported back to spawn");
            Assert.That(aRespawns.Count, Is.EqualTo(1).And.EqualTo(bRespawns.Count), "both clients saw the respawn");
            Assert.That(a.CombatState.StateHash(), Is.EqualTo(h.Server.CombatState.StateHash()), "post-respawn parity (A)");
            Assert.That(b.CombatState.StateHash(), Is.EqualTo(h.Server.CombatState.StateHash()), "post-respawn parity (B)");
        }

        [Test]
        public void Fire_HeadshotZone_TripleMultiplier_OneShotsAt120()
        {
            var h = new Harness(50105).Connected("shooter", "victim");
            var a = h.Clients[0];
            var b = h.Clients[1];
            var confirms = new List<HitConfirmEvent>();
            a.HitConfirmed += confirms.Add;

            h.Server.Players.TryGetByOwner(a.PlayerId, out var ape);
            h.Server.Players.TryGetByOwner(b.PlayerId, out var bpe);
            var origin = Eye(ape.Pos);
            var head = bpe.Pos + new Vector3(0f, 1.6f, 0f);   // 1.45..1.8 band -> 3.0x

            a.SendFire(origin, AimAt(origin, head));
            h.Step(15);

            Assert.That(confirms.Count, Is.EqualTo(1));
            Assert.That(confirms[0].Headshot, Is.True, "the 1.6 m band resolves as the head zone");
            Assert.That(confirms[0].Damage, Is.EqualTo(120f).Within(0.1f), "40 x 3.0 head multiplier");
            Assert.That(confirms[0].Killed, Is.True, "120 > 100 = a one-shot kill");
            Assert.That(h.Server.CombatState.TryGet(b.PlayerId, out var bState), Is.True);
            Assert.That(bState.Alive, Is.False);
        }

        // ---------------------------------------------------------------- zombies through the wire

        [Test]
        public void Fire_KillsZombie_CreditAndEvents_OnBothClients()
        {
            var h = new Harness(50106).Connected("shooter", "observer");
            var a = h.Clients[0];
            var b = h.Clients[1];
            var mock = new MockZombieHost();
            h.Server.Combat.ZombieHost = mock;

            var zid = h.Server.Ids.Mint();
            h.Server.Zombies.ServerSpawn(zid, 0, new Vector3(0f, 0f, 5f), h.Server.Session.CurrentTick);
            mock.Hp[zid.Value] = 150f;   // Eaglefire Zombie_Damage 99 -> exactly two hits

            var aHits = new List<ZombieHitEvent>();
            var aDied = new List<ZombieDiedEvent>();
            var bDied = new List<ZombieDiedEvent>();
            a.ZombieHit += aHits.Add;
            a.ZombieDied += aDied.Add;
            b.ZombieDied += bDied.Add;

            h.Server.Players.TryGetByOwner(a.PlayerId, out var ape);
            var origin = Eye(ape.Pos);
            var center = new Vector3(0f, 1.0f, 5f);

            int tick = 0, shots = 0;
            h.Step(30, () => { if (tick++ % 7 == 0 && shots < 2) { a.SendFire(origin, AimAt(origin, center)); shots++; } });

            Assert.That(mock.Hits, Is.EqualTo(2), "both server bullets landed on the zombie");
            Assert.That(mock.Hp[zid.Value], Is.LessThanOrEqualTo(0f), "the host's brain died");
            Assert.That(h.Server.Zombies.TryGet(zid, out var ze), Is.True);
            Assert.That(ze.IsDead, Is.True, "the killed zombie flips Dead the same tick (no re-hit window)");
            Assert.That(h.Server.CombatState.TryGet(a.PlayerId, out var aState), Is.True);
            Assert.That(aState.Kills, Is.EqualTo(1), "zombie kill credited to the shooter");

            Assert.That(aHits.Count, Is.EqualTo(2), "each landed bullet broadcast a ZombieHit");
            Assert.That(aDied.Count, Is.EqualTo(1).And.EqualTo(bDied.Count), "ZombieDied reached both clients");
            Assert.That(aDied[0].NetId, Is.EqualTo(zid.Value));
            Assert.That(aDied[0].Killer, Is.EqualTo(a.PlayerId));
            Assert.That(a.Zombies.TryGet(zid, out var zOnA), Is.True, "zombie replicated to A");
            Assert.That(zOnA.IsDead, Is.True, "A's replica agrees it is dead");
            Assert.That(b.Zombies.StateHash(), Is.EqualTo(h.Server.Zombies.StateHash()), "B's zombie replica == server");
        }

        // ---------------------------------------------------------------- melee: the deferred-hit timer

        [Test]
        public void Melee_DeferredHit_LandsAfterTheDelay_AndCooldownRejectsSpam()
        {
            var h = new Harness(50107).Connected("attacker", "victim");
            var a = h.Clients[0];
            var b = h.Clients[1];

            // victim spawns 2 m along +X; the attacker faces it with yaw 90 (forward = (sin,0,cos))
            a.SendMelee(strong: false, yawDegrees: 90f);
            a.SendMelee(strong: false, yawDegrees: 90f);   // immediate second swing -> cooldown-rejected
            h.Step(5);

            Assert.That(h.Server.Combat.Diag.MeleeAccepted, Is.EqualTo(1), "one swing accepted");
            Assert.That(h.Server.Combat.Diag.MeleeRejected, Is.EqualTo(1), "the spam swing rejected");
            Assert.That(h.Server.CombatState.TryGet(b.PlayerId, out var bState), Is.True);
            Assert.That(bState.Health, Is.EqualTo(100), "no damage before the deferred-hit timer lands");

            h.Step(20);   // past HitDelayTicks (16) -- the hit re-evaluates NOW and lands
            Assert.That(bState.Health, Is.EqualTo(60), "melee landed for Player_Damage 40 after the delay");

            h.Step(20);   // past CooldownTicks -> a STRONG swing goes through at 1.5x
            a.SendMelee(strong: true, yawDegrees: 90f);
            h.Step(25);
            Assert.That(bState.Health, Is.EqualTo(0), "strong swing = 40 x 1.5 = 60 -> dead");
            Assert.That(bState.Alive, Is.False);
        }

        // ---------------------------------------------------------------- the D1 PvP-off posture

        [Test]
        public void PvPDisabled_PlayersAreNotTargets_ZombiesUnaffected()
        {
            var h = new Harness(50109).Connected("shooter", "bystander");
            var a = h.Clients[0];
            var b = h.Clients[1];   // spawns 2 m along +X (SpawnPosition spacing)
            var mock = new MockZombieHost();
            h.Server.Combat.ZombieHost = mock;
            h.Server.Combat.PvPEnabled = false;   // the D1 dedicated-server posture

            // a zombie BEHIND the bystander on the same aim line: with PvP off the bullet must pass
            // THROUGH the player and land on the zombie
            var zid = h.Server.Ids.Mint();
            h.Server.Zombies.ServerSpawn(zid, 0, new Vector3(6f, 0f, 0f), h.Server.Session.CurrentTick);
            mock.Hp[zid.Value] = 500f;   // survives everything below -- we count damage, not corpses

            var confirms = new List<HitConfirmEvent>();
            var deaths = new List<PlayerDiedEvent>();
            a.HitConfirmed += confirms.Add;
            a.PlayerDied += deaths.Add;
            b.PlayerDied += deaths.Add;

            h.Server.Players.TryGetByOwner(a.PlayerId, out var ape);
            var origin = ape.Pos + new Vector3(0f, 1.0f, 0f);
            a.SendFire(origin, new Vector3(1f, 0f, 0f));   // flat +X: bystander torso band at 2 m, zombie at 6 m
            h.Step(15);

            Assert.That(h.Server.Combat.Diag.ShotsAccepted, Is.EqualTo(1), "validation is untouched by the toggle");
            Assert.That(h.Server.Combat.Diag.BulletHitsPlayer, Is.EqualTo(0), "the bullet never targets the player");
            Assert.That(mock.Hits, Is.EqualTo(1), "it flew through and hit the zombie behind");
            Assert.That(confirms.Count, Is.EqualTo(1));
            Assert.That(confirms[0].TargetKind, Is.EqualTo((byte)HitTargetKind.Zombie), "the confirm is the zombie hit");

            // melee at the bystander (in reach, in the cone): the swing resolves but finds no player target
            h.Step(25);   // clear the melee cooldown window
            a.SendMelee(strong: false, yawDegrees: 90f);   // forward = +X, straight at the bystander
            h.Step(25);   // past HitDelayTicks -- the deferred hit re-evaluated and skipped the player

            // a point-blank grenade: the blast spares BOTH players (self-damage included -- a D1 shell has
            // no server-auth vitals to render a death with), while the zombie inside the radius takes the
            // linear-falloff damage
            float zHpBefore = mock.Hp[zid.Value];
            a.SendGrenade(ape.Pos + new Vector3(0f, 1f, 0f), Vector3.zero);
            h.Step(h.Server.Combat.DefaultGrenade.FuseTicks + 15);

            Assert.That(mock.Hp[zid.Value], Is.LessThan(zHpBefore), "zombie blast damage unaffected (6 m < radius 8)");
            Assert.That(h.Server.CombatState.TryGet(a.PlayerId, out var aState), Is.True);
            Assert.That(h.Server.CombatState.TryGet(b.PlayerId, out var bState), Is.True);
            Assert.That(aState.Alive, Is.True.And.EqualTo(bState.Alive), "everyone alive");
            Assert.That(aState.Health, Is.EqualTo(100), "the thrower's own blast spared it");
            Assert.That(bState.Health, Is.EqualTo(100), "bullet + melee + blast all skipped the bystander");
            Assert.That(bState.Deaths, Is.EqualTo(0));
            Assert.That(deaths.Count, Is.EqualTo(0), "no PlayerDied fact ever broadcast");
        }

        // ---------------------------------------------------------------- grenades: entity + event

        [Test]
        public void Grenade_ServerEntity_SnapsWhileFlying_ExplodesWithSourceFalloff()
        {
            var h = new Harness(50108).Connected("thrower", "observer");
            var a = h.Clients[0];
            var b = h.Clients[1];
            var mock = new MockZombieHost();
            h.Server.Combat.ZombieHost = mock;

            long t0 = h.Server.Session.CurrentTick;
            var zNear = h.Server.Ids.Mint();
            var zFar = h.Server.Ids.Mint();
            h.Server.Zombies.ServerSpawn(zNear, 0, new Vector3(4f, 0f, 0f), t0);
            h.Server.Zombies.ServerSpawn(zFar, 0, new Vector3(9f, 0f, 0f), t0);
            mock.Hp[zNear.Value] = 200f;
            mock.Hp[zFar.Value] = 200f;

            var aBoom = new List<GrenadeExplodedEvent>();
            var bBoom = new List<GrenadeExplodedEvent>();
            a.GrenadeExploded += aBoom.Add;
            b.GrenadeExploded += bBoom.Add;

            h.Server.Players.TryGetByOwner(a.PlayerId, out var ape);
            a.SendGrenade(ape.Pos + new Vector3(0f, 1f, 0f), Vector3.zero);   // dropped at the feet
            a.SendGrenade(ape.Pos + new Vector3(0f, 1f, 0f), Vector3.zero);   // second throw inside the 1 s cooldown
            h.Step(20);

            Assert.That(h.Server.Combat.Diag.GrenadesAccepted, Is.EqualTo(1));
            Assert.That(h.Server.Combat.Diag.GrenadesRejected, Is.EqualTo(1), "cooldown rejected the double-throw");
            Assert.That(h.Server.Projectiles.Count, Is.EqualTo(1), "the grenade is a live server entity while fused");
            Assert.That(a.Projectiles.Count, Is.EqualTo(1), "…and snaps to clients while flying");

            h.Step(h.Server.Combat.DefaultGrenade.FuseTicks + 10);

            Assert.That(aBoom.Count, Is.EqualTo(1).And.EqualTo(bBoom.Count), "explosion event reached both clients");
            Assert.That(h.Server.Projectiles.Count, Is.EqualTo(0), "the entity is retired on detonation");
            Assert.That(a.Projectiles.Count, Is.EqualTo(0), "replica retired too");
            // zombie falloff is the source LINEAR curve: r≈4 -> 175*(1-4/8)≈87.5; r=9 > radius 8 -> untouched
            Assert.That(200f - mock.Hp[zNear.Value], Is.EqualTo(87.5f).Within(1.0f), "linear falloff at 4 m");
            Assert.That(mock.Hp[zFar.Value], Is.EqualTo(200f), "outside the radius = nothing");
            // both players stood inside the blast: SQUARED falloff (Player.cs:1975) killed them -- the
            // thrower's own death earns no credit, the observer's death credits the thrower
            Assert.That(h.Server.CombatState.TryGet(a.PlayerId, out var aState), Is.True);
            Assert.That(h.Server.CombatState.TryGet(b.PlayerId, out var bState), Is.True);
            Assert.That(aState.Alive, Is.False, "the thrower ate its own point-blank grenade");
            Assert.That(bState.Alive, Is.False, "the observer 2 m away died too");
            Assert.That(aState.Deaths, Is.EqualTo(1));
            Assert.That(aState.Kills, Is.EqualTo(1), "credit for the observer, none for the self-kill");
        }
    }
}
