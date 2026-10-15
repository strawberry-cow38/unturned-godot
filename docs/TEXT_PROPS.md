# Modelled sign text → prop

Requested by Strawberry, 2026-10-15. The letter set was reviewed against the actual vanilla `Hardware_0` lettering before this tool was implemented.

## Use

1. In **Level**, click **Text mesh…** in the Objects browser, or press **Ctrl+Shift+H** with world keyboard focus.
2. Enter text. The live front/angle preview uses the real mesh generator, not a different UI font.
3. Set cap height, width, extrusion depth, letter gap, word spacing, line height, line alignment, vertical origin and colour. Label/store/large presets change height and depth.
4. Click **Place new…**, then click a wall or other collidable surface. Surface alignment, outward offset and rotation around the surface normal are adjustable. Escape cancels placement; a second Escape closes the draft.
5. After placement, normal **Q/W/R** move/rotate/scale, selection, **Ctrl+C/V**, Delete and transform tools apply. Select a text prop and reopen **Text mesh…** to change it; **Apply changes** commits the draft. **New text** starts a fresh draft; **Place new…** can also place a copy of an existing style.
6. Save normally. Reopened props retain source text and settings, not only their final triangles.

**Ctrl+Z** undoes map actions. **Ctrl+Y** or **Ctrl+Shift+Z** redoes redo-capable actions. Text placement, editing, deletion, duplication and transforms participate in the editor's one chronological history. Legacy actions without redo callbacks remain undo-only and form a redo boundary; this is not a claim that every old editor tool gained redo. Inside the text field, native text-edit undo is separate from committed map actions.

Draft changes affect the preview only. Closing/cancelling or entering Playtest discards the uncommitted draft, leaving the committed prop untouched. Active path tools are suspended while the panel owns input, without destroying their unfinished paths. The glyph preview stops rendering when hidden.

## Style and limits

- 94 printable ASCII meshes plus space: A–Z, a–z, digits and punctuation. Unsupported characters visibly become `?`, with a count in the panel. Tabs display as four spaces.
- Maximum 512 source characters and 16 explicitly entered lines. The panel warns when content is truncated. Soft visual wrapping in the text-entry field does not add line breaks to the prop.
- Cap height 0.02–20 m; width 10–500%; depth 0.001–3 m. Letter gap is extra advance in cap-height units; word spacing multiplies the font's normal space; line height is baseline separation in cap-height units.
- Left/centre/right is applied per line. Baseline/top/middle/bottom controls the vertical local origin. Width scaling affects glyphs and their built-in advance; extra letter gap stays in cap-height units.
- Flat-shaded, un-bevelled, closed geometry. One combined mesh/material per prop, not a node per letter. Colour is opaque; shadows and exact solid-letter collision are optional. Decorative text has no gameplay collision by default. Editor picking uses a separate bounds box so thin letters remain selectable.
- The tool creates lettering, not sign boards, business logos or automatic shop signage. The backing boards in the review scene are ordinary demo geometry.

## Persistence / integration

`content/objects/editor_<map>_textprops.json`, version 1, stores stable IDs, source style and the full 12-float Godot transform (including rotation/scale). Writes use a temporary file followed by replacement. An unreadable/unknown-version/invalid sidecar is not silently overwritten.

Editor loading is through `EditorObjects.LoadCustomPlaceables`; `_Ready` installs the UI once for either editor entry point. The normal world builder loads the same authored data for playable/client/aerial worlds and optional solid collision for dedicated worlds. Editor mode skips that runtime loader to avoid duplicates. Custom-map gameplay uses the existing editor/playtest route and keeps the committed world nodes in place.

No global GUID registration or source-building mesh edits are needed. No automatic text placement in generated buildings was added.

## Font provenance

`content/text/vanilla_sign_font.json` contains the approved mesh geometry and advances only; review/reference meshes are excluded. The vanilla `Hardware_0` front contours match Blender's builtin Bfont at curve resolution 2. Measured normalized per-glyph outline differences were about 1–3×10^-6, with whole-word differences below 1.4×10^-5 after normalization (source float precision). Default cap height is 1; source relative depth is 0.2 / 1.023002. Closed rear caps were added.

Authoring coordinates: +X text advance, Z up, front −Y, back mounting plane Y=0. Runtime conversion is `(x,z,-y)`, front +Z. Runtime depth is normalized independently so thickness can change without changing the contours.

## Visual review workbench

Open `res://tools/TextPropsPreview.tscn` for the small in-engine workbench. It uses the production EditorObjects/browser/text tool and creates four example text props. With `UG_TEXT_REVIEW_DIR` set to an output folder it captures the panel, placed examples and the same examples after save/reload, then demonstrates edit→undo→redo→undo and exits. Without the variable it stays interactive.

The review run used a game build and actual rendered editor session, not a test suite. It is not an exhaustive gameplay/network regression pass. Review-map sidecars and screenshots are not shipped as production map edits.