using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    // Mounted-only adapter correction: source assumes its muzzle clears its own
    // collider. Exact rendered barrel triangles can overlap it as the hull moves.
    // Capture the firing vehicle, never infer it from the shooter's current seat.
    bool MountedWorldRay(uint vehicle,Vector3 from,Vector3 to,out CombatWorldHit hit)
    {
        bool found=_zombies.Ray((Vector3 a,Vector3 b,out CombatWorldHit h)=>
            _wildlife.RayExcludingVehicle(vehicle,a,b,out h),from,to,out hit);
        if(_cosmetic.RayWater(from,to,out var point,out var normal)
            &&(!found||(point-from).sqrMagnitude<(hit.Point-from).sqrMagnitude))
        {hit=new(point,normal:normal,water:true);return true;}
        return found;
    }
}
