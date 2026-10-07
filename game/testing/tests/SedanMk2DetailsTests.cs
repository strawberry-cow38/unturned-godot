using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    public sealed class SedanMk2DetailsTests : GameTest
    {
        public override string Name => "vehicle.sedan_mk2_details";
        internal static float[] Hits(Mesh mesh, Vector3 origin, Vector3 direction)
        {
            var f=mesh.GetFaces(); var hits=new List<float>();
            for(int i=0;i+2<f.Length;i+=3)
            {
                var a=f[i];var e1=f[i+1]-a;var e2=f[i+2]-a;var h=direction.Cross(e2);float det=e1.Dot(h);
                if(Mathf.Abs(det)<.0000001f)continue;
                var delta=origin-a;float u=delta.Dot(h)/det;var q=delta.Cross(e1);float v=direction.Dot(q)/det;
                float d=e2.Dot(q)/det;
                if(u>=-.00001f&&v>=-.00001f&&u+v<=1.00001f&&d>=0f)hits.Add(d);
            }
            var unique=new List<float>();foreach(float d in hits.OrderBy(x=>x))
                if(unique.Count==0||Mathf.Abs(d-unique[^1])>.0002f)unique.Add(d);
            return unique.ToArray();
        }
        static int Palette(Vector2 uv)=>Mathf.Clamp((int)(uv.X*4),0,3)+4*Mathf.Clamp((int)(uv.Y*2),0,1);
        void PaintedWells(Mesh mesh,string label)
        {
            var arrays=mesh.SurfaceGetArrays(0);var p=arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var ns=arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();var uv=arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            int[] indices=arrays[(int)Mesh.ArrayType.Index].VariantType==Variant.Type.Nil
                ? Enumerable.Range(0,p.Length).ToArray():arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            int outer=0,inner=0;bool outerPaint=true,innerDark=true;
            for(int i=0;i+2<indices.Length;i+=3)
            {
                int[] ids={indices[i],indices[i+1],indices[i+2]};var pts=ids.Select(j=>p[j]).ToArray();
                var n=(ns[ids[0]]+ns[ids[1]]+ns[ids[2]]).Normalized();int c=Palette(uv[ids[0]]);
                foreach(float z in new[]{-2.2892f,1.949f})
                {
                    if(!pts.All(v=>Mathf.Abs(v.Z-z)<=.751f&&v.Y>=-.131f&&v.Y<=.751f&&Mathf.Abs(v.X)>=.794f))continue;
                    bool rim=pts.All(v=>Mathf.Abs(Mathf.Abs(v.X)-1.275f)<.00005f)&&Mathf.Abs(n.X)>.99f&&n.X*pts[0].X>0f;
                    bool arc=Mathf.Abs(n.X)<.001f&&n.Y>0f&&pts.All(v=>Mathf.Abs(n.Y*v.Y+n.Z*(v.Z-z)-.75f*Mathf.Cos(Mathf.Pi/16))<.00005f);
                    bool leg=Mathf.Abs(n.X)<.001f&&Mathf.Abs(n.Z)>.99f&&pts.All(v=>Mathf.Abs(n.Z*(v.Z-z)-.75f)<.00005f);
                    bool inside=Mathf.Abs(n.X)<.001f&&n.Y<0f&&pts.All(v=>Mathf.Abs(n.Y*v.Y+n.Z*(v.Z-z)+.70f*Mathf.Cos(Mathf.Pi/16))<.00005f);
                    if(rim||arc||leg){outer++;outerPaint&=c==0;}
                    if(inside){inner++;innerDark&=c==5;}
                }
            }
            T.Check($"{label}: actual exposed wheelhouse faces use paintable body texel ({outer})",outer>=20&&outerPaint);
            T.Check($"{label}: tyre-facing arch surfaces remain dark ({inner})",inner>=8&&innerDark);
        }
        public override IEnumerable<Step> Run()
        {
            var car=Vehicle.BuildSedanMk2(5);var puppet=Vehicle.BuildPuppetByName("sedan_mk2",5);
            try
            {
                foreach(var owner in new Node3D[]{car,puppet})
                {
                    var frame=SedanMk2Tests.InstalledMesh(owner,"sedan_mk2_frame.txt");
                    var dash=SedanMk2Tests.InstalledMesh(owner,"sedan_mk2_dashboard.txt");
                    var lid=SedanMk2Tests.InstalledMesh(owner,"sedan_mk2_trunk_lid.txt");
                    T.Check("V16 installed detail meshes exist",frame!=null&&dash!=null&&lid!=null);if(frame==null||dash==null||lid==null)continue;
                    PaintedWells(frame.Mesh,owner.Name);
                    foreach(var probe in new[]{new Vector3(0,.15f,0),new Vector3(0,.9f,.2173f)})
                    {
                        var hits=Hits(frame.Mesh,probe,Vector3.Right);
                        T.Check($"single continuous jamb, no detached shelf at Y={probe.Y}",hits.Length==2&&hits[0]>1.10f&&hits[1]>1.30f);
                    }
                    foreach(float x in new[]{0f,.5f,1.04f})
                    foreach(float y in new[]{1.064f,1.075f,1.085f})
                    {
                        var hit=Hits(frame.Mesh,new Vector3(x,y,-1.60f),Vector3.Back);
                        T.Check($"fixed cowl blocks engine-to-dash sight gap X={x}, Y={y}",hit.Any(d=>d<.061f));
                    }
                    var df=dash.Mesh.GetFaces();T.Check("dash caps/placement clear cowl and original engine bay",
                        df.All(v=>Mathf.Abs(v.X)<=1.0652f&&v.Z>=-1.5396f));
                    var lf=lid.Mesh.GetFaces();T.Check("one trunk skin wraps into rear face down to cargo lip",
                        lf.Min(v=>v.Y)<.491f&&lf.Min(v=>v.Y)>.489f&&lf.Max(v=>v.Z)>2.92f);
                    foreach(float y in new[]{.60f,.75f,.90f})
                    {
                        var edge=Hits(lid.Mesh,new Vector3(0,y,2.85f),Vector3.Right);
                        float lampInner=ContentProvider.ParseObj("res://content/sedan_mk2_stock_taillights.txt").GetFaces().Min(v=>Mathf.Abs(v.X));
                        T.Check($"rear flange leaves fixed taillight border at Y={y}",edge.Length>0&&edge[0]>.779f
                            &&edge[0]<.781f&&lampInner-edge[0]>.03f);
                    }
                    // Keep the original section and add its rigidly shifted V17 counterpart.
                    // OBJ triangle rays at Z=-2.66 give top returns .996803/1.006803/1.016803;
                    // cowl remains Z=-1.58..-1.55, so its existing probes must NOT shift.
                    foreach(float z in new[]{-2.66f,-2.3f,2.3f})
                    foreach(float x in new[]{.975f,.985f,.995f})
                    {
                        var sheet=Hits(frame.Mesh,new Vector3(x,0,z),Vector3.Up).Where(y=>y>.9f&&y<1.12f).ToArray();
                        T.Check($"hood/trunk edge is one 6mm sheet return, not a stepped filler X={x},Z={z}",
                            sheet.Length==2&&Mathf.Abs(sheet[1]-sheet[0]-.006f)<.0004f);
                    }
                    foreach(float y in new[]{.951f,.958f,.965f,.969f})
                    {
                        float x=.780f+(y-.950f)*(.99852f-.780f)/.020f;
                        var hits=Hits(frame.Mesh,new Vector3(x,y,2.740f),Vector3.Back);
                        T.Check($"diagonal trunk shutline has real painted backing at Y={y}",hits.Any(d=>d>.008f&&d<.068f));
                    }
                }
            }
            finally{car.Free();puppet.Free();}
            yield return Ticks(1);
        }
    }
}
