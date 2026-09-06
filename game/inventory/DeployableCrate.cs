using Godot;

namespace UnturnedGodot
{
    /// <summary>The storage grid of a placed deployable that is a container but NOT the fridge -- today, the
    /// campfire (strawberry 2026-09-06: "make it a smart storage container").
    ///
    /// A CHILD of the Deployable rather than a replacement for it, which is the whole point. The fridge takes
    /// the other route: DeployableDef.IsStorage swaps the Deployable body out for a Refrigerator, and brings a
    /// consumer port and food preservation with it. Doing that to a campfire would cost it everything a
    /// deployable is -- its health, its salvage, the placement rules, the damage model -- to gain a grid. So
    /// the body stays a Deployable and this rides along carrying nothing but the grid and the "crates"
    /// membership that makes F-open find it.
    ///
    /// It draws NOTHING. StorageCrate's own BuildVisual is a wooden box, which parented to a campfire would
    /// put a crate in the fire.</summary>
    public partial class DeployableCrate : StorageCrate
    {
        protected override void BuildVisual() { }   // the deployable body IS the visual

        public static DeployableCrate Attach(Deployable owner, byte w, byte h, uint netId)
        {
            if (owner == null || w == 0 || h == 0) return null;
            var c = new DeployableCrate { Width = w, Height = h, NetId = netId };
            owner.AddChild(c);
            return c;
        }
    }
}
