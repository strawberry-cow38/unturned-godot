using System.Text.Json;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    // Export read-only source data; init starts authority separately. The parsed
    // source map is local to this request rather than retained across sessions.
    object ReadFiniteMap(JsonElement request)
    {
        string root=Path.GetFullPath(Text(request,"directory"));
        string content=Path.GetFullPath(Text(request,"contentRoot"));
        FiniteIslandFiles files;
        if(Bool(request,"create"))
            files=FiniteIslandFiles.Generate(content,Text(request,"environmentRoot"),root,
                Text(request,"name"),Int(request,"seed",1337),Int(request,"tilesX",3),Int(request,"tilesZ",3),Bool(request,"lakes"));
        else files=FiniteIslandFiles.Open(content,root);
        files.PrepareRuntime();
        var initial = FiniteSpawnPool.Load(files.PlayersPath).Initial();
        return new { directory=root, descriptor=files.Info, generated=files.Generated!=null,
            initialSpawn=initial is UnityEngine.Vector3 at ? Array(at) : null,
            chart=new {width=FiniteIslandChart.Res,height=FiniteIslandChart.Res,
                raw=Path.Combine(root,$"island_{files.Info.Seed}_map.rgb"),
                png=Path.Combine(root,$"island_{files.Info.Seed}_map.png")},
            chunks=new[]{files.Surface.ChunkCountX,files.Surface.ChunkCountZ} };
    }
}
