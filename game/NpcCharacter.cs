using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>A human NPC: a player model that stands there until you talk to it (master 2026-09-11: "puppet
    /// players that stand around until interacted with").
    ///
    /// It is almost literally what retail's data describes -- an NPC asset is clothing item ids, a Face index
    /// and a skin colour -- so this builds the SAME RiggedCharacter + PlayerClothingController the remote-player
    /// puppets use rather than a parallel "NPC model" path. One body pipeline: an NPC wearing a shirt the port
    /// cannot dress is a bug you find once, in both places.
    ///
    /// Solid, on the world layer, because a person you can walk through is not standing there in any useful
    /// sense -- and because the look ray already masks that layer, which is what makes F work with no new
    /// plumbing.</summary>
    public partial class NpcCharacter : StaticBody3D
    {
        public const float Radius = 0.35f, Height = 1.8f;

        public NpcCharacterDef Def;
        RiggedCharacter _body;
        PlayerInventory _inv;
        PlayerClothingController _clothing;
        Nameplate _plate;
        bool _focused;

        /// <summary>Retail Color_Skin/Color_Hair are "#RRGGBB". A miss returns the default rather than black:
        /// an NPC whose asset is missing a colour should look like a person, not a silhouette.</summary>
        static Color Hex(string s, Color dv)
        {
            s = (s ?? "").Trim();
            if (s.Length == 7 && s[0] == '#') { try { return new Color(s); } catch { } }
            return dv;
        }

        public static NpcCharacter Spawn(Node parent, NpcCharacterDef def, Vector3 pos, float yawDegrees = 0f)
        {
            if (parent == null || def == null) return null;
            var npc = new NpcCharacter
            {
                Def = def,
                Name = "Npc_" + (def.Key.Length > 0 ? def.Key : def.Id.ToString()),
                CollisionLayer = 1u << 0,   // the ordinary world layer: solid, and already in the look ray's mask
                CollisionMask = 0,
            };
            parent.AddChild(npc);
            npc.GlobalPosition = pos;
            npc.Rotation = new Vector3(0f, Mathf.DegToRad(yawDegrees), 0f);

            var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Radius = Radius, Height = Height } };
            shape.Position = new Vector3(0f, Height * 0.5f, 0f);
            npc.AddChild(shape);

            npc._body = RiggedCharacter.Build("res://content/rig.json", Hex(def.Skin, new Color(0.82f, 0.66f, 0.52f)),
                                              false, null, RiggedCharacter.FacePath(def.Face));
            if (npc._body != null)
            {
                npc.AddChild(npc._body);
                // ⚠ PlayLoop ALONE LEAVES THEM T-POSING. A RiggedCharacter is posed by SetLocomotion + Tick,
                // the pair RemotePlayers drives its puppets with; PlayLoop only names the clip, and a body that
                // is never ticked sits in the bind pose forever. The first render of this caught it -- a chef in
                // his whites with his arms straight out -- which a "the PNG exists" check would have passed.
                npc._body.SetLocomotion(0f, EPlayerStance.STAND);
                npc._body.Tick(0.0);
                npc._inv = new PlayerInventory();
                npc._clothing = new PlayerClothingController(npc._body, npc._inv);
                Wear(npc._inv, def);
                npc._clothing.Refresh();
                npc._plate = Nameplate.Attach(npc._body);
                npc._plate?.Set(def.Name, null);
                if (npc._plate != null)
                {
                    // The height is MEASURED off the worn gear, not guessed -- see Nameplate.SeatAboveGear.
                    // ⚠ NOT HERE THOUGH: a BoneAttachment3D only catches up when the skeleton emits its pose,
                    // so the hat's transform at build time is still the identity and measuring it now returns
                    // the head height with a very confident zero attached. Seated on the first _Process frame
                    // instead, after Tick has actually posed the thing. The depth test stays on, deliberately
                    // (a plate behind a wall is meant to stay behind the wall; that is the shared rule working).
                    npc._plate.Visible = false;   // shown on look, see SetLookFocused
                }
            }
            return npc;
        }

        /// <summary>Dress them from the asset's item ids. 0 means the slot is empty, which is a real state --
        /// Captain Sydney ships shirt 0 and is meant to be in whatever the bare model wears.</summary>
        static void Wear(PlayerInventory inv, NpcCharacterDef d)
        {
            if (d.Shirt != 0) inv.wearShirt(new Item(d.Shirt));
            if (d.Pants != 0) inv.wearPants(new Item(d.Pants));
            if (d.Hat != 0) inv.wearHat(new Item(d.Hat));
            if (d.Vest != 0) inv.wearVest(new Item(d.Vest));
            if (d.Mask != 0) inv.wearMask(new Item(d.Mask));
            if (d.Glasses != 0) inv.wearGlasses(new Item(d.Glasses));
            if (d.Backpack != 0) inv.wearBackpack(new Item(d.Backpack));
        }

        /// <summary>Looked at: show who they are. The nameplate is the affordance -- a name appearing over
        /// somebody's head is how you learn they are talkable, and it costs nothing when you are not looking.</summary>
        public void SetLookFocused(bool on)
        {
            if (_focused == on) return;
            _focused = on;
            if (_plate != null && GodotObject.IsInstanceValid(_plate)) _plate.Visible = on;
        }

        /// <summary>Standing still, every frame. One AnimationPlayer tick per person -- the same cost a remote
        /// player's puppet already pays, and the reason they breathe rather than freeze. A crowd of them would
        /// be worth staggering; a handful is not, and pretending otherwise is the premature half of optimising.</summary>
        public override void _Process(double delta)
        {
            if (_body == null || !GodotObject.IsInstanceValid(_body)) { SetProcess(false); return; }
            _body.SetLocomotion(0f, EPlayerStance.STAND);
            _body.Tick(delta);
            if (!_plateSeated && _plate != null && GodotObject.IsInstanceValid(_plate))
            {
                // Once, on the first posed frame -- the bone attachment has a real transform by now. Re-running
                // it every frame would be a GetAabb loop over gear that cannot move on a person who never does.
                _plateSeated = true;
                float gearTop = _body.HeadwearTopLocalY();
                _plate.SeatAboveGear(gearTop);
                // Say the number. Whether a hat cleared is exactly the thing a downscaled screenshot cannot
                // settle, and "0.00" here is how a hat that never attached tells you so instead of looking fine.
                if (System.Environment.GetEnvironmentVariable("UG_UIGEOM") == "1")
                    Log.Print($"[npcplate] {Def?.Key} hat={Def?.Hat} gearTop={gearTop:0.###} plateY={_plate.Position.Y:0.###}");
            }
        }
        bool _plateSeated;

        public string DisplayName => Def?.Name ?? "Someone";
        public int DialogueId => Def?.Dialogue ?? 0;
        /// <summary>Test seam: is the plate up? The focus state is the whole interaction affordance, so a test
        /// that cannot see it cannot tell "you can talk to them" from "you cannot".</summary>
        public bool DebugPlateVisible => _plate != null && GodotObject.IsInstanceValid(_plate) && _plate.Visible;
    }
}
