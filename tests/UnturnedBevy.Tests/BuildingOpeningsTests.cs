using System.Text.Json;
using Godot;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedSim;

namespace UnturnedBevyTests;

[TestFixture]
public sealed class BuildingOpeningsTests
{
    static string Root()
    {
        for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)
            if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return d.FullName;
        throw new DirectoryNotFoundException();
    }
    static Vector3 Vector(JsonElement value)
    {
        var p=value.EnumerateArray().Select(v=>v.GetSingle()).ToArray();return new(p[0],p[1],p[2]);
    }
    static void Near(Vector3 actual,JsonElement expected,string label,float tolerance=0.0003f)
    {
        var e=Vector(expected);
        Assert.That(actual.X,Is.EqualTo(e.X).Within(tolerance),label+" X");
        Assert.That(actual.Y,Is.EqualTo(e.Y).Within(tolerance),label+" Y");
        Assert.That(actual.Z,Is.EqualTo(e.Z).Within(tolerance),label+" Z");
    }
    static void Pose(BuildingTransform actual,JsonElement expected,string label)
    {
        Near(actual.Position,expected.GetProperty("position"),label+" position");
        var basis=expected.GetProperty("basis");
        Near(actual.Basis.X,basis[0],label+" basisX",2e-6f);
        Near(actual.Basis.Y,basis[1],label+" basisY",2e-6f);
        Near(actual.Basis.Z,basis[2],label+" basisZ",2e-6f);
    }
    [Test] public void OriginalGodotFittedDoorHierarchyAndCollidersMatchAllOpeningCases()
    {
        string root=Root();var palettes=new BuildingPalettes(Path.Combine(root,"game/content"));
        var assets=new BuildingOpenings(Path.Combine(root,"game/content"));
        using var oracle=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/UnturnedBevy.Tests/fixtures/building-openings-godot.json")));
        int cases=0,leaves=0;
        foreach(var row in oracle.RootElement.EnumerateArray())
        {
            var r=row.GetProperty("input");var wall=new BuildingSurface(palettes) {
                Position=Vector(r.GetProperty("position")),RotationDegrees=Vector(r.GetProperty("euler")),Thickness=r.GetProperty("thickness").GetSingle() };
            var opening=new WallOpening {U=r.GetProperty("u").GetSingle(),V=r.GetProperty("v").GetSingle(),
                Width=r.GetProperty("width").GetSingle(),Height=r.GetProperty("height").GetSingle(),Depth=999,
                DoorProp=r.GetProperty("prop").GetString(),DoorHingeAtStart=r.GetProperty("hinge").GetBoolean(),
                DoorOpensFront=r.GetProperty("front").GetBoolean(),DoorAction=(DoorAction)r.GetProperty("action").GetInt32()};
            var host=assets.Door(wall,opening);string label=$"case {cases++} {opening.DoorProp}/{opening.DoorAction}";
            if(row.GetProperty("host").ValueKind==JsonValueKind.Null) {Assert.That(host,Is.Null,label);continue;}
            Assert.That(host,Is.Not.Null,label);Pose(host.Transform,row.GetProperty("host"),label);
            var expected=row.GetProperty("leaves");Assert.That(host.Leaves.Length,Is.EqualTo(expected.GetArrayLength()));
            for(int i=0;i<host.Leaves.Length;i++)
            {
                var leaf=host.Leaves[i];var e=expected[i];++leaves;
                Near(leaf.Pivot,e.GetProperty("pivot"),label+" pivot",2e-6f);
                Near(leaf.Axis,e.GetProperty("axis"),label+" axis",2e-6f);
                Near(leaf.Slide,e.GetProperty("slide"),label+" slide",2e-6f);
                Near(leaf.ColliderSize,e.GetProperty("colliderSize"),label+" collider",2e-6f);
                Near(leaf.Mesh.GetAabb().Position,e.GetProperty("min"),label+" min",2e-6f);
                Near(leaf.Mesh.GetAabb().End,e.GetProperty("max"),label+" max",2e-6f);
                Assert.That(leaf.Angle,Is.EqualTo(e.GetProperty("angle").GetSingle()));
                Assert.That(leaf.Duration,Is.EqualTo(e.GetProperty("duration").GetSingle()));
                Pose(leaf.Body,e.GetProperty("body"),label+" body");
                Pose(leaf.ClosedMesh(wall.GlobalTransform*host.Transform),e.GetProperty("poses")[0].GetProperty("mesh"),label+" closed world mesh");
            }
            opening.DoorOpen=true;
            Assert.That(assets.Door(wall,opening).Position,Is.EqualTo(host.Position),"source does not apply unused DoorOpen token");
        }
        Assert.That(cases,Is.EqualTo(19));Assert.That(leaves,Is.GreaterThan(18));
    }
    [Test] public void DoorBoundsUseReferencedFacesAndScaleWithoutMovingTheHingeAncestor()
    {
        string path=Path.Combine(TestContext.CurrentContext.WorkDirectory,"building-door-bounds.obj");
        try {
            File.WriteAllText(path,"v 100 100 100\nv -2 0 -1\nv 2 0 -1\nv 0 1 1\nf 2 3 4\n");
            var mesh=BuildingDoorMesh.Load(path);var scaled=mesh.Scaled(new(2,1,3));
            Assert.That(mesh.GetAabb().End.X,Is.EqualTo(2));
            Assert.That(scaled.GetAabb().Size.X,Is.EqualTo(8));
            Assert.That(scaled.GetAabb().Size.Z,Is.EqualTo(6));
            Assert.That(mesh.GetAabb().Size.X,Is.EqualTo(4),"shared source geometry remains unchanged");
        } finally {File.Delete(path);}
    }
    [Test] public void GlassUsesRawOpeningSizeSourceTintHealthAndInteriorConvention()
    {
        var wall=new BuildingSurface(new BuildingPalettes(Path.Combine(Root(),"game/content"))){Thickness=.7f};
        var o=new WallOpening { U=1,V=2,Width=3,Height=1.5f,Glazed=true };
        var pane=BuildingOpenings.Pane(wall,o);
        Assert.That(pane.Position,Is.EqualTo(new Vector3(2.5f,2.75f,0)));
        Assert.That(pane.Size,Is.EqualTo(new Vector3(3,1.5f,.04f)));
        Assert.That(pane.Color,Is.EqualTo(new Color(.62f,.73f,.78f,.26f)));
        Assert.That(pane.Health,Is.EqualTo(1));Assert.That(pane.Covered,Is.False);
        wall.Thickness=.5f;o.GlassHp=.5f;o.GlassTint=0x406080;o.GlassIndestructible=true;
        pane=BuildingOpenings.Pane(wall,o);
        Assert.That(pane.Color,Is.EqualTo(new Color(64/255f,96/255f,128/255f,.26f)));
        Assert.That(pane.Covered&&pane.Indestructible,Is.True);Assert.That(pane.Health,Is.EqualTo(1));
        o.GlassHp=27;Assert.That(BuildingOpenings.Pane(wall,o).Health,Is.EqualTo(27));
        o.GlassBroken=true;Assert.That(BuildingOpenings.Pane(wall,o),Is.Null);
        o.GlassBroken=false;o.Glazed=false;Assert.That(BuildingOpenings.Pane(wall,o),Is.Null);
    }

    [Test] public void OriginalGodotBoxGlassDimensionsAxisAndSnappedTieOrderMatchElevenCases()
    {
        string root=Root();var wall=new BuildingSurface(new BuildingPalettes(Path.Combine(root,"game/content"))){Thickness=.7f};
        using var oracle=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/UnturnedBevy.Tests/fixtures/building-glass-godot.json")));
        foreach(var row in oracle.RootElement.EnumerateArray()) {
            var size=row.GetProperty("size");var pane=BuildingOpenings.Pane(wall,new WallOpening{Glazed=true,Width=size[0].GetSingle(),Height=size[1].GetSingle()});
            Near(pane.Size,row.GetProperty("box"),"box",1e-7f);Near(pane.Axis,row.GetProperty("axis"),"dominant axis",1e-7f);
            Assert.That(pane.Health,Is.EqualTo(row.GetProperty("health").GetSingle()));
            var color=row.GetProperty("color");Assert.That(new[]{pane.Color.R,pane.Color.G,pane.Color.B,pane.Color.A},Is.EqualTo(color.EnumerateArray().Select(x=>x.GetSingle()).ToArray()));
        }
    }

}
