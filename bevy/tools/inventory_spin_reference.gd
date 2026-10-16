# Original Godot 4.6 SpinBox oracle. Run with --headless --script this file;
# UG_SPIN_CASES and UG_SPIN_OUTPUT are absolute JSON paths. No rendered UI needed.
extends SceneTree
func _initialize():
    _run.call_deferred()
func _run():
    var box = SpinBox.new()
    box.min_value = 1
    box.max_value = 499
    box.step = 1
    root.add_child(box)
    await process_frame
    var cases = JSON.parse_string(FileAccess.get_file_as_string(OS.get_environment("UG_SPIN_CASES")))
    var rows = []
    for text in cases:
        box.value = 17
        box.get_line_edit().text = text
        box.apply()
        rows.append({"text":text, "value":int(box.value), "shown":box.get_line_edit().text})
    var output = FileAccess.open(OS.get_environment("UG_SPIN_OUTPUT"), FileAccess.WRITE)
    output.store_string(JSON.stringify(rows, "  ")+"\n")
    quit()
