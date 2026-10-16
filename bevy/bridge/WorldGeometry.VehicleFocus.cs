using UnityEngine;
namespace UnturnedBevy;
public sealed partial class WorldGeometry
{
    /// <summary>Source look-end sphere against the uploaded original vehicle
    /// faces. Returns identities, not bounding-box false positives. The caller
    /// applies source nearest-root arbitration after the clipped eye ray.</summary>
    public HashSet<uint> VehiclesAt(Vector3 point, float radius)
    {
        var found = new HashSet<uint>(); float limit = radius * radius;
        void Visit(Node node)
        {
            if (node == null) return;
            var near = new Vector3(Math.Clamp(point.x,node.Min.x,node.Max.x),Math.Clamp(point.y,node.Min.y,node.Max.y),Math.Clamp(point.z,node.Min.z,node.Max.z));
            if ((near-point).sqrMagnitude > limit) return;
            if(node.Leaf==null){Visit(node.Left);Visit(node.Right);return;}
            foreach(var t in node.Leaf)
                if(t.Combat && t.Vehicle!=0 && VehicleExists?.Invoke(t.Vehicle)!=false
                    && VehiclePartSolid?.Invoke(t.Vehicle,t.Part)!=false
                    && TriangleDistanceSquared(point,t.A,t.B,t.C)<=limit) found.Add(t.Vehicle);
        }
        Visit(_root);
        foreach(var pair in _dynamic) if(DynamicAlive?.Invoke(pair.Key)!=false) Visit(pair.Value);
        return found;
    }
    static float TriangleDistanceSquared(Vector3 point,Vector3 a,Vector3 b,Vector3 c)
    {
        static float Edge(Vector3 p,Vector3 a,Vector3 b)
        {var ab=b-a;float d=ab.sqrMagnitude;var t=d>0?Math.Clamp(Vector3.Dot(p-a,ab)/d,0,1):0;return (p-a-ab*t).sqrMagnitude;}
        var ab=b-a;var ac=c-a;var n=Vector3.Cross(ab,ac);float nn=n.sqrMagnitude;
        if(nn>1e-16f)
        {
            var q=point-n*(Vector3.Dot(point-a,n)/nn);
            // Winding-independent signed edge tests on the projected plane.
            if(Vector3.Dot(Vector3.Cross(b-a,q-a),n)>=0
                &&Vector3.Dot(Vector3.Cross(c-b,q-b),n)>=0
                &&Vector3.Dot(Vector3.Cross(a-c,q-c),n)>=0)
            {float d=Vector3.Dot(point-a,n);return d*d/nn;}
        }
        return Math.Min(Edge(point,a,b),Math.Min(Edge(point,b,c),Edge(point,c,a)));
    }
}
