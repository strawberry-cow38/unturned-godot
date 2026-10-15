using UnturnedGodot;
using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Remote picture of accepted mounted rounds. It uses the same source
/// profile/math, advances only to observed server ticks, and never owns geometry,
/// combat or damage. Host continuation/retirement supersedes straight flight.</summary>
public sealed class RemoteMountedFlights
{
    // Presentation memory bound for an unusually busy/malicious server. Dropping
    // an old streak cannot change gameplay, ammunition or any authoritative body.
    public const int MaxFlights=1024;
    sealed class Flight
    {
        public MountedShotEvent Launch;
        public Vector3 Position,Velocity;
        public int Steps,Pierced;
        public uint AtTick;
        public float Width;
        public float[] Color;
        public bool Rocket;
    }
    readonly MountedWeaponCatalog _catalog;
    readonly Action<string,object,long> _emit;
    readonly Dictionary<ulong,Flight> _flights=new();
    public int Count=>_flights.Count;
    public void Clear()=>_flights.Clear();
    static ulong Key(uint tick,ushort sequence)=>((ulong)tick<<16)|sequence;
    public RemoteMountedFlights(MountedWeaponCatalog catalog,Action<string,object,long> emit)
    {_catalog=catalog;_emit=emit;}
    public void Launch(MountedShotEvent shot)
    {
        if(!shot.IsValid||!_catalog.TryGun(shot.Shot.Gun,out var gun))return;
        ulong key=Key(shot.Tick,shot.Sequence);if(_flights.ContainsKey(key))return;
        var profile=MountedWeaponServices.ProfileFor(gun,shot.Shot.Gun,shot.Npc);
        if(profile.BallisticSteps is <1 or >ushort.MaxValue||!float.IsFinite(profile.MuzzleVelocity)||profile.MuzzleVelocity<=0)return;
        if(_flights.Count==MaxFlights)_flights.Remove(_flights.Keys.First());
        var color=GunDef.TracerColor(gun?.CaliberName);
        _flights.Add(key,new(){Launch=shot,Position=shot.Shot.Origin,Velocity=shot.Shot.Dir*profile.MuzzleVelocity,
            Steps=profile.BallisticSteps,AtTick=shot.Tick,Width=CosmeticRules.TracerBaseW*GunDef.TracerScale(gun?.CaliberName),
            Color=[color.r,color.g,color.b],Rocket=gun?.Action=="Rocket"});
    }
    public void Apply(MountedFlightEvent fact)
    {
        if(!fact.IsValid)return;
        if(fact.Kind==MountedFlightKind.Blast)
        {
            _emit("bulletExplosion",new{playerId=fact.Shot.PlayerId,sequence=fact.Sequence,pellet=0,itemId=0,
                gun=fact.Shot.Gun,position=Array(fact.Shot.Origin),radius=fact.Radius},fact.Tick);
            return; // A late join may see a new blast without the historical launch.
        }
        ulong key=Key(fact.LaunchTick,fact.Sequence);if(!_flights.TryGetValue(key,out var flight))return;
        if(fact.Kind==MountedFlightKind.Continue)
        {flight.Position=fact.Position;flight.Velocity=fact.Velocity;flight.Steps=fact.RemainingSteps;flight.Pierced=fact.Pierced;flight.AtTick=fact.Tick;}
        else
        {
            _flights.Remove(key);
            _emit("bulletRetired",new{id=key,shotId=key,playerId=flight.Launch.Shot.PlayerId,
                reason=fact.Reason==MountedFlightRetirement.Impact?"impact":"expired",position=Array(fact.Position),
                velocity=Array(fact.Velocity),acceptedTick=fact.Tick},fact.Tick);
        }
    }
    public void Step(uint observedServerTick)
    {
        foreach(var pair in _flights.ToArray())
        {
            var flight=pair.Value;
            int elapsed=Math.Max(0,unchecked((int)(observedServerTick-flight.AtTick)));
            int count=Math.Min(elapsed,flight.Steps);
            for(int n=0;n<count;n++)flight.Position=BallisticsMath.NextPos(flight.Position,flight.Velocity);
            flight.AtTick+=unchecked((uint)count);flight.Steps-=count;
            if(flight.Steps==0)_flights.Remove(pair.Key);
        }
    }
    public object[] Snapshots()=>_flights.Select(pair=>{
        var f=pair.Value;return (object)new{id=pair.Key,shotId=pair.Key,playerId=f.Launch.Shot.PlayerId,
            sequence=f.Launch.Sequence,pellet=0,itemId=0,gun=f.Launch.Shot.Gun,origin=Array(f.Launch.Shot.Origin),
            position=Array(f.Position),velocity=Array(f.Velocity),gravity=0,remainingSteps=f.Steps,
            launchTick=f.Launch.Tick,silenced=false,tracerWidth=f.Width,tracerColor=f.Color,rocket=f.Rocket,pierced=f.Pierced};
    }).ToArray();
}

public sealed partial class BridgeHost
{
    RemoteMountedFlights _remoteMounted;
    void ReceiveMountedShot(MountedShotEvent shot)
    {
        _events.Add(new(++_eventSequence,"mountShot",new { vehicleNetId=shot.VehicleNetId,index=shot.Mount,
            operatorPlayerId=shot.Shot.PlayerId,npc=shot.Npc,gun=shot.Shot.Gun,muzzle=Array(shot.Shot.Origin),
            direction=Array(shot.Shot.Dir),tick=shot.Tick,remainingAmmo=shot.RemainingAmmo,sequence=shot.Sequence },shot.Tick));
        _remoteMounted?.Launch(shot);
    }
    void ReceiveMountedFlight(MountedFlightEvent fact)=>_remoteMounted?.Apply(fact);
}
