// Godot oracle for the vending machine's look-at billboard
// (bevy/docs/WORLD_DEVICES_PORT.md). Builds the actual VendingMachine on the
// --proptest=Vendor_0 mesh and shows the InfoBillboard exactly as
// PlayerController.VendPromptSet fills it, for a looker with no wallet
// (StateFor(null) -> NoFunds). To run it, copy this file to
// game/VendingOracle.cs, register it for one run with an untracked
// game/override.cfg:
//   [autoload]
//   VendingOracle="*res://VendingOracle.cs"
// rebuild the game, then run --proptest=Vendor_0 UG_LIVE=1 with --shot.
// Remove both files afterwards.
using Godot;

namespace UnturnedGodot
{
    public partial class VendingOracle : Node
    {
        int _frames;

        public override void _Process(double delta)
        {
            if (++_frames != 3) return;
            MeshInstance3D prop = null;
            foreach (var n in GetTree().Root.FindChildren("*", "MeshInstance3D", true, false))
                if (n is MeshInstance3D mi && mi.Mesh != null && mi.Mesh.GetAabb().Size.Z > 2.4f) { prop = mi; break; }
            if (prop == null) { Log.Err("[vendoracle] no Vendor mesh"); return; }
            var v = VendingMachine.Make(prop, "Vendor_0");
            prop.GetParent().AddChild(v);
            var s = v.StateFor(null);
            var info = new InfoBillboard { TopLevel = true };
            AddChild(info);
            info.GlobalPosition = v.PromptAnchor;
            info.SetName(v.MachineName, v.MachineColor);
            info.SetPrompt(v.PromptFor(s, "F"), VendingMachine.ColorOf(s));
            info.SetActive(true);
            Log.Print($"[vendoracle] state={s} anchor={v.PromptAnchor} dispense={v.GetType().GetMethod("DispensePoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(v, null)}");
        }
    }
}
