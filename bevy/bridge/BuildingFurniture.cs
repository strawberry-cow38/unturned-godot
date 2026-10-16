using Godot;
using UnturnedSim;
using System.Text.Json;

namespace UnturnedBevy;

/// <summary>Original ShowFurniture's preview assembly. These values do not place
/// a map prop, register power devices, tick lights or mutate saved room records.</summary>
public sealed partial class BuildingFurniture(string contentRoot)
{
    readonly List<BuildingFurnitureNode> _furnitureNodes = new();
    readonly List<BuildingFurnitureSwitch> _switches = new();
    readonly List<BuildingFurnitureLamp> _ceilingLights = new();
    readonly List<BuildingFurnitureOutlet> _outlets = new();
    readonly List<PropDesignation> Furniture = new();
    readonly List<RoomDesignation> Designations = new();
    readonly Dictionary<string, BuildingFurnitureMesh> meshes = new(StringComparer.Ordinal);
    BuildingDoodads _doodads;
    BuildingDoodads Doodads => _doodads ??= new(contentRoot);
    int sourceIndex;
    public IReadOnlyList<BuildingFurnitureNode> Nodes => _furnitureNodes;
    public IReadOnlyList<BuildingFurnitureSwitch> Switches => _switches;
    public IReadOnlyList<BuildingFurnitureLamp> CeilingLights => _ceilingLights;
    public IReadOnlyList<BuildingFurnitureOutlet> Outlets => _outlets;
    public void Build(IEnumerable<PropDesignation> props, IEnumerable<RoomDesignation> rooms)
    {
        Furniture.Clear(); Furniture.AddRange(props);
        Designations.Clear(); Designations.AddRange(rooms);
        ShowFurniture();
    }
    internal void AddChild(BuildingFurnitureNode node) => node.PropIndex = sourceIndex;
    BuildingFurnitureMesh MeshPath(string relative)
    {
        if (!meshes.TryGetValue(relative, out var mesh))
        {
            string path = Path.Combine(contentRoot, relative);
            // ObjMesh.Load returns null for a file without faces; source then
            // takes the same missing-mesh branch as for an absent file.
            var source = File.Exists(path) ? BuildingDoorMesh.Load(path) : null;
            meshes[relative] = mesh = source == null ? null : new(relative, source, null);
        }
        return mesh;
    }
    BuildingFurnitureMesh PreviewMesh(string name) => name == null ? null : MeshPath("objects/" + (name == StoreShelfName ? "Shelf_1" : name) + ".obj");
    // The material source identity is preserved. Its original editor loader
    // (texture/alpha-sidecar/filtering) belongs to the eventual native consumer.
    string PreviewMaterial(string name) => name;
}

public sealed record BuildingFurnitureMesh(string File, BuildingDoorMesh Source, BuildingDecorMesh Decor)
{
    public BuildingAabb GetAabb() => (Source ?? new BuildingDoorMesh(Decor.Vertices, new(1,1,1))).GetAabb();
}
public class BuildingFurnitureNode
{
    public int PropIndex;
    public string Name;
    public BuildingFurnitureMesh Mesh;
    public string MaterialOverride;
    public BuildingTransform Transform = new(Basis.Identity, Vector3.Zero);
    public BuildingTransform GlobalTransform { get => Transform; set => Transform = value; }
    public readonly List<BuildingStockItem> StockItems = new();
}
public sealed record BuildingStockItem(ushort Item, BuildingTransform Local);
public sealed record BuildingFurnitureLook(Vector3 Position, Vector3 Size, uint Layer, uint Mask);

public sealed partial class BuildingFurnitureSwitch : BuildingFurnitureNode
{
    public BuildingFurnitureMesh BaseMesh, RockerMesh;
    public RoomElectrical Record;
    public int SwitchIndex = -1;
    public readonly List<BuildingFurnitureLamp> Lamps = new();
    public bool On => Record?.LightsOn ?? true;
    public bool Down => Record != null && SwitchIndex >= 0 && SwitchIndex < Record.Down.Count ? Record.Down[SwitchIndex] : true;
    public float RockerAngle => Down ? RockerDownDegrees : RockerUpDegrees;
    public static BuildingFurnitureSwitch Make(BuildingFurnitureMesh baseMesh, BuildingFurnitureMesh rocker, string material)
        => new() { Name="LightSwitch", BaseMesh=baseMesh, RockerMesh=rocker, MaterialOverride=material };
    public void Bind(RoomElectrical record, int index, IEnumerable<BuildingFurnitureLamp> lamps, IEnumerable<BuildingFurnitureSwitch> siblings)
    {
        Record=record; SwitchIndex=index; Lamps.Clear(); Lamps.AddRange(lamps);
        foreach(var lamp in Lamps) lamp.Switched=On;
        // Source siblings only re-apply their own rocker; the shared record
        // remains the data source. No press or authority mutation occurs here.
    }
}

public sealed partial class BuildingFurnitureLamp : BuildingFurnitureNode
{
    public enum Kind { Generic }
    public BuildingFurnitureNode Fixture;
    public bool Switched = true;
    float _worn=1, _reactionFrac;
    public float Wear => _worn;
    public float ReactionFraction => _reactionFrac;
    public static BuildingFurnitureLamp Make(Vector3 world, BuildingFurnitureNode fixture, Kind kind)
    {
        var result=new BuildingFurnitureLamp { Transform=new(Basis.Identity,world), Fixture=fixture };
        result.InitJitter(world); return result;
    }
}

public sealed record BuildingOutletPort(Vector3 Position, float Watts, string Label);
public sealed partial class BuildingFurnitureOutlet : BuildingFurnitureNode
{
    public float Watts;
    public string GridName = "";
    public readonly List<BuildingOutletPort> Ports = new();
    public void AddOutputPort(Vector3 local, string label) => Ports.Add(new(local,Watts,label));
    public float SharedBudget => Ports.Count>1 ? Watts : 0;
    public static BuildingFurnitureOutlet Attach(BuildingFurniture parent, Vector3 at, Basis basis, Vector3 port, float watts, string name, BuildingFurnitureMesh mesh)
    {
        var result=new BuildingFurnitureOutlet { GridName=name, Transform=new(basis,at), Watts=watts, Mesh=mesh };
        result.Ports.Add(new(port,watts,"Grid Power")); parent.AddChild(result); return result;
    }
}

/// <summary>Original DoodadKitView.Kids transform order, with the JSON/resource
/// boundary represented by values. Stock children remain presentation-only.</summary>
sealed class BuildingDoodads
{
    readonly Dictionary<string,List<BuildingStockItem>> kids = new(StringComparer.Ordinal);
    public BuildingDoodads(string contentRoot)
    {
        string path=Path.Combine(contentRoot,"objects/doodad_kit_children.json");
        if(!File.Exists(path)) return;
        using var parsed=JsonDocument.Parse(File.ReadAllText(path));
        if(parsed.RootElement.ValueKind!=JsonValueKind.Object) return;
        var native=new Basis(new Vector3(1,0,0),new Vector3(0,0,1),new Vector3(0,-1,0));
        foreach(var row in parsed.RootElement.EnumerateObject())
        {
            if(row.Name.StartsWith('_')) continue;
            var list=new List<BuildingStockItem>();
            foreach(var c in row.Value.EnumerateArray())
            {
                var p=c.GetProperty("position");var r=c.GetProperty("rotation_deg");float s=c.GetProperty("scale").GetSingle();
                var rx=new Basis(Vector3.Right,Mathf.DegToRad(r[0].GetSingle()));
                var ry=new Basis(Vector3.Up,Mathf.DegToRad(r[1].GetSingle()));
                var rz=new Basis(Vector3.Back,Mathf.DegToRad(r[2].GetSingle()));
                var basis=rz*ry*rx*native*Basis.FromScale(new(s,s,s));
                list.Add(new(unchecked((ushort)c.GetProperty("item").GetInt32()),new(basis,new(p[0].GetSingle(),p[1].GetSingle(),p[2].GetSingle()))));
            }
            kids[row.Name]=list;
        }
    }
    public bool Has(string name) => name!=null && kids.ContainsKey(name);
    public void Attach(BuildingFurnitureNode node,string name)
    {
        if(node!=null && kids.TryGetValue(name??"",out var values)) node.StockItems.AddRange(values);
    }
}
