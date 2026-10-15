using System.Text.Json;
using Godot;
using UnturnedGodot;

namespace UnturnedBevy;

public sealed partial class ZombieServices
{
    public sealed record SavedZombie(float[] Home,float[] Position,float[] Velocity,byte Table,uint Outfit,uint Body);
    public sealed record SavedChunk(int X,int Z,float[] Center,float[][] Points,byte[] Tables,int Cap,int Killed,double RefillAt,Tier Tier,uint Seed,SavedZombie[] Live);
    public sealed record SavedBody(uint Id,uint Outfit,byte Table,float[] Position,float[] Velocity,float[] Desired,float Yaw,float Health,
        bool Dead,long CorpseRemaining,float[] DeathImpulse,ZombieRules.Wardrobe Wardrobe,JsonElement? Motion);
    public sealed record Saved(string MapRoot,SavedChunk[] Chunks,SavedBody[] Bodies,double Clock,double Acc,double ColdAcc,
        float[] Alert,float Loudness,double AlertExpiry,float[] FieldAnchor,int[] FieldCell,bool HasField,float[][] Anchors,
        float[] DebugAnchor,ZombieFlowField.Saved Flow,int[][] WalkCache,int[] TierChunks,int[] TierZombies);
    static float[] V(Vector3 v)=>new[]{v.X,v.Y,v.Z};
    static Vector3 V(float[] a)=>new(a[0],a[1],a[2]);
    public Saved Capture()=>_mapRoot==null?null:new(_mapRoot,_chunks.Values.Select(c=>new SavedChunk(c.Cx,c.Cz,V(c.Center),
        c.SpawnPts.Select(V).ToArray(),c.SpawnTables.ToArray(),c.Cap,c.Killed,c.RefillAt,c.Tier,c.Seed,
        c.Live?.Select(z=>new SavedZombie(V(z.Home),V(z.Pos),new[]{z.Vel.X,z.Vel.Y},z.Table,z.Outfit,z.Body?.NetId??0)).ToArray())).ToArray(),
        _bodies.Values.Select(b=>new SavedBody(b.NetId,b.Outfit,b.Table,V(b.GlobalPosition),V(b.Velocity),new[]{b.DesiredVel.X,b.DesiredVel.Y},
            b.Yaw,b.Health,b.Dead,b.Dead?Math.Max(0,b.RetireTick-Tick):0,V(b.DeathImpulse),b.Wardrobe,b.Motion)).ToArray(),
        _clock,_acc,_coldAcc,V(_alertPos),_alertLoud,_alertExpiry,V(_fieldAnchor),new[]{_fieldCell.Item1,_fieldCell.Item2},_hasField,
        _anchors.Select(V).ToArray(),DebugAnchor.HasValue?V(DebugAnchor.Value):null,_field.Capture(),
        _walkCache.Select(p=>new[]{p.Key.Item1,p.Key.Item2,p.Value?1:0}).ToArray(),TierChunks.ToArray(),TierZombies.ToArray());
    public void Restore(Saved s)
    {
        if(s==null)return;
        _mapRoot=s.MapRoot;ZombieTables.Load(_mapRoot);
        _clock=s.Clock;_acc=s.Acc;_coldAcc=s.ColdAcc;_alertPos=V(s.Alert);_alertLoud=s.Loudness;_alertExpiry=s.AlertExpiry;
        _fieldAnchor=V(s.FieldAnchor);_fieldCell=(s.FieldCell[0],s.FieldCell[1]);_hasField=s.HasField;
        _anchors.AddRange(s.Anchors.Select(V));DebugAnchor=s.DebugAnchor==null?null:V(s.DebugAnchor);_field.Restore(s.Flow);
        foreach(var w in s.WalkCache)_walkCache[(w[0],w[1])]=w[2]!=0;
        s.TierChunks.CopyTo(TierChunks,0);s.TierZombies.CopyTo(TierZombies,0);
        var restored=new Dictionary<uint,ZombieBodyState>();
        foreach(var body in s.Bodies)
        {
            var b=BuildBody(body.Table,body.Outfit,V(body.Position));restored.Add(body.Id,b);
            b.Velocity=V(body.Velocity);b.DesiredVel=new(body.Desired[0],body.Desired[1]);b.Yaw=body.Yaw;b.Health=body.Health;
            b.Dead=body.Dead;b.RetireTick=Tick+body.CorpseRemaining;b.DeathImpulse=V(body.DeathImpulse);b.Wardrobe=body.Wardrobe;b.Motion=body.Motion;
            _server.Zombies.ServerPublish(new(b.NetId),U(b.GlobalPosition),b.Yaw,b.Dead?(byte)UnturnedGodot.Net.ZombieNetAnim.Dead:(byte)UnturnedGodot.Net.ZombieNetAnim.Idle,Tick+1);
        }
        foreach(var c in s.Chunks)
        {
            var chunk=new Chunk{Cx=c.X,Cz=c.Z,Center=V(c.Center),Cap=c.Cap,Killed=c.Killed,RefillAt=c.RefillAt,Tier=c.Tier,Seed=c.Seed};
            chunk.SpawnPts.AddRange(c.Points.Select(V));chunk.SpawnTables.AddRange(c.Tables);
            chunk.Live=c.Live?.Select(z=>new Zombie{Home=V(z.Home),Pos=V(z.Position),Vel=new(z.Velocity[0],z.Velocity[1]),
                Table=z.Table,Outfit=z.Outfit,Body=restored.GetValueOrDefault(z.Body)}).ToList();
            _chunks.Add((chunk.Cx,chunk.Cz),chunk);
        }
    }
}
