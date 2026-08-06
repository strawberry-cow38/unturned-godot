# Third-party assets

Assets in this repo that did not come from the retail game files, with their provenance and licence.

Ripped Unturned content under `game/content/` (meshes, textures, `.dat`-derived tables) is **not** listed here:
that is Smartly Dressed Games' material, used for a reimplementation, and is not third-party in this sense.

---

## Audio

### `game/content/ecg_beep.ogg`, `game/content/ecg_flat.wav`

The Science_3 patient monitor's heartbeat blip and flatline tone.

- **Source:** "Heart Monitor Beep" by **samfk360**, via freesound.org / Wikimedia Commons
  (`https://upload.wikimedia.org/wikipedia/commons/8/81/Heart_Monitor_Beep--freesound.org.oga`)
- **Licence:** **CC0 1.0 Universal** — public domain dedication. No attribution required; recorded here anyway
  so the provenance of anything shipped is answerable.
- **Derivation:** both files are cuts from that single 28-second recording, which beeps, accelerates, then
  flatlines — so both halves of what the monitor needs were already in one clip. They were located by
  measurement rather than by ear: a 5 ms amplitude envelope over the whole file found 18 discrete blips of
  ~180 ms and then one unbroken 11.4 s run starting at t=15.994 s, which is the flatline. `ecg_beep.ogg` is one
  steady early blip (206 ms, from t≈1.49 s), normalised ×7.2 — the source recording peaks at only 4224/32768.
  `ecg_flat.wav` is **not** a cut of the recording; it is synthesised from the spectrum measured over that long
  run, for the reasons below.
- **`ecg_flat.wav` is SYNTHESISED from the recording's measured spectrum, not cut from it.** An FFT over the
  flatline run shows a fundamental of exactly 880.00 Hz with **no measurable pitch drift** across the whole
  11.4 s and ±0.5% of level, and a purely **odd** harmonic series: H1 1.0000, H3 0.1104, H5 0.0396, H7 0.0200,
  H9 0.0123 (even harmonics sit at the numerical noise floor, −116 dB). The shipped tone is rebuilt from those
  partials at their measured phases, which preserves the timbre and drops the tape noise. Level is matched to
  the recording by **RMS, not peak** — a synthesised tone has a different crest factor, so peak-matching would
  make it audibly louder.
- **Loop structure:** 5071 frames, looping 661..5071 via the file's standard RIFF `smpl` chunk. The body is
  4410 samples = **176 whole cycles at 880 Hz**, so the wrap is exact by construction rather than merely small,
  and no crossfade is involved at all. The 30 ms lead-in before `LoopBegin` ramps up from silence and plays
  once: the bare tone starts at full amplitude, and without the ramp, switching a flatlined monitor on is itself
  a click. Loop points live in the asset rather than in a C# constant so the two cannot drift apart.
- **Two earlier versions of this file failed, and how they failed is the reason for the tests:**
  1. A crossfaded `.ogg`. The crossfade was right in PCM and wrecked by the encoder — Vorbis is a lapped
     transform and returned 13312 frames for the 13230 given, putting the real wrap 82 samples from the faded
     join. It stepped 22801: 4.9× the wave's own p95, 35% of full scale, a tick every 600 ms. The check that
     "verified" it read frame 13230 — an ordinary interior sample — and reported a clean 4572. *A check at the
     wrong offset agrees with the bug.*
  2. A WAV that fixed the wrap and still **pulsed**. 880 Hz × 0.600 s is 528 whole cycles, so the body's two
     ends were already in phase, and an equal-**power** (cos/sin) crossfade of two in-phase copies sums to
     1.414 — a **+3.01 dB bulge over 50 ms, once per loop**. Equal-power is the correct law for uncorrelated
     signals and the wrong one here. *This build had a perfect seam.* Continuity at the join and constancy
     across the body are different properties, and no seam check can see the second one.
- **Verification** in `props.heart_monitor`, on the shipped bytes: the seam is measured at the real wrap (last
  sample → `LoopBegin`, read from the asset) against the waveform's **own** step distribution rather than a
  chosen tolerance — a loop is inaudible when its join is indistinguishable from ordinary motion of the wave,
  and only the wave can say what that is. Steadiness is measured separately as peak-per-10 ms across the loop
  body (now a 0.01% spread, against 29% for version 2), and the body is asserted to span a whole number of
  cycles at the measured fundamental. Both checks were confirmed to fail on the builds above.
- **The beep is left as `.ogg`**: one-shot, so it has no seam to protect, and its edges already decay to near
  zero on their own (the last 35 ms fall 12325 → 7). Trimming its tail would cut into that decay and *create* a
  click — of the 7 discontinuities measured in the first posted preview, all 7 were the flatline's wrap and
  none were in the beep.

Note for anyone adding audio here: files dropped into `content/` have never been through the Godot editor's
import step, so they have no `.import` sidecar and `GD.Load`/`res://` returns **null** for them — silently, with
no error, which presents as "the sound is quiet". Load them off disk instead, the way everything else at runtime
is loaded (`HeartMonitor.LoadClip`, `Viewmodel.LoadOgg`).
