using NUnit.Framework;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture]
public sealed class FiniteIslandMeshTests
{
    static FiniteIslandSurface Surface()
    {
        var layout=new FiniteIslandLayout(1,1);
        for(int x=0;x<layout.Width;x++) for(int z=0;z<layout.Height;z++)
            layout.Grid[x,z]=.5f+(x*2+z*3)/2048f;
        return new(layout);
    }

    [Test] public void OriginalAxesNormalsAndChunkEdgesStayShared()
    {
        var surface=Surface();var first=surface.BuildChunk(0,0);var next=surface.BuildChunk(1,0);
        Assert.That(first.Positions,Has.Length.EqualTo(49*49));
        Assert.That(first.Indices,Has.Length.EqualTo(48*48*6));
        Assert.That(first.Positions[1].Z,Is.EqualTo(-4));
        Assert.That(first.Positions[49].X,Is.EqualTo(4));
        Assert.That(first.Positions[49+1].Y,Is.EqualTo(5));
        Assert.That(first.Indices.Take(6),Is.EqualTo(new[]{0,1,49,49,1,50}),"Godot winding; native conversion reverses each triangle once");
        for(int z=0;z<49;z++)
        {
            Assert.That(next.Positions[z],Is.EqualTo(first.Positions[48*49+z]));
            Assert.That(next.Normals[z],Is.EqualTo(first.Normals[48*49+z]));
            Assert.That(next.Uvs[z],Is.EqualTo(first.Uvs[48*49+z]));
        }
        Assert.That(surface.BuildChunk(5,5).Positions,Has.Length.EqualTo(17*17));
        Assert.That(surface.BuildChunk(6,0),Is.Null);Assert.That(surface.BuildChunk(0,-1),Is.Null);
    }

    [Test] public void HoleRemovesExactlyOneQuadWithoutRenumberingNeighbours()
    {
        var surface=Surface();var before=surface.BuildChunk(0,0);
        Assert.That(surface.SetHole(3,4,true),Is.True);
        var after=surface.BuildChunk(0,0);
        Assert.That(after.Positions,Is.EqualTo(before.Positions));
        Assert.That(after.Normals,Is.EqualTo(before.Normals));
        int offset=(3*48+4)*6;
        Assert.That(after.Indices,Is.EqualTo(before.Indices.Take(offset).Concat(before.Indices.Skip(offset+6))));
        surface.SetHole(3,4,false);
        Assert.That(surface.BuildChunk(0,0).Indices,Is.EqualTo(before.Indices));
    }

    [Test] public void FullyDugChunkHasNoTrianglesAndFillRestoresTheOriginalSurface()
    {
        var surface=Surface();var original=surface.BuildChunk(0,0);
        for(int x=0;x<48;x++) for(int z=0;z<48;z++) surface.SetHole(x,z,true);
        Assert.That(surface.BuildChunk(0,0).Indices,Is.Empty);
        Assert.That(surface.BuildChunk(1,0).Indices,Has.Length.EqualTo(48*48*6));
        for(int x=0;x<48;x++) for(int z=0;z<48;z++) surface.SetHole(x,z,false);
        Assert.That(surface.BuildChunk(0,0).Indices,Is.EqualTo(original.Indices));
    }

    [Test] public void RiverSubdividesOnlyItsQuadsAndUsesOriginalAnalyticHeights()
    {
        var surface=Surface();var plain=surface.BuildChunk(0,0);
        string path=Path.GetTempFileName();
        try
        {
            using(var writer=new BinaryWriter(File.Create(path)))
            {
                writer.Write(3);writer.Write(1);writer.Write(2);
                foreach(var p in new[]{new[]{20f,60f,-50f},new[]{20f,60f,-100f}})
                    foreach(float v in p) writer.Write(v);
                writer.Write(8f);writer.Write(4f);writer.Write(0);
            }
            surface.LoadRivers(path);
            var carved=surface.BuildChunk(0,0);
            Assert.That(carved.Positions.Length,Is.GreaterThan(plain.Positions.Length));
            Assert.That(carved.Indices.Length,Is.GreaterThan(plain.Indices.Length));
            Assert.That(carved.Positions.Skip(plain.Positions.Length).Any(p=>p.X%4!=0 || p.Z%4!=0),Is.True);
            foreach(var p in carved.Positions.Skip(plain.Positions.Length))
                Assert.That(p.Y,Is.EqualTo(surface.SurfaceHeightWorld(p.X,p.Z)));
            // A hole over a river removes its entire16-subquad patch, while
            // preserving the regular neighbouring grid and other river patches.
            surface.SetHole(5,20,true);
            Assert.That(surface.BuildChunk(0,0).Indices.Length,Is.EqualTo(carved.Indices.Length-16*6));
            surface.SetHole(5,20,false);surface.LoadRivers(path);
            Assert.That(surface.BuildChunk(0,0).Positions,Is.EqualTo(carved.Positions),"reopen does not cut the river twice");
        }
        finally { File.Delete(path); }
    }
}
