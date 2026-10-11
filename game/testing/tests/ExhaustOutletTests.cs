using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Where the tailpipe smoke comes out (strawberry 2026-10-10: "fix the exhaust particles to emit
    /// from the rear face of the exhaust pipe on every vehicle").
    ///
    /// ⭐⭐ THIS RE-DERIVES FROM THE MESH; it does not compare against the numbers that were authored. Copying
    /// the constants into the test would assert that I typed them twice consistently, which is worth nothing.
    /// It reloads each body .obj, finds the geometry protruding to the rearmost Z plane, and checks the live
    /// emitter is sitting on it -- so a mesh that gets re-extracted and shifts, or a constant that gets
    /// nudged, fails here.</summary>
    public sealed class ExhaustOutlet : GameTest
    {
        public override string Name => "vehicle.exhaust_outlet";

        // vehicle -> the body mesh its spec actually draws
        static readonly (string Veh, string Mesh)[] Fleet =
        {
            ("ambulance", "ambulance_body.txt"), ("apc", "apc_body.txt"), ("bus", "bus_body.txt"),
            ("firetruck", "firetruck_body.txt"), ("golf", "golf_body.txt"), ("hatchback", "hatchback_body.txt"),
            ("humvee", "humvee_body.txt"), ("jeep", "jeep_body.txt"), ("offroader", "offroad_body.txt"),
            ("police", "police_body.txt"), ("quad", "quad_body.txt"), ("roadster", "roadster_body.txt"),
            ("sedan", "sedan_body.txt"), ("truck", "truck_body.txt"), ("ural", "ural_body.txt"),
            ("van", "van_body.txt"),
        };

        /// <summary>The outlet, re-derived: centroid of the verts reaching the rearmost Z plane, lower half.</summary>
        /// <param name="lowerHalfOnly">true for a whole-vehicle body, where the rear plane also contains the
        /// tail panel and only the LOW part of it is the pipe. false for a standalone pipe part, where every
        /// vert already belongs to the pipe -- applying the body filter there slices the pipe in half and
        /// moves the answer, which is exactly how this calibration first failed.</param>
        static bool OutletOf(string mesh, bool lowerHalfOnly, out Vector3 c, out int n, out float width)
        {
            c = Vector3.Zero; n = 0; width = 0f;
            string path = ProjectSettings.GlobalizePath($"res://content/{mesh}");
            if (!System.IO.File.Exists(path)) return false;
            var v = new List<Vector3>();
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (!line.StartsWith("v ")) continue;
                var t = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 4) v.Add(new Vector3(float.Parse(t[1]), float.Parse(t[2]), float.Parse(t[3])));
            }
            if (v.Count == 0) return false;
            float zmax = float.MinValue, ylo = float.MaxValue, yhi = float.MinValue;
            foreach (var q in v) { zmax = Mathf.Max(zmax, q.Z); ylo = Mathf.Min(ylo, q.Y); yhi = Mathf.Max(yhi, q.Y); }
            float ymid = (ylo + yhi) * 0.5f, mnX = float.MaxValue, mxX = float.MinValue;
            var sum = Vector3.Zero;
            foreach (var q in v)
                if (zmax - q.Z < 0.004f && (!lowerHalfOnly || q.Y < ymid))
                { sum += q; n++; mnX = Mathf.Min(mnX, q.X); mxX = Mathf.Max(mxX, q.X); }
            if (n == 0) return false;
            c = sum / n; width = mxX - mnX;
            return true;
        }

        public override IEnumerable<Step> Run()
        {
            // ⭐⭐ CALIBRATION FIRST. wagon_exhaust.txt is the only pipe shipped as its own part and is where
            // the single pre-existing authored value came from. If the rule cannot recover THAT, nothing it
            // says about the other sixteen means anything -- so this runs before any of them.
            T.Check("the rule recovers the one known answer (wagon_exhaust.txt)",
                    OutletOf("wagon_exhaust.txt", lowerHalfOnly: false, out var cal, out _, out _)
                    && cal.DistanceTo(new Vector3(0.7937f, -0.1747f, 2.8269f)) < 0.001f);

            int bad = 0;
            foreach (var (veh, mesh) in Fleet)
            {
                if (!OutletOf(mesh, lowerHalfOnly: true, out var want, out int n, out float w))
                { T.Check($"{veh}: {mesh} has rear-plane geometry", false); bad++; continue; }

                var car = Vehicle.BuildByName(veh);
                World.AddChild(car);
                yield return Ticks(1);
                var got = car.DebugExhaustLocal;
                bool ok = got.DistanceTo(want) < 0.02f;
                if (!ok) bad++;
                T.Check($"{veh}: emitter on the pipe face (got {got}, mesh says {want}, {n} verts {w:0.00} m)", ok);

                // ⚠ AND IT IS A PIPE, NOT THE WHOLE TAIL PANEL. A 2 m-wide "outlet" means the detector found
                // the back of the body and the position is meaningless even though it matches.
                T.Check($"{veh}: ...and that face is pipe-sized ({w:0.00} m)", w < 1.0f);
                car.QueueFree();
            }
            T.Check($"every vehicle in the fleet list resolved ({Fleet.Length - bad}/{Fleet.Length})", bad == 0);
        }
    }
}
