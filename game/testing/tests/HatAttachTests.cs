using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>A WORN HAT HAS TO END UP AS A MESH ON THE HEAD, and the body has to notice when the worn slot
    /// is written by something that did not call through the clothing controller.
    ///
    /// master 2026-10-05: *"see if we can finally fix the hat equip bug... it doesnt show or apply on my
    /// character until i update my inv. (by moving an item in my inv)"*. "Finally" is doing real work in that
    /// sentence -- this shape has been reported and "fixed" twice (2026-09-07 ReconcileTick, 2026-09-13 the worn
    /// slots joining InventoryUI's poll hash) and has come back both times.
    ///
    /// ⭐⭐ WHY THIS TEST AND NOT ANOTHER CLIENT-SIDE POLL. Two fixes in a row addressed the POLLING and the bug
    /// survived, which means the diagnosis was wrong twice, so the thing to do is cover what was never covered
    /// rather than guess a third time. clothing.inventory_equip_unequip asserts SHIRT and PANTS, and both of
    /// those are TEXTURE FLAGS on the body shader (has_shirt_albedo). A hat is a different mechanism entirely --
    /// ClothingContent mesh -> PlayerClothingController.ApplyGear -> RiggedCharacter.AttachHat -> a
    /// BoneAttachment3D on the "Skull" bone -- and NOTHING has ever asserted that a hat produces a mesh at all.
    /// Verified by hand before writing this: all 157 hat rows in clothing_content.tsv name a mesh and all 157
    /// files exist, and the rig really does have a bone called "Skull", so the inputs are not the gap.
    ///
    /// ⭐ THE SECOND CASE IS THE ONE THAT MATTERS. In normal play the inventory is SERVER-OWNED (singleplayer
    /// runs through the loopback), so WearFromGrid sends an intent and returns -- nothing local wears anything.
    /// The hat arrives later as a bare field write when the owner echo is adopted, with nobody calling Wear().
    /// ReconcileTick is what is supposed to spot that, and it is asserted here against a slot written DIRECTLY,
    /// which is exactly what the echo does.</summary>
    public class HatMeshAttaches : GameTest
    {
        public override string Name => "clothing.hat_mesh_attaches";

        const ushort Tophat = 27;

        static Node FindAtt(RiggedCharacter body) => body?.Skeleton?.GetNodeOrNull("HatAttach");

        static bool HasHatMesh(RiggedCharacter body)
        {
            var att = FindAtt(body);
            if (att == null) return false;
            foreach (var c in att.GetChildren())
                if (c is MeshInstance3D mi && mi.Mesh != null) return true;
            return false;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            var asset = Assets.find(Tophat);
            T.Check("item 27 resolves to a HAT asset", asset != null && asset.type == EItemType.HAT);

            var body = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), false, null, "res://content/face_19.png");
            T.Check("RiggedCharacter body built", body != null);
            if (body == null) yield break;
            World.AddChild(body);
            yield return Ticks(2);
            T.Check("the rig has the Skull bone AttachHat targets", body.Skeleton != null);

            var inv = new PlayerInventory();
            var clothing = new PlayerClothingController(body, inv);
            var ui = new InventoryUI { Inv = inv, Clothing = clothing };
            World.AddChild(ui);
            yield return Ticks(2);

            // BASELINE -- the teeth anchor. Without this a broken attach could never be distinguished from a
            // hat that was already on the head when the test started.
            T.Check("baseline: nothing worn on the head", inv.wornHat == null);
            T.Check("baseline: no HatAttach node on the skeleton", FindAtt(body) == null);

            // ---- CASE 1: the LOCAL path (singleplayer with no server owning the bag) ----
            inv.items[2].addItem(0, 0, 0, new Item(Tophat));
            bool wore = ui.DebugWearFromGrid(EItemType.HAT, 2, 0, 0);
            yield return Ticks(1);
            T.Check("WearFromGrid(HAT) returned true", wore);
            T.Check("wornHat = 27 (STATE)", inv.wornHat != null && inv.wornHat.id == Tophat);
            // ⭐ THE TEETH: state set and no mesh on the head IS master's report. Asserting wornHat alone would
            // pass while the player stands there bare-headed, which is how this survived two fixes.
            T.Check("after equip: a HatAttach exists on the Skull bone (VISUAL -- teeth)", FindAtt(body) != null);
            T.Check("after equip: the HatAttach carries a real mesh (VISUAL -- teeth)", HasHatMesh(body));
            T.Check("the hat left the grid on equip", inv.getItemCount(Tophat) == 0);

            // ---- CONTROL: taking it off has to remove the mesh, not just the state ----
            bool off = ui.DebugTakeOff(EItemType.HAT);
            yield return Ticks(1);
            T.Check("TakeOff(HAT) returned true", off);
            T.Check("wornHat cleared", inv.wornHat == null);
            T.Check("control: the HatAttach is gone again", FindAtt(body) == null);

            // ---- CASE 2: THE SERVER-OWNED SHAPE. The worn slot written directly, exactly as adopting the
            // owner echo writes it, with nothing calling PlayerClothingController.Wear. ReconcileTick is the
            // only thing standing between this and master's bug.
            inv.wearHat(new Item(Tophat));
            T.Check("echo-shaped write set the state", inv.wornHat != null && inv.wornHat.id == Tophat);
            T.Check("...and painted NOTHING yet, as expected (nobody called Wear)", FindAtt(body) == null);

            clothing.ReconcileTick();
            yield return Ticks(1);
            T.Check("ReconcileTick noticed a slot it did not write and attached the hat", FindAtt(body) != null);
            T.Check("...with a real mesh", HasHatMesh(body));

            // ...and it must be IDEMPOTENT: a second tick with nothing changed cannot drop the hat it just put
            // on. A reconcile that repainted every frame would also "pass" the check above while thrashing.
            clothing.ReconcileTick();
            yield return Ticks(1);
            T.Check("a second reconcile with no change keeps the hat on", HasHatMesh(body));

            // ...and the REMOVAL half of the same path: the echo can take a hat off too.
            inv.wearHat(null);
            clothing.ReconcileTick();
            yield return Ticks(1);
            T.Check("ReconcileTick also notices an echo-shaped UNWEAR", FindAtt(body) == null);

            ui.QueueFree();
            body.QueueFree();
        }
    }
}
