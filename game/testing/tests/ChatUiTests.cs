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

    // Profile pictures beside the speaker (strawberry 2026-09-17). Three cases, because the interesting
    // ones are the absences: a picture must appear for a speaker who HAS one, must NOT appear on a server
    // line (which has no speaker at all), and a speaker WITHOUT one must still render a normal row rather
    // than vanishing or throwing -- a missing avatar is the common case, not an error path.
    //
    // ⚠ Asserts on the ROW, via DebugAvatarsShown, not on the lookup being called. A test that counted
    // AvatarFor invocations would pass with the texture dropped on the floor between decode and display,
    // which is exactly the bug worth catching here.
    public sealed class ChatAvatarTests : GameTest
    {
        public override string Name => "chat.avatars";
        public override int Tier => 1;

        static byte[] Avatar128()
        {
            var img = Image.CreateEmpty(SDG.Unturned.ProfileRules.AvatarPixels,
                                        SDG.Unturned.ProfileRules.AvatarPixels, false, Image.Format.Rgba8);
            img.Fill(new Color(0.2f, 0.7f, 0.9f));
            return img.SavePngToBuffer();
        }

        public override IEnumerable<Step> Run()
        {
            var ui = new ChatUI();
            World.AddChild(ui);
            yield return Ticks(1);

            var png = Avatar128();
            T.Check($"the fixture really is a decodable {SDG.Unturned.ProfileRules.AvatarPixels}px avatar",
                    PlayerProfile.DecodeAvatar(png) != null);

            ui.AvatarFor = id => id == 5 ? png : null;   // only Alice has a picture

            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Global, SpeakerId = 5, Name = "Alice", Text = "hello" });
            T.Check($"a speaker with a picture draws it ({ui.DebugAvatarsShown} shown)", ui.DebugAvatarsShown == 1);

            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Server, SpeakerId = 0, Name = "", Text = "restarting" });
            T.Check($"a server line adds no picture (still {ui.DebugAvatarsShown})", ui.DebugAvatarsShown == 1);

            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Global, SpeakerId = 7, Name = "Bob", Text = "hi" });
            T.Check($"a speaker with NO picture still gets a row ({ui.DebugRowCount} rows)", ui.DebugRowCount == 3);
            T.Check($"...and adds no picture ({ui.DebugAvatarsShown} shown)", ui.DebugAvatarsShown == 1);

            // The hook being absent entirely is singleplayer, and must render plain rows rather than fail.
            var solo = new ChatUI();
            World.AddChild(solo);
            yield return Ticks(1);
            solo.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Global, SpeakerId = 5, Name = "Alice", Text = "hello" });
            T.Check($"no AvatarFor hook at all: a row still renders ({solo.DebugRowCount})", solo.DebugRowCount == 1);
            T.Check("...with no picture", solo.DebugAvatarsShown == 0);

            ui.QueueFree();
            solo.QueueFree();
            yield break;
        }
    }

    // "should only appear if theres a recent message in chat or if we have the chat input box focused,
    // otherwise fade after no new chat msg" (strawberry 2026-09-17).
    //
    // ⚠ The third leg costs ~14 sim seconds and is the only one that matters. "Visible when fresh" and
    // "visible while typing" both pass on a panel that is ALWAYS visible -- which is exactly the bug being
    // guarded against -- so a suite without the slow leg would be green on the broken behaviour.
    public sealed class ChatFadeTests : GameTest
    {
        public override string Name => "chat.fades_when_idle";
        public override int Tier => 1;

        static ChatUI Panel(Node world)
        {
            var ui = new ChatUI { Send = _ => true };
            world.AddChild(ui);
            return ui;
        }

        public override IEnumerable<Step> Run()
        {
            var ui = Panel(World);
            yield return Ticks(2);
            T.Check("nothing said yet: no panel", !ui.DebugPanelVisible);

            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Global, SpeakerId = 5, Name = "Alice", Text = "hello" });
            yield return Ticks(2);
            T.Check($"a fresh line shows it at full opacity (alpha {ui.DebugPanelAlpha:0.00})",
                    ui.DebugPanelVisible && ui.DebugPanelAlpha > 0.99f);

            // TYPING PINS IT. Opened well after the line would otherwise have started fading.
            ui.Open();
            T.Check("opening the input shows it", ui.DebugPanelVisible && ui.IsTyping);
            yield return Ticks(14 * Engine.PhysicsTicksPerSecond);   // burn past the fade window while typing
            T.Check($"still fully visible while typing, {14}s after the last message (alpha {ui.DebugPanelAlpha:0.00})",
                    ui.DebugPanelVisible && ui.DebugPanelAlpha > 0.99f);

            // ...and once you stop typing, the same old line goes out.
            ui.Close();
            yield return Until(() => !ui.DebugPanelVisible, maxSimSeconds: 6);
            T.Check("closing the box lets the stale line fade away", !ui.DebugPanelVisible);

            // A new line brings it back from faded rather than leaving it half-gone.
            ui.Receive(new ChatMessageEvent { Channel = (byte)ChatChannel.Global, SpeakerId = 5, Name = "Alice", Text = "still here" });
            yield return Ticks(2);
            T.Check($"a new line restores it to full (alpha {ui.DebugPanelAlpha:0.00})",
                    ui.DebugPanelVisible && ui.DebugPanelAlpha > 0.99f);

            ui.QueueFree();
            yield break;
        }
    }
}
