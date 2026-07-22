using Godot;

namespace UnturnedGodot
{
    // Master 2026-07-20 ("is there no standard holdable flag? or are u two hard coding holds for each thing"): the
    // held TOOLS (the power Wire + the vehicle tow Rope) were dispatched by HARD-CODED item id in EquipItemAsset
    // (`if (id == 65) wire; if (id == 64) rope`). This is the data-driven registry for them -- a new held tool is a
    // ToolDef entry (mesh + colour + kind), NOT a new hard-coded branch. First step of the general-holdable pass;
    // the other held types (gun/melee/consumable/deployable/fuel) fold onto a shared descriptor next.
    public enum ToolKind { Wire, Rope, Hose }   // which held-tool mode: power wiring / vehicle tow / fluid hose

    public sealed class ToolDef
    {
        public ushort Id;
        public string Name;
        public string HeldMesh;      // the in-hand mesh (obj under content/)
        public Color HeldColor;      // flat albedo tint (these meshes carry no texture)
        public ToolKind Kind;        // Wire (65) -> wiring mode / Rope (64) -> tow mode / Hose (66) -> fluid hose mode
        public bool IsRope => Kind == ToolKind.Rope;   // the Viewmodel.IsRopeTool bit
        public bool IsHose => Kind == ToolKind.Hose;   // the Viewmodel.IsHoseTool bit

        // wire + rope + hose currently share wire_hold.obj (the coil), tinted; dedicated meshes are a drop-in HeldMesh swap.
        public static readonly ToolDef Wire = new() { Id = 65, Name = "Wire tool", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.647f, 0.647f, 0.647f), Kind = ToolKind.Wire };
        public static readonly ToolDef Rope = new() { Id = 64, Name = "Rope tool", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.42f, 0.30f, 0.16f), Kind = ToolKind.Rope };
        public static readonly ToolDef Hose = new() { Id = 66, Name = "Hose tool", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.16f, 0.17f, 0.19f), Kind = ToolKind.Hose };

        public static readonly ToolDef[] All = { Wire, Rope, Hose };
        public static ToolDef ById(ushort id) { foreach (var t in All) if (t.Id == id) return t; return null; }
    }
}
