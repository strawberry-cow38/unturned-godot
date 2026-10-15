using UnityEngine;

namespace UnturnedBevy;

public sealed partial class WorldGeometry
{
    /// <summary>Actual triangle overlap with the source zombie walkability box.
    /// BVH bounds prune the query; triangle/AABB separating axes test the surfaces.</summary>
    public bool OverlapsBox(Vector3 center,Vector3 size)
    {
        var half=size*.5f;
        bool VisitBox(Node node)
        {
            if(node==null||node.Max.x<center.x-half.x||node.Min.x>center.x+half.x
                ||node.Max.y<center.y-half.y||node.Min.y>center.y+half.y
                ||node.Max.z<center.z-half.z||node.Min.z>center.z+half.z)return false;
            if(node.Leaf==null)return VisitBox(node.Left)||VisitBox(node.Right);
            foreach(var t in node.Leaf)
            {
                if(!t.Combat)continue;
                if(t.Destructible>=0&&DestructibleAlive?.Invoke(t.Destructible)==false)continue;
                if(t.Resource>=0&&ResourceAlive?.Invoke(t.Resource)==false)continue;
                if(t.Vehicle!=0&&VehicleExists?.Invoke(t.Vehicle)==false)continue;
                if(t.Deployable!=0&&DeployableExists?.Invoke(t.Deployable)==false)continue;
                var a=t.A-center;var b=t.B-center;var c=t.C-center;
                bool Separate(Vector3 axis)
                {
                    float r=half.x*MathF.Abs(axis.x)+half.y*MathF.Abs(axis.y)+half.z*MathF.Abs(axis.z);
                    float x=Vector3.Dot(a,axis),y=Vector3.Dot(b,axis),z=Vector3.Dot(c,axis);
                    return Math.Min(x,Math.Min(y,z))>r||Math.Max(x,Math.Max(y,z))< -r;
                }
                var ex=new Vector3(1,0,0);var ey=new Vector3(0,1,0);var ez=new Vector3(0,0,1);
                var ab=b-a;var bc=c-b;var ca=a-c;
                if(Separate(ex)||Separate(ey)||Separate(ez)||Separate(Vector3.Cross(ab,bc)))continue;
                if(Separate(Vector3.Cross(ab,ex))||Separate(Vector3.Cross(ab,ey))||Separate(Vector3.Cross(ab,ez)))continue;
                if(Separate(Vector3.Cross(bc,ex))||Separate(Vector3.Cross(bc,ey))||Separate(Vector3.Cross(bc,ez)))continue;
                if(Separate(Vector3.Cross(ca,ex))||Separate(Vector3.Cross(ca,ey))||Separate(Vector3.Cross(ca,ez)))continue;
                return true;
            }
            return false;
        }
        bool CapsuleBox(Capsule capsule)
        {
            // Closest separation of the vertical capsule axis and the query AABB.
            float x=Math.Max(0,Math.Abs(capsule.Foot.x-center.x)-half.x);
            float z=Math.Max(0,Math.Abs(capsule.Foot.z-center.z)-half.z);
            float y=Math.Max(0,Math.Max(capsule.Foot.y+capsule.Radius-(center.y+half.y),
                center.y-half.y-(capsule.Foot.y+capsule.Height-capsule.Radius)));
            return x*x+y*y+z*z<=capsule.Radius*capsule.Radius;
        }
        return VisitBox(_root)||_dynamic.Values.Any(VisitBox)||_capsules.Any(CapsuleBox)
            ||_dynamicCapsules.Values.Any(capsules=>capsules.Any(CapsuleBox));
    }
}
