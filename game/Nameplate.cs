using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// The floating "who is that" over a remote player: their profile picture and their display name.
    ///
    /// Label3D, NOT a RichTextLabel, and that is a security decision rather than a styling one. RichTextLabel
    /// renders BBCode, and a display name is attacker-controlled text -- [img]https://attacker/x[/img] in a
    /// name would make every client that can see that player fetch a URL for them. ProfileRules already
    /// strips the brackets, but the two defences are independent on purpose: the renderer that cannot be
    /// injected does not depend on the filter being perfect, and the filter does not depend on every future
    /// caller picking the safe renderer.
    ///
    /// A player with no picture, or one whose picture was refused, gets the MISSING TEXTURE checkerboard
    /// rather than an empty gap (strawberry: "anything else is rejected and a missing texture in its place").
    /// A blank space reads as a rendering bug; the checkerboard reads as "there is supposed to be an image
    /// here and there is not", which is the true statement.
    /// </summary>
    public partial class Nameplate : Node3D
    {
        const float HeadHeight = 2.05f;      // just above a standing rig's head
        const float PictureSize = 0.42f;     // world metres across
        const float TextSize = 0.16f;
        const float VisibleRange = 60f;      // past this the plate is noise; matches roughly where a body is a few pixels

        Sprite3D _picture;
        Label3D _label;
        static ImageTexture _missing;

        /// <summary>Lift the plate clear of whatever is on the head. <paramref name="topLocalY"/> is the top of
        /// the worn gear in the BODY's space -- the same space this node's Position is in.
        ///
        /// MEASURED, not tabled. 2.05 clears a BARE head and a chef's toque does not fit under it (the first NPC
        /// render had his name with a hat through the middle of it). Driving it off the gear's own height means
        /// a toque and a flat cap both come out right, including hats nobody has put on a character yet.
        /// Never goes BELOW HeadHeight, so a bare head is exactly where it has always been.</summary>
        public void SeatAboveGear(float topLocalY)
        {
            // The Label3D is vertically centred on this node, so half of it has to clear the hat as well.
            float want = topLocalY + TextSize * 0.5f + GearGap;
            Position = new Vector3(Position.X, Mathf.Max(HeadHeight, want), Position.Z);
        }
        const float GearGap = 0.07f;   // breathing room between the hat and the text

        public static Nameplate Attach(Node3D body)
        {
            if (body == null || !GodotObject.IsInstanceValid(body)) return null;
            var plate = new Nameplate { Name = "Nameplate", Position = new Vector3(0f, HeadHeight, 0f) };
            body.AddChild(plate);
            plate.Build();
            return plate;
        }

        void Build()
        {
            _picture = new Sprite3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = false,               // a plate behind a wall stays behind the wall -- it is not a wallhack
                PixelSize = PictureSize / ProfileRules.AvatarPixels,
                Position = new Vector3(0f, PictureSize * 0.75f, 0f),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            };
            AddChild(_picture);

            _label = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = false,
                FontSize = 64,
                PixelSize = TextSize / 64f,
                OutlineSize = 12,
                Modulate = Colors.White,
                OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
                HorizontalAlignment = HorizontalAlignment.Center,
                Text = "",
            };
            AddChild(_label);
        }

        /// <summary>Set what the plate shows.
        ///
        /// NO PICTURE AND A BROKEN PICTURE ARE DIFFERENT THINGS, and conflating them is what put a magenta
        /// checkerboard over the head of every player in the game. Nobody has set UG_PROFILE_PNG, so `png`
        /// arrived null for everyone, and null took the "missing texture" path -- the debug chequer that
        /// means "an asset failed to load". Tonight's reports are two people looking at it and saying so:
        /// "the thing above this guy's head is like a pattern... I think it's like a missing texture
        /// pattern", with three more screenshots showing it on other players.
        ///
        ///   png == null            -> no avatar SET. The common case, not a fault: show the name alone.
        ///   png != null, undecoded -> they sent something and it was bad. That IS a fault, and the
        ///                             checkerboard is the right way to say so.
        ///
        /// The name is sanitised ONE more time here: this is the last line before it reaches a renderer, and
        /// the cost of being wrong at this point is paid by everyone who can see this player.</summary>
        public void Set(string name, byte[] png)
        {
            if (_label == null || _picture == null) return;
            _label.Text = ProfileRules.SanitizeName(name);
            if (png == null) { _picture.Texture = null; _picture.Visible = false; return; }
            var tex = PlayerProfile.DecodeAvatar(png);   // null on anything Godot refuses, or wrong dimensions
            _picture.Texture = tex ?? MissingTexture();  // they sent a picture and it did not survive -> say so
            _picture.Visible = true;
        }

        /// <summary>The classic magenta/black checkerboard, generated rather than shipped as an asset so it
        /// cannot itself go missing. Built once and shared by every plate that needs it.</summary>
        public static ImageTexture MissingTexture()
        {
            if (_missing != null && GodotObject.IsInstanceValid(_missing)) return _missing;
            const int size = ProfileRules.AvatarPixels, cell = size / 8;
            var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            var magenta = new Color(1f, 0f, 1f);
            var black = new Color(0.05f, 0.05f, 0.05f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    img.SetPixel(x, y, ((x / cell) + (y / cell)) % 2 == 0 ? magenta : black);
            _missing = ImageTexture.CreateFromImage(img);
            return _missing;
        }

        public override void _Process(double delta)
        {
            // Fade out with distance rather than drawing a wall of names across the map. Cheap: one distance
            // check per visible player per frame, and hidden plates stop drawing entirely.
            var cam = GetViewport()?.GetCamera3D();
            if (cam == null) return;
            float d = cam.GlobalPosition.DistanceTo(GlobalPosition);
            bool show = d <= VisibleRange;
            if (Visible != show) Visible = show;
        }

        /// <summary>Test seam: what the plate currently reads, and whether it fell back to the placeholder.</summary>
        public string DebugText => _label?.Text ?? "";
        public bool DebugShowingMissingTexture => _picture != null && _picture.Texture != null
                                                  && _picture.Texture == _missing;
    }
}
