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
