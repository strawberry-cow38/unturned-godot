#!/usr/bin/env bash
# Turn a --bakeicon render into a shippable inventory icon.
#
# WHY THIS EXISTS: Main.cs's --bakeicon draws the item over a flat MAGENTA key colour, and nothing in the repo
# turned that key into alpha -- the 1878 shipped icons were RIPPED from the game, not baked, so the bake path
# had never actually produced a shipped icon. Installing a bake straight into content/items/icons/ puts a
# magenta rectangle in the inventory grid. Shipped icons are RGBA over full transparency (measured:
# icons/95.png corner pixel = 0,0,0,0), so that is what this produces.
#
# ⚠ THE FUZZ IS A COMPROMISE, not a default. 2% leaves an antialiased magenta fringe on every edge; much higher
# starts eating PINK, and two of these items are a pink eraser and a pink pencil ferrule. 12% keys the flat
# background and leaves the eraser intact (checked against it specifically).
#
# ⭐ AND THEN THE ALPHA IS ERODED BY 1px, which is what actually removes the fringe. Raising the fuzz until the
# fringe goes is the obvious move and it is the one that destroys the pink items; shrinking the silhouette by a
# pixel takes the half-magenta rim with it and costs nothing visible -- the thinnest icon here is the pencil at
# 10px tall and it still reads correctly at 8.
set -euo pipefail
in=$1; out=$2; pinholes=${3:-}   # pass --fill-pinholes for a model with NO purple in it
convert "$in" -fuzz 12% -transparent 'rgb(255,0,255)' \
        -channel A -morphology Erode Octagon:1 +channel -trim +repage PNG32:"$out"

# ⭐ --fill-pinholes: ENCLOSED BACKGROUND PIXELS, which the erode above cannot reach. A 1 px PINHOLE between
# two parts of a model -- the gap where two ice cubes almost touch -- keys to a half-magenta pixel surrounded
# on all sides by opaque model, so shrinking the silhouette by a pixel does nothing to it and the icon ships
# with a magenta dot in the middle. The ice pile had exactly one.
#
# ⚠⚠ OPT-IN, AND IT TOOK TWO MEASUREMENTS TO LEARN WHY. The obvious rule is a magenta-dominant HUE test, on
# the reasoning that pink items fail it (pink has green close to blue) while a magenta blend does not. It
# looked safe. Measured across all 1,890 shipped icons it would have punched holes in 31 of them, up to
# 24,577 px at a time -- because plenty of items are legitimately PURPLE, and purple is red and blue above
# green too. Restricting it to SPECKS (mask minus its own morphological opening) still damaged 29 of the 31:
# a purple region's anti-aliased rim is hundreds of isolated single pixels. There is no colour rule here that
# separates "background showing through a crack" from "a purple thing"; only the caller knows. So the caller
# says so, and the default path is byte-identical to what it has always produced.
if [ "$pinholes" = "--fill-pinholes" ]; then
  # ⚠ THE MASK MUST BE BUILT WITH ALPHA ON. `-transparent` only zeroes the ALPHA -- it leaves the stored RGB
  # magenta -- so a colour test under `-alpha off` reads the whole keyed-out background as a match and
  # reported 124,637 "specks" on an icon that had one.
  t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
  convert "$out" -alpha on -fx "(a > 0.5 && (r-g) > 0.25 && (b-g) > 0.25) ? 1 : 0" -alpha off -colorspace gray -threshold 50% "$t/m.png"
  convert "$t/m.png" -morphology Open Octagon:1 "$t/o.png"
  convert "$t/m.png" "$t/o.png" -compose Difference -composite -threshold 50% "$t/speck.png"
  convert "$out" "$t/speck.png" -alpha on -channel A -fx "v.r > 0.5 ? 0 : u.a" +channel -delete 1 PNG32:"$out"
fi

# ⭐⭐ VERIFY AT A WIDER TOLERANCE THAN THE ONE USED TO KEY (35% vs 12%), and this is the whole point of the
# check. The first version of it detected at the SAME 12% it had just keyed with, so every pixel the key had
# failed to catch was also, by construction, invisible to the detector -- it reported 0 fringe on icons with a
# plainly visible magenta rim. A measurement taken at the tolerance of the fix can only ever agree with the fix.
# Flattening onto GREEN first is also load-bearing: without it this re-reads the transparent background's own
# magenta and reports thousands of false positives.
left=$(convert "$out" -background 'rgb(0,255,0)' -flatten -fuzz 35% \
       -fill white +opaque 'rgb(255,0,255)' -fill black -opaque 'rgb(255,0,255)' \
       -colorspace gray -format "%[fx:(1-mean)*w*h]" info:)
printf '%s -> %s  (%.0f magenta px remaining at 35%%)\n' "$in" "$out" "$left"
# ⚠ awk, not shell -eq: ImageMagick returns a FLOAT, and a clean icon comes back as "1.11022e-16" rather than
# "0". `${left%.*}` turned that into "1" and the guard refused four icons it had just verified as perfect --
# it was reading the mantissa. Compare numerically, with a sub-pixel tolerance.
awk -v n="$left" 'BEGIN { exit (n + 0 < 0.5) ? 0 : 1 }' \
  || { echo "REFUSING: magenta survived the key ($left px)"; exit 1; }
