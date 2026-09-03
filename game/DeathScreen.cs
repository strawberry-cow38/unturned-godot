using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// The death screen (master 2026-09-03: "wire up an actual death player ragdoll and death screen. orbits the
    /// ragdoll, death music plays, options appear to respawn random, at a bed (stub it.) exit to menu").
    ///
    /// The ragdoll and the death sting already existed -- Die() builds a real RiggedCharacter corpse and throws it
    /// with retail's own RagdollTool force, and MusicPlayer stings the map outro. What was missing is this: the
    /// camera used to freeze at one fixed angle and the body just self-respawned after 3.5 s with nothing asked
    /// and nothing shown. So this screen owns the CHOICE, and PlayerController owns the orbit.
    ///
    /// It does NOT pause the tree, unlike PauseMenu. The whole point is that the ragdoll is still settling and the
    /// camera is still moving behind it -- a paused world would show a corpse frozen mid-air.
    /// </summary>
    public partial class DeathScreen : CanvasLayer
    {
        /// <summary>Is a death screen currently asking for the cursor? PauseMenu.Close() consults this before
        /// recapturing the mouse: without it, die -> ESC -> ESC hands the cursor back to the game while this
        /// screen is still up, leaving the respawn buttons unclickable and no way out of being dead.
        /// Cleared in _ExitTree as well as HideDeath, so a torn-down session cannot leave it stuck true --
        /// a static that outlives its owner is how the vehicle alarm hook broke.</summary>
        public static bool WantsCursor { get; private set; }

        public System.Action OnRespawnRandom;
        public System.Action OnRespawnBed;

        Button _randomBtn, _bedBtn;
        Label _note;

        public override void _Ready()
        {
            Layer = 55;   // under the pause menu (60), over the HUD
            Visible = false;
            ProcessMode = Node.ProcessModeEnum.Always;

            // Full scrim, not ScrimLight: the gameplay HUD sits on layers 10/12, well under this one, so the
            // only thing that pushes the vitals bars and the vehicle status box back is how dark this is. The
            // first render of this screen had them reading straight through it.
            var dim = new ColorRect { Color = UITheme.Scrim, MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(dim);

            // Bottom-centre, not a centred modal: the ragdoll is the thing worth looking at, and a panel over the
            // middle of the screen would cover the one bit of theatre the whole feature exists for.
            var anchor = new MarginContainer();
            anchor.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            anchor.AddThemeConstantOverride("margin_bottom", 64);
            AddChild(anchor);

            // An OPAQUE panel behind the text. Without one the labels and buttons float over whatever the HUD
            // is drawing, and the bottom-centre vehicle status box occupies this exact region -- the first
            // render had "Jeep ... G3" and its fuel bars showing through the middle of the death panel.
            var panel = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkEnd, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
            var pstyle = new StyleBoxFlat { BgColor = UITheme.BgSolid, BorderColor = UITheme.SlotEmptyEdge };
            pstyle.SetBorderWidthAll(1);
            pstyle.SetCornerRadiusAll(6);
            pstyle.SetContentMarginAll(22);
            panel.AddThemeStyleboxOverride("panel", pstyle);
            anchor.AddChild(panel);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 10);
            panel.AddChild(col);

            var title = new Label { Text = "YOU DIED", HorizontalAlignment = HorizontalAlignment.Center };
            title.AddThemeFontSizeOverride("font_size", 44);
            title.AddThemeColorOverride("font_color", UITheme.Text);
            col.AddChild(title);

            _note = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            _note.AddThemeFontSizeOverride("font_size", 14);
            _note.AddThemeColorOverride("font_color", UITheme.TextDim);
            col.AddChild(_note);

            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
            row.AddThemeConstantOverride("separation", 10);
            col.AddChild(row);

            _randomBtn = MakeButton("Respawn", () => OnRespawnRandom?.Invoke());
            row.AddChild(_randomBtn);
            _bedBtn = MakeButton("Respawn at Bed", () => OnRespawnBed?.Invoke());
            row.AddChild(_bedBtn);
            row.AddChild(MakeButton("Exit to Menu", ExitToMenu));
        }

        static Button MakeButton(string text, System.Action onPressed)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(190f, 42f) };
            b.AddThemeFontSizeOverride("font_size", 17);
            b.Pressed += () => onPressed();
            return b;
        }

        /// <summary>Show the screen. <paramref name="bedClaimed"/> enables the bed option -- Respawn() already
        /// spawns you at a claimed bed, so this is the real behaviour rather than a stub whenever you have one;
        /// with no bed the button is disabled and SAYS why, instead of being a live control that silently does
        /// the same thing as the one next to it. <paramref name="serverClocked"/> is MP, where the server owns
        /// the respawn clock and there is no client->server respawn command to hang these buttons on: they are
        /// disabled and the screen closes itself when the server's PlayerRespawnedEvent arrives.</summary>
        public void ShowDeath(bool bedClaimed, bool serverClocked)
        {
            Visible = true;
            // Exit to Menu is never disabled: leaving is always the player's call, server-clocked or not.
            _randomBtn.Disabled = serverClocked;
            _bedBtn.Disabled = serverClocked || !bedClaimed;
            _note.Text = serverClocked ? "waiting for the server to respawn you"
                       : bedClaimed ? "you have a bed claimed"
                       : "no bed claimed";
            WantsCursor = true;
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }

        public void HideDeath()
        {
            if (!Visible) return;
            Visible = false;
            WantsCursor = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        public override void _ExitTree() { WantsCursor = false; }

        // Same teardown as PauseMenu.ExitToMenu, including the cache clear -- this is the THIRD way out of a
        // session, and the static caches surviving ReloadCurrentScene is exactly the bug that note warns about.
        void ExitToMenu()
        {
            GetTree().Paused = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            ResourceCaches.ClearAll();
            GetTree().ReloadCurrentScene();
        }
    }
}
