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
        // Data-texture width = the most displacers (current footprints + wake breadcrumbs) that can bend grass at once.
        // The nearest Max to the player win; grass only renders within ~160m, so anything past the cull range never
        // mattered. Bumped to 32 to leave room for the wake trail behind a mover.
        public const int Max = 32;

        // Typical footprints, in metres of flattened radius. Tunable per call site.
        public const float VehicleRadius = 4.2f;   // a car/truck presses a wide swath
        public const float PlayerRadius = 0.6f;     // a remote player's stance (the LOCAL player uses the retail point)
        public const float ItemRadius = 0.4f;       // a dropped item dimples a small patch
        public const float PlayerWakeRadius = 3.3f; // the local player's WAKE breadcrumb footprint -- matches the shader `radius` uniform so the trail is as wide as the live flatten

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

        // ---- WAKE (master 2026-08-24: "leave a short wake in the grass") --------------------------------------------
        // A moving displacer drops a breadcrumb every WakeSpacing metres; each fades over WakeSeconds, so the flattened
        // patch trails behind the mover and springs back up. Breadcrumbs are just extra (fading) displacer texels -- the
        // shader loop already handles them -- so the wake needs NO shader change, only more slots (Max).
        public const float WakeSeconds = 1.6f;   // how long a flattened wake takes to stand back up
        public const float WakeSpacing = 0.9f;   // drop a breadcrumb every this many metres of movement (radius > spacing => a continuous trail)

        struct Bread { public Vector3 Pos; public ulong Ms; public float R; }
        static readonly System.Collections.Generic.List<Bread> _wake = new();
        static readonly System.Collections.Generic.Dictionary<ulong, Vector3> _lastWake = new();   // per-source (instance id) last breadcrumb pos

        /// <summary>Age the wake trail, dropping breadcrumbs that have fully sprung back. Call once per frame.</summary>
        public static void AgeWake(ulong nowMs)
        {
            for (int i = _wake.Count - 1; i >= 0; i--)
                if ((nowMs - _wake[i].Ms) * 0.001f >= WakeSeconds) _wake.RemoveAt(i);
        }

        /// <summary>Drop a breadcrumb for a moving source (keyed by instance id) if it has moved >= WakeSpacing since its
        /// last one. Gate the CALL on a mover (player / vehicle): a stationary source would drop one then never again,
        /// but its id would linger in _lastWake, so items + remote players (small radius) are deliberately not called.</summary>
        public static void DropWake(ulong id, Vector3 pos, float radius, ulong nowMs)
        {
            if (_lastWake.TryGetValue(id, out var last) && pos.DistanceTo(last) < WakeSpacing) return;
            _lastWake[id] = pos;
            _wake.Add(new Bread { Pos = pos, Ms = nowMs, R = radius });
        }

        /// <summary>Add each live wake breadcrumb within range of the camera to the packing scratch, with a radius that
        /// FADES to 0 over WakeSeconds -- so the flatten weakens + narrows as the grass stands back up behind the mover.</summary>
        public static void GatherWake(System.Collections.Generic.List<(float d2, Vector3 pos, float r)> scratch, Vector3 cam, float range2, ulong nowMs)
        {
            foreach (var b in _wake)
            {
                float fade = 1f - (nowMs - b.Ms) * 0.001f / WakeSeconds;
                if (fade <= 0f) continue;
                float dx = b.Pos.X - cam.X, dz = b.Pos.Z - cam.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 > range2) continue;
                scratch.Add((d2, b.Pos, b.R * fade));
            }
        }
    }
}
