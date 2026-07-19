using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // P4 equip wiring -- the singleplayer analog of SDG.Unturned.PlayerClothing (its `thirdClothes` HumanClothes, the
    // one 3P body SP needs). It does NOT own the worn STATE: PlayerInventory already models the 7 worn slots
    // (wornShirt/wornPants/wornHat/... + wear*(Item) + the armor/fall aggregation, tested in ArmorTests). This drives
    // the VISUAL off that state onto the player's RiggedCharacter body, exactly as PlayerClothing.ReceiveWear* does:
    //   set the slot -> thirdClothes.apply() -> UpdateStatModifiers().
    // Here the equivalent is: _inv.wear*(item) [state + armor, already done] -> Apply<Slot>() [the P3/P3b setters].
    // (UpdateStatModifiers is implicit: the aggregation is computed on read from the worn items, no separate call.)
    //
    //   shirt/pants -> ClothingContent textures + RiggedCharacter.SetShirt/SetPants (P3a body-atlas paint)
    //   hat/mask/glasses/vest/backpack -> ClothingContent gear mesh+albedo + RiggedCharacter.AttachHat/... (P3b bone-attach)
    // Unwear(slot) -> wear*(null) + ClearShirt/ClearPants or Detach*. Content is looked up by item id via ClothingContent.
    public class PlayerClothingController
    {
        readonly RiggedCharacter _body;   // the live 3P body (albedoTexPath==null -> the clothes-shader path P3a/P3b drive)
        readonly PlayerInventory _inv;    // the worn-slot STATE + armor aggregation (owned elsewhere; we only read/set slots)

        public PlayerClothingController(RiggedCharacter body, PlayerInventory inv)
        {
            _body = body;
            _inv = inv;
        }

        // Wear an item into its slot (state + armor via PlayerInventory.wear*), then re-sync that slot's visual --
        // the port of PlayerClothing.ReceiveWear<Slot>: set slot -> apply. Non-clothing items are ignored.
        public void Wear(Item item)
        {
            var a = item?.GetAsset();
            if (a == null) return;
            switch (a.type)
            {
                case EItemType.SHIRT:    _inv.wearShirt(item);    ApplyShirt();    break;
                case EItemType.PANTS:    _inv.wearPants(item);    ApplyPants();    break;
                case EItemType.HAT:      _inv.wearHat(item);      ApplyHat();      break;
                case EItemType.VEST:     _inv.wearVest(item);     ApplyVest();     break;
                case EItemType.MASK:     _inv.wearMask(item);     ApplyMask();     break;
                case EItemType.GLASSES:  _inv.wearGlasses(item);  ApplyGlasses();  break;
                case EItemType.BACKPACK: _inv.wearBackpack(item); ApplyBackpack(); break;
            }
        }

        public void Wear(int id) => Wear(new Item((ushort)id));

        // Remove a slot: clear the STATE (wear*(null)) then the visual. Only the 7 clothing types are meaningful.
        public void Unwear(EItemType slot)
        {
            switch (slot)
            {
                case EItemType.SHIRT:    _inv.wearShirt(null);    ApplyShirt();    break;
                case EItemType.PANTS:    _inv.wearPants(null);    ApplyPants();    break;
                case EItemType.HAT:      _inv.wearHat(null);      ApplyHat();      break;
                case EItemType.VEST:     _inv.wearVest(null);     ApplyVest();     break;
                case EItemType.MASK:     _inv.wearMask(null);     ApplyMask();     break;
                case EItemType.GLASSES:  _inv.wearGlasses(null);  ApplyGlasses();  break;
                case EItemType.BACKPACK: _inv.wearBackpack(null); ApplyBackpack(); break;
            }
        }

        // Full re-sync of every slot's visual from the current worn STATE -- the port of a fresh thirdClothes.apply().
        // Used at spawn/respawn so the body reflects whatever the inventory already wears.
        public void Refresh()
        {
            ApplyShirt(); ApplyPants(); ApplyHat(); ApplyVest(); ApplyMask(); ApplyGlasses(); ApplyBackpack();
        }

        // ---- per-slot visual apply (reads the worn item -> paints textures / attaches or detaches the gear mesh) ----

        void ApplyShirt()
        {
            var it = _inv.wornShirt;
            if (it == null) { _body?.ClearShirt(); return; }
            var t = ClothingContent.LoadTextures(it.id);
            _body?.SetShirt(t.Albedo, t.Emission, t.Metallic);
        }

        void ApplyPants()
        {
            var it = _inv.wornPants;
            if (it == null) { _body?.ClearPants(); return; }
            var t = ClothingContent.LoadTextures(it.id);
            _body?.SetPants(t.Albedo, t.Emission, t.Metallic);
        }

        void ApplyHat()      => ApplyGear(_inv.wornHat, EItemType.HAT);
        void ApplyVest()     => ApplyGear(_inv.wornVest, EItemType.VEST);
        void ApplyMask()     => ApplyGear(_inv.wornMask, EItemType.MASK);
        void ApplyGlasses()  => ApplyGear(_inv.wornGlasses, EItemType.GLASSES);
        void ApplyBackpack() => ApplyGear(_inv.wornBackpack, EItemType.BACKPACK);

        // Attach (or, when nothing worn / no ripped mesh for it, detach) the bone-attached gear mesh for a slot.
        void ApplyGear(Item worn, EItemType slot)
        {
            if (_body == null) return;
            var e = worn != null ? ClothingContent.Get(worn.id) : null;
            var mesh = e != null ? ClothingContent.LoadMesh(e.Mesh) : null;
            if (mesh == null) { Detach(slot); return; }   // nothing worn, or worn but no ripped mesh yet -> clear the slot
            var albedo = ClothingContent.LoadTex(e.Albedo);
            switch (slot)
            {
                case EItemType.HAT:      _body.AttachHat(mesh, albedo, e.Offset);      break;
                case EItemType.VEST:     _body.AttachVest(mesh, albedo, e.Offset);     break;
                case EItemType.MASK:     _body.AttachMask(mesh, albedo, e.Offset);     break;
                case EItemType.GLASSES:  _body.AttachGlasses(mesh, albedo, e.Offset);  break;
                case EItemType.BACKPACK: _body.AttachBackpack(mesh, albedo, e.Offset); break;
            }
        }

        void Detach(EItemType slot)
        {
            switch (slot)
            {
                case EItemType.HAT:      _body.DetachHat();      break;
                case EItemType.VEST:     _body.DetachVest();     break;
                case EItemType.MASK:     _body.DetachMask();     break;
                case EItemType.GLASSES:  _body.DetachGlasses();  break;
                case EItemType.BACKPACK: _body.DetachBackpack(); break;
            }
        }
    }
}
