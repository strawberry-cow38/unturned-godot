using System.Text.Json;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Complete source terrain grid for airborne AI, independent of render range.</summary>
public sealed partial class NpcHeliTerrainGrid
{
    readonly float[,] _grid;readonly int _gw,_gh;
    public float X,Z;public readonly float Dx,Dz;
    public sealed record Saved(float[] Origin,int Cells,float[] Spacing,float[] Heights);
    public NpcHeliTerrainGrid(JsonElement row)
    {
        var origin=row.GetProperty("origin");if(origin.GetArrayLength()!=2)throw new ArgumentException("Terrain grid requires X,Z origin");
        X=origin[0].GetSingle();Z=origin[1].GetSingle();
        var spacing=row.GetProperty("spacing");Dx=spacing.ValueKind==JsonValueKind.Array?spacing[0].GetSingle():spacing.GetSingle();
        Dz=spacing.ValueKind==JsonValueKind.Array?spacing[1].GetSingle():Dx;
        int cells=Int(row,"cells");if(cells<1||cells>4096||!float.IsFinite(X)||!float.IsFinite(Z)||!float.IsFinite(Dx)||!float.IsFinite(Dz)||Dx==0||Dz==0)
            throw new ArgumentException("Invalid NPC terrain grid dimensions");
        _gw=_gh=cells+1;var heights=row.GetProperty("heights");if(heights.GetArrayLength()!=(long)_gw*_gh)throw new ArgumentException("NPC terrain height count mismatch");
        _grid=new float[_gw,_gh];int index=0;foreach(var h in heights.EnumerateArray()){float value=h.GetSingle();if(!float.IsFinite(value))throw new ArgumentException("Nonfinite terrain height");_grid[index%_gw,index/_gw]=value;index++;}
    }
    public float DistanceSquared(float x,float z)
    {float gx=(x-X)/Dx,gz=(z-Z)/Dz;float a=(gx-Math.Clamp(gx,0,_gw-1))*Dx,b=(gz-Math.Clamp(gz,0,_gh-1))*Dz;return a*a+b*b;}
    public Saved Capture(){var h=new float[_gw*_gh];for(int z=0;z<_gh;z++)for(int x=0;x<_gw;x++)h[z*_gw+x]=_grid[x,z];return new([X,Z],_gw-1,[Dx,Dz],h);}
    public static NpcHeliTerrainGrid Restore(Saved value)=>new(JsonSerializer.SerializeToElement(new{origin=value.Origin,cells=value.Cells,spacing=value.Spacing,heights=value.Heights}));
    public void Translate(UnityEngine.Vector3 delta){X+=delta.x;Z+=delta.z;for(int z=0;z<_gh;z++)for(int x=0;x<_gw;x++)_grid[x,z]+=delta.y;}
}
