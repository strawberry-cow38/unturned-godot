using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UnturnedGodot
{
    // Discord rich presence (strawberry 2026-09-16: "research and implement discord game detection and rich
    // presence (singleplayer/multiplayer, map name, server name, server population, gamemode. singleplayer
    // days survived / main menu / map editor)").
    //
    // ⚠ WHAT "GAME DETECTION" ACTUALLY IS, because the request names a thing that does not work the way it
    // sounds. Discord's auto-detection reads a CURATED database of executables that you join by shipping a
    // verified app with a real userbase -- you cannot self-register a local build into it. What you CAN do,
    // and what this is, is drive the presence panel yourself from inside the game: Discord then shows
    // "Playing <application name>" plus these fields. Same visible result, different mechanism, and the only
    // one available to a port.
    //
    // TRANSPORT: Discord's local IPC, not the Game SDK. The SDK is a native .dll per platform that has to be
    // shipped, loaded and version-matched; the IPC is a named pipe (Windows) or unix socket (everything else)
    // speaking 8-byte-header + JSON. Zero dependencies, no native blob in the repo, and it is the same
    // protocol the SDK speaks underneath. A port that already fights one ripped-asset pipeline does not need
    // a second binary dependency for a status line.
    //
    // ⚠ EVERY FAILURE HERE IS SILENT AND NON-FATAL. Discord not installed, not running, closed mid-session,
    // a pipe that vanishes -- all of it is normal, and none of it may cost a frame or print a wall of errors
    // into a log people actually read. The worker thread is a background thread, so it cannot hold the
    // process open at quit either.
    public static class DiscordPresence
    {
        // strawberry's application, created 2026-09-16. This id is PUBLIC by design -- it is sent to a local
        // Discord client to say which app's presence to draw, it authorises nothing and there is no secret
        // half. UG_DISCORD_APPID overrides it for anyone testing against their own app.
        public const string DefaultAppId = "1549809602760155246";

        // Discord accepts roughly 5 activity updates per 20s and silently drops the rest, so a caller that
        // pushes every frame must not become a caller that pushes every frame TO DISCORD. Updates coalesce:
        // the worker always sends the LATEST state, never a backlog, so a burst costs one message.
        const int MinSendIntervalMs = 2500;
        const int ReconnectDelayMs = 15000;   // Discord closed -> retry occasionally, not in a spin

        // ---- the state the game pushes ------------------------------------------------------------------
        // A value type compared by its fields: "has anything actually changed" is the whole throttle, and a
        // class would have made that a reference check that is always true.
        readonly record struct Activity(
            string Details,       // top line
            string State,         // second line
            string LargeText,     // tooltip on the big icon
            string SmallImage,    // per-state badge, if the app has art uploaded
            string SmallText,
            int PartySize,        // MP only: (3 of 24). 0 = omit
            int PartyMax,
            long StartUnix);      // "for 12:34". 0 = omit

        static Activity _pending;
        static bool _hasPending;
        static readonly object _gate = new();
        static Thread _worker;
        static volatile bool _stop;
        static bool _disabled;

        /// <summary>When the CURRENT activity began, so Discord's elapsed timer measures this session rather
        /// than restarting every time a field changes. Reset only when the state KIND changes (menu -> SP),
        /// not when the day counter ticks -- otherwise "for 2:00:13" resets every in-game day.</summary>
        static string _kind = "";
        static long _kindStart;

        // ---- public API: one call per game state --------------------------------------------------------
        // Push-based rather than polling a dozen globals from a timer. The places that KNOW the state say so;
        // this file does not reach into Terrain, the menu, the editor and the net session to guess.

        public static void SetMenu() =>
            Push("menu", new Activity("In the main menu", "", "Unturned Godot", "menu", "Main menu", 0, 0, 0));

        public static void SetEditor(string mapName) =>
            Push("editor", new Activity("Map Editor", Clean(mapName), "Unturned Godot", "editor", "Map editor", 0, 0, 0));

        /// <summary>Singleplayer: the map, and how long you have kept it together. `day` is
        /// DayNightCycle.Day.</summary>
        public static void SetSingleplayer(string mapName, int day) =>
            Push("sp", new Activity(
                Details: "Singleplayer",
                State: $"{Clean(mapName)} — day {Math.Max(1, day)}",
                LargeText: "Unturned Godot", SmallImage: "singleplayer", SmallText: "Singleplayer",
                PartySize: 0, PartyMax: 0, StartUnix: 0));

        /// <summary>Multiplayer: whose server, which map, how full, what mode. Population comes from the
        /// server's own status block, so it is the real number rather than a guess from the browser row.</summary>
        public static void SetMultiplayer(string serverName, string mapName, int players, int max, string gamemode)
        {
            string mode = Clean(gamemode);
            string second = string.IsNullOrEmpty(mode) ? Clean(mapName) : $"{Clean(mapName)} — {mode}";
            Push("mp", new Activity(
                Details: Clean(serverName),
                State: second,
                LargeText: "Unturned Godot", SmallImage: "multiplayer", SmallText: "Multiplayer",
                PartySize: players > 0 ? players : 0,
                PartyMax: max > 0 ? max : 0,
                StartUnix: 0));
        }

        /// <summary>Drop the presence (quit / back to desktop). Closing the pipe is what actually clears it in
        /// Discord, so this just stops the worker and lets the stream dispose.</summary>
        public static void Shutdown()
        {
            _stop = true;
            lock (_gate) Monitor.PulseAll(_gate);
        }

        // ---- plumbing -----------------------------------------------------------------------------------

        static void Push(string kind, Activity a)
        {
            if (_disabled) return;
            // UG_NODISCORD: renders, tests and the dedicated server have no business advertising a presence,
            // and a headless box has no Discord to talk to anyway.
            if (_worker == null)
            {
                if (Environment.GetEnvironmentVariable("UG_NODISCORD") == "1" || Godot.OS.HasFeature("dedicated_server"))
                { _disabled = true; return; }
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "discord-rpc" };
                _worker.Start();
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (kind != _kind) { _kind = kind; _kindStart = now; }
            a = a with { StartUnix = _kindStart };
            lock (_gate)
            {
                if (_hasPending && _pending.Equals(a)) return;   // nothing changed -> nothing to send
                _pending = a; _hasPending = true;
                Monitor.PulseAll(_gate);
            }
        }

        static void WorkerLoop()
        {
            while (!_stop)
            {
                Stream pipe = null;
                try
                {
                    pipe = Connect();
                    if (pipe == null) { if (!SleepOrStop(ReconnectDelayMs)) return; continue; }

                    string appId = Environment.GetEnvironmentVariable("UG_DISCORD_APPID");
                    if (string.IsNullOrWhiteSpace(appId)) appId = DefaultAppId;
                    Send(pipe, 0, $"{{\"v\":1,\"client_id\":\"{Esc(appId)}\"}}");   // op 0 = HANDSHAKE
                    Drain(pipe);

                    var last = default(Activity);
                    bool sentOnce = false;
                    while (!_stop)
                    {
                        Activity a;
                        lock (_gate)
                        {
                            while (!_stop && (!_hasPending || (sentOnce && _pending.Equals(last))))
                                Monitor.Wait(_gate, 1000);
                            if (_stop) break;
                            a = _pending;
                        }
                        Send(pipe, 1, Frame(a));   // op 1 = FRAME
                        Drain(pipe);
                        last = a; sentOnce = true;
                        if (!SleepOrStop(MinSendIntervalMs)) break;   // rate limit AFTER sending, so the first update is instant
                    }
                }
                catch
                {
                    // Discord quit, the pipe went away, or it was never there. Not an error anyone can act on
                    // -- drop the connection and try again later. Deliberately not logged: this runs on a
                    // timer forever, and a log line per retry is how a log stops being readable.
                }
                finally { try { pipe?.Dispose(); } catch { } }

                if (!_stop && !SleepOrStop(ReconnectDelayMs)) return;
            }
        }

        static bool SleepOrStop(int ms)
        {
            lock (_gate) { if (_stop) return false; Monitor.Wait(_gate, ms); return !_stop; }
        }

        /// <summary>Discord listens on discord-ipc-0..9 -- several can exist at once (stable beside PTB beside
        /// Canary), and the first one that accepts is the one to talk to.</summary>
        static Stream Connect()
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                        p.Connect(200);
                        return p;
                    }
                    // Everything else: a unix socket under the runtime dir. Flatpak/Snap Discord puts it one
                    // level deeper, which is why the candidates below are tried rather than one fixed path.
                    foreach (string dir in UnixSocketDirs())
                    {
                        string path = Path.Combine(dir, $"discord-ipc-{i}");
                        if (!File.Exists(path)) continue;
                        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        s.Connect(new UnixDomainSocketEndPoint(path));
                        return new NetworkStream(s, ownsSocket: true);
                    }
                }
                catch { }
            }
            return null;
        }

        static IEnumerable<string> UnixSocketDirs()
        {
            string run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(run))
            {
                yield return run;
                yield return Path.Combine(run, "app", "com.discordapp.Discord");
                yield return Path.Combine(run, "snap.discord");
            }
            foreach (var v in new[] { "TMPDIR", "TMP", "TEMP" })
            {
                string t = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(t)) yield return t;
            }
            yield return "/tmp";
        }

        // 4-byte opcode + 4-byte length, both little-endian, then UTF-8 JSON.
        static void Send(Stream s, int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var head = new byte[8];
            BinaryPrimitivesWrite(head, 0, op);
            BinaryPrimitivesWrite(head, 4, body.Length);
            s.Write(head, 0, 8);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        static void BinaryPrimitivesWrite(byte[] buf, int off, int value)
        {
            buf[off] = (byte)value; buf[off + 1] = (byte)(value >> 8);
            buf[off + 2] = (byte)(value >> 16); buf[off + 3] = (byte)(value >> 24);
        }

        /// <summary>Read and discard whatever Discord replied. ⚠ Not optional: the responses (READY, and one
        /// per SET_ACTIVITY) are real bytes, and never reading them fills the pipe buffer until a later write
        /// blocks forever on a background thread nobody is watching.</summary>
        static void Drain(Stream s)
        {
            var head = new byte[8];
            var deadline = DateTime.UtcNow.AddMilliseconds(400);
            while (DateTime.UtcNow < deadline)
            {
                if (s is NamedPipeClientStream np && !np.IsConnected) return;
                if (!TryReadExact(s, head, 8)) return;
                int len = head[4] | (head[5] << 8) | (head[6] << 16) | (head[7] << 24);
                if (len <= 0 || len > 1 << 20) return;
                var body = new byte[len];
                if (!TryReadExact(s, body, len)) return;
                return;   // one reply is all we expect per message; anything else drains on the next pass
            }
        }

        static bool TryReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            try
            {
                while (got < count)
                {
                    int n = s.Read(buf, got, count - got);
                    if (n <= 0) return false;
                    got += n;
                }
            }
            catch { return false; }
            return true;
        }

        static string Frame(Activity a)
        {
            var sb = new StringBuilder();
            sb.Append("{\"cmd\":\"SET_ACTIVITY\",\"nonce\":\"").Append(Guid.NewGuid().ToString("N"))
              .Append("\",\"args\":{\"pid\":").Append(Environment.ProcessId)
              .Append(",\"activity\":{");
            bool first = true;
            void Field(string k, string v)
            {
                if (string.IsNullOrEmpty(v)) return;
                if (!first) sb.Append(',');
                sb.Append('"').Append(k).Append("\":\"").Append(Esc(v)).Append('"');
                first = false;
            }
            Field("details", a.Details);
            Field("state", a.State);
            if (a.StartUnix > 0)
            {
                if (!first) sb.Append(',');
                sb.Append("\"timestamps\":{\"start\":").Append(a.StartUnix).Append('}');
                first = false;
            }
            if (a.PartySize > 0 && a.PartyMax > 0)
            {
                if (!first) sb.Append(',');
                sb.Append("\"party\":{\"size\":[").Append(a.PartySize).Append(',').Append(a.PartyMax).Append("]}");
                first = false;
            }
            // Art keys are names uploaded under the app's Rich Presence assets. If none exist Discord simply
            // draws no image -- it is not an error and the text fields still show, which is why these are sent
            // unconditionally rather than gated on someone remembering to upload art.
            if (!first) sb.Append(',');
            sb.Append("\"assets\":{\"large_image\":\"logo\",\"large_text\":\"").Append(Esc(a.LargeText)).Append('"');
            if (!string.IsNullOrEmpty(a.SmallImage))
                sb.Append(",\"small_image\":\"").Append(Esc(a.SmallImage)).Append("\",\"small_text\":\"").Append(Esc(a.SmallText)).Append('"');
            sb.Append("}}}}");
            return sb.ToString();
        }

        static string Esc(string s) => JsonEncodedText.Encode(s ?? "").ToString();

        // Test seam: the frame JSON is the whole wire contract, and it is the one part of this that can be
        // wrong without Discord being installed to notice.
        internal static string FrameForTest(string details, string state, int size, int max, long start) =>
            Frame(new Activity(details, state, "Unturned Godot", "", "", size, max, start));

        /// <summary>⚠ Clean() runs in the SetX methods, NOT in Frame() -- escaping and sanitising are separate
        /// jobs and only one of them is Frame's. A test that drove Clean THROUGH FrameForTest would be
        /// asserting behaviour this code does not have and blaming Frame for it.</summary>
        internal static string CleanForTest(string s) => Clean(s);

        /// <summary>Discord truncates past 128 chars and a server name arrives off the wire from a stranger.
        /// Strip control characters so a crafted name cannot inject newlines into the panel.</summary>
        static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= 120) break;
            }
            return sb.ToString().Trim();
        }
    }

    /// <summary>Re-pushes a presence line once a second, for the fields that MOVE -- the day counter and the
    /// player count. Everything else is set once at the transition and never changes, and DiscordPresence
    /// drops an identical push, so a 1 Hz repeat costs one value comparison when nothing has happened.
    ///
    /// Owned by whoever created the state it describes, so it dies with that scene rather than outliving it
    /// and advertising a session that ended. A top-level node rather than a member of the static class
    /// because Godot's source generator wants its Node types declared at namespace scope.</summary>
    public sealed partial class DiscordPresenceTicker : Godot.Node
    {
        /// <summary>What to push. Set at construction by the caller that knows the state.</summary>
        public System.Action Push;

        public override void _Ready()
        {
            // 1 Hz through the hub rather than _Process: per-NODE engine callbacks are the tax TickHub exists
            // to remove, and this needs a tick a second, not sixty.
            TickHub.Add(this, Tick, 1f);
            SetProcess(false);
        }

        /// <summary>⚠ Unsubscribe. A hub subscriber that outlives its node is exactly the leak fixed in
        /// 14d99395 -- the hub holds the delegate, so a freed ticker keeps being called and keeps pushing a
        /// presence for a world that is gone.</summary>
        public override void _ExitTree() => TickHub.Remove(this);

        void Tick(double delta) { try { Push?.Invoke(); } catch { } }
    }
}
