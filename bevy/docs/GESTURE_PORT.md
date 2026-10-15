# Accepted player gestures

The native body consumes the original `GestureRules` table from `catalog.gestures`.
The table owns clip names, loop/stop relationships, requestability, broadcast and
required stance. Optional null strings may be omitted by the bridge. No native
copy of the admission rules or gesture wire enum is introduced.

The existing B surrender binding toggles its original start/stop. Rebindable wave,
salute, point, facepalm and rest actions use the same route; rest toggles only its
matching stop. The original `gesture` console selector lists/currently reports the
accepted state and supports wave, salute, point, facepalm, surrender, rest, tpose,
inventory and stop. There is no new gesture wheel: none exists in the selected
source. Console stop asks for the current definition's stop, so it cannot locally
clear cuffs. The managed advisory check supplies original refusal text, and the
server remains responsible for accepting the unchanged command.

Requests do not pose the character. An accepted looping replica change starts its
real rig clip once; accepted one-shot events start/restart only the named owner's
upper-body animation. Retained sequence IDs are deduplicated. Sampling uses the
same upper-body adapter as the held weapon, preserves locomotion legs and applies
the source crouch/prone shoulder counter-rotation. The existing look/lean modifier
runs afterward. Pause freezes the presentation; death and a new session clear
local playback. Equipping an item restores the held overlay instead of allowing
a stale gesture to overwrite it. First-person gesture arms are not invented: the
source only applies these clips to the full body.

Two actual source limitations are retained and explicit. T-pose metadata has no
clip and the source does not apply a bind pose, despite its comment; this adapter
likewise does not synthesize one. A one-shot requested over an existing looping
state leaves that state intact, and the source timer retains the one-shot's final
upper-body sample instead of restarting the loop. Inventory start/stop are not
broadcast by the original authority publisher; they are not turned into a new
replicated state here.

Five focused tests pass against the frozen gesture-metadata bridge: real catalog
and console/key selection, modal input and no optimistic state, repeat/finish and
loop-state behavior, actual original rig arm changes with unchanged legs and
retained-event deduplication, and a real B-key request through delayed server
acceptance into the body followed by an authoritative stop. Actual native Vulkan captures were opened and inspected: `bevy-gesture-surrender`
shows accepted state6 and both clothed arms raised; `bevy-gesture-surrender-stopped`
shows state0 and restored idle arms; `bevy-gesture-wave` shows the raised right hand
while persistent state remains0. Their JSON sidecars record the real authority
state. No shader or runtime error occurred, and the session exited through Quit.
Independent review found no upper-body ordering or acceptance blocker. The shared
`Playback` adapter is available for the separate remote-player renderer; that
wiring is not claimed by this local-body checkpoint.

The visual control harness exposed an input issue outside gesture admission:
long synthetic X11 toggle presses can repeat after the initial edge. Captures
used short presses. Input-repeat suppression needs a dedicated regression in the
next UI lifecycle slice rather than being treated as fixed by these pose tests.
