# CORRECTION to 8ef4b55b -- the stutter measurements were of the MAIN MENU

`--peidrive --headless` never leaves the menu on the Windows build box. The run prints
`[menu] diorama: placed 114` / `[menu] placed Hero`, then floods
`ERROR: Not supported by this display server` from KeybindMenu._Ready -> Bind.Label ->
DisplayServer.KeyboardGetKeycodeFromPhysical, and never builds a world. No `[terrain]`
line and no `[collbudget]` line is ever printed.

## What this invalidates

- **"the GC is innocent on the load path" is UNSUPPORTED.** 9 ms of pause across 1400
  frames was measured on MENU frames. It says nothing about the game.
- **"headless does not create the prop collision" is WRONG.** The world never loaded,
  so nothing can be concluded about what it would have created.
  ColliderBudgetTests.cs says as much in its own note: reachability "is checked from a
  real boot's `[collbudget] N collision shapes in M cells` line, not from here" -- and
  there was no real boot.

## What still stands

- GcWatch itself, including the blame-weighting fix (a pause must explain >=30% of the
  frame before the GC is blamed). That correction was real and is independent of where
  the frames came from.
- ColliderBudget.Rebalance being unbounded per call. That is read off the source, not
  measured.

## The tell, for next time

The word "PEI" appeared in my description of the run and `[menu]` appeared in its
output, in the same session, and I did not reconcile them. An instrument must name what
it measured; I named it and never checked the name was true.

## How to actually measure it

Not on the Windows box with --headless. The project's workflow is the Linux host with
xvfb + lavapipe (never --headless -- see tools/shot.py, which says so in its header) and
the retail maps at ~/unturned/Maps. Failing that, master runs `UG_GCWATCH=1` on the real
client and pastes the lines; the hitch line names both suspects together.
