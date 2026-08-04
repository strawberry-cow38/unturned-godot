using Godot;
using System.Collections.Generic;

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

        static (float A, float B, float Thin) FaceExtent(ArrayMesh m)
        {
            var v = m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in v)
            {
                lo = new Vector3(Mathf.Min(lo.X, p.X), Mathf.Min(lo.Y, p.Y), Mathf.Min(lo.Z, p.Z));
                hi = new Vector3(Mathf.Max(hi.X, p.X), Mathf.Max(hi.Y, p.Y), Mathf.Max(hi.Z, p.Z));
            }
            var s = hi - lo;
            // Sorted: a screen is a PLANE, so one axis is ~0 and the other two are its width and height -- without
            // caring which axis of the authored frame each landed on (they differ per prop).
            float[] e = { s.X, s.Y, s.Z };
            System.Array.Sort(e);
            return (e[2], e[1], e[0]);
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
            foreach (var prop in new[] { "Television_0", "Television_1", "Computer_0", "Computer_3" })
                T.Check($"{prop} is a screen prop", TVDevice.IsDeviceProp(prop));
            foreach (var prop in new[] { "Computer_1", "Computer_2", "Computer_4", "Chair_Metal_0" })
                T.Check($"{prop} is NOT ({(prop.StartsWith("Computer") ? "a tower/keyboard, no screen" : "not a screen at all")})",
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
            // ...and the televisions did NOT lose anything on the way. Half of a "dupe X minus Y" change going wrong
            // looks like the SOURCE losing Y, and nobody re-checks the thing that already worked.
            T.Check("the CRT television kept its card, its roll and its tone",
                TVDevice.HasPattern(TVDevice.DeviceKind.CrtTv) && TVDevice.HasDesync(TVDevice.DeviceKind.CrtTv)
                && TVDevice.HasTone(TVDevice.DeviceKind.CrtTv) && TVDevice.IsTube(TVDevice.DeviceKind.CrtTv));
            T.Check("the flatscreen television kept its card and tone and is still a panel",
                TVDevice.HasPattern(TVDevice.DeviceKind.FlatTv) && TVDevice.HasTone(TVDevice.DeviceKind.FlatTv)
                && !TVDevice.IsTube(TVDevice.DeviceKind.FlatTv) && !TVDevice.HasDesync(TVDevice.DeviceKind.FlatTv));

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
                var (a, b, thin) = FaceExtent(screen);
                T.Check($"{nm}: ...and genuinely planar (thickness {thin:0.###} m)", thin < 1e-3f);
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
                var normal = ScreenNormal(body, screen);
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
                //   2. ...and ON the cabinet's footprint, not off past its edge: nudging it back through the panel has
                //      to land inside the body. A port floating a metre to the side would pass check 1 alone.
                T.Check($"{nm}: ...and within the cabinet's own footprint",
                    bodyAabb.Grow(1e-3f).HasPoint(plug + normal * (clear + 0.02f)));
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
                    var (_, _, wthin) = FaceExtent(wrong);
                    T.Check($"...and it is not planar ({wthin:0.###} m thick), which is what the shape check rejects",
                        wthin > 1e-3f);
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

        /// <summary>The screen's outward normal, derived the way Reproject does: summed triangle cross products, signed
        /// away from the body centre. Reproduced rather than exposed because what is under test here is the PLUG's
        /// placement given a normal, not the normal itself (TVScreenTests owns that).</summary>
        static Vector3 ScreenNormal(ArrayMesh body, ArrayMesh screen)
        {
            var v = screen.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3 nrm = Vector3.Zero, c = Vector3.Zero;
            for (int i = 0; i + 2 < v.Length; i += 3) nrm += (v[i + 1] - v[i]).Cross(v[i + 2] - v[i]);
            foreach (var p in v) c += p;
            c /= Mathf.Max(1, v.Length);
            if (nrm.LengthSquared() < 1e-9f) return Vector3.Up;
            nrm = nrm.Normalized();
            return nrm.Dot(c - body.GetAabb().GetCenter()) < 0f ? -nrm : nrm;
        }
    }
}
