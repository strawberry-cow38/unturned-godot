using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // P4 equip wiring: PlayerClothingController.Wear drives the P3a body paint (clothes-shader has_shirt/pants_albedo)
    // AND the P3b gear bone-attach (a hat rides a BoneAttachment3D on the Skull bone). Guards the whole equip->visual
    // path end-to-end on a real RiggedCharacter body.
    //
    // TEETH: the BARE body (before any Wear) is asserted to have has_shirt_albedo == false + NO Skull hat-attachment --
    // exactly the state if the controller didn't drive the visual. The post-Wear asserts then require the flip, so
    // stubbing out ApplyShirt/ApplyPants/ApplyGear (removing the wiring) fails this test. (Verified: with the visual
    // driving removed the post-Wear checks go false.)
    public class ClothingEquipDrivesVisual : GameTest
    {
        public override string Name => "clothing.equip_drives_visual";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // ids 3 (shirt), 2 (pants), 27 (hat) -> real assets with EItemType (host resets statics after)

            // the live 3P body path: albedoTexPath == null -> the ported StandardClothes shader (the one SetShirt/SetPants paint)
            var body = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), false, null, "res://content/face_19.png");
            T.Check("RiggedCharacter body built", body != null);
            if (body == null) yield break;
            World.AddChild(body);
            yield return Ticks(2);   // let the skeleton enter the tree + poses settle (BoneAttachment binds BoneIdx on entry)

            var shaderMat = body.Body?.MaterialOverride as ShaderMaterial;
            T.Check("body uses the clothes ShaderMaterial", shaderMat != null);
            if (shaderMat == null) yield break;

            // BASELINE (no wiring applied yet): the flags are false + no hat attachment on the Skull bone
            T.Check("bare body: has_shirt_albedo false", !shaderMat.GetShaderParameter("has_shirt_albedo").AsBool());
            T.Check("bare body: has_pants_albedo false", !shaderMat.GetShaderParameter("has_pants_albedo").AsBool());
            Skeleton3D skel = FindDown<Skeleton3D>(body);
            T.Check("body has a skeleton", skel != null);
            if (skel == null) yield break;
            int skull = skel.FindBone("Skull");
            T.Check($"skeleton has a Skull bone (idx {skull})", skull >= 0);
            T.Check("bare body: no hat BoneAttachment yet", FindHatAttachment(skel) == null);

            // EQUIP through the ACTUAL controller (state + visual), exactly as the SP spawn / DevConsole `wear` path does
            var inv = new PlayerInventory();
            var clothing = new PlayerClothingController(body, inv);
            clothing.Wear(new Item(3));    // Orange Hoodie (shirt)
            clothing.Wear(new Item(2));    // Work Jeans (pants)
            clothing.Wear(new Item(27));   // Tophat (hat) -> Skull-bone mesh
            yield return Ticks(2);         // let the new BoneAttachment enter the tree + bind

            // state landed in the worn slots (PlayerInventory), and the shader flipped its texture flags
            T.Check("wornShirt = 3", inv.wornShirt != null && inv.wornShirt.id == 3);
            T.Check("wornPants = 2", inv.wornPants != null && inv.wornPants.id == 2);
            T.Check("wornHat = 27", inv.wornHat != null && inv.wornHat.id == 27);
            T.Check("after Wear: has_shirt_albedo TRUE", shaderMat.GetShaderParameter("has_shirt_albedo").AsBool());
            T.Check("after Wear: has_pants_albedo TRUE", shaderMat.GetShaderParameter("has_pants_albedo").AsBool());

            // the hat bone-attach: a BoneAttachment3D bound to the Skull bone, holding the ripped hat mesh
            BoneAttachment3D hatAtt = FindHatAttachment(skel);
            T.Check("hat BoneAttachment3D exists under the skeleton", hatAtt != null);
            if (hatAtt == null) yield break;
            T.Check($"hat attachment bound to the Skull bone (BoneIdx {hatAtt.BoneIdx}, Skull {skull})", hatAtt.BoneIdx == skull);
            var hatMesh = hatAtt.GetNodeOrNull<MeshInstance3D>("Hat");
            T.Check("hat attachment holds a Hat MeshInstance3D with a real mesh", hatMesh != null && hatMesh.Mesh != null);

            // Unwear clears both state + visual (proves the removal path)
            clothing.Unwear(EItemType.SHIRT);
            clothing.Unwear(EItemType.HAT);
            yield return Ticks(2);
            T.Check("Unwear shirt: has_shirt_albedo back to false", !shaderMat.GetShaderParameter("has_shirt_albedo").AsBool());
            T.Check("Unwear hat: no Skull hat attachment", FindHatAttachment(skel) == null);
        }

        // the P3b hat slot: a BoneAttachment3D holding a MeshInstance3D named "Hat"
        static BoneAttachment3D FindHatAttachment(Skeleton3D skel)
        {
            foreach (var c in skel.GetChildren())
                if (c is BoneAttachment3D ba && GodotObject.IsInstanceValid(ba) && !ba.IsQueuedForDeletion()
                    && ba.GetNodeOrNull<MeshInstance3D>("Hat") != null)
                    return ba;
            return null;
        }

        static TN FindDown<TN>(Node n) where TN : Node
        {
            if (n is TN hit) return hit;
            foreach (var c in n.GetChildren())
                if (FindDown<TN>(c) is TN found) return found;
            return null;
        }
    }
}
