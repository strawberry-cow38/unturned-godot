using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnturnedGodot;

/// <summary>Runtime-generated décor. No OBJ, baked size library or external textures are loaded.
/// Mesh-only game/ factory: core FurniturePlan owns placement and persisted input specs. Godot Y-up coordinates.
/// Curtains: back Z=0/front -Z, centered X/Y overall; use CurtainRailY to align the rail to a window.
/// Rugs: centered X/Z on floor, plane Y=0.015 to clear the room floor; height means floor depth.</summary>
public static class RuntimeDecorGenerators
{
    public enum RugStyle { Bordered, Diamond, Striped, Checker }
    const int CacheLimit = 128;
    sealed record Asset(ArrayMesh Mesh, Aabb Bounds, float RailY, float ClothTopY, float ClothBottomY);
    static readonly Dictionary<string, Asset> Cache = new();
    static readonly Queue<string> Keys = new();
    static string F(float x) => x.ToString("R", CultureInfo.InvariantCulture);
    static void Validate(float value, string name)
    { if (!float.IsFinite(value) || value <= 0 || value > float.MaxValue / 16) throw new ArgumentOutOfRangeException(name, "Use a finite positive size in metres."); }
    static Color Shade(Color c, float s) => new(c.R*s,c.G*s,c.B*s,1);
    static void Store(string key, Asset value)
    { Cache[key]=value;Keys.Enqueue(key);while(Cache.Count>CacheLimit)Cache.Remove(Keys.Dequeue()); }

    sealed class Builder
    {
        public readonly List<Vector3> V=new(), N=new();
        public readonly List<Vector2> UV=new();
        public readonly List<Color> Colors=new();
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color color, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            Vector3 normal=(b-a).Cross(c-a).Normalized();
            // Input polygons use outward CCW normals; Godot's front-face winding is clockwise.
            V.Add(a);V.Add(c);V.Add(b);N.Add(normal);N.Add(normal);N.Add(normal);
            UV.Add(ua);UV.Add(uc);UV.Add(ub);Colors.Add(color);Colors.Add(color);Colors.Add(color);
        }
        public void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Color color)
        { Tri(a,b,c,color,new(0,0),new(1,0),new(1,1));Tri(a,c,d,color,new(0,0),new(1,1),new(0,1)); }
        public void Box(Vector3 p,Vector3 size,Color color)
        {
            Vector3 h=size*.5f;
            var v=new Vector3[8];int k=0;
            foreach(int z in new[]{-1,1})foreach(int y in new[]{-1,1})foreach(int x in new[]{-1,1})v[k++]=p+new Vector3(x*h.X,y*h.Y,z*h.Z);
            foreach(var q in new[]{new[]{0,2,3,1},new[]{4,5,7,6},new[]{0,1,5,4},new[]{2,6,7,3},new[]{0,4,6,2},new[]{1,3,7,5}})Quad(v[q[0]],v[q[1]],v[q[2]],v[q[3]],color);
        }
        public Aabb Bounds()
        { Vector3 lo=V[0],hi=V[0];foreach(var p in V){lo=lo.Min(p);hi=hi.Max(p);}return new(lo,hi-lo); }
        public void Shift(Vector3 delta){for(int i=0;i<V.Count;i++)V[i]+=delta;}
        public ArrayMesh Mesh(StandardMaterial3D material)
        {
            var a=new Godot.Collections.Array();a.Resize((int)Godot.Mesh.ArrayType.Max);
            a[(int)Godot.Mesh.ArrayType.Vertex]=V.ToArray();a[(int)Godot.Mesh.ArrayType.Normal]=N.ToArray();
            a[(int)Godot.Mesh.ArrayType.TexUV]=UV.ToArray();a[(int)Godot.Mesh.ArrayType.Color]=Colors.ToArray();
            var mesh=new ArrayMesh();mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles,a);mesh.SurfaceSetMaterial(0,material);return mesh;
        }
    }
    static StandardMaterial3D Material() => new(){VertexColorUseAsAlbedo=true,Roughness=1,CullMode=BaseMaterial3D.CullModeEnum.Disabled};
    static (int cols,int rows,int cell,int ox,int oy,int lx,int ly,int w,int h) RugLayout(float length,float depth)
    {
        double border=Math.Min(.15,Math.Min(length,depth)*.09),line=border*.45;
        double iw=length-2*(border+line),ih=depth-2*(border+line);
        int baseC=Math.Clamp((int)Math.Round(Math.Min(64,iw/.45)),1,64),baseR=Math.Clamp((int)Math.Round(Math.Min(64,ih/.45)),1,64);
        int cols=baseC,rows=baseR;double best=double.PositiveInfinity;
        // Whole repeats, near-square physical cells, and a modest preference for ~0.45 m motifs.
        for(int c=Math.Max(1,baseC-3);c<=Math.Min(64,baseC+3);c++)
        for(int r=Math.Max(1,baseR-3);r<=Math.Min(64,baseR+3);r++)
        {
            double cw=iw/c,ch=ih/r,score=2*Math.Abs(Math.Log(cw/ch))+.30*(Math.Abs(Math.Log(cw/.45))+Math.Abs(Math.Log(ch/.45)));
            if(score<best){best=score;cols=c;rows=r;}
        }
        int cell=Math.Clamp(224/Math.Max(cols,rows),1,8);
        while(true)
        {
            int ox=Math.Max(1,(int)Math.Round(border/iw*cols*cell)),oy=Math.Max(1,(int)Math.Round(border/ih*rows*cell));
            int lx=Math.Max(1,(int)Math.Round(line/iw*cols*cell)),ly=Math.Max(1,(int)Math.Round(line/ih*rows*cell));
            int w=cols*cell+2*(ox+lx),h=rows*cell+2*(oy+ly);
            if((w<=256&&h<=256)||cell==1)return(cols,rows,cell,ox,oy,lx,ly,w,h);
            cell--;
        }
    }

    /// <param name="length">Desired fabric coverage width, metres; rail adds 0.28*scale m.
    /// Window side overlap is selected by FurniturePlan (e.g. window opening width + 0.40 m).</param>
    /// <param name="height">Cloth drop in metres, unchanged between open and closed states.</param>
    /// <param name="openAmount">0 closed, 1 gathered open; intermediate values also regenerate correctly.</param>
    public static ArrayMesh CreateCurtainMesh(float length,float height,bool open,Color? clothColor=null)
        => CreateCurtainMesh(length,height,open?1f:0f,clothColor);
    public static float CurtainRailY(float length,float height)
    { Validate(length,nameof(length));Validate(height,nameof(height));return height*.5f+.025f*MathF.Min(1,MathF.Min(length,height)); }
    public static float CurtainClothTopY(float length,float height)
    { Validate(length,nameof(length));Validate(height,nameof(height));return height*.5f-.065f*MathF.Min(1,MathF.Min(length,height)); }
    public static ArrayMesh CreateCurtainMesh(float length,float height,float openAmount=0,Color? clothColor=null)
    {
        Validate(length,nameof(length));Validate(height,nameof(height));
        if(!float.IsFinite(openAmount))throw new ArgumentOutOfRangeException(nameof(openAmount));
        float open=Math.Clamp(openAmount,0,1);Color cloth=clothColor??new Color(.80f,.77f,.68f);
        string key="curtain|"+F(length)+"|"+F(height)+"|"+F(open)+"|"+F(cloth.R)+"|"+F(cloth.G)+"|"+F(cloth.B)+"|"+F(cloth.A);
        if(!Cache.TryGetValue(key,out var asset))
        {
            var b=new Builder();float scale=MathF.Min(1,MathF.Min(length,height)),half=length*.5f;
            Color rail=new(.30f,.32f,.30f),tie=new(.57f,.49f,.38f);
            float railY=.09f*scale;
            b.Box(new(0,railY,-.14f*scale),new(length+.20f*scale,.055f*scale,.055f*scale),rail);
            foreach(int sign in new[]{-1,1})
            {
                b.Box(new(sign*(half+.10f*scale),railY,-.14f*scale),new(.08f*scale,.08f*scale,.08f*scale),rail);
                b.Box(new(sign*half,railY,-.07f*scale),new(.075f*scale,.075f*scale,.14f*scale),rail);
            }
            int pleats=Math.Clamp((int)MathF.Ceiling(MathF.Min(8,length/.65f)),2,8),segments=pleats*2;
            float[] level={0,.48f,1};float[] gathered={.22f,.15f,.31f};
            foreach(int side in new[]{-1,1})
            {
                var points=new Vector3[3,segments+1];
                for(int row=0;row<3;row++)
                {
                    float span=half*(1+(gathered[row]-1)*open);
                    for(int j=0;j<=segments;j++)
                    {
                        float t=(float)j/segments,x=side*half-side*span*t;
                        float depth=(.115f+(j%2)*(.065f+.025f*open))*scale;
                        points[row,j]=new(x,-height*level[row],-depth);
                    }
                }
                for(int row=0;row<2;row++)for(int j=0;j<segments;j++)
                {
                    Color c=j%2==0?cloth:Shade(cloth,.91f);
                    if(side<0)b.Quad(points[row,j],points[row,j+1],points[row+1,j+1],points[row+1,j],c);
                    else b.Quad(points[row,j],points[row+1,j],points[row+1,j+1],points[row,j+1],c);
                }
                // Tabs move with the gathered top, not with the original closed width.
                for(int j=0;j<=segments;j+=2)
                {
                    Vector3 p=points[0,j];float w=MathF.Min(.035f*scale,half/segments*.65f);
                    b.Quad(p+new Vector3(-w/2,0,0),p+new Vector3(-w/2,railY,-.14f*scale-p.Z),p+new Vector3(w/2,railY,-.14f*scale-p.Z),p+new Vector3(w/2,0,0),cloth);
                }
                if(open>.001f)
                {
                    float span=half*(1+(gathered[1]-1)*open);
                    b.Box(new(side*half-side*span*.5f,-height*.48f,-.232f*scale),new(span+.025f*scale,.045f*scale*open,.03f*scale),tie);
                }
            }
            var bounds=b.Bounds();float centerY=bounds.Position.Y+bounds.Size.Y*.5f;
            b.Shift(new(0,-centerY,0));asset=new(b.Mesh(Material()),b.Bounds(),railY-centerY,-centerY,-height-centerY);Store(key,asset);
        }
        return asset.Mesh;
    }

    /// <summary>Generate one flat textured quad at runtime. height is the second FLOOR dimension, not pile thickness.</summary>
    public static ArrayMesh CreateRugMesh(float length,float height,RugStyle style=RugStyle.Bordered)
    {
        Validate(length,nameof(length));Validate(height,nameof(height));
        if(!Enum.IsDefined(style))throw new ArgumentOutOfRangeException(nameof(style));
        string key="rug|"+F(length)+"|"+F(height)+"|"+style;
        if(!Cache.TryGetValue(key,out var asset))
        {
            var layout=RugLayout(length,height);int w=layout.w,h=layout.h;
            var image=Image.CreateEmpty(w,h,false,Image.Format.Rgba8);
            Color[] palette=style switch{
                RugStyle.Diamond=>new[]{new Color(.25f,.32f,.35f),new Color(.74f,.72f,.61f),new Color(.44f,.55f,.57f),new Color(.86f,.83f,.72f)},
                RugStyle.Striped=>new[]{new Color(.34f,.39f,.28f),new Color(.69f,.72f,.56f),new Color(.83f,.80f,.68f),new Color(.48f,.52f,.36f)},
                RugStyle.Checker=>new[]{new Color(.31f,.40f,.49f),new Color(.73f,.68f,.49f),new Color(.85f,.81f,.69f),new Color(.45f,.56f,.59f)},
                _=>new[]{new Color(.49f,.25f,.19f),new Color(.68f,.43f,.29f),new Color(.83f,.75f,.56f),new Color(.57f,.32f,.23f)}};
            int sx=layout.ox+layout.lx,sy=layout.oy+layout.ly;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                bool outer=x<layout.ox||x>=w-layout.ox||y<layout.oy||y>=h-layout.oy;
                bool interior=x>=sx&&x<w-sx&&y>=sy&&y<h-sy;
                int c=outer?0:interior?1:2;
                if(interior)
                {
                    int col=(x-sx)/layout.cell,row=(y-sy)/layout.cell;
                    float a=((x-sx)%layout.cell+.5f)/layout.cell,b=((y-sy)%layout.cell+.5f)/layout.cell;
                    c=style switch{RugStyle.Diamond=>MathF.Abs(a-.5f)+MathF.Abs(b-.5f)<.36f?0:1,
                        RugStyle.Striped=>(row&1)==0?1:3,
                        RugStyle.Checker=>((col+row)&1)==0?1:3,_=>1};
                }
                image.SetPixel(x,y,palette[c]);
            }
            var material=Material();material.AlbedoTexture=ImageTexture.CreateFromImage(image);material.TextureFilter=BaseMaterial3D.TextureFilterEnum.Nearest;
            var bld=new Builder();float xh=length*.5f,zh=height*.5f;
            Vector3 pa=new(-xh,.015f,-zh),pb=new(-xh,.015f,zh),pc=new(xh,.015f,zh),pd=new(xh,.015f,-zh);
            // U follows length/X and V follows depth/Z; don't rotate a rectangular rug's texture.
            bld.Tri(pa,pb,pc,Colors.White,new(0,0),new(0,1),new(1,1));
            bld.Tri(pa,pc,pd,Colors.White,new(0,0),new(1,1),new(1,0));
            asset=new(bld.Mesh(material),bld.Bounds(),0,0,0);Store(key,asset);
        }
        return asset.Mesh;
    }

    /// <summary>Clear cache ownership only; live nodes still retain their resource references.</summary>
    public static void ClearCache(){Cache.Clear();Keys.Clear();}
}