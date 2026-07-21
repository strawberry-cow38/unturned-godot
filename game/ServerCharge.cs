using System.Collections.Generic;
using UnityEngine;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server-authoritative half of the C4 / REMOTE CHARGE (SP/MP-unify §3.1). A FixtureKind.Charge deployable places as a
    // plain ENTITY + a VIEW-ONLY client replica (Charge.Materialize renders the brick). Unlike the sentry/trap/beacon,
    // the charge has NO per-tick logic -- it's inert until DETONATED: a detonator command fires EVERY charge the sender
    // owns at once (source Charge.Detonate). DetonateAll is the command SEAM (mirrors IFuelStation): the net layer's
    // DetonateChargesCommand handler calls it with the sender's player id. Each owned charge blasts an AoE over its
    // Range2 through ServerCombat.DamageZombieExternal (so the kill renders on clients) then self-destructs.
    //
    // CUT 1 scope (documented): ZOMBIES ONLY. The SP Charge also blasts players / vehicles / DEPLOYABLES (the raid --
    // Barricade_Damage vs placed structures); those are the follow-up (player/vehicle/deployable server-damage seams).
    public sealed class ServerCharge
    {
        readonly ZombieReplication _zombies;
        readonly DeployableReplication _deployables;
        readonly ServerCombat _combat;
        readonly PlayerReplication _players;

        struct ChargeParams { public float ZombieDamage, PlayerDamage, Range2; }
        readonly Dictionary<ushort, ChargeParams> _paramCache = new();   // per-variant params (from Charge.ForDefId), cached by DefId

        public ServerCharge(ZombieReplication zombies, DeployableReplication deployables, ServerCombat combat, PlayerReplication players)
        { _zombies = zombies; _deployables = deployables; _combat = combat; _players = players; }

        // read a variant's zombie-relevant params from the SAME source the client node uses (Charge.ForDefId). The
        // transient config node is never added to the tree and is freed immediately.
        ChargeParams ParamsFor(ushort defId)
        {
            if (_paramCache.TryGetValue(defId, out var p)) return p;
            var c = Charge.ForDefId(defId);
            p = new ChargeParams { ZombieDamage = c.ZombieDamage, PlayerDamage = c.PlayerDamage, Range2 = c.Range2 };
            c.Free();
            _paramCache[defId] = p;
            return p;
        }

        // The detonator: fire EVERY charge owned by `ownerPlayerId` at once (source: a detonator triggers all of the
        // owner's charges). Called by the net layer's DetonateChargesCommand handler via the OnDetonateCharges seam.
        // Returns how many charges detonated. Collect the owned charges FIRST -- ServerRemove mutates the registry.
        public int DetonateAll(ushort ownerPlayerId, long tick)
        {
            System.Func<Vector3, Vector3, bool> losClear = _combat.WorldRay != null
                ? (from, to) => !_combat.WorldRay(from, to, out _, out _)
                : (from, to) => true;

            var toBlast = new List<(uint netId, Vector3 pos, ushort defId)>();
            foreach (var e in _deployables.All)
            {
                if (e == null) continue;
                var def = DeployableDef.ById(e.DefId);
                if (def == null || def.Fixture != FixtureKind.Charge) continue;
                if (e.OwnerPlayerId != ownerPlayerId) continue;
                toBlast.Add((e.NetIdValue, e.Pos, e.DefId));
            }

            foreach (var c in toBlast)
            {
                var p = ParamsFor(c.defId);
                foreach (var z in _zombies.All)
                {
                    if (z.IsDead) continue;
                    float d = Vector3.Distance(z.Pos, c.pos);
                    if (d > p.Range2) continue;
                    Vector3 hit = z.Pos + Vector3.up * SentryTargeting.AimHeight(z.Speciality);
                    if (!losClear(c.pos + Vector3.up * 0.8f, hit)) continue;   // a wall shields the blast (source ExplosionBlocked)
                    float dmg = ExplosionMath.Linear(p.ZombieDamage, d, p.Range2);
                    if (dmg > 0f) _combat.DamageZombieExternal(z.NetIdValue, dmg, hit, (z.Pos - c.pos).normalized, tick);
                }
                // players in the blast: PvP-GATED (source DamageTool.explode:1009 canDealPlayerDamage = Provider.isPvP;
                // a PvE server's C4 never touches a player -- same gate my grenade path uses, ServerCombat:550). SQUARED
                // falloff (source Player.cs:1975; thrower included). No server-side explosion armor yet (shared follow-up
                // -- SP applies pc.Inventory.ExplosionArmor, the server doesn't). attacker = owner.
                if (_combat.PvPEnabled) foreach (var pl in _players.All)
                {
                    float d = Vector3.Distance(pl.Pos, c.pos);
                    if (d > p.Range2) continue;
                    Vector3 hit = pl.Pos + Vector3.up * 0.9f;
                    if (!losClear(c.pos + Vector3.up * 0.8f, hit)) continue;
                    float dmg = ExplosionMath.Squared(p.PlayerDamage, d, p.Range2);
                    if (dmg > 0f) _combat.DamagePlayerExternal(pl.OwnerPlayerId, dmg, ownerPlayerId);
                }
                _deployables.ServerRemove(c.netId, tick);   // the charge self-destructs after firing
            }
            return toBlast.Count;
        }
    }
}
