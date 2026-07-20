using Godot;

namespace UnturnedGodot
{
    // B11 (SP/MP-unify): the rope tool's tie/untie targets. The SP/loopback HOST scans real Vehicle nodes in
    // the "vehicles" group and calls AttachTow directly; a JOINED client scans VehiclePuppet nodes in the
    // "vehicle_puppets" group (its real cars are RemoveFromGroup'd) and sends CommandAttachTow/DetachTow by
    // NetId. Both node kinds expose the same tow-node geometry + nub-visual surface through this interface so
    // PlayerController's pick/highlight/preview code is one path across SP and MP.
    public interface ITowNode
    {
        Vector3 FrontTowWorld { get; }   // the towed end (-Z bumper)
        Vector3 RearTowWorld { get; }    // the tower end (+Z bumper)
        uint TowNetId { get; }           // the replicated entity id sent over the wire (0 for an SP-local Vehicle -> the direct AttachTow path, no wire)
        bool TowRoped { get; }           // already a rope end (client-side START gate; the server re-validates authoritatively)
        bool TowScannable { get; }       // eligible as a tow target this frame (a wreck is not)
        void SetTowNodesVisible(bool on);          // show/hide the two tow-node nubs while the rope tool is out
        void SetTowNubHighlighted(bool rear, bool on);   // brighten the aimed node
    }
}
