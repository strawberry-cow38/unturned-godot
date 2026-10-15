using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>Host-resolved pose of an accepted furniture occupant. The anchor
    /// and facing come from the world, never from a client position assertion.</summary>
    public readonly record struct StationarySeatPose(uint NetId, Vector3 Position, float YawDegrees);
}
