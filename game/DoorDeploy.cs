using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Placeable DOOR spawn seam, mirroring FridgeDeploy/FluidDeploy: a DeployableDef carrying a DoorProp name
    // places a standalone swinging door instead of a plain Deployable body.
    //
    // This deliberately reuses ObjectDoor -- the CONTAINER door -- rather than the old building Door.cs.
    // strawberry_cow, 2026-08-09: "screw the door mechanics. they were done unattended and never actually
    // properly tested. for all im concerned, that door doesnt exist. the container doors ARE tested and
    // proven, however." That is the better evidence: Door.cs had 11 green headless tests, one hardcoded demo
    // instance in WorldBuilder, no way for a player to make one, and a BoxMesh for a leaf. ObjectDoor gets
    // opened by hand in game every session.
    //
    // What comes free by reusing it, none of which is re-implemented here: the catalog-driven hinge
    // (pivot/axis/signed angle/duration), the retail easing curves sampled off the real animation clips, the
    // per-toggle sound, the group sync that swings a double door's two leaves together, and a leaf collider
    // that TRACKS the swing -- solid at the endpoints, never solid mid-swing so it cannot trap you, and held
    // open when a body is standing in its volume.
    //
    // A standalone door needs no extra collision work: ObjectDoor's leaf collider already blocks movement
    // (StoreShelf: "the player can't walk through the doorway anymore"), which is the one thing a container
    // door was expected to borrow from its parent prop and turns out to own itself.
    public static class DoorDeploy
    {
        static Dictionary<string, List<WorldBuilder.DoorCatalogEntry>> _cat;
        static readonly Dictionary<string, Material> _mats = new();

        /// <summary>Place a door prop at <paramref name="pos"/> facing <paramref name="yawDeg"/>. Returns the
        /// host node, or null if the prop has no catalog entry / no loadable leaf mesh -- callers treat null
        /// as "not a door" and fall through, same as FridgeDeploy.</summary>
        public static Node3D SpawnFor(DeployableDef def, Node parent, Vector3 pos, float yawDeg)
        {
            if (string.IsNullOrEmpty(def?.DoorProp) || parent == null) return null;

            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            _cat ??= WorldBuilder.LoadDoorCatalog(dir);
            if (_cat == null || !_cat.TryGetValue(def.DoorProp, out var leaves) || leaves.Count == 0) return null;

            var host = new Node3D { Name = def.DoorProp };
            parent.AddChild(host);
            host.GlobalPosition = pos;
            host.RotationDegrees = new Vector3(0f, yawDeg, 0f);

            // The leaf .obj is authored LYING FLAT and DeployableDef.StandRotX (90) stands it up. Taken from
            // the shared constant, not written as a literal: it is env-tunable (UG_DEPLOYROT) and a door that
            // hard-coded the number would silently stop matching every other deployable the moment someone
            // tuned it. I first copied StoreShelf's 270 here, which is the SHELF family's convention -- that
            // is the opposite rotation, and it would have stood the door up mirrored, hinging on the wrong
            // edge while looking perfectly plausible (cow tools caught it: "barricades authored lying flat ->
            // +90 X stands them up").
            //
            // Yaw stays on the HOST rather than being folded in here, so the catalog's pivot and axis are
            // consumed in the flat authored frame the extractor wrote them in -- "hinge params are
            // pre-stand-up, compose accordingly" -- and nothing has to re-derive them. Composed, host-yaw x
            // this = DeployableDef.StandBasis(yaw), the same basis a normal deployable gets.
            var xform = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(DeployableDef.StandRotX)), Vector3.Zero);
            var mat = MatFor(def.DoorProp, dir);

            var made = new List<ObjectDoor>();
            foreach (var e in leaves)
            {
                var leaf = ObjMesh.Load(dir + e.MeshFile);
                if (leaf == null) continue;
                string curveBase = e.MeshFile.EndsWith("_door.obj")
                    ? e.MeshFile.Substring(0, e.MeshFile.Length - "_door.obj".Length)
                    : def.DoorProp;
                made.Add(ObjectDoor.Spawn(host, xform, e.Pivot, e.Axis, e.AngleDeg, e.DurationSec, leaf, mat,
                    startOpen: false,
                    openCurve: WorldBuilder.LoadDoorCurve(dir, curveBase, "open"),
                    closeCurve: WorldBuilder.LoadDoorCurve(dir, curveBase, "close"),
                    soundName: e.Sound));
            }
            if (made.Count == 0) { host.QueueFree(); return null; }

            // A double door is two catalog lines under one prop name, exactly like Wardrobe_0's Left/Right --
            // grouping makes both leaves answer one interaction, and cow tools split the wooden Doubledoor's
            // two hinges out for this reason.
            if (made.Count > 1) foreach (var d in made) d.SetGroup(made);
            return host;
        }

        static Material MatFor(string prop, string dir)
        {
            if (_mats.TryGetValue(prop, out var cached)) return cached;
            var mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            string tp = dir + prop + "_tex.png";
            if (System.IO.File.Exists(tp) && img.Load(tp) == Error.Ok)
            {
                mm.AlbedoTexture = ImageTexture.CreateFromImage(img);
                mm.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // tiny palette texel
            }
            else mm.AlbedoColor = new Color(0.62f, 0.62f, 0.64f);
            _mats[prop] = mm;
            return mm;
        }

        /// <summary>Test seam: forget the cached catalog so a test can write doors.txt and have it re-read.</summary>
        public static void ForgetCatalog() { _cat = null; _mats.Clear(); }
    }
}
