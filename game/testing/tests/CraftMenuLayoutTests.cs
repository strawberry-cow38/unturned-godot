using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>The crafting menu's layout against the vitals (strawberry 2026-10-05: "match the vertical list of
    /// categories' width to the width of the vitals (same w search bar, move that just above the craft queue. make
    /// the text and iconography (within elements bounds) of the craft menu bigger to fill space properly").
    ///
    /// Every edge is held against HUD.VitalsRect -- the same rectangle the bars are BUILT from -- so the check is
    /// "the column IS the vitals' column", not "the column is 589 px", which would only be true at one window size.
    /// It runs at two viewport widths for the same reason: a layout keyed to a constant passes the first and fails
    /// the second.
    ///
    /// ⚠ WHAT THIS CANNOT SEE: whether it LOOKS right. "Bigger, to fill the space" is a judgement; this checks the
    /// half of it that is measurable -- every unclipped string fits the control that draws it, with the font that
    /// draws it -- and the render is the other half.</summary>
    public sealed class CraftMenuLayoutTests : GameTest
    {
        public override string Name => "craft.layout";
        public override double TimeoutSimSeconds => 30;

        static bool Near(float a, float b) => Mathf.Abs(a - b) <= 1.5f;
        static string E(Rect2 r) => $"[{r.Position.X:0}..{r.End.X:0} x {r.Position.Y:0}..{r.End.Y:0}]";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.ResetForTests();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(4);
            var craft = p.DebugCraftMenu;
            var win = craft.GetViewport() as Window;
            var size0 = win?.Size ?? Vector2I.Zero;

            foreach (var w in new[] { 2560, 3440 })
            {
                if (win != null) win.ContentScaleSize = new Vector2I(w, 1440);
                p.ShowMenu(MenuNavbar.Tab.Craft);
                yield return Ticks(3);
                var r = craft.DebugRects();
                Vector2 vp = craft.GetViewport().GetVisibleRect().Size;
                var vit = HUD.VitalsRect(vp);
                string at = $"@{vp.X:0}x{vp.Y:0}";
                GD.Print($"[craft.layout] {at} vitals {E(vit)}  categories {E(r["categories"])}  grid {E(r["grid"])}  search {E(r["search"])}  queue {E(r["queue"])}  detail {E(r["detail"])}");
                T.Check($"{at} the canvas really is {w} wide ({vp.X:0}) -- else the second pass repeats the first", Near(vp.X, w));

                // ---- 1. THE CATEGORY COLUMN IS THE VITALS' COLUMN
                var cat = r["categories"];
                T.Check($"{at} categories {E(cat)} span the vitals' width {E(vit)}",
                        Near(cat.Position.X, vit.Position.X) && Near(cat.End.X, vit.End.X));
                T.Check($"{at} ...and stop above them ({cat.End.Y:0} <= {vit.Position.Y:0})", cat.End.Y <= vit.Position.Y);

                // ---- 2. THE SEARCH: vitals-wide, sitting on the craft queue
                var s = r["search"]; var q = r["queue"];
                T.Check($"{at} search {E(s)} is the vitals' width ({vit.Size.X:0})", Near(s.Size.X, vit.Size.X));
                T.Check($"{at} ...on the queue {E(q)}: left edges flush, a gutter between ({q.Position.Y - s.End.Y:0} px)",
                        Near(s.Position.X, q.Position.X) && q.Position.Y - s.End.Y > 0f && q.Position.Y - s.End.Y <= 16f);

                // ---- 3. THE MIDDLE IS ONE COLUMN: grid over search over queue, and none of it on the vitals
                var g = r["grid"]; var d = r["detail"];
                T.Check($"{at} grid {E(g)} lines up with the queue and ends above the search",
                        Near(g.Position.X, q.Position.X) && Near(g.End.X, q.End.X) && g.End.Y <= s.Position.Y);
                foreach (var (name, rect) in new[] { ("grid", g), ("search", s), ("queue", q), ("detail", d) })
                    T.Check($"{at} {name} {E(rect)} keeps off the vitals", !rect.Intersects(vit));
                T.Check($"{at} the columns do not overlap (cat|grid {cat.End.X:0}<{g.Position.X:0}, grid|detail {g.End.X:0}<{d.Position.X:0})",
                        cat.End.X < g.Position.X && g.End.X < d.Position.X);

                // ---- 4. THE TEXT FITS ITS BOUNDS, at the new sizes
                var bad = craft.DebugOverflows(out int measured);
                GD.Print($"[craft.layout] {at} text measured {measured}, over {bad.Count}");
                T.Check($"{at} every unclipped string fits its control ({measured} measured, {bad.Count} over: {string.Join("; ", bad)})",
                        measured >= 10 && bad.Count == 0);
            }

            if (win != null) win.ContentScaleSize = new Vector2I(2560, 1440);
            p.ShowMenu(MenuNavbar.Tab.Inventory);
            yield return Ticks(1);
        }
    }
}
