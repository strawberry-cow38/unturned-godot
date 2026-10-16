extends SceneTree
func _initialize():
    run.call_deferred()
func run():
    var box=SpinBox.new()
    box.position=Vector2(100,200)
    box.size=Vector2(100,31)
    box.min_value=1
    box.max_value=499
    box.step=1
    box.value=50
    root.add_child(box)
    await process_frame
    await process_frame
    var point=Vector2(192,207)
    var rows=[]
    for i in range(7):
        var event
        if i==0 or i==6:
            event=InputEventMouseButton.new()
            event.button_index=MOUSE_BUTTON_LEFT
            event.pressed=i==0
            event.button_mask=MOUSE_BUTTON_MASK_LEFT if i==0 else 0
        else:
            event=InputEventMouseMotion.new()
            event.button_mask=MOUSE_BUTTON_MASK_LEFT
            var dy=[0,-3,-10,-20,10,30][i]
            point+=Vector2(0,dy)
            event.relative=Vector2(0,dy)
        event.position=point
        event.global_position=point
        root.push_input(event, true)
        await process_frame
        rows.append({"stage":i,"value":box.value,"mode":Input.mouse_mode})
    print("DRAG_ORACLE ",JSON.stringify(rows))
    quit()
