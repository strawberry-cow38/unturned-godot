namespace SDG.Unturned
{
    public enum HoseVerdict { None, Ok, Mismatch }

    // The engine-free rule for whether a hose may complete between two fluid ports (mirror of the wire tool's
    // CanCompleteWire, but with the fluid TYPE-LOCK). Kept out of PlayerController so it's unit-testable with no
    // Godot/scene: the caller reduces the live ports to booleans. Gravity is NOT decided here — a hose may connect
    // uphill; whether it FLOWS is FluidNet's elevation gate.
    public static class FluidHoseRule
    {
        // Ok/None/Mismatch for completing a hose started at one port onto a target port.
        //   startEmpty/targetEmpty: is that container's tank fluid None (empty adopts the other's type on connect)?
        //   typesEqual: do the two tanks carry the same fluid type? (only consulted when both are non-empty)
        // None  = not a legal target (same container, same role, or already hosed) -> no feedback.
        // Mismatch = both ends hold DIFFERENT set fluids -> "cannot mix fluids".
        // Ok    = opposite roles, different container, target free, and fluids compatible (an empty end adopts).
        public static HoseVerdict Completion(FluidPortKind startKind, FluidPortKind targetKind,
                                             bool startEmpty, bool targetEmpty, bool typesEqual,
                                             bool sameOwner, bool targetHosed)
        {
            if (sameOwner) return HoseVerdict.None;                 // can't hose a container to itself
            if (startKind == targetKind) return HoseVerdict.None;   // need opposite roles (source <-> consumer)
            if (targetHosed) return HoseVerdict.None;               // one hose per port (lean pass)
            if (!startEmpty && !targetEmpty && !typesEqual) return HoseVerdict.Mismatch;
            return HoseVerdict.Ok;
        }
    }
}
