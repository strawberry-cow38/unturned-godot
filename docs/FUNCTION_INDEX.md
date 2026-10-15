# FUNCTION_INDEX

The shared-function reference. **Check here before writing a helper.** Built from a
full sweep of `core/UnturnedSim/`, `core/UnturnedNet/`, `core/SDG.*/`, `game/`, and
`tools/` — the reusable surface only, i.e. things with a real non-test call site.

It is deliberately **not** a list of every public method (there are ~2800; nobody opens
that). It is organised by *what you are trying to do*, because the failure mode this
document exists to prevent is not "I could not find the function" — it is "it did not
occur to me that one existed".

Rules of the road:
- Entries state the **sentinel** as loudly as the return type. Most bugs in this repo's
  history came from a `0` / `-1` / `255` / `null` that meant two different things.
- `A ≡ B` means the two are known duplicates; see `docs/DUPLICATE_AUDIT.md`.
- Line numbers drift. The signature and the file are the durable part — grep the name.

---

## 1. "I need to…" — the anti-rewrite index

| I need to… | Use | Do **not** |
|---|---|---|
| Decide whether a player's on-foot transform/teleport is allowed while riding a vehicle | `ServerVehicles.IsSeated(playerId)` in `core/UnturnedNet/VehicleReplication.cs`; `IsDriver` is only for vehicle control authority | Treat a passenger as on foot because they do not own the steering/transform stream |
| damage falling off from a blast | `ExplosionMath.Linear/Squared` (`core/UnturnedSim/CombatMath.cs:12`/`:15`) | write `1 - dist/r` inline (`game/Deployable.cs:318` does; it is the one straggler) |
| decide if a fall hurts / how much / breaks legs | `FallMath.Hurts/Damage/BreaksLegs` (`core/UnturnedSim/CombatMath.cs:30`–`:36`) | compare against 22 yourself |
| know how far a zombie can sense a player | `StealthDetection.Radius/DrivingRadius` (`core/UnturnedSim/CombatMath.cs:53`/`:66`) | rebuild the stance table |
| ray-test a standing body | `ZombieCombat.RayCapsule` (`core/UnturnedSim/ZombieCombat.cs:41`) **SP** / `ServerCombat.SegmentHitsCylinder` (`core/UnturnedNet/ServerCombat.cs:629`) **MP** | add a third — these two already disagree, see DUPLICATE_AUDIT |
| band a hit into head/torso/leg | `ZombieCombat.LimbAt` (`core/UnturnedSim/ZombieCombat.cs:95`) | hardcode `0.82` (three files already do) |
| point-to-segment distance | `ZombieSpatial.SqrDistanceToSegment` (`core/UnturnedSim/ZombieSpatial.cs:192`) | — note `d` is `b-a`, **not** normalised |
| clamp to [-1,1] | `Mathf.Clamp(v, -1f, 1f)` | write a private `Clamp1` — there are **five** already |
| clamp to [0,1] | `Mathf.Clamp01` (`core/SDG.Compat/Mathf.cs:44`) | hand-roll it (`PlayerVitalsReplication.cs:215` does, in a file that already imports the shim) |
| degrees → radians | `Mathf.Deg2Rad` (`core/SDG.Compat/Mathf.cs:12`) | write `Mathf.PI / 180f` — **five** sites do, and it is not bit-identical to the constant |
| positive modulo / wrap | `Mathf.Repeat` (`core/SDG.Compat/Mathf.cs:48`) | plain `%` — it is wrong for negatives (`game/WorldItemReplicaView.cs:79`) |
| yaw → forward vector | the Godot-frame rule `(-sin yaw, 0, -cos yaw)` | copy it a fourth time — it is inline in 3 places and **one shipped inverted**, aiming 180° behind the player |
| parse `"x,y,z"` from a `.dat` | `UnityDatEx.TryParseVector3` | `Split(',')` + `float.Parse` — **five** hand-rolled copies exist, one of which (`game/CropNode.cs:26`) *throws* instead of defaulting |
| quantize a world position | `PlayerReplication.Quantize` (`core/UnturnedNet/PlayerReplication.cs:458`) | pick your own bit budget; this is THE grid every entity shares |
| rebase an integrated world without teleporting or restarting clocks | `WorldRebase.Translate` + `NetWorldServer/Client.PollNetwork` (`core/UnturnedNet/WorldRebase.cs`; managed barrier `bevy/bridge/OriginServices.cs`) | drain every peer first; shift native physics/geometry too; this does not negotiate a remote UDP epoch |
| expose the managed world through the original UDP transport | `BridgeHost.ConfigureNetwork/ServerTransport/LocalTransport` (`bevy/bridge/NetworkSession.cs`) | `init.listenPort`; same original content hash and commands, bounded shared origin; native lifecycle remains separate |
| join a remote world without creating local authority | `BridgeHost.StartRemote/TickRemote` and `RemoteWeaponView` (`bevy/bridge/RemoteSession.cs`) | wait for actual first owner replica before publishing position; unknown wire metadata stays absent; host operations are refused |
| probe a server without blocking the native frame | `BridgeHost` `serverProbeStart/Poll/Cancel`, generated `SourceServerMenu` (`bevy/bridge/ServerProbe.cs`) | exact original status query; one worker and superseding tokens; no world/player initialization; UI owns retries |
| bind authored map interactions on a remote client | `BridgeHost.ReadMapBindings` / `RemoteMapBindings` (`bevy/bridge/RemoteMapBindings.cs`), native `entities::map_bindings::adopt` / `ServiceManifest::adopt` | accepted server holiday and full manifest counters; minted IDs require unique actual replica kind/wire-pose matches; never register local authority |
| send exact held/attachment intents over UDP | `EquipmentSession`, `EquipmentServices`, `RemoteWeaponView` (`core/UnturnedNet`, `bevy/bridge`) | introduced in protocol62; owner inventory and identity tokens adopt atomically; source transaction helper rechecks token/version/ownership; old gun assertions disabled only with authority adapter |
| render accepted NPC conversation/trade/quest choices | `BridgeHost.ReadNpcView` / `NpcMetadata.Character` (`bevy/bridge/NpcView.cs`) | original core rules over accepted events and inventory/skills; preserve response/vendor row indices; never duplicate gating in Rust or key characters by nonunique numeric IDs |
| build native dialogue/vendor/quest panels | `ui::npc::NpcUiPlugin` (`bevy/src/ui/npc.rs`) | consume `npcView`, retain original response/vendor indices and accepted revisions; emit intents, retain scroll, and clear panels on session changes; pinning is presentation only |
| show the original Information map and accepted quest/roster dashboard | `ui::map::MapPlugin`, `map::geometry`, shared `ui::npc::quest_panel` | source 1920m X/Z extent; cursor zoom and normalized local markers; original overview/tiles only, accepted profile/quest data; infinite mode has no MapUI |
| render an original human NPC and its focused nameplate | `entities::npcs::NpcPlugin`, shared `character::Appearance` / `NameplateEnabled` | source catalog/key identity, posed headwear bounds, original static capsule and `npc` console; accepted dialogue remains managed; normal maps have no fabricated placements |
| publish a scene NPC into host combat geometry | `entities::npcs::collision` → `WorldGeometry.AddCapsule`, using `ZombieCombat.RayCapsule` / `CapsuleNormal` | exact opaque source capsule; playable/listen scene only, keyed removal/rebase/revision lifecycle; remote diagnostics cannot upload authority bodies |
| retrieve an accepted profile picture without snapshot image payloads | `BridgeHost.ReadProfileAvatar` / `ReadProfileRules` (`bevy/bridge/ProfileRead.cs`) | connected live owner plus exact accepted hash; missing/stale/none/pending are distinct; original validator/cache/sanitizer, no authority writes |
| load the original local name/picture/face preferences and publish them on join | `LocalProfileSettings`, `BridgeHost.ReadLocalProfile` / `SendConfiguredProfile`, `profile::LocalProfile` | original environment/config precedence and bounded PNG; opt-in local preferences are separate from accepted server profiles; menu choice persists but never directly changes character replicas |
| present accepted remote players in Bevy | `avatar::remotes::RemotePlayersPlugin` (`bevy/src/avatar/remotes.rs`), shared `avatar::outfit_parts` and `player::StanceHeights` | transform-wire stance differs from combat appearance enum; exclude local owner, clear bodies on session changes; no authority writes |
| choose native network ownership and initialize a session | `network::{Target,State,owns_world,is_networked}` (`bevy/src/network.rs`) | accepted `ownsWorld` gates host publication; client join must adopt the first owner replica before movement; menus do not own authority |
| reset native world coordinates before a UDP session | `WorldOrigin::reset_for_network` (`bevy/src/world/origin.rs`) | also mark the world not-ready so absolute map positions rebuild before initialization; UDP rebasing remains disabled |
| play native local/remote footsteps, landings and swim strokes | `footsteps::FootstepPlugin` / `footsteps::rules` (`bevy/src/footsteps.rs`); shared `impact::surfaces::Surfaces`, `sound::shelter::Shelter` | retain material before gait fallback; cosmetic puddles must not alter grip; no gear rustle or authority writes |
| inspect/save a live throwable's exact age and deadline | `ServerCombat.ProjectileFlights` / `TryGetProjectileFlight` / `ServerRestoreProjectile` (`core/UnturnedNet/ProjectileFlight.cs`) | restore remaining ticks, not a fresh throw; original packet format remains unchanged |
| run wildlife rules without an engine | `WildlifeServices` / generated `AnimalBrain` (`bevy/bridge/`), extracted from `game/AnimalAgent.cs` and `AnimalField.cs` | duplicate wander, fauna rolls or species constants in native presentation; native host uses `hostPosition`/`hostYaw` |
| sample source dominant terrain layers in Bevy | authority: `WorldGeometry.DominantLayer` (`bevy/bridge/TerrainLayers.cs`); native: `world::layers::sample(&geometry.terrain, local_xz)` (`bevy/src/world/layers.rs`); signed grid spacing and midpoint-even edge-clamped samples | infer layer 5 from sea level; it also represents source dry sand. Unknown metadata is **255** |
| run original NPC helicopter control without a second body | `NpcHeliServices`, generated `NpcHeliBrain`, `NpcHeliTerrainGrid` (`bevy/bridge/`) | `DebugThrust` is the source spec constant; upload full terrain grids before `spawnheli`; AI shots use single-use `ReadyNpc`/`CompleteNpc` tickets |
| drive the native NPC airframe outside the local terrain window | `vehicle::npc::NpcFlightPlugin`, existing `VehicleBody` / `Flight::step`, `npc::terrain::support` | only host accepted axes drive physics; warm rotor once; upload full terrain grids and use supplemental original terrain cells, never a second aircraft body or nearby-only height sampler |
| apply original aircraft crew hits and retire their accepted visuals | `NpcHeliServices.RayCrew`, `NpcHeliCrewBody.TakeHit`, `ServerGunProfile.HumanoidDamage` | hull-local source box; captured effective player round, not wildlife damage; protocol67 Humanoid target uses vehicle ID; no invented melee/blast damage |
| render native foliage wind and press/rotor response | `world::foliage::{FoliageSurface, Batch}` and `foliage::disturbance::Field` (`bevy/src/world/foliage/`); reuse `RainState.wind` | keep authored local height when merging instances; grass UV weight is not a canopy height, flowers ignore press, pebbles stay rigid; source field is cosmetic |
| present accepted tree hits/falls without owning resource state | `world::foliage::resources` / `fall::Fall` / `reaction::Reaction` (`bevy/src/world/foliage/`) | adopt accepted age before waiting for dead snapshot; preserve source name+instance identity; seek elapsed timber instead of replaying it; dead state alone supplies no direction; rewards stay in ResourceServices |
| transmit accepted resource hits/falls without creating client authority | `ResourceContactEvent` (`core/UnturnedNet/ResourceContact.cs`), `ResourceServices.Publish`, `ResourceEvents` (`bevy/bridge/`) | protocol63/Event54; original registry index and accepted tick; shared position/direction encoding; no late-join replay or client rewards |
| attenuate native positional audio with the original distance curve | `sound::distance_gain(db, unit, range, distance)` (`bevy/src/sound.rs`) | source inverse distance, +3dB cap and linear max-distance fade; disable additional Rodio distance gain when reusing it |
| query original roof shelter in the native world | `world::rain::roof::RoofMap::{covered,shelter,invalidate}` (`bevy/src/world/rain/roof.rs`) | unknown raw cells remain open; shader shelter blends cover comparisons, never roof heights; fixed cover only |
| write a pos/dir/vel/damage to the wire | `NetWire.WritePos/WriteDir/WriteVel/WriteDamage` (`core/UnturnedNet/CombatReplication.cs:339`+) | choose bit widths yourself — golden byte tests lock these |
| hash replicated state | `NetHash.Mix*` / `HashString` (`core/UnturnedNet/NetHash.cs`) | `string.GetHashCode` (not stable across processes) |
| put an item in a bag | `PlayerInventory.tryAddItem` (`core/UnturnedSim/PlayerInventory.cs:107`) | walk pages yourself |
| move/swap an item between slots | `PlayerInventory.TryDrag` (`:127`) | — it **is** the validator; MP calls the same one |
| count / peek / consume / repair by item id | `getItemCount` `:164` / `peekItemQuality` `:182` / `removeItemAmount` `:197` / `restoreQuality` `:219` | rewrite the page scan (these four are the same walk 4×) |
| solve a power network | `PowerSolver.Solve` (`core/UnturnedSim/PowerSolver.cs:51`) | — engine-free and L0-tested |
| solve a fluid network | `FluidSolver.Solve` (`core/UnturnedSim/FluidSolver.cs:51`) | — a line-for-line mirror of the power one |
| decide if a door may open | `DoorLogic.CanToggle/Toggle` (`core/UnturnedSim/DoorLogic.cs:55`/`:69`) | — SP and MP both route here, keep it that way |
| decide if a bed may be claimed | `BedClaims.CanClaim/Claim` (`core/UnturnedSim/BedClaims.cs:63`/`:73`) | — replicas use `Adopt` `:122`, which skips validation on purpose |
| decide if a hose may connect | `FluidHoseRule.Completion` (`core/UnturnedSim/FluidHoseRule.cs:21`) | — the wire-tool twin is still inline in `PlayerController`, see DUPLICATE_AUDIT |
| check a craft / run a craft | `Crafting.CanCraft/DoCraft` (`core/UnturnedSim/Crafting.cs:40`/`:67`) | — `DoCraft` re-validates itself |
| step deadzone exposure | `DeadzoneSim.Step` (`core/UnturnedSim/DeadzoneSim.cs:103`) | — the *sim* is shared; the volume/bookkeeping layer around it is duplicated |
| find nearby zombies / bullets vs zombies | `ZombieSpatial.QuerySphere` `:71` / `QuerySegment` `:117` | a linear scan — these already fall back to one when it is cheaper |
| format a fluid volume | `FluidDef.Litres` (`game/FluidTank.cs:94`) | — 1 unit = 1 mL everywhere |
| show a look-at info panel | `InfoBillboard` (`game/InfoBillboard.cs`) | build another Label3D rig |
| cut a pitched roof slab so it meets the next slope (ridge, hip, valley, a wall it leans on) | `RoofShapes.PlumbFaces` (`core/UnturnedSim/RoofShapes.cs`) — WallSurface applies it to every pitched `Roof` | cut it square and shift it about: a square cut leaves a V `t/cos(pitch)` deep at every ridge |
| a tapered surface's outline over one solid | `RoofShapes.TaperPolygon` — WallSurface's `ClipToTaper` is a wrapper | re-lerp the insets: a negative inset (cut line outside the surface) must still clip to the rectangle |
| the faces of the convex hull of a few points | `ConvexHull.Faces` (`core/UnturnedSim/ConvexHull.cs`) — outward normal, plane offset, ordered loop | use it past a few dozen points: it is brute force, O(n^4) |
| collect meshes for an outline | — **there is no shared helper**; 3 private `CollectMeshes` copies exist | (candidate for extraction) |

---

### Bevy authoritative zombie adapters

| Need | Reuse | Notes |
|---|---|---|
| run current chunked zombie decisions | `ZombieServices` (`bevy/bridge/ZombieServices.cs`) | generated original Chunk/Flow code; native owns the only HOT physics solve |
| test a source walkability box against exported geometry | `WorldGeometry.OverlapsBox` (`bevy/bridge/GeometryOverlap.cs`) | BVH + triangle/AABB separating axes; surfaces, not solid-volume containment |
| persist zombie flow/population/corpse and native unsticking state | `ZombieServices.Capture/Restore`, `ZombieFlowField.Capture/Restore` | remaining corpse ticks; original outfit seeds and killed-slot refill clocks |
| sample submerged native camera state and warp | `world::underwater::UnderwaterState`, `WOBBLE/WOBBLE_SPEED` (`bevy/src/world/underwater.rs`) | eye-height 6cm/0cm hysteresis; shared palette/veil in `world/water_optics.wesl` |
| resolve original attachment fit, installed IDs, factory irons and shot audio | generated `AttachmentNoise` from `game/AttachmentFit.cs` / `Viewmodel.Snd` | no copied attachment tables; barrel ID9400 is consumed after its one accepted shot |
| resolve native remote-body attachment meshes and mounts | `viewmodel::attachments::{body_parts,bottle_fit}` (`bevy/src/viewmodel/attachments.rs`) | source body sights use SightPos and zero-ID factory irons; magazine zero stays empty; do not substitute first-person optical alignment |
| atomically fit/remove a physical attachment or select magazine/shell ammo | `WeaponState.QueueAttachment` / `AttachmentOptions` (`bevy/bridge/AttachmentServices.cs`), generated `AttachmentTransaction` and `WeaponAmmoActions` | selected-gun and incoming-item tokens; same original placement/round rules; preflight before mutation |
| render source cosmetic bullet flight without duplicating damage | `CosmeticBulletServices` / generated `CosmeticScatter` / `BallisticsMath` (`bevy/bridge`) | accepted combat sequence joins visual context; `state.bullets` is visual only; ocean boxes never enter solid-world damage geometry |
| render original tracer strips and project the actual FP muzzle | `bullets::{tracer,muzzle_projection,fire_context}` / `viewmodel::project_muzzle` (`bevy/src`) | accepted visual flight only; muzzle depth0.6m, max strip10m; no damage/impact publication |
| steady a magnifying optic and show actual player vitals | `ScopeSteadySim` / `BridgeHost.StepSteady`, `motion::ScopeSway`, `ui::vitals` | predictive oxygen must persist between replicas; breath scales phase, not amplitude; infection fill is1−infection |
| carry passenger combat without moving the seat-owned body | `ServerPlayerAuthority.AcceptSeatedActions` / native `player::simulate` | Opt-in state carrier dispatches deduped actions then stops before motion adoption; FP passenger guns only, driver/vehicle melee refused |
| adopt a saved owner pose or accepted furniture anchor without walking rollback | `BridgeHost.StartSession` / `ServerPlayerAuthority.StationarySeatOf`, `WorldServices.Seats` | wait for owner join snapshot before publication; source chair hip/bed root, one entry recovery, release on death/vehicle boarding; walking bounds stay unchanged |
| place an objective-mounted source optic | `viewmodel::scopes::{ScopeView, mapped_objective}` | rigid optical axis and linear HDR target; activate/position before directional shadow cascades |
| apply accepted gun warheads and recover single-round projectiles | `WarheadServices` / `CombatBulletImpact` (`bevy/bridge/WarheadServices.cs`; `core/UnturnedNet/CombatBulletImpact.cs`) | immutable launch payload and actual contact normal; ordinary world-item pickup ownership; no guessed normal for approximate contacts |
| continue source bullets through tagged thin surfaces or water | `ServerCombat.ContinueBullet` / `BulletRayDetailed`, `WarheadServices.Continue` | maximum2 surfaces; source velocity/all-damage costs; one ballistic step without gravity; water stays outside movement/melee/blast masks |
| classify an accepted bullet impact without another physics guess | `WarheadServices.TakeImpact` / `BridgeHost` impact subscription | one reliable ordered fact; exact normal/water/thin and shot identity; coincident pellets remain separate; origin barrier drains before shifting positions |
| render accepted warhead blasts and frozen recovered ammunition | `projectiles::effects::accepted_blast`, `impact::receive`, `entities::items::rotation` | one retained event per contact; exact water/normal wins over local probe; frozen root pose survives load/origin shift; no native damage |
| share original pellet scatter with actual damage flight | `ServerCombat.ResolveBulletDirections` / `CosmeticBulletServices.Launch` | one accepted source sampling pass; profile owns count/speed and packed command owns origin; burst count is server-derived; null keeps legacy center ray |
| publish accepted gestures and original worn/held appearance | original `PlayerAppearanceNetSync` compiled by the bridge | source5Hz before replication; `player.gesture/arrested` read the client replica; one-shot gestures remain events; pending requests are cleared on session teardown |
| recover an analytic capsule contact normal | `ZombieCombat.CapsuleNormal` (`core/UnturnedSim/ZombieCombat.cs`) | use the same foot position, radius and height as `RayCapsule` |
| share local tactical, handheld and worn-light state | `viewmodel::lights::{Switches, LightSurface, LightInput}` | actual accepted equipment; ignition consumes its shared key for drivers; third-person laser emitter uses `avatar::AvatarMuzzle` |
| share original lamp/lens masks between owner and remote equipment | `viewmodel::lights::{EmissionMasks,apply_surface_emission}`, `avatar::remotes::lights` | cache immutable brightest-colour masks, retain per-player materials; pending albedo stays dark; remote accepted flags create no extra spotlights |
| render original world names and accepted profile images | `character::nameplate::{Nameplate,decode_picture}`, `avatar::remotes::profiles` | source font/plain text, real depth and60m cutoff; None means no picture, corrupt PNG means checkerboard; query bytes by accepted player/hash and retry late arrival |
| pose a chair or reclined bed without rotating the controller | `world::interactables::OccupiedPropSeat::avatar_pose`, `avatar::remotes` | source feet/hip anchor and eye offset; accepted occupancy plus authored recline flag, fresh yaw each frame; third person retains shared shoulder camera |
| materialize original fallback bed furniture | `world::beds::BedPlugin`, `--demo-furniture` | opt-in original seeded map anchor; independent bed/seat ID spaces, host registration and accepted claim/recline replicas; ordinary retail worlds remain unchanged |
| damage an original fallback bed from accepted combat | `WorldServices.AttachBarricadeDamage`, `WorldGeometry.RayDynamicKey` (`bevy/bridge`) | original200HP and VehicleDamage through the existing authority callback; nearest world obstruction wins, retire claim/seat/geometry together, never infer client damage or revive stale uploads |
| render and interact with original fallback hinged doors | `world::demo_door::DemoDoorPlugin`, `InteractionFocus::{offer_manual,selected}`, `WorldServices.RegisterDoorBody` | `--demo-furniture` only; accepted tap/hold/open/lock and 250HP retirement; fixed-hinge triangles/noise, host-only registration, original unified closing-arc limitation |
| render accepted dropped items and real stack prefixes | `entities::items::{Models, Focus}` (`bevy/src/entities/items.rs`) | original dropped manifest, not held art; source .24m rarity fallback and transformed15% focus box |
| reproduce source transparent-item main depth and shadows | `entities::items::material::{AlphaDepth, AlphaDepthPlugin}` (`bevy/src/entities/items/material.rs`) | mark a Blend mesh; companions share its texture/mesh/layers/skin, use main0.99 and shadow0.1, retire with the parent |
| dress the local body and rebuilt first-person arms from accepted state | `avatar::{LocalAvatar, FirstPersonArms}`, `character::{Appearance, CharacterLayers, CharacterUpdate}` | source fixed skin/profile face; do not create another owner body or mutate worn items |
| find the actual third-person held muzzle after transforms | `avatar::{AvatarMuzzle, AvatarMuzzleUpdate}` | world-space presentation only, never replace authoritative eye origin |
| read unapplied camera recoil for the original third-person HUD bloom | `recoil::Recoil::pending_pitch()` | vertical degrees after the native 18/s drain; do not create a separate shot/recoil accumulator |
| use source shoulder/vehicle chase cameras and eye-plane convergence | `avatar::camera::{follow, sweep, aim, VehicleCameras}`, `avatar::Perspective` | radius0.39, length2m on foot; collide against world/vehicle and exclude owner/dropped items |
| partition original native vehicle triangles into articulated parts | `content::ObjData::split_zones`, `vehicle::turrets::build` | every corner must be inside one zone; preserve UV/color/normal data; mount parent uses yaw only, never its pitch |
| animate original tank hatches and republish moving mount geometry | `vehicle::turrets::{Mounts,animate}`, `vehicle::geometry::upload` | accepted occupancy plus source0.8s curves; player-only separate turret/barrel bodies; mount signatures invalidate stationary-hull uploads |
| resolve and fire an accepted source vehicle gun | `MountedWeaponServices::{IndexFor,Step}`, `ServerCombat.ServerLaunchMounted` | accepted seat/type selects original belt/cycle/muzzle; no held-inventory mutation; trusted NPC owner0 is explicit; protocol64 snapshots/events work remotely |
| select and drive a native mounted weapon | `vehicle::turrets::input::{Operator,send,crosshair}` | accepted seat priority/slot/belt; intent refreshed before each 50 Hz tick; SP pause queues nothing; own nested colliders excluded from camera aim; never local damage |
| render an accepted mounted muzzle report | `vehicle::turrets::fx::MountedEffectsPlugin`, `fx::voices` | original flash/audio/light/smoke from deduplicated mountShot; player cannon scale2.2 and local spring; source NPC scale1; native effects never launch authority |
| present original helicopter door crew | `vehicle::turrets::crew::CrewPlugin`, `vehicle::damage::WreckMaterial::handle` | accepted crewAlive and catalog appearance only; hull-local GunnerAt, original bind pose and +90Z hook gun, no native health/collider; live wreck crew stays visible and charred; ancestor marker excludes every crew mesh from hull BVH uploads |
| preserve original vehicle battery, beam switches, alarm and shootable lamps | `VehicleLightingServices`, extracted `VehicleLightState`, `VehicleLightingCommand`/`VehicleLightingEvent` | protocol68 protects light/alarm flags from body streams; use current swamped, not permanent engineDrowned; lamp centers stay hull-local |
| preserve shootable vehicle glazing, free indexed repairs and manual horns | `VehicleMaintenanceServices`, `VehicleMaintenanceCommand`/`VehicleMaintenanceEvent` | protocol69 canonical pane/tire masks; current hull-local pane transforms; broken triangles stop blocking immediately; source horn gates and one authority hearing alert |
| render accepted vehicle panes and preserve real player apertures | `vehicle::glazing::GlazingPlugin`, `vehicle::exit::probe` | original ordered files/canopy fallback; actual tire availability; host metadata/triangle tags; accepted mask drives visibility and zero-mass player-only colliders; exit excludes all own descendant bodies but retains own deck for ground |
| teleport an accepted seat exit without a kinematic sweep | `player::sync_seated_body`, `world::origin::move_physics_owner` | reset current and next translation/rotation before enabling the standing collider; Transform-only movement can impart the seat-to-door displacement as crash velocity |
| resolve original vehicle hood/door focus through actual visible collision | `vehicle::access::{focus,resolve}`, `VehicleAccess` (`bevy/src/vehicle/access.rs`) | source2.6m clipped camera ray and0.16m endpoint sphere; select vehicle before all-zone fallback; player query membership ignores stripped driving hulls; stable ties and existing-focus priority |
| request accepted lightbar patterns and indexed vehicle repairs from original menus | `ui::vehicle_controls::{input,mechanics}`, `Backdrop::ammo_pie` | Ctrl220ms; cancellation wins coalesced edges; persistent pane/lamp/tire widgets use canonical accepted masks; source catalog display names; no local damage or inventory mutation |
| play an accepted manual vehicle horn without local duplication | `vehicle::lighting::presentation::events`, `VehicleMaintenanceServices` | driver input requests Horn; only accepted vehicleHorn creates the original clip; cooldown and32m hearing remain authoritative, engine hearing stays native |
| render accepted source vehicle beams, navigation lamps and lightbar clocks | `vehicle::lighting::{geometry,presentation,state}` | split original lenses once; catalog beam constants; accepted flags/masks only; 50 Hz siren and rotor beacon clocks; source material alias and repair behavior |
| render accepted glass feedback across reordered snapshots/events | `particles::glass::GlassShards`, `vehicle::lighting::breaks::BreakColors` | source untextured flat shards and original sound; bounded per-lamp color history; never compare process-local tick to accepted server tick; no effects on late-join masks |
| split original vehicle lamp geometry and reuse its source emitter locations | `vehicle::lighting::geometry::{build,BuiltLighting}`, `ObjData::split_where` (`bevy/src/content.rs`) | triangle attributes/winding preserved; original side/zone branch order, material aliases, Kelvin hull classification and aircraft lens clusters; hidden amplifier is not a visible collider |
| place accepted vehicle occupants at their original body anchors | `ServerVehicles.{BeforeOccupantStep,OccupantPoseOf}`, `MountedWeaponServices.{Prepare,OccupantPose}` | source bodySeats/full hull basis; tank seat uses cannon yaw and accepted3/s rise before combat; protocol65 publishes gunnerRise |
| resolve native tank body and optic anchors | `vehicle::turrets::Mounts::{seat_body,seat_eye}`, `vehicle::adopt_occupancy` | full hull transform; gunner positions use turret yaw only; eye orientation stays hull-relative; accepted rise used once; explicit periscope override avoids source static initializer zero |
| place ordinary first-person vehicle eyes and driver peek | `avatar::camera::VehicleCameras::seated_eye` (`camera/seated.rs`) | previous completed Skull pose through current hull; missing bone differs from unposed; source 40° deadband and 240/360 side divisors; optic overrides replace skull+peek |
| advance remote aircraft blades and engine audio without flight simulation | `vehicle::flight::Flight::advance_presentation` and `remote::advance_aircraft_presentation` | one fixed 50 Hz clock across owner handoffs; source ignition re-arms only after full stop; wire does not carry spool age or rotor health |
| switch an accepted occupant to another source vehicle seat | `ServerVehicles.{CanEnter,ServerEnter}`, `VehicleReplication.ApplyEntered` | same server-held vehicle only; explicit free different seat; departing driver loses authority/input, velocity and ignition persist; reliable replica clears only that owner's prior slot |
| report source vehicle seat capacity to a bridge client | `BridgeHost.VehicleSeatCount`, `VehicleReplication.SeatsOn` | SeatCount is host-only, never read from decoded replica; remote TypeId resolves original fleet, unknown stays null; sparse passengers are occupants, not capacity |
| exclude only a mounted round's firing vehicle from its entire flight | `ServerCombat.MountedRayDetailed`, `BridgeHost.MountedWorldRay`, `WorldGeometry.RayExcludingVehicle` | capture vehicle ID at accepted launch; carry through cosmetic and barricade identity queries; never infer from current seat or alter handheld rays |
| present accepted mounted flights on a remote client | `MountedFlightEvent`, `RemoteMountedFlights`, `MountedWeaponServices.ProfileFor` | protocol66/Event56 continuation, retirement and host-applied blast; reuse source math on observed snapshot ticks; never client geometry or damage |
| render tank periscope/gunsight and read its mouse scale | `ui::tank_optics::TankOptics::{mode,zoom,look_sensitivity}` | accepted FP seat/hatch gates; source8→12→4 cycle; input after PerspectiveInput, FOV after PlayerCameraUpdate; never mounted authority |
| billboard accepted mounted muzzle stars in every camera | `vehicle::turrets::fx::material::{source_material,roll_tag}` and `flash.wesl` | source HDR2.6/Add, per-instance negative roll, current-view basis; use NoFrustumCulling and NotShadowCaster |
| preserve source per-light attenuation, headlight fades and shadow bias | `hdr::SourceFalloff::{new,with_headlight_fade}` (`hdr/light_units/metadata.rs`) | exact thousandths in reserved GPU flags; count-guarded clustered upload; energy60–85m, shadow50–75m smoothstep; marked finite→infinite shadow bias; reflection copies inherit profiles |
| blur prior world/HUD beneath a dashboard or AmmoPie modal | `ui::backdrop::Backdrop::{dashboard,ammo_pie}(fade)`, `AboveDashboard` | caller supplies eased fade; source sRGB Gaussian mips and 25 taps; dashboard layer11 flat tint keeps vitals12 sharp, AmmoPie60 blurs vitals with its vignette; mechanics remains dim-only |
| animate original dashboard open/close/reversal and map cursor release | `ui::swoop::{Swoop,replace}`, `UiState::captures_input` | source85ms clock survives widget rebuilds; retired UI cannot act; closing Information uses layer9 and retains cursor until Closed, tab switches never recapture; continuous drive controls honor capture while discrete vehicle keys remain live |
| apply accepted local camera shake without changing aim | `player::flinch::{ExplosionFlinch, FlinchUpdate}`, `recoil::Recoil::shot_pitch()` | blast radius metres/magnitude degrees; one source4/s spring after base camera; TP gun uses35% of the existing accepted pitch roll; interaction rays follow final camera |
| preserve source player clearance and mounted aim collision masks | `player::{player_collision_groups,throw_clearance}`, `vehicle::turrets::input::crosshair` | physical probes use player surfaces; ThrowOrigin excludes source mesh/pane hit layers but keeps fallback chassis; mounted ALL-membership must still skip stripped driving hulls without hiding zombie hitboxes |
| derive native physical eyes and aim under leaning | `player::lean::LeanState::{eyes,aim}`, `player::lean::pivot` | angles: yaw/pitch degrees, lean radians; foot pivot is yaw × lean before pitch; Alt head-look does not steer the weapon |
| enter real player damage for debug death rendering | `BridgeHost.PlayerDebugConsole` (`bevy/bridge/PlayerDebugConsole.cs`) | `kill`/`sethp` obey cheats and queue `DamagePlayerExternal`; no direct health mutation |
| emit host hearing with original rain masking | `ZombieServices.Noise(position,radius)` | zero is silent; source radius and authority weather |
| render filtered weather audio, shared canopy targets and source lightning | `world::rain::audio::{filter::FilteredClip, MaterialTargets}`, `world::rain::lightning::Lightning` (`bevy/src`) | four Godot low-pass stages; cosmetic emitters never enter hearing; lightning flash uses render time, interval uses authority ticks |
| convert original nonphysical light energy into native renderer units | `hdr::{directional_illuminance,point_lumens,ambient_brightness}` (`bevy/src/hdr.rs`) | derived Godot pi factors and Bevy default exposure; do not restore the old visual18klux calibration |
| mirror tagged world lights onto first-person content or read the nearby light level | `viewmodel::lighting::{WorldLight,NearbyLight}` (`bevy/src/viewmodel/lighting/world.rs`) | exact source dynlight membership; 10 Hz, four pooled mirrors; shared sRGB level includes every candidate and updates while unarmed |
| select and render accepted player gestures without optimistic animation | `avatar::gestures::{Definition,Playback}`, `avatar::held::apply_upper_body` (`bevy/src`) | source catalog owns rules; loop replicas and one-shot events are separate; upper-body overlay preserves locomotion legs |
| size/crop the first-person viewport or project its pixels into the window | `hdr::viewport::ViewmodelViewport` | source1.15×1.45 target, nearest-even pixels, width-locked FOV; crop-only muzzle/UI projection, separate negative lean image roll |
| apply goggles to world/arms while preserving HUD colors | `nightvision::NightVision`, `hdr::CompositePass` (`bevy/src`) | consumes actual local switch; source-sRGB tube pass follows independent world/VM ACES and precedes HUD; overlay dim/tint remain separate |

## 2. Sentinels that lie

The single highest-value table in this document. Each of these returns a value that is
indistinguishable from a legitimate result:

| Call | Returns on failure | Collides with |
|---|---|---|
| `Items.getIndex(x,y)` | `byte.MaxValue` (255) | nothing — but it is **not** `-1`, and it is not `0` |
| `ZombieCombat.RayCapsule` | `-1f` | also returns `-1f` when the hit is *behind* the origin, so an origin inside the capsule reads as a miss |
| `BedClaims.OwnerOf` / `ServerInteractables.BedOwner` | `0UL` | a bed that exists but is unclaimed |
| `SkillsReplication.ServerAward` | `0` | a player whose real XP total is 0 |
| `PlayerInventory.peekItemQuality` | `100` | a genuinely pristine item |
| `ServerCombat.AmmoOf` | `-1` (unknown player) | distinct from `0` = empty — do not conflate |
| `GasStationServer.Percent` | `0` | a genuinely empty station |
| `WorldClockReplication.TimeOfDayAt` | `0` (no clock configured) | midnight |
| `ZombieRegions.RegionOf` | `-1` | correctly reads "cold" everywhere; `BoundsOf(-1)` **throws** |
| `ItemAsset.makeLoot(unregistered)` | a valid `Item(id, 1)` | a real item — it does **not** return null |
| `Assets.find` / `findByGuid` | `null` | very common during boot before `ItemCatalog.RegisterAll` |
| `NetPakReader.ReadString` | `""` on **every** failure path, never null | an intentionally empty string — **check the bool return, not the value** |
| `.dat` `ParseBool("Flag")` on a bare flag | `false` | an explicit `false`. A bare flag is `DatValue(null)`. Read flags with `ContainsKey` |
| `.dat` `GetString("Flag", "fallback")` on a bare flag | `null` — **not** the fallback | — |
| `ParseListOfStructs` (missing key) | `null`, not empty | — |
| `FluidNet.ResolveNetType` | `FluidType.None` | a genuinely untyped fitting |
| `Bed.TryGetSpawn` / `BedClaims.TryGetSpawn` | `false` = no bed, use map spawn | — this is normal, not an error |
| `Door.TryGetByNetId` | `false` | normal — door outside this client's world |
| `DeployableDef.ById` | `null` | fail-closed by design; `DeployableReplicaView` never materialises |
| `ZombieSim.Damage` | `false` | means **both** "survived" and "unknown/already dead". `true` = *this call killed it* |
| `DestructibleReplication.IsAlive(out of range)` | `false` (dead) | **`game/DestructibleField.cs:49` returns `true` (alive) for the same input** — genuine divergence |

---

## 3. Reach, range and rate constants

Server validators are deliberately looser than the client UI. Do not "fix" a mismatch
without checking which side owns the rule.

| Constant | Value | Where |
|---|---|---|
| `PickupReach` | 6 m | `core/UnturnedNet/ServerTransactions.cs:36` |
| `PickupFacingMinDot` | 0.25 (~75° half-angle) | `:44` |
| `PickupFacingSkipRange` | 1.5 m (cone skipped inside) | `:48` |
| `CropReach` | 6 m | `:52` |
| `PlaceRangeSlack` | +4 m on the def's own `Range` | `core/UnturnedNet/DeployableReplication.cs:319` |
| `WireReach` (server) | 16 m per endpoint | `:322` |
| wire look reach (SP) | 5.5 m + 40 m/20-node polyline cap | `game/PlayerController.cs:351`/`:352` — **server has no length cap** |
| `StorageReach` | 4 m (SP opens at 2.5) | `core/UnturnedNet/InventoryReplication.cs:210` |
| `InteractReach` | 4 m | `core/UnturnedNet/InteractableReplication.cs:109` |
| `EnterReach` | 6 m **from vehicle centre** | `core/UnturnedNet/VehicleReplication.cs:695` |
| `CoupleReach` / `HitchReach` | 1.6 m / 3.5 m | `game/Vehicle.cs:117`/`:119` |
| `TowRestMin` / `TowAttachReach` / `TowBreakLen` | 2.0 / 4.5 / 7.5 m | `game/Vehicle.cs:136`–`:138` |
| `DoorLogic.ToggleCooldown` = `BedClaims.ClaimCooldown` | 0.75 s (two literals, one rule) | `DoorLogic.cs:27`, `BedClaims.cs:21` |
| `SimClock.FixedDelta` | 0.02 s — also `NetProtocol.TicksPerSecond`=50 and `BallisticsMath.StepSeconds` | three declarations of one 50 Hz |
| `SeaLevel` | 25.6 (PEI water plane) — also `Deployable.WindSeaLevel` | `game/DeployableDef.cs:63` |
| player envelope | 7 m/s sprint × 1.25 slack; down-rate 110 m/s | `core/UnturnedNet/PlayerAuthority.cs:154`–`:181` |
| `OutOfBoundsFloorY` | **−250**, not the SP shell's −1030 | `:212` — because the wire clamps Y at −256 |

---

## 4. Wire encoding budgets

Changing any of these breaks golden byte tests and cross-version compat. They are
listed so you can *read* a packet, not so you can retune one.

| Field | Encoding | Real range / grain |
|---|---|---|
| position X/Z | `ClampedFloat(13,8)` | ±4096 m, ~4 mm (integer bits include sign) |
| position Y | `ClampedFloat(9,8)` | ±256 m |
| direction | 3× `SignedNormalized(12)` | ~0.03° |
| velocity | 3× `ClampedFloat(6,6)` | **±32 m/s** (the doc comment saying ±64 is wrong) |
| damage | `ClampedFloat(9,4)` | ±512 HP, 1/16 |
| yaw / pitch | `Degrees(11)` | wraps to `[0,360)` — **wraps, not clamps** |
| deployable health/fuel | `ClampedFloat(12,2)` | max 4095.75, ¼ |
| vehicle fuel / hull health | `ClampedFloat(20,1)` / `(17,1)` | protocol69 max 524287.5 mL / 65535.5 HP; original fleet requires 300000 mL / 40000 HP |
| vitals (food/water/stamina/infection) | `UnsignedNormalized(8)` | 0..1 |
| time of day | `UnsignedNormalized(16)` | ~1.3 s of a PEI day |
| string | 1 bit null/empty + 11 bit len + UTF-8 | max 2048 **bytes**; oversized writes as EMPTY; **not thread-safe** (shared static buffer) |

Three special paths inside `WriteClampedFloat` are load-bearing and regression-guarded
(`NetPakClampedFloatTests:28`): under-range → all zeros, over-range → all ones, and
`|value| < 0.0001f` → decodes to exactly `0.0`. The bias is applied to the **floored
int**, not the raw float — biasing the float caused a +1.0 decode error just below an
integer. `WriteUnsignedClampedFloat` deliberately lacks the near-zero case.
`WriteSignedNormalizedFloat` is sign-magnitude (two encodings of zero) and does **not**
clamp — which is the entire reason five private `Clamp1` copies exist.

Decoder hazards: `ReadDateTime` is the one decoder that can **throw** on hostile input;
`ReadFloat` does no NaN/Inf filtering; `WriteBits` masks rather than clamps (300 in 8
bits yields 44); `WriteStateArray` length is an unchecked `(byte)` cast.

---

## 5. Subsystem reference

### 5.1 `core/UnturnedSim/` — engine-free rules

Everything here is L0-testable and has no Godot dependency. That is the point: if a rule
lives here, SP and MP cannot drift.

- **`SimRoot` / `SimClock`** — `Frame(delta)` converts one engine frame into N fixed
  0.02 s steps. **Registration order is execution order** and is load-bearing
  (replication must register last). `Advance` caps a stall at 0.33 s, so a 10 s hitch
  produces ≤16 catch-up steps, not 500.
- **`PlayerMovementSim.Step`** — no acceleration model; horizontal velocity is
  instantaneous. Input magnitudes >1 normalise down, <1 do **not** scale up (analog
  sticks work).
- **`PlayerVitalsSim.Step`** — returns `true` **only on the step health crossed ≤0**.
  Order is load-bearing: infection decays *before* the sick test. `Multipliers.None` is
  the neutral value; a `default` struct freezes every rate to 0.
- **`PlayerStanceSim.Step`** — mutates internal edge state; calling it twice per tick
  double-toggles. `currentCapsuleHeight <= 0` is the "first tick, skip the headroom gate"
  sentinel.
- **`ZombieSim`** — rows, not nodes. `PositionOf` returns **feet**, not centre. Row
  indices go stale across a despawn; hold a `ZombieId` and re-resolve with `TryGetRow`.
  `Attacks`/`Deaths` spans live exactly one tick. `Hear(pos, loudness)` takes **metres of
  carry**, not decibels.
- **`Items` / `PlayerInventory`** — the grid. `addItem` performs **no space check**
  (`checkSpace*` is the caller's job). `loadSize` **silently discards items that no
  longer fit** — that is how un-equipping a bag loses its contents. Page layout:
  `SLOTS=2, BACKPACK=3, VEST=4, SHIRT=5, PANTS=6, STORAGE=7, AREA=8`.
- **`Crafting`** — `MeetsSkill` **fails open** three ways (no requirement, null skills,
  *unrecognised* skill tag). `DoCraft` is not transactional and silently skips outputs
  whose GUID fails to resolve — you can lose supplies and get nothing if content is
  missing.
- **`PlayerSkills`** — every multiplier is a fixed shape: `1 - mastery*k` for reductions,
  `1 + mastery*k` for gains, `1/(1 ∓ mastery*k)` for the two interval→rate inversions.
  `DexterityReloadSpeed` is a **speed** — divide the duration by it.
- **`Layers.cs`** — the verified 32-slot retail layer table. **It has zero callers.** The
  game invented its own ad-hoc Godot numbering (`ZombieController.cs:82` uses bit 1 where
  retail ENEMY is 10). Either adopt the table or delete it; today it is a trap that looks
  authoritative.

### 5.2 `core/UnturnedNet/` — replication

- **`CommandRegistry.Register<T>` is the one validation choke point.** parse → validate →
  apply, with a separate diag counter per rejection class. A `null` validator means "no
  authority check" and is a deliberate choice at each site, not a default.
- **`TryDispatch(data, senderPlayerId)`** — `senderPlayerId` **must** come from the
  transport, never the payload. That is the entire anti-spoof guarantee. A throwing
  handler is caught and counted, never rethrown (fuzz-proof by construction).
- **`IReplicatedSystem`** — `WriteFull` / `WriteDelta` / `ReadSnapshot` / `StateHash`.
  `StateHash` **must** be order-independent: every implementation sorts by NetId first,
  because `NetEntityRegistry.Ids` is dictionary order.
- **`SnapshotComposer.Compose`** — a block that would overflow the budget is emitted as
  an **empty** block; framing stays valid and the system's baseline pins so the next
  delta carries everything the skips withheld. `EnableSyncCheck` must only list systems
  every client mirrors *completely* — owner-only and relevancy-filtered systems
  false-alarm by design.
- **`Relevancy`** — `ShouldWrite` keeps writing a newly-relevant entity into every delta
  until an ack proves the client saw it. `CollectRemovals` **appends**; it does not clear.
- **`ServerCombat.DamagePlayerExternal`** is the public non-weapon damage entry (fall,
  starvation, deadzone, blast). It **enqueues** rather than applying, so the hit lands at
  the next `Step` with the live tick — applying at a stale tick marks state dirty at a
  tick the sync-check already reflects, i.e. a phantom desync.
- **`ServerCombat.Step` uses `== tick` equality** for reload completion and respawn. If
  `Step` is not called every tick those are skipped *forever*, not merely delayed.

### 5.3 `game/` — the Godot layer

- **`PowerNet.Recompute`** applies `PowerScale` to **Output ports only**. The replicated
  adapter does not apply it at all — see DUPLICATE_AUDIT §A.
- **`ConnectionPort.PortLayer`** = `1<<8`, **`HosePort.PortLayer`** = `1<<11`,
  deliberately distinct so the wire ray never picks a fluid port.
- **`InfoBillboard.SetBar(i, …)`** — `i` must be 0/1/2 and **the icon per row is fixed at
  build time** (health/fuel/stamina). Callers reusing row 1 for a wind bar get the fuel
  icon regardless of the colour passed. Out-of-range `i` silently returns.
- **`Vehicle.LookRayHitsHull`** tests real oriented box hulls in their own local frames —
  no world-axis bloat, which is why a diagonal 16 m trailer no longer swallows the cab's
  airspace. It deliberately does **not** skip `.Disabled` hulls.
- **`Vehicle.MakeSmoke`** generates mipmaps on the runtime-loaded image. Without them a
  minified sprite samples black — that was the "stationary black smoke cluster" bug.
- **`Bed.Spawn` records position before `AddChild`** because `_Ready` registers the
  claim; assigning `GlobalPosition` afterwards registered every bed at the origin.
- **`StreetLight.Make`** derives its ±5% brightness jitter from a stable hash of the world
  position, so a fixture looks identical every load and for every MP player.

### 5.4 `.dat` parsing

Key lookup is **case-insensitive** (`OrdinalIgnoreCase`). All numeric parsers use
`NumberStyles.Any`, so `"(5)"` parses as **−5**, `"1,000"` as 1000, and `"NaN"`/
`"Infinity"` parse *successfully* for floats. `DatParser` never throws and never returns
null — errors accumulate in `ErrorMessages` and are **cleared on the next `Parse`**.
`DatValueEx` (node-level) does **not** null-check; only the `DatDictionaryEx` layer is
safe against missing keys.

Colour parsers are asymmetric and it is easy to grab the wrong one:
`TryParseColor32RGB` fails to **opaque** black, `TryParseColor32RGBA` to **transparent**
black; `ParseColor32RGB` forces `defaultValue.a = 255` (your alpha is discarded).
`LegacyParseColor` reads `_R/_G/_B` as floats `[0,1]`; `LegacyParseColor32RGB` reads the
same keys as bytes `[0,255]`.

### 5.5 Compat shims — a footgun worth knowing, not a bug

Two `Mathf` types coexist: `game/` compiles against `Godot.Mathf`, `core/` against
`core/SDG.Compat/Mathf.cs`. They genuinely differ in three places —
`Sign(0)` (Godot `0`, shim `1`), `Min/Max(NaN, x)` (Godot `NaN`, shim `x`), and
`Clamp` with `min > max` (Godot **throws**, shim returns silently).

**As of this sweep none of the three has a reachable call site**, and `RoundToInt` —
the one most often assumed to differ — is banker's rounding on *both* sides. Verified
empirically against `GodotSharp 4.6.2`, not from memory. Be aware of it when moving code
across the boundary; do not "fix" it by unifying the types.

`core/SDG.Compat/Quaternion` is missing `LookRotation`, `Slerp`, `Inverse`, `AngleAxis`,
`eulerAngles`, and `operator*(Quaternion, Vector3)` — a real gap for orientation maths in
`core/`. `Quaternion.Euler` is ZXY intrinsic (Unity order), not ZYX.

---

## 6. Maintaining this document

When you add a function that another subsystem could plausibly want, add a row to §1. If
it returns a sentinel, add a row to §2 — that table is the one that earns its keep.

Companion documents:
- `docs/FUNCTION_REFERENCE.md` — the per-function detail behind this index: signature,
  inputs with units and sentinels, failure path, and a verified example call site, for 675
  functions. This file is the front door; that one is what you read once you know which
  function you want.
- `docs/DUPLICATE_AUDIT.md` — known duplicate implementations and their status.
- `docs/SP_MP_PARITY_GAPS.md` — where the SP and MP paths knowingly differ.

### Authored weapon content (MAC-10 integration, 2026-10-05)
- `RiggedCharacter.GunClipFor(name, suffix, fallback)` — own clip, explicit named donor, existing fallback; shared by 1P and 3P hold selection.
- `Viewmodel.MagazineVisualFor(name)` — shared resolved magazine mesh and gun-local mount; prevents 3P/world viewers substituting Eaglefire's mount.
- `AuthoredGunProfiles.Install(server)` — scoped new-gun server profile binding, owned held weapon only; other gun behavior unchanged. See `MAC10_INTEGRATION.md`.
- `UnturnedSim.ShotCadence` — opt-in 50 Hz rational RPM phase; `CanFire` is read-only, consume with `AcceptShot` only after all other checks. Late shots reanchor; legacy-only instances stay inactive. `IsCoolingDown` protects switching; no state is serialized.


### Bevy crafting presentation

| Need | Reuse | Notes |
| --- | --- | --- |
| Display an original blueprint title | `SourceCraftingMenu.Title` (extracted unchanged from `game/inventory/CraftingMenu.cs` by `bevy/bridge/extract_data.py`) | Output item/quantity, then owner, then authored name/operation; `BlueprintDef.Name` is often empty and is not the display label |
