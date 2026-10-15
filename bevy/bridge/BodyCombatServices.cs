using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Host lifecycle from Vehicle.TakeDamage/Explode and
/// Deployable.TakeDamage/Explode. The original replication entities own HP;
/// Bevy owns the single physical body and consumes accepted damage/effect facts.</summary>
public sealed class BodyCombatServices
{
    readonly NetWorldServer _server;
    readonly WorldGeometry _geometry;
    readonly VehicleServices _vehicles;
    readonly Action<string, object> _emit;
    readonly Dictionary<(bool vehicle, uint id), Life> _life = new();
    readonly Dictionary<(bool vehicle, uint id), (float exact, float published)> _repair = new();
    readonly Dictionary<(ushort owner, uint id), long> _salvageApproved = new();
    ushort _toolOwner;
    long _toolUntil = -1, _toolStarted;
    (bool vehicle, uint id)? _toolTarget;
    public Func<ushort, float> RepairRate;
    public Action<uint> RequestSalvage;
    const long NoTime = long.MinValue;
    sealed class Life { public long Fuse = NoTime, Burn = NoTime; }
    public sealed record Saved(bool Vehicle, int Ordinal, ushort DefId, float[] Position, long FuseRemaining, long BurnElapsed);
    public BodyCombatServices(NetWorldServer server, WorldGeometry geometry, VehicleServices vehicles, Action<string, object> emit)
    {
        _server = server; _geometry = geometry; _vehicles = vehicles; _emit = emit;
        server.Combat.DamageBody = Hit;
        server.Combat.BlastOccluded = geometry.BlastBlocked;
        server.Combat.DamageHostBlast = b => BlastBodies(b.Position, b.Radius, b.VehicleDamage, b.Attacker, true, false);
        server.Transactions.CanSalvageWreck = (owner, id) => Salvageable(false, id)
            && _salvageApproved.TryGetValue((owner, id), out long approved) && approved >= Tick;
        geometry.VehicleExists = id => server.Vehicles.TryGet(id, out _);
        geometry.DeployableExists = id => server.Deployables.TryGet(id, out _);
    }
    Life State(bool vehicle, uint id)
    {
        if (!_life.TryGetValue((vehicle, id), out var s)) _life[(vehicle, id)] = s = new();
        return s;
    }
    long Tick => _server.Session.CurrentTick;
    public float BurnSeconds(bool vehicle, uint id) => _life.TryGetValue((vehicle, id), out var l) && l.Burn != NoTime ? (Tick - l.Burn) * .02f : -1;
    public bool OnFire(bool vehicle, uint id) => _life.TryGetValue((vehicle, id), out var l) && (l.Fuse != NoTime || l.Burn != NoTime);
    public bool Salvageable(bool vehicle, uint id) => BurnSeconds(vehicle, id) >= BodyRules.CoolSeconds;
    public bool ToolUse(ushort owner, bool active)
    {
        _toolOwner = owner; _toolUntil = active ? Tick + 5 : -1;
        if (!active) _toolTarget = null;
        return !active || RepairRate?.Invoke(owner) > 0;
    }
    void StepTool()
    {
        float rate = RepairRate?.Invoke(_toolOwner) ?? 0;
        if (_toolUntil < Tick || rate <= 0 || !_server.Players.TryGetByOwner(_toolOwner, out var player)
            || !_server.CombatState.TryGet(_toolOwner, out var combat) || !combat.Alive || _server.Vehicles.All.Any(v => v.DriverPlayerId == _toolOwner || (v.Passengers?.Contains(_toolOwner) ?? false)))
        { _toolTarget = null; return; }
        float yaw = player.YawDegrees * Mathf.Deg2Rad, pitch = (_server.PlayerHost.TryGetDrivenState(_toolOwner, out var pose) ? pose.PitchDegrees : 0) * Mathf.Deg2Rad;
        var direction = new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
        var origin = player.Pos + new Vector3(0, PlayerMovementDef.EyeHeightForStance(player.Stance switch { 2 => EPlayerStance.CROUCH, 3 => EPlayerStance.PRONE, _ => EPlayerStance.STAND }), 0);
        if (!_geometry.RayDetailed(origin, origin + direction * HandRules.LookReach, out var hit) || (hit.VehicleNetId == 0 && hit.DeployableNetId == 0))
        { _toolTarget = null; return; }
        var key = (vehicle: hit.VehicleNetId != 0, id: hit.VehicleNetId != 0 ? hit.VehicleNetId : hit.DeployableNetId);
        if (_toolTarget != key) { _toolTarget = key; _toolStarted = Tick; }
        if (Salvageable(key.vehicle, key.id))
        {
            if (Tick - _toolStarted < Ticks(BodyRules.SalvageSeconds)) return;
            if (key.vehicle && _server.Vehicles.TryGet(key.id, out var wreck))
            {
                for (int i = 0; i < 3; i++) _server.WorldItems.ServerSpawn(_server.Ids.Mint(), new Item(67), wreck.Pos + new Vector3((i - 1) * .6f, .5f, 0), Tick);
                _server.Vehicles.ServerRemove(new(key.id), Tick);
                _emit("vehicleSalvaged", new { netId = key.id, playerId = _toolOwner });
            }
            else { _salvageApproved[(_toolOwner, key.id)] = Tick + 10; RequestSalvage?.Invoke(key.id); }
            _toolTarget = null; return;
        }
        _toolStarted = Tick;
        float health, maximum;
        if (key.vehicle)
        {
            if (!_server.Vehicles.TryGet(key.id, out var e) || e.Exploded) return;
            health = e.Health; maximum = _vehicles.HealthMax(key.id);
        }
        else
        {
            if (!_server.Deployables.TryGet(key.id, out var e) || e.OnFire) return;
            health = e.Health; maximum = DeployableDef.ById(e.DefId)?.Health ?? 0;
        }
        if ((!key.vehicle && health <= 0) || health >= maximum) return;
        var prior = _repair.GetValueOrDefault(key);
        float exact = prior.published == health ? prior.exact : health;
        float next = Math.Min(maximum, exact + rate * (float)SimClock.FixedDelta);
        if (key.vehicle)
        {
            _server.Vehicles.TryGet(key.id, out var e);
            _server.Vehicles.ServerPublishVitals(new(key.id), _vehicles.FuelFor(key.id, e.Fuel), next, e.Battery, false, Tick);
            _vehicles.RepairParts(key.id, next - exact, maximum);
            _repair[key] = (next, e.Health);
            _emit("vehicleRepaired", new { netId = key.id, amount = next - exact, health = e.Health, healthMax = maximum });
        }
        else
        {
            _server.Deployables.TryGet(key.id, out var e); _server.Deployables.ServerSetScalars(key.id, next, e.Fuel, false, Tick);
            _repair[key] = (next, e.Health);
        }
    }
    void Hit(CombatBodyHit h)
    {
        if (h.Hit.VehicleNetId != 0) DamageVehicle(h.Hit.VehicleNetId, h.Damage, h.Melee ? .5f : 0, h.Attacker, h.Hit.Point, h.Direction,
            h.Melee ? "hull" : _vehicles.HitPart(h.Hit.VehicleNetId, h.Hit.Point, h.Hit.BodyPart));
        else if (h.Hit.DeployableNetId != 0) DamageDeployable(h.Hit.DeployableNetId, h.Damage, h.Attacker);
    }
    public object HostVehicleDamage(JsonElement request)
    {
        uint id = checked((uint)Int(request, "netId"));
        if (!_server.Vehicles.TryGet(id, out var vehicle)) throw new ArgumentException($"Unknown vehicle {id}");
        string part = Text(request, "bodyPart", "hull");
        float amount = Float(request, "amount"), share = Float(request, "engineShare", part == "hull" ? .5f : 0);
        if (amount < 0 || share < 0 || share > 1) throw new ArgumentException("Invalid host damage amount/engine share");
        if (part is not ("hull" or "engine" or "mainRotor" or "tailRotor") && !(part.StartsWith("tire:") && int.TryParse(part.AsSpan(5), out int tire) && tire is >= 0 and < 8))
            throw new ArgumentException("Unknown host vehicle body part");
        var point = Vec(request, "point", vehicle.Pos); var direction = Vec(request, "direction");
        bool explodeNow = Bool(request, "explodeNow");
        var props = new List<(int index, float amount)>();
        if (request.TryGetProperty("destructibles", out var destructibles)) foreach (var prop in destructibles.EnumerateArray())
        {
            int index = Int(prop, "index", -1); float damage = Float(prop, "amount");
            if (index < 0 || index >= _server.Destructibles.Count || damage < 0) throw new ArgumentException("Invalid host destructible damage");
            props.Add((index, damage));
        }
        bool accepted = (amount > 0 || explodeNow) && !vehicle.Exploded;
        if (accepted)
        {
            DamageVehicle(id, amount, share, 0, point, direction, part);
            foreach (var prop in props) _server.DestructibleHost.DamageObject(prop.index, prop.amount, Tick + 1);
            // The native host publishes contact facts before the upcoming authority Step.
            // Resolve through the normal explosion lifecycle that step, without its 4s fuse.
            if (explodeNow) State(true, id).Fuse = Tick;
        }
        return new { accepted, netId = id };
    }
    public void DamageVehicle(uint id, float amount, float engineShare, ushort attacker, Vector3 point, Vector3 direction, string part = "hull")
    {
        if (amount <= 0 || !_server.Vehicles.TryGet(id, out var e) || e.Exploded) return;
        bool hull = part is null or "hull";
        _vehicles.DamageParts(id, amount, engineShare, part);
        float health = hull ? Math.Max(0, e.Health - amount) : e.Health;
        _server.Vehicles.ServerPublishVitals(new(id), _vehicles.FuelFor(id, e.Fuel), health, e.Battery, false, Tick + 1);
        _emit("vehicleDamage", new { netId = id, amount, engineShare, bodyPart = part, point = Array(point), direction = Array(direction), attacker, health });
        var life = State(true, id);
        if (hull && health <= 0 && life.Fuse == NoTime) life.Fuse = Tick + Ticks(BodyRules.VehicleFuse);
        // Vehicle.StepHeli: losing both rotor pools kills the hull, then follows the
        // ordinary fuse. Rotor-only hits otherwise leave hull HP untouched.
        if (!hull && health > 0 && _vehicles.IsHelicopter(id)
            && _vehicles.MainRotorHealth(id) <= 0 && _vehicles.TailRotorHealth(id) <= 0)
            DamageVehicle(id, health, .5f, attacker, point, direction);
    }
    void DamageDeployable(uint id, float amount, ushort attacker)
    {
        if (amount <= 0 || !_server.Deployables.TryGet(id, out var e) || e.OnFire) return;
        var def = DeployableDef.ById(e.DefId);
        if (def == null || def.Fixture != FixtureKind.None) return; // map service fixtures have no damage node
        float health = Math.Max(0, e.Health - amount);
        _server.Deployables.ServerSetScalars(id, health, e.Fuel, health <= 0 && def.IsGenerator, Tick);
        _emit("deployableDamage", new { netId = id, amount, health, attacker });
        if (health > 0) return;
        if (def.IsTrap && def.TrapExplosive)
        {
            // Source trap explosion consumes itself; other traps are excluded
            // from the structure blast to prevent synchronous chain recursion.
            _server.Transactions.ServerDestroyDeployable(id);
            _server.Combat.ServerDetonateAt(e.Pos, def.TrapBlast, def.TrapPlayerDamage, Tick, zombieDamage: def.TrapZombieDamage);
            BlastBodies(e.Pos, def.TrapBlast, def.TrapVehicleDamage, attacker, true, false);
            foreach (var dep in _server.Deployables.All.ToArray())
                if (DeployableDef.ById(dep.DefId) is { IsTrap: false })
                    DamageDeployable(dep.NetIdValue, ExplosionMath.Linear(def.TrapStructureDamage, (dep.Pos - e.Pos).magnitude, def.TrapBlast), attacker);
            _emit("deployableExploded", new { netId = id, position = Array(e.Pos), radius = def.TrapBlast, shatter = true });
        }
        else if (!def.IsGenerator)
        {
            _server.Transactions.ServerDestroyDeployable(id);
            _emit("deployableBroken", new { netId = id, position = Array(e.Pos) });
        }
        else
        {
            DisconnectWires(id);
            State(false, id).Fuse = Tick + Ticks(BodyRules.DeployableFuse);
        }
    }
    void DisconnectWires(uint id)
    {
        foreach (var wire in _server.Deployables.AllWires.Where(w => w.SrcId == id || w.DstId == id).ToArray())
            if (_server.Deployables.ServerRemoveWire(wire.NetIdValue, Tick))
                _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventWireRemoved, new WireRemovedEvent { WireId = wire.NetIdValue }.Write));
    }
    void BlastBodies(Vector3 position, float radius, float damage, ushort attacker, bool walls, bool deployables)
    {
        foreach (var e in _server.Vehicles.All.ToArray())
        {
            float range = (e.Pos - position).magnitude;
            if (range >= radius || (walls && _geometry.BlastBlocked(position, e.Pos))) continue;
            DamageVehicle(e.NetIdValue, ExplosionMath.Linear(damage, range, radius), .5f, attacker, e.Pos, (e.Pos - position).normalized);
        }
        if (deployables) foreach (var e in _server.Deployables.All.ToArray())
        {
            float range = (e.Pos - position).magnitude;
            if (range >= radius || DeployableDef.ById(e.DefId)?.ExplosionProof == true) continue;
            DamageDeployable(e.NetIdValue, ExplosionMath.Linear(damage, range, radius), attacker);
        }
    }
    void BlastPlayers(Vector3 position, float radius, float damage)
    {
        foreach (var p in _server.Players.All)
            if ((p.Pos - position).magnitude is var range && range < radius)
                _server.Combat.DamagePlayerExternal(p.OwnerPlayerId, ExplosionMath.Linear(damage, range, radius), zone: Durability.Zone.Whole);
    }
    public void Step()
    {
        StepTool();
        // Also adopt an explicitly supplied initial wreck / external host crash.
        foreach (var e in _server.Vehicles.All)
        {
            if (e.Exploded && !_life.ContainsKey((true, e.NetIdValue))) State(true, e.NetIdValue).Burn = Tick;
            else if (e.Health <= 0 && !e.Exploded && State(true, e.NetIdValue).Fuse == NoTime) State(true, e.NetIdValue).Fuse = Tick + Ticks(BodyRules.VehicleFuse);
        }
        foreach (var e in _server.Deployables.All)
            if (e.OnFire && !_life.ContainsKey((false, e.NetIdValue))) State(false, e.NetIdValue).Fuse = Tick + Ticks(BodyRules.DeployableFuse);
        foreach (var ((vehicle, id), life) in _life.ToArray())
        {
            if (vehicle ? !_server.Vehicles.TryGet(id, out _) : !_server.Deployables.TryGet(id, out _)) { _life.Remove((vehicle, id)); continue; }
            if (life.Fuse != NoTime && Tick >= life.Fuse)
            {
                life.Fuse = NoTime; life.Burn = Tick;
                if (vehicle)
                {
                    _server.Vehicles.TryGet(id, out var e);
                    _server.Vehicles.ServerPublishVitals(new(id), _vehicles.FuelFor(id, e.Fuel), 0, e.Battery, true, Tick);
                    foreach (var tow in _server.Vehicles.All.Where(v => v.NetIdValue == id || v.TowedNetId == id).ToArray())
                        _server.Vehicles.ServerPublishTow(new(tow.NetIdValue), 0, 0, Tick);
                    BlastBodies(e.Pos, BodyRules.VehicleRadius, BodyRules.VehicleDamage, 0, false, true);
                    BlastPlayers(e.Pos, BodyRules.VehicleRadius, BodyRules.VehiclePlayerDamage);
                    _emit("vehicleExploded", new { netId = id, position = Array(e.Pos), radius = BodyRules.VehicleRadius,
                        impulse = new[] { 0, 18000, 0 }, torqueImpulse = new[] { 2800, 0, 0 } });
                }
                else
                {
                    _server.Deployables.TryGet(id, out var e); var def = DeployableDef.ById(e.DefId);
                    DisconnectWires(id);
                    BlastBodies(e.Pos, BodyRules.DeployableRadius, BodyRules.DeployableDamage, 0, false, true);
                    BlastPlayers(e.Pos, BodyRules.DeployableRadius, BodyRules.DeployableDamage);
                    _emit("deployableExploded", new { netId = id, position = Array(e.Pos), radius = BodyRules.DeployableRadius, shatter = def?.ShatterOnDeath == true });
                    if (def?.ShatterOnDeath == true) _server.Transactions.ServerDestroyDeployable(id);
                }
            }
            if (life.Burn != NoTime && Tick - life.Burn >= Ticks(BodyRules.RetireSeconds))
            {
                if (vehicle) _server.Vehicles.ServerRemove(new(id), Tick); else _server.Transactions.ServerDestroyDeployable(id);
                _life.Remove((vehicle, id));
            }
        }
    }
    public Saved[] Capture()
    {
        var vehicles = _server.Vehicles.All.ToArray(); var result = new List<Saved>();
        foreach (var ((vehicle, id), l) in _life)
        {
            if (vehicle)
            {
                int index = System.Array.FindIndex(vehicles, v => v.NetIdValue == id);
                if (index >= 0) result.Add(new(true, index, 0, null, l.Fuse == NoTime ? -1 : Math.Max(0, l.Fuse - Tick), l.Burn == NoTime ? -1 : Tick - l.Burn));
            }
            else if (_server.Deployables.TryGet(id, out var e)) result.Add(new(false, -1, e.DefId, Array(e.Pos), l.Fuse == NoTime ? -1 : Math.Max(0, l.Fuse - Tick), l.Burn == NoTime ? -1 : Tick - l.Burn));
        }
        return result.ToArray();
    }
    public void Restore(Saved[] saved)
    {
        var vehicles = _server.Vehicles.All.ToArray();
        foreach (var s in saved ?? [])
        {
            uint id = s.Vehicle ? (s.Ordinal >= 0 && s.Ordinal < vehicles.Length ? vehicles[s.Ordinal].NetIdValue : 0)
                : _server.Deployables.All.FirstOrDefault(d => d.DefId == s.DefId && (d.Pos - new Vector3(s.Position[0], s.Position[1], s.Position[2])).magnitude < .01f)?.NetIdValue ?? 0;
            if (id == 0) continue;
            var l = State(s.Vehicle, id); l.Fuse = s.FuseRemaining < 0 ? NoTime : Tick + s.FuseRemaining; l.Burn = s.BurnElapsed < 0 ? NoTime : Tick - s.BurnElapsed;
        }
    }
    static long Ticks(float seconds) => (long)Math.Round(seconds / SimClock.FixedDelta);
}
