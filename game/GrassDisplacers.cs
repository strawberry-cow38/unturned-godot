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
    //
    // This class ALSO owns the grass-shader GLOBALS (EnsureGlobals). They MUST be registered before any grass_displace
    // material is created: a material built while a global it references does not yet exist links that global as invalid
    // ("Shader uses global parameter X, but it was removed at some point. Material will not display correctly.") and
    // then silently renders with NO displacement -- for ALL of them, since one bad global poisons the material. So
    // every site that creates a grass_displace material calls EnsureGlobals FIRST (FoliageField.MakeGrassMaterial, the
    // drivetest lawn), and so does PlayerController before it sets them. Idempotent.
    public static class GrassDisplacers
    {
        public const string Group = "grass_displacer";
        // Data-texture width = the most displacers that can bend grass at once. The nearest Max to the player win;
        // grass only renders within ~160m, so anything past the cull range never mattered. 24 is plenty and keeps the
        // per-blade shader loop short.
        public const int Max = 24;

        // Typical footprints, in metres of flattened radius. Tunable per call site.
        public const float VehicleRadius = 3.5f;   // a car/truck presses a wide swath
        public const float PlayerRadius = 0.6f;     // a person's stance (remote players; the LOCAL player is the retail point)
        public const float ItemRadius = 0.4f;       // a dropped item dimples a small patch

        static readonly StringName RadiusMeta = "grass_disp_radius";

        // The four grass-shader globals this owns. Public so PlayerController sets them without redeclaring the names.
        public static readonly StringName PointParam = "grass_displacement_point";   // retail: the LOCAL player point (x, y+0.5, z)
        public static readonly StringName WindParam = "wind_vec";                    // xy = wind dir, z = 0..1 strength (foliage sway shaders)
        public static readonly StringName TexParam = "grass_displacers";             // data texture: nearest extended displacers (xyz pos, w radius)
        public static readonly StringName CountParam = "grass_displacer_count";      // how many texels are live this frame

        static bool _globalsReady;
        public static Image DispImg { get; private set; }       // the packed displacer texels, updated each frame by PlayerController
        public static ImageTexture DispTex { get; private set; } // ...bound once to the TexParam global; Update re-uploads in place

        /// <summary>Register the grass-shader globals ONCE, and crucially BEFORE any grass_displace material is created.
        /// Call it at every grass-material creation site. Idempotent + static-guarded, so it runs once per process.</summary>
        public static void EnsureGlobals()
        {
            if (_globalsReady) return;
            _globalsReady = true;
            // Registered at runtime rather than in project settings so the shader works from a fresh clone with no editor
            // step. Order matters relative to material creation (see the class note), NOT relative to each other.
            RenderingServer.GlobalShaderParameterAdd(PointParam, RenderingServer.GlobalShaderParameterType.Vec4, Variant.From(Vector4.Zero));
            RenderingServer.GlobalShaderParameterAdd(WindParam, RenderingServer.GlobalShaderParameterType.Vec4, Variant.From(Vector4.Zero));
            RenderingServer.GlobalShaderParameterAdd(CountParam, RenderingServer.GlobalShaderParameterType.Int, Variant.From(0));
            DispImg = Image.CreateEmpty(Max, 1, false, Image.Format.Rgbaf);   // one texel per displacer; RGBAF holds raw world coords + radius UNCLAMPED
            DispTex = ImageTexture.CreateFromImage(DispImg);
            RenderingServer.GlobalShaderParameterAdd(TexParam, RenderingServer.GlobalShaderParameterType.Sampler2D, Variant.From(DispTex));
        }

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
