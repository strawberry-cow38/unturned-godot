# Scope steadying and player vital bars

The source `ScopeSteadySim` runs in the managed player shell before the existing
player-state publication. Holding the Sprint control while actually aiming
through an optic above1x requests steadying. `MoveInput.ButtonSteady` carries the
accepted hold to the original server vitals implementation; the server owns its
oxygen cost. The predictive client copy keeps its drained oxygen between
snapshots exactly as `PlayerController` requires. Discarding this prediction
skips the reserve floor behind the quantized replica and never enters lockout.

The source hold spends the upper80% of oxygen in6s. At20% it releases, never
spending into drowning damage. The2.5s lockout counts down only while the control
is released, and oxygen must recover to35% before rearming. Engagement and
release use the original asymmetric exponential blend. Init/load/death clear
the transient shell state; pausing does not step it. `simulate` updates intent,
while each authority `tick` steps it once.

Native `viewmodel::motion::ScopeSway` uses the source oscillator:

- pitch/yaw follow `sin(0.75*t)` and `sin(t)` with source4/s smoothing;
- amplitude is `(1−1/zoom)*1.25*gun.ScopeSwayScale`, scaled0.85 crouched or0.7 prone;
- held breath slows the clock toward0.18; it never shrinks amplitude toward the
  center or gives the player a free re-aim;
- source `SteadyAccuracy` has no assignment in this branch and remains0. No
  additional skill bonus is inferred from its unused field.

The oscillator enters shared player yaw/pitch as the difference from its last
applied value. Camera and firing direction therefore use the same aim, and
releasing ADS unwinds only sway while preserving mouse aim/recoil. The viewmodel
input spring now receives actual sensitivity-scaled mouse deltas, matching
`AddLookDelta`, rather than differences between replicated angles. Scope drift
and recoil cannot feed themselves back into arms inertia and shift the optic
inside the frame. Source scoped movement tilt retains its executed oddity:
forward/wiggle multiplier goes to0, but the strafe term omits that multiplier;
bob remains at the original blended ADS amount.

The six available vital bars use `HUD.AddVital` layout and original icons/colors
in `ui/vitals.rs`:24px icons,36px row spacing,12px bars, left24px, width23%, bottom24px.
Health uses the source unified player's100 maximum. Food/water/stamina use their
actual fractions, the infection row shows `1−infection`, and oxygen is always
present below infection in white. Original current panel height reserves seven
rows; the unimplemented temperature row remains empty, preserving the current
source location of the other six. Vitals render above inventory menus and
ignore mouse picking. Source `VitalTweenRate=6/s` and `VitalSnapEps=.002`
seed the first observation immediately and ease later replicated steps. Menu
columns intersecting the actual bars use `HUD.ContentBottom` with its12px gap;
other columns keep their full height. No invented comfortable-temperature reading
is shown.

## Verification

Three actual-host regressions cover the ADS/magnification gate, real replicated
oxygen cost, floor/held-control lockout/rearm and session reset. The full managed
suite passes94 tests. Four native regressions cover phase/amplitude/stance/gun
scaling, source movement-tilt behavior and actual input-system delta application,
mouse-only inertia and unwinding. Four native HUD tests build the real UI nodes
from a replica sample, check fill widths/icon separation/menu visibility, verify
all six original PNGs and cover infection/oxygen color semantics, frame-independent easing and actual
menu-node positioning above the vitals.

Opened `bevy-avatar-inventory.png` and `bevy-avatar-authoritative-death.png`
from the real PEI character journey: all six bars/icons are visible, oxygen sits
below infection, and the overlapping menus stop at y432,12px above the y444
vitals rectangle at720p. The death image shows the easing HP fill above its empty
trough. Scope rendering is part of the combined native weapon checkpoint. A still
image alone cannot establish slower sway; deterministic motion and actual-host
oxygen tests provide the quantitative evidence. The
original `UG_STEADY=1` diagnostic is supported and remains gated on magnifying ADS.

## Remaining HUD work

Body temperature, thermometer readout, status icons, the original hotbar and full
ammo/firemode typography are separate source presentation work. The existing
native weapon/interaction/notice labels continue alongside the original vital
bars. No authority or wire schema is extended for this presentation.
