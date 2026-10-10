using Godot;
using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UnturnedGodot;

public partial class VerifyMarkers : Node
{
    int checks=0;
    void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    public override void _Ready()
    {
        try
        {
            string repo=Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"),"../.."));
            string dir=Path.Combine(repo,"game/content/objects");
            using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"traffic_markers_manifest.json")));
            foreach(var f in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                string filename=Path.Combine(repo,f.GetProperty("file").GetString());
                Check(File.Exists(filename),"file exists "+filename);
                string sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filename))).ToLowerInvariant();
                Check(sha==f.GetProperty("sha256").GetString(),"exact approved bytes "+filename);
            }
            // Same transform as EditorObjects.Upright(0), not a resource/tree transform.
            var upright=new Basis(Vector3.Right,Mathf.DegToRad(270));
            foreach(var spec in new[]{("Traffic_Cone_0",192,.82f),("Traffic_Cone_0_lod1",136,.82f),("Traffic_Barrel_0",312,1.155f),("Traffic_Barrel_0_lod1",216,1.155f)})
            {
                var mesh=ObjMesh.Load(Path.Combine(dir,spec.Item1+".obj"));
                Check(mesh!=null && mesh.GetSurfaceCount()==1,"real ObjMesh.Load "+spec.Item1);
                var arrays=mesh.SurfaceGetArrays(0);
                var vertices=arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var normals=arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var uv=arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                Check(vertices.Length==spec.Item2*3,"first-version triangle count "+spec.Item1);
                Check(normals.Length==vertices.Length && uv.Length==vertices.Length,"corner streams "+spec.Item1);
                var vs=vertices.Select(v=>upright*v).ToArray();
                Check(Math.Abs(vs.Min(v=>v.Y))<1e-6 && Math.Abs(vs.Max(v=>v.Y)-spec.Item3)<1e-6,"upright datum/height "+spec.Item1);
                Check(normals.All(n=>Math.Abs(n.Length()-1)<2e-6),"unit authored normals "+spec.Item1);
                // Godot fronts are clockwise: Load reverses native OBJ winding.
                bool winding=true;
                for(int i=0;i<vertices.Length;i+=3)
                {
                    var cross=(vertices[i+1]-vertices[i]).Cross(vertices[i+2]-vertices[i]);
                    if(cross.LengthSquared()<1e-20 || cross.Normalized().Dot(normals[i])>=0)winding=false;
                }
                Check(winding,"Godot clockwise front winding "+spec.Item1);
                Check(uv.All(u=>(u==new Vector2(.25f,.25f)||u==new Vector2(.75f,.25f)||u==new Vector2(.25f,.75f))),"V-flipped palette centres "+spec.Item1);
                Check(uv.Any(u=>u==new Vector2(.75f,.25f)),"white band present "+spec.Item1);
                Check(ObjMesh.TrimeshShape(mesh) is ConcavePolygonShape3D,"native collider faces available "+spec.Item1);
            }
            var expected=new[]{new Color(210/255f,114/255f,50/255f,1),new Color(200/255f,200/255f,200/255f,1),new Color(42/255f,42/255f,42/255f,1),new Color(170/255f,89/255f,43/255f,1)};
            foreach(string key in new[]{"Traffic_Cone_0","Traffic_Barrel_0"})
            {
                using var im=Image.LoadFromFile(Path.Combine(dir,key+"_tex.png"));
                Check(im!=null && im.GetWidth()==2 && im.GetHeight()==2,"palette image "+key);
                for(int j=0;j<4;j++) Check(im.GetPixel(j%2,j/2).IsEqualApprox(expected[j]),"palette texel "+key+" "+j);
            }
            GD.Print($"TRAFFIC_MARKERS_NATIVE_LOAD_PASS checks={checks}");GetTree().Quit(0);
        }
        catch(Exception e){GD.PushError(e.ToString());GetTree().Quit(1);}
    }
}
