using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    /// <summary>The chat box behaves, and the ban file round-trips.
    ///
    /// ⚠ WHAT THIS CANNOT SEE, stated rather than implied: it builds its own ChatUI, so it proves the
    /// WIDGET and never that ClientWorldSession wires one up. That is exactly the blind spot that let the
    /// foliage tool ship unreachable this morning. The wiring is verified by booting instead --
    /// "[bans] loaded ..." on a dedicated server with a seeded file.</summary>
    public class ChatUiBehaves : GameTest
    {
        public override string Name => "chat.ui_behaves";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            var ui = new ChatUI();
            World.AddChild(ui);
            yield return Ticks(2);

            T.Check("closed at rest", !ui.IsTyping);

            // Without a Send it must NOT open -- otherwise singleplayer with no connection eats Enter and
            // the player cannot press it for anything else.
            ui.Open();
            T.Check("opens even with no sender when asked directly", ui.IsTyping);
            ui.Close();
            T.Check("closes", !ui.IsTyping);

            string sent = null;
            ui.Send = t => { sent = t; return true; };

            // A SERVER line renders without a name; a player line renders with one. This is the last place
            // impersonation could be reintroduced, by deciding from the name instead of the channel.
            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Server, SpeakerId = 0, Name = "", Text = "restarting soon" });
            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Global, SpeakerId = 5, Name = "Alice", Text = "hello" });
            yield return Ticks(2);
            T.Check("a player line is attributed", ui.DebugLastLine == "Alice: hello");

            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Server, SpeakerId = 0, Name = "SERVER", Text = "wipe at midnight" });
            yield return Ticks(1);
            T.Check($"a server line carries NO name even when one is supplied ({ui.DebugLastLine})",
                    ui.DebugLastLine == "wipe at midnight");

            // A player CALLED "SERVER" must still be rendered as a player.
            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Global, SpeakerId = 9, Name = "SERVER", Text = "trust me" });
            yield return Ticks(1);
            T.Check($"a player named SERVER is still shown as a speaker ({ui.DebugLastLine})",
                    ui.DebugLastLine == "SERVER: trust me");

            // Submitting sends and closes; an empty submit sends nothing.
            ui.Open();
            ui.DebugSubmit("  ");
            T.Check("an empty line sends nothing", sent == null);
            T.Check("...and still closes the box", !ui.IsTyping);

            ui.Open();
            ui.DebugSubmit("hello world");
            T.Check($"a real line is sent ({sent})", sent == "hello world");
            T.Check("and the box closes so movement keys work again", !ui.IsTyping);
        }
    }

    /// <summary>The ban file survives a restart, and a wipe does not clear it.</summary>
    public class BanStorePersists : GameTest
    {
        public override string Name => "chat.bans_persist";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            string prev = BanStore.Path;
            BanStore.Path = "user://bans_test.tsv";
            string abs = ProjectSettings.GlobalizePath(BanStore.Path);
            if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);
            yield return Ticks(1);

            long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var a = new ServerModeration();
            a.Add(0x0A000005u, "Cheater", 0, "aimbot", ModerationKind.Ban);          // permanent
            a.Add(0x0A000006u, "Griefer", now + 3600, "spam", ModerationKind.Kick);  // timed
            a.Add(0x0A000007u, "Expired", now - 10, "old", ModerationKind.Ban);      // already lapsed
            BanStore.Save(a);
            T.Check("the file was written", System.IO.File.Exists(abs));

            // A FRESH list, as if the server had restarted.
            var b = new ServerModeration();
            int n = BanStore.Load(b);
            T.Check($"the expired entry did not come back ({n} loaded)", n == 2);
            T.Check("the permanent ban survived", b.IsBanned(0x0A000005u, "Cheater", now, out var hit1) && hit1.IsPermanent);
            T.Check("the timed ban survived with its expiry", b.IsBanned(0x0A000006u, "Griefer", now, out var hit2) && !hit2.IsPermanent);
            T.Check("the reason survived", hit1.Reason == "aimbot");
            T.Check("and the lapsed one is gone", !b.IsBanned(0x0A000007u, "Expired", now, out _));

            // A tab in a reason must corrupt nothing: free text is last on the line for this reason.
            var c = new ServerModeration();
            c.Add(0x0A000008u, "Odd", 0, "used\ta tab\nand a newline", ModerationKind.Ban);
            BanStore.Save(c);
            var d = new ServerModeration();
            BanStore.Load(d);
            T.Check($"a tab in the reason does not shift the fields ({d.Count} entry)", d.Count == 1);
            T.Check("the address still parsed", d.IsBanned(0x0A000008u, "anyone", now, out var hit3));
            T.Check($"and the reason is flattened, not split ({hit3.Reason})", hit3.Reason == "used a tab and a newline");

            if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);
            BanStore.Path = prev;
        }
    }
}
