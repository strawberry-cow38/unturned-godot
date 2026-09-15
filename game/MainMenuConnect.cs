using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Direct Connect (its own main-menu page, moved off the server browser) + the join-failure modal.
    //
    // Why this is a separate file rather than more of MainMenuServers.cs: the browser's job is "pick from a
    // list", this one's is "reach an address and say what happened". They shared a panel and the shared half
    // was the honest-feedback half, which neither did -- Direct Connect collected a password it never sent and
    // handed off to a world build that could not report a refusal.
    //
    // ⭐ THE ORDER IS THE POINT: PROBE, THEN LOAD. The old path freed the menu, built the entire world, and
    // only then opened a socket -- so an unreachable address cost a full map load before saying nothing at
    // all. strawberry 2026-09-15: "we should try to connect before loading. if we cant, give error."
    public partial class MainMenu
    {
        /// <summary>The port a server runs on unless told otherwise. Named because the browser HIDES it in the
        /// address column -- ":47872" on every row is noise, and a row that shows a port is telling you something.</summary>
        public const ushort DefaultServerPort = 47872;

        Control _directPanel, _errorPanel;
        Label _errorText;
        LineEdit _pcHost, _pcPort, _pcPass;
        Label _pcStatus;
        Button _pcConnect;
        int _probeGen;                       // bumps on every new attempt + on Back: a late worker answering for an abandoned attempt must not write to the UI
        public const int ConnectAttempts = 4;         // retail-ish: a few tries before giving up, each one reported

        /// <summary>The address a successful probe cleared, handed to the real join.</summary>
        public System.Action<string, ushort, string> OnDirectConnect;   // host, port, password

        void BuildConnectPanels(CanvasLayer layer)
        {
            // ---- Direct Connect page
            var panel = new PanelContainer { Visible = false };
            panel.SetAnchorsPreset(Control.LayoutPreset.Center);
            panel.GrowHorizontal = Control.GrowDirection.Both; panel.GrowVertical = Control.GrowDirection.Both;
            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(s, 18);
            panel.AddChild(margin);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(430f, 0f) };
            box.AddThemeConstantOverride("separation", 9);
            margin.AddChild(box);

            box.AddChild(Header("DIRECT CONNECT", 24));
            _pcHost = ConnectField(box, "Host / IP", "127.0.0.1");
            _pcPort = ConnectField(box, "Port", DefaultServerPort.ToString());
            _pcPass = ConnectField(box, "Password", "", isPassword: true);

            _pcStatus = new Label
            {
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(430f, 54f),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _pcStatus.AddThemeFontSizeOverride("font_size", 14);
            _pcStatus.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
            box.AddChild(_pcStatus);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            box.AddChild(row);
            var back = new Button { Text = "◄  Back", CustomMinimumSize = new Vector2(150f, 42f) };
            back.Pressed += () => { _probeGen++; SetConnectBusy(false); BackToDashboard(); };   // abandon any in-flight probe
            row.AddChild(back);
            _pcConnect = new Button { Text = "CONNECT", CustomMinimumSize = new Vector2(264f, 42f) };
            _pcConnect.AddThemeFontSizeOverride("font_size", 18);
            _pcConnect.Pressed += StartDirectConnect;
            row.AddChild(_pcConnect);

            layer.AddChild(panel);
            _directPanel = panel;

            // ---- join-failure modal (also shown after a mid-game drop bounces back to the menu)
            var ep = new PanelContainer { Visible = false };
            ep.SetAnchorsPreset(Control.LayoutPreset.Center);
            ep.GrowHorizontal = Control.GrowDirection.Both; ep.GrowVertical = Control.GrowDirection.Both;
            var em = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                em.AddThemeConstantOverride(s, 20);
            ep.AddChild(em);
            var ebox = new VBoxContainer { CustomMinimumSize = new Vector2(460f, 0f) };
            ebox.AddThemeConstantOverride("separation", 12);
            em.AddChild(ebox);
            ebox.AddChild(Header("COULD NOT JOIN", 22));
            _errorText = new Label
            {
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(460f, 64f),
            };
            _errorText.AddThemeFontSizeOverride("font_size", 16);
            _errorText.AddThemeColorOverride("font_color", new Color(1f, 0.72f, 0.66f));
            ebox.AddChild(_errorText);
            var ok = new Button { Text = "OK", CustomMinimumSize = new Vector2(460f, 40f) };
            ok.Pressed += () => { if (_errorPanel != null) _errorPanel.Visible = false; };
            ebox.AddChild(ok);
            layer.AddChild(ep);
            _errorPanel = ep;
        }

        /// <summary>Show a join failure. Called on menu build for a message left behind by a dropped session.</summary>
        public void ShowJoinError(string message)
        {
            if (_errorPanel == null || _errorText == null) return;
            _errorText.Text = message;
            _errorPanel.Visible = true;
        }

        void ShowDirectConnect()
        {
            HideAllPanels();
            if (_directPanel == null) return;
            _directPanel.Visible = true;
            if (_pcStatus != null) _pcStatus.Text = "";
            SetConnectBusy(false);
        }

        LineEdit ConnectField(VBoxContainer parent, string label, string def, bool isPassword = false)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(430f, 32f) };
            row.AddThemeConstantOverride("separation", 8);
            var l = new Label { Text = label, CustomMinimumSize = new Vector2(104f, 0f), VerticalAlignment = VerticalAlignment.Center };
            l.AddThemeFontSizeOverride("font_size", 15);
            row.AddChild(l);
            var f = new LineEdit { Text = def, CustomMinimumSize = new Vector2(310f, 30f), Secret = isPassword };
            row.AddChild(f);
            parent.AddChild(row);
            return f;
        }

        void SetConnectBusy(bool busy)
        {
            if (_pcConnect != null && IsInstanceValid(_pcConnect))
            {
                _pcConnect.Disabled = busy;
                _pcConnect.Text = busy ? "CONNECTING…" : "CONNECT";
            }
        }

        // Parse + validate here rather than at the socket: an empty host or a junk port should say so instantly
        // instead of spending four timeouts discovering that "localhost:" is not an address.
        public static bool TryParseAddress(string hostText, string portText, out string host, out ushort port, out string error)
        {
            host = (hostText ?? "").Trim();
            port = DefaultServerPort;
            error = null;
            if (host.Length == 0) { error = "Enter a host or IP address."; return false; }
            if (host.Contains(' ')) { error = "Host cannot contain spaces."; return false; }
            string pt = (portText ?? "").Trim();
            if (pt.Length != 0)
            {
                if (!ushort.TryParse(pt, out port) || port == 0) { error = $"\"{pt}\" is not a valid port (1-65535)."; return false; }
            }
            return true;
        }

        /// <summary>
        /// The max-ping gate, client side. The server ADVERTISES its limit in the status block and the client
        /// measures its own round trip timing the status query -- which is a real ping, unlike the server's
        /// smoothed ack turnaround (see the note in NetServerSession: idle, that reads the keepalive interval
        /// and would kick a healthy player). Returns null when the join may proceed.
        /// </summary>
        public static string PingGateRefusal(int serverMaxPing, int measuredPing)
        {
            if (serverMaxPing <= 0) return null;              // 0 = no limit, and it is the default
            if (measuredPing <= serverMaxPing) return null;
            return $"{NetRejectText.Describe(NetRejectReason.PingTooHigh)} (this server allows {serverMaxPing} ms, yours is {measuredPing} ms)";
        }

        void StartDirectConnect()
        {
            if (!TryParseAddress(_pcHost?.Text, _pcPort?.Text, out string host, out ushort port, out string err))
            {
                if (_pcStatus != null) { _pcStatus.Text = err; _pcStatus.AddThemeColorOverride("font_color", new Color(1f, 0.6f, 0.55f)); }
                return;
            }
            RunConnectProbe(host, port, _pcPass?.Text ?? "");
        }

        // Probe the address up to ConnectAttempts times, reporting EACH attempt, and only hand off to the real
        // join once something answers. The status socket is the right probe: it is the same UDP port, it is
        // already non-amplifying, and it costs nothing next to a map load.
        void RunConnectProbe(string host, ushort port, string password)
        {
            int gen = ++_probeGen;
            SetConnectBusy(true);
            SetProbeStatus($"Connecting to {host}:{port} — attempt 1 of {ConnectAttempts}…", false);
            System.Threading.Tasks.Task.Run(() =>
            {
                for (int attempt = 1; attempt <= ConnectAttempts; attempt++)
                {
                    if (gen != _probeGen) return;   // Back pressed, or a newer attempt started
                    uint nonce = (uint)System.Threading.Interlocked.Increment(ref _statusNonce) ^ (uint)System.Environment.TickCount;
                    var st = StatusQueryFull(host, port, nonce);
                    bool ok = st.Ok; int ping = st.Ping; int maxPing = st.MaxPing;
                    if (gen != _probeGen) return;
                    if (ok)
                    {
                        bool contentOk = st.Version == NetContent.Hash;
                        Callable.From(() =>
                        {
                            if (gen != _probeGen) return;
                            if (!contentOk)
                            {
                                SetProbeStatus(NetRejectText.Describe(NetRejectReason.ContentMismatch), true);
                                SetConnectBusy(false);
                                return;
                            }
                            string pingRefusal = PingGateRefusal(maxPing, ping);
                            if (pingRefusal != null) { SetProbeStatus(pingRefusal, true); SetConnectBusy(false); return; }
                            SetProbeStatus($"Reached {host}:{port} ({ping} ms) — joining…", false);
                            OnDirectConnect?.Invoke(host, port, password);
                        }).CallDeferred();
                        return;
                    }
                    int next = attempt + 1;
                    if (next <= ConnectAttempts)
                        Callable.From(() =>
                        {
                            if (gen != _probeGen) return;
                            SetProbeStatus($"No answer from {host}:{port} — attempt {next} of {ConnectAttempts}…", false);
                        }).CallDeferred();
                }
                Callable.From(() =>
                {
                    if (gen != _probeGen) return;
                    // NOT "the server refused you" -- nothing answered, and naming a refusal we never received
                    // would be inventing the half of the exchange that did not happen.
                    SetProbeStatus($"{NetRejectText.Describe(NetRejectReason.None)} (tried {ConnectAttempts} times)", true);
                    SetConnectBusy(false);
                }).CallDeferred();
            });
        }

        void SetProbeStatus(string text, bool bad)
        {
            if (_pcStatus == null || !IsInstanceValid(_pcStatus)) return;   // freed is not null -- see UpdateSelectedLive
            _pcStatus.Text = text;
            _pcStatus.AddThemeColorOverride("font_color", bad ? new Color(1f, 0.6f, 0.55f) : new Color(0.78f, 0.82f, 0.78f));
        }
    }
}
