using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // SMART PROPS IN THE MAP EDITOR (strawberry: "make all 'smart' props (tvs, clocks, lights, doors, etc etc)
    // functional in the editor and editor play mode").
    //
    // What needs proving is not "a device got created". That is a count, and a count passes happily while the
    // light sits at the world origin and the prop it belongs to is 400 m away. Four properties carry the feature,
    // and each is asserted as the thing that would actually be REPORTED if it broke:
    //
    //   1. the editor attaches EXACTLY what the shared table promises, for every smart prop in the catalogue --
    //      the anti-drift check, and the reason SmartProps.KindsFor exists at all
    //   2. a device FOLLOWS its prop when the gizmo moves it. This is the trap the whole editor path had to work
    //      around: the device factories hand back TopLevel nodes whose Position is a WORLD position, because in
    //      the world loader nothing ever moves again. Left alone, dragging a lamp in the editor leaves its light
    //      standing where the lamp used to be -- and the prop still looks fine, so it reads as a lighting bug
    //   3. the placed prop is SOLID, on the same layers the world loader uses. Before this it was pick-only, so
    //      a playtest walked through every prop you had placed
    //   4. its collider routes the look-ray back to the device, which is what makes F work in playtest
    public sealed class SmartPropEditorTests : GameTest
    {
        public override string Name => "editor.smart_props";

        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");

        static List<string> CatalogNames()
        {
            var names = new List<string>();
            var seen = new HashSet<string>();
            string gm = Dir + "guid_mesh.txt";
            if (!System.IO.File.Exists(gm)) return names;
            foreach (var line in System.IO.File.ReadLines(gm))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2 && seen.Add(p[1])) names.Add(p[1]);
            }
            return names;
        }

        public override IEnumerable<Step> Run()
        {
            var doors = WorldBuilder.LoadDoorCatalog(Dir);
            System.Func<string, bool> hasDoors = doors.ContainsKey;

            // ---- 1. THE TABLE AND THE EDITOR AGREE, prop by prop ----------------------------------------
            var names = CatalogNames();
            T.Check($"the props catalogue loaded ({names.Count} names)", names.Count > 0);

            int smart = 0, checkedProps = 0, skippedNoMesh = 0, mismatched = 0;
            string firstBad = null, skippedNames = "";
            foreach (var n in names)
            {
                var want = SmartProps.KindsFor(n, hasDoors);
                if (want == SmartKind.None) continue;
                smart++;
                var mesh = ObjMesh.Load(Dir + n + ".obj");
                if (mesh == null)
                {
                    // Not extracted on this box. Named rather than swallowed: a silent skip turns "every smart
                    // prop agrees" into "the ones I happened to have agree", which reads identically in a pass.
                    skippedNoMesh++;
                    if (skippedNames.Length < 120) skippedNames += (skippedNames.Length > 0 ? " " : "") + n;
                    continue;
                }
                var root = new Node3D { Transform = new Transform3D(EditorObjects.Upright(0f), new Vector3(checkedProps * 40f, 0f, 0f)) };
                World.AddChild(root);
                var mi = new MeshInstance3D { Mesh = mesh };
                root.AddChild(mi);
                var got = SmartProps.AttachEditor(root, n, mi, null, root.Position, 0f, Dir, doors).Kinds;
                checkedProps++;
                if (got != want) { mismatched++; firstBad ??= $"{n}: table says {want}, editor built {got}"; }
                root.QueueFree();
            }
            yield return Step.Ticks(2);

            T.Check($"the catalogue has smart props to check ({smart} of {names.Count}, {checkedProps} with meshes on this box)",
                    checkedProps > 0);
            T.Check($"every smart prop attaches exactly what the table promises ({checkedProps} checked{(mismatched > 0 ? " -- " + firstBad : "")})",
                    mismatched == 0);
            if (skippedNoMesh > 0)
                GD.Print($"[editor.smart_props] {skippedNoMesh} smart prop(s) not extracted on this box, unchecked: {skippedNames}");

            // ---- 2. A DEVICE FOLLOWS ITS PROP -----------------------------------------------------------
            // Lamp_0 is the sharp case: LampLight.Make returns a TopLevel node holding a world position. If the
            // editor path ever stops clearing TopLevel, the light stays put and the lamp walks away from it.
            var lampMesh = ObjMesh.Load(Dir + "Lamp_0.obj");
            if (lampMesh == null) T.Fail("Lamp_0.obj missing -- cannot test whether a device follows its prop");
            else
            {
                var root = new Node3D { Transform = new Transform3D(EditorObjects.Upright(0f), new Vector3(5f, 0f, 5f)) };
                World.AddChild(root);
                var mi = new MeshInstance3D { Mesh = lampMesh };
                root.AddChild(mi);
                var a = SmartProps.AttachEditor(root, "Lamp_0", mi, null, root.Position, 0f, Dir, doors);
                yield return Step.Ticks(2);

                T.Check("a desk lamp gets a LampLight", a.Lamp != null);
                if (a.Lamp != null)
                {
                    var before = a.Lamp.GlobalPosition;
                    T.Check($"...seated on the lamp, not at the origin (|{before}| vs prop at {root.Position})",
                            before.DistanceTo(root.Position) < 2f);

                    var delta = new Vector3(60f, 3f, -25f);
                    root.Position += delta;                     // what the gizmo does
                    yield return Step.Ticks(2);
                    var after = a.Lamp.GlobalPosition;
                    T.Check($"...and it MOVES with the prop (moved {(after - before)}, expected {delta})",
                            (after - before - delta).Length() < 0.01f);
                }
                root.QueueFree();
            }
            yield return Step.Ticks(2);

            // ---- 3+4. SOLID, AND WIRED TO THE PLAYER'S LOOK-RAY -----------------------------------------
            // Through the REAL editor placement path, because the layer + meta wiring is what playtest depends
            // on and a hand-built root would prove nothing about EditorObjects.Place.
            var ed = new Editor();
            World.AddChild(ed);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            yield return Step.Ticks(2);

            const uint PlayerCollides = (1u << 0) | (1u << 6);   // PlayerController's movement mask

            var lamp = objs.Place("Lamp_0", new Vector3(3f, 0f, 3f), EditorObjects.Upright(0f));
            T.Check("the editor placed a desk lamp", lamp != null);
            if (lamp != null)
            {
                var body = FindBody(lamp);
                T.Check("...with a collider", body != null);
                if (body != null)
                {
                    T.Check($"...the player can actually walk into (layer 0x{body.CollisionLayer:X})",
                            (body.CollisionLayer & PlayerCollides) != 0);
                    T.Check("...and its collider routes a look-ray to the lamp (F toggles it in playtest)",
                            body.HasMeta(LampLight.LookMeta));
                }
            }

            var tv = objs.Place("Television_0", new Vector3(9f, 0f, 3f), EditorObjects.Upright(0f));
            if (tv != null)
            {
                var body = FindBody(tv);
                T.Check("a placed television carries its TVDevice on the collider",
                        body != null && body.HasMeta(TVDevice.HitMeta));
            }

            // A wardrobe is the multi-leaf case: two ObjectDoors that must be GROUPED, or F opens one door of a
            // two-door wardrobe and the other just sits there.
            var wardrobe = objs.Place("Wardrobe_0", new Vector3(15f, 0f, 3f), EditorObjects.Upright(0f));
            if (wardrobe == null) T.Fail("Wardrobe_0 did not place -- cannot check editor doors");
            else
            {
                int leaves = 0;
                foreach (var c in wardrobe.GetChildren()) if (c is ObjectDoor) leaves++;
                T.Check($"a placed wardrobe gets both of its openable leaves ({leaves})", leaves == 2);
                var body = FindBody(wardrobe);
                T.Check("...and the body collider resolves to a door, so F works on the prop itself",
                        body != null && body.HasMeta("objectdoor"));
            }

            // A streetlight is the prop with the most parts: the lens has to come OFF the housing (or the bulb
            // never lights, it just renders grey), the lamp has to sit at the head, and the base has to carry a
            // wire-able tap or the lamp cannot be put on a generator.
            var slMesh = ObjMesh.Load(Dir + "Street_Light_0.obj");
            if (slMesh == null) T.Fail("Street_Light_0.obj missing -- cannot check the streetlight's parts");
            else
            {
                var r = new Node3D { Transform = new Transform3D(EditorObjects.Upright(0f), new Vector3(0f, 0f, 100f)) };
                World.AddChild(r);
                var mi3 = new MeshInstance3D { Mesh = slMesh };
                r.AddChild(mi3);
                int triBefore = slMesh.GetFaces().Length / 3;
                var a2 = SmartProps.AttachEditor(r, "Street_Light_0", mi3, null, r.Position, 0f, Dir, doors);
                yield return Step.Ticks(2);
                T.Check("a placed streetlight gets its light", a2.Street != null);
                T.Check("...its lens split onto its own instance, so the bulb can glow", a2.Lens != null);
                int triAfter = (mi3.Mesh as ArrayMesh)?.GetFaces().Length / 3 ?? triBefore;
                T.Check($"...carved OUT of the housing rather than drawn twice ({triBefore} -> {triAfter} tris)",
                        triAfter < triBefore);
                T.Check("...and a wire-able tap on its base, so a generator can feed it", a2.Tap != null);
                r.QueueFree();
            }
            yield return Step.Ticks(2);

            // The negative, and it is not decoration. Check 1 SKIPS any prop the table calls ordinary, so it
            // cannot see a device wired into AttachEditor that nobody added to the table -- the drift running the
            // other way. That prop would then be batched (the deny-list is derived from the table), and a batched
            // device fails silently. So: sample ordinary props across the catalogue and require they stay inert.
            int spurious = 0; string firstSpurious = null, sampled = "";
            for (int i = 0; i < names.Count && sampled.Length < 400; i += 17)
            {
                var n = names[i];
                if (SmartProps.KindsFor(n, hasDoors) != SmartKind.None) continue;
                var m = ObjMesh.Load(Dir + n + ".obj");
                if (m == null) continue;
                var r = new Node3D { Transform = new Transform3D(EditorObjects.Upright(0f), new Vector3(i * 5f, 0f, 200f)) };
                World.AddChild(r);
                var mi2 = new MeshInstance3D { Mesh = m };
                r.AddChild(mi2);
                var got = SmartProps.AttachEditor(r, n, mi2, null, r.Position, 0f, Dir, doors).Kinds;
                if (got != SmartKind.None) { spurious++; firstSpurious ??= $"{n} sprouted {got}"; }
                sampled += (sampled.Length > 0 ? "," : "") + n;
                r.QueueFree();
            }
            T.Check($"ordinary props stay inert{(spurious > 0 ? " -- " + firstSpurious : $" ({sampled.Split(',').Length} sampled)")}",
                    spurious == 0);
            T.Check("an ordinary house is not a smart prop", !SmartProps.NeedsOwnNode("House_00", hasDoors));

            // ---- THE BATCHING PIN ------------------------------------------------------------------------
            // WorldBuilder's batch deny-list is now DERIVED from this table (a device carves sub-meshes off its
            // own instance's node, which a shared MultiMesh has nowhere to put). Deriving it removes the
            // hand-maintenance bug it used to carry -- and creates a new one: the list can now shrink silently by
            // an edit to the table. These are the names that list held, pinned so that cannot happen quietly.
            int unpinned = 0; string firstUnpinned = null;
            foreach (var n in new[]
            {
                "Street_Light_0", "Traffic_Light_0", "Lighthouse_0", "Toaster_0", "Clock_0",
                "Light_0", "Light_1", "Lamp_0", "Lamp_1",
                "Television_0", "Television_1", "Computer_0", "Computer_2", "Computer_3",
                "Science_3", "Tower_Water_0", "Fire_Hydrant_0", "Well_0", "Counter_1", "Counter_3",
                "Fridge_0", "Wardrobe_0", "Container_0", "Oven_0", "Washer_0",
            })
                if (!SmartProps.NeedsOwnNode(n, hasDoors)) { unpinned++; firstUnpinned ??= n; }
            T.Check($"every prop that needs its own node still says so{(unpinned > 0 ? $" -- {firstUnpinned} does not" : "")}",
                    unpinned == 0);

            objs.QueueFree(); ed.QueueFree();
        }

        // The PROP's own collider, which is a plain StaticBody3D. Type-exact on purpose: ObjectDoor and
        // FluidContainer both derive from StaticBody3D and are added to the same root first, so "the first
        // StaticBody3D child" hands back a door leaf on every prop that has one.
        static StaticBody3D FindBody(Node3D root)
        {
            foreach (var c in root.GetChildren())
                if (c is StaticBody3D b && b.GetType() == typeof(StaticBody3D)) return b;
            return null;
        }
    }
}
