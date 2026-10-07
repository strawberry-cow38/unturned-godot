using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The vendor window (master 2026-09-11: "shop ui following the same theme as the inventory ui"), now
    /// a SHOP IN DOLLARS (strawberry 2026-10-07: "make npc vendors trade in $").
    ///
    /// Two columns: what they sell, priced in $, and what they buy, with what they pay you for each. Your wallet
    /// sits over both, because every decision on this screen is "can I afford it" or "what will I get". The
    /// barter pile that used to sit between them is gone -- money is the pile now. Prices are the vendor's own
    /// retail numbers, 1:1 (see TradeRules).
    ///
    /// A VIEW, like DialogueUI. In multiplayer and the singleplayer loopback the SERVER moves the goods and the
    /// money; this asks and then redraws when the bag echo lands. It opens OVER the conversation rather than
    /// replacing it, which is why closing it returns you to the person you were talking to.</summary>
    public partial class TradeUI : CanvasLayer
    {
        Control _root;
        PanelContainer _panel;
        Label _name, _desc, _wallet, _status;
        VBoxContainer _buyList, _sellList;
        Button _buyBtn, _sellOneBtn, _sellAllBtn;

        NpcVendorDef _vendor;
        NpcTradeLine _pick;            // the selected line, or null
        bool _pickSelling;             // ...on THEIR buying list (you are selling), else on their selling list
        PlayerController _player;
        string _drawn = "";            // what the bag looked like at the last draw -- see _Process

        const int PanelW = 1000, ColW = 480, RowH = 42, IconPx = 32, ListH = 420, Pad = 18;

        public override void _Ready()
        {
            Layer = 92;   // one above DialogueUI: the trade opens ON the conversation, it does not replace it
            _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);

            var dim = new ColorRect { Color = UITheme.Scrim, MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(dim);

            _panel = new PanelContainer();
            UITheme.Panel(_panel, solid: true);
            _root.AddChild(_panel);

            // The PanelContainer hands its child the whole box and UITheme.Panel carries no content margins --
            // the lesson the dialogue panel's first render taught, applied before it could happen twice.
            var pad = new MarginContainer();
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                pad.AddThemeConstantOverride(side, Pad);
            _panel.AddChild(pad);

            var col = new VBoxContainer { CustomMinimumSize = new Vector2(PanelW, 0) };
            col.AddThemeConstantOverride("separation", 10);
            pad.AddChild(col);

            var top = new HBoxContainer();
            col.AddChild(top);
            _name = UITheme.Label(new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, UITheme.FontTitle, UITheme.Accent);
            top.AddChild(_name);
            _wallet = UITheme.Label(new Label { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center },
                                    UITheme.FontHeading, UITheme.Good);
            top.AddChild(_wallet);
            // ⚠ AN AUTOWRAP LABEL NEEDS A WIDTH OR IT INVENTS ITS OWN HEIGHT. With none, this measured itself
            // against a 1 px column -- one character per line -- and reported a 637 px minimum, which the panel
            // dutifully grew to and then never gave back. Same fix as DialogueUI's body: hand it the width.
            _desc = UITheme.Label(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart,
                                              CustomMinimumSize = new Vector2(PanelW, 0) }, UITheme.FontLabel, UITheme.TextDim);
            col.AddChild(_desc);
            col.AddChild(new HSeparator());

            var cols = new HBoxContainer();
            cols.AddThemeConstantOverride("separation", 16);
            col.AddChild(cols);
            _buyList = Column(cols, "They're selling", "pick one, then Buy");
            _sellList = Column(cols, "They're buying", "what they pay you for each");

            col.AddChild(new HSeparator());

            var foot = new HBoxContainer();
            foot.AddThemeConstantOverride("separation", 10);
            col.AddChild(foot);
            _status = UITheme.Label(new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                                                VerticalAlignment = VerticalAlignment.Center,
                                                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis },
                                    UITheme.FontHeading, UITheme.TextDim);
            foot.AddChild(_status);

            _buyBtn = UITheme.Label(new Button { Text = "Buy" }, UITheme.FontBody, UITheme.Text);
            _buyBtn.Pressed += Buy;
            foot.AddChild(_buyBtn);
            _sellOneBtn = UITheme.Label(new Button { Text = "Sell 1" }, UITheme.FontBody, UITheme.Text);
            _sellOneBtn.Pressed += () => Sell(1);
            foot.AddChild(_sellOneBtn);
            _sellAllBtn = UITheme.Label(new Button { Text = "Sell all" }, UITheme.FontBody, UITheme.Text);
            _sellAllBtn.Pressed += () => Sell(Held(_pick?.Item ?? 0));
            foot.AddChild(_sellAllBtn);

            var close = UITheme.Label(new Button { Text = "Close  (Esc)" }, UITheme.FontBody, UITheme.TextDim);
            close.Pressed += Close;
            foot.AddChild(close);

            GetViewport().SizeChanged += Layout;
            _panel.Resized += Layout;   // a PanelContainer does not know its size until its children lay out
        }

        /// <summary>One titled column. The subtitle says what CLICKING a row does, because three lists that all
        /// look alike and behave differently is the part of a trade window people get wrong.</summary>
        VBoxContainer Column(Control parent, string title, string subtitle)
        {
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(ColW, 0) };
            box.AddThemeConstantOverride("separation", 4);
            parent.AddChild(box);
            box.AddChild(UITheme.Label(new Label { Text = title }, UITheme.FontHeading, UITheme.Text));
            box.AddChild(UITheme.Label(new Label { Text = subtitle }, UITheme.FontSmall, UITheme.TextDim));

            var strip = new PanelContainer { CustomMinimumSize = new Vector2(ColW, ListH) };
            UITheme.Strip(strip);
            box.AddChild(strip);

            // A ScrollContainer whose minimum size follows its CONTENT does not scroll, it just gets taller.
            // Pinned to the column box so a 22-item bag scrolls inside ListH instead of stretching the window.
            var scroll = new ScrollContainer
            {
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                CustomMinimumSize = new Vector2(ColW, ListH),
                ClipContents = true,
            };
            strip.AddChild(scroll);
            var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(list);
            return list;
        }

        void Layout()
        {
            if (_panel == null) return;
            var vp = GetViewport().GetVisibleRect().Size;
            var sz = _panel.Size;
            _panel.Position = new Vector2((vp.X - sz.X) * 0.5f, Mathf.Max(24f, (vp.Y - sz.Y) * 0.5f));
            if (System.Environment.GetEnvironmentVariable("UG_UIGEOM") == "1")
            {
                Log.Print($"[tradeui] viewport {vp.X}x{vp.Y}  panel {sz.X}x{sz.Y} at {_panel.Position}");
                // Walk the box and say what each row COSTS. A panel that is taller than the sum of what you
                // put in it has one child lying about its minimum, and the only way to find out which is to
                // ask all of them rather than reason about which container is at fault.
                var probe = _panel.GetChild(0).GetChild(0);
                foreach (var c in probe.GetChildren())
                    if (c is Control ctl)
                        Log.Print($"[tradeui]   {ctl.GetType().Name,-18} size {ctl.Size.X,6:0}x{ctl.Size.Y,-6:0} min {ctl.GetCombinedMinimumSize().Y:0}");
            }
        }

        public bool IsOpen => _root != null && _root.Visible;

        public void Open(PlayerController player, NpcVendorDef vendor)
        {
            _player = player;
            _vendor = vendor;
            _pick = null;
            _name.Text = TradeRules.PlainText(vendor?.Name) is { Length: > 0 } n ? n : "Trader";
            _desc.Text = TradeRules.PlainText(vendor?.Description);
            _root.Visible = true;
            Redraw();
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }

        // ---- the bag, read straight off it: getItemCount sums AMOUNTS, so for money it IS the dollar figure ----
        int Wallet => _player?.Inventory?.getItemCount(Currency.StackId) ?? 0;
        int Held(ushort id) => id == 0 ? 0 : _player?.Inventory?.getItemCount(id) ?? 0;

        static string NameOf(ushort id) => Assets.find(id)?.itemName ?? $"#{id}";

        /// <summary>A fingerprint of everything this screen draws from the bag. In MP the result of a trade arrives
        /// as an inventory echo some ticks after the click, so the window watches for it instead of guessing when.</summary>
        string BagPrint()
        {
            var sb = new System.Text.StringBuilder().Append(Wallet);
            foreach (var l in _vendor?.Buying ?? System.Array.Empty<NpcTradeLine>()) sb.Append(',').Append(Held(l.Item));
            return sb.ToString();
        }

        public override void _Process(double delta)
        {
            if (IsOpen && BagPrint() != _drawn) Redraw();
        }

        void Redraw()
        {
            foreach (var l in new[] { _buyList, _sellList })
                foreach (var c in l.GetChildren()) ((Node)c).QueueFree();
            int wallet = Wallet;
            _wallet.Text = $"Your wallet: ${wallet}";

            // ---- LEFT: what they sell, in dollars ----------------------------------------------------------
            foreach (var line in _vendor?.Selling ?? System.Array.Empty<NpcTradeLine>())
            {
                bool vehicle = line.Type == "Vehicle" || line.Item == 0;
                string title = vehicle ? (line.Spawnpoint?.Replace('_', ' ') ?? "Vehicle") : NameOf(line.Item);
                // ⚠ SAID OUT LOUD RATHER THAN HIDDEN. Vehicle lines need a spawn + a delivery point, which this
                // does not do yet; the Aircraft Hangar is seven of them. Dropping them from the list would make
                // a vendor look empty instead of look unfinished, and nobody would ever come back to it.
                bool afford = TradeRules.CanBuy(line, wallet);
                var row = Row(vehicle ? null : InventoryUI.IconFor(line.Item), title,
                              vehicle ? "not deliverable yet" : $"${line.Cost}",
                              vehicle ? UITheme.Warn : afford ? UITheme.Accent : UITheme.Bad, !vehicle);
                if (_pick == line) row.AddThemeStyleboxOverride("normal", UITheme.Box(UITheme.Selected, UITheme.RadiusCell));
                var captured = line;
                if (!vehicle) row.Pressed += () => { _pick = captured; _pickSelling = false; Redraw(); };
                _buyList.AddChild(row);
            }

            // ---- RIGHT: what they buy, and what you have of it ---------------------------------------------
            // Every line they buy is listed, held or not -- that is how you learn what is worth carrying here.
            // Lines you hold none of are dimmed and cannot be picked.
            foreach (var line in _vendor?.Buying ?? System.Array.Empty<NpcTradeLine>())
            {
                if (line.Item == 0 || Currency.IsCurrency(line.Item)) continue;
                int held = Held(line.Item);
                var row = Row(InventoryUI.IconFor(line.Item), held > 0 ? $"{NameOf(line.Item)}  x{held}" : NameOf(line.Item),
                              $"${line.Cost} ea", held > 0 ? UITheme.Good : UITheme.TextDisabled, held > 0);
                if (_pick == line) row.AddThemeStyleboxOverride("normal", UITheme.Box(UITheme.Selected, UITheme.RadiusCell));
                var captured = line;
                if (held > 0) row.Pressed += () => { _pick = captured; _pickSelling = true; Redraw(); };
                _sellList.AddChild(row);
            }

            UpdateStatus(wallet);
            _drawn = BagPrint();
            CallDeferred(nameof(Layout));
        }

        void UpdateStatus(int wallet)
        {
            _buyBtn.Disabled = _sellOneBtn.Disabled = _sellAllBtn.Disabled = true;
            _buyBtn.Text = "Buy"; _sellOneBtn.Text = "Sell 1"; _sellAllBtn.Text = "Sell all";
            if (_pick == null)
            {
                Say("Pick something to buy or sell.", UITheme.TextDim);
                return;
            }
            string name = NameOf(_pick.Item);
            if (!_pickSelling)
            {
                bool ok = TradeRules.CanBuy(_pick, wallet);
                _buyBtn.Disabled = !ok;
                _buyBtn.Text = $"Buy  ${_pick.Cost}";
                if (ok) Say($"{name} for ${_pick.Cost}", UITheme.Good);
                else Say($"{name} is ${_pick.Cost} -- you're ${_pick.Cost - wallet} short", UITheme.Bad);
                return;
            }
            int held = Held(_pick.Item);
            if (held <= 0) { _pick = null; Say("Pick something to buy or sell.", UITheme.TextDim); return; }   // sold the last one
            _sellOneBtn.Disabled = false;
            _sellOneBtn.Text = $"Sell 1  +${_pick.Cost}";
            _sellAllBtn.Disabled = held < 2;
            _sellAllBtn.Text = $"Sell all  +${(long)_pick.Cost * System.Math.Min(held, ServerNpcsMax)}";
            Say($"{name}: ${_pick.Cost} each, you have {held}", UITheme.Good);
        }

        // The server refuses a count over this in one message; "Sell all" of a bigger pile takes more than one press.
        const int ServerNpcsMax = UnturnedGodot.Net.ServerNpcs.MaxTradeCount;

        void Say(string text, Color c)
        {
            _status.Text = text;
            _status.AddThemeColorOverride("font_color", c);
        }

        void Buy()
        {
            if (_pick == null || _pickSelling || !TradeRules.CanBuy(_pick, Wallet)) return;
            int index = System.Array.IndexOf(_vendor.Selling, _pick);
            if (index < 0) return;
            // ⚠ IN MP THE SERVER MOVES THE MONEY AND THE GOODS. The CanBuy above is the client's, there so the button
            // greys out sensibly; the server re-checks the wallet it actually owns and places the item before it
            // charges, so a full bag costs nothing.
            if (_player?.NetNpcTrade != null) { _player.NetNpcTrade(_vendor.Guid, false, (byte)index, 1); return; }
            var inv = _player?.Inventory;
            if (inv == null) return;
            if (!inv.tryAddItem(new Item(_pick.Item))) { Say("No room in your bag.", UITheme.Bad); return; }   // placed BEFORE it is paid for
            if (_pick.Cost > 0) inv.removeItemAmount(Currency.StackId, _pick.Cost);
            Log.Print($"[trade] {TradeRules.PlainText(_vendor?.Name)}: bought {NameOf(_pick.Item)} for ${_pick.Cost}");
            _player.RefreshInventoryUI();
            Redraw();
        }

        void Sell(int count)
        {
            if (_pick == null || !_pickSelling || count <= 0) return;
            count = System.Math.Min(System.Math.Min(count, Held(_pick.Item)), ServerNpcsMax);
            int index = System.Array.IndexOf(_vendor.Buying, _pick);
            if (count <= 0 || index < 0) return;
            if (_player?.NetNpcTrade != null) { _player.NetNpcTrade(_vendor.Guid, true, (byte)index, (ushort)count); return; }
            var inv = _player?.Inventory;
            if (inv == null) return;
            inv.removeItemAmount(_pick.Item, count);
            Currency.Pay(inv, _pick.Cost * count);
            Log.Print($"[trade] {TradeRules.PlainText(_vendor?.Name)}: sold {count} {NameOf(_pick.Item)} for ${_pick.Cost * count}");
            _player.RefreshInventoryUI();
            Redraw();
        }

        public override void _Input(InputEvent e)
        {
            if (!IsOpen) return;
            if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
        }

        /// <summary>Shut the window and go back to the CONVERSATION, not to the world -- the dialogue panel is
        /// still up on the layer underneath, which is the whole reason this one sits above it.</summary>
        public void Close()
        {
            if (_root != null) _root.Visible = false;
            _pick = null;
            if (_player != null && GodotObject.IsInstanceValid(_player) && _player.CurrentDialogue == null)
                Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // ---- row -------------------------------------------------------------------------------------------
        /// <summary>Icon, name, and a right-aligned number. The children are MouseFilter.Ignore so the click
        /// lands on the Button underneath them rather than on whichever label happened to be under the cursor.</summary>
        static Button Row(Texture2D icon, string title, string right, Color rightColor, bool enabled)
        {
            var b = new Button { Flat = true, Disabled = !enabled, CustomMinimumSize = new Vector2(0, RowH) };
            var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            h.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            h.AddThemeConstantOverride("separation", 8);
            b.AddChild(h);

            var cell = new PanelContainer { CustomMinimumSize = new Vector2(IconPx, IconPx), MouseFilter = Control.MouseFilterEnum.Ignore };
            UITheme.Cell(cell, icon != null);
            h.AddChild(cell);
            if (icon != null)
                cell.AddChild(new TextureRect
                {
                    Texture = icon,
                    // ⚠ IgnoreSize, or the icon sets the floor for everything above it. A TextureRect's MINIMUM
                    // size is its texture's full size, and an item icon is a couple of hundred pixels -- so the
                    // cell grew to fit it, the row grew to fit the cell, the list grew, and the panel came out
                    // 1252 px tall with the names hidden behind the pictures. The cell's CustomMinimumSize is
                    // meant to be what decides this; IgnoreSize is what lets it.
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    TextureFilter = CanvasItem.TextureFilterEnum.Nearest,   // blocky Unturned pixels, like the bag
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                });

            h.AddChild(UITheme.Label(new Label
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            }, UITheme.FontBody, enabled ? UITheme.Text : UITheme.TextDisabled));

            h.AddChild(UITheme.Label(new Label
            {
                Text = right,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                CustomMinimumSize = new Vector2(96, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            }, UITheme.FontBody, rightColor));
            return b;
        }

        // ---- test seams: what a trade window IS is what it shows and what it will let you do ----------------
        public string DebugVendor => _name?.Text ?? "";
        public string DebugStatus => _status?.Text ?? "";
        public string DebugWallet => _wallet?.Text ?? "";
        public bool DebugCanBuy => _buyBtn != null && !_buyBtn.Disabled;
        public bool DebugCanSell => _sellOneBtn != null && !_sellOneBtn.Disabled;
        /// <summary>The right-hand text of each row as drawn, per list -- the prices the player actually reads.</summary>
        public List<string> DebugRowPrices(bool buyingList)
        {
            var outp = new List<string>();
            foreach (var c in (buyingList ? _sellList : _buyList).GetChildren())
                if (c is Button b && !b.IsQueuedForDeletion() && b.GetChild(0) is HBoxContainer h && h.GetChild(h.GetChildCount() - 1) is Label l) outp.Add(l.Text);
            return outp;
        }
        public void DebugPickSelling(int i) { var l = _vendor?.Selling; if (l != null && (uint)i < (uint)l.Length) { _pick = l[i]; _pickSelling = false; Redraw(); } }
        public void DebugPickBuying(int i) { var l = _vendor?.Buying; if (l != null && (uint)i < (uint)l.Length) { _pick = l[i]; _pickSelling = true; Redraw(); } }
        public void DebugPressBuy() { if (DebugCanBuy) _buyBtn.EmitSignal(BaseButton.SignalName.Pressed); }
        public void DebugPressSellOne() { if (DebugCanSell) _sellOneBtn.EmitSignal(BaseButton.SignalName.Pressed); }
        public void DebugPressSellAll() { if (_sellAllBtn != null && !_sellAllBtn.Disabled) _sellAllBtn.EmitSignal(BaseButton.SignalName.Pressed); }
    }
}
