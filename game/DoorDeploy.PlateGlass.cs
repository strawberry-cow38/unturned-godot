using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace UnturnedGodot
{
    public static partial class DoorDeploy
    {
        public const string PlateGlass = "Door_PlateGlass";
        public const string PlateGlassBronze = "Door_PlateGlass_Bronze";
        public static bool IsPlateGlass(string prop) => prop == PlateGlass || prop == PlateGlassBronze;

        // New art, existing ObjectDoor motion and GlassPane damage. No transparent prop or full-leaf box
        // behind the glass: the frame uses the twelve actual solid model components as its hitboxes.
        static Node3D SpawnPlateGlass(string prop, Node parent, Vector3 pos, float yawDeg, Vector2? fit, bool reverse)
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var mesh = ObjMesh.Load(dir + prop + ".obj");
            if (mesh == null) return null;
            var scale = fit is Vector2 size ? new Vector3(size.X / 2.45f, 1f, size.Y / 2.8f) : Vector3.One;
            if (scale != Vector3.One) mesh = Scaled(mesh, scale);
            var boxes = new List<Aabb>();
            using (var doc = JsonDocument.Parse(System.IO.File.ReadAllText(dir + prop + "_colliders.json")))
                foreach (var box in doc.RootElement.EnumerateArray())
                {
                    var lo = box.GetProperty("min"); var hi = box.GetProperty("max");
                    var a = new Vector3(lo[0].GetSingle(), lo[1].GetSingle(), lo[2].GetSingle()) * scale;
                    var b = new Vector3(hi[0].GetSingle(), hi[1].GetSingle(), hi[2].GetSingle()) * scale;
                    boxes.Add(new Aabb(a, b - a));
                }
            var host = new Node3D { Name = prop };
            parent.AddChild(host); host.GlobalPosition = pos; host.RotationDegrees = new Vector3(0f, yawDeg, 0f);
            var door = ObjectDoor.Spawn(host,
                new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(270f)), Vector3.Zero),
                new Vector3(1.145f, 0f, 0f) * scale, new Vector3(0f, 0f, -1f), reverse ? -90f : 90f,
                .4667f, mesh, MatFor(prop, dir), soundName: "DoorHandle");
            door.UsePlateGlassFrame(boxes, new Vector3(0f, 0f, 1.48f) * scale,
                new Vector2(2.13f * scale.X, 2.32f * scale.Z));
            return host;
        }
    }
}