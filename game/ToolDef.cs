using Godot;

namespace UnturnedGodot
{
    // Master 2026-07-20 ("is there no standard holdable flag? or are u two hard coding holds for each thing"): the
    // held TOOLS (the power Wire + the vehicle tow Rope) were dispatched by HARD-CODED item id in EquipItemAsset
    // (`if (id == 65) wire; if (id == 64) rope`). This is the data-driven registry for them -- a new held tool is a
    // ToolDef entry (mesh + colour + kind), NOT a new hard-coded branch. First step of the general-holdable pass;
    // the other held types (gun/melee/consumable/deployable/fuel) fold onto a shared descriptor next.
    public sealed class ToolDef
    {
        public ushort Id;
        public string Name;
        public string HeldMesh;      // the in-hand mesh (obj under content/)
        public Color HeldColor;      // flat albedo tint (these meshes carry no texture)
        public bool IsRope;          // true = tow ROPE (item 64) -> tow mode; false = power WIRE (65) -> wiring mode. The Viewmodel.IsRopeTool bit.

        // wire + rope currently share wire_hold.obj (the coil), tinted; a dedicated hemp-rope mesh is a drop-in HeldMesh swap.
        public static readonly ToolDef Wire = new() { Id = 65, Name = "Wire tool", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.647f, 0.647f, 0.647f), IsRope = false };
        public static readonly ToolDef Rope = new() { Id = 64, Name = "Rope tool", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.42f, 0.30f, 0.16f), IsRope = true };

        public static readonly ToolDef[] All = { Wire, Rope };
        public static ToolDef ById(ushort id) { foreach (var t in All) if (t.Id == id) return t; return null; }
    }
}
