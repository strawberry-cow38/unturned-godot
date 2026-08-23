using Godot;

namespace UnturnedGodot
{
    /// <summary>One palette, one type scale, one set of shapes for every panel in the game.
    ///
    /// WHY THIS EXISTS. Each screen grew its own near-miss of the same look: the inventory panel was
    /// rgba(0.10,0.12,0.15,0.93), the crafting menu 0.08/0.10/0.13/0.97, the skills panel a third value;
    /// muted label text was 0.55/0.56/0.60 in one file and 0.60/0.60/0.64 in another; five different
    /// near-blacks were in use for the same job. None of those differences were decisions — they are what
    /// happens when six screens are written months apart, and the result reads as six apps rather than one.
    ///
    /// The reference is the INVENTORY (strawberry, 2026-08-23: "standardize the look ... based off the
    /// inventory ui"), so its values are the ones that survived; everything else moves to match it.
    ///
    /// HOW TO USE IT. Prefer the helpers over reading the raw colours — `UITheme.Panel(p)` rather than
    /// building a StyleBoxFlat by hand — because the helpers are what keep the RADII and MARGINS consistent
    /// too, and those drift just as badly as colour. Reach for a raw token only when styling something the
    /// helpers do not cover, and if you find yourself doing that twice, add a helper.
    ///
    /// A note on what does NOT belong here: per-item rarity colours, condition/quality gradients and skill
    /// colours are DATA, not chrome — they encode what a thing is, and they are already derived in
    /// ItemTool. Leave them alone. This type owns the surface the data is drawn on.</summary>
    public static class UITheme
    {
        // ---- the two knobs -----------------------------------------------------------------------------
        // These, and Neutral() below, moved here from InventoryUI. They are the whole tuning surface for the
        // UI's brightness and transparency, and they now apply to EVERY screen rather than one.
        //
        // READ THIS BEFORE "FIXING" THE GREY. The desaturation is not drift, it was asked for:
        // strawberry, 2026-08-04 -- "increase the transparency of the entire inventory ui, nd remove the
        // blue tint and tint it more white-ish/ very light gray." I nearly reverted it, because a render
        // shows pale-grey bars next to blue-slate slots and the grey reads like the mistake. It is the
        // opposite: the grey is the intent, and the blue-slate bits are the ones that never got the
        // treatment. That split inside a single screen is exactly what "standardize the look" is about.
        //
        //   UiLighten     0 = original brightness, 1 = pure white.
        //   UiAlphaScale  multiplies every alpha; below 1 is more see-through.
        public const float UiLighten = 0.45f, UiAlphaScale = 0.72f;

        /// <summary>Desaturate to luminance, then lift toward white.
        ///
        /// DERIVED rather than hand-picked, so the palette keeps its internal ORDERING: the nav strip stays
        /// darker than a header bar, a lit tab stays brighter than an unlit one. Hand-writing a dozen final
        /// greys loses that, and it surfaces as "the tabs are hard to tell apart", which reads as a
        /// different bug entirely.
        ///
        /// Rec.709 luma, not an RGB average: averaging makes blue-heavy swatches come out too bright
        /// relative to their neighbours and scrambles the ordering.</summary>
        public static Color Neutral(float r, float g, float b, float a)
        {
            float y = r * 0.2126f + g * 0.7152f + b * 0.0722f;
            float v = Mathf.Lerp(y, 1f, UiLighten);
            return new Color(v, v, v, Mathf.Clamp(a * UiAlphaScale, 0f, 1f));
        }

        // ---- surfaces ----------------------------------------------------------------------------------
        // Every value is the ORIGINAL authored colour passed through Neutral(), so the source colours stay
        // readable as intent and the treatment stays in one place.
        /// <summary>Panel body / backdrop. Deliberately see-through: the world shows behind it.</summary>
        public static Color Bg => Neutral(0.10f, 0.13f, 0.18f, 0.42f);
        /// <summary>An OPAQUE panel. Deliberately NOT Neutral()'d.
        ///
        /// Neutral() lifts toward white by 0.45, which is right for a translucent overlay — the dark world
        /// behind it supplies the darkness, and lifting only removes the blue cast. Run an opaque panel
        /// through the same function and you get a mid-grey slab at v=0.52, on which TextDim (0.55) is
        /// invisible. I shipped exactly that for one render: the crafting menu came out a flat pale sheet
        /// with its ingredient list unreadable, and it BUILT fine and had no blue left in it, so nothing
        /// short of looking at the image would have caught it.
        ///
        /// So: neutral in HUE (no blue tint, which is what was actually asked for) but genuinely dark in
        /// VALUE, because contrast against the text is what an opaque surface owes.</summary>
        public static Color BgSolid => new(0.13f, 0.13f, 0.14f, 0.96f);
        /// <summary>An opaque raised strip — the header inside a solid panel.</summary>
        public static Color BarSolid => new(0.19f, 0.19f, 0.20f, 0.98f);
        /// <summary>A raised strip inside a panel: page headers, toolbars, the category bar.</summary>
        public static Color Bar => Neutral(0.17f, 0.24f, 0.32f, 0.78f);
        /// <summary>The navbar strip. Darker than a header bar, and that ordering is load-bearing.</summary>
        public static Color Nav => Neutral(0.13f, 0.18f, 0.24f, 0.80f);
        /// <summary>An occupied cell.</summary>
        public static Color Slot => Neutral(0.22f, 0.29f, 0.37f, 0.62f);
        /// <summary>An empty cell: LIGHT and see-through, so a grid reads as holes rather than tiles.</summary>
        public static Color SlotEmpty => Neutral(0.62f, 0.72f, 0.84f, 0.30f);
        /// <summary>The lit/open tab, and the selected row in a list.</summary>
        public static Color Selected => Neutral(0.55f, 0.62f, 0.70f, 0.72f);
        /// <summary>Unlit tabs and icon buttons.</summary>
        public static Color Hover => Neutral(0.40f, 0.48f, 0.56f, 0.70f);
        /// <summary>Backing behind a 3D stage (the paperdoll), barely there on purpose.</summary>
        public static Color Stage => Neutral(0.08f, 0.11f, 0.15f, 0.30f);
        /// <summary>The dark chip drawn ON TOP of an icon. NOT neutralised and NOT lightened — it has to
        /// stay readable over an arbitrary sprite, which is the one job a lifted grey cannot do.</summary>
        public static Color Chip => new(0f, 0f, 0f, 0.72f);
        /// <summary>Full-screen scrim behind a modal that owns the screen.</summary>
        public static Color Scrim => new(0f, 0f, 0f, 0.72f);
        /// <summary>A lighter scrim for a modal you work THROUGH rather than over: the attachment menu sits
        /// over the gun you are modifying, and the full Scrim would hide the thing the menu is about.</summary>
        public static Color ScrimLight => new(0f, 0f, 0f, 0.35f);

        // ---- text --------------------------------------------------------------------------------------
        /// <summary>Primary reading text and item names.</summary>
        public static Color Text => new(0.88f, 0.88f, 0.91f);
        /// <summary>Body copy — descriptions, stat lines.</summary>
        public static Color TextBody => new(0.79f, 0.79f, 0.79f);
        /// <summary>Secondary text: section headers, counts, anything the eye should skip.</summary>
        public static Color TextDim => new(0.55f, 0.56f, 0.60f);
        /// <summary>Text on a disabled or unaffordable control. Distinct from TextDim — dim is "less
        /// important", this is "you cannot have it", and conflating them is why unaffordable recipes used
        /// to read as merely quiet.</summary>
        public static Color TextDisabled => new(0.40f, 0.41f, 0.45f);

        // ---- semantic ----------------------------------------------------------------------------------
        /// <summary>The one accent. Used for the player's own name, currency, and the single most important
        /// number on a screen. Spending it on more than that is what makes an accent stop working.</summary>
        public static Color Accent => new(1f, 0.84f, 0.22f);
        /// <summary>Affordable, satisfied, succeeded.</summary>
        public static Color Good => new(0.62f, 0.82f, 0.60f);
        /// <summary>A refusal the player should act on: unaffordable, incompatible, full, blocked.
        /// This is the colour a red-out state takes.</summary>
        public static Color Bad => new(0.86f, 0.52f, 0.46f);
        /// <summary>Caution short of refusal — nearly full, nearly broken, nearly expired.</summary>
        public static Color Warn => new(0.95f, 0.78f, 0.20f);
        /// <summary>Hairline separators and the outline on a raised card.</summary>
        public static Color Border => new(0f, 0f, 0f, 0.5f);

        // ---- drag-and-drop + progress states -----------------------------------------------------------
        // Requested by cow tools 2026-08-23 for the magazine load/unload wheel. Named for the JOB rather
        // than the colour, so a later palette change does not need every call site revisited.
        //
        // Note that the three REFUSALS deliberately share one colour. "Magazine full", "Incompatible" and
        // "Unload first" are all "this drop will not happen" — the player reads the reason from the text,
        // and giving each its own shade would spend three colours teaching a distinction the words already
        // make, while making none of them mean "blocked" reliably.
        /// <summary>A drop that will succeed — the tint filling a valid target.
        ///
        /// These are MORE SATURATED than the chrome colours above, deliberately. Good/Bad up there are for
        /// text and borders sitting inside a panel, where a vivid colour shouts. These are a transient
        /// signal painted over an item icon during a drag, competing with the icon's own colours, and they
        /// have to read in the quarter-second the player is looking. Values came from cow tools tuning them
        /// against a live drag (2026-08-23) rather than from me picking them off a palette — they were
        /// right and my muted versions were not, so the tokens took theirs.</summary>
        public static Color DropOkFill => new(0.30f, 0.85f, 0.40f, 0.30f);
        /// <summary>The outline on a valid drop target.</summary>
        public static Color DropOkEdge => new(0.45f, 1f, 0.55f, 0.90f);
        /// <summary>Any refused drop: full, incompatible, would-mix.
        ///
        /// One colour for all three on purpose. "Magazine full", "Incompatible" and "Unload first" are all
        /// "this drop will not happen"; the player reads WHICH from the text. Giving each its own shade
        /// spends three colours teaching a distinction the words already make, and leaves none of them
        /// reliably meaning "blocked".</summary>
        public static Color DropBlockedFill => new(0.90f, 0.25f, 0.22f, 0.34f);
        /// <summary>The outline on a refused drop target.</summary>
        public static Color DropBlockedEdge => new(1f, 0.42f, 0.36f, 0.95f);
        /// <summary>The filled arc of a progress ring while LOADING.</summary>
        public static Color WheelLoad => new(0.35f, 0.90f, 0.45f);
        /// <summary>The arc while UNLOADING. Amber, not green, because the two directions look identical in
        /// motion otherwise — and "am I filling or emptying this?" is the one thing the ring must answer at
        /// a glance.</summary>
        public static Color WheelUnload => new(0.95f, 0.45f, 0.20f);
        /// <summary>The unfilled remainder of a ring. Dim rather than absent, so the ring shows CAPACITY
        /// even when nearly empty — a 30-round mag and a 10-round mag should differ at 2/x.</summary>
        public static Color WheelEmpty => new(1f, 1f, 1f, 0.18f);

        // ---- shape + scale -----------------------------------------------------------------------------
        // Two radii, not five. A panel and the cells inside it should not each pick their own curvature.
        public const int RadiusPanel = 6;
        public const int RadiusCell = 4;
        public const int RadiusChip = 3;
        public const int BorderWidth = 2;
        public const int PadPanel = 10;
        public const int PadCell = 4;
        public const int Gap = 6;

        // A type scale, so "slightly bigger" stops being a per-file guess. Every size in the UI should be
        // one of these.
        public const int FontTitle = 20;
        public const int FontHeading = 16;
        public const int FontBody = 13;
        public const int FontLabel = 12;
        public const int FontSmall = 11;
        public const int FontTiny = 9;

        // ---- helpers -----------------------------------------------------------------------------------
        public static StyleBoxFlat Box(Color bg, int radius = RadiusCell, Color? border = null, int borderWidth = 0)
        {
            var sb = new StyleBoxFlat { BgColor = bg };
            sb.SetCornerRadiusAll(radius);
            if (border.HasValue && borderWidth > 0)
            {
                sb.BorderColor = border.Value;
                sb.SetBorderWidthAll(borderWidth);
            }
            return sb;
        }

        /// <summary>Style a Control as a panel body. Works on anything that takes a "panel" stylebox.</summary>
        public static void Panel(Control c, bool solid = false, int radius = RadiusPanel)
            => c.AddThemeStyleboxOverride("panel", Box(solid ? BgSolid : Bg, radius));

        /// <summary>A raised strip inside a panel.</summary>
        public static void Strip(Control c, int radius = RadiusCell)
            => c.AddThemeStyleboxOverride("panel", Box(Bar, radius));

        /// <summary>A grid cell. `filled` picks the occupied vs empty treatment; pass an accent to outline
        /// it (rarity, or a red-out state).</summary>
        public static void Cell(Control c, bool filled, Color? outline = null)
            => c.AddThemeStyleboxOverride("panel",
                   Box(filled ? Slot : SlotEmpty, RadiusCell, outline, outline.HasValue ? BorderWidth : 0));

        /// <summary>The dark chip that sits on top of an icon. Carries its own L/R content margin so the
        /// text is not jammed against the rounded corners.</summary>
        public static StyleBoxFlat ChipBox(Color outline)
        {
            var sb = Box(Chip, RadiusChip, outline, 1);
            sb.ContentMarginLeft = 4;
            sb.ContentMarginRight = 4;
            return sb;
        }

        /// <summary>Set a label's size and colour in one call. Almost every AddThemeColorOverride /
        /// AddThemeFontSizeOverride pair in the UI is doing exactly this.</summary>
        public static T Label<T>(T l, int size, Color? color = null) where T : Control
        {
            l.AddThemeFontSizeOverride("font_size", size);
            l.AddThemeColorOverride("font_color", color ?? Text);
            return l;
        }

        /// <summary>Text drawn over an icon or the world, where the background is unknown. The outline is
        /// what makes it legible on a light sprite; without it a white count vanishes over snow.</summary>
        public static T LabelOutlined<T>(T l, int size, Color? color = null) where T : Control
        {
            Label(l, size, color ?? Colors.White);
            l.AddThemeColorOverride("font_outline_color", Colors.Black);
            l.AddThemeConstantOverride("outline_size", 4);
            return l;
        }

        /// <summary>Style a text field. An un-themed LineEdit renders in Godot's DEFAULT light chrome — a
        /// pale slab in the middle of a dark panel — and that is exactly the "one screen looks like a
        /// different app" effect, arriving from a control nobody remembered to style rather than from a
        /// colour anyone chose.</summary>
        public static void Field(LineEdit e)
        {
            e.AddThemeStyleboxOverride("normal", Box(new Color(0.09f, 0.09f, 0.10f, 0.98f), RadiusCell));
            e.AddThemeStyleboxOverride("focus", Box(new Color(0.09f, 0.09f, 0.10f, 0.98f), RadiusCell, Accent, 1));
            e.AddThemeFontSizeOverride("font_size", FontBody);
            e.AddThemeColorOverride("font_color", Text);
            e.AddThemeColorOverride("font_placeholder_color", TextDim);
            e.AddThemeColorOverride("caret_color", Accent);
        }

        /// <summary>Style a Button across all of its states in one call, so a button cannot end up with a
        /// hover colour from one screen and a pressed colour from another.</summary>
        public static void Button(Button b, bool primary = false)
        {
            Color face = primary ? Selected : Bar;
            b.AddThemeStyleboxOverride("normal", Box(face, RadiusCell));
            b.AddThemeStyleboxOverride("hover", Box(Hover, RadiusCell));
            b.AddThemeStyleboxOverride("pressed", Box(Selected, RadiusCell));
            b.AddThemeStyleboxOverride("focus", Box(face, RadiusCell, Accent, 1));
            b.AddThemeStyleboxOverride("disabled", Box(SlotEmpty, RadiusCell));
            b.AddThemeFontSizeOverride("font_size", FontBody);
            b.AddThemeColorOverride("font_color", Text);
            b.AddThemeColorOverride("font_hover_color", Colors.White);
            b.AddThemeColorOverride("font_pressed_color", Colors.White);
            b.AddThemeColorOverride("font_disabled_color", TextDisabled);
        }
    }
}
