# Vehicle hand tools port

Ledger row 9: the spraypaint, the spare tire, the carjack, and the vehicle
branches of the held fuel can (pour and siphon). Gas-pump fill (command 29) and
generator pour (command 90) belong to the power port (`POWER_PORT.md`).

## Source

- `game/PlayerController.cs`
  - `KindOf` (the `Paint`/`Carjack`/`Tire` hand kinds).
  - Spraypaint: `EquipHeldSpraypaint`, `TrySprayVehicle`, `ApplySpray`, `PaintApplyFraction`.
  - Spare tire: `EquipHeldTire`, `TryFitTire`, `TickTire`, `TireFitFraction`.
  - Carjack: `EquipHeldCarjack`, `TryCarjack`, `ApplyCarjack`, `CarjackApplyFraction`.
  - Fuel can: `TryDepositFuel` and `TryExtractFuel`, vehicle branches only.
  - The LMB dispatch order, and the `_focusVehicle` look focus.
- `game/Vehicle.cs`
  - `SetPaint` and `SpawnPaint`.
  - `Carjack`, `JackableNow`, `ProbeAgl` and the carjack constants.
  - `TiresReplaceable`, `RepairTire` and `ClosestTireIndex`.
  - `FuelMax = Spec.Fuel`.
- `game/VehiclePuppet.cs` (`ApplyReplicatedPaint`, `ApplyReplicatedTires`), `game/vehicle_paint.gdshader`, `game/VehiclePaints.cs`.
- `game/Viewmodel.cs`: equip clip precedence (`EmptyHands` before `ConsumableEquipClip`), `PlayConsumeUse`, and the `ConsumableColor` tint.
- `game/GameAudio.cs` (`FuelPour`), `game/SoundBus.cs`, `game/MpLoopback.cs` / `game/DedicatedServer.cs` (`PaintColorFor`), and `game/VehicleNetSync.cs` (`ApplyCarjackForce`).
- Core: `ServerVehicles` handlers for commands 50/55/56 and `ServerTransactions.SpendPaintCan`/`SpendTire`.

## Authority

The client sends one intent per use and renders only the replicated result.
Nothing is mutated optimistically.

- **Paint (command 50).** The client sends `paintVehicle {netId, itemId}`. Core validates:
  - the vehicle exists and is not a wreck;
  - the sender is within `PaintReach` 8 m;
  - the can is in the sender's bag (spent last).
  
  The result rides `VehicleEntity.PaintRgb`. The bridge had never set `Transactions.PaintColorFor`, so every respray was refused. `VehicleHandToolServices` now hands down the `VehiclePaints` table, packed as `MpLoopback` packs it (`R8/G8/B8`).
- **Spare (command 55).** The client sends `fitTire`. Core:
  - refuses a car with no flat, one out of reach, a driven one, or a sender without a spare;
  - fits the lowest flat bit and spends one 1451.
  
  The result rides `poppedTireMask`. Native `tires.rs` already hides popped tyres and applies their grip and radius.
- **Carjack (command 56).** Core refuses a driven, wrecked or out-of-reach car. The host then rolls the source vectors (`System.Random`, in source order):
  - force `(R(-32,32), R(480,544), R(-32,32))`;
  - torque `R(-64,64)` on each axis.
  
  It emits `carjack {netId, force, torque}`. Only the physics owner (the listen host's native process) receives it. `apply_jacks` then applies `Vehicle.Carjack`'s own gate:
  - the body is simulated here;
  - it is not a wreck;
  - it has no driver and no passengers;
  - `JackableNow`: a 0.5 s settle dwell under 1.5 m/s, else not rising faster than 1 m/s and within 3 m of the world below (`ProbeAgl`, 260 m world-only probe).
  
  It adds `force * 0.02 / 2 * mass` as linear impulse and the same scale for torque: retail's one-step dv on mass 2.0, re-expressed for the real mass. Everyone else sees the launch through the ordinary transform stream. The jack is not consumed.
- **Pour/siphon (new commands 99/100, protocol 82).** The source mutated its local `Vehicle` and held can directly, so the vehicle branches never reached a server. They now name the exact held can, using B's v78 `DepositFuel` address and identity token, plus the car. `VehicleFuelServices` validates:
  - the sender is alive;
  - the car exists, `FuelMax > 0` (`Spec.Fuel`), and the sender is within 8 m;
  - the instance and token match.
  
  It then moves min(can, free tank) when pouring, or min(free can, tank) when siphoning. It refuses an empty can, a full tank, a full can and an empty car, as the source does. It writes the host fuel overlay and the replicated row together (`VehicleServices.SetFuel`), and the can through the owner echo. The bridge resolves the token for the `pourVehicleFuel`/`siphonVehicleFuel` actions on both host and remote clients.

## Native

`bevy/src/vehicle/hand_tools.rs`:
- **Targeting.** `access::focus` publishes the aimed vehicle (`LookedVehicle`). It counts only while it is the `InteractionFocus` winner: the source `ResolveFocus` has a single winner, so a nearer item or deployable clears `_focusVehicle`.
- **`HandTools::step`** ports the timelines:
  - Spray: starts with no target test, sends at 85 % of `Paint_Use` (2.8 s) against the focus at that moment, refuses a wreck there, and keeps the hand busy to the end of the clip.
  - Spare: validates at the click with the source `HUD.Alert` strings ("Aim at a vehicle.", "That one's a wreck.", "Stop it moving first.", "No flat tires on that."), sent as notices. It re-tests the same car at 75 % and is busy only until then, as `_tirePendingT` is.
  - Carjack: refuses before the wind-up, but only where the physics is local. A puppet is not pre-tested, matching the source MP path. It sends at 75 % of `Jack_Use` (1.967 s), stays busy for the whole clip, and ignores re-clicks.
  - Switching items drops a pending effect.
  - The use lengths, fractions and fallbacks come from the bridge catalog (`handTool`). `Catalog.HandTool` reads the source constants through the generated `HandRules` and the clip lengths from `consumable_anims.json`.
- **Sound and noise.**
  - Spray: `misc/vehicle_spraypaint` in 2D at -4 dB when the sweep starts.
  - Carjack: `items/tools_carjack_use` positional at the car (-3 dB, unit 6).
  - Fuel can: the source `FuelPour` clip at -5 dB.
  - The carjack's `SoundBus.Emit(.., 8)` becomes `worldZombieNoise` radius 8 at the player, sent only by the world owner.
- **Viewmodel.** Each tool uses its own Equip/Use pair (`Paint_*`, `Jack_*`; the spare uses `Melee_Equip` + `Tire_Use`, because `EmptyHands` wins the source equip precedence). It uses `spraypaint.txt` tinted by the can's colour, and `carjack.txt` with its 2×2 palette albedo.
- **Respray rendering.** `render::body_material` bakes the palette the way `vehicle_paint.gdshader` shades it: alpha-0 texels take the paint, opaque texels keep theirs. It is cached per (vehicle, colour). `BodyPaint` keeps the body handle, and `repaint` swaps only the descendants that still use it, so lamps, glass, wheels and wrecks are untouched. Spawn paint (`paintRgb` null) keeps the variant bake.
- **Fuel can.** With a held fuel container and the car as focus winner, LMB pours and RMB siphons. The source refusals are checked first from accepted state: the exact can's fuel from `inventoryView`, and the tank from the row. The source logs them as `Log.Print` only.

## Deviations (stated, not hidden)

- **Which flat wheel.** The source local path fits the popped wheel closest to the aim. Core's handler fits the lowest flat bit and deliberately ignores the wire index, because the server has no wheel geometry. The client sends the index it expects (the lowest bit), and the authority's rule stands.
- **Spare busy time.** The source joined-client path keeps the hand busy for the whole clip and sends at the click. The port follows the local timeline instead: validate at the click, send at 75 %.
- **Reach.** The source aims with the 2.6 m look ray. The authority's guard against forged ids is the vehicle tools' 8 m reach. Pour and siphon reuse that same 8 m.
- **Fuel can sound.** It plays when the intent is sent after the client checks pass, which is when the source plays it. A host refusal can therefore follow a played sound (out of reach, stale token); no state moves.
- **No HUD alert line.** The source `HUD.Alert` messages arrive as native session notices; the native port has no separate alert line.
- **No third-person animation.** Remote players see no third-person tool animation; the source had none on puppets either.
- **Carjack on a remote client.** A remote client's carjack noise reaches no zombies. The source `SoundBus` on a joined client also only reached local puppets.

## Verification

See `PROGRESS.md` (2026-10-16 vehicle hand tools) for counts, negative controls
and the opened Vulkan frames (`bevy/artifacts/bevy-hand-tools-*.png`).
