# Native look-at information panel

`src/info_billboard.rs` renders the original `game/InfoBillboard.cs` layout.
Gameplay producers submit one `FocusedInfo` after interaction arbitration. An
empty focus hides the existing surface; changing content updates its texture.
The panel owns no gameplay state and sends no commands.

The source canvas is 320×296 pixels: name at Y4, five fixed stat rows at
Y44+32i, and prompt at Y258. Each row retains the original 26px icon, 210×22px
bar, black 0.55-alpha background and source color. Hidden rows leave their
original gaps. Icons use the source SubViewport's linear filter; the finished
world canvas uses nearest filtering. The 0.0042m pixel size, per-camera
billboard, unshaded material and depth-ignore behavior are preserved.

Text shares the existing embedded original OpenSans SemiBold font and Bevy
shaper with character nameplates. Labels shrink from 30/26px to a 15px minimum
against the original 312px advance-width limit. Their baseline uses the font's
rounded-up ascent, matching Godot's non-MSDF FreeType metrics rather than
Bevy's default 1.2em leading. Godot4.6's FreeType stroke radius is
`outline_size * 16 / 64`, so the source outline6 is 1.5px. Native fractional
mask coverage approximates that stroke; different font rasterizers can still
differ by edge pixels. Existing 64px/12px/0.85 character nameplates retain their
previous integer mask and positioning.

`tools/info_billboard_reference.gd` instantiates the actual original C# panel.
Run it with the built original Mono project, Vulkan/display and `--fixed-fps30`;
its user argument is the output prefix. The source project can override window
dimensions, so compare the exported 320×296 canvases for layout. The
ignored native `info_billboard::visual` test captures the same content, an
occluding wall, long text and panel retirement. Both export canvas PNGs for
inspection. These are visual checks, not screenshot golden gates.

This checkpoint provides the panel renderer. Individual vehicle/deployable
focus, pickup, utility load/fuel readouts and specialized device producers are
separate gameplay wiring and must not be inferred from the isolated render.
