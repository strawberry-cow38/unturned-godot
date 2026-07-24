namespace SDG.Unturned
{
    public enum HoseVerdict { None, Ok, Mismatch }

    // The engine-free rule for whether a hose may complete between two fluid ports (mirror of the wire tool's
    // CanCompleteWire, but with the fluid TYPE-LOCK). Kept out of PlayerController so it's unit-testable with no
    // Godot/scene: the caller reduces the live ports to booleans. Gravity is NOT decided here — a hose may connect
    // uphill; whether it FLOWS is FluidNet's elevation gate.
    public static class FluidHoseRule
    {
        // A hose runs from a SOURCE-SIDE port (pushes fluid out: a container Source or a fitting Passthrough) to a
        // CONSUMER-SIDE port (draws fluid in: a Consumer / relay input). Mirror of the wire tool's IsSourcePort.
        public static bool IsSourceSide(FluidPortKind k) => k == FluidPortKind.Source || k == FluidPortKind.Passthrough;

        // Ok/None/Mismatch for completing a hose started at one port onto a target port.
        //   startEmpty/targetEmpty: is that container's tank fluid None (empty adopts the other's type on connect)?
        //   typesEqual: do the two tanks carry the same fluid type? (only consulted when both are non-empty)
        // None  = not a legal target (same container, same side, or already hosed) -> no feedback.
        // Mismatch = both ends hold DIFFERENT set fluids -> "cannot mix fluids".
        // Ok    = one source-side + one consumer-side, different container, target free, and fluids compatible (empty adopts).
        public static HoseVerdict Completion(FluidPortKind startKind, FluidPortKind targetKind,
                                             bool startEmpty, bool targetEmpty, bool typesEqual,
                                             bool sameOwner, bool targetHosed)
        {
            if (sameOwner) return HoseVerdict.None;                              // can't hose a container to itself
            if (IsSourceSide(startKind) == IsSourceSide(targetKind)) return HoseVerdict.None;   // need one source-side + one consumer-side
            if (targetHosed) return HoseVerdict.None;                           // one hose per port (lean pass)
            if (!startEmpty && !targetEmpty && !typesEqual) return HoseVerdict.Mismatch;
            return HoseVerdict.Ok;
        }
    }
}
