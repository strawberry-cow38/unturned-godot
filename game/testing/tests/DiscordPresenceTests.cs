using System.Collections.Generic;
using System.Text.Json;

namespace UnturnedGodot.Testing
{
    // THE FRAME JSON IS THE ENTIRE CONTRACT, and it is the one part of the presence that can be wrong with
    // nothing to notice. Discord's IPC answers a malformed SET_ACTIVITY by ignoring it -- no error, no reply
    // the game reads, just a panel that never appears. On a box with no Discord installed (every CI box, and
    // the build box) even the pipe never opens, so "it ran and threw nothing" is the strongest thing an
    // integration check could ever say here, and it would say it about a frame full of garbage too.
    //
    // So this parses what the game would actually send. It is hand-built with a StringBuilder rather than
    // serialised from a type -- deliberately, to keep a JSON dependency out of a hot path -- which is exactly
    // the construction that produces a trailing comma or an unbalanced brace under a field that is sometimes
    // omitted. Three of these fields ARE sometimes omitted.
    public sealed class DiscordPresenceFrameTests : GameTest
    {
        public override string Name => "discord.frame_shape";

        public override IEnumerable<Step> Run()
        {
            // ---- the multiplayer shape: every optional field present ------------------------------------
            string mp = DiscordPresence.FrameForTest("VoX Official — PEI", "PEI — Survival", 3, 24, 1758000000L);
            JsonDocument doc = null;
            bool parsed = true;
            try { doc = JsonDocument.Parse(mp); } catch { parsed = false; }
            T.Check("a full frame is valid JSON at all", parsed);
            if (!parsed) yield break;

            var root = doc.RootElement;
            T.Check("cmd is SET_ACTIVITY", root.GetProperty("cmd").GetString() == "SET_ACTIVITY");
            T.Check("carries a nonce (Discord drops a frame without one)", root.GetProperty("nonce").GetString().Length > 0);

            var args = root.GetProperty("args");
            T.Check("args.pid is this process", args.GetProperty("pid").GetInt32() == System.Environment.ProcessId);

            var act = args.GetProperty("activity");
            T.Check("details = the server name", act.GetProperty("details").GetString() == "VoX Official — PEI");
            T.Check("state = map and gamemode", act.GetProperty("state").GetString() == "PEI — Survival");
            T.Check("timestamps.start is the session start", act.GetProperty("timestamps").GetProperty("start").GetInt64() == 1758000000L);

            // party drives Discord's "(3 of 24)". An array of the wrong length renders nothing at all.
            var party = act.GetProperty("party").GetProperty("size");
            T.Check("party.size is a 2-element [cur,max]", party.GetArrayLength() == 2);
            T.Check("party.size = [3,24]", party[0].GetInt32() == 3 && party[1].GetInt32() == 24);
            T.Check("assets.large_image is set", act.GetProperty("assets").GetProperty("large_image").GetString() == "logo");

            // ---- singleplayer: party and the empty second line must be OMITTED, not sent empty ----------
            // The omissions are the risk. Each one is a branch that has to decide whether it still owes a
            // comma, and a frame that parses with party present-but-zero renders "(0 of 0)" in the panel.
            string sp = DiscordPresence.FrameForTest("Singleplayer", "PEI — day 12", 0, 0, 1758000000L);
            bool spParsed = true;
            JsonDocument spDoc = null;
            try { spDoc = JsonDocument.Parse(sp); } catch { spParsed = false; }
            T.Check("a frame with no party is still valid JSON", spParsed);
            if (spParsed)
            {
                var spAct = spDoc.RootElement.GetProperty("args").GetProperty("activity");
                T.Check("party is absent when there is no party", !spAct.TryGetProperty("party", out _));
                T.Check("details survives alongside the omission", spAct.GetProperty("details").GetString() == "Singleplayer");
            }

            // ---- the menu: no state, no party, no timestamp. The most-omitted frame there is. -----------
            string menu = DiscordPresence.FrameForTest("In the main menu", "", 0, 0, 0);
            bool menuParsed = true;
            JsonDocument menuDoc = null;
            try { menuDoc = JsonDocument.Parse(menu); } catch { menuParsed = false; }
            T.Check("the emptiest frame is valid JSON", menuParsed);
            if (menuParsed)
            {
                var mAct = menuDoc.RootElement.GetProperty("args").GetProperty("activity");
                T.Check("empty state is omitted rather than sent as \"\"", !mAct.TryGetProperty("state", out _));
                T.Check("timestamps omitted when there is no start", !mAct.TryGetProperty("timestamps", out _));
                T.Check("assets still present (the panel needs an icon)", mAct.TryGetProperty("assets", out _));
            }

            // ---- a hostile server name ------------------------------------------------------------------
            // ⚠ THE SERVER NAME COMES OFF THE WIRE FROM A STRANGER and lands in a UI panel. A quote or a
            // newline in it must not be able to break out of the string and forge sibling fields.
            string nasty = DiscordPresence.FrameForTest("evil\" , \"details\":\"pwned", "a\nb\tc", 0, 0, 0);
            bool nastyParsed = true;
            JsonDocument nastyDoc = null;
            try { nastyDoc = JsonDocument.Parse(nasty); } catch { nastyParsed = false; }
            T.Check("a name full of quotes still produces valid JSON", nastyParsed);
            if (nastyParsed)
            {
                var nAct = nastyDoc.RootElement.GetProperty("args").GetProperty("activity");
                // The whole payload must land in ONE field. If escaping failed, "details" would read "pwned".
                T.Check("an injected field does not escape its string",
                        nAct.GetProperty("details").GetString() == "evil\" , \"details\":\"pwned");
            }
        }
    }

    // The throttle is the other half that cannot be observed without Discord: Clean() is what stops a server
    // name from carrying control characters into the panel, and it is pure.
    public sealed class DiscordPresenceCleanTests : GameTest
    {
        public override string Name => "discord.name_clean";

        public override IEnumerable<Step> Run()
        {
            // ⚠ Clean() is called by SetMenu/SetSingleplayer/SetMultiplayer BEFORE the Activity is built --
            // Frame() only escapes. Driving this through FrameForTest would assert a behaviour Frame does not
            // have and report Frame as broken when it is doing exactly its job.
            T.Check("control characters are stripped, not escaped through",
                    DiscordPresence.CleanForTest("line\r\nbreak") == "linebreak");
            T.Check("ordinary text is untouched", DiscordPresence.CleanForTest("VoX Official — PEI") == "VoX Official — PEI");
            T.Check("surrounding whitespace goes", DiscordPresence.CleanForTest("  spaced  ") == "spaced");
            T.Check("null is a string, not a crash", DiscordPresence.CleanForTest(null) == "");

            // Discord truncates long fields itself, but a 4KB server name would be 4KB on the pipe on every
            // update. Clamped at the source instead.
            T.Check("an absurd name is clamped before it reaches the pipe",
                    DiscordPresence.CleanForTest(new string('x', 400)).Length <= 120);
            yield break;
        }
    }
}
