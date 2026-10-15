using Godot;

namespace UnturnedGodot;

public partial class ZombieFlowField
{
    public void TranslateWorld(Vector3 delta)
    {_ox+=(int)(delta.X/Cell);_oz+=(int)(delta.Z/Cell);_target+=delta;}
    public sealed record Saved(int X,int Z,int Width,int Height,float[] Cost,bool[] Blocked,float[][] Flow,float[] Target,int BlockedCount);
    public Saved Capture()=>new(_ox,_oz,_w,_h,_cost,_blocked,_flow?.Select(v=>new[]{v.X,v.Y}).ToArray(),new[]{_target.X,_target.Y,_target.Z},BlockedCells);
    public void Restore(Saved s)
    {
        if(s==null)return;
        _ox=s.X;_oz=s.Z;_w=s.Width;_h=s.Height;_cost=s.Cost;_blocked=s.Blocked;
        _flow=s.Flow?.Select(v=>new Vector2(v[0],v[1])).ToArray();_target=new(s.Target[0],s.Target[1],s.Target[2]);BlockedCells=s.BlockedCount;
    }
}
