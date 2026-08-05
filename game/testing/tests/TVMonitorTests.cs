using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // COMPUTER MONITORS + POWER IO (master: "add power io for both TVs, computer crt and computer flat screen monitor.
    // then i want u to dupe the CRT thing onto the computer crt, minus the test pattern, and vertical hold desync, and
    // test tone. computer monitors cycle through a few random colors (which tints the 'cone'), same for the flatscreen
    // computer monitor, minus the crt exclusive things. another thing is making all tvs/monitors on at start").
    //
    // The load-bearing claim in that request is a MINUS list, and every item on it fails silently in the same direction:
    // a monitor that kept the test card, or the roll, or the tone, still works, still looks like a screen, and is only
    // wrong if you already know what it should have been. So the suite asserts each absence by name against the kind
    // table, not by looking at one built device.
    //
    // The other half is done against the REAL prop meshes. Television_0/1 and Computer_0/3 are in the repo as .obj +
    // palette png, so the actual UV predicate, the actual glass texel and the actual plug placement are all reachable
    // here -- which matters because "which face is the screen" is a fact about the ART, and the failure it produces is
    // a picture rendered on the back of the cabinet rather than an exception.
    public sealed class TVMonitorTests : GameTest
    {
        public override string Name => "tv.monitors_and_power_io";

        static int Tris(ArrayMesh m) => m == null ? 0 : m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;

        /// <summary>A screen face's real dimensions: its two IN-PLANE extents, plus how far the worst vertex strays
        /// off the plane.
        ///
        /// This used to sort the AABB's three axis extents and call the smallest one "thickness", which is only a
        /// planarity test for a face that happens to be axis-aligned. The laptop's lid is HINGED -- tilted about 6
        /// degrees back -- so its bounding box is 0.05 m deep and that measurement called a perfectly flat quad
        /// non-planar. It also mismeasured the height, reporting the box's vertical span rather than the panel's own.
        /// Fitting the plane instead is both the correct test and the one that keeps working when a prop is angled.</summary>
        static (float A, float B, float Off) FaceExtent(ArrayMesh m)
        {
            var v = m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (v.Length < 3) return (0f, 0f, float.MaxValue);

            Vector3 c = Vector3.Zero;
            foreach (var p in v) c += p;
            c /= v.Length;
            // Summed cross products: robust to one sliver triangle in a way that taking tri 0's normal is not.
            Vector3 n = Vector3.Zero;
            for (int i = 0; i + 2 < v.Length; i += 3) n += (v[i + 1] - v[i]).Cross(v[i + 2] - v[i]);
            if (n.LengthSquared() < 1e-12f) return (0f, 0f, float.MaxValue);
            n = n.Normalized();

            // Any two in-plane axes will do -- the pair of extents is reported sorted, so which one is "width" does
            // not have to be recovered.
            Vector3 ax = (Mathf.Abs(n.Z) < 0.9f ? Vector3.Back : Vector3.Right);
            ax = (ax - n * ax.Dot(n)).Normalized();
            Vector3 ay = n.Cross(ax).Normalized();

            float uLo = float.MaxValue, uHi = float.MinValue, vLo = float.MaxValue, vHi = float.MinValue, off = 0f;
            foreach (var p in v)
            {
                var d = p - c;
                float u = d.Dot(ax), w = d.Dot(ay);
                uLo = Mathf.Min(uLo, u); uHi = Mathf.Max(uHi, u);
                vLo = Mathf.Min(vLo, w); vHi = Mathf.Max(vHi, w);
                off = Mathf.Max(off, Mathf.Abs(d.Dot(n)));
            }
            float a = uHi - uLo, b = vHi - vLo;
            return (Mathf.Max(a, b), Mathf.Min(a, b), off);
        }

        public override IEnumerable<Step> Run()
        {
            // ---- THE KIND TABLE. Four props, two axes, and one feature (the roll) that belongs to a single cell.
            var K = TVDevice.KindFor;
            T.Check("Television_1 is the CRT television", K("Television_1") == TVDevice.DeviceKind.CrtTv);
            T.Check("Television_0 is the flatscreen television", K("Television_0") == TVDevice.DeviceKind.FlatTv);
            T.Check("Computer_0 is the CRT monitor", K("Computer_0") == TVDevice.DeviceKind.CrtMonitor);
            T.Check("Computer_3 is the flatscreen monitor", K("Computer_3") == TVDevice.DeviceKind.FlatMonitor);

            // IsDeviceProp and KindFor are a PAIR: KindFor's default arm is only safe because nothing else ever reaches
            // it. Pin that the gate admits exactly the four -- in particular that it rejects the other three Computer_N
            // props, which are towers and a keyboard and would otherwise get a screen carved out of a case panel.
            foreach (var prop in new[] { "Television_0", "Television_1", "Computer_0", "Computer_2", "Computer_3" })
                T.Check($"{prop} is a screen prop", TVDevice.IsDeviceProp(prop));
            // Computer_1 and Computer_4 are the TOWERS. They are excluded on evidence, not on the name: their CRT
            // predicate match is the drive slot, ten triangles across a 0.04 m slab, asserted at the bottom of this
            // suite. Computer_2 was in this list until master pointed out it is the laptop -- it had been sorted by
            // bounding box, where a 0.76 x 0.61 x 0.67 prop reads as a small case.
            foreach (var prop in new[] { "Computer_1", "Computer_4", "Chair_Metal_0" })
                T.Check($"{prop} is NOT ({(prop.StartsWith("Computer") ? "a tower, its screen-texel match is a drive slot" : "not a screen at all")})",
                    !TVDevice.IsDeviceProp(prop));

            // ---- THE MINUS LIST, item by item.
            var crtMon = TVDevice.DeviceKind.CrtMonitor;
            var flatMon = TVDevice.DeviceKind.FlatMonitor;
            T.Check("the computer CRT is a TUBE -- it warms up, flickers and collapses like the television one",
                TVDevice.IsTube(crtMon));
            T.Check("...but shows NO test pattern", !TVDevice.HasPattern(crtMon));
            T.Check("...NO vertical-hold roll", !TVDevice.HasDesync(crtMon));
            T.Check("...and NO test tone", !TVDevice.HasTone(crtMon));
            T.Check("...it cycles a flat colour instead", TVDevice.CyclesColour(crtMon));
            T.Check("the flatscreen monitor is a PANEL (no warmup/flicker/collapse)", !TVDevice.IsTube(flatMon));
            T.Check("...and takes the same minus list", !TVDevice.HasPattern(flatMon) && !TVDevice.HasDesync(flatMon)
                && !TVDevice.HasTone(flatMon) && TVDevice.CyclesColour(flatMon));
            // The LAPTOP (master: "theres also a laptop model, get that with the flatscreen thing too"). Its whole
            // behaviour is the flatscreen monitor's; only its label and its draw differ, because it is a computer
            // rather than a panel on a desk. Asserted as an EQUIVALENCE against flatMon rather than as five separate
            // flags, so a later change to the flatscreen's behaviour cannot leave the laptop behind without saying so.
            var lap = TVDevice.DeviceKind.Laptop;
            T.Check("the laptop behaves exactly like the flatscreen monitor",
                TVDevice.IsTube(lap) == TVDevice.IsTube(flatMon) && TVDevice.HasPattern(lap) == TVDevice.HasPattern(flatMon)
                && TVDevice.HasDesync(lap) == TVDevice.HasDesync(flatMon) && TVDevice.HasTone(lap) == TVDevice.HasTone(flatMon)
                && TVDevice.CyclesColour(lap) == TVDevice.CyclesColour(flatMon));
            T.Check($"...but says what it is ({TVDevice.LabelFor(lap)})", TVDevice.LabelFor(lap) == "Laptop");
            T.Check($"...and draws like a whole computer, not a bare panel ({TVDevice.WattsFor(lap):0} vs {TVDevice.WattsFor(flatMon):0} W)",
                TVDevice.WattsFor(lap) > TVDevice.WattsFor(flatMon));
            // A NEW KIND MUST DEFAULT TO QUIET. Every predicate is an allowlist, so an unhandled member is false
            // everywhere -- no card, no roll, no tone. Asserted over the whole enum so the next kind added inherits
            // the guarantee rather than relying on whoever adds it having read the comment.
            foreach (TVDevice.DeviceKind k in System.Enum.GetValues(typeof(TVDevice.DeviceKind)))
            {
                T.Check($"{k}: a roll implies a card (only a television rolls)", !TVDevice.HasDesync(k) || TVDevice.HasPattern(k));
                T.Check($"{k}: a tone implies a card (the tone IS the test broadcast)", TVDevice.HasTone(k) == TVDevice.HasPattern(k));
                T.Check($"{k}: it either shows a card or cycles a colour, never both and never neither",
                    TVDevice.HasPattern(k) != TVDevice.CyclesColour(k));
                T.Check($"{k}: draws a real load ({TVDevice.WattsFor(k):0} W)", TVDevice.WattsFor(k) > 0f);
                T.Check($"{k}: has a label", !string.IsNullOrWhiteSpace(TVDevice.LabelFor(k)));
            }
            // ...and the televisions did NOT lose anything on the way. Half of a "dupe X minus Y" change going wrong
            // looks like the SOURCE losing Y, and nobody re-checks the thing that already worked.
            T.Check("the CRT television kept its card, its roll and its tone",
                TVDevice.HasPattern(TVDevice.DeviceKind.CrtTv) && TVDevice.HasDesync(TVDevice.DeviceKind.CrtTv)
                && TVDevice.HasTone(TVDevice.DeviceKind.CrtTv) && TVDevice.IsTube(TVDevice.DeviceKind.CrtTv));
            T.Check("the flatscreen television kept its card and tone and is still a panel",
                TVDevice.HasPattern(TVDevice.DeviceKind.FlatTv) && TVDevice.HasTone(TVDevice.DeviceKind.FlatTv)
                && !TVDevice.IsTube(TVDevice.DeviceKind.FlatTv) && !TVDevice.HasDesync(TVDevice.DeviceKind.FlatTv));

            // ---- THE STANDBY LAMP IS A THREE-STATE READ, not a negation of "on". A set with no mains and no wire shows
            // NOTHING -- an unpowered television does not glow red. Getting this wrong is invisible in the common case
            // (mains up, set switched off -> red either way) and only shows during a blackout, which is exactly when
            // someone would be looking at a wall of televisions to see whether the power is back.
            T.Check("lit -> green, not red", TVDevice.LedState(lit: true, hasFeed: true, broken: false, screenShot: false) == (true, false));
            T.Check("powered but switched off -> red standby",
                TVDevice.LedState(lit: false, hasFeed: true, broken: false, screenShot: false) == (false, true));
            T.Check("NO power at all -> nothing lit, not standby",
                TVDevice.LedState(lit: false, hasFeed: false, broken: false, screenShot: false) == (false, false));
            T.Check("smashed -> nothing, even on a live grid",
                TVDevice.LedState(lit: false, hasFeed: true, broken: true, screenShot: false) == (false, false));
            T.Check("shot-out glass -> no standby either (the set is not waiting for anything)",
                TVDevice.LedState(lit: false, hasFeed: true, broken: false, screenShot: true) == (false, false));
            T.Check("...and the two lamps are never both lit",
                !new[] { (true, true, false, false), (false, true, false, false), (false, false, false, false) }
                    .Select(t => TVDevice.LedState(t.Item1, t.Item2, t.Item3, t.Item4))
                    .Any(r => r.On && r.Standby));

            // ---- WHICH PROPS HAVE WHICH LAMPS. Only the flatscreen television has a red/green PAIR; the monitors have
            // a single green; the CRT television's cube is plain grey and the laptop has no indicator geometry at all.
            T.Check("the flatscreen TV has both lamps",
                TVDevice.LedsFor(TVDevice.DeviceKind.FlatTv).On != null && TVDevice.LedsFor(TVDevice.DeviceKind.FlatTv).Standby != null);
            foreach (var k in new[] { TVDevice.DeviceKind.CrtMonitor, TVDevice.DeviceKind.FlatMonitor })
                T.Check($"{k} has an on-lamp but no standby lamp",
                    TVDevice.LedsFor(k).On != null && TVDevice.LedsFor(k).Standby == null);
            foreach (var k in new[] { TVDevice.DeviceKind.CrtTv, TVDevice.DeviceKind.Laptop })
                T.Check($"{k} has no indicator geometry, so it claims neither lamp",
                    TVDevice.LedsFor(k).On == null && TVDevice.LedsFor(k).Standby == null);

            // ---- THE COLOUR CYCLE never repeats. A plain `Randi() % n` sits on the same colour a fifth of the time,
            // and a repeat does not read as chance -- it reads as the cycle having STOPPED, which is how it would be
            // reported. Swept across the whole roll range and every starting index rather than sampled.
            int n = 5, repeats = 0, distinct = 0;
            var reached = new HashSet<int>();
            for (int cur = 0; cur < n; cur++)
                for (int i = 0; i <= 200; i++)
                {
                    int nxt = TVDevice.NextColourIndex(cur, i / 200f, n);
                    if (nxt == cur) repeats++; else distinct++;
                    if (nxt < 0 || nxt >= n) repeats++;   // out of range is worse than a repeat: it would throw on index
                    if (cur == 0) reached.Add(nxt);
                }
            T.Check($"the next colour is never the current one ({distinct} advances, {repeats} repeats)", repeats == 0);
            // ...and it can reach ALL of them. A "never repeats" rule is trivially satisfiable by always advancing by
            // exactly one, which would make five monitors in a room show the same rotation in lockstep forever.
            T.Check($"...and every other colour is reachable from a given one ({reached.Count}/{n - 1})",
                reached.Count == n - 1);
            T.Check("a one-colour palette degenerates safely rather than looping forever",
                TVDevice.NextColourIndex(0, 0.5f, 1) == 0);

            // ---- THE CONE FOLLOWS WHAT IS ON SCREEN (master: "change the intensity of the cone to represent the
            // average brightness of the colors on screen"). The values are MEASURED means, so what is pinned here is
            // the ORDERING and the anchor -- exact constants would just restate the table, and the ordering is the
            // thing that is actually claimed: a black terminal must throw less light than a test card.
            float LumOf(TVDevice.ScreenProgram p) => TVDevice.MeanLuma(p, Colors.White);
            T.Check($"snow is the brightest thing a set shows ({LumOf(TVDevice.ScreenProgram.Static):0.000})",
                LumOf(TVDevice.ScreenProgram.Static) > LumOf(TVDevice.ScreenProgram.TestCard));
            T.Check($"...the test card next ({LumOf(TVDevice.ScreenProgram.TestCard):0.000})",
                LumOf(TVDevice.ScreenProgram.TestCard) > LumOf(TVDevice.ScreenProgram.BarGraph));
            T.Check($"...bar graphs above scrolling text ({LumOf(TVDevice.ScreenProgram.BarGraph):0.000} > {LumOf(TVDevice.ScreenProgram.TerminalScroll):0.000})",
                LumOf(TVDevice.ScreenProgram.BarGraph) > LumOf(TVDevice.ScreenProgram.TerminalScroll));
            T.Check($"...text above a mostly-black DVD screen ({LumOf(TVDevice.ScreenProgram.TerminalScroll):0.000} > {LumOf(TVDevice.ScreenProgram.Dvd):0.000})",
                LumOf(TVDevice.ScreenProgram.TerminalScroll) > LumOf(TVDevice.ScreenProgram.Dvd));
            T.Check($"...and an idle terminal is nearly black ({LumOf(TVDevice.ScreenProgram.TerminalCursor):0.000})",
                LumOf(TVDevice.ScreenProgram.TerminalCursor) < 0.01f);

            // THE ANCHOR: a test card leaves the cone exactly where it was tuned, so this change cannot have quietly
            // re-lit every television in the map while claiming to be about the dark ones.
            T.Check($"a test card scales the cone by 1.0 ({TVDevice.ConeScale(TVDevice.ScreenProgram.TestCard, Colors.White):0.000})",
                Mathf.IsEqualApprox(TVDevice.ConeScale(TVDevice.ScreenProgram.TestCard, Colors.White), 1f));
            // ...and nothing reaches zero, or a dark screen would read as a broken cone rather than as a dark screen.
            foreach (TVDevice.ScreenProgram p in System.Enum.GetValues(typeof(TVDevice.ScreenProgram)))
                T.Check($"{p}: the cone never goes fully out ({TVDevice.ConeScale(p, Colors.Black):0.000})",
                    TVDevice.ConeScale(p, Colors.Black) > 0f);

            // The FLAT COLOUR program's brightness is the colour it is showing, so its cone dims and brightens as it
            // cycles. Checked with two real palette colours rather than an assertion about the formula.
            float dark = TVDevice.ConeScale(TVDevice.ScreenProgram.Colour, new Color(0.13f, 0.30f, 0.75f));  // desktop blue
            float pale = TVDevice.ConeScale(TVDevice.ScreenProgram.Colour, new Color(0.62f, 0.66f, 0.70f));  // grey UI
            T.Check($"a pale colour throws more light than a deep blue ({pale:0.00} vs {dark:0.00})", pale > dark);

            // ---- WHERE THE LIGHT COMES FROM (master: "the light beam comes from the dvd logo as it moves across the
            // dark screen, the blinking cursor is the source of that cone"). Mean brightness alone made a screen
            // showing one small bright thing into a DIM light at the CENTRE; it is a SMALL light where that thing is.
            var half = TVDevice.BlobHalf(2.0f);
            // The blob's emitter must sit ON the blob. Note the V FLIP: UV v runs DOWN the screen (image top at screen
            // top), so a centre computed without it lands on the blob's mirror image -- which still tracks, still
            // moves, and is wrong in a way that looks like it is working.
            var topLeft = TVDevice.Emitter(TVDevice.ScreenProgram.Dvd, new Vector2(0.2f, 0.2f), half);
            var botRight = TVDevice.Emitter(TVDevice.ScreenProgram.Dvd, new Vector2(0.8f, 0.8f), half);
            T.Check($"a blob at UV (0.2,0.2) emits from up-LEFT ({topLeft.Centre})",
                topLeft.Centre.X < 0f && topLeft.Centre.Y > 0f);
            T.Check($"...and at (0.8,0.8) from down-RIGHT ({botRight.Centre})",
                botRight.Centre.X > 0f && botRight.Centre.Y < 0f);
            T.Check($"the cursor emits from the top-left corner ({TVDevice.Emitter(TVDevice.ScreenProgram.TerminalCursor, Vector2.Zero, half).Centre})",
                TVDevice.Emitter(TVDevice.ScreenProgram.TerminalCursor, Vector2.Zero, half).Centre.X < -0.3f
                && TVDevice.Emitter(TVDevice.ScreenProgram.TerminalCursor, Vector2.Zero, half).Centre.Y > 0.3f);
            T.Check($"bar graphs emit from LOW and wide ({TVDevice.Emitter(TVDevice.ScreenProgram.BarGraph, Vector2.Zero, half).Centre})",
                TVDevice.Emitter(TVDevice.ScreenProgram.BarGraph, Vector2.Zero, half).Centre.Y < 0f);
            foreach (var full in new[] { TVDevice.ScreenProgram.TestCard, TVDevice.ScreenProgram.Static, TVDevice.ScreenProgram.Colour })
            {
                var e = TVDevice.Emitter(full, Vector2.Zero, half);
                T.Check($"{full} genuinely fills the screen, so it stays centred and full-width ({e.Centre}, {e.Extent})",
                    e.Centre == Vector2.Zero && e.Extent == Vector2.One);
            }
            // A cursor is a far smaller emitter than a test card -- that is what narrows its beam.
            T.Check("a cursor's lit area is a fraction of a test card's",
                TVDevice.Emitter(TVDevice.ScreenProgram.TerminalCursor, Vector2.Zero, half).Extent.X
                < TVDevice.Emitter(TVDevice.ScreenProgram.TestCard, Vector2.Zero, half).Extent.X * 0.2f);

            // ---- THE BOUNCE. Triangle waves reflect perfectly off the walls, so the logo must stay wholly on screen
            // for all time -- a blob that clips through an edge is the classic failure of doing this with a modulo.
            float minX = 1f, maxX = 0f, minY = 1f, maxY = 0f;
            var seen = new List<Vector2>();
            for (float t = 0f; t < 400f; t += 0.37f)
            {
                var b = TVDevice.BlobPos(t, 3.7f, half);
                minX = Mathf.Min(minX, b.X); maxX = Mathf.Max(maxX, b.X);
                minY = Mathf.Min(minY, b.Y); maxY = Mathf.Max(maxY, b.Y);
                seen.Add(b);
            }
            T.Check($"the logo never leaves the screen horizontally ([{minX:0.000},{maxX:0.000}] within [{half.X:0.000},{1 - half.X:0.000}])",
                minX >= half.X - 1e-4f && maxX <= 1f - half.X + 1e-4f);
            T.Check($"...nor vertically ([{minY:0.000},{maxY:0.000}])",
                minY >= half.Y - 1e-4f && maxY <= 1f - half.Y + 1e-4f);
            T.Check("...and it actually reaches both walls, so it is bouncing rather than drifting in the middle",
                minX < half.X + 0.02f && maxX > 1f - half.X - 0.02f);
            T.Check($"...covering the screen rather than retracing one line ({seen.Select(v => Mathf.RoundToInt(v.X * 6) * 8 + Mathf.RoundToInt(v.Y * 6)).Distinct().Count()} cells visited)",
                seen.Select(v => Mathf.RoundToInt(v.X * 6) * 8 + Mathf.RoundToInt(v.Y * 6)).Distinct().Count() > 15);

            // ---- ASPECT. The screen's UV is 0..1 across a face that is 3.55 x 1.80 m on the big flatscreen, so equal
            // UV steps are nothing like equal distances. Without this the logo is a wide smear on exactly the set it is
            // most visible on -- and it would still bounce, so nothing else would look wrong.
            var wide = TVDevice.BlobHalf(3.55f / 1.80f);
            var square = TVDevice.BlobHalf(1f);
            T.Check($"on a 2:1 screen the logo is narrower in UV to stay square ({wide.X:0.000} vs {wide.Y:0.000})",
                wide.X < wide.Y * 0.6f);
            T.Check($"...on a square screen it is not squashed at all ({square.X:0.000} vs {square.Y:0.000})",
                Mathf.IsEqualApprox(square.X, square.Y));
            T.Check($"...and in WORLD units it comes out square on both ({wide.X * 3.55f / (wide.Y * 1.80f):0.000})",
                Mathf.Abs(wide.X * 3.55f / (wide.Y * 1.80f) - 1f) < 0.02f);

            // ---- WATTS are per kind and none of them is zero. A 0 W consumer is a REAL thing in this codebase (a
            // splitter's relay input) and the solver treats it as always-satisfied -- so a monitor that fell through to
            // 0 W would light up wired to nothing at all and look like the feature working.
            foreach (var k in new[] { TVDevice.DeviceKind.CrtTv, TVDevice.DeviceKind.FlatTv, crtMon, flatMon })
                T.Check($"{k} draws a real load ({TVDevice.WattsFor(k):0} W)", TVDevice.WattsFor(k) > 0f);
            T.Check($"a tube costs more than the panel it replaced (TV {TVDevice.WattsFor(TVDevice.DeviceKind.CrtTv):0} > {TVDevice.WattsFor(TVDevice.DeviceKind.FlatTv):0} W, "
                + $"monitor {TVDevice.WattsFor(crtMon):0} > {TVDevice.WattsFor(flatMon):0} W)",
                TVDevice.WattsFor(TVDevice.DeviceKind.CrtTv) > TVDevice.WattsFor(TVDevice.DeviceKind.FlatTv)
                && TVDevice.WattsFor(crtMon) > TVDevice.WattsFor(flatMon));

            // ---- MONOCHROME is Rec.709 luma, shared with the SMPTE mono copy. A flat channel average sends pure blue
            // and pure green to the SAME grey, so a collapsing monitor would lose which colour it had been showing --
            // and a mono SMPTE card would lose several of its bars, which is the only reason to go mono at all.
            var mBlue = TVDevice.Mono(new Color(0f, 0f, 1f));
            var mGreen = TVDevice.Mono(new Color(0f, 1f, 0f));
            T.Check($"green reads far brighter than blue ({mGreen.R:0.00} vs {mBlue.R:0.00})", mGreen.R > mBlue.R + 0.5f);
            T.Check("...where a flat RGB average would make them identical -- so this has teeth",
                Mathf.IsEqualApprox((0f + 0f + 1f) / 3f, (0f + 1f + 0f) / 3f));
            T.Check("mono is achromatic", Mathf.IsEqualApprox(mGreen.R, mGreen.G) && Mathf.IsEqualApprox(mGreen.G, mGreen.B));
            T.Check("...and keeps alpha (the warmup crossfade rides it)",
                Mathf.IsEqualApprox(TVDevice.Mono(new Color(1f, 0f, 0f, 0.4f)).A, 0.4f));

            // ---- THE TINTED SCREEN COLOUR. Albedo MULTIPLIES on an unshaded material, which is why a television's
            // tint has to be WHITE (anything else recolours the SMPTE bars) and why a monitor can carry its colour here
            // at all.
            var lit = TVDevice.ScreenColor(new Color(0.2f, 0.8f, 0.4f), 1f, 1f);
            T.Check($"a monitor's screen colour survives at full brightness ({lit.G:0.00})", Mathf.IsEqualApprox(lit.G, 0.8f));
            T.Check("...and scales with brightness rather than being replaced by it",
                Mathf.IsEqualApprox(TVDevice.ScreenColor(new Color(0.2f, 0.8f, 0.4f), 0.5f, 1f).G, 0.4f));
            T.Check("the untinted overload is still exactly white x brightness (the televisions' path is unchanged)",
                TVDevice.ScreenColor(0.7f, 1f) == TVDevice.ScreenColor(Colors.White, 0.7f, 1f));
            T.Check("fade rides alpha, not brightness -- the crossfade out of the glass depends on it",
                Mathf.IsEqualApprox(TVDevice.ScreenColor(new Color(1f, 1f, 1f), 1f, 0.25f).A, 0.25f));

            // ================= AGAINST THE REAL PROPS =================
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            // Expected screen face, measured off the .obj: (width, height) in metres. These are the numbers that say
            // the predicate found the SCREEN and not some other panel sharing the texel.
            var props = new (string Name, float W, float H)[]
            {
                ("Television_1", 0.85f, 0.79f),   // CRT television
                ("Television_0", 3.55f, 1.80f),   // flatscreen television (the big wall set)
                ("Computer_0",   0.85f, 0.79f),   // CRT monitor -- literally the television tube's face
                ("Computer_3",   1.15f, 0.79f),   // flatscreen monitor
                ("Computer_2",   0.61f, 0.44f),   // LAPTOP lid -- measured IN PLANE, so the hinge tilt does not
                                                  //  shorten it the way the bounding box did
            };
            foreach (var (nm, w, h) in props)
            {
                var body = ObjMesh.Load(dir + nm + ".obj");
                T.Check($"{nm}.obj loads", body != null);
                if (body == null) continue;

                var kind = TVDevice.KindFor(nm);
                var screen = TVDevice.SplitScreen(body, kind);
                T.Check($"{nm}: the screen split matches something", screen != null);
                if (screen == null) continue;

                // A screen is ONE QUAD. This is what separates a real hit from a plausible one: run the same CRT
                // predicate over Computer_1 (a tower) below and it matches 10 triangles spanning a 0.10 m slab -- a
                // count-only check would call that a screen.
                T.Check($"{nm}: it is a single flat quad ({Tris(screen)} tris)", Tris(screen) == 2);
                var (a, b, off) = FaceExtent(screen);
                T.Check($"{nm}: ...and genuinely planar ({off:0.####} m off-plane)", off < 1e-3f);
                T.Check($"{nm}: ...the right size ({a:0.00} x {b:0.00}, want {w:0.00} x {h:0.00})",
                    Mathf.Abs(a - w) < 0.02f && Mathf.Abs(b - h) < 0.02f);

                // THE GLASS COLOUR, sampled from the prop's own palette. The fallback is deliberately MAGENTA rather
                // than the production default: this exact check once would have passed on a totally broken texture
                // read, because the CRT's real fallback happens to be the right answer. A sentinel that cannot collide
                // with a plausible value is the only version of this check that carries information.
                var glass = TVDevice.SampleScreenTexel(nm, screen, Colors.Magenta);
                T.Check($"{nm}: the glass texel actually read ({glass.R8},{glass.G8},{glass.B8}) -- not the sentinel",
                    glass != Colors.Magenta);
                int want8 = kind == TVDevice.DeviceKind.FlatTv ? 39 : 53;
                T.Check($"{nm}: ...and it is the dark screen grey ({glass.R8}, want {want8})",
                    Mathf.Abs(glass.R8 - want8) <= 1 && glass.R8 == glass.G8 && glass.G8 == glass.B8);

                // ---- THE PLUG. Derived from the bounds, so it has to be checked against them: OUTSIDE the cabinet
                // (a cube inside the mesh is invisible and unclickable), on the BACK (not out through the picture),
                // and within arm's reach of the prop rather than off in the room.
                var bodyAabb = body.GetAabb();
                var winding = WindingNormal(screen);
                var normal = -winding;   // outward, the same negation Reproject applies -- see below for why it is one

                // 1. THE WINDING IS UNANIMOUS. ObjMesh.Load reverses face order on import, so the summed cross product
                //    points INTO the prop -- on every one of these, -Y local. That uniformity is what lets the outward
                //    facing be a plain negation instead of a heuristic, so it is asserted rather than assumed: a
                //    re-extraction that flips one prop's winding has to fail HERE, loudly, because the symptom
                //    otherwise is a picture rendered on the back of a lid.
                T.Check($"{nm}: the loaded winding points inward, -Y like every other screen prop ({winding})",
                    winding.Y < -0.9f);

                // 2. THE OLD RULE -- flip the winding whenever it pointed at the body's AABB centre -- agreed with that
                //    negation on the three props that were verified by eye, and disagreed on exactly the two that were
                //    not. Written as an executable comparison so the reason for the change cannot decay into a story
                //    that someone later "simplifies" away.
                bool oldRuleFlipped = winding.Dot(screen.GetAabb().GetCenter() - bodyAabb.GetCenter()) < 0f;
                if (nm is "Computer_3" or "Computer_2")
                    T.Check($"{nm}: ...and the old rule FAILED to flip it -- the bug: screen, spill and cone faced backwards",
                        !oldRuleFlipped);
                else
                    T.Check($"{nm}: ...and the old rule flipped it to the same answer, so this change is a no-op here",
                        oldRuleFlipped);
                // The AUTHORED up axis, which is +Z: these props are modelled lying flat in Unity's Z-up frame and the
                // placement basis stands them upright, so the prop-local "up" production computes is +Z, not +Y.
                // Passing Vector3.Up here instead is not a harmless approximation -- it is PARALLEL to the screen
                // normal on these props, so the height slide would run straight back through the cabinet. (That
                // degenerate case is asserted on its own below; it must never put the port inside the box.)
                var up = new Vector3(0f, 0f, 1f);
                var plug = TVDevice.PlugLocal(bodyAabb, normal, up);
                T.Check($"{nm}: the plug is OUTSIDE the cabinet", !bodyAabb.HasPoint(plug));
                T.Check($"{nm}: ...on the BACK, not through the screen ({(plug - bodyAabb.GetCenter()).Dot(normal):0.00} along the screen normal)",
                    (plug - bodyAabb.GetCenter()).Dot(normal) < 0f);
                // Two separate properties, because one distance conflates them and passes for the wrong reason. On a
                // 3.55 m wall television the quarter-height slide is a big LATERAL move, so straight-line distance from
                // the back face's midpoint is half a metre even when the port is sitting flush on the panel.
                //   1. clearance ALONG THE NORMAL: a few cm, i.e. resting on the surface rather than out in the room.
                float back = (bodyAabb.GetCenter() - normal * (bodyAabb.Size * 0.5f).Dot(normal.Abs())).Dot(normal);
                float clear = back - plug.Dot(normal);
                T.Check($"{nm}: ...resting on the back panel ({clear:0.000} m proud of it)", clear > 0f && clear < 0.15f);
                //   2. ...and ON the cabinet's footprint, not off past its edge -- a port floating a metre to the side
                //      passes check 1 alone. Measured in the SCREEN'S OWN PLANE, not by pushing the point back through
                //      the panel and asking the AABB whether it contains it: along a tilted normal the box's support
                //      point is a CORNER, so that construction lands a few mm outside on the laptop and would have
                //      reported a correctly-placed port as off the prop.
                Vector3 pax = (Mathf.Abs(normal.Z) < 0.9f ? Vector3.Back : Vector3.Right);
                pax = (pax - normal * pax.Dot(normal)).Normalized();
                Vector3 pay = normal.Cross(pax).Normalized();
                float aLo = float.MaxValue, aHi = float.MinValue, bLo = float.MaxValue, bHi = float.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    var e = bodyAabb.GetEndpoint(i);
                    aLo = Mathf.Min(aLo, e.Dot(pax)); aHi = Mathf.Max(aHi, e.Dot(pax));
                    bLo = Mathf.Min(bLo, e.Dot(pay)); bHi = Mathf.Max(bHi, e.Dot(pay));
                }
                T.Check($"{nm}: ...and within the cabinet's own footprint ({plug.Dot(pax):0.00} in [{aLo:0.00},{aHi:0.00}], {plug.Dot(pay):0.00} in [{bLo:0.00},{bHi:0.00}])",
                    plug.Dot(pax) >= aLo - 1e-3f && plug.Dot(pax) <= aHi + 1e-3f
                    && plug.Dot(pay) >= bLo - 1e-3f && plug.Dot(pay) <= bHi + 1e-3f);
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < 8; i++) { float d = bodyAabb.GetEndpoint(i).Dot(up); lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d); }
                float frac = (plug.Dot(up) - lo) / Mathf.Max(1e-5f, hi - lo);
                T.Check($"{nm}: ...at a quarter height ({frac:0.00}) rather than at the top or under the floor",
                    frac > 0.1f && frac < 0.45f);
            }

            // ---- THE TEETH on "a single flat quad". Computer_1 is a TOWER: it is not a screen prop, so it never gets
            // a device -- but the CRT predicate DOES match geometry on it. Asserted here so the size/planarity checks
            // above are known to be doing work rather than restating "the split returned something".
            var tower = ObjMesh.Load(dir + "Computer_1.obj");
            if (tower != null)
            {
                var wrong = TVDevice.SplitScreen(tower, TVDevice.DeviceKind.CrtMonitor);
                int wt = Tris(wrong);
                T.Check($"the CRT predicate DOES match a tower's case panels ({wt} tris) -- so 'matched > 0' proves nothing",
                    wt > 2);
                if (wrong != null)
                {
                    var (_, _, woff) = FaceExtent(wrong);
                    T.Check($"...and it is not planar ({woff:0.###} m off-plane), which is what the shape check rejects",
                        woff > 1e-3f);
                }
            }

            // ---- THE DEGENERATE UP. A screen whose normal IS the up axis (a set lying on its back) has no height
            // direction at all. The slide must be skipped rather than run along a near-zero vector -- the failure mode
            // is not a wrong height, it is a port dragged back through the cabinet and lost inside it, which looks
            // exactly like "power io was never added".
            var box = new Aabb(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1f, 1f, 1f));
            foreach (var (nrm, why) in new[]
            {
                (Vector3.Up, "up is PARALLEL to the normal (set lying face-up)"),
                (new Vector3(0f, 0.94f, 0.34f).Normalized(), "up is merely NOT PERPENDICULAR (a tilted wall set)"),
                (Vector3.Forward, "the ordinary case, for contrast"),
            })
            {
                var p = TVDevice.PlugLocal(box, nrm, Vector3.Up);
                T.Check($"plug stays outside the cabinet when {why}", !box.HasPoint(p));
                T.Check($"...and behind the screen ({(p - box.GetCenter()).Dot(nrm):0.00} along the normal)",
                    (p - box.GetCenter()).Dot(nrm) < 0f);
            }

            yield break;
        }

        /// <summary>The screen face's raw WINDING normal, exactly as summed from the loaded triangles -- deliberately
        /// NOT the outward facing, so the suite can assert the two separately.
        ///
        /// This helper used to re-sign itself away from the body's AABB centre, mirroring what Reproject did. Both were
        /// wrong on two of five props, and the test agreeing with the code is precisely why that survived a green run:
        /// it reproduced the rule instead of checking the result.</summary>
        internal static Vector3 WindingNormal(ArrayMesh screen)
        {
            var v = screen.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3 nrm = Vector3.Zero;
            for (int i = 0; i + 2 < v.Length; i += 3) nrm += (v[i + 1] - v[i]).Cross(v[i + 2] - v[i]);
            return nrm.LengthSquared() < 1e-9f ? Vector3.Down : nrm.Normalized();
        }
    }
}
