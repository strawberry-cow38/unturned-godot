namespace UnturnedGodot
{
    // A client-side replicated puppet (a VehiclePuppet or a WorldItemPuppet) that can show a look-at
    // outline. The SP Vehicle/WorldItem nodes have their own focus path (SetLookFocused/SetFocused);
    // this is the MP-puppet parallel so the client's look-focus raycast (PlayerController.UpdateLookFocus)
    // can highlight REPLICATED entities. Those puppets are render-only nodes -- without a look-detection
    // collider + this seam the focus ray passes straight through every car and dropped item in MP.
    //
    // Implementors are always Node3D subclasses; the resolver casts to Node3D for the focus distance and
    // guards with IsInstanceValid before toggling (a puppet can despawn while focused).
    public interface IPuppetFocusable
    {
        void SetLookFocused(bool on);
    }
}
