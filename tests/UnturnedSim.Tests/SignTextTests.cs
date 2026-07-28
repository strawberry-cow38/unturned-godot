using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 for sign text. This is the only player-authored content other players are shown verbatim,
    // so the rules matter more than the feature's size suggests: the string arrives over the wire
    // from a peer that may not be running our client, and it persists in the world afterwards.
    //
    // Control characters are written as \u escapes throughout and asserted as a PROPERTY ("nothing
    // controlling survives") rather than by searching for specific literal bytes. Raw control bytes
    // in a source file are invisible in a diff and survive an editor round-trip only by luck -- the
    // first version of this file embedded them literally and failed for that reason rather than for
    // any real defect in the code under test.
    [TestFixture]
    public class SignTextTests
    {
        const string Bell = "\u0007";   // BEL
        const string Esc = "\u001B";    // ESC, the head of an ANSI colour run

        [Test]
        public void Ordinary_Text_Survives_Untouched()
        {
            Assert.That(SignText.Sanitize("Trader - knock twice"), Is.EqualTo("Trader - knock twice"));
            Assert.That(SignText.IsClean("Trader - knock twice"), Is.True);
        }

        [Test]
        public void Null_And_Empty_Are_Empty_Not_A_Crash()
        {
            Assert.That(SignText.Sanitize(null), Is.EqualTo(string.Empty));
            Assert.That(SignText.Sanitize(""), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Text_Is_Capped_So_One_Client_Cannot_Cost_Everyone_Bandwidth()
        {
            var huge = new string('x', 5000);
            Assert.That(SignText.Sanitize(huge).Length, Is.EqualTo(SignText.MaxChars));
        }

        [Test]
        public void Control_Characters_Are_Dropped()
        {
            // Nothing legitimate types these, and handing them to a label is how a renderer gets
            // surprised. Tab included: it would let one client shift text on everyone else's screen.
            var dirty = "ok" + Bell + " bad\ttab" + Esc + "[31m";
            var clean = SignText.Sanitize(dirty);

            foreach (char c in clean)
                Assert.That(char.IsControl(c) && c != '\n', Is.False,
                    $"a control character (U+{(int)c:X4}) survived sanitising");

            Assert.That(clean, Does.Contain("ok"), "the legitimate text must survive");
            Assert.That(clean, Does.Contain("bad"), "...all of it, not just the head");
            Assert.That(SignText.IsClean(dirty), Is.False, "and the raw input is reported as unclean");
        }

        [Test]
        public void Newlines_Are_Kept_But_Runs_Collapse()
        {
            Assert.That(SignText.Sanitize("a\nb"), Is.EqualTo("a\nb"), "a real line break is legitimate");
            Assert.That(SignText.Sanitize("a\n\n\n\nb"), Is.EqualTo("a\nb"),
                "blank-line spam must not let a sign push its own text off screen");
        }

        [Test]
        public void The_Line_Count_Is_Capped()
        {
            var many = "1\n2\n3\n4\n5\n6\n7\n8";
            var clean = SignText.Sanitize(many);
            Assert.That(clean.Split('\n').Length, Is.LessThanOrEqualTo(SignText.MaxLines));
        }

        [Test]
        public void Trailing_Whitespace_And_Blank_Lines_Are_Trimmed()
        {
            // Invisible padding shifts a sign off centre for everyone looking at it.
            Assert.That(SignText.Sanitize("shop   "), Is.EqualTo("shop"));
            Assert.That(SignText.Sanitize("shop\n   \n"), Is.EqualTo("shop"));
            Assert.That(SignText.Sanitize("   \n   "), Is.EqualTo(string.Empty),
                "a sign of pure whitespace stores as empty, not as invisible text");
        }

        [Test]
        public void Sanitize_Is_Idempotent()
        {
            // Text is sanitised where it is STORED, so the stored value must already be a fixed point
            // -- otherwise a value could drift each time it is round-tripped through the wire.
            var cases = new[] { "plain", "a\n\n\nb", "trail   ", "ctl" + Bell + "x", new string('y', 500), "  \n  " };
            foreach (var s in cases)
            {
                var once = SignText.Sanitize(s);
                Assert.That(SignText.Sanitize(once), Is.EqualTo(once), $"not a fixed point for: {s}");
                Assert.That(SignText.IsClean(once), Is.True);
            }
        }
    }
}
