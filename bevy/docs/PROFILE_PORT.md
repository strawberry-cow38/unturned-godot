# Local profile and Appearance

The native Appearance menu uses the original 32 `faces/face_*.png` textures,
eight columns, 64-pixel buttons, six-pixel spacing/insets, fixed source skin
color, and selected/unselected brightness. All faces are available, as in the
Godot port. Choosing one updates the local preference. Live character rigs and
remote nameplates continue to consume accepted profile replicas.

`LocalProfileSettings.cs` ports `game/PlayerProfile.cs` without a renderer:

- `UG_USERNAME` supplies the default name; `--name` and the existing multiplayer
  name field override it. The original `ProfileRules.SanitizeName` runs before
  connection and again on the server.
- `UG_PROFILE_PNG` supplies the optional 128x128 PNG. File length is checked
  before reading, using the original 64 KB limit and PNG validator. Missing,
  unreadable, malformed or oversized files leave the profile without a picture
  and display a notice; they do not prevent play.
- `UG_FACE` has the original integer/clamp precedence. Otherwise `profile.cfg`
  stores `[character] face`; a fresh character chooses and persists a free face
  from 0 through 9. Selecting a face preserves unrelated config sections.
- Native `profile.cfg` lives beside `Settings::path()`; `UG_BEVY_PROFILE` overrides
  that path. If absent, the existing Godot `user://profile.cfg` is imported once
  from the platform's Godot application-data directory. The Godot file is never
  modified by Bevy. A process environment override is read once, matching source
  caching; a menu choice changes the current process and saved preference.

The bridge's pre-session `localProfile` and `setLocalFace` operations own only
local preferences. Native session initialization explicitly opts into these
settings with `useLocalProfile:true`. The integrated client and real UDP client
then send the existing `SetProfile` command once connected, including bounded
avatar bytes and face. Rejoining republishes the current preference. Sessions
that do not opt in retain the existing harness default, face 0 and no picture.
No wire field or profile authority rule changes in this adapter.

Eight managed regressions cover config import/preservation, environment clamps,
fresh free-face persistence, oversized PNG fallback, integrated/default session
separation and actual UDP join/rejoin. The UDP test checks both server and client
replicas, separately delivered picture bytes, and that selecting a local face
does not mutate the server before the command is processed. Native regressions
build all 32 real UI buttons, exercise the actual activation observer and persisted
choice, preserve an existing player snapshot, and reject an inactive menu button.
The private profile candidate passed all 49 managed tests and all 121 JSONL
protocol tests, including the latest remote equipment adapter. Its preceding
48-test candidate passed the combined native 383-test suite. The menu-camera regression
also runs the real camera system through Appearance, browser/direct/host and
Settings screens, proving these pregame panels retain the menu view.

An actual xvfb/lavapipe session loaded a real PNG and saved face 1, selected face
30 with the mouse, then entered PEI. `bevy-profile-face30-selected.png` records
the original heart-eye texture and selected brightness; the accepted JSON
profile contains face 30, `Profile Survivor`, and the uploaded image hash.
`bevy-profile-face30-body-front.png` shows the matching face on the actual
third-person body, using held freelook. These images were opened and independently
reviewed. That journey also caught and fixed a camera jump from the town menu to
an uninitialized player when opening Appearance. The fresh
`bevy-profile-appearance-fixed-menu.png` capture verifies the corrected town
background and saved face 30 surviving a full process restart. Both native
sessions ended through the actual Quit control with exit code 0; neither log
contains a renderer or asset-load error.

The source main-menu hero/diorama is not recreated by this slice. Launcher
profile editing/authentication, hair/beard controls (also absent from the current
Godot Appearance panel), and broader original menu layout remain separate work.
