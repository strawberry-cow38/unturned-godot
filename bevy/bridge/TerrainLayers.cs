using System.Text.Json;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

public sealed partial class WorldGeometry
{
    sealed class LayerPatch
    { public float X,Z,Dx,Dz; public int Width,Height; public byte[] Layers; }
    readonly List<LayerPatch> _layerPatches=new();
    public bool HasTerrain => _terrainRoot!=null;
    public void AddLayers(JsonElement value)
    {
        var origin=value.GetProperty("origin"); var spacing=value.GetProperty("spacing");
        if(origin.GetArrayLength()!=2||spacing.GetArrayLength()!=2) throw new ArgumentException("Terrain layer origin/spacing require two components");
        var p=new LayerPatch{X=origin[0].GetSingle(),Z=origin[1].GetSingle(),Dx=spacing[0].GetSingle(),Dz=spacing[1].GetSingle(),
            Width=Int(value,"width"),Height=Int(value,"height"),Layers=value.GetProperty("layers").EnumerateArray().Select(v=>v.GetByte()).ToArray()};
        if(!float.IsFinite(p.X)||!float.IsFinite(p.Z)||!float.IsFinite(p.Dx)||!float.IsFinite(p.Dz)||p.Dx==0||p.Dz==0
            ||p.Width<1||p.Height<1||(long)p.Width*p.Height!=p.Layers.Length)throw new ArgumentException("Invalid terrain layer grid");
        _layerPatches.Add(p);
    }
    public byte DominantLayer(float x,float z)
    {
        LayerPatch best=null; float distance=float.PositiveInfinity;
        foreach(var p in _layerPatches)
        {
            float gx=(x-p.X)/p.Dx,gz=(z-p.Z)/p.Dz;
            float dx=(gx-Math.Clamp(gx,0,p.Width-1))*p.Dx,dz=(gz-Math.Clamp(gz,0,p.Height-1))*p.Dz;
            float d=dx*dx+dz*dz; if(d>=distance)continue;best=p;distance=d;if(d==0)break;
        }
        if(best==null)return 255;
        int ix=Math.Clamp((int)MathF.Round((x-best.X)/best.Dx),0,best.Width-1);
        int iz=Math.Clamp((int)MathF.Round((z-best.Z)/best.Dz),0,best.Height-1);
        return best.Layers[iz*best.Width+ix];
    }
}
