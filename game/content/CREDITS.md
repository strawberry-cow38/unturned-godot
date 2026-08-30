# Credits & Attributions — weather audio

Third-party audio shipped under `game/content/` for the weather system, with licence, source, and how each
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
