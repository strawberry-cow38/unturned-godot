using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The playtest button drops you on the ground UNDER the fly camera (master: "spawns u to walk
    /// around somewhere near the editor camera").
    ///
    /// Two assertions, and each catches what the other cannot:
    ///   - X/Z must track the CAMERA. The old behaviour spawned at the building stage regardless of where you
    ///     were looking, and a height-only check passes that happily.
    ///   - Y must be the GROUND, not the camera. The editor cam sits ~130 m up; "spawn at the camera" satisfies
    ///     an X/Z check perfectly and then drops the player off a cliff they never saw.
    /// Assert one and the other failure walks straight through.</summary>
    public class PlaytestSpawnsUnderTheCamera : GameTest
    {
        public override string Name => "editor.playtest_spawns_under_the_camera";

        public override IEnumerable<Step> Run()
        {
            // A floor to land on, deliberately NOT at y=0 so "ground" can't be confused with the origin.
            const float FloorY = 37.5f;
            var floor = new StaticBody3D();
            var shape = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(400f, 1f, 400f) } };
            floor.AddChild(shape);
            World.AddChild(floor);
            floor.GlobalPosition = new Vector3(0f, FloorY - 0.5f, 0f);   // top face at FloorY

            var editor = new Editor();
            World.AddChild(editor);
            var cam = new EditorCamera();
            editor.AddChild(cam);
            editor.Setup("PlaytestSpawnTest", null, cam);

            var play = new EditorPlayMode();
            editor.AddChild(play);
            play.Setup(editor, null, cam);

            // Somewhere specific and far from both the origin and the building stage, so a spawn that ignores
            // the camera cannot coincidentally look correct.
            var camPos = new Vector3(123.5f, FloorY + 130f, -87.25f);
            cam.GlobalPosition = camPos;
            yield return Step.Ticks(2);   // let the physics space see the floor body

            var spawn = play.ComputeSpawnForTest();

            float dxz = new Vector2(spawn.X - camPos.X, spawn.Z - camPos.Z).Length();
            T.Check($"spawns under the camera in X/Z ({dxz:0.00} m away, cam {camPos.X:0.0}/{camPos.Z:0.0}, spawn {spawn.X:0.0}/{spawn.Z:0.0})",
                    dxz < 0.5f);

            T.Check($"stands on the ground, not at camera height (y {spawn.Y:0.00}, floor {FloorY:0.00}, cam {camPos.Y:0.00})",
                    Mathf.Abs(spawn.Y - (FloorY + 1.2f)) < 0.35f);

            T.Check($"and is NOT the building stage ({EditorBuildings.StageOrigin.X:0.0}/{EditorBuildings.StageOrigin.Z:0.0})",
                    new Vector2(spawn.X - EditorBuildings.StageOrigin.X, spawn.Z - EditorBuildings.StageOrigin.Z).Length() > 1f);

            // Move the camera: the spawn must follow it. A hardcoded point passes every check above exactly once.
            var camPos2 = new Vector3(-64.0f, FloorY + 60f, 150.75f);
            cam.GlobalPosition = camPos2;
            yield return Step.Ticks(1);
            var spawn2 = play.ComputeSpawnForTest();
            float dxz2 = new Vector2(spawn2.X - camPos2.X, spawn2.Z - camPos2.Z).Length();
            T.Check($"follows the camera when it moves ({dxz2:0.00} m from the new position)", dxz2 < 0.5f);
            T.Check($"the two spawns differ ({(spawn2 - spawn).Length():0.0} m apart)", (spawn2 - spawn).Length() > 1f);

            editor.QueueFree();
            floor.QueueFree();
        }
    }
}
