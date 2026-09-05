# Credits & Attributions — shipped audio

Third-party audio shipped under `game/content/`, with licence, source, and how each
file was modified. This is the SHIPPED ledger (it rides under `res://` and the in-game `credits` console command
prints it verbatim). The canonical repo-root ledger `/CREDITS.md` mirrors this section and points back here.

CC0 items need no attribution and are listed for completeness. **CC BY 4.0 items require the credit shown** —
the licence deed is linked and the modification is stated, which is what BY compels.

Licence deeds: CC0 1.0 → https://creativecommons.org/publicdomain/zero/1.0/ · CC BY 4.0 →
https://creativecommons.org/licenses/by/4.0/

## Thunder → `thunder.wav`, `thunder2.wav`, `thunder3.wav`

Three distance-tiered thunder samples (near clap → distant rumble), picked by the strike's simulated distance.
All three were trimmed to the usable transient+tail and peak-normalised; `thunder.wav` additionally had ~0.5 s
of leading digital silence cut so the boom lands on the scheduled flash-to-thunder delay.

- **Kinoton** — "Thunder Clap And Rumble #9" — **CC0 1.0** — https://freesound.org/people/Kinoton/sounds/760216/ (2026-08-29)
- **hifijohn** — "thunder" — **CC0 1.0** — https://freesound.org/people/hifijohn/sounds/242586/ (2026-08-29)
- **klankbeeld** — "Deep thunder clap 03" — **CC BY 4.0** — https://freesound.org/people/klankbeeld/sounds/322210/ (2026-08-29)
  - **Attribution required.** Credit: "Deep thunder clap 03" by klankbeeld (freesound.org), CC BY 4.0.
  - **Modified:** trimmed and peak-normalised for use as one of the layered thunder samples.

## Rain → `rain_light.wav`, `rain_heavy.wav`

Two seamless-looped rain beds; intensity crossfades between them (light bed → heavy roar).

- **rain_light.wav** ← **_lynks** — "Soft Rain Loop" — **CC0 1.0** — https://freesound.org/people/_lynks/sounds/595717/ (2026-08-29)
  - **Modified:** high-passed at 120 Hz (the source is ~92% sub-250 Hz — reads as wind/rumble unfiltered),
    baked to a seamless 2 s-crossfaded loop, RMS-matched to −24 dBFS.
- **rain_heavy.wav** ← **AdrianoAnjos** — "Heavy rain pouring with water stream on concrete" — **CC0 1.0** — https://freesound.org/people/AdrianoAnjos/sounds/616446/ (2026-08-29)
  - **Modified:** baked to a seamless 2 s-crossfaded loop, RMS-matched to −24 dBFS.

## Rain on materials → `rain_metal_roof.wav`, `rain_tarp.wav`, `rain_car.wav`, `rain_foliage.wav`

Positional rain-on-material layers (RainMaterialAudio): the nearest prop of each material emits its own rain sound
from itself within a radius. All **CC0 1.0**. Sourced + baked by tinyclaw — trimmed to a steady window, equal-power
crossfade looped, loudness-matched to −24 dBFS. (Canvas + glass takes dropped per master; `rain_tarp` currently uses
the wood-deck take, pending master's canvas-vs-wood call.)

- **rain_metal_roof.wav** ← **Froggerbottom** — "Rain and tin roof" — **CC0 1.0** — https://freesound.org/s/607074/ (2026-08-30)
- **rain_tarp.wav** ← **buzkill** — "Rain Wood Deck Furniture Nighttime Coastal" — **CC0 1.0** — https://freesound.org/s/824671/ (2026-08-30)
- **rain_car.wav** ← **jankooiker** — "Rain from inside car" — **CC0 1.0** — https://freesound.org/s/237941/ (2026-08-30)
- **rain_foliage.wav** ← **bone666138** — "Rain in Trees" — **CC0 1.0** — https://freesound.org/s/655311/ (2026-08-30)

## Bus door → `busdoor_open.wav`

A city-bus door opening — the pneumatic hiss and swing, for the bi-fold doors.

- **Jedo** — "Bus doors open-close" — **CC0 1.0** — https://freesound.org/people/Jedo/sounds/396811/ (2026-09-05)
  - **Modified:** one 1.70 s open cut from the 116 s session at 93.40 s, high-passed at 50 Hz, faded in/out
    (15 ms / 120 ms), peak-normalised to −2.00 dBFS, mono 44.1 kHz 16-bit to match the other vehicle sounds.
  - The cut deliberately ends before the latch clunk at 95.64 s: strawberry picked this window over a longer
    one that included it (2026-09-05), so the file is the door opening only.
