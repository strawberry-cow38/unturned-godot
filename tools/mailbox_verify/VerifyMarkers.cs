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
            using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"street_mailbox_blue_manifest.json")));
            foreach(var f in manifest.RootElement.GetProperty("files").EnumerateObject())
            {
                string filename=Path.Combine(dir,f.Name);Check(File.Exists(filename),"file exists "+filename);
                string sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filename))).ToLowerInvariant();
                Check(sha==f.Value.GetString(),"approved/runtime bytes "+filename);
            }
            // Same transform as EditorObjects.Upright(0), not a resource/tree transform.
            var upright=new Basis(Vector3.Right,Mathf.DegToRad(270));
            foreach(var spec in new[]{("Street_Mailbox_Blue_0",110,1.5065f),("Street_Mailbox_Blue_0_lod1",80,1.5065f)})
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
                Check(uv.All(u=>(u.Y==.25f||u.Y==.75f) && (u.X==.125f||u.X==.375f||u.X==.625f||u.X==.875f)),"V-flipped palette centres "+spec.Item1);
                Check(uv.Any(u=>u==new Vector2(.875f,.25f)),"envelope white present "+spec.Item1);
                Check(ObjMesh.TrimeshShape(mesh) is ConcavePolygonShape3D,"native collider faces available "+spec.Item1);
            }
            int[][] rgb={new[]{59,103,152},new[]{159,159,159},new[]{39,39,39},new[]{196,196,196},new[]{159,41,41},new[]{202,176,65},new[]{150,150,150},new[]{53,53,53}};
            using var im=Image.LoadFromFile(Path.Combine(dir,"Street_Mailbox_Blue_0_tex.png"));
            Check(im!=null && im.GetWidth()==4 && im.GetHeight()==2,"palette image");
            for(int j=0;j<8;j++)Check(im.GetPixel(j%4,j/4).IsEqualApprox(new Color(rgb[j][0]/255f,rgb[j][1]/255f,rgb[j][2]/255f,1)),"palette texel "+j);
            GD.Print($"MAILBOX_NATIVE_LOAD_PASS checks={checks}");GetTree().Quit(0);
        }
        catch(Exception e){GD.PushError(e.ToString());GetTree().Quit(1);}
    }
}
