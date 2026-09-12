#!/usr/bin/env python3
"""Synthesise the geiger counter's click (strawberry 2026-09-11: "synth a geiger counter thats frequency
scales with radiation").

SYNTHESISED, not ripped, and that is the honest choice rather than a shortcut: retail has no geiger counter,
so there is no source clip to extract and matching one would mean inventing it anyway. A click is also about
the cheapest thing to make correctly -- it is a transient, not a tone.

WHAT A GEIGER CLICK ACTUALLY IS: a gas tube discharging. The sound is a sharp broadband spike with almost no
sustain and no pitch to speak of -- closer to a spark than a beep. So this is filtered noise under a very fast
exponential decay, NOT a sine burst. A tone would read as a UI beep and the whole point is that it reads as a
physical instrument.

Two clicks are written, not one. Real tubes do not produce identical pulses, and a single sample retriggered
at a fixed pitch is instantly recognisable as one sample retriggered -- the ear locks onto the repeat far more
readily than it does onto the rate, which is the thing actually carrying information here.
"""
import math
import os
import random
import struct
import sys

RATE = 44100
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "game", "content", "audio", "geiger")


def click(seed, decay_s=0.012, bright=0.55):
    """One tube discharge. `decay_s` is the whole audible length -- a few milliseconds."""
    rng = random.Random(seed)
    n = int(RATE * decay_s)
    prev = 0.0
    out = []
    for i in range(n):
        t = i / RATE
        # White noise, one-pole high-passed toward brightness: a discharge is mostly upper-mid energy, and
        # unfiltered white noise reads as a "shh" rather than a "tick".
        w = rng.uniform(-1.0, 1.0)
        hp = bright * (w - prev) + (1.0 - bright) * w
        prev = w
        # Exponential decay, steep. The leading edge IS the click; anything with a tail sounds like a pop.
        env = math.exp(-t / (decay_s * 0.22))
        out.append(hp * env)
    peak = max(1e-9, max(abs(v) for v in out))
    return [v / peak * 0.82 for v in out]   # leave headroom; the game mixes several of these at once


def write_wav(path, samples):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    data = b"".join(struct.pack("<h", int(max(-1.0, min(1.0, s)) * 32767)) for s in samples)
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVE")
        f.write(b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, RATE, RATE * 2, 2, 16))
        f.write(b"data" + struct.pack("<I", len(data)) + data)
    return len(samples) / RATE


def main():
    made = []
    for i, (seed, decay, bright) in enumerate(((1337, 0.012, 0.55), (2024, 0.010, 0.62)), start=1):
        p = os.path.join(OUT_DIR, f"geiger_click_{i}.wav")
        secs = write_wav(p, click(seed, decay, bright))
        made.append((p, secs))
    for p, secs in made:
        print(f"{os.path.relpath(p, ROOT)}  {secs*1000:.1f} ms  {os.path.getsize(p)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
