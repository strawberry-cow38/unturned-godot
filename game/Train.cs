using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A train that rides a track ROAD spline (RoadField material 4 = Tracks): a locomotive + trailing cargo cars,
    // each sitting on 2 bogies snapped to the rail at its distance-along. The body SPANS its two bogies so it
    // articulates correctly through curves; the cars trail at fixed 11 m offsets. Spawned onto the nearest track
    // by the `spawntrain` console command. Movement (throttle -> advance the distance) is the next phase; for now
    // it is placed statically on the rail. (master 2026-08-18)
    public partial class Train : Node3D
    {
        RoadField _roads;
        int _road;
        float _s;                       // distance-along the track of the LOCO's centre
        const float RailY = 0.9f;       // lift the body so its floor rides above the rail
        const float BogieHalf = 3.5f;   // bogie spacing from a unit's centre (source Track_Front/Back at +-3.5)
        const float CarGap = 11f;       // car-to-car spacing along the rail (source Train_Car spacing)
        readonly List<(MeshInstance3D body, MeshInstance3D bf, MeshInstance3D bb, float off)> _units = new();

        /// <summary>Spawn a train onto the nearest track spline to <paramref name="near"/>. Null if there is no
        /// track road (material 4) in the world (only Yukon has tracks).</summary>
        public static Train Spawn(Node parent, RoadField roads, Vector3 near)
        {
            if (roads == null || !roads.NearestTrack(near, out int road, out float s)) return null;
            var t = new Train { _roads = roads, _road = road };
            // Keep the whole train ON the rail: the tail car sits at s-33, the loco's front bogie at s+3.5, so
            // clamp the loco's centre into a range that fits (open roads clamp; a loop wraps so any s is fine).
            if (roads.RoadLoops(road)) t._s = s;
            else { float len = roads.RoadLength(road); t._s = Mathf.Clamp(s, 3f * CarGap + BogieHalf, Mathf.Max(3f * CarGap + BogieHalf, len - BogieHalf)); }
            parent.AddChild(t);
            t.Build();
            return t;
        }

        Material MakeMat(string tex, Color? liveryBody)
        {
            var m = new StandardMaterial3D { TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            if (img.Load(ProjectSettings.GlobalizePath($"res://content/{tex}.png")) == Error.Ok)
            {
                // PAINTABLE LIVERY: recolour the body palette slot (0,1) to a random livery; the orange stripe
                // slot (3,1) stays fixed (master). vanilla trains are not paintable -- this is our addition.
                if (liveryBody.HasValue) { img.Convert(Image.Format.Rgba8); img.SetPixel(0, 1, liveryBody.Value); }
                m.AlbedoTexture = ImageTexture.CreateFromImage(img);
            }
            return m;
        }

        void Build()
        {
            // random livery per spawn: 10% muted grey else a muted random hue (the game's RandomHueOrGrayscale feel)
            Color livery = GD.Randf() < 0.1f ? new Color(0.45f, 0.45f, 0.47f) : Color.FromHsv(GD.Randf(), 0.5f, 0.55f);
            var bodyMat = MakeMat("train_body_tex", livery);
            var carMat = MakeMat("train_car_tex", null);
            var bogieMat = MakeMat("train_bogie_tex", null);
            Mesh Lm(string n) => ContentProvider.ParseObj($"res://content/{n}.txt");
            Mesh body = Lm("train_body"), bogie = Lm("train_bogie"), car = Lm("train_car");

            void MakeUnit(Mesh m, Material mat, float off)
            {
                var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat };
                var bf = new MeshInstance3D { Mesh = bogie, MaterialOverride = bogieMat };
                var bb = new MeshInstance3D { Mesh = bogie, MaterialOverride = bogieMat };
                AddChild(mi); AddChild(bf); AddChild(bb);
                _units.Add((mi, bf, bb, off));
            }
            MakeUnit(body, bodyMat, 0f);
            MakeUnit(car, carMat, CarGap);
            MakeUnit(car, carMat, 2f * CarGap);
            MakeUnit(car, carMat, 3f * CarGap);
            Place();
        }

        void PlaceUnit((MeshInstance3D body, MeshInstance3D bf, MeshInstance3D bb, float off) u, float sctr)
        {
            _roads.EvaluateAlong(_road, sctr + BogieHalf, out var pf, out var tf);
            _roads.EvaluateAlong(_road, sctr - BogieHalf, out var pb, out var tb);
            Vector3 c = (pf + pb) * 0.5f + Vector3.Up * RailY;
            Vector3 fwd = pf - pb; fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
            u.body.GlobalTransform = new Transform3D(Basis.Identity, c).LookingAt(c + fwd, Vector3.Up);
            Vector3 cf = pf + Vector3.Up * (RailY - 0.5f);
            u.bf.GlobalTransform = new Transform3D(Basis.Identity, cf).LookingAt(cf + tf, Vector3.Up);
            Vector3 cb = pb + Vector3.Up * (RailY - 0.5f);
            u.bb.GlobalTransform = new Transform3D(Basis.Identity, cb).LookingAt(cb + tb, Vector3.Up);
        }

        void Place() { foreach (var u in _units) PlaceUnit(u, _s - u.off); }
    }
}
