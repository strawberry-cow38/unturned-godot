namespace UnturnedBevy;

public sealed partial class FiniteIslandFiles
{
    /// <summary>Disposable native transport files, rebuilt from the original
    /// authored files when reopened. They never replace grid/paint/river/prop
    /// persistence. Chunk indices retain Godot winding until the native adapter.</summary>
    public void PrepareRuntime()
    {
        string cache=Path.Combine(Root,".native");
        Directory.CreateDirectory(cache);
        for(int x=0;x<Surface.ChunkCountX;x++) for(int z=0;z<Surface.ChunkCountZ;z++)
        {
            var chunk=Surface.BuildChunk(x,z);
            WriteCache(Path.Combine(cache,$"chunk_{x}_{z}.bin"),writer=>{
            writer.Write(0x43464755u);writer.Write(1); // UGFC, local format version
            writer.Write(chunk.Positions.Length);writer.Write(chunk.Indices.Length);
            foreach(var v in chunk.Positions) {writer.Write(v.X);writer.Write(v.Y);writer.Write(v.Z);}
            foreach(var v in chunk.Normals) {writer.Write(v.X);writer.Write(v.Y);writer.Write(v.Z);}
            foreach(var v in chunk.Uvs) {writer.Write(v.X);writer.Write(v.Y);}
            foreach(var v in chunk.Colors) {writer.Write(v.R);writer.Write(v.G);writer.Write(v.B);writer.Write(v.A);}
            foreach(int index in chunk.Indices) writer.Write(index);
            });
        }
        WriteCache(Path.Combine(cache,"surface.bin"),Surface.WriteNativeSurface);
        var spawns=new FiniteIslandPlayerSpawns();spawns.Load(PlayersPath);
        var points=spawns.Points.Select(p=>new { position=new[]{p.Pos.X,p.Pos.Y,p.Pos.Z},yaw=p.Yaw,alt=p.IsAlt,
            type=p.Type,ground=Surface.SampleHeight(p.Pos.X,p.Pos.Z) }).ToArray();
        WriteCache(Path.Combine(cache,"players.json"),w=>w.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(points)));
    }
    static void WriteCache(string path,Action<BinaryWriter> write)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var writer=new BinaryWriter(File.Create(temporary))) write(writer);
            File.Move(temporary,path,true);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed partial class FiniteIslandSurface
{
    internal void WriteNativeSurface(BinaryWriter writer)
    {
        writer.Write(0x53464755u);writer.Write(2); // UGFS; normalized base + analytic river
        writer.Write(Width);writer.Write(Height);
        // Keep source negative-Z rows and normalized base heights. Baking the
        // river into coarse samples would change SampleHeight between vertices.
        for(int z=0;z<Height;z++) for(int x=0;x<Width;x++) writer.Write(Grid[x,z]);
        for(int z=0;z<Height-1;z++) for(int x=0;x<Width-1;x++) writer.Write(Layers[x,z]);
        for(int z=0;z<Height-1;z++) for(int x=0;x<Width-1;x++) writer.Write(IsHole(x,z));
        writer.Write(RiverBlendFactor);writer.Write(RiverBlendDrop);
        var rivers=RiverPolylines();writer.Write(rivers.Count);
        foreach(var (points,half,depth) in rivers)
        {
            writer.Write(half);writer.Write(depth);writer.Write(points.Count);
            foreach(var p in points) {writer.Write(p.X);writer.Write(p.Y);writer.Write(p.Z);}
        }
    }
}
