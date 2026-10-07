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
in=$1; out=$2
convert "$in" -fuzz 12% -transparent 'rgb(255,0,255)' \
        -channel A -morphology Erode Octagon:1 +channel -trim +repage PNG32:"$out"

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
