// RAINDROP IMPACTS: the expanding RING and the SPLASHBACK CROWN, produced by ONE loop over one cell field.
//
// The crown is the ring's own first moment seen from the side -- the water thrown up as the drop lands, before the
// ring has finished spreading. It therefore has to stand exactly where a ring is being born, and the only way to
// guarantee that is to read the same cell, the same seed, the same per-cycle re-roll and the same fire gate. A
// second loop with "the same" constants is a second implementation, and this project has spent a day today paying
// for those: the placement ghost that lost its model fixup and the blueprint that snapped to sea level were both
// exactly this shape. So: one loop, two outputs, no way for them to disagree.
//
// WHY THIS IS AN INCLUDE. It used to be copied verbatim into Terrain.cs's string-embedded shader, under a comment
// saying "Godot has no shader include for a string-embedded shader". That is not true -- that same shader has
// included puddles.gdshaderinc all along (Terrain.cs:45, doubled quotes) -- and the copy carried its own warning
// that the two "must stay in step: terrain road and road props meet at the kerb, and two subtly different ripple
// fields there is worse than none". Adding splashback to one of two copies is how that warning comes true, so the
// copy is gone instead.
//
// The one real difference between the two former copies is preserved as the `scale` argument: the road-prop shader
// multiplies its cell size by a splash_scale uniform, the terrain passed a bare 1.0.

float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }

// HOW FAR THE RAIN LEANS, as ground-plane metres travelled per metre of fall (so length() is tan of the tilt
// from vertical, and the direction is the way the wind is driving it). RainSystem3D.PushWindDrift sets this
// from the SAME numbers it puts in the particle gravity, so the splash and the streak that made it cannot
// lean different ways. Zero = straight down, which is what every shader saw before this existed.
global uniform vec2 rain_slant;

// Crown shape lives HERE, as constants, for the same reason the loop does: tuning it per-surface is how the kerb
// stops matching. Life is a fraction of the cell's cycle -- a real splash is over long before its ring is.
const float RAIN_CROWN_LIFE   = 0.34;    // crown is gone by a third of the way through the ripple
const float RAIN_CROWN_HEIGHT = 0.150;   // metres of apparent rise at full size (0.075 was there but read as specks)
const float RAIN_CROWN_BASE   = 0.042;   // half-width at the foot, in cell units
const float RAIN_CROWN_TIP    = 0.008;   // ...and at the tip: the taper is what reads as a crown, not a stripe

/// ring  = the expanding impact ring (unchanged from the original splashes()).
/// crown = the splashback standing at that same impact point.
/// `away` is the normalised ground-plane direction from the CAMERA to this fragment. A column of water above the
/// impact point projects onto the ground away from the viewer, so smearing the test along `away` and letting
/// distance stand for height fakes the vertical without leaving the surface -- which is the only thing a shader
/// on ground geometry can do. Pass vec2(0.0) to skip the crown entirely.
void rain_impacts(vec2 wxz, float t, float amt, float scale, vec2 away, out float ring, out float crown){
    ring = 0.0;
    crown = 0.0;
    float gate = 1.0 - clamp(amt, 0.0, 1.0) * 0.20;       // more rain -> lower threshold -> more impacts fire (kept SPARSE: heavy ~18%, drizzle ~7% of cells)
    bool want_crown = dot(away, away) > 0.5;
    // THE CROWN LEANS WITH THE RAIN (master 2026-09-11: "the rain splashback should also match the angle of the
    // rain instead of being vertical"). `away` stands in for the VERTICAL -- a column of water above the impact
    // point projects away from the viewer -- while the rain's horizontal travel lies in the ground plane already
    // and so projects into it 1:1, in its own direction. Adding them is the projection of a LEANING column, and
    // the sum's LENGTH is how long that lean reads from here: a splash thrown across the view stretches, one
    // thrown at the camera foreshortens. Floored so rain blowing straight at you cannot cancel the crown to
    // nothing. Computed once -- it is the same for every cell in the fragment.
    vec2 caxis = away + rain_slant;
    float caxlen = length(caxis);
    vec2 cdir = caxlen > 1e-4 ? caxis / caxlen : away;
    float clen = max(caxlen, 0.35);
    for (int k = 0; k < 2; k++){
        float sc = (1.6 + float(k) * 2.8) * scale;         // larger cells -> fewer impacts per area
        vec2 g = wxz * sc + float(k) * 21.0;
        vec2 base = floor(g);
        // Voronoi-style: scan the 3x3 neighbourhood, each cell has a JITTERED source -> scattered, not a grid,
        // and rings from neighbouring cells overlap freely (no cell-edge clipping).
        for (int dy = -1; dy <= 1; dy++){
            for (int dx = -1; dx <= 1; dx++){
                vec2 id = base + vec2(float(dx), float(dy));
                float seed = h21(id);
                float tt = t * (0.65 + seed * 0.7) + seed;              // rate stays on the STATIC seed -- per-cycle rate feeds back into cyc + stutters the lifecycle (tinyclaw)
                float cyc = floor(tt);                                  // WHICH ripple this cell is on (not where within it)
                float life = fract(tt);                                 // 0..1 within that ripple
                vec2 q = id + cyc * 13.7;                               // re-roll the cell's randomness each cycle
                vec2 center = id + vec2(h21(q + 1.3), h21(q + 7.7));    // splash point MOVES every cycle -> scattered, not frozen at one spot per cell (master: impacts looked fixed)
                float fire = step(gate, h21(q)) * (0.55 + seed * 0.45); // WHICH cells fire also re-rolls per cycle (was static seed -> same ~18% forever)
                vec2 d = g - center;

                float rad = life * (0.16 + seed * 0.20);                // small raindrop-impact radius (~0.22m), not big pond ripples
                ring += smoothstep(0.05, 0.0, abs(length(d) - rad)) * (1.0 - life) * fire;

                if (!want_crown) continue;
                float up = 1.0 - smoothstep(0.0, RAIN_CROWN_LIFE, life);
                if (up <= 0.0) continue;                                // most of a cycle has no crown at all -- skip the rest
                // Height in CELL units, so the crown scales with the cell field exactly as the ring does and a
                // splash_scale change cannot make one grow while the other stays put.
                float len = RAIN_CROWN_HEIGHT * sc * up * clen;
                float along = clamp(dot(d, cdir), 0.0, len);
                float lateral = length(d - cdir * along);
                float taper = 1.0 - along / max(len, 1e-4);             // 1 at the foot -> 0 at the tip
                float w = mix(RAIN_CROWN_TIP, RAIN_CROWN_BASE, taper);
                crown += smoothstep(w, 0.0, lateral) * taper * up * fire;
            }
        }
    }
}
