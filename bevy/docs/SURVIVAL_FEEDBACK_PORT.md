# Survival feedback (temperature, status icons, deadzone, Geiger, fluids)

Sources: `game/HUD.cs` (AddVital/AddBipolarVital/AddStatus, Alert),
`core/UnturnedSim/PlayerTemperatureSim.cs`, `ThermalFalloff.cs`,
`ClothingInsulation.cs`, `WorldTemperature.cs`, `game/ThermalField.cs`,
`game/ThermalSource.cs` + `inventory/StoreShelf.SetCookerOn`, `game/ShelterProbe.cs`,
`game/PlayerController.cs` (TemperatureTick, TryDrinkContainer, TryFillAtTap,
UpdateFluidContainerHud), `game/DeadzoneOverlay.cs` + `content/deadzone.gdshader`,
`game/GeigerCounter.cs`, `game/ClientWorldSession.cs` (ItemPickupDenied toast,
VehicleExitRefused alert), core `ServerDeadzones`/`DeadzoneSim`, `ServerFluids`.

## Authority split

Body temperature is **client-computed in the source**: the shell steps
`PlayerTemperatureSim` from its own world probes and streams only the band on
`PlayerStateCommand.TempBand` (v59); the server's vitals read that band
(`TemperatureBandOf`) for cold/heat drains and Freezing/Boiling exposure damage. The
port keeps that: the bridge's client adapter (`TemperatureFeedback.cs`) runs the
engine-free sim and sends the band with every state command (local and remote
sessions). The native client supplies the two probes that need its physics world:

- `ShelterProbe` overhead cover (shared native `Shelter`, lifted 0.2 m off the feet as
  the footstep probe does, because native feet rest exactly on the floor surface);
- `ThermalField.NetC`: accepted container replicas whose appliance is lit
  (`cookerOn`) are the source's 25 °C / 5 m / SelfRadius 0.9 m sources; range first,
  then a world-layer (source bit 0 → `GROUP_1`) ray from the player, smoothstep
  falloff, ±40 °C clamp.

Ambient = `WorldTemperature.AmbientC(DayOfYear(StartDayOfYear, day), timeOfDay,
WeatherOffsetC(active weather name, blend))`, raining = the weather sim's `IsRaining`,
exertion = moving + sprint stance, not broken, not majorly irradiated, not seated;
clothing insulation/waterproofing from the accepted owner bag. Probes latch at 4 Hz,
the body steps every 50 Hz tick, dead players do not step. No protocol change.

Source quirks kept: `WeatherOffsetC` only knows `rain/storm/snow/blizzard/fog`, so the
actual PEI snow type ("Snowfall") adds no direct offset — snow chills through
wetness (snow counts as `IsRaining`). A remote client has no accepted day number or
weather yet (existing `dayNumber`/`weather` wire gaps): it uses the replicated time of
day with day 0 and no weather. That is a port gap, not a source fallback (the source
remote client has its own synced day and weather); its thermal LOS uses its own
native world.

## HUD (layers as in HUD.cs)

- Vitals panel (layer 12, above menus): the seventh row is the bipolar temperature
  bar: `hud_temperature.png`, neutral trough `(0.75,0.78,0.82,0.35)`, 2 px centre
  mark, fill from the centre by `Comfort` (-1..1), `CCold (0.42,0.80,0.98)` /
  `CHot (0.98,0.38,0.10)`, the same 6/s exponential tween seeded by |x|>1. Carrying the
  Thermometer (item 9346) shows `Units.TemperatureReadout` in the default `Both`
  system (`"19 °C / 66 °F"`, ties-to-even rounding), 15 px original font, size-5
  black outline at 0.95.
- Status row (layer 10, `VitalsTopGap + 8` above the bottom, 40 px boxes every 46 px
  from x=24, black 0.5 box): bleeding, broken, starved (`food<=0`), dehydrated
  (`water<=0`, the water icon), deadzone (`hud_virus.png`) — each on its own source
  condition, from the accepted owner replica. Deadzone = the host's own
  `ServerDeadzones.SecondsInside(owner) > 0` (the same DeadzoneSim clock the source
  DeadzoneField publishes); not yet available to a remote client (no wire field).
- DeadzoneOverlay: deadzone.gdshader's arithmetic in WESL on the scene canvas after
  night vision and before layer 9/HUD (source layer 8): `pow(dose,1.6)` ramp, Rec.709
  luma desaturation ×0.85, 3 px grain cells ×0.35, time-folded seed; exposure =
  accepted radiation / `FullExposureDose 1`, no pass at all while clear. `UG_DEADZONE`
  mirrors the source harness override.
- GeigerCounter: Poisson clicks at `RateFor(dose)` (silent below 0.02, 1.2→30/s
  squared), first click after the source's 1 s wait, four reusable voices, −6 dB with
  −2.5..+1.5 dB jitter, the original `geiger_click_1/2.wav`; paused with
  singleplayer pause. `UG_GEIGER` mirrors the source harness override.
- HUD.Alert (centre, 26 px, red `(0.98,0.42,0.35)`, size-6 outline, holds then fades
  over the last second) for an accepted `vehicleExitRefused` ("Your door is blocked",
  2 s). The session toast (24,86; 20 px; `(1,0.85,0.4)`; 2.5 s) for an accepted
  `pickupDenied` ("No room in inventory"). XpAwarded(10) has **no** source consumer
  and is still not shown.

## Fluid containers

`PlayerController.KindOf` holds a bottle as a fluid container, never as food: the
native hand-use timeline no longer consumes items whose hand kind is `fluid`. With
one in hand, LMB = `TryDrinkContainer` (whole bottle, `FluidRules.DrinkAll`) and RMB
at a sink/bathtub = `TryFillAtTap` (`FluidRules.FillClean`), both through the
existing Commands 69/70. The toast text is the source's (computed on a **clone** of
the accepted item, so nothing local changes); the bottle, its label and the water bar
change only with the server's echo. The drink plays the held item's Use clip
(`PlayConsumeUse`). The held line (CenterTop 185 px, 24 px, size-6 outline at 0.9)
shows the toast for 2.2 s, otherwise `FluidItem.Label` plus `[LMB] drink` or
`[RMB] fill at the sink|bathtub` from `WaterTap.AimedAt` (host map taps, `AimSlack`
0.15 / X scale) on the native 2.6 m first-person look ray; like the source, the toast
is cleared whenever no fluid container is held. The server still re-validates the
2.5 m reach from its own copy of the position.

Not ported: placed fluid-tank focus (its source fill is SP-local with no command; the
tank hints/refusals therefore never show), the mains switch (`the water's off`), the
third-person shoulder look origin, and tap aiming for remote clients (no map taps).

## Evidence

See PROGRESS.md (2026-10-16 survival feedback entry).
