using Godot;

namespace UnturnedGodot
{
    // Master 2026-07-20 ("is there no standard holdable flag? or are u two hard coding holds for each thing"): the
    // held TOOLS (the power Wire + the vehicle tow Rope) were dispatched by HARD-CODED item id in EquipItemAsset
    // (`if (id == 65) wire; if (id == 64) rope`). This is the data-driven registry for them -- a new held tool is a
    // ToolDef entry (mesh + colour + kind), NOT a new hard-coded branch. First step of the general-holdable pass;
    // the other held types (gun/melee/consumable/deployable/fuel) fold onto a shared descriptor next.
    /// <summary>Which held-tool mode: power wiring / vehicle tow / fluid hose / remote-charge detonator
    /// (LMB fires all your charges) / Handheld = NO mode at all.
    ///
    /// Handheld exists so a tool can be carried without inheriting somebody else's trigger. The walkie-talkie
    /// has no implemented function yet, and every other kind here does something on click -- filing it under
    /// Detonator because it is "about the right shape" would arm every placed charge in the world the first
    /// time a player pressed LMB while holding a radio.</summary>
    public enum ToolKind { Wire, Rope, Hose, Detonator, Handheld }

    public sealed class ToolDef
    {
        public ushort Id;
        public string Name;
        public string HeldMesh;      // the in-hand mesh (obj under content/)
        /// <summary>An optional texture for the held mesh. Null keeps the flat <see cref="HeldColor"/> tint,
        /// which is what the shared wire coil wants; a tool with its own ripped mesh wants its own map.</summary>
        public string HeldAlbedo;
        public Color HeldColor;      // flat albedo tint (these meshes carry no texture)
        public ToolKind Kind;        // Wire (65) -> wiring mode / Rope (64) -> tow mode / Hose (9118) -> fluid hose mode
        public bool IsRope => Kind == ToolKind.Rope;   // the Viewmodel.IsRopeTool bit
        public bool IsHose => Kind == ToolKind.Hose;   // the Viewmodel.IsHoseTool bit
        public bool IsDetonator => Kind == ToolKind.Detonator;   // the Viewmodel.IsDetonatorTool bit

        // wire + rope + hose currently share wire_hold.obj (the coil), tinted; dedicated meshes are a drop-in HeldMesh swap.
        public static readonly ToolDef Wire = new() { Id = 65, Name = "Wire tool", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.647f, 0.647f, 0.647f), Kind = ToolKind.Wire };
        public static readonly ToolDef Rope = new() { Id = 64, Name = "Rope tool", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.42f, 0.30f, 0.16f), Kind = ToolKind.Rope };
        public static readonly ToolDef Hose = new() { Id = 9118, Name = "Hose tool", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.16f, 0.17f, 0.19f), Kind = ToolKind.Hose };   // 9118 = custom (fluid block), not a retail id
        // src Detonator.dat id 1240: the remote-charge trigger. Held -> LMB fires every placed Charge (Deployable.DetonateAllCharges).
        // Placeholder coil mesh (dark, like the other tools); the real Detonator_0 held mesh is a drop-in HeldMesh swap (viewmodel-pose follow-up).
        public static readonly ToolDef Detonator = new() { Id = 1240, Name = "Detonator", HeldMesh = "wire_hold.obj", HeldColor = new Color(0.11f, 0.11f, 0.12f), Kind = ToolKind.Detonator };

        // RETAIL'S OWN MESH, not an authored copy (strawberry 2026-09-11: "oh then we use the retail one").
        // 1445 has been ripped to content/items/1445.txt since 24 Aug -- the catalog row, the mesh and the 2x2
        // palette were all already on disk; the item simply had no ToolDef, so KindOf never called it a Tool
        // and nothing ever put it in a hand. The mesh lives under items/ because rips are keyed by NUMBER,
        // which is also why searching content/ for "walkie" found nothing and convinced me it did not exist.
        //
        // No ToolKind of its own behaviour: radio is not implemented, so this is a carry-only holdable.
        public static readonly ToolDef WalkieTalkie = new() { Id = 1445, Name = "Walkie Talkie", HeldMesh = "items/1445.txt", HeldAlbedo = "items/1445.png", HeldColor = new Color(1f, 1f, 1f), Kind = ToolKind.Handheld };

        public static readonly ToolDef[] All = { Wire, Rope, Hose, Detonator, WalkieTalkie };
        public static ToolDef ById(ushort id) { foreach (var t in All) if (t.Id == id) return t; return null; }
    }
}
