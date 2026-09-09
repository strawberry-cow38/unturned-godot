#!/usr/bin/env python3
"""Recut VoX's chosen reload and the developer's SKS rack; never stretch time.

Requires ffmpeg (libvorbis) and numpy. Source WAVs are supplied separately.
Example: python3 tools/retime_sks_audio.py /tmp/snow/integrate
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

import numpy as np

RATE = 48000
# name, source in/out, destination in, linear mix gain. All times in seconds.
# The continuous 3.32–3.84 cut preserves the Mosin sweep's original samples.
RELOAD_EDITS = [
    ("handling", 0.02, 0.18, 0.28, 0.55),
    ("clip appears", 1.98, 2.16, 0.60, 0.80),
    ("guide contact", 2.36, 2.46, 1.06, 0.80),
    ("clip seated", 3.14, 3.28, 1.26, 1.00),
    ("strip press and untouched sweep", 3.32, 3.84, 1.74, 1.00),
    ("empty clip lift", 4.08, 4.23, 2.40, 0.65),
    ("clip flick", 4.28, 4.42, 2.60, 0.55),
    ("short clip-away contact", 4.96, 5.12, 2.68, 0.28),
]
# Exclude the initial rearward pull and retain release, slam and natural decay.
HAMMER_EDITS = [("real SKS release and slam", 0.47, 0.97, 0.0, 1.0)]


def decode(path, channels):
    result = subprocess.run([
        "ffmpeg", "-v", "error", "-i", str(path), "-ar", str(RATE),
        "-ac", str(channels), "-af", "aresample=async=1:first_pts=0", "-f", "f32le", "-",
    ], check=True, capture_output=True)
    return np.frombuffer(result.stdout, dtype="<f4").reshape(-1, channels).copy()


def assemble(source, duration, edits):
    result = np.zeros((round(duration * RATE), source.shape[1]), dtype=np.float32)
    for _, start, end, destination, gain in edits:
        cut = source[round(start * RATE):round(end * RATE)].copy()
        # Edge-only fades suppress splice clicks; the interior is unchanged.
        fade = min(round(0.003 * RATE), len(cut) // 2)
        cut[:fade] *= np.linspace(0, 1, fade)[:, None]
        cut[-fade:] *= np.linspace(1, 0, fade)[:, None]
        at = round(destination * RATE)
        result[at:at + len(cut)] += cut * gain
    return result


def encode_measured(signal, path):
    """Normalize against DECODED written Vorbis, including codec overshoot."""
    channels = signal.shape[1]
    gain = 10 ** (-2 / 20) / np.max(np.abs(signal))
    best = None
    for attempt in range(40):
        subprocess.run([
            "ffmpeg", "-v", "error", "-y", "-f", "f32le", "-ar", str(RATE),
            "-ac", str(channels), "-i", "-", "-c:a", "libvorbis", "-q:a", "10",
            "-map_metadata", "-1", str(path),
        ], input=(signal * gain).astype("<f4").tobytes(), check=True)
        written = decode(path, channels)
        peak = float(20 * np.log10(np.max(np.abs(written))))
        if best is None or abs(peak + 2) < abs(best[0] + 2):
            best = (peak, path.read_bytes(), written)
        if abs(peak + 2) <= 0.02:
            break
        gain *= 10 ** (0.5 * (-2 - peak) / 20)
    peak, data, written = best
    path.write_bytes(data)
    assert abs(peak + 2) <= 0.02, f"{path}: decoded peak {peak:.6f} dBFS"
    assert len(written) == len(signal), f"{path}: codec duration changed"
    return {
        "file": str(path), "sample_rate": RATE, "channels": channels,
        "decoded_samples": len(written), "duration_s": len(written) / RATE,
        "decoded_peak_dbfs": peak, "sha256": hashlib.sha256(data).hexdigest(),
        "encoding_attempts": attempt + 1,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sources", type=Path)
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[1] / "game/content")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"files": [], "sources": [], "reload_edits": RELOAD_EDITS, "hammer_edits": HAMMER_EDITS}
    for source_name, output_name, channels, duration, edits in [
        ("CHOSEN-reload.wav", "sks_reload.ogg", 1, 4.0, RELOAD_EDITS),
        ("REAL-sks-rack.wav", "sks_hammer.ogg", 2, 0.5, HAMMER_EDITS),
    ]:
        source = args.sources / source_name
        report["sources"].append({"file": str(source), "sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
        signal = assemble(decode(source, channels), duration, edits)
        report["files"].append(encode_measured(signal, args.output / output_name))
    result = json.dumps(report, indent=2) + "\n"
    if args.report:
        args.report.write_text(result)
    print(result)


if __name__ == "__main__":
    main()
