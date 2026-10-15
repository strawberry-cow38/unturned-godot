using System.Globalization;
using System.Text.Json;

namespace UnturnedBevy;

/// <summary>Original editor-map files in a separate writable directory. Shared
/// source art is read from contentRoot; generation never rewrites retail files.
/// The descriptor only locates those original files for the native consumer.</summary>
public sealed partial class FiniteIslandFiles
{
    const int MaxNameLength = 40;
    public sealed record Manifest(int Version, string Name, int TilesX, int TilesZ,
        int Seed, bool Water, float SeaLevel, string Foliage, string Resources);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Root { get; }
    public Manifest Info { get; }
    public FiniteIslandSurface Surface { get; }
    public FiniteIslandObjects Objects { get; }
    public FiniteIslandRoads Roads { get; }
    // Source EditorPlayMode populates generated entities only while the original
    // layout still exists. Reopen must not silently regenerate over saved edits.
    public FiniteIslandGeneration Generated { get; }
    public string PropsPath => Path.Combine(Root,"objects",$"editor_{Info.Name}.txt");
    public string HeightPath => Path.Combine(Root,"terrain",$"editor_{Info.Name}_heightmap.bin");
    public string RoadsPath => Path.Combine(Root,"roads",$"editor_{Info.Name}_Paths.dat");
    public string PlayersPath => Path.Combine(Root,"spawns",$"editor_{Info.Name}_player.txt");

    FiniteIslandFiles(string root,Manifest info,FiniteIslandSurface surface,
        FiniteIslandObjects objects,FiniteIslandRoads roads,FiniteIslandGeneration generated=null)
    { Root=root;Info=info;Surface=surface;Objects=objects;Roads=roads;Generated=generated; }

    public static FiniteIslandFiles Generate(string contentRoot,string environmentRoot,
        string destination,string name,int seed,int tilesX=3,int tilesZ=3,bool lakes=false)
    {
        string clean=Sanitise(name);
        if(clean==null || clean!=name) throw new ArgumentException("Map name must follow original editor naming rules",nameof(name));
        destination=Path.GetFullPath(destination);
        if(Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Generation requires a new map directory; reopen preserves existing authored data");
        string parent=Path.GetDirectoryName(destination);
        Directory.CreateDirectory(parent);
        string staging=Path.Combine(parent,".island-"+Guid.NewGuid().ToString("N"));
        try
        {
            foreach(string dir in new[]{"objects","terrain","roads","spawns"}) Directory.CreateDirectory(Path.Combine(staging,dir));
            var generated=FiniteIslandGeneration.Generate(contentRoot,environmentRoot,tilesX,tilesZ,seed,lakes);
            string foliage=FiniteIslandScatter.Bake(generated.Surface,seed,$"island_{seed}",contentRoot,staging);
            string resources=FiniteIslandScatter.BakeResources(generated.Surface,seed,$"island_{seed}",contentRoot,staging);
            var info=new Manifest(1,name,tilesX,tilesZ,seed,generated.Surface.HasWater,generated.Surface.SeaLevelY,foliage,resources);
            var files=new FiniteIslandFiles(staging,info,generated.Surface,generated.Objects,generated.Roads,generated);
            generated.Objects.SaveProps(files.PropsPath);
            generated.Surface.SaveHeightmap(files.HeightPath);
            generated.Roads.SavePaths(files.RoadsPath);
            File.Copy(Path.Combine(environmentRoot,"Roads.dat"),Path.Combine(staging,"roads","Roads.dat"));
            var spawns=new FiniteIslandPlayerSpawns();
            foreach(var (position,yaw) in generated.Players) spawns.Points.Add(new(position,yaw,false,0));
            spawns.Save(files.PlayersPath);
            var chart=FiniteIslandChart.Bake(generated.Surface,seed);
            File.WriteAllBytes(Path.Combine(staging,$"island_{seed}_map.rgb"),chart.Rgb);
            File.WriteAllText(Path.Combine(staging,chart.NodesName),chart.Nodes);
            File.WriteAllText(Path.Combine(staging,"objects",$"editor_{name}_proc.txt"),seed.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(staging,"finite.json"),JsonSerializer.Serialize(info,Json));
            Directory.Move(staging,destination);
            return new(destination,info,generated.Surface,generated.Objects,generated.Roads,generated);
        }
        catch
        {
            if(Directory.Exists(staging)) Directory.Delete(staging,true);
            throw;
        }
    }

    public static FiniteIslandFiles Open(string contentRoot,string directory)
    {
        directory=Path.GetFullPath(directory);
        var info=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory,"finite.json")),Json)
            ?? throw new InvalidDataException("Missing finite map descriptor");
        if(info.Version!=1 || Sanitise(info.Name)!=info.Name || string.IsNullOrEmpty(info.Name)
            || info.TilesX<1 || info.TilesX>8 || info.TilesZ<1 || info.TilesZ>8
            || !float.IsFinite(info.SeaLevel) || info.SeaLevel!=25.6f)
            throw new InvalidDataException("Invalid finite map descriptor");
        // Only generated source directories are named by this first adapter.
        // Never let a file descriptor select art outside its own map directory.
        if(info.Foliage!=null && info.Foliage!=$"foliage_island_{info.Seed}"
            || info.Resources!=null && info.Resources!=$"resources_island_{info.Seed}")
            throw new InvalidDataException("Invalid finite scatter directory");
        string marker=Path.Combine(directory,"objects",$"editor_{info.Name}_proc.txt");
        if(!int.TryParse(File.ReadAllText(marker).Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out int seed) || seed!=info.Seed)
            throw new InvalidDataException("Finite descriptor and original seed marker disagree");
        var surface=new FiniteIslandSurface(new FiniteIslandLayout(info.TilesX,info.TilesZ),info.Water);
        var objects=new FiniteIslandObjects(contentRoot);
        var roads=new FiniteIslandRoads(surface,Path.Combine(directory,"roads"));
        var files=new FiniteIslandFiles(directory,info,surface,objects,roads);
        if(!surface.LoadHeightmap(files.HeightPath)) throw new InvalidDataException("Saved finite dimensions do not match descriptor");
        objects.LoadProps(files.PropsPath);
        if(File.Exists(files.RoadsPath)) roads.LoadPaths(files.RoadsPath);
        return files;
    }
}
