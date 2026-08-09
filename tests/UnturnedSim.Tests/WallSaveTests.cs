using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // L0 for the drawn-wall save format.
    //
    // Persistence is the one place where a bug costs the user work rather than a redraw, so these lean on
    // round-trips rather than on inspecting the text: a format that reads back what it wrote is correct
    // whatever it looks like, and a test that asserts the exact bytes just freezes today's spelling.
    [TestFixture]
    public class WallSaveTests
    {
        static List<WallPlan> Sample()
        {
            var a = new WallPlan { X = -6f, Y = 0f, Z = 2.5f, Yaw = -90f, Length = 12f, Height = 4.25f, Thickness = 0.7f, Material = 24 };
            a.Openings.Add(new WallOpening(1f, 0f, 2.5f, 3.75f, 999f, 0));
            a.Openings.Add(new WallOpening(5f, 1f, 3.31f, 2.75f, 999f, 1));
            var b = new WallPlan { X = 3f, Y = 4.75f, Z = -1f, Yaw = 15f, Length = 9f, Height = 9.5f, Thickness = 0.5f, Material = 6 };
            var c = new WallPlan { X = -7f, Y = 4.75f, Z = 1f, Yaw = 0f, Pitch = -90f, Kind = SurfaceKind.Roof,
                                   Length = 13f, Height = 10f, Thickness = 0.5f, Material = 24 };
            c.Openings.Add(new WallOpening(4f, 3f, 2f, 2.5f, 999f, 0));   // a stairwell is just an opening
            return new List<WallPlan> { a, b, c };
        }

        static void AssertSame(List<WallPlan> want, List<WallPlan> got)
        {
            Assert.That(got.Count, Is.EqualTo(want.Count), "wall count");
            for (int i = 0; i < want.Count; i++)
            {
                var w = want[i];
                var g = got[i];
                Assert.That(g.X, Is.EqualTo(w.X).Within(1e-3f), $"wall {i} x");
                Assert.That(g.Y, Is.EqualTo(w.Y).Within(1e-3f), $"wall {i} y");
                Assert.That(g.Z, Is.EqualTo(w.Z).Within(1e-3f), $"wall {i} z");
                Assert.That(g.Yaw, Is.EqualTo(w.Yaw).Within(1e-3f), $"wall {i} yaw");
                Assert.That(g.Length, Is.EqualTo(w.Length).Within(1e-3f), $"wall {i} length");
                Assert.That(g.Height, Is.EqualTo(w.Height).Within(1e-3f), $"wall {i} height");
                Assert.That(g.Pitch, Is.EqualTo(w.Pitch).Within(1e-3f), $"wall {i} pitch");
                Assert.That(g.Kind, Is.EqualTo(w.Kind), $"wall {i} kind");
                Assert.That(g.Thickness, Is.EqualTo(w.Thickness).Within(1e-3f), $"wall {i} thickness");
                Assert.That(g.Material, Is.EqualTo(w.Material), $"wall {i} material");
                Assert.That(g.Openings.Count, Is.EqualTo(w.Openings.Count), $"wall {i} opening count");
                for (int j = 0; j < w.Openings.Count; j++)
                {
                    Assert.That(g.Openings[j].U, Is.EqualTo(w.Openings[j].U).Within(1e-3f), $"wall {i} opening {j} u");
                    Assert.That(g.Openings[j].V, Is.EqualTo(w.Openings[j].V).Within(1e-3f), $"wall {i} opening {j} v");
                    Assert.That(g.Openings[j].Width, Is.EqualTo(w.Openings[j].Width).Within(1e-3f), $"wall {i} opening {j} w");
                    Assert.That(g.Openings[j].Height, Is.EqualTo(w.Openings[j].Height).Within(1e-3f), $"wall {i} opening {j} h");
                    Assert.That(g.Openings[j].Archetype, Is.EqualTo(w.Openings[j].Archetype), $"wall {i} opening {j} archetype");
                }
            }
        }

        static List<string> Lines(string s) => s.Split('\n').ToList();

        // BREAK IT: stop writing the `open` lines, or attach them to the wrong wall -> the second wall gains
        // openings it never had and the first loses them.
        [Test]
        public void WallsAndTheirOpeningsSurviveARoundTrip()
        {
            var want = Sample();
            AssertSame(want, WallSave.Read(Lines(WallSave.Write(want))));
        }

        // BREAK IT: format with the current culture instead of the invariant one. Under a comma-decimal locale
        // "0,7" is written and read back as two tokens, so the wall silently loads with the wrong field count
        // and vanishes -- on that machine only, which is the worst kind of bug to be handed.
        [Test]
        public void ALocaleWithCommaDecimalsDoesNotCorruptTheFile()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var want = Sample();
                string text = WallSave.Write(want);
                Assert.That(text, Does.Not.Contain("0,7"), "floats must be written invariant");
                AssertSame(want, WallSave.Read(Lines(text)));
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }

        // BREAK IT: throw on an unrecognised token instead of skipping -> a file written by a newer editor
        // loses every building rather than one field.
        [Test]
        public void UnknownLinesAndJunkDegradeGracefully()
        {
            var text = string.Join("\n",
                WallSave.Header,
                "# a comment",
                "wall 0 0 0 0 12 0.7 3",
                "  open 1 0 2.5 3.75 999 0",
                "  flange 3 3 3",                       // a field from some later version
                "wall bogus bogus 0 0 12 0.7 0",        // malformed: drops itself
                "  open 4 0 2 2 999 0",                 // orphaned by the bad wall above
                "wall 5 0 5 90 9 0.5 1",
                "",
                "   ");
            var got = WallSave.Read(Lines(text));
            Assert.That(got.Count, Is.EqualTo(2), "the malformed wall drops itself, the good ones survive");
            Assert.That(got[0].Openings.Count, Is.EqualTo(1), "the unknown line did not eat the opening");
            Assert.That(got[1].Openings.Count, Is.EqualTo(0), "an opening orphaned by a bad wall is not adopted by the next one");
            Assert.That(got[1].Material, Is.EqualTo(1));

            Assert.That(WallSave.Read(null).Count, Is.EqualTo(0));
            Assert.That(WallSave.Read(new List<string> { null, "", "wall" }).Count, Is.EqualTo(0));
        }

        // A field added after the format shipped must not invalidate what is already on disk.
        // BREAK IT: require 9 fields on a wall line -> every pre-height file loses all its walls.
        [Test]
        public void AFileWrittenBeforeHeightExistedStillLoads()
        {
            var got = WallSave.Read(Lines(string.Join("\n",
                WallSave.Header,
                "wall 0 0 0 0 12 0.7 3",                 // seven fields: the original v1 wall line
                "  open 1 0 2.5 3.75 999 0")));
            Assert.That(got.Count, Is.EqualTo(1), "an old wall line still loads");
            Assert.That(got[0].Height, Is.EqualTo(WallOpenings.DoorHeight).Within(1e-4f),
                        "and defaults to one storey, which is what those walls were");
            Assert.That(got[0].Pitch, Is.EqualTo(0f).Within(1e-4f), "upright, which is what they all were");
            Assert.That(got[0].Kind, Is.EqualTo(SurfaceKind.Wall), "and a wall, since floors did not exist yet");
            Assert.That(got[0].Openings.Count, Is.EqualTo(1));
        }

        // Overlapping openings are LEGAL -- the partition removes their union once, which WallOpeningsTests
        // pins as load-bearing -- and the editor can produce them, because Clamp's two-sided fallback parks an
        // opening overlapping a neighbour and DragEdge never constrains size against siblings at all.
        //
        // BREAK IT: clamp each loaded opening against the ones already loaded. That is what it did: openings
        // at U=1 and U=2 came back with the second at U=4. Save and reopen gave a different building than the
        // one you baked, silently, and no disjoint-opening round trip could see it.
        [Test]
        public void OverlappingOpeningsAreNotShovedApartOnLoad()
        {
            var w = new WallPlan { X = 0f, Y = 0f, Z = 0f, Yaw = 0f, Length = 12f, Height = 4.25f, Thickness = 0.7f };
            w.Openings.Add(new WallOpening(1f, 1f, 2f, 2f));
            w.Openings.Add(new WallOpening(2f, 1f, 2f, 2f));      // deliberately overlapping the first
            var got = WallSave.Read(Lines(WallSave.Write(new List<WallPlan> { w })));

            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Openings.Count, Is.EqualTo(2));
            Assert.That(got[0].Openings[0].U, Is.EqualTo(1f).Within(1e-3f), "the first stays put");
            Assert.That(got[0].Openings[1].U, Is.EqualTo(2f).Within(1e-3f), "and so does the second -- a loader returns what was written");
        }

        // BREAK IT: snap length to the lattice anywhere on the LOAD path. An off-lattice wall -- imported from
        // a retail mesh, or hand-edited -- gets rounded by up to half a lattice step, so Load stops being the
        // inverse of Save and imported walls mis-meet at their corners.
        [Test]
        public void AnOffLatticeLengthSurvivesTheRoundTrip()
        {
            var w = new WallPlan { X = 0f, Y = 0f, Z = 0f, Yaw = 0f, Length = 7.43f, Height = 4.25f, Thickness = 0.7f };
            var got = WallSave.Read(Lines(WallSave.Write(new List<WallPlan> { w })));
            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Length, Is.EqualTo(7.43f).Within(1e-3f), "not rounded to a multiple of the lattice step");
        }

        // BREAK IT: trust the file. A hand-edited opening wider than its wall must give a silly hole, never a
        // wall that fails to load -- the generator already never throws, and the loader must not either.
        [Test]
        public void OversizedOpeningsAreClampedRatherThanRejected()
        {
            var got = WallSave.Read(Lines(string.Join("\n",
                WallSave.Header,
                "wall 0 0 0 0 6 0.7 0",
                "  open -50 -50 500 500 999 0")));
            Assert.That(got.Count, Is.EqualTo(1), "the wall still loads");
            Assert.That(got[0].Openings.Count, Is.EqualTo(1));
            var o = got[0].Openings[0];
            Assert.That(o.U, Is.GreaterThanOrEqualTo(-1e-3f));
            Assert.That(o.Width, Is.LessThanOrEqualTo(6f + 1e-3f), "clamped into the wall it belongs to");
        }

        // An empty layout must produce a real file, not an empty string: saving after deleting your last wall
        // has to overwrite the old one, or the building you deleted comes back next session.
        // BREAK IT: return early on an empty list -> the header goes missing and the write is a no-op.
        [Test]
        public void AnEmptyLayoutStillWritesAHeader()
        {
            string text = WallSave.Write(new List<WallPlan>());
            Assert.That(text, Does.StartWith(WallSave.Header));
            Assert.That(WallSave.Read(Lines(text)).Count, Is.EqualTo(0));
            Assert.That(WallSave.Write(null), Does.StartWith(WallSave.Header));
        }
    }
}
