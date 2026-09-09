# SKS reload and bolt-release audio

The chosen reload is recut to **4.000 s**, and the developer's SKS recording supplies a **0.500 s** release/closure clip. Both are installed by filename convention; **no C# or animation data was changed**.

**Full animation/gameplay sync is not complete.** This branch has an SKS-specific exception to the generic `Viewmodel.cs` comment: `Viewmodel.Sks.cs` returns `ReloadIncludesChambering = true` for `Sks_Reload`. `PlayerController.cs` therefore suppresses the automatic hammer after an empty SKS reload (both normal reload and magazine-swap paths). The reload animation itself closes the bolt around 3.16–3.30 s. The requested reload contains no bolt slam, so that closure is silent. Separately, `Sks_Hammer` is 1.400 s and closes its bolt around 1.08 s; the requested short release sound peaks around 0.137 s when played immediately. Fixing that scheduling/animation mismatch would exceed this audio-only change.

## Installed assets and measurements

Measurements decode the **written Ogg Vorbis files**, rather than measuring only the input WAVs. Peaks are sample peaks across all channels, rounded to the requested one decimal. Encoding uses libvorbis quality 10, with encode/decode gain adjustment and a ±0.02 dB acceptance tolerance. No ReplayGain dependency.

| File | Duration | Rate | Channels | Written Ogg peak | Unrounded peak |
| --- | ---: | ---: | ---: | ---: | ---: |
| `game/content/sks_reload.ogg` | 4.000 s | 48,000 Hz | 1 | −2.0 dBFS | −2.012390 dBFS |
| `game/content/sks_hammer.ogg` | 0.500 s | 48,000 Hz | 2 | −2.0 dBFS | −1.984570 dBFS |

Ogg container/granule durations are checked with ffprobe. The measurement decoder preserves timestamps (`aresample=async=1:first_pts=0`), including the short hammer stream's 128-sample initial timestamp gap. Its timeline is 24,000 samples; reload is 192,000 samples.

## Recut and event placement

All source cuts play at their original rate. Each cut has only a 3 ms fade at its edges; the sweep's interior is untouched before lossy encoding. No time stretching, granular reconstruction, or synthesized replacement was used.

| Material from chosen WAV | Source cut | New placement | Animation relationship |
| --- | --- | --- | --- |
| Quiet initial handling | 0.02–0.18 s | 0.28–0.44 s | Hand retrieves clip |
| Clip presentation contact | 1.98–2.16 s | 0.60–0.78 s | Clip appears around 0.64 s; measured contact peak 0.691 s |
| Guide contact | 2.36–2.46 s | 1.06–1.16 s | Clip approaching its seat; peak 1.106 s |
| Clip seating contact | 3.14–3.28 s | 1.26–1.40 s | Clip seated at 1.30 s; peak 1.311 s |
| Press and real Mosin sweep | 3.32–3.84 s | 1.74–2.26 s | Press peak 1.828 s; original 3.52–3.68 s sweep now **1.94–2.10 s**, inside requested 1.76–2.24 s strip beat |
| Empty clip lift | 4.08–4.23 s | 2.40–2.55 s | Lift after stripping; peak 2.448 s |
| Clip flick contact | 4.28–4.42 s | 2.60–2.74 s | Flick interval 2.60–2.76 s |
| Short, attenuated late contact | 4.96–5.12 s | 2.68–2.84 s | Reused as a small clip-away contact; composite peak **2.712 s** |

The 0.16–2.02 s dead gap is removed. Late material is relocated rather than truncating the original at 4 s. The terminal 5.28 s action and its tail are excluded under the no-reload-bolt requirement. The 4.98 s contact is shortened and reduced to 0.28 linear gain for the clip flick. Reload has no signal after 2.90 s, including no terminal bolt slam.

Hammer uses **0.47–0.97 s** of `REAL-sks-rack.wav`, resampled from 44.1 kHz to 48 kHz while retaining stereo. This excludes the first rearward pull and keeps the release, slam, and decay. Its measured maximum is **0.136646 s** from sound start. The short clip meets the requested asset duration but does **not** match the existing 1.4 s hammer animation.

Timing is checked using captured waveforms and rendered poses; no subjective listening approval is claimed.

The decoded sweep's correlation with the supplied source is **0.999883** at exactly 1.940 s, confirming that its grain was retained and repositioned without stretching.

Rendered round counts confirm the relationship: frame 76 / 0.64 s has ten rounds; frame 104 / 1.76 s has eight; frame 108 / 1.92 s has five; frame 112 / 2.08 s has one; frames 114 and 116 / 2.16 and 2.24 s have none. At frame 125 / 2.60 s the empty rail is visibly being flicked clear. The authored stripping movement starts slightly earlier than the supplied sampled beat range; the retained sweep is within both the sampled range and the visible cartridge movement.

## Loading and verification

`Viewmodel.cs` constructs the SKS filenames in the gun table, checks existence with `Snd`, and loads them through `AudioStreamOggVorbis.LoadFromFile`. `SetReloading` and `PlayHammer` start the respective players with the animations. Both players have the existing −3 dB player gain, so the rendered movie level is not the asset's −2 dBFS peak.

The loader does not print its resolved audio filenames. A narrow `strace` wrapper records successful `openat(..., O_RDONLY)` calls **in each run log**, limited to the two SKS filenames and their Eaglefire fallback equivalents. Successful SKS file opens are required; Eaglefire file opens fail the check. Captured movie audio is also correlated against the installed assets, so an opened but unplayed file cannot pass.

Build command: `dotnet build game/UnturnedGodot.csproj -c Debug -v quiet -nologo`. Result: **0 errors, 22 existing warnings**. Log: `/tmp/snow/integrate/verification/build.log`.

Final verifier result: **31 checks pass, 2 fail; exit status 1**. The failed checks are the hammer closure timing and the source audit of SKS automatic-rack suppression. The audio files, reload beats, file-open traces, movie completion, and negative controls pass. Both final movies contain video and audio, finalize normally, and log no engine errors. Godot still emits an ObjectDB leaked-instance warning at normal exit.

| Captured action | Correlation with installed SKS Ogg | Correlation with Eaglefire reference (rejected) | Audio start in movie | Offset after animation's frame-60 start at 2.360 s |
| --- | ---: | ---: | ---: | ---: |
| Reload | 0.9999998 | 0.084031 | 2.369375 s | +9.375 ms |
| Hammer | 1.0000000 | 0.105445 | 2.366708 s | +6.708 ms |

In the actual reload movie, the sweep is at **1.949–2.109 s relative to animation start**, and the clip-away peak is at **2.722 s**, including the measured mixer offset. These remain inside the requested 1.76–2.24 s and 2.60–2.76 s beat windows. The source sweep still at 3.52 s is rejected by the timing check. Silence is rejected for both movies.

The hammer peak occurs around **0.143 s relative to animation start**, while the first fully closed bolt key is **1.100 s** (the authored closure target is approximately 1.08 s). This is a real failure, not a successful sync report.

Final render locations:

- Reload movie: `/tmp/snow/integrate/verification/reload/v.avi`
- Hammer movie: `/tmp/snow/integrate/verification/hammer/v.avi`
- Each movie also has a smaller H.264/AAC `review.mp4` for convenient playback; measurements use the original AVI audio.
- Each render directory contains `render.log`, `contact_sheet.png`, raw PNG captures, and `sks_state_*.json` snapshots.
- Audio timeline: `/tmp/snow/integrate/verification/audio_timeline.png`
- Measurements: `/tmp/snow/integrate/verification/audio_measurements.json`
- Machine-readable verification: `/tmp/snow/integrate/verification/verification.json`
- Full check output: `/tmp/snow/integrate/verification/verify.log`

Renders use Xvfb, Vulkan/lavapipe, Movie Maker audio, fixed 25 fps, action at frame 60, and `--vm=... --gun=sks`. No `--headless` run was used. A temporary, uncommitted `game/override.cfg` sets a 1280×720 capture viewport and safe render threading; the command-line window size alone does not override the project's initial movie viewport. The capture override is removed after rendering. Earlier interrupted attempts are kept in directories named `*_partial_*` and are not verification evidence.

The first 720p reload run lost its X display when the concurrent hammer run finished. It exited with errors before completion and was rejected; the final reload is rerun alone. Run the two capture commands sequentially. The verifier requires the normal movie-finalization log marker and rejects engine errors or a broken X connection.

## Reproduction

Rebuild the assets from the two supplied WAVs:

```bash
python3 tools/retime_sks_audio.py /tmp/snow/integrate \
  --report /tmp/snow/integrate/verification/audio_measurements.json
```

The trace wrapper used for rendering is `/tmp/snow/integrate/verification/godot-trace.sh`. It wraps the local Godot 4.6 Mono binary with `strace -f -qq -e trace=openat -P ...` for all four relevant audio paths. The existing render script supplies `--path game --rendering-driver vulkan --write-movie <out>/v.avi --fixed-fps 25 --quit-after <last-frame+2> -- --vm=<out> --gun=sks`.

```bash
GODOT_BIN=/tmp/snow/integrate/verification/godot-trace.sh \
  bash tools/render_sks_anims.sh reload /tmp/snow/integrate/verification/reload \
  60,74,75,76,88,92,98,100,102,104,108,112,114,115,116,120,124,125,126,127,128,129,132,138,142,150,160,175
GODOT_BIN=/tmp/snow/integrate/verification/godot-trace.sh \
  bash tools/render_sks_anims.sh hammer /tmp/snow/integrate/verification/hammer \
  60,63,64,65,70,74,75,76,82,84,85,86,87,88,95,102
python3 tools/verify_sks_audio.py /tmp/snow/integrate/verification \
  --sources /tmp/snow/integrate
```

The verifier checks decoded peaks/durations, actual file opens, captured waveform identity, audio onset, sweep placement, clip-away placement, post-reload silence, and rendered clip/round states. Negative controls substitute the Eaglefire reference, silence, and the original late sweep. It also checks the existing hammer closure timing and SKS reload scheduling; these unresolved integration failures must produce **exit 1**, even when asset and reload beat checks pass. The report must not be interpreted as complete gameplay sync.

Source WAVs were supplied by the user: chosen CC0 rifle/Mosin mix and the developer's own real SKS recording. This change adds no other audio source. Source SHA-256 hashes:

- `CHOSEN-reload.wav`: `4d2ce874fdd01f0349ade2451c5676d244a0ebc84ad22c1588abd0506afbdcb3`
- `REAL-sks-rack.wav`: `62e70552e40edf64d8f90de2b719b135eae14708d65750388bd9a7c10f9a1eca`

Only the two Ogg assets, the recut/verification scripts, and this document belong in the commit. Existing tracked build artifacts are excluded. Push destination is exclusively `origin staging-work:Staging-Tinyclaw`.
