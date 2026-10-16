using Godot;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedSim;
using System.Text.Json;

namespace UnturnedBevyTests;

[TestFixture]
public sealed class BuildingFurnitureTests
{
    static string Root()
    {
        for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)
            if(Directory.Exists(Path.Combine(d.FullName,"game/content"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
    static void Near(Vector3 actual,JsonElement expected,string label,float tolerance=0.0003f)
    {
        Assert.That(actual.X,Is.EqualTo(expected[0].GetSingle()).Within(tolerance),label+" X");
        Assert.That(actual.Y,Is.EqualTo(expected[1].GetSingle()).Within(tolerance),label+" Y");
        Assert.That(actual.Z,Is.EqualTo(expected[2].GetSingle()).Within(tolerance),label+" Z");
    }
    static void Pose(BuildingTransform actual,JsonElement expected,string label)
    {
        Near(actual.Position,expected.GetProperty("position"),label);
        var basis=expected.GetProperty("basis");
        Near(actual.Basis.X,basis[0],label+" basisX",2e-6f);
        Near(actual.Basis.Y,basis[1],label+" basisY",2e-6f);
        Near(actual.Basis.Z,basis[2],label+" basisZ",2e-6f);
    }
    [Test]
    public void ActualGodotFurnitureOrderTransformsStockChildrenAndMissingElectricalSlotsMatch()
    {
        string root=Root();
        using var oracle=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/UnturnedBevy.Tests/fixtures/building-furniture-godot.json")));
        int cases=0;
        foreach(var row in oracle.RootElement.EnumerateArray())
        {
            var input=row.GetProperty("input");var props=new List<PropDesignation>();
            foreach(var p in input.GetProperty("props").EnumerateArray())
            {
                var at=p.GetProperty("at");RuntimeDecor gen=null;
                if(p.TryGetProperty("gen",out var g))gen=new(Enum.Parse<RuntimeDecorKind>(g.GetProperty("kind").GetString()),1,g.GetProperty("width").GetSingle(),g.GetProperty("height").GetSingle(),g.GetProperty("open").GetBoolean(),g.GetProperty("style").GetString());
                props.Add(new(p.GetProperty("name").GetString(),at[0].GetSingle(),at[1].GetSingle(),at[2].GetSingle(),p.GetProperty("yaw").GetSingle(),gen));
            }
            var missing=input.GetProperty("missing").EnumerateArray().Select(x=>x.GetString()).ToHashSet();
            string content=Path.Combine(TestContext.CurrentContext.WorkDirectory,"furniture-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(content,"objects"));
            try
            {
                foreach(string name in props.Select(p=>p.Name).Concat(new[]{"Light_Switch_0_Base","Light_Switch_0_Rocker"}).Distinct())
                {
                    string original=Path.Combine(root,"game/content/objects",name+".obj");
                    if(!missing.Contains(name)&&File.Exists(original)) File.CreateSymbolicLink(Path.Combine(content,"objects",name+".obj"),original);
                }
                File.Copy(Path.Combine(root,"game/content/objects/doodad_kit_children.json"),Path.Combine(content,"objects/doodad_kit_children.json"));
                var electrical=new RoomElectrical();var down=input.GetProperty("down");
                electrical.Add(new(ElectricalKind.Switch,0,0,0,0,0),down[0].GetBoolean());
                electrical.Add(new(ElectricalKind.Switch,2,0,0,0,0),down[1].GetBoolean());
                electrical.Add(new(ElectricalKind.Outlet,1,0,0,0,0,1500));electrical.Lights.AddRange(new[]{0,1});
                var rooms=new[]{new RoomDesignation(RoomKind.Kitchen,0,0,0,null,electrical)};
                string before=WallSave.Write(new List<WallPlan>(),BuildingKind.Residential,rooms,null,props);
                var view=new BuildingFurniture(content);view.Build(props,rooms);
                var expected=row.GetProperty("nodes");Assert.That(view.Nodes.Count,Is.EqualTo(expected.GetArrayLength()));
                for(int i=0;i<view.Nodes.Count;i++)
                {
                    var node=view.Nodes[i];var e=expected[i];string label=$"case {cases} node {i}";
                    Pose(node.GlobalTransform,e.GetProperty("pose"),label);
                    if(e.GetProperty("name").GetString().StartsWith("Furniture_"))Assert.That(node.Name,Is.EqualTo(e.GetProperty("name").GetString()));
                    var stock=e.GetProperty("stock");Assert.That(node.StockItems.Count,Is.EqualTo(stock.GetArrayLength()));
                    for(int k=0;k<node.StockItems.Count;k++)
                    {
                        Assert.That($"Stock_{node.StockItems[k].Item}_{k}",Is.EqualTo(stock[k].GetProperty("name").GetString()));
                        Pose(node.StockItems[k].Local,stock[k].GetProperty("pose"),label+" stock");
                    }
                    if(node is BuildingFurnitureSwitch sw)
                    {
                        var s=e.GetProperty("sw");Assert.That(sw.Down,Is.EqualTo(s.GetProperty("down").GetBoolean()));
                        Assert.That(sw.On,Is.EqualTo(s.GetProperty("on").GetBoolean()));Assert.That(sw.RockerAngle,Is.EqualTo(s.GetProperty("angle").GetSingle()).Within(1e-5));
                        Assert.That(sw.Lamps.Count,Is.EqualTo(s.GetProperty("lamps").GetInt32()));
                        Pose(new(new Basis(Vector3.Right,Mathf.DegToRad(sw.RockerAngle)),BuildingFurnitureSwitch.RockerPivot),s.GetProperty("pivot"),label+" rocker");
                    }
                    if(node is BuildingFurnitureLamp lamp)
                    {
                        var l=e.GetProperty("light");Assert.That(lamp.Switched,Is.EqualTo(l.GetProperty("switched").GetBoolean()));
                        Assert.That(lamp.Wear,Is.EqualTo(l.GetProperty("wear").GetSingle()));Assert.That(lamp.ReactionFraction,Is.EqualTo(l.GetProperty("reaction").GetSingle()));
                    }
                    if(node is BuildingFurnitureOutlet outlet)
                    {
                        var o=e.GetProperty("outlet");Assert.That(outlet.Watts,Is.EqualTo(o.GetProperty("watts").GetSingle()));
                        Assert.That(outlet.SharedBudget,Is.EqualTo(1500));Assert.That(outlet.Ports.Count,Is.EqualTo(2));
                        for(int k=0;k<2;k++)Near(outlet.Ports[k].Position,o.GetProperty("ports")[k],label+" port");
                        Near(outlet.Look.Position,o.GetProperty("lookPosition"),label+" look");Near(outlet.Look.Size,o.GetProperty("lookSize"),label+" looksize");
                        Assert.That(outlet.Look.Layer,Is.EqualTo(o.GetProperty("layer").GetUInt32()));Assert.That(outlet.Look.Mask,Is.Zero);
                    }
                }
                Assert.That(view.CeilingLights.Select(l=>l==null?-1:view.Nodes.ToList().IndexOf(l)),Is.EqualTo(row.GetProperty("lightSlots").EnumerateArray().Select(x=>x.GetInt32())));
                Assert.That(WallSave.Write(new List<WallPlan>(),BuildingKind.Residential,rooms,null,props),Is.EqualTo(before),"projection never flips/spends/rewrites room records");
                Assert.That(view.Nodes.Select(n=>n.PropIndex),Is.EqualTo(missing.Count==0?new[]{0,2,2,3,4,4,5,6,7,8,11,11}:new[]{0,3,5,6,7,8,11,11}));
                view.Build(Array.Empty<PropDesignation>(),Array.Empty<RoomDesignation>());
                Assert.That(view.Nodes,Is.Empty);Assert.That(view.Switches,Is.Empty);Assert.That(view.CeilingLights,Is.Empty);Assert.That(view.Outlets,Is.Empty);
                cases++;
            }
            finally { Directory.Delete(content,true); }
        }
        Assert.That(cases,Is.EqualTo(3));
    }
}
