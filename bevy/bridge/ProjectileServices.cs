using System.Text.Json;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    public sealed record SavedProjectile(ushort ItemId, string Owner, float[] Position, float[] Velocity, long RemainingTicks, long ElapsedTicks);
    SavedProjectile[] CaptureProjectiles()
    {
        long tick = _server.Session.CurrentTick;
        return _server.Combat.ProjectileFlights.Select(p => new SavedProjectile(p.ItemId,
            _server.Profiles.TryGet(p.OwnerPlayerId, out var owner) ? owner.Name : null,
            Array(p.Position), Array(p.Velocity), Math.Max(0, p.RetireTick - tick), Math.Max(0, tick - p.SpawnTick))).ToArray();
    }
    void RestoreProjectiles(SavedProjectile[] saved)
    {
        foreach (var p in saved ?? [])
        {
            ushort owner = _server.Profiles.All.FirstOrDefault(o => o.Name == p.Owner)?.OwnerPlayerId ?? 0;
            Vector3 Point(float[] v) => Vec(JsonSerializer.SerializeToElement(v));
            _server.Combat.ServerRestoreProjectile(_server.Ids.Mint(), owner, p.ItemId, Point(p.Position), Point(p.Velocity),
                _server.Session.CurrentTick, p.RemainingTicks, p.ElapsedTicks);
        }
    }
    object[] ProjectileSnapshots()
    {
        long tick = _server.Session.CurrentTick;
        return _client.Projectiles.All.Select(e => {
            bool found = _server.Combat.TryGetProjectileFlight(e.NetIdValue, out var p);
            return (object)new { netId = e.NetIdValue, kind = e.Kind, position = Array(e.Pos), lastChangedTick = e.LastChangedTick,
                itemId = found ? (ushort?)p.ItemId : null, velocity = found ? Array(p.Velocity) : null,
                retireTick = found ? (long?)p.RetireTick : null, remainingTicks = found ? Math.Max(0, p.RetireTick - tick) : 0,
                elapsedTicks = found ? (long?)Math.Max(0, tick - p.SpawnTick) : null,
                lifetimeTicks = found ? (long?)(p.RetireTick - p.SpawnTick) : null };
        }).ToArray();
    }
    bool RotorConsole(string command)
    {
        string verb = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (verb is not ("hurtmain" or "killmain" or "hurttail" or "killtail")) return false;
        string text;
        if (!_server.Transactions.AllowCheats) text = "console commands are disabled on this server";
        else if (!_server.VehicleHost.TryGetDriven(_client.PlayerId, out uint id) || !_vehicles.IsHelicopter(id)
            || !_server.CombatState.IsAlive(_client.PlayerId)) text = $"{verb}: you're not flying a helicopter";
        else
        {
            bool tail = verb.EndsWith("tail"); float health = tail ? _vehicles.TailRotorHealth(id) : _vehicles.MainRotorHealth(id);
            _server.Vehicles.TryGet(id, out var car);
            _bodies.DamageVehicle(id, verb.StartsWith("kill") ? health : health * .5f + 1, 0, 0, car.Pos, Vector3.zero, tail ? "tailRotor" : "mainRotor");
            float main = _vehicles.MainRotorHealth(id), tailHp = _vehicles.TailRotorHealth(id);
            float Norm(float current, float maximum) => maximum > 0 ? current / maximum : 0;
            text = $"{verb}: main {main:0} ({Norm(main, _vehicles.MainRotorMax(id)) * 100:0}%), tail {tailHp:0} ({Norm(tailHp, _vehicles.TailRotorMax(id)) * 100:0}%)"
                + (main <= 0 ? " -- MAIN DEAD, no lift" : "") + (tailHp <= 0 ? " -- TAIL DEAD, spinning" : "");
        }
        Emit("console", new { text }); return true;
    }
}
