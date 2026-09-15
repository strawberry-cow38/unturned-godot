using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Play dashboard -> "Multiplayer" -> retail-style SERVER BROWSER (ported from MenuPlayServersUI +
    // SleekServer + MenuPlayConnectUI). Retail's list is fed by Steam's master server, which the port has no
    // access to, so the LIST is a hardcoded stand-in: the one Official server (our MP test server,
    // claw.bitvox.me). The JOIN button is REAL (direct connect moved to MainMenuConnect.cs) -- it runs the port's actual
    // client join (Main.BuildClient -> ClientWorldSession over UdpClientTransport), the same path the old
    // Multiplayer button used. BuildServersPanel assigns _serversPanel; TogglePlayPanel/etc. mutual-hide it.
    public partial class MainMenu
    {
        // one browsable server. Steam-only stats (VAC / workshop / gold / plugins / curation) have no source in
        // the port; live players + ping aren't queried (no A2S), so they show "-". Host:Port is real + joinable.
        public sealed record ServerEntry(string Name, string Host, ushort Port, string Map, int Max, bool Pvp, bool Locked,
                                         string Gamemode = "Survival");

        /// <summary>host:port for display, with the DEFAULT port left off. A port in the address column then
        /// means "this one is unusual", which is the only time it is worth the width (strawberry 2026-09-15).</summary>
        public static string AddressText(string host, ushort port) =>
            port == DefaultServerPort ? host : $"{host}:{port}";

        // the hardcoded server list. Retail's "Internet" tab is a Steam master-server query; ours is this one
        // authored "Official" entry (the VoX MP test server) until there's a real backend.
        static readonly ServerEntry[] OfficialServers =
        {
            new("VoX Official — PEI", "claw.bitvox.me", 47872, "PEI", 24, true, false),
        };

        ServerEntry _selectedServer;
        Label _svInfoName, _svInfoDetail;
        TextureRect _svIcon;
        Button _refreshBtn;
        bool _serversAutoRefreshed;
        readonly System.Collections.Generic.List<(ServerEntry sv, Button row, Label name, Label ping, Label players)> _serverRows = new();
        VBoxContainer _serverList;        // re-ordered in place when a column head is clicked
        string _sortKey = "Name";         // which column the list is ordered by
        bool _sortDesc;                   // second click on the same head reverses
        readonly System.Collections.Generic.Dictionary<ServerEntry, int> _livePing = new();     // measured, for sorting by a column that shows "-" until Refresh
        readonly System.Collections.Generic.Dictionary<ServerEntry, int> _livePlayers = new();
        readonly System.Collections.Generic.Dictionary<ServerEntry, ServerStatus> _liveStatus = new();   // the v2 block, for the info panel
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
            var left = new VBoxContainer { CustomMinimumSize = new Vector2(ListWidth, 0f) };
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

            var searchRow = new HBoxContainer { CustomMinimumSize = new Vector2(ListWidth, 30f) };
            searchRow.AddThemeConstantOverride("separation", 6);
            searchRow.AddChild(new LineEdit { PlaceholderText = "Search servers…", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            _refreshBtn = new Button { Text = "⟳ Refresh", CustomMinimumSize = new Vector2(110f, 30f) };
            _refreshBtn.AddThemeFontSizeOverride("font_size", 14);
            _refreshBtn.Pressed += RefreshServers;
            searchRow.AddChild(_refreshBtn);
            left.AddChild(searchRow);

            var hdr = new HBoxContainer { CustomMinimumSize = new Vector2(ListWidth, 22f) };
            hdr.AddThemeConstantOverride("separation", 4);
            foreach (var (key, w) in Columns) hdr.AddChild(ColHeadButton(key, w));
            left.AddChild(hdr);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(ListWidth, 300f), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            left.AddChild(scroll);
            _serverList = new VBoxContainer { CustomMinimumSize = new Vector2(ListWidth - 18f, 0f) };
            _serverList.AddThemeConstantOverride("separation", 3);
            scroll.AddChild(_serverList);
            foreach (var sv in OfficialServers) _serverList.AddChild(ServerRow(sv));
            ApplySort();

            // ---- right column: selected-server info + JOIN + direct connect
            var right = new VBoxContainer { CustomMinimumSize = new Vector2(320f, 0f) };
            right.AddThemeConstantOverride("separation", 7);
            cols.AddChild(right);

            right.AddChild(Header("SERVER INFO", 16));
            var nameRow = new HBoxContainer();
            nameRow.AddThemeConstantOverride("separation", 8);
            right.AddChild(nameRow);
            _svIcon = new TextureRect
            {
                CustomMinimumSize = new Vector2(48f, 48f),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Visible = false,
            };
            nameRow.AddChild(_svIcon);
            _svInfoName = new Label { Text = "Select a server", VerticalAlignment = VerticalAlignment.Center };
            _svInfoName.AddThemeFontSizeOverride("font_size", 20);
            _svInfoName.AddThemeColorOverride("font_color", new Color(0.95f, 0.94f, 0.9f));
            nameRow.AddChild(_svInfoName);
            _svInfoDetail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320f, 78f) };
            _svInfoDetail.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
            _svInfoDetail.AddThemeFontSizeOverride("font_size", 14);
            right.AddChild(_svInfoDetail);

            _joinBtn = new Button { Text = "JOIN", CustomMinimumSize = new Vector2(320f, 48f) };
            _joinBtn.AddThemeFontSizeOverride("font_size", 22);
            _joinBtn.Pressed += () => { if (_selectedServer != null && !_mismatched.Contains(_selectedServer)) OnJoinServer?.Invoke(_selectedServer.Host, _selectedServer.Port); };
            right.AddChild(_joinBtn);

            // Direct connect MOVED OUT to its own main-menu page (MainMenuConnect.cs, strawberry 2026-09-15).
            // It never belonged beside a list you pick from, and the version here handed straight to the world
            // build with no probe and a password it silently dropped.
            var backBtn = new Button { Text = "◄  Back", CustomMinimumSize = new Vector2(0f, 40f), Alignment = HorizontalAlignment.Left };
            backBtn.Pressed += BackToDashboard;   // dashboard is hidden while this is up -> give it its own way out
            right.AddChild(backBtn);

            layer.AddChild(panel);
            _serversPanel = panel;
            if (OfficialServers.Length > 0) SelectServer(OfficialServers[0]);   // preselect the official server
        }

        // The columns, in display order, with their widths. ONE table drives the heads, the rows and the
        // sort -- a head list and a row list maintained separately is how a column ends up sorting by its
        // neighbour.
        const float ListWidth = 700f;
        static readonly (string Key, int W)[] Columns =
        {
            ("Name", 210), ("Map", 120), ("Mode", 58), ("Gamemode", 92), ("Players", 80), ("Ping", 60),
        };

        // A head is a Button now: every column sorts, and clicking the active one reverses it.
        Button ColHeadButton(string key, int w)
        {
            var b = new Button { Text = key, CustomMinimumSize = new Vector2(w, 22f), Flat = true, Alignment = HorizontalAlignment.Left };
            b.AddThemeFontSizeOverride("font_size", 12);
            b.AddThemeColorOverride("font_color", new Color(0.6f, 0.58f, 0.5f));
            b.Pressed += () =>
            {
                if (_sortKey == key) _sortDesc = !_sortDesc; else { _sortKey = key; _sortDesc = false; }
                ApplySort();
            };
            _headButtons[key] = b;
            return b;
        }

        readonly System.Collections.Generic.Dictionary<string, Button> _headButtons = new();

        // Sort the EXISTING row nodes rather than rebuilding them: a row owns its live ping/player labels and
        // its mismatch colouring, and rebuilding would drop a Refresh already in flight onto freed labels.
        void ApplySort()
        {
            if (_serverList == null || !IsInstanceValid(_serverList)) return;
            var ordered = new System.Collections.Generic.List<(ServerEntry sv, Button row, Label name, Label ping, Label players)>(_serverRows);
            ordered.Sort((a, b) => { int c = CompareBy(_sortKey, a.sv, b.sv); return _sortDesc ? -c : c; });
            for (int i = 0; i < ordered.Count; i++)
                if (IsInstanceValid(ordered[i].row)) _serverList.MoveChild(ordered[i].row, i);
            foreach (var (key, b) in _headButtons)
                if (IsInstanceValid(b)) b.Text = key == _sortKey ? key + (_sortDesc ? "  ▼" : "  ▲") : key;
        }

        // Ping and Players sort by the MEASURED value where there is one. A column that displays a live number
        // has to sort by that number, not by the static row it was built from -- otherwise "sort by ping" on a
        // refreshed list silently orders by name.
        int CompareBy(string key, ServerEntry a, ServerEntry b) => CompareServers(key, a, b, _livePing, _livePlayers);

        /// <summary>The ordering, as a pure function of the rows and the measured values -- so the column
        /// order is testable without building a menu, which headless cannot do.</summary>
        public static int CompareServers(string key, ServerEntry a, ServerEntry b,
                                         System.Collections.Generic.Dictionary<ServerEntry, int> livePing,
                                         System.Collections.Generic.Dictionary<ServerEntry, int> livePlayers)
        {
            switch (key)
            {
                case "Map": return string.Compare(a.Map, b.Map, System.StringComparison.OrdinalIgnoreCase);
                case "Mode": return a.Pvp == b.Pvp ? 0 : (a.Pvp ? -1 : 1);
                case "Gamemode": return string.Compare(a.Gamemode, b.Gamemode, System.StringComparison.OrdinalIgnoreCase);
                case "Players": return LiveOr(livePlayers, b, -1).CompareTo(LiveOr(livePlayers, a, -1));   // busiest first
                case "Ping": return LiveOr(livePing, a, int.MaxValue).CompareTo(LiveOr(livePing, b, int.MaxValue));   // unmeasured sorts last, never first
                case "Name":
                default: return string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        static int LiveOr(System.Collections.Generic.Dictionary<ServerEntry, int> d, ServerEntry sv, int fallback) =>
            d.TryGetValue(sv, out int v) ? v : fallback;

        Label ColCell(string text, int w, bool dim = false)
        {
            var l = new Label
            {
                Text = text,
                CustomMinimumSize = new Vector2(w, 0f),
                VerticalAlignment = VerticalAlignment.Center,
                ClipText = true,
            };
            l.AddThemeFontSizeOverride("font_size", 14);
            l.AddThemeColorOverride("font_color", dim ? new Color(0.72f, 0.72f, 0.7f) : new Color(0.9f, 0.9f, 0.88f));
            return l;
        }

        // one server row: the Columns table, in the same order as the heads above it.
        Control ServerRow(ServerEntry sv)
        {
            var b = new Button { CustomMinimumSize = new Vector2(ListWidth - 18f, 44f) };
            b.Pressed += () => SelectServer(sv);
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 4);
            row.OffsetLeft = 8; row.OffsetRight = -8;

            var name = ColCell(sv.Name + (sv.Locked ? "  \U0001F512" : ""), Columns[0].W);
            name.AddThemeFontSizeOverride("font_size", 15);
            row.AddChild(name);
            row.AddChild(ColCell(sv.Map, Columns[1].W, dim: true));
            row.AddChild(ColCell(sv.Pvp ? "PvP" : "PvE", Columns[2].W, dim: true));
            row.AddChild(ColCell(sv.Gamemode, Columns[3].W, dim: true));
            var playersL = InfoBox($"\u2014/{sv.Max}", Columns[4].W);
            var pingL = InfoBox("\u2014", Columns[5].W);
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
                    ? $"Map:  {sv.Map}\nAddress:  {AddressText(sv.Host, sv.Port)}\nMode:  {(sv.Pvp ? "PvP" : "PvE")}\nStatus:  ⚠ VERSION MISMATCH — cannot join"
                    : $"Map:  {sv.Map}\nAddress:  {AddressText(sv.Host, sv.Port)}\nMode:  {(sv.Pvp ? "PvP" : "PvE")}     Max players:  {sv.Max}\nStatus:  press ⟳ Refresh for live ping + players";
            RefreshJoinButton();
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
                var st = StatusQueryFull(sv.Host, sv.Port, nonce);
                bool ok = st.Ok; int ping = st.Ping; int players = st.Players; int max = st.Max;
                bool mismatch = ok && st.Version != NetContent.Hash;   // our content identity vs the server's
                Callable.From(() =>
                {
                    if (mismatch) _mismatched.Add(sv); else _mismatched.Remove(sv);
                    // Keep the measured numbers, not just the label text: a column that displays a live value
                    // must SORT by that value, or "sort by ping" on a refreshed list quietly orders by name.
                    if (ok) { _livePing[sv] = ping; _livePlayers[sv] = players; _liveStatus[sv] = st; }
                    else { _livePing.Remove(sv); _livePlayers.Remove(sv); _liveStatus.Remove(sv); }
                    var dim = new Color(0.5f, 0.5f, 0.5f);
                    var lit = new Color(0.9f, 0.9f, 0.88f);
                    if (IsInstanceValid(pingL)) { pingL.Text = ok ? $"{ping} ms" : "—"; pingL.AddThemeColorOverride("font_color", mismatch ? dim : lit); }
                    // The row and the join dialog say the SAME word for the same failure. Two vocabularies for
                    // one state ("mismatch" here, "different game content" there) is how a player concludes
                    // they are two different problems.
                    if (IsInstanceValid(playersL))
                    {
                        playersL.Text = mismatch ? NetRejectText.Short(NetRejectReason.ContentMismatch)
                                      : (ok ? $"{players}/{(max > 0 ? max : sv.Max)}" : NetRejectText.Short(NetRejectReason.None));
                        playersL.AddThemeColorOverride("font_color", mismatch ? dim : lit);
                    }
                    if (IsInstanceValid(name)) name.AddThemeColorOverride("font_color", mismatch ? dim : new Color(0.95f, 0.95f, 0.95f));
                    if (_selectedServer == sv) { UpdateSelectedLive(ok, ping, players, max > 0 ? max : sv.Max, mismatch); RefreshJoinButton(); }
                    if (_sortKey == "Ping" || _sortKey == "Players") ApplySort();   // the values it is sorted BY just changed
                }).CallDeferred();
            });
        }

        /// <summary>What a status query got back. Everything past `Version` is v2 and may be absent.</summary>
        public struct ServerStatus
        {
            public bool Ok; public int Ping; public int Players; public int Max; public ulong Version;
            public byte Protocol;      // 0 = the server did not say (pre-v2)
            public bool Pvp, Passworded, HasFlags;
            public int MaxPing;        // 0 = no limit
            public string Map, Gamemode, Motd;
            public int IconBytes;      // total size of the server's icon; the image itself is pulled on select
        }

        // The request is PADDED to StatusReqBytes, and that padding is what buys the v2 block: the server
        // clamps its reply to the request length, so a bigger ask is the only way to be told more and the
        // socket can never amplify. 512 keeps the whole exchange inside one datagram on any sane path.
        const int StatusReqBytes = 1200;   // room for the icon; still one datagram on any sane path (NetProtocol.MaxDatagramBytes)

        // blocking UDP status query, run on a worker thread. Sends UGSQ + nonce (padded so req >= resp), waits
        // up to 1.5 s for UGSR + the echoed nonce + players(u16) + max(u16) + content version, then the
        // optional v2 tail; ping = the measured round-trip time.
        static ServerStatus StatusQueryFull(string host, ushort port, uint nonce)
        {
            var outp = new ServerStatus();
            try
            {
                System.Net.IPAddress addr = System.Net.IPAddress.TryParse(host, out var lit)
                    ? lit
                    : System.Array.Find(System.Net.Dns.GetHostAddresses(host), a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                      ?? System.Net.Dns.GetHostAddresses(host)[0];
                var ep = new System.Net.IPEndPoint(addr, port);
                using var udp = new System.Net.Sockets.UdpClient();
                udp.Client.ReceiveTimeout = 1500;
                var req = new byte[StatusReqBytes];
                req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'S'; req[3] = (byte)'Q';
                req[4] = (byte)(nonce & 0xFF); req[5] = (byte)((nonce >> 8) & 0xFF); req[6] = (byte)((nonce >> 16) & 0xFF); req[7] = (byte)((nonce >> 24) & 0xFF);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                udp.Send(req, req.Length, ep);
                var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                byte[] r = udp.Receive(ref from);   // SocketException on timeout -> caught below
                sw.Stop();
                if (r.Length < 20 || r[0] != (byte)'U' || r[1] != (byte)'G' || r[2] != (byte)'S' || r[3] != (byte)'R'
                    || r[4] != req[4] || r[5] != req[5] || r[6] != req[6] || r[7] != req[7]) return outp;
                ulong ver = 0;
                for (int i = 0; i < 8; i++) ver |= (ulong)r[12 + i] << (8 * i);
                outp.Ok = true; outp.Ping = (int)sw.ElapsedMilliseconds;
                outp.Players = r[8] | (r[9] << 8); outp.Max = r[10] | (r[11] << 8); outp.Version = ver;

                // ---- optional v2 tail. EVERY read is bounded by the buffer AND by our own caps: the server
                // clamps on its side, but a client that trusts a remote length byte is a client that can be
                // made to read whatever a hostile server likes. Any short read just stops -- a truncated tail
                // must degrade to "we know less", never to a throw that loses the whole (valid) core.
                int o = 20;
                if (o < r.Length) { outp.Protocol = r[o++]; }
                if (o < r.Length) { byte f = r[o++]; outp.Pvp = (f & 1) != 0; outp.Passworded = (f & 2) != 0; outp.HasFlags = true; }
                if (o + 1 < r.Length) { outp.MaxPing = r[o] | (r[o + 1] << 8); o += 2; }
                outp.Map = ReadBoundedString(r, ref o, 64);
                outp.Gamemode = ReadBoundedString(r, ref o, 32);
                outp.Motd = ReadBoundedString(r, ref o, 200);
                if (o + 2 < r.Length) { outp.IconBytes = r[o] | (r[o + 1] << 8) | (r[o + 2] << 16); o += 3; }
                return outp;
            }
            catch { return default; }
        }

        // Length-prefixed UTF-8, clamped twice (buffer AND our cap) and stripped of control characters.
        // ⚠ THE STRIP IS NOT COSMETIC. This is operator-authored text from a stranger's server heading into
        // our UI: newlines and zero-width characters let a MOTD forge extra rows or hide text inside a name.
        // strawberry asked for size limits "for security of server owners sending stuff to clients" -- the
        // size is only half of it, the shape is the other half.
        static string ReadBoundedString(byte[] r, ref int o, int cap)
        {
            if (o >= r.Length) return "";
            int n = r[o++];
            if (n <= 0) return "";
            if (n > cap) n = cap;
            if (o + n > r.Length) n = r.Length - o;
            if (n <= 0) return "";
            string v = System.Text.Encoding.UTF8.GetString(r, o, n);
            o += n;
            return Sanitize(v, cap);
        }

        /// <summary>
        /// Pull a server's icon over UGIQ, one chunk per request, blocking -- call it off the main thread.
        /// Returns null on anything unexpected rather than a partial image.
        ///
        /// ⭐ PULL, NOT PUSH, AND THAT IS THE SECURITY PROPERTY. Every reply is bounded by the request that
        /// asked for it, so fetching 60 KB costs the asker 60 KB. A spoofed source address therefore gains an
        /// attacker nothing -- they must send every byte they want reflected. It is the same rule the status
        /// reply follows, which is why the icon could not simply ride along in one datagram.
        /// </summary>
        static byte[] FetchIcon(string host, ushort port, int totalBytes, int capBytes = 64 * 1024)
        {
            if (totalBytes <= 0 || totalBytes > capBytes) return null;
            try
            {
                System.Net.IPAddress addr = System.Net.IPAddress.TryParse(host, out var lit)
                    ? lit
                    : System.Array.Find(System.Net.Dns.GetHostAddresses(host), a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                      ?? System.Net.Dns.GetHostAddresses(host)[0];
                var ep = new System.Net.IPEndPoint(addr, port);
                using var udp = new System.Net.Sockets.UdpClient();
                udp.Client.ReceiveTimeout = 1500;
                var buf = new byte[totalBytes];
                int got = 0, guard = 0;
                while (got < totalBytes)
                {
                    if (++guard > (totalBytes / 256) + 64) return null;   // bounded work: a server feeding short chunks forever is a hang, not a download
                    var req = new byte[IconReqBytes];
                    req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'I'; req[3] = (byte)'Q';
                    req[4] = (byte)(got & 0xFF); req[5] = (byte)((got >> 8) & 0xFF); req[6] = (byte)((got >> 16) & 0xFF);
                    udp.Send(req, req.Length, ep);
                    var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                    byte[] r = udp.Receive(ref from);
                    if (r.Length <= IconHeaderBytes || r[0] != (byte)'U' || r[1] != (byte)'G' || r[2] != (byte)'I' || r[3] != (byte)'R') return null;
                    int off = r[4] | (r[5] << 8) | (r[6] << 16);
                    int total = r[7] | (r[8] << 8) | (r[9] << 16);
                    if (off != got || total != totalBytes) return null;    // a reply for a different offset or a shifting total is not our transfer
                    int n = r.Length - IconHeaderBytes;                    // the datagram states the chunk length; there is no second field to disagree with it
                    if (n <= 0 || got + n > totalBytes) return null;
                    System.Array.Copy(r, IconHeaderBytes, buf, got, n);
                    got += n;
                }
                return buf;
            }
            catch { return null; }
        }

        /// <summary>
        /// Decode a server icon, or null. Bounded on BOTH axes: bytes at the fetch, and PIXELS here -- a few
        /// KB of PNG can still declare enormous dimensions, so a byte cap alone does not bound what the decode
        /// allocates. Anything that fails is simply not an icon; a server sending rubbish gets no picture.
        /// </summary>
        public static ImageTexture TryDecodeIcon(byte[] png, int maxW = 320, int maxH = 180)
        {
            if (png == null || png.Length == 0) return null;
            var img = new Image();
            if (img.LoadPngFromBuffer(png) != Error.Ok) return null;
            int w = img.GetWidth(), h = img.GetHeight();
            if (w <= 0 || h <= 0 || w > maxW || h > maxH) return null;
            return ImageTexture.CreateFromImage(img);
        }

        readonly System.Collections.Generic.Dictionary<ServerEntry, ImageTexture> _iconCache = new();
        int _iconGen;   // a slow fetch for a server the user already clicked away from must not overwrite the new one

        // Fetch + decode off the main thread, then assign only if this is still the selected server.
        void RequestIcon(ServerEntry sv, string host, ushort port, int totalBytes)
        {
            int gen = ++_iconGen;
            System.Threading.Tasks.Task.Run(() =>
            {
                byte[] png = FetchIcon(host, port, totalBytes);
                Callable.From(() =>
                {
                    if (gen != _iconGen || _selectedServer != sv) return;
                    var tex = TryDecodeIcon(png);
                    if (tex != null) _iconCache[sv] = tex;
                    if (_svIcon != null && IsInstanceValid(_svIcon)) { _svIcon.Texture = tex; _svIcon.Visible = tex != null; }
                }).CallDeferred();
            });
        }

        const int IconReqBytes = 1200;    // budget the server fills a chunk into; reply <= request, as everywhere here
        const int IconHeaderBytes = 10;   // magic(4) + offset(3) + total(3) -- must match UdpServerTransport

        /// <summary>Strip control/format characters and collapse whitespace, then hard-clamp the length.</summary>
        public static string Sanitize(string v, int maxChars)
        {
            if (string.IsNullOrEmpty(v)) return "";
            var sb = new System.Text.StringBuilder(v.Length);
            foreach (char c in v)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.Control
                    || cat == System.Globalization.UnicodeCategory.Format
                    || cat == System.Globalization.UnicodeCategory.LineSeparator
                    || cat == System.Globalization.UnicodeCategory.ParagraphSeparator) { sb.Append(' '); continue; }
                sb.Append(c);
            }
            string outp = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return outp.Length > maxChars ? outp.Substring(0, maxChars) : outp;
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
            // A live answer beats the hardcoded row: map and mode come from the SERVER when it told us,
            // because the row is a guess about somebody else's config and the status reply is the fact.
            bool live = _liveStatus.TryGetValue(sv, out var st);
            // The icon is pulled ONLY here, on select -- never during a browser refresh. That is what makes a
            // 320x180 image affordable: one transfer when a human asks about one server, not N transfers every
            // time the list repaints (strawberry 2026-09-15).
            if (_svIcon != null && IsInstanceValid(_svIcon))
            {
                if (_iconCache.TryGetValue(sv, out var cached)) { _svIcon.Texture = cached; _svIcon.Visible = cached != null; }
                else { _svIcon.Texture = null; _svIcon.Visible = false; if (live && st.IconBytes > 0) RequestIcon(sv, sv.Host, sv.Port, st.IconBytes); }
            }
            string map = live && !string.IsNullOrEmpty(st.Map) ? st.Map : sv.Map;
            string mode = live && st.HasFlags ? (st.Pvp ? "PvP" : "PvE") : (sv.Pvp ? "PvP" : "PvE");
            string status = mismatch ? "\u26A0 VERSION MISMATCH \u2014 cannot join" : (ok ? $"{players}/{max} players  \u00B7  {ping} ms" : "offline / no response");
            string text = $"Map:  {map}\nAddress:  {AddressText(sv.Host, sv.Port)}\nMode:  {mode}\nStatus:  {status}";
            if (live && st.MaxPing > 0) text += $"\nMax ping:  {st.MaxPing} ms";
            if (live && !string.IsNullOrEmpty(st.Motd)) text += $"\n\n{st.Motd}";   // already sanitised + clamped at the parse
            _svInfoDetail.Text = text;
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
