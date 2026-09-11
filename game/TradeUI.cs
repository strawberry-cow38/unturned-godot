using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The vendor window (master 2026-09-11: "shop ui following the same theme as the inventory ui",
    /// then "the shop ui should be more of a trade ui. items for items").
    ///
    /// SO THERE IS NO MONEY IN IT. Retail pays vendors in experience; this pays them in goods. The vendor's
    /// `Buying` list stops being a list of things it will purchase and becomes an EXCHANGE RATE -- what each
    /// item is worth to this particular person -- and buying something means piling up goods until the pile
    /// covers the asking price. Chef Leonard buying raw tomatoes at 30 and selling cooked ones at 65 is a
    /// trade you can read off the screen without anybody explaining a currency.
    ///
    /// Three columns, left to right, in the order the trade happens: what they have, what you have put up,
    /// what they will take. The middle column is the only one that is not a list of facts -- it is the thing
    /// you are building -- so it sits between the two things you build it out of.
    ///
    /// A VIEW, like DialogueUI. TradeRules does the arithmetic and PlayerInventory holds the goods; this
    /// draws them and calls Settle. It opens OVER the conversation rather than replacing it, which is why
    /// closing it returns you to the person you were talking to instead of to the world.</summary>
    public partial class TradeUI : CanvasLayer
    {
        Control _root;
        PanelContainer _panel;
        Label _name, _desc, _status;
        VBoxContainer _sellList, _offerList, _bagList;
        Button _tradeBtn, _autoBtn;

        NpcVendorDef _vendor;
        NpcTradeLine _want;                            // the line being bought, or null
        readonly Dictionary<ushort, int> _offer = new();
        PlayerController _player;

        const int PanelW = 1180, ColW = 360, RowH = 42, IconPx = 32, ListH = 420, Pad = 18;

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

            _name = UITheme.Label(new Label(), UITheme.FontTitle, UITheme.Accent);
            col.AddChild(_name);
            // ⚠ AN AUTOWRAP LABEL NEEDS A WIDTH OR IT INVENTS ITS OWN HEIGHT. With none, this measured itself
            // against a 1 px column -- one character per line -- and reported a 637 px minimum, which the panel
            // dutifully grew to and then never gave back. It cost 632 px of dead space under the footer and
            // looked like a container bug. Same fix as DialogueUI's body: hand it the width to wrap against.
            _desc = UITheme.Label(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart,
                                              CustomMinimumSize = new Vector2(PanelW, 0) }, UITheme.FontLabel, UITheme.TextDim);
            col.AddChild(_desc);
            col.AddChild(new HSeparator());

            var cols = new HBoxContainer();
            cols.AddThemeConstantOverride("separation", 16);
            col.AddChild(cols);
            _sellList = Column(cols, "They're selling", "hand over goods to cover the price");
            _offerList = Column(cols, "Your offer", "click to take something back");
            _bagList = Column(cols, "They'll take", "click to add it to the offer");

            col.AddChild(new HSeparator());

            var foot = new HBoxContainer();
            foot.AddThemeConstantOverride("separation", 10);
            col.AddChild(foot);
            _status = UITheme.Label(new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                                                VerticalAlignment = VerticalAlignment.Center },
                                    UITheme.FontHeading, UITheme.TextDim);
            foot.AddChild(_status);

            _autoBtn = UITheme.Label(new Button { Text = "Offer for me" }, UITheme.FontBody, UITheme.Text);
            _autoBtn.Pressed += AutoFill;
            foot.AddChild(_autoBtn);

            _tradeBtn = UITheme.Label(new Button { Text = "Trade" }, UITheme.FontBody, UITheme.Text);
            _tradeBtn.Pressed += Settle;
            foot.AddChild(_tradeBtn);

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
            _want = null;
            _offer.Clear();
            _name.Text = TradeRules.PlainText(vendor?.Name) is { Length: > 0 } n ? n : "Trader";
            _desc.Text = TradeRules.PlainText(vendor?.Description);
            _root.Visible = true;
            Redraw();
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }

        // ---- what the player is holding, through the SAME adapter crafting spends against -------------------
        // A fourth private copy of "how many of these do I have" is a fourth chance to disagree with the bag.
        Crafting.IInv Inv => _player?.Inventory != null ? new Crafting.PlayerInvAdapter(_player.Inventory) : null;

        /// <summary>Distinct item ids in the player's own pages, in bag order. Ids rather than jars: a trade is
        /// about WHAT you hand over, and two half-stacks of the same thing are one line on this screen.</summary>
        List<ushort> BagIds()
        {
            var seen = new List<ushort>();
            var inv = _player?.Inventory;
            if (inv == null) return seen;
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
            {
                var page = inv.items[b];
                if (page == null) continue;
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var jar = page.getItem(i);
                    ushort id = jar?.item?.id ?? 0;
                    if (id != 0 && !seen.Contains(id)) seen.Add(id);
                }
            }
            return seen;
        }

        static string NameOf(ushort id) => Assets.find(id)?.itemName ?? $"#{id}";

        void Redraw()
        {
            foreach (var l in new[] { _sellList, _offerList, _bagList })
                foreach (var c in l.GetChildren()) ((Node)c).QueueFree();

            // ---- LEFT: what they have ----------------------------------------------------------------------
            foreach (var line in _vendor?.Selling ?? System.Array.Empty<NpcTradeLine>())
            {
                bool vehicle = line.Type == "Vehicle" || line.Item == 0;
                string title = vehicle ? (line.Spawnpoint?.Replace('_', ' ') ?? "Vehicle") : NameOf(line.Item);
                // ⚠ SAID OUT LOUD RATHER THAN HIDDEN. Vehicle lines need a spawn + a delivery point, which this
                // does not do yet; the Aircraft Hangar is seven of them. Dropping them from the list would make
                // a vendor look empty instead of look unfinished, and nobody would ever come back to it.
                var row = Row(vehicle ? null : InventoryUI.IconFor(line.Item), title,
                              vehicle ? "not deliverable yet" : $"{line.Cost}",
                              vehicle ? UITheme.Warn : UITheme.Accent, !vehicle);
                if (_want == line) row.AddThemeStyleboxOverride("normal", UITheme.Box(UITheme.Selected, UITheme.RadiusCell));
                var captured = line;
                if (!vehicle) row.Pressed += () => { _want = captured; _offer.Clear(); Redraw(); };
                _sellList.AddChild(row);
            }

            // ---- MIDDLE: the pile ---------------------------------------------------------------------------
            foreach (var kv in _offer)
            {
                int worth = TradeRules.ValueOf(_vendor, kv.Key) * kv.Value;
                var row = Row(InventoryUI.IconFor(kv.Key), $"{NameOf(kv.Key)}  x{kv.Value}", $"{worth}", UITheme.Good, true);
                ushort id = kv.Key;
                row.Pressed += () => { Take(id, -1); };
                _offerList.AddChild(row);
            }

            // ---- RIGHT: your bag, with their rate ------------------------------------------------------------
            foreach (ushort id in BagIds())
            {
                int unit = TradeRules.ValueOf(_vendor, id);
                int held = Inv?.Count(id) ?? 0;
                int offered = _offer.TryGetValue(id, out var o) ? o : 0;
                int spare = held - offered;
                bool takeable = unit > 0 && spare > 0;
                // Items they will NOT take stay on screen, dimmed. Hiding them reads as "where did my stuff go";
                // showing them with a dash is how the exchange rate teaches itself.
                var row = Row(InventoryUI.IconFor(id), $"{NameOf(id)}  x{spare}",
                              unit > 0 ? $"{unit} ea" : "won't take",
                              unit > 0 ? UITheme.TextBody : UITheme.TextDisabled, takeable);
                if (takeable) { ushort cid = id; row.Pressed += () => Take(cid, +1); }
                _bagList.AddChild(row);
            }

            UpdateStatus();
            CallDeferred(nameof(Layout));
        }

        void Take(ushort id, int delta)
        {
            int held = Inv?.Count(id) ?? 0;
            int have = _offer.TryGetValue(id, out var n) ? n : 0;
            int next = Mathf.Clamp(have + delta, 0, held);
            if (next == 0) _offer.Remove(id); else _offer[id] = next;
            Redraw();
        }

        void AutoFill()
        {
            if (_want == null) { _status.Text = "Pick something to buy first."; return; }
            var inv = Inv;
            var pile = TradeRules.AutoOffer(_vendor, _want, id => inv?.Count(id) ?? 0);
            if (pile == null)
            {
                // A partial pile would LOOK like progress and buy nothing. Say the shortfall instead.
                _status.Text = $"Nothing in your bag adds up to {_want.Cost}.";
                return;
            }
            _offer.Clear();
            foreach (var kv in pile) _offer[kv.Key] = kv.Value;
            Redraw();
        }

        void UpdateStatus()
        {
            int value = TradeRules.OfferValue(_vendor, _offer);
            if (_want == null)
            {
                _status.Text = "Pick something they're selling.";
                _status.AddThemeColorOverride("font_color", UITheme.TextDim);
                _tradeBtn.Disabled = true;
                _autoBtn.Disabled = true;
                return;
            }
            _autoBtn.Disabled = false;
            bool ok = TradeRules.CanAfford(_vendor, _want, _offer);
            _tradeBtn.Disabled = !ok;
            int over = value - _want.Cost;
            // Overpay is allowed (see TradeRules.CanAfford) so it has to be VISIBLE. Handing over more than the
            // asking price and getting no change is a fair trade only if you were told you were doing it.
            _status.Text = ok
                ? (over > 0 ? $"{NameOf(_want.Item)} for {value} — {over} over the asking price, no change given"
                            : $"{NameOf(_want.Item)} for {value}. Even.")
                : $"{value} of {_want.Cost} — {_want.Cost - value} short";
            _status.AddThemeColorOverride("font_color", ok ? (over > 0 ? UITheme.Warn : UITheme.Good) : UITheme.Bad);
        }

        void Settle()
        {
            if (_want == null || !TradeRules.CanAfford(_vendor, _want, _offer)) return;
            var inv = Inv;
            if (inv == null) return;
            // Take the goods BEFORE handing anything over. The bag is grid-backed and tryAddItem can fail on a
            // full bag; paying first means a refused delivery leaves the player short, which is the one outcome
            // a trade must never produce.
            foreach (var kv in _offer) inv.Remove(kv.Key, kv.Value);
            inv.Add(_want.Item, 1);
            Log.Print($"[trade] {TradeRules.PlainText(_vendor?.Name)}: {NameOf(_want.Item)} for {TradeRules.OfferValue(_vendor, _offer)}");
            _offer.Clear();
            _want = null;
            _player?.RefreshInventoryUI();
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
            _offer.Clear();
            _want = null;
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
        public int DebugOfferValue => TradeRules.OfferValue(_vendor, _offer);
        public bool DebugCanTrade => _tradeBtn != null && !_tradeBtn.Disabled;
        public void DebugSelect(int sellIndex)
        {
            var sell = _vendor?.Selling;
            if (sell != null && (uint)sellIndex < (uint)sell.Length) { _want = sell[sellIndex]; _offer.Clear(); Redraw(); }
        }
        public void DebugAutoFill() => AutoFill();
        public void DebugSettle() => Settle();
    }
}
