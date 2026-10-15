using UnityEngine;
namespace UnturnedBevy;

public sealed partial class WorldGeometry
{
    public static bool SegmentBox(Vector3 center,Vector3 size,Vector3 from,Vector3 to,out Vector3 point,out Vector3 normal)
    {
        // Godot ray queries default hit_from_inside=false: after piercing the
        // top face a round starts inside the .2m splash slab and passes its back.
        normal=default;var half=size*.5f;var min=center-half;var max=center+half;
        if(from.x>min.x&&from.x<max.x&&from.y>min.y&&from.y<max.y&&from.z>min.z&&from.z<max.z){point=to;return false;}
        float enter=0,exit=1;var dir=to-from;
        for(int axis=0;axis<3;axis++)
        {
            float p=axis==0?from.x:axis==1?from.y:from.z,d=axis==0?dir.x:axis==1?dir.y:dir.z;
            float lo=axis==0?min.x:axis==1?min.y:min.z,hi=axis==0?max.x:axis==1?max.y:max.z;
            if(Math.Abs(d)<1e-8f){if(p<lo||p>hi){point=to;return false;}continue;}
            float a=(lo-p)/d,b=(hi-p)/d;if(a>b)(a,b)=(b,a);
            if(a>=enter){enter=a;float sign=d>0?-1:1;normal=axis==0?new(sign,0,0):axis==1?new(0,sign,0):new(0,0,sign);}
            exit=Math.Min(exit,b);
            if(enter>exit){point=to;return false;}
        }
        // A tangent along a boundary (or a ray leaving it) has no entry face.
        // Like hit_from_inside=false, it must not manufacture a t=0 surface hit.
        if(normal.sqrMagnitude==0){point=to;return false;}
        point=from+dir*enter;return true;
    }
}
