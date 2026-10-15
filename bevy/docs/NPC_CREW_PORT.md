# Original helicopter door crew presentation

`vehicle::turrets::crew::CrewPlugin` materializes accepted `vehicleMounts[].mounts[].crewAlive` for mounts with an original nonzero `GunnerAt`. The `npcCrew` catalog metadata supplies the source rig, face, skin tint and gun rotation. Both integrated hosts and UDP observers consume this same accepted state. The renderer neither creates crew health nor publishes collision geometry.

The current source behavior is deliberately static. `Vehicle.EquipDoorGunners` parents each `TargetDummy` directly to the hull at `GunnerAt`, with no root rotation. `TargetDummy._Ready` builds `rig.json` with face 19 and skin `(0.82,0.66,0.52)`. `RiggedCharacter.AttachGun` places the mount's actual gun art on `Right_Hook` (fallback `Right_Hand`) at +90 degrees around Z. Despite its old comment, that method starts no animation. There is no seated, aiming or firing clip, magazine/sight attachment, or character muzzle flash in this path. Native crew therefore retains the original bind pose and does not swivel with the independently traversing mounted gun.

If the rig is missing, the renderer uses the six original `Humanoid.Build` boxes, dimensions and fallback colors. If catalog metadata has not arrived, materialization waits rather than inventing appearance. Accepted death removes only that gunner's visual subtree. Vehicle retirement, disconnect and session replacement remove all descendants and cached identities. Crew roots have no native rigid body or collider; the managed adapter owns the original box and damage rules.

`Vehicle.Explode` does not kill its crew. Its `CharWreck` recursively overrides every mesh with the source dark, rough, double-sided material. The native crew adapter shares `vehicle::damage::WreckMaterial`, applies it after character dressing and retains live crew until an accepted death. This also covers an already-exploded vehicle whose catalog and character material arrive later. Face, skin and held gun all receive the override without changing authority state.

Validation uses the real rig, face and weapon assets. Four focused ECS tests cover hull-local transforms under tilt, independent mount traverse, the actual +90Z hook axes, no playing clip, accepted death/session/parent cleanup, late catalog, missing-rig fallback, and late-loaded living crew on a wreck. The Vulkan fixtures were rendered and opened:

- `artifacts/bevy-huey-door-crew.png`: both original Huey crew, with the near doorway/body/face visible.
- `artifacts/bevy-orca-door-crew.png`: opposite-side Orca crew and original Nykorev art.
- `artifacts/bevy-huey-door-crew-accepted-dead.png`: same camera after one accepted crew death; the near doorway is empty and opposite crew remains.
- `artifacts/bevy-huey-door-crew-wreck.png`: living crew receives the source char override; frame-time assertions also verify the exploded flag and actual skin material. The fixture isolates crew charring and deliberately retains the ordinary hull material.

These are explicit accepted-replica presentation fixtures. They do not establish playable helicopter AI flight, a live shot-to-crew-death network journey, or reference-lighting pixel parity. The source's awkward static arm pose and gun/hull overlap remain source behavior, not a deliberately animated improvement.

Reproduce with the native test executable under Xvfb/Vulkan:

```sh
env -u WAYLAND_DISPLAY VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
  UG_CREW_VEHICLE=huey UG_CREW_SHOT=/tmp/crew.png xvfb-run -a TEST_EXECUTABLE \
  vehicle::turrets::crew::tests::visual::render_original_door_crew --ignored --exact --nocapture
```

`UG_CREW_SIDE=1` changes side, `UG_CREW_DEAD=1` supplies one accepted dead mount, and `UG_CREW_WRECK=1` tests the crew char override on an exploded replica. The last fixture intentionally isolates the crew override; it does not install the whole vehicle explosion-effects plugin.
