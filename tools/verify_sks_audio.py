#!/usr/bin/env python3
"""Check written SKS assets, actual Vulkan movies, file-open logs and beat timing.

Exit 1 on any failed check, including the existing SKS chambering/hammer mismatch.
Requires ffmpeg, numpy, scipy, and renders from tools/render_sks_anims.sh with
file-open tracing enabled (see INTEGRATION.md).
"""
import argparse
import json
from pathlib import Path
import re
import subprocess

import numpy as np
from scipy.signal import correlate

from retime_sks_audio import RATE, decode


def locate(signal, reference):
    if len(signal) < len(reference) or not np.any(signal) or not np.any(reference):
        return 0.0, 0.0
    at = int(np.argmax(correlate(signal, reference, mode="valid", method="fft")))
    segment = signal[at:at + len(reference)]
    score = float(np.dot(segment, reference) / (np.linalg.norm(segment) * np.linalg.norm(reference)))
    return at / RATE, score


def probe(path):
    return json.loads(subprocess.check_output([
        "ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path),
    ]))["streams"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("renders", type=Path)
    parser.add_argument("--sources", type=Path, required=True)
    parser.add_argument("--content", type=Path, default=Path(__file__).resolve().parents[1] / "game/content")
    args = parser.parse_args()
    results = []

    def check(name, passed, **details):
        result = {"check": name, "passed": bool(passed), **details}
        results.append(result)
        print(f"{'PASS' if passed else 'FAIL'} {name}: {json.dumps(details)}", flush=True)

    movie_audio = {}
    starts = {}
    for action, duration, channels in [("reload", 4.0, 1), ("hammer", 0.5, 2)]:
        path = args.content / f"sks_{action}.ogg"
        stream = probe(path)[0]
        signal = decode(path, channels)
        peak = float(20 * np.log10(np.max(np.abs(signal))))
        check(f"{action} format/duration/decoded peak",
              int(stream["sample_rate"]) == RATE and int(stream["channels"]) == channels
              and abs(float(stream["duration"]) - duration) < 1 / RATE
              and abs(peak + 2) <= 0.02,
              duration_s=float(stream["duration"]), rate=int(stream["sample_rate"]), peak_dbfs=peak)
        directory = args.renders / action
        log = (directory / "render.log").read_text()
        for clip in ("sks_reload.ogg", "sks_hammer.ogg"):
            check(f"{action} runtime opened {clip}",
                  re.search(r'openat\([^\n]*' + re.escape(clip) + r'", O_RDONLY\) = [0-9]+', log) is not None)
        check(f"{action} no Eaglefire opens", not re.search(r'openat\([^\n]*eaglefire_(reload|hammer)\.ogg', log))
        check(f"{action} real Vulkan movie", "Vulkan" in log and "Movie Maker mode enabled" in log)
        check(f"{action} no engine errors", "ERROR:" not in log)
        check(f"{action} movie finalized normally", "Done recording movie at path:" in log
              and "X connection to" not in log)
        streams = probe(directory / "v.avi")
        check(f"{action} movie contains video and audio", {s["codec_type"] for s in streams} >= {"video", "audio"})
        audio = decode(directory / "v.avi", 1)[:, 0]
        reference = decode(path, 1)[:, 0]
        start, score = locate(audio, reference)
        fired = re.search(rf"{action} fired at frame (\d+)", log)
        assert fired, f"{action} never fired"
        expected = (int(fired[1]) - 1) / 25
        check(f"{action} movie plays installed waveform", score > 0.98, correlation=score, movie_start_s=start)
        check(f"{action} audio starts with animation", abs(start - expected) <= 0.08,
              audio_start_s=start, animation_frame_start_s=expected, offset_s=start - expected)
        movie_audio[action] = audio
        starts[action] = start
        # Use the same waveform matcher to prove a working fallback or silence
        # cannot pass merely because the scene produced a movie file.
        _, fallback_score = locate(audio, decode(args.content / f"eaglefire_{action}.ogg", 1)[:, 0])
        check(f"negative control: {action} rejects Eaglefire", fallback_score <= 0.98, correlation=fallback_score)
        _, silent_score = locate(np.zeros_like(audio), reference)
        check(f"negative control: {action} rejects silence", silent_score <= 0.98)

    chosen = decode(args.sources / "CHOSEN-reload.wav", 1)[:, 0]
    sweep = chosen[round(3.52 * RATE):round(3.68 * RATE)]
    reload_audio = decode(args.content / "sks_reload.ogg", 1)[:, 0]
    sweep_start, sweep_score = locate(reload_audio, sweep)
    sweep_end = sweep_start + len(sweep) / RATE
    check("original sweep retained without stretching", sweep_score > 0.995, correlation=sweep_score)
    check("sweep inside stripping beat", 1.76 <= sweep_start and sweep_end <= 2.24,
          sweep_start_s=sweep_start, sweep_end_s=sweep_end, animation_beat_s=[1.76, 2.24])
    recorded_sweep, recorded_score = locate(movie_audio["reload"], sweep)
    check("movie sweep inside stripping beat", recorded_score > 0.98
          and 1.76 <= recorded_sweep - starts["reload"] <= 2.24 - len(sweep) / RATE,
          relative_start_s=recorded_sweep - starts["reload"], correlation=recorded_score)
    # Locate the sweep in the unretimed source and apply the same beat bounds.
    old_start, old_score = locate(chosen, sweep)
    check("negative control: old sweep timing fails", old_score > 0.995
          and not (1.76 <= old_start and old_start + len(sweep) / RATE <= 2.24),
          original_sweep_start_s=old_start)
    # Match the transient interior, excluding the edge fade and mixed decay.
    flick = chosen[round(4.98 * RATE):round(5.02 * RATE)]
    flick_start, flick_score = locate(reload_audio, flick)
    flick_peak = flick_start + int(np.argmax(np.abs(flick))) / RATE
    check("clip-away peak inside flick beat", 2.60 <= flick_peak <= 2.76 and flick_score > 0.98,
          peak_s=flick_peak, correlation=flick_score, animation_beat_s=[2.60, 2.76])
    tail_peak = float(np.max(np.abs(reload_audio[round(2.90 * RATE):])))
    check("no terminal reload bolt slam", tail_peak < 1e-5, peak_after_2_90_s=tail_peak)

    states = [json.loads(p.read_text()) for p in sorted((args.renders / "reload").glob("sks_state_*.json"))]
    present = [s for s in states if s["action"] == "reload" and 0.58 <= s["clip_time"] <= 0.70]
    stripped = [s for s in states if s["action"] == "reload" and 2.16 <= s["clip_time"] <= 2.28]
    flicking = [s for s in states if s["action"] == "reload" and 2.60 <= s["clip_time"] <= 2.76]
    check("rendered loaded clip and emptied clip", any(s["visible_rounds"] == 10 for s in present)
          and any(s["visible_rounds"] == 0 for s in stripped)
          and any(s["clip_visible"] and s["visible_rounds"] == 0 for s in flicking))

    # These are integration failures, not hidden by the passing asset checks.
    tracks = json.loads((args.content / "sks_action_tracks.json").read_text())
    bolt = np.array(tracks["hammer"]["bolt"])
    rear = int(np.argmax(bolt[:, 1]))
    close = float(bolt[rear:][np.flatnonzero(bolt[rear:, 1] <= 1e-6)[0], 0])
    hammer = decode(args.content / "sks_hammer.ogg", 2)
    slam = int(np.argmax(np.max(np.abs(hammer), axis=1))) / RATE
    check("hammer slam aligned with animated closure", abs(slam - close) <= 0.08,
          slam_s=slam, first_closed_key_s=close, error_s=slam - close)
    game = Path(__file__).resolve().parents[1] / "game"
    sks_cs = (game / "Viewmodel.Sks.cs").read_text()
    player_cs = (game / "PlayerController.cs").read_text()
    skips_rack = 'ReloadIncludesChambering => GunName == "sks"' in sks_cs and "&& !_viewmodel.ReloadIncludesChambering" in player_cs
    check("source audit: SKS automatic-rack suppression absent", not skips_rack,
          reason="Current SKS ReloadIncludesChambering suppresses automatic follow-up rack" if skips_rack
          else "Known suppression pattern absent; this source audit is not an empty-reload gameplay test")
    (args.renders / "verification.json").write_text(json.dumps(results, indent=2) + "\n")
    raise SystemExit(0 if all(r["passed"] for r in results) else 1)


if __name__ == "__main__":
    main()
