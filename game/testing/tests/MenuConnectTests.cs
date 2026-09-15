using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The menu had NO L1 coverage at all before this file (no menu.* or connect.* test existed), which is
    // part of why Direct Connect could ship collecting a password it never sent and handing an unreachable
    // address to a full map load that then said nothing.
    //
    // These cover the parts that are honest to assert headlessly: the pure decisions. The panels themselves
    // still are not verified here -- a headless run has no one looking at it, and claiming otherwise would be
    // the same mistake as the silent connect.
    public class MenuAddressFormatting : GameTest
    {
        public override string Name => "menu.address_hides_default_port";
        public override IEnumerable<Step> Run()
        {
            // ":47872" on every row is noise. A port shown therefore MEANS something -- that this one is not
            // the default -- which is the only thing that earns the width (strawberry 2026-09-15).
            T.Check("default port is hidden",
                    MainMenu.AddressText("claw.bitvox.me", MainMenu.DefaultServerPort) == "claw.bitvox.me");
            T.Check("a non-default port is SHOWN, because then it is information",
                    MainMenu.AddressText("claw.bitvox.me", 27015) == "claw.bitvox.me:27015");
            // The failure this guards: "hide the port" implemented as "never show a port", which loses the
            // one case the column exists for.
            T.Check("port 0 is not mistaken for the default",
                    MainMenu.AddressText("host", 0) == "host:0");
            yield break;
        }
    }

    public class MenuDirectConnectValidation : GameTest
    {
        public override string Name => "menu.direct_connect_validates";
        public override IEnumerable<Step> Run()
        {
            // Rejected instantly rather than after four 1.5 s timeouts discovering that "" is not an address.
            T.Check("empty host is refused", !MainMenu.TryParseAddress("", "47872", out _, out _, out _));
            T.Check("host with a space is refused", !MainMenu.TryParseAddress("a b", "47872", out _, out _, out _));
            T.Check("junk port is refused", !MainMenu.TryParseAddress("host", "not-a-port", out _, out _, out _));
            T.Check("port 0 is refused", !MainMenu.TryParseAddress("host", "0", out _, out _, out _));
            T.Check("port above 65535 is refused", !MainMenu.TryParseAddress("host", "70000", out _, out _, out _));

            bool ok = MainMenu.TryParseAddress(" claw.bitvox.me ", "27015", out string h, out ushort p, out string err);
            T.Check($"a good address parses (got {h}:{p})", ok && h == "claw.bitvox.me" && p == 27015 && err == null);

            // Blank port means the default -- typing an address without a port is the common case and must not
            // be an error. The teeth: this is the branch a "reject anything unparseable" rule would break.
            T.Check("blank port falls back to the default",
                    MainMenu.TryParseAddress("host", "", out _, out ushort dp, out _) && dp == MainMenu.DefaultServerPort);

            foreach (var bad in new[] { "", "a b", "not-a-port" })
            {
                MainMenu.TryParseAddress(bad == "not-a-port" ? "host" : bad, bad == "not-a-port" ? bad : "47872", out _, out _, out string e);
                T.Check($"refusal for \"{bad}\" explains itself", !string.IsNullOrEmpty(e));
            }
            yield break;
        }
    }

    public class MenuPingGate : GameTest
    {
        public override string Name => "menu.ping_gate";
        public override IEnumerable<Step> Run()
        {
            // 0 is the default and means NO limit -- the gate being off has to be genuinely off, or every
            // server that never configured one starts refusing players.
            T.Check("no limit configured lets anything through", MainMenu.PingGateRefusal(0, 5000) == null);
            T.Check("under the limit passes", MainMenu.PingGateRefusal(100, 99) == null);
            T.Check("exactly at the limit passes", MainMenu.PingGateRefusal(100, 100) == null);

            string refused = MainMenu.PingGateRefusal(100, 101);
            T.Check($"over the limit is refused (got {refused ?? "null"})", refused != null);
            T.Check("the refusal names BOTH numbers, or the player cannot tell how far over they are",
                    refused != null && refused.Contains("100") && refused.Contains("101"));
            yield break;
        }
    }

    public class MenuMotdSanitising : GameTest
    {
        public override string Name => "menu.motd_is_sanitised";
        public override IEnumerable<Step> Run()
        {
            // ⚠ This is a stranger's server sending text into OUR ui. Size is only half the problem -- the
            // shape is the other half, and it is the half that lets a MOTD forge rows or hide characters.
            T.Check("newlines cannot forge extra lines",
                    !MainMenu.Sanitize("evil\nPLAYERS: 99/99", 200).Contains("\n"));
            T.Check("carriage returns go too",
                    !MainMenu.Sanitize("a\rb", 200).Contains("\r"));
            T.Check("zero-width characters are stripped, not passed through invisibly",
                    MainMenu.Sanitize("a\u200Bb", 200) == "ab" || MainMenu.Sanitize("a\u200Bb", 200) == "a b");
            T.Check("control characters are stripped",
                    !MainMenu.Sanitize("a\u0007b", 200).Contains('\u0007'));
            T.Check("runs of whitespace collapse so padding cannot shove text off-screen",
                    MainMenu.Sanitize("a" + new string(' ', 400) + "b", 200) == "a b");

            string clamped = MainMenu.Sanitize(new string('x', 900), 200);
            T.Check($"length is hard-clamped ({clamped.Length} <= 200)", clamped.Length <= 200);

            // Ordinary text must survive intact -- a sanitiser that eats real content is its own bug.
            T.Check("normal text is untouched", MainMenu.Sanitize("Welcome to VoX Official!", 200) == "Welcome to VoX Official!");
            T.Check("non-ascii names survive", MainMenu.Sanitize("Сервер", 200) == "Сервер");
            yield break;
        }
    }

    public class MenuServerSorting : GameTest
    {
        public override string Name => "menu.server_columns_sort";
        public override IEnumerable<Step> Run()
        {
            var a = new MainMenu.ServerEntry("Alpha", "a.example", 47872, "PEI", 24, true, false, "Survival");
            var b = new MainMenu.ServerEntry("Bravo", "b.example", 47872, "Washington", 12, false, false, "Arena");
            var ping = new Dictionary<MainMenu.ServerEntry, int>();
            var players = new Dictionary<MainMenu.ServerEntry, int>();

            T.Check("name sorts A before B", MainMenu.CompareServers("Name", a, b, ping, players) < 0);
            T.Check("map sorts PEI before Washington", MainMenu.CompareServers("Map", a, b, ping, players) < 0);
            T.Check("gamemode sorts Arena before Survival", MainMenu.CompareServers("Gamemode", b, a, ping, players) < 0);
            T.Check("mode groups PvP ahead of PvE", MainMenu.CompareServers("Mode", a, b, ping, players) < 0);

            // ⭐ The one that matters: a column showing a LIVE number must order by that number, not by the
            // static row it was built from. Before the measured values are recorded, sorting by ping silently
            // ordered by name -- which looks exactly like a working sort on a list that happens to be short.
            ping[b] = 20; ping[a] = 180;
            T.Check($"ping sorts by the MEASURED value, not the row order (b={ping[b]} before a={ping[a]})",
                    MainMenu.CompareServers("Ping", a, b, ping, players) > 0);
            players[a] = 2; players[b] = 19;
            T.Check("players sorts busiest first", MainMenu.CompareServers("Players", a, b, ping, players) > 0);

            // An unmeasured server must sort LAST on ping, never first -- "-" ahead of a real 20 ms would
            // recommend the server we know least about.
            ping.Remove(a);
            T.Check("unmeasured ping sorts after a measured one",
                    MainMenu.CompareServers("Ping", a, b, ping, players) > 0);
            yield break;
        }
    }
}
