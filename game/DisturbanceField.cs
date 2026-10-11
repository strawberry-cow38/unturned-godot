using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>Everything in the world that shoves plants around, packed once a frame into the one data texture
    /// grass, bushes, hedges and tree canopies all read (strawberry 2026-10-11: "rip out our grass displacement
    /// and rebuild it from the ground up. add rotor wash for helis on foliage and trees").
    ///
    /// ⚠⚠ THIS IS A REBUILD, NOT A REWRITE OF THE FEEL. The old grass displacement took three rounds against
    /// master's own eyes to tune, and every round failed the same way -- too subtle to see in real, tall grass,
    /// judged from short test grass. So the ARCHITECTURE here is new and the NUMBERS are the ones that shipped:
    /// PressFromRadius reproduces the old shader's radius->strength curve exactly, and the local player keeps its
    /// 3.3 / 1.4 / 3.5 / 1.4. If the grass feels different after this change, that is a BUG, not a retune.
    ///
    /// WHAT THE OLD DESIGN COULD NOT DO, and why it was worth replacing rather than extending:
    ///   * It was GRASS ONLY -- the texture was called grass_displacers and exactly one shader read it, so a
    ///     helicopter could not touch a tree however hard it blew.
    ///   * A source was (position, radius) and nothing else, so the SHADER had to guess strength from footprint.
    ///     That works while every source is an object resting on the ground and breaks for a column of air.
    ///   * The per-frame pack lived in PlayerController._Process. The world's disturbance field is not the
    ///     player's property, and hanging it there meant no player, no field -- which is every harness that
    ///     renders foliage without one.</summary>
    public static class DisturbanceField
    {
        /// <summary>Columns in the data texture = the most sources that can bend foliage at once. The nearest
        /// Max to the camera win; foliage only renders within the cull range, so anything past it never mattered.
        /// 32 leaves room for a mover's wake trail on top of the live footprints.</summary>
        public const int Max = 32;

        public enum Kind { Press = 0, Wash = 1 }

        public static readonly StringName TexParam = "disturb_tex";
        public static readonly StringName CountParam = "disturb_count";

        // Typical footprints, metres of affected radius. Carried over unchanged.
        public const float VehicleRadius = 4.2f;
        public const float PlayerRadius = 0.6f;
        public const float ItemRadius = 0.4f;
        public const float PlayerWakeRadius = 3.3f;

        // The LOCAL PLAYER's numbers, verbatim from grass_displace.gdshader's uniforms. Retail pushes one global
        // point half a metre above the transform (GrassDisplacement.cs), which is what makes the bend read as a
        // shin pushing through; that offset is applied by the caller, the response curve is ours.
        public const float PlayerPushRadius = 3.3f, PlayerPushStrength = 1.4f, PlayerPushVert = 3.5f, PlayerPushFlat = 1.4f;

        /// <summary>The old shader's radius -> (strength, flatten, vertical) curve, lifted line for line so a car
        /// presses exactly the swath it pressed yesterday. It was `ps = clamp(pr*0.5, 0.6, 2.4)`,
        /// `pv = max(pr, 3)`, `pd = clamp(1 + pr*0.2, 1, 2.2)` inside the loop; it lives here now because
        /// strength is a property of the SOURCE, not something the shader should be inferring.</summary>
        public static void PressFromRadius(float r, out float strength, out float flatten, out float vert)
        {
            strength = Mathf.Clamp(r * 0.5f, 0.6f, 2.4f);
            vert = Mathf.Max(r, 3f);
            flatten = Mathf.Clamp(1f + r * 0.2f, 1f, 2.2f);
        }

        public sealed class Entry
        {
            public Node3D Node;
            public float Radius;
            public Kind Sort;
            public ulong Id;
            /// <summary>Optional: a source whose strength changes moment to moment (a spooling rotor) supplies
            /// one of these instead of being read off its radius. Returns 0..1; 0 drops it from the pack.</summary>
            public System.Func<float> Intensity;
            /// <summary>Metres BELOW the node to place the effect. A rotor's wash acts on the ground, not at the
            /// hub, so the heli projects its source down onto whatever it is hovering over.</summary>
            public float GroundDrop;
        }

        public static readonly List<Entry> Live = new();

        public static Image Img { get; private set; }
        public static ImageTexture Tex { get; private set; }
        /// <summary>How many sources the last Pack uploaded, AFTER the cap and the graphics gate.
        /// ⚠ A test seam, and a necessary one: HEADLESS GODOT REGISTERS NO GLOBAL SHADER PARAMETERS, so reading
        /// the count back with GlobalShaderParameterGet returns Nil (→ 0) whether the pack worked or not. That is
        /// a question about the renderer; this is the answer about our logic.</summary>
        public static int PackedCount { get; private set; }
        static bool _ready;

        /// <summary>Register the field's globals. ⚠ MUST run before any material that reads them is created: a
        /// material built while a global it references does not exist links it invalid, Godot logs "removed at
        /// some point", and ONE bad global poisons that material's whole uniform buffer -- so nothing displaces,
        /// silently, while the foliage still renders green. That bug shipped once already. Every foliage material
        /// site funnels through GrassDisplacers.EnsureGlobals, which calls this.</summary>
        public static void EnsureGlobals()
        {
            if (_ready) return;
            _ready = true;
            RenderingServer.GlobalShaderParameterAdd(CountParam, RenderingServer.GlobalShaderParameterType.Int, Variant.From(0));
            Img = Image.CreateEmpty(Max, 2, false, Image.Format.Rgbaf);   // 2 ROWS: pos+radius, then strength/flatten/kind/vert
            Tex = ImageTexture.CreateFromImage(Img);
            RenderingServer.GlobalShaderParameterAdd(TexParam, RenderingServer.GlobalShaderParameterType.Sampler2D, Variant.From(Tex));
        }

        /// <summary>Enlist a node. Idempotent -- re-registering updates in place rather than stacking duplicates.</summary>
        public static void Register(Node3D node, float radius, Kind sort = Kind.Press,
                                    System.Func<float> intensity = null, float groundDrop = 0f)
        {
            if (node == null) return;
            for (int i = 0; i < Live.Count; i++)
                if (Live[i].Node == node)
                { Live[i].Radius = radius; Live[i].Sort = sort; Live[i].Intensity = intensity; Live[i].GroundDrop = groundDrop; return; }
            Live.Add(new Entry { Node = node, Radius = radius, Sort = sort, Id = node.GetInstanceId(),
                                 Intensity = intensity, GroundDrop = groundDrop });
        }

        public static void Unregister(Node node)
        { for (int i = Live.Count - 1; i >= 0; i--) if (Live[i].Node == node) Live.RemoveAt(i); }

        // ---- WAKE: a moving source leaves a flattened trail that springs back up -------------------------------
        public const float WakeSeconds = 1.6f, WakeSpacing = 0.9f;
        struct Bread { public Vector3 Pos; public ulong Ms; public float R; }
        static readonly List<Bread> _wake = new();
        static readonly Dictionary<ulong, Vector3> _lastWake = new();

        public static void AgeWake(ulong nowMs)
        { for (int i = _wake.Count - 1; i >= 0; i--) if ((nowMs - _wake[i].Ms) * 0.001f >= WakeSeconds) _wake.RemoveAt(i); }

        /// <summary>Drop a breadcrumb if this source has moved far enough. ⚠ Gate the CALL on an actual mover: a
        /// stationary source drops one and never again, but its id lingers in _lastWake forever.</summary>
        public static void DropWake(ulong id, Vector3 pos, float radius, ulong nowMs)
        {
            if (_lastWake.TryGetValue(id, out var last) && pos.DistanceTo(last) < WakeSpacing) return;
            _lastWake[id] = pos;
            _wake.Add(new Bread { Pos = pos, Ms = nowMs, R = radius });
        }

        // ---- THE PACK -------------------------------------------------------------------------------------------
        struct Slot : System.IEquatable<Slot>
        {
            public float D2; public Vector3 Pos; public float R, S, F, V; public Kind K;
            public bool Equals(Slot o) => Pos == o.Pos && R == o.R && S == o.S && F == o.F && V == o.V && K == o.K;
        }
        static readonly List<Slot> _scratch = new();
        static readonly List<Slot> _prev = new();   // last uploaded set -- see the upload guard in Pack

        /// <summary>Gather every live source near the camera, keep the nearest Max, and upload. Called once a
        /// frame by whoever owns the world -- NOT by the player, who is merely one of the sources.</summary>
        public static void Pack(Vector3 cam, float range, ulong nowMs, Vector3? playerPoint = null)
        {
            EnsureGlobals();
            AgeWake(nowMs);
            _scratch.Clear();
            float range2 = range * range;

            // The LOCAL PLAYER, if there is one. Retail pushes this as its own global point; here it is simply the
            // first source, with the tuning it already had. One curve for everything is the point of the rebuild.
            if (playerPoint.HasValue)
                Add(playerPoint.Value, PlayerPushRadius, PlayerPushStrength, PlayerPushFlat, PlayerPushVert, Kind.Press, cam, range2);

            for (int i = Live.Count - 1; i >= 0; i--)
            {
                var e = Live[i];
                if (e.Node == null || !GodotObject.IsInstanceValid(e.Node)) { Live.RemoveAt(i); continue; }
                float k = e.Intensity != null ? Mathf.Clamp(e.Intensity(), 0f, 1f) : 1f;
                if (k <= 0.001f) continue;                       // a parked heli's rotor is not blowing anything
                var p = e.Node.GlobalPosition;
                p.Y -= e.GroundDrop;
                if (e.Sort == Kind.Wash) Add(p, e.Radius * k, WashStrength * k, WashFlatten, WashVert, Kind.Wash, cam, range2);
                else
                {
                    PressFromRadius(e.Radius, out float s, out float f, out float v);
                    Add(p, e.Radius, s * k, f, v, Kind.Press, cam, range2);
                }
            }

            foreach (var b in _wake)
            {
                float fade = 1f - (nowMs - b.Ms) * 0.001f / WakeSeconds;
                if (fade <= 0f) continue;
                float r = b.R * fade;   // the trail narrows AND weakens as the grass stands back up
                PressFromRadius(r, out float s, out float f, out float v);
                Add(b.Pos, r, s, f, v, Kind.Press, cam, range2);
            }

            _scratch.Sort((x, y) => x.D2.CompareTo(y.D2));   // nearest first, so the Max that survive are the ones on screen
            int n = Mathf.Min(_scratch.Count, Max);

            // ⚠⚠ UPLOAD ONLY WHEN THE SET ACTUALLY CHANGED. Carried over from the old driver, where it fixed two
            // real problems: an idle world was paying a GPU texture update every frame for nothing, and under the
            // separate render thread the update RACED the renderer -- four "empty image" errors on every load.
            // An unconditional Tex.Update here would quietly put both back.
            bool changed = _prev.Count != n;
            if (!changed) for (int i = 0; i < n; i++) if (!_prev[i].Equals(_scratch[i])) { changed = true; break; }
            if (changed)
            {
                _prev.Clear();
                for (int i = 0; i < n; i++)
                {
                    var s = _scratch[i];
                    _prev.Add(s);
                    Img.SetPixel(i, 0, new Color(s.Pos.X, s.Pos.Y, s.Pos.Z, s.R));
                    Img.SetPixel(i, 1, new Color(s.S, s.F, (float)(int)s.K, s.V));
                }
                if (!Img.IsEmpty()) Tex.Update(Img);   // the global sampler still points at this same RID
            }
            // The count is set every frame regardless: it is how the graphics setting switches the whole effect
            // off (retail IsGrassDisplacementEnabled), and that must take effect without waiting for a move.
            PackedCount = GraphicsOptions.GrassDisplacement ? n : 0;
            RenderingServer.GlobalShaderParameterSet(CountParam, PackedCount);
        }

        static void Add(Vector3 pos, float r, float s, float f, float v, Kind k, Vector3 cam, float range2)
        {
            if (r <= 0.01f) return;
            float dx = pos.X - cam.X, dz = pos.Z - cam.Z;
            float d2 = dx * dx + dz * dz;
            if (d2 > range2) return;
            _scratch.Add(new Slot { D2 = d2, Pos = pos, R = r, S = s, F = f, V = v, K = k });
        }

        // ---- ROTOR WASH ------------------------------------------------------------------------------------------
        /// <summary>How far out a rotor's downwash still lays grass over, as a multiple of the blade half-span.
        /// A real downwash column is roughly the disc's width and spreads as it hits the ground; 2.6 puts a
        /// Little Bird's wash at ~9 m and a transport's at ~14 m, which reads as a big circle of thrashing
        /// grass rather than a dinner plate under the aircraft.</summary>
        public const float WashRadiusPerBlade = 2.6f;
        /// <summary>Wash is STRONGER than anything standing on the ground -- it is the one disturbance that
        /// should be visible from the air -- and presses down hard, because downwash is a vertical column.</summary>
        /// ⚠ TUNED UP FROM 2.8/1.8 AFTER LOOKING, not before. At 2.8 an A/B against a ×8 crank showed the
        /// difference was real but barely readable -- the bushes under a hovering Huey still stood up straight.
        /// This effect's whole history is shipping too subtle: the grass flatten went out twice at roughly a
        /// third of what it needed, judged from short test grass, and master had to say "this goes easier if
        /// you dont doubt the person with eyes". A downwash should be the most violent thing in the field.
        /// Bracketed by rendering the same hover at 2.8 / 8.0 / 22: at 2.8 the bushes under a hovering Huey still
        /// stood straight up, and by 8.0 the FLATTEN had crushed them into the ground -- foliage deleted rather
        /// than blown. The fix was the flatten, not the strength: a downwash should throw plants OUTWARD hard and
        /// press them down only moderately, or a bush simply disappears into its own base.
        public const float WashStrength = 6.0f, WashFlatten = 1.6f;
        /// <summary>Vertical reach: how high the aircraft can be and still move the grass. A helicopter at 30 m
        /// is not laying a field flat, and without this gate it would do exactly that from any altitude.</summary>
        public const float WashVert = 18f;
    }
}
