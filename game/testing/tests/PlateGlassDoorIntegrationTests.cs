using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    public class PlateGlassDoorSwingAndSafety : GameTest
    {
        public override string Name => "plate_glass.four_hangings_existing_motion_and_safe_collision";
        static ObjectDoor Find(Node n) { foreach (var c in n.GetChildren()) { if (c is ObjectDoor d) return d; var f = Find(c); if (f != null) return f; } return null; }
        public override IEnumerable<Step> Run()
        {
            foreach (var action in new[] { DoorAction.Swing, DoorAction.DoubleSwing, DoorAction.Pocket })
            {
                var w = new WallSurface { Length = 28f }; World.AddChild(w);
                for (int i = 0; i < 4; i++) w.Openings.Add(new WallOpening(1.5f + i * 6f,0f,2.5f,2.85f) {
                    DoorProp = DoorDeploy.PlateGlass, DoorHingeAtStart = (i & 1) != 0, DoorOpensFront = (i & 2) != 0, DoorAction = action });
                w.Rebuild(); yield return Ticks(3);
                foreach (var c in w.GetChildren())
                {
                    if (c is not Node3D host) continue;
                    int k = w.OpeningIndexForDoorHost(host); if (k < 0) continue;
                    var o = w.Openings[k]; var d = Find(host); var pane = d?.PlatePane;
                    T.Check(action + " creates a pane in hanging " + k, pane != null);
                    if (pane == null) continue;
                    var shut = pane.GlobalPosition;
                    d.SetInitialState(true); yield return Ticks(2);
                    var local = w.ToLocal(pane.GlobalPosition);
                    T.Check(action + " moves the actual pane with hanging " + k, shut.DistanceTo(pane.GlobalPosition) > .8f);
                    if (action == DoorAction.Pocket)
                        T.Check("pocket glass slides along its wall", Mathf.Abs(local.Z) < .05f && Mathf.Abs(local.X - (o.U+o.Width*.5f)) > 1.8f);
                    else
                        T.Check("glass swings out of the requested face", o.DoorOpensFront ? local.Z > .7f : local.Z < -.7f);
                    d.SetInitialState(false);
                }
                w.QueueFree(); yield return Ticks(3);
            }
            var h = DoorDeploy.SpawnProp(DoorDeploy.PlateGlass, World, Vector3.Zero, 0f);
            var door = Find(h); yield return Ticks(2);
            door.SetOpen(true); door._PhysicsProcess(.2);
            bool disabled = true;
            foreach (var c in door.GetChildren()) if (c is CollisionShape3D shape) disabled &= shape.Disabled;
            foreach (var c in door.PlatePane.GetChildren()) if (c is CollisionShape3D shape) disabled &= shape.Disabled;
            T.Check("both pane and frame are non-solid mid-swing", disabled);
            door.SetInitialState(true); yield return Ticks(2);
            var person = new CharacterBody3D { CollisionLayer = 1u << 3, CollisionMask = 0u, Position = new Vector3(0f,1.8f,0f) };
            person.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(.5f,1f,.5f) } });
            World.AddChild(person); yield return Ticks(2);
            door.SetInitialState(false); yield return Ticks(2);
            T.Check("closing does not solidify glass on top of a person", door.DebugLeafCollider.Disabled);
            person.QueueFree(); yield return Ticks(4);
            T.Check("the frame solidifies once the person clears", !door.DebugLeafCollider.Disabled);
        }
    }

    public class PlateGlassDoorEditor : GameTest
    {
        public override string Name => "plate_glass.editor_choice_repair_remove_and_undo";
        public override IEnumerable<Step> Run()
        {
            var b = Glass.Rig(this, out var editor); yield return Ticks(1);
            var wall = b.AddWall(new Vector3(0f,b.ActiveFloorY,0f),0f,12f);
            int i = b.AddOpening(wall,3.5f,0f,0);
            b.SetOpeningDoor(wall,i,DoorDeploy.PlateGlass); yield return Ticks(2);
            T.Check("editor door choice supplies a pane", wall.Openings[i].Glazed && wall.Openings[i].HasGlass);
            T.Check("same existing smashed-glass control breaks the door pane", b.BreakGlass(wall,i));
            yield return Ticks(2); T.Check("editor records the smashed door", wall.Openings[i].GlassBroken);
            T.Check("same repair control restores it", b.BreakGlass(wall,i,false));
            b.SetOpeningDoor(wall,i,null); yield return Ticks(2);
            T.Check("removing a plate door leaves an open hole, not stationary door-shaped glass", !wall.Openings[i].HasDoor && !wall.Openings[i].Glazed);
            b.ActiveDoorProp = DoorDeploy.PlateGlassBronze;
            T.Check("new openings can select the bronze glass door", b.PlannedOpening(wall,8f,0f,0).DoorProp == DoorDeploy.PlateGlassBronze && b.PlannedOpening(wall,8f,0f,0).Glazed);
            editor.Undo(); yield return Ticks(2);
            T.Check("undo restores the plate door and its glazing", wall.Openings[i].DoorProp == DoorDeploy.PlateGlass && wall.Openings[i].HasGlass);
        }
    }
}