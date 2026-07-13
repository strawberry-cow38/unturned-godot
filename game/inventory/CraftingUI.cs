using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // The crafting menu: lists the recipes the player can make from their supplies (BlueprintRegistry.Applicable),
    // each with a Craft button that runs Crafting.DoCraft against the real PlayerInventory + refreshes. Styled like
    // InventoryUI (dimmed centred panel, default font + size overrides). Toggled by a keybind (PlayerController).
    public partial class CraftingUI : CanvasLayer
    {
        public PlayerInventory Inv;
        public PlayerController Player;

        const int PANELW = 660, PANELH = 660;

        Control _root;
        Panel _panel;
        Label _header;
        VBoxContainer _list;
        bool _open;
        public bool IsOpen => _open;

        public override void _Ready()
        {
            Layer = 11;
            Visible = false;

            _root = new Control();
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(_root);

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.72f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Ignore;
            _root.AddChild(dim);

            _panel = new Panel { CustomMinimumSize = new Vector2(PANELW, PANELH), Size = new Vector2(PANELW, PANELH) };
            _root.AddChild(_panel);

            _header = new Label { Text = "CRAFTING", Position = new Vector2(20, 14), Size = new Vector2(PANELW - 40, 30) };
            _header.AddThemeFontSizeOverride("font_size", 24);
            _panel.AddChild(_header);

            var scroll = new ScrollContainer { Position = new Vector2(14, 52), Size = new Vector2(PANELW - 28, PANELH - 66) };
            scroll.CustomMinimumSize = scroll.Size;
            _panel.AddChild(scroll);
            _list = new VBoxContainer { CustomMinimumSize = new Vector2(PANELW - 40, 0) };
            scroll.AddChild(_list);
        }

        public override void _Process(double delta)
        {
            if (_open && _panel != null)
                _panel.Position = new Vector2((_root.Size.X - PANELW) / 2f, (_root.Size.Y - PANELH) / 2f);
        }

        public void Toggle() { if (_open) Close(); else Open(); }
        public void Open() { _open = true; Visible = true; Refresh(); }
        public void Close() { _open = false; Visible = false; }

        public void Refresh()
        {
            foreach (Node c in _list.GetChildren()) c.QueueFree();
            if (Inv == null) return;
            var adapter = new Crafting.PlayerInvAdapter(Inv);
            var recipes = BlueprintRegistry.Applicable(adapter);
            _header.Text = $"CRAFTING  ·  {recipes.Count} recipe{(recipes.Count == 1 ? "" : "s")} available";
            if (recipes.Count == 0)
            {
                var empty = new Label { Text = "Nothing craftable from your current supplies." };
                empty.AddThemeFontSizeOverride("font_size", 15);
                _list.AddChild(empty);
                return;
            }
            int shown = 0;
            foreach (var bp in recipes)
            {
                if (shown++ >= 80) break;   // cap the list length
                _list.AddChild(MakeRow(bp));
            }
        }

        Control MakeRow(BlueprintDef bp)
        {
            var row = new PanelContainer { CustomMinimumSize = new Vector2(0, 44) };
            var hb = new HBoxContainer();
            row.AddChild(hb);

            var lbl = new Label { Text = Describe(bp), CustomMinimumSize = new Vector2(PANELW - 150, 0), VerticalAlignment = VerticalAlignment.Center };
            lbl.AddThemeFontSizeOverride("font_size", 14);
            lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            hb.AddChild(lbl);

            var btn = new Button { Text = "Craft", CustomMinimumSize = new Vector2(96, 36) };
            if (!Crafting.MeetsSkill(bp, Player?.Skills)) { btn.Disabled = true; btn.TooltipText = $"needs {bp.Skill} skill {bp.SkillLevel}"; }   // skill-locked recipe
            btn.Pressed += () => OnCraft(bp);
            hb.AddChild(btn);
            return row;
        }

        void OnCraft(BlueprintDef bp)
        {
            if (!Crafting.MeetsSkill(bp, Player?.Skills)) { GD.Print($"[craftui] blocked: needs {bp.Skill} skill {bp.SkillLevel}"); return; }   // skill gate (source EBlueprintSkill level)
            var adapter = new Crafting.PlayerInvAdapter(Inv);
            if (!Crafting.DoCraft(bp, adapter)) return;
            // RepairTargetItem restores the owned item's quality (target-op); other ops just consume->produce.
            if (bp.Operation == "RepairTargetItem" && ushort.TryParse(bp.OwnerItemId, out var oid))
                Inv.restoreQuality(oid, 100);
            GD.Print($"[craftui] crafted: {Describe(bp)}");
            Refresh();
        }

        // "Result  <  inputs" -- for a Craft show the output item; for a target-op (Repair/Salvage) show the operation + owned item.
        static string Describe(BlueprintDef bp)
        {
            var sb = new System.Text.StringBuilder();
            if (bp.Outputs.Count > 0)
            {
                var o = Assets.findByGuid(bp.Outputs[0].Guid);
                sb.Append(o?.itemName ?? "item");
                if (bp.Outputs[0].Amount > 1) sb.Append(" x").Append(bp.Outputs[0].Amount);
            }
            else
            {
                var owner = ushort.TryParse(bp.OwnerItemId, out var oid) ? Assets.find(oid) : null;
                sb.Append(string.IsNullOrEmpty(bp.Name) ? bp.Operation : bp.Name);
                if (owner != null) sb.Append(' ').Append(owner.itemName);
            }
            var parts = new List<string>();
            foreach (var i in bp.Inputs)
            {
                var a = Assets.findByGuid(i.Guid);
                parts.Add($"{i.Amount}x {a?.itemName ?? "?"}{(i.Consume ? "" : " (tool)")}");
            }
            if (parts.Count > 0) sb.Append("   <   ").Append(string.Join(", ", parts));
            if (bp.RequiresStation) sb.Append("   [station]");
            if (bp.RequiresSkill) sb.Append($"   [{bp.Skill} {bp.SkillLevel}]");
            return sb.ToString();
        }
    }
}
