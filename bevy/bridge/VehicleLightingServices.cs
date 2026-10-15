using System.Text.Json;
using UnturnedGodot;
using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;
using static UnturnedBevy.VehicleLightRules;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;

namespace UnturnedBevy;

/// <summary>Original electrical state on the host; one native body reports
/// motion/swamping, source meshes report lens centers, clients request switches.</summary>
public sealed class VehicleLightingServices
{
    sealed class Entry
    {
        public byte TypeId, Lamps, Broken;
        public bool Aircraft, HasHorn, Swamped, BurnsFuel;
        public readonly List<(byte Slot, Vector3 Center)> Centers = new();
        public VehicleLightState State = new();
    }
    public sealed record LampSaved(byte Slot, float[] Center);
    public sealed record Saved(int Ordinal, byte TypeId, float Battery, byte Flags, bool HighBeam, byte Pattern,
        byte Lamps, byte Broken, float AlarmTimer, float AlarmBlip, float AlarmCheck, bool AlarmLit, bool Swamped, LampSaved[] Centers);
    readonly NetWorldServer _server;
    readonly MountedWeaponCatalog _catalog;
    readonly VehicleServices _vehicles;
    readonly BodyCombatServices _bodies;
    readonly Dictionary<uint, Entry> _states = new();
    readonly Func<double> _roll;
    public Action<Vector3, float> Noise;
    public VehicleLightingServices(NetWorldServer server, Catalog catalog, VehicleServices vehicles, BodyCombatServices bodies, Func<double> roll = null)
    {
        _server = server; _catalog = catalog.Mounted; _vehicles = vehicles; _bodies = bodies; _roll = roll ?? Random.Shared.NextDouble;
        server.VehicleLightingAllowed = Allowed; server.VehicleLightingRequested = Apply;
        server.VehicleHost.NormalizeDriverFlags = NormalizeFlags;
    }
    Entry Ensure(uint id)
    {
        if (!_server.Vehicles.TryGet(id, out var e)) return null;
        if (_states.TryGetValue(id, out var found) && found.TypeId == e.TypeId) return found;
        var original = _catalog.For(e.TypeId)?.Source;
        bool siren = false, aircraft = false, horn = false, fuel = false;
        if (original is JsonElement source)
        {
            var spec = source.GetProperty("spec"); fuel = Float(spec,"Fuel") > 0; aircraft = Bool(spec, "Heli") || Bool(spec, "Plane");
            siren = spec.TryGetProperty("Parts",out var parts) && parts.ValueKind==JsonValueKind.Array
                && parts.EnumerateArray().Any(p => p[0].GetString()?.Contains("_siren0") == true);
            horn = spec.TryGetProperty("Horn", out var h) && h.ValueKind == JsonValueKind.String && h.GetString()?.Length > 0;
        }
        var entry = new Entry { TypeId = e.TypeId, Aircraft = aircraft, HasHorn = horn, BurnsFuel = fuel };
        entry.State.Battery = e.Battery; entry.State.HasSiren = siren; entry.State.Adopt(e.Flags, e.HighBeam, e.LightbarPattern);
        // The original real Vehicle rolls once at Build. Replicas never roll.
        entry.State._alarmed = original != null && _roll() < AlarmSpawnChance;
        entry.State.Proximity = () => _server.Vehicles.TryGet(id, out var v) && _server.Players.All.Any(p => (p.Pos - v.Pos).sqrMagnitude < AlarmRadiusSq);
        entry.State.Horn = () => { if (entry.HasHorn && _server.Vehicles.TryGet(id, out var v)) { Fact(id, VehicleLightingKind.AlarmHorn, 255, v.Pos); Noise?.Invoke(v.Pos, NoiseRules.Horn); } };
        _states[id] = entry; return entry;
    }
    public void Registered(uint id, JsonElement source)
    {
        var entry = Ensure(id); if (entry == null) return;
        // Keep the unquantized source battery from trusted registration.
        entry.State.Battery = Math.Clamp(Float(source, "battery"), 0, BatteryMax);
        Publish(id, entry);
    }
    public bool CanHonk(uint id) { var e=Ensure(id);return e!=null&&e.HasHorn&&e.State.Battery>0&&e.State._alarmTimer<=0; }
    public float BatteryFor(uint id, float fallback) => _states.TryGetValue(id, out var s) ? s.State.Battery : fallback;
    public void BodyState(uint id, JsonElement state)
    {
        var s = Ensure(id); if (s == null) return;
        if (state.TryGetProperty("swamped", out var swamped)) s.Swamped = swamped.GetBoolean();
    }
    bool EngineAvailable(uint id) => !_vehicles.EngineDrowned(id) && (!_vehicles.HasEngineHealth(id) || _vehicles.EngineHealth(id) > 0);
    public byte NormalizeFlags(uint id, byte proposed)
    {
        if (!_server.Vehicles.TryGet(id, out var e)) return proposed;
        var entry = Ensure(id); var s = entry.State;
        bool wantEngine = (proposed & VehicleReplication.FlagEngineOn) != 0;
        s.EngineOn = wantEngine && !e.Exploded && !entry.Swamped && !_bodies.OnFire(true, id) && EngineAvailable(id)
            && (s.EngineOn || s.Battery >= BatteryStartMin);
        return s.Flags((byte)((proposed & VehicleReplication.FlagBraking) | (e.Flags & VehicleReplication.FlagExploded)));
    }
    bool Allowed(ushort owner, VehicleLightingCommand cmd)
    {
        if (!cmd.IsValid || !_server.Vehicles.TryGet(cmd.NetId, out var e) || e.Exploded) return false;
        var entry = Ensure(cmd.NetId);
        if (cmd.Action == VehicleLightingAction.RepairLamp)
            return (entry.Broken & (1 << cmd.Value)) != 0 && _server.Players.TryGetByOwner(owner, out var p)
                && (p.Pos - e.Pos).sqrMagnitude <= ServerVehicles.PaintReach * ServerVehicles.PaintReach;
        return _server.VehicleHost.TryGetDriven(owner, out uint id) && id == cmd.NetId
            && !entry.Swamped && (cmd.Action == VehicleLightingAction.CycleHeadlights || entry.State.HasSiren);
    }
    void Apply(ushort owner, VehicleLightingCommand cmd)
    {
        var e = Ensure(cmd.NetId); if (e == null) return;
        switch (cmd.Action)
        {
            case VehicleLightingAction.CycleHeadlights: e.State.ToggleHeadlights(); break;
            case VehicleLightingAction.ToggleSiren: e.State.ToggleSiren(); break;
            case VehicleLightingAction.SetPattern: e.State.SetLightbar(cmd.Value == 255 ? -1 : cmd.Value); break;
            case VehicleLightingAction.RepairLamp:
                e.Broken &= (byte)~(1 << cmd.Value); Fact(cmd.NetId, VehicleLightingKind.LampRepaired, cmd.Value, LampWorld(cmd.NetId, e, cmd.Value)); break;
        }
        Publish(cmd.NetId, e);
    }
    public void Damage(uint id, string part, float engineBefore)
    {
        var e = Ensure(id); if (e == null) return;
        if (part is null or "hull" || part == "engine" && engineBefore > 0) e.State.TriggerAlarm();
    }
    public void Step()
    {
        foreach (uint id in _states.Keys.ToArray()) if (!_server.Vehicles.TryGet(id, out _)) _states.Remove(id);
        foreach (var vehicle in _server.Vehicles.All)
        {
            var e = Ensure(vehicle.NetIdValue); var s = e.State;
            // Engine is still an accepted body/control field (including the
            // source NPC warm-start writer); lights themselves have no such writer.
            s.EngineOn = vehicle.EngineOn;
            if (vehicle.Exploded) { e.Broken = e.Lamps; s.Extinguish(true); }
            else
            {
                if (!EngineAvailable(vehicle.NetIdValue) || _bodies.OnFire(true, vehicle.NetIdValue)
                    || e.BurnsFuel && _vehicles.FuelFor(vehicle.NetIdValue,vehicle.Fuel)<=0) s.EngineOn = false;
                // Vehicle._PhysicsProcess returns through StepHeli/StepPlane
                // before the road electrical/alarm/tail loop.
                if (!e.Aircraft) s.Step(SimClock.FixedDelta);
                if (e.Swamped) s.Extinguish(false);
            }
            Publish(vehicle.NetIdValue, e);
        }
    }
    public void PublishCurrent(uint id) { if(_states.TryGetValue(id,out var e))Publish(id,e); }
    void Publish(uint id, Entry e)
    {
        if (!_server.Vehicles.TryGet(id, out var v)) return;
        _server.Vehicles.ServerPublishLighting(new(id), e.State.Flags(v.Flags), e.State.Battery,
            e.State._headlightsOn && e.State._highBeam, checked((byte)e.State.LightbarPattern), e.Lamps, e.Broken, _server.Session.CurrentTick + 1);
    }
    public object RegisterGeometry(JsonElement request)
    {
        uint id = checked((uint)Int(request, "netId")); var e = Ensure(id);
        if (e == null) throw new ArgumentException("Unknown vehicle");
        var list = new List<(byte Slot, Vector3 Center)>(); byte mask = 0;
        foreach (var lamp in request.GetProperty("lamps").EnumerateArray())
        {
            int slot = System.Array.IndexOf(LampLabels, Text(lamp, "label"));
            if (slot < 0 || (mask & (1 << slot)) != 0 || list.Count >= LampLabels.Length) throw new ArgumentException("Invalid/duplicate source lamp label");
            var center = Vec(lamp, "center"); if (center.sqrMagnitude > 10000) throw new ArgumentException("Lamp center outside source hull");
            mask |= (byte)(1 << slot); list.Add(((byte)slot, center));
        }
        // A source vehicle type has one immutable lamp set. Streaming may
        // refresh centers, but a partial/empty upload cannot erase damage and
        // then recreate an intact lens on the next full upload.
        if(e.Lamps!=0 && mask!=e.Lamps)throw new ArgumentException("Source vehicle lamp set changed after registration");
        e.Centers.Clear(); e.Centers.AddRange(list); e.Lamps = mask;
        if (_server.Vehicles.TryGet(id, out var v) && v.Exploded) e.Broken = mask;
        Publish(id, e); return new { registered = list.Count, lampMask = mask };
    }
    static Q Rotation(VehicleReplication.VehicleEntity e) => Q.CreateFromYawPitchRoll(e.YawDegrees * Mathf.Deg2Rad, e.PitchDegrees * Mathf.Deg2Rad, e.RollDegrees * Mathf.Deg2Rad);
    Vector3 LampWorld(uint id, Entry e, byte slot)
    {
        if (!_server.Vehicles.TryGet(id, out var v)) return Vector3.zero;
        var c = e.Centers.FirstOrDefault(l => l.Slot == slot).Center;
        var p = V.Transform(new(c.x,c.y,c.z), Rotation(v)); return v.Pos + new Vector3(p.X,p.Y,p.Z);
    }
    public int ResolveLamp(uint id, Vector3 point)
    {
        if (!_states.TryGetValue(id, out var e)) return -1;
        int best = -1; float bestD = LampHitTolerance * LampHitTolerance;
        foreach (var lamp in e.Centers)
        {
            if ((e.Broken & (1 << lamp.Slot)) != 0) continue;
            float d = (LampWorld(id,e,lamp.Slot) - point).sqrMagnitude;
            if (d < bestD) { best = lamp.Slot; bestD = d; }
        }
        return best;
    }
    public bool BreakLamp(uint id, int slot)
    {
        if (!_states.TryGetValue(id, out var e) || slot < 0 || slot >= 7 || (e.Lamps & (1 << slot)) == 0 || (e.Broken & (1 << slot)) != 0) return false;
        e.Broken |= (byte)(1 << slot); Publish(id,e); Fact(id,VehicleLightingKind.LampBroken,(byte)slot,LampWorld(id,e,(byte)slot)); return true;
    }
    void Fact(uint id, VehicleLightingKind kind, byte lamp, Vector3 position)
    {
        var fact = new VehicleLightingEvent { NetId=id, Kind=kind, Lamp=lamp, Position=position, Tick=unchecked((uint)_server.Session.CurrentTick) };
        _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventVehicleLighting, fact.Write));
    }
    public Saved[] Capture() => _server.Vehicles.All.Select((v,i) => _states.TryGetValue(v.NetIdValue,out var e)
        ? new Saved(i,v.TypeId,e.State.Battery,e.State.Flags(v.Flags),e.State._highBeam,(byte)e.State.LightbarPattern,e.Lamps,e.Broken,
            Math.Max(0,e.State._alarmTimer),e.State._alarmBlip,e.State._alarmCheckT,e.State._alarmLit,e.Swamped,e.Centers.Select(l=>new LampSaved(l.Slot,Array(l.Center))).ToArray()) : null).Where(s=>s!=null).ToArray();
    public void AdoptSavedBatteries(WorldSave save)
    {
        if(save==null)return;var fleet=_server.Vehicles.All.ToArray();
        foreach(var row in save.Vehicles)if(row.SpawnIndex>=0&&row.SpawnIndex<fleet.Length&&fleet[row.SpawnIndex].TypeId==row.TypeId)
            Ensure(fleet[row.SpawnIndex].NetIdValue).State.Battery=Math.Clamp(row.Battery,0,BatteryMax);
    }
    public void Restore(Saved[] saved)
    {
        var fleet=_server.Vehicles.All.ToArray();
        foreach(var s in saved??[])
        {
            if(s.Ordinal<0||s.Ordinal>=fleet.Length||fleet[s.Ordinal].TypeId!=s.TypeId)continue;
            if(!float.IsFinite(s.Battery)||s.Battery<0||s.Battery>BatteryMax||s.Pattern>2||s.Lamps>127||(s.Broken&~s.Lamps)!=0
                ||!float.IsFinite(s.AlarmTimer)||s.AlarmTimer<0||s.AlarmTimer>30||!float.IsFinite(s.AlarmBlip)||s.AlarmBlip<0
                ||!float.IsFinite(s.AlarmCheck)||s.AlarmCheck<-.1f||s.AlarmCheck>.31f)throw new InvalidDataException("Invalid saved vehicle electrics");
            uint id=fleet[s.Ordinal].NetIdValue;var e=Ensure(id);e.State.Battery=s.Battery;e.State.Adopt(s.Flags,s.HighBeam,s.Pattern);
            e.Lamps=s.Lamps;e.Broken=s.Broken;e.State._alarmTimer=s.AlarmTimer;e.State._alarmBlip=s.AlarmBlip;
            e.State._alarmCheckT=s.AlarmCheck;e.State._alarmLit=s.AlarmLit;e.Swamped=s.Swamped;e.Centers.Clear();
            foreach(var l in s.Centers??[])if(l.Slot<7&&l.Center?.Length==3&&l.Center.All(float.IsFinite))e.Centers.Add((l.Slot,new(l.Center[0],l.Center[1],l.Center[2])));
            Publish(id,e);
        }
    }
    public static object Metadata() => new {
        lampLabels=LampLabels, patternNames=LightbarPatternNames, sirenPitch=LightbarSirenPitch,
        batteryMax=BatteryMax, batteryStartMin=BatteryStartMin, batteryBurnRate=BatteryBurnRate, sirenBurnRate=SirenBurnRate, chargeRate=BatteryChargeRate,
        low=new{range=LowRange,energy=LowEnergy,angle=LowAngle,attenuation=LowAtten},
        high=new{range=HighRange,energy=HighEnergy,angle=HighAngle,attenuation=HighAtten},
        lampKelvinRound=LampKelvinRound,lampKelvinRect=LampKelvinRect,highBeamBurn=HighBeamBurn,lampHitTolerance=LampHitTolerance
    };
    public static VehicleLightingCommand ReadCommand(JsonElement args)
    {
        string name=Text(args,"action");
        if(!Enum.TryParse<VehicleLightingAction>(name,true,out var action)||!Enum.IsDefined(action))throw new ArgumentException("Unknown vehicle lighting action");
        int value=Int(args,"value");if(action==VehicleLightingAction.SetPattern&&value==-1)value=255;
        var command=new VehicleLightingCommand{NetId=checked((uint)Int(args,"netId")),Action=action,Value=checked((byte)value)};
        if(!command.IsValid)throw new ArgumentException("Invalid vehicle lighting intent");return command;
    }
}
