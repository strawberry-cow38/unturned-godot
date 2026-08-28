using Godot;

namespace UnturnedGodot
{
    // Play dashboard -> "Multiplayer" -> retail-style SERVER BROWSER (ported from MenuPlayServersUI +
    // SleekServer + MenuPlayConnectUI). Retail's list is fed by Steam's master server, which the port has no
    // access to, so the LIST is a hardcoded stand-in: the one Official server (our MP test server,
    // claw.bitvox.me). The JOIN button + the direct-connect-by-IP box are REAL -- they run the port's actual
    // client join (Main.BuildClient -> ClientWorldSession over UdpClientTransport), the same path the old
    // Multiplayer button used. BuildServersPanel assigns _serversPanel; TogglePlayPanel/etc. mutual-hide it.
    public partial class MainMenu
    {
        // one browsable server. Steam-only stats (VAC / workshop / gold / plugins / curation) have no source in
        // the port; live players + ping aren't queried (no A2S), so they show "-". Host:Port is real + joinable.
        public sealed record ServerEntry(string Name, string Host, ushort Port, string Map, int Max, bool Pvp, bool Locked);

        // the hardcoded server list. Retail's "Internet" tab is a Steam master-server query; ours is this one
        // authored "Official" entry (the VoX MP test server) until there's a real backend.
        static readonly ServerEntry[] OfficialServers =
        {
            new("VoX Official — Prince Edward Island", "claw.bitvox.me", 47872, "Prince Edward Island", 24, true, false),
        };

        ServerEntry _selectedServer;
        Label _svInfoName, _svInfoDetail;
        LineEdit _dcHost, _dcPort, _dcPass;
        Button _refreshBtn;
        bool _serversAutoRefreshed;
        readonly System.Collections.Generic.List<(ServerEntry sv, Button row, Label name, Label ping, Label players)> _serverRows = new();
        readonly System.Collections.Generic.HashSet<ServerEntry> _mismatched = new();   // servers whose content version != ours -> grayed, join blocked
        Button _joinBtn;
        static int _statusNonce;

        void BuildServersPanel(CanvasLayer layer)
        {
            var panel = new PanelContainer { Visible = false };
            panel.SetAnchorsPreset(Control.LayoutPreset.Center);   // centered like every other submenu
            panel.GrowHorizontal = Control.GrowDirection.Both; panel.GrowVertical = Control.GrowDirection.Both;
            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(s, 14);
            panel.AddChild(margin);
            var cols = new HBoxContainer();
            cols.AddThemeConstantOverride("separation", 18);
            margin.AddChild(cols);

            // ---- left column: header + list-source tabs + search + column heads + scrollable server list
            var left = new VBoxContainer { CustomMinimumSize = new Vector2(440f, 0f) };
            left.AddThemeConstantOverride("separation", 8);
            cols.AddChild(left);
            left.AddChild(Header("SERVERS", 24));

            var tabs = new HBoxContainer();
            tabs.AddThemeConstantOverride("separation", 4);
            left.AddChild(tabs);
            string[] sources = { "Official", "Favorites", "History", "LAN" };   // retail EServerList; only Official has data
            for (int i = 0; i < sources.Length; i++)
            {
                var tb = new Button { Text = sources[i], ToggleMode = true, ButtonPressed = i == 0, Disabled = i != 0 };
                tb.AddThemeFontSizeOverride("font_size", 14);
                tabs.AddChild(tb);
            }

            var searchRow = new HBoxContainer { CustomMinimumSize = new Vector2(440f, 30f) };
            searchRow.AddThemeConstantOverride("separation", 6);
            searchRow.AddChild(new LineEdit { PlaceholderText = "Search servers…", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            _refreshBtn = new Button { Text = "⟳ Refresh", CustomMinimumSize = new Vector2(110f, 30f) };
            _refreshBtn.AddThemeFontSizeOverride("font_size", 14);
            _refreshBtn.Pressed += RefreshServers;
            searchRow.AddChild(_refreshBtn);
            left.AddChild(searchRow);

            var hdr = new HBoxContainer { CustomMinimumSize = new Vector2(440f, 20f) };
            hdr.AddThemeConstantOverride("separation", 4);
            hdr.AddChild(ColHead("Name", 258));
            hdr.AddChild(ColHead("Players", 80));
            hdr.AddChild(ColHead("Ping", 60));
            left.AddChild(hdr);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(440f, 300f), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            left.AddChild(scroll);
            var list = new VBoxContainer { CustomMinimumSize = new Vector2(422f, 0f) };
            list.AddThemeConstantOverride("separation", 3);
            scroll.AddChild(list);
            foreach (var sv in OfficialServers) list.AddChild(ServerRow(sv));

            // ---- right column: selected-server info + JOIN + direct connect
            var right = new VBoxContainer { CustomMinimumSize = new Vector2(320f, 0f) };
            right.AddThemeConstantOverride("separation", 7);
            cols.AddChild(right);

            right.AddChild(Header("SERVER INFO", 16));
            _svInfoName = new Label { Text = "Select a server" };
            _svInfoName.AddThemeFontSizeOverride("font_size", 20);
            _svInfoName.AddThemeColorOverride("font_color", new Color(0.95f, 0.94f, 0.9f));
            right.AddChild(_svInfoName);
            _svInfoDetail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320f, 78f) };
            _svInfoDetail.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
            _svInfoDetail.AddThemeFontSizeOverride("font_size", 14);
            right.AddChild(_svInfoDetail);

            _joinBtn = new Button { Text = "JOIN", CustomMinimumSize = new Vector2(320f, 48f) };
            _joinBtn.AddThemeFontSizeOverride("font_size", 22);
            _joinBtn.Pressed += () => { if (_selectedServer != null && !_mismatched.Contains(_selectedServer)) OnJoinServer?.Invoke(_selectedServer.Host, _selectedServer.Port); };
            right.AddChild(_joinBtn);

            right.AddChild(new HSeparator());
            right.AddChild(Header("DIRECT CONNECT", 16));
            _dcHost = DcField("Host / IP", "127.0.0.1", right);
            _dcPort = DcField("Port", "47872", right);
            _dcPass = DcField("Password", "", right, isPassword: true);
            var dcBtn = new Button { Text = "CONNECT", CustomMinimumSize = new Vector2(320f, 40f) };
            dcBtn.AddThemeFontSizeOverride("font_size", 18);
            dcBtn.Pressed += () =>
            {
                string host = _dcHost.Text.Trim();
                if (host == "") host = "127.0.0.1";
                ushort port = ushort.TryParse(_dcPort.Text.Trim(), out var p) && p != 0 ? p : (ushort)47872;
                OnJoinServer?.Invoke(host, port);   // password collected but not yet part of the port handshake (follow-up)
            };
            right.AddChild(dcBtn);
            var backBtn = new Button { Text = "◄  Back", CustomMinimumSize = new Vector2(0f, 40f), Alignment = HorizontalAlignment.Left };
            backBtn.Pressed += BackToDashboard;   // dashboard is hidden while this is up -> give it its own way out
            right.AddChild(backBtn);

            layer.AddChild(panel);
            _serversPanel = panel;
            if (OfficialServers.Length > 0) SelectServer(OfficialServers[0]);   // preselect the official server
        }

        Label ColHead(string text, int w)
        {
            var l = new Label { Text = text, CustomMinimumSize = new Vector2(w, 0f) };
            l.AddThemeFontSizeOverride("font_size", 12);
            l.AddThemeColorOverride("font_color", new Color(0.6f, 0.58f, 0.5f));
            return l;
        }

        // one server row: name (+ lock / PvP tag) + players + ping columns -- the visible subset of SleekServer's
        // columns (Steam-only columns omitted, no data). The whole row is a Button; the columns overlay it.
        Control ServerRow(ServerEntry sv)
        {
            var b = new Button { CustomMinimumSize = new Vector2(422f, 44f) };
            b.Pressed += () => SelectServer(sv);
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 4);
            row.OffsetLeft = 8; row.OffsetRight = -8;
            var name = new Label
            {
                Text = sv.Name + (sv.Locked ? "  🔒" : "") + (sv.Pvp ? "   PvP" : "   PvE"),
                CustomMinimumSize = new Vector2(258f, 0f),
                VerticalAlignment = VerticalAlignment.Center,
                ClipText = true,
            };
            name.AddThemeFontSizeOverride("font_size", 15);
            row.AddChild(name);
            var playersL = InfoBox($"—/{sv.Max}", 80);
            var pingL = InfoBox("—", 60);
            row.AddChild(playersL);
            row.AddChild(pingL);
            _serverRows.Add((sv, b, name, pingL, playersL));   // Refresh updates this row's live ping/count + version-mismatch state
            b.AddChild(row);
            return b;
        }

        Label InfoBox(string text, int w)
        {
            var l = new Label
            {
                Text = text,
                CustomMinimumSize = new Vector2(w, 0f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            l.AddThemeFontSizeOverride("font_size", 14);
            l.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.88f));
            return l;
        }

        void SelectServer(ServerEntry sv)
        {
            _selectedServer = sv;
            if (_svInfoName != null) _svInfoName.Text = sv.Name;
            if (_svInfoDetail != null)
                _svInfoDetail.Text = _mismatched.Contains(sv)
                    ? $"Map:  {sv.Map}\nAddress:  {sv.Host}:{sv.Port}\nMode:  {(sv.Pvp ? "PvP" : "PvE")}\nStatus:  ⚠ VERSION MISMATCH — cannot join"
                    : $"Map:  {sv.Map}\nAddress:  {sv.Host}:{sv.Port}\nMode:  {(sv.Pvp ? "PvP" : "PvE")}     Max players:  {sv.Max}\nStatus:  press ⟳ Refresh for live ping + players";
            RefreshJoinButton();
        }

        LineEdit DcField(string label, string def, VBoxContainer parent, bool isPassword = false)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(320f, 30f) };
            row.AddThemeConstantOverride("separation", 6);
            var l = new Label { Text = label, CustomMinimumSize = new Vector2(90f, 0f), VerticalAlignment = VerticalAlignment.Center };
            l.AddThemeFontSizeOverride("font_size", 14);
            row.AddChild(l);
            var field = new LineEdit { Text = def, CustomMinimumSize = new Vector2(220f, 28f), Secret = isPassword };
            row.AddChild(field);
            parent.AddChild(row);
            return field;
        }

        // ---- server-browser status query (Refresh -> live ping + player count) ----
        // On refresh, fire a rate-limited status-req at each server (a raw UDP socket, OFF the game net stack) and
        // update the row + info panel with the round-trip ping + live player count. The server answers it down in the
        // UDP transport (UdpServerTransport.ReplyStatus). Refresh has a 5 s cooldown so it can't be spammed. Called on
        // the first open of the browser and on the ⟳ button.
        void RefreshServers()
        {
            if (_refreshBtn != null && _refreshBtn.Disabled) return;   // cooldown active
            if (_refreshBtn != null)
            {
                _refreshBtn.Disabled = true;
                _refreshBtn.Text = "Refreshing…";
                GetTree().CreateTimer(5.0).Timeout += () => { if (IsInstanceValid(_refreshBtn)) { _refreshBtn.Disabled = false; _refreshBtn.Text = "⟳ Refresh"; } };
            }
            foreach (var (sv, row, name, pingL, playersL) in _serverRows)
            {
                if (IsInstanceValid(pingL)) pingL.Text = "…";
                if (IsInstanceValid(playersL)) playersL.Text = "…";
                QueryServer(sv, row, name, pingL, playersL);
            }
        }

        void QueryServer(ServerEntry sv, Button row, Label name, Label pingL, Label playersL)
        {
            uint nonce = (uint)System.Threading.Interlocked.Increment(ref _statusNonce) ^ (uint)System.Environment.TickCount;
            System.Threading.Tasks.Task.Run(() =>
            {
                var (ok, ping, players, max, ver) = StatusQuery(sv.Host, sv.Port, nonce);
                bool mismatch = ok && ver != NetContent.Hash;   // our content identity vs the server's
                Callable.From(() =>
                {
                    if (mismatch) _mismatched.Add(sv); else _mismatched.Remove(sv);
                    var dim = new Color(0.5f, 0.5f, 0.5f);
                    var lit = new Color(0.9f, 0.9f, 0.88f);
                    if (IsInstanceValid(pingL)) { pingL.Text = ok ? $"{ping} ms" : "—"; pingL.AddThemeColorOverride("font_color", mismatch ? dim : lit); }
                    if (IsInstanceValid(playersL)) { playersL.Text = mismatch ? "mismatch" : (ok ? $"{players}/{(max > 0 ? max : sv.Max)}" : "offline"); playersL.AddThemeColorOverride("font_color", mismatch ? dim : lit); }
                    if (IsInstanceValid(name)) name.AddThemeColorOverride("font_color", mismatch ? dim : new Color(0.95f, 0.95f, 0.95f));
                    if (_selectedServer == sv) { UpdateSelectedLive(ok, ping, players, max > 0 ? max : sv.Max, mismatch); RefreshJoinButton(); }
                }).CallDeferred();
            });
        }

        // blocking UDP status query, run on a worker thread. Sends UGSQ + nonce (padded so req >= resp), waits up to
        // 1.5 s for UGSR + the echoed nonce + players(u16) + max(u16); ping = the measured round-trip time.
        static (bool ok, int ping, int players, int max, ulong version) StatusQuery(string host, ushort port, uint nonce)
        {
            try
            {
                System.Net.IPAddress addr = System.Net.IPAddress.TryParse(host, out var lit)
                    ? lit
                    : System.Array.Find(System.Net.Dns.GetHostAddresses(host), a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                      ?? System.Net.Dns.GetHostAddresses(host)[0];
                var ep = new System.Net.IPEndPoint(addr, port);
                using var udp = new System.Net.Sockets.UdpClient();
                udp.Client.ReceiveTimeout = 1500;
                var req = new byte[24];   // 4 magic + 4 nonce + 16 pad (req >= resp: no amplification)
                req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'S'; req[3] = (byte)'Q';
                req[4] = (byte)(nonce & 0xFF); req[5] = (byte)((nonce >> 8) & 0xFF); req[6] = (byte)((nonce >> 16) & 0xFF); req[7] = (byte)((nonce >> 24) & 0xFF);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                udp.Send(req, req.Length, ep);
                var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                byte[] r = udp.Receive(ref from);   // SocketException on timeout -> caught below
                sw.Stop();
                if (r.Length >= 20 && r[0] == (byte)'U' && r[1] == (byte)'G' && r[2] == (byte)'S' && r[3] == (byte)'R'
                    && r[4] == req[4] && r[5] == req[5] && r[6] == req[6] && r[7] == req[7])
                {
                    ulong ver = 0;
                    for (int i = 0; i < 8; i++) ver |= (ulong)r[12 + i] << (8 * i);   // content version (little-endian)
                    return (true, (int)sw.ElapsedMilliseconds, r[8] | (r[9] << 8), r[10] | (r[11] << 8), ver);
                }
                return (false, 0, 0, 0, 0);
            }
            catch { return (false, 0, 0, 0, 0); }
        }

        void UpdateSelectedLive(bool ok, int ping, int players, int max, bool mismatch)
        {
            // IsInstanceValid, not just a null check. This runs from a Callable.From(...).CallDeferred() bound to
            // a managed delegate rather than to a Node, so it fires even after the menu has been freed -- and a
            // freed Godot node's C# reference is NOT null. Joining inside the 1.5 s query window (the browser
            // auto-refreshes on first open, so no button press is needed) queued this against a disposed Label.
            // The row labels above already guard this way; these two did not. Review 2026-08-16.
            if (_selectedServer == null || _svInfoDetail == null || !IsInstanceValid(_svInfoDetail)) return;
            var sv = _selectedServer;
            string status = mismatch ? "⚠ VERSION MISMATCH — cannot join" : (ok ? $"{players}/{max} players  ·  {ping} ms" : "offline / no response");
            _svInfoDetail.Text = $"Map:  {sv.Map}\nAddress:  {sv.Host}:{sv.Port}\nMode:  {(sv.Pvp ? "PvP" : "PvE")}\nStatus:  {status}";
        }

        // Row gray-out is per-row (QueryServer); the JOIN button tracks whichever server is currently selected.
        void RefreshJoinButton()
        {
            if (_joinBtn == null || !IsInstanceValid(_joinBtn)) return;   // see UpdateSelectedLive -- freed is not null
            bool blocked = _selectedServer != null && _mismatched.Contains(_selectedServer);
            _joinBtn.Disabled = blocked;
            _joinBtn.Text = blocked ? "VERSION MISMATCH" : "JOIN";
        }
    }
}
