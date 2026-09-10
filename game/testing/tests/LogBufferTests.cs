using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE LOG STILL EXISTS WHEN IT IS NOT BEING PRINTED (strawberry 2026-09-10: "writing to the windows console is
    // really slow and causes a stutter when we do a lot of prints").
    //
    // The whole point of the change is that a line can stop going to stdout and still be there to read. So the one
    // thing worth asserting is exactly that separation: buffering must not be a side effect of mirroring. If those
    // two ever get wired together, turning off the slow path silently throws the log away, and the symptom is
    // "the console is empty" long after the commit that caused it.
    public sealed class LogBufferTests : GameTest
    {
        public override string Name => "log.buffer_independent_of_stdout";

        static bool Has(string needle)
        {
            foreach (var l in Log.Lines) if (l.Contains(needle)) return true;
            return false;
        }

        public override IEnumerable<Step> Run()
        {
            var saved = Log.Mirror;
            Log.ClearForTest();

            // ---- mirroring OFF: the line must still be readable in-game.
            Log.SetMirrorForTest(false);
            Log.Print("catboy-marker-quiet");
            T.Check("a line printed with stdout OFF is still buffered", Has("catboy-marker-quiet"));

            // ---- CONTROL: with mirroring ON it also buffers. Without this the check above passes on a Log that
            // buffers nothing and a Has() that is broken -- both would read as green.
            Log.SetMirrorForTest(true);
            Log.Print("catboy-marker-loud");
            T.Check("...and so is one printed with stdout ON", Has("catboy-marker-loud"));
            T.Check("...the quiet one did not disappear when the mode changed", Has("catboy-marker-quiet"));

            // ---- errors are buffered too, and marked.
            Log.SetMirrorForTest(false);
            ulong before = Log.Sequence;
            Log.Err("catboy-marker-bad");
            T.Check("an error reaches the buffer", Has("catboy-marker-bad"));
            T.Check("...tagged so it reads as an error in the scrollback", Has("[err] catboy-marker-bad"));
            T.Check($"...and advances the sequence so a viewer repaints ({before} -> {Log.Sequence})", Log.Sequence > before);

            // ---- the ring is bounded: a long session must not grow without limit.
            Log.ClearForTest();
            for (int i = 0; i < Log.Capacity + 250; i++) Log.Print($"fill-{i}");
            T.Check($"the ring stays at its cap ({Log.Lines.Length} <= {Log.Capacity})", Log.Lines.Length <= Log.Capacity);
            T.Check("...keeping the NEWEST lines", Has($"fill-{Log.Capacity + 249}"));
            T.Check("...and dropping the oldest", !Has("fill-0"));

            Log.ClearForTest();
            Log.SetMirrorForTest(saved);   // leave the run as we found it -- other tests print
            yield break;
        }
    }
}
