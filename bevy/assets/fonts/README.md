# Original default font

`OpenSans_SemiBold.ttf` is the lossless SFNT decompression of Godot 4.6.2's
`thirdparty/fonts/OpenSans_SemiBold.woff2`. That exact font is selected by
`scene/theme/SCsub` and `make_default_theme` in `scene/theme/default_theme.cpp`.
The source game uses this engine default in `Nameplate.cs`.

Source: https://github.com/godotengine/godot/tree/4.6.2-stable/thirdparty/fonts

The original SIL Open Font License is retained in `LICENSE.OpenSans.txt`.
No outlines, metrics, names or glyphs were changed. The TTF container allows
Bevy's font parser to read the same font without a runtime WOFF2 decoder.

Source SHA256: `55809808749613ed9ef9ad4f61424a14d2aa265d74369ad43403b9761aa6bac4`.
TTF SHA256: `ab424b0dfe9aa67c0305a992402750431ea024254047f6e09a99c0b89955e9a1`.
Conversion used fontTools 4.67.0: load `TTFont(..., recalcTimestamp=False)` from the WOFF2 bytes, set
`font.flavor = None`, then save as TTF. No runtime network access is involved.

`DejaVuSans_SkillSymbols.ttf` preserves U+2713 and U+2717 from DejaVu Sans,
which Godot 4.6.2's actual TextServer selected as the system fallback for the
Skills prerequisite check/cross on this Linux host. Open Sans lacks both glyphs.
The native Skills text supplies this tiny embedded fallback after unchanged
Open Sans, so an installation without system fonts still shows the states.
Outlines and metrics are unchanged; fontTools subsetting removes unrelated glyphs.
The original copyright/license is retained in `LICENSE.DejaVu.txt`.

Source `/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf` SHA256:
`ae7b7855e115a5966d8b1b3f80f254ccc117ec86f9965e202ee2940453837280`.
Subset SHA256: `093addf06053b5099fded7eca7a0228df786acdf6cac178be9308e91ba7f965b`.
Reproduce with fontTools 4.67.0: `TTFont(source, recalcTimestamp=False)`,
`subset.Options().recalc_timestamp = False`, `Subsetter.populate(unicodes=[0x2713, 0x2717])`,
then `subsetter.subset(font)` and `font.save(destination)`.

`DejaVuSans_InventorySnow.ttf` similarly preserves U+2744, the exact fallback
chosen by Godot's TextServer for InventoryUI's frozen-food label on this host.
It uses the same DejaVu source and license above. Reproduce with the same subset
steps, using `unicodes=[0x2744]`.
Subset SHA256: `6838457a97277d2cacc857b303f8b17f36bb255eefe05bda9078e708a32262bd`.
