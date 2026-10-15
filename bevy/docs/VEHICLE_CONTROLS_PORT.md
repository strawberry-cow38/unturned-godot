# Vehicle controls and mechanics panel

`ui/vehicle_controls` consumes the accepted vehicle snapshot and sends the
`vehicleLighting` and protocol69 `vehicleMaintenance` actions. It does not change
light flags, battery or damage masks locally.

The original `PlayerController`, `LightbarRadial` and `AmmoPie` own the interaction
rules: an emergency-vehicle driver taps Ctrl for less than220ms to toggle the
siren, or holds it for220ms to open the four-sector selector. Releasing Ctrl or
clicking the left button confirms; the hub, right button and Escape cancel. The
off sector is selectable only if the accepted lightbar was on when the menu
opened. Losing the accepted driver's seat, dying, disconnecting or changing
session closes the selector without sending a switch. Bus-door and tank-hatch
Ctrl uses retain their earlier priority.

The native ring preserves source directions, colors,120ms opening,22/s hover
easing, disabled-sector behavior, labels and icon. It scales the entire source
ring and hit region together to fit small viewports. The source frosted
HUD-inclusive backdrop uses the shared modal compositor; the radial supplies
its original opening fade through `Backdrop::ammo_pie`. The final full-game frost and typography capture is
`bevy-lightbar-play-wail-frost.png/json` (frame510), opened and independently reviewed.

The hood opens the source `MechanicsPanel` structure through ordinary interaction
focus. Its segment uses the original2.6m reach and stops at Rapier world/vehicle
geometry. `vehicle::access` first selects the owning vehicle through the original
0.16m endpoint sphere or disabled-box visual-footprint fallback, then resolves
all access zones together. Intersected zones rank by center distance from the
eye; otherwise the nearest box within1.5m of the clipped endpoint wins, retaining
source-order ties. Queries use player membership so a convex chassis stripped
of player/look contact cannot hide its visible mesh. Current source `LookTrace`
explicitly uses the active camera in both first and third person; earlier
shoulder comments describe superseded behavior. Trunk focus now requests the
accepted storage dashboard (see `VEHICLE_STORAGE_PORT.md`); bifold movement remains
a separate interaction. Neither is converted to hood or driver access. The panel displays accepted live vehicle figures and
builds lamp buttons once per open, keeping the same entities across damage and
repair snapshots. Intact lamps disable their buttons. Fix sends the exact
canonical lamp slot to the authority and waits for the resulting snapshot.

Original section/row spacing, font sizes, margins and dimming are retained. A
scrolling maximum-height panel keeps the real rows reachable at smaller window
sizes; source `MechanicsPanel` could extend beyond those windows. F (or its
rebound interaction key) and Escape close the panel and consume that edge.

The panel now includes original GLAZING, LAMPS and TIRES sections in that order.
All three use accepted canonical presence/broken masks. Sparse wheel slots remain
sparse: slot3 stays slot3 and does not manufacture absent slots0 or2. Glass display
names come from the managed source extraction. `fix` repairs the selected pane or
lamp; `replace` requests the original free indexed tire repair. Buttons persist
across accepted damage/repair updates, disable intact parts and never modify the
local snapshot or consume an inventory spare tire. The summary uses actual
accepted fuel and hull values with protocol69's source-fleet encoding budgets.

Driver horn input now sends the maintenance intent. Only the accepted
`vehicleHorn` fact creates the original horn clip, pitch1 and +4dB spatial audio,
using the same path as the alarm horn. The old native counter, cooldown, optimistic
audio and duplicate hearing publication are removed; managed source authority
owns cooldown, battery/alarm/driver gates and the one32m hearing alert. Native
engine hearing retains its original0.4s cadence and forward-speed range.

Validation against immutable
`.local/vehicle-lights-v68-final/UnturnedBevy.Bridge.dll` passes all nine focused
tests: Ctrl timing/cancellation, disabled hover animation, accepted ownership,
persistent lamp widgets, rebound close, coalesced cancellation edges, actual
Rapier wall obstruction and a
real two-process UDP controls/repair journey through the production button observer.
The final69 candidate also passes those nine controls tests. Three additional
access tests exercise original priority/ties, the actual failed police pose with
its shipped OBJ and stripped convex-root negative control, wall refusal, and
claimed-target preservation. The first backdrop integration passed all59 UI tests
before the final source typography refinement.

The full PEI application journey entered the actual police driver's seat with F,
used L and a Ctrl tap (accepted flags14), then held Ctrl to open the original
ring. `bevy-lightbar-play-wail-preblur.png/json` records frame333; the accepted
pattern1 frame554 and off frame686 record flags14 then6 without changing the
chosen pattern. These are the preblur captures, independently opened and checked
against `AmmoPie`. The final frost frame510 confirms blurred HUD/world with a
sharp ring and source typography. The original full-game hood check exposed the
missing shared access fallback/query mask; its corrected full-application check
now passes: `bevy-hood-focus-fixed.png/json` shows the same tilted bonnet,
`bevy-hood-panel-initial` and `bevy-hood-panel-scrolled-initial` show F opening
and wheel scrolling, and `bevy-hood-closed-by-f` records the consumed close edge.
The first panel render exposed wrapped lamp status and bright disabled captions;
source NoWrap/intrinsic row sizing and the actual Godot default disabled caption
alpha0.5 are now guarded by the production panel fixture. Its final Vulkan
render, `bevy-mechanics-panel-final.png`, passed the actual text-layout assertions
and was opened and independently reviewed: each status remains on one line,
working lamps have dimmed repair captions, and all tire rows remain visible. The large driving forearms match the original Godot
`drivetest` reference and are not a menu regression.

The protocol69 native follow-up passes12 focused controls/maintenance tests, one
actual driver-input→real UDP→accepted audio test and the engine-hearing cadence
regression. Two real UDP sessions exercise normal Fire against canonical pane17,
a sparse tire3 contact, production repair observers and unchanged inventory. The
native horn test submits two clicks before an authority tick: zero optimistic
sound, one accepted fact/clip after the original cooldown rejects the duplicate.
Source/code review by interfaces found no blocker. The expanded panel Vulkan fixture also passes: all17 actual source part rows
retain one-line text layout and correct disabled-caption contrast. Both
`bevy-mechanics-maintenance-top.png` and `bevy-mechanics-maintenance-bottom.png`
were opened; they show source GLAZING/LAMPS/TIRES order, exact fix/replace
captions, broken-part status and scroll access to the last wheel and close hint.
The earlier seven-lamp capture above remains its own verified checkpoint.
