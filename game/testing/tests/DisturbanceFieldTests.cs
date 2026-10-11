using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The rebuilt plant-disturbance field (strawberry 2026-10-11: "rip out our grass displacement and
    /// rebuild it from the ground up. add rotor wash for helis on foliage and trees").
    ///
    /// ⚠⚠ THE MOST IMPORTANT CHECK HERE IS THAT NOTHING CHANGED. The old grass displacement took three rounds
    /// against master's own eyes to tune, and every round failed the same way: too subtle to see in real, tall
    /// grass. A rebuild that quietly re-derives those numbers would regress a thing master already signed off,
    /// and it would do it invisibly -- grass still renders, it just bends differently. So the radius->push curve
    /// is asserted against the literal constants the OLD shader shipped with.
    ///
    /// Everything else here is packing: the field is a data texture read by two shaders, and a wrong row, a
    /// wrong kind or a dropped source produces no error at all, just foliage that does not move.</summary>
    public sealed class DisturbanceFieldTests : GameTest
    {
        public override string Name => "world.disturbance_field";
        public override double TimeoutSimSeconds => 40;

        static (Color a, Color b) Slot(int i)
        {
            var img = DisturbanceField.Img;
            return (img.GetPixel(i, 0), img.GetPixel(i, 1));
        }
        /// <summary>⚠ NOT GlobalShaderParameterGet. Headless Godot registers no global shader parameters at all,
        /// so reading the count back off the renderer returns Nil -> 0 whether the pack worked or not -- the first
        /// version of this test did exactly that and reported "packs one source (0)" against a working field.
        /// PackedCount is the part that is ours.</summary>
        static int Count() => DisturbanceField.PackedCount;

        public override IEnumerable<Step> Run()
        {
            DisturbanceField.EnsureGlobals();
            yield return Ticks(1);

            // ---- 1. THE TUNING IS THE OLD TUNING. These are the literal expressions from the retired
            // grass_displace.gdshader loop -- ps = clamp(pr*0.5, 0.6, 2.4), pv = max(pr, 3), pd = clamp(1 + pr*0.2,
            // 1, 2.2) -- recomputed here rather than referenced, so this fails if PressFromRadius drifts.
            foreach (float r in new[] { 0.4f, 0.6f, 3.3f, 4.2f, 12f })
            {
                DisturbanceField.PressFromRadius(r, out float s, out float f, out float v);
                float wantS = Mathf.Clamp(r * 0.5f, 0.6f, 2.4f);
                float wantV = Mathf.Max(r, 3f);
                float wantF = Mathf.Clamp(1f + r * 0.2f, 1f, 2.2f);
                T.Check($"r={r}: push curve matches the shipped shader ({s:0.000}/{f:0.000}/{v:0.000})",
                        Mathf.IsEqualApprox(s, wantS) && Mathf.IsEqualApprox(f, wantF) && Mathf.IsEqualApprox(v, wantV));
            }
            // The local player's four numbers, which master tuned by eye and confirmed.
            T.Check($"the player's push is still 3.3/1.4/3.5/1.4 ({DisturbanceField.PlayerPushRadius}/{DisturbanceField.PlayerPushStrength}/{DisturbanceField.PlayerPushVert}/{DisturbanceField.PlayerPushFlat})",
                    DisturbanceField.PlayerPushRadius == 3.3f && DisturbanceField.PlayerPushStrength == 1.4f
                    && DisturbanceField.PlayerPushVert == 3.5f && DisturbanceField.PlayerPushFlat == 1.4f);

            // ---- 2. THE PLAYER PACKS AS AN ORDINARY SOURCE. He used to be a separate global point; the whole
            // point of the rebuild is that there is now one curve and one field.
            DisturbanceField.Live.Clear();
            var origin = Vector3.Zero;
            DisturbanceField.Pack(origin, 160f, 1000, new Vector3(1f, 0.5f, 2f));
            T.Check($"the player alone packs one source ({Count()})", Count() == 1);
            var (a0, b0) = Slot(0);
            T.Check($"...row 0 carries world position + radius ({a0.R},{a0.G},{a0.B} r={a0.A})",
                    Mathf.IsEqualApprox(a0.R, 1f) && Mathf.IsEqualApprox(a0.B, 2f)
                    && Mathf.IsEqualApprox(a0.A, DisturbanceField.PlayerPushRadius));
            T.Check($"...row 1 carries strength/flatten/kind/vert ({b0.R},{b0.G},{b0.B},{b0.A})",
                    Mathf.IsEqualApprox(b0.R, DisturbanceField.PlayerPushStrength)
                    && Mathf.IsEqualApprox(b0.G, DisturbanceField.PlayerPushFlat)
                    && Mathf.IsEqualApprox(b0.B, 0f)        // Press
                    && Mathf.IsEqualApprox(b0.A, DisturbanceField.PlayerPushVert));

            // ---- 3. ROTOR WASH IS ITS OWN KIND, and that is the thing the old single-texel layout could not
            // express at all: strength had to be guessed from footprint, which is meaningless for a column of air.
            var heli = new Node3D();
            World.AddChild(heli);
            heli.GlobalPosition = new Vector3(10f, 6f, 0f);
            float spool = 1f;
            DisturbanceField.Live.Clear();
            DisturbanceField.Register(heli, 9f, DisturbanceField.Kind.Wash, () => spool, groundDrop: 2.2f);
            DisturbanceField.Pack(origin, 160f, 2000);
            T.Check($"a spooled rotor packs a source ({Count()})", Count() == 1);
            var (a1, b1) = Slot(0);
            T.Check($"...marked as WASH, not a ground press (kind {b1.B})", Mathf.IsEqualApprox(b1.B, 1f));
            T.Check($"...dropped to the ground under the aircraft (y {a1.G:0.00} from 6.00)",
                    Mathf.IsEqualApprox(a1.G, 6f - 2.2f));
            T.Check($"...and blows harder than anything standing on the ground ({b1.R:0.00} > 2.4 max press)",
                    b1.R > 2.4f);

            // ---- 4. SPOOL GATES IT. A parked helicopter must not flatten the field it is sitting in -- and this
            // is why intensity is a callback and not a number captured at registration.
            spool = 0f;
            DisturbanceField.Pack(origin, 160f, 2100);
            T.Check($"a stopped rotor blows nothing ({Count()})", Count() == 0);
            spool = 0.5f;
            DisturbanceField.Pack(origin, 160f, 2200);
            var (a2, b2) = Slot(0);
            T.Check($"...and a half-spooled one is weaker and narrower ({b2.R:0.00} < {b1.R:0.00}, r {a2.A:0.0} < {a1.A:0.0})",
                    Count() == 1 && b2.R < b1.R && a2.A < a1.A);

            // ---- 5. RANGE AND CAP. Foliage only renders so far; a source past that displaces nothing visible,
            // and the texture is a fixed width so the nearest must win.
            spool = 1f;
            heli.GlobalPosition = new Vector3(900f, 6f, 0f);
            DisturbanceField.Pack(origin, 160f, 2300);
            T.Check($"a source beyond foliage range is dropped ({Count()})", Count() == 0);

            DisturbanceField.Live.Clear();
            var many = new List<Node3D>();
            for (int i = 0; i < DisturbanceField.Max + 12; i++)
            {
                var n = new Node3D(); World.AddChild(n); many.Add(n);
                n.GlobalPosition = new Vector3(i * 1.5f, 0f, 0f);   // increasing distance from the camera at origin
                DisturbanceField.Register(n, 2f);
            }
            DisturbanceField.Pack(origin, 160f, 2400);
            T.Check($"the pack is capped at Max ({Count()} <= {DisturbanceField.Max})", Count() == DisturbanceField.Max);
            var (aNear, _) = Slot(0);
            T.Check($"...and the NEAREST survive (first slot x={aNear.R:0.0})", aNear.R < 3f);

            // ---- 6. THE GRAPHICS SETTING STILL SWITCHES IT OFF (retail IsGrassDisplacementEnabled). It gates the
            // COUNT rather than the pack, so it has to take effect on a quiet frame too -- not only when something
            // moves and triggers an upload.
            bool was = GraphicsOptions.GrassDisplacement;
            GraphicsOptions.GrassDisplacement = false;
            DisturbanceField.Pack(origin, 160f, 2500);
            T.Check($"displacement off -> no sources reach the shader ({Count()})", Count() == 0);
            GraphicsOptions.GrassDisplacement = was;

            foreach (var n in many) { World.RemoveChild(n); n.QueueFree(); }
            World.RemoveChild(heli); heli.QueueFree();
            DisturbanceField.Live.Clear();
            yield return Ticks(1);
        }
    }
}
