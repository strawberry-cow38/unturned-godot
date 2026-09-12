#!/usr/bin/env python3
"""Synthesise the walkie-talkie's carrier static (strawberry 2026-09-11: "lmb toggles it on/off. plays
static").

SYNTHESISED for the same reason the geiger clicks were: retail has no walkie-talkie audio on the box, so
there is nothing to rip and matching one would mean inventing it anyway. Noise is also the one sound that is
genuinely better generated than sampled -- a looped noise SAMPLE has a period, and the ear finds it.

WHAT RADIO STATIC ACTUALLY IS: band-limited noise, not white noise. A speaker in a handheld radio rolls off
hard at both ends, so full-spectrum white hiss reads as a broken tweeter rather than a squelch. This is white
noise through a one-pole high-pass and then a one-pole low-pass -- a crude band-pass, which is exactly what a
cheap speaker is.

SEAMLESS BY CONSTRUCTION, not by crossfade: the loop is built from a whole number of cycles of a phase-
continuous process, then the first and last few ms are cross-faded into each other so the wrap has no click.
A click at the loop point is the single most audible artefact in a sustained sound.

Slow amplitude drift is added on purpose. Dead-flat noise sounds synthetic; a real carrier breathes.
"""
import math, os, random, struct, sys

RATE = 44100
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "game", "content", "audio", "radio", "static_loop.wav")
SECONDS = 2.0          # long enough that the drift is not itself a rhythm
XFADE = int(RATE * 0.012)


def band_noise(n, seed=4242, hp=0.62, lp=0.45):
    rng = random.Random(seed)
    out, prev_in, prev_hp, prev_lp = [], 0.0, 0.0, 0.0
    for _ in range(n):
        w = rng.uniform(-1.0, 1.0)
        hp_v = hp * (prev_hp + w - prev_in)      # one-pole high-pass
        prev_in, prev_hp = w, hp_v
        prev_lp = prev_lp + lp * (hp_v - prev_lp)  # one-pole low-pass
        out.append(prev_lp)
    return out


def main():
    n = int(RATE * SECONDS)
    s = band_noise(n + XFADE)
    # slow breathing, two incommensurate rates so the envelope never repeats inside the loop
    for i in range(len(s)):
        t = i / RATE
        s[i] *= 0.80 + 0.20 * (0.6 * math.sin(2 * math.pi * 0.37 * t) + 0.4 * math.sin(2 * math.pi * 0.13 * t))
    # wrap the tail into the head so the loop point is inaudible
    for i in range(XFADE):
        a = i / XFADE
        s[i] = s[i] * a + s[n + i] * (1.0 - a)
    s = s[:n]
    peak = max(1e-9, max(abs(v) for v in s))
    s = [v / peak * 0.55 for v in s]            # headroom: this sits under gunfire and footsteps

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    data = b"".join(struct.pack("<h", int(max(-1.0, min(1.0, v)) * 32767)) for v in s)
    with open(OUT, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVE")
        f.write(b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, RATE, RATE * 2, 2, 16))
        f.write(b"data" + struct.pack("<I", len(data)) + data)
    print(f"{os.path.relpath(OUT, ROOT)}  {len(s)/RATE:.2f}s  {os.path.getsize(OUT)} bytes")
    # the wrap discontinuity, measured rather than asserted -- a loop click is what this file exists to avoid
    print(f"loop seam step: {abs(s[0]-s[-1]):.4f} (vs mean |delta| {sum(abs(s[i]-s[i-1]) for i in range(1,2000))/1999:.4f})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
