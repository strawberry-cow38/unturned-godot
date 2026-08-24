using Godot;

namespace UnturnedGodot
{
    // Registry of things that FLATTEN grass BEYOND retail's single local-player point (master 2026-08-24: "implement
    // a grass displacement shader for vehicles, players, dropped items"). Retail's GrassDisplacement.cs pushes ONE
    // global point -- only the local player -- and PlayerController keeps reproducing that verbatim. THIS is the
    // extension: every extra displacer (a driven vehicle, a dropped item, a remote player) joins one group and carries
    // its footprint RADIUS as node meta. Each frame PlayerController gathers the group, keeps the nearest `Max` to the
    // camera (the rest are outside grass render range anyway), and packs (world pos + radius) into the `grass_displacers`
    // data texture the shader reads. Godot groups auto-drop freed nodes, so a picked-up item / despawned car needs no
    // explicit unregister -- QueueFree is the unregister.
    public static class GrassDisplacers
    {
        public const string Group = "grass_displacer";
        // Data-texture width = the most displacers that can bend grass at once. The nearest Max to the player win;
        // grass only renders within ~160m, so anything past the cull range never mattered. 24 is plenty (a convoy +
        // a loot pile in view) and keeps the per-blade shader loop short.
        public const int Max = 24;

        // Typical footprints, in metres of flattened radius. Tunable per call site.
        public const float VehicleRadius = 3.5f;   // a car/truck presses a wide swath
        public const float PlayerRadius = 0.6f;     // a person's stance (remote players; the LOCAL player is the retail point)
        public const float ItemRadius = 0.4f;       // a dropped item dimples a small patch

        static readonly StringName RadiusMeta = "grass_disp_radius";

        /// <summary>Enlist a node as a grass displacer with the given flattened-footprint radius (metres). Idempotent.</summary>
        public static void Register(Node node, float radius)
        {
            if (node == null) return;
            node.SetMeta(RadiusMeta, radius);
            if (!node.IsInGroup(Group)) node.AddToGroup(Group);
        }

        public static float RadiusOf(Node node) => node != null && node.HasMeta(RadiusMeta) ? (float)node.GetMeta(RadiusMeta) : PlayerRadius;
    }
}
