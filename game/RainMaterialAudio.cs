using Godot;

namespace UnturnedGodot
{
    // Positional rain-on-material audio (master 2026-08-30): a material prop near the player EMITS its own
    // rain-on-that-material sound from itself, 3D-positioned + gated on rain intensity + faded over a radius. Walk near
    // a car -> rain drums on the car; near trees -> the canopy hiss; near a metal shed -> the tin drum. Instead of an
    // emitter on every prop (there are thousands of trees), ONE pooled 3D emitter per material snaps to the NEAREST
    // prop of that material inside the radius each poll -- cost is O(one sphere query), not O(props).
    //
    // ⚠⚠ on the "Rain" bus (NOT SoundBus -- that's the zombie-HEARING path; a rain loop through it = permanent aggro).
    // Materials wired: car (Vehicle nodes), foliage (TreeTrunk). metal + tarp are built structures, keyed by
    // WallPlan.Material (a per-surface palette index -- tinyclaw); slotting them in is a palette-index -> sound map,
    // added once the metal indices are confirmed. freesound CC0 layers, see content/CREDITS.md.
    public partial class RainMaterialAudio : Node
    {
        public float Intensity;   // rint 0..1 (WeatherManager drives it)
        public Camera3D Cam;      // listener position (the player camera)

        const float Radius = 16f;         // "a radius where the material sound is produced from" (master) -- audible range per prop
        const float PollSeconds = 0.25f;  // re-scan for the nearest material prop 4x/sec (props don't teleport; cheap)

        AudioStreamPlayer3D _car, _foliage;   // one emitter per material, re-homed to the nearest prop of that material
        float _poll;

        public override void _Ready()
        {
            _car = MakeEmitter("res://content/rain_car.wav");
            _foliage = MakeEmitter("res://content/rain_foliage.wav");
        }

        AudioStreamPlayer3D MakeEmitter(string res)
        {
            var p = ProjectSettings.GlobalizePath(res);
            if (!System.IO.File.Exists(p) || AudioStreamWav.LoadFromFile(p) is not AudioStreamWav w) return null;
            w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            w.LoopEnd = (int)(w.GetLength() * w.MixRate + 0.5f);   // ⚠ else LoopEnd defaults 0 -> plays silent (the rain-bed trap)
            var pl = new AudioStreamPlayer3D
            {
                Stream = w, Bus = "Rain", VolumeDb = -80f, Autoplay = false,
                UnitSize = Radius * 0.6f, MaxDistance = Radius, MaxDb = 0f,   // full-ish within the prop's reach, silent past the radius
            };
            AddChild(pl);
            return pl;
        }

        public override void _Process(double delta)
        {
            float rint = Mathf.Clamp(Intensity, 0f, 1f);
            if (rint < 0.02f || Cam == null) { Silence(_car); Silence(_foliage); return; }
            _poll -= (float)delta;
            if (_poll > 0f) return;
            _poll = PollSeconds;

            if (Cam.GetWorld3D()?.DirectSpaceState is not PhysicsDirectSpaceState3D space) return;
            Vector3 at = Cam.GlobalPosition;

            // one sphere query for everything nearby on the world layer, classified by node type -> nearest per material
            var q = new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = Radius },
                Transform = new Transform3D(Basis.Identity, at),
                CollisionMask = 1u << 0, CollideWithBodies = true, CollideWithAreas = false,
            };
            var hits = space.IntersectShape(q, 48);
            Vector3? carPos = null, folPos = null;
            float carD = float.MaxValue, folD = float.MaxValue;
            foreach (var h in hits)
            {
                if (h["collider"].As<GodotObject>() is not Node3D n) continue;
                float d = n.GlobalPosition.DistanceTo(at);
                if (FindAncestor<Vehicle>(n) != null) { if (d < carD) { carD = d; carPos = n.GlobalPosition; } }
                else if (FindAncestor<TreeTrunk>(n) != null) { if (d < folD) { folD = d; folPos = n.GlobalPosition; } }
            }
            Drive(_car, carPos, rint);
            Drive(_foliage, folPos, rint);
            if (System.Environment.GetEnvironmentVariable("UG_RAINMATDBG") != null)
                GD.Print($"[rainmat] car={carPos.HasValue}({carD:0.0}m) foliage={folPos.HasValue}({folD:0.0}m) rint={rint:0.00}");
        }

        static T FindAncestor<T>(Node n) where T : Node { for (; n != null; n = n.GetParent()) if (n is T t) return t; return null; }

        static void Drive(AudioStreamPlayer3D pl, Vector3? pos, float rint)
        {
            if (pl == null) return;
            if (pos is Vector3 p)
            {
                pl.GlobalPosition = p;
                if (!pl.Playing)
                {
                    double len = (pl.Stream as AudioStreamWav)?.GetLength() ?? 0.0;
                    pl.Play(len > 0.0 ? (float)(GD.Randf() * len) : 0f);   // random start so a row of same-material props doesn't phase-lock
                }
                pl.VolumeDb = Mathf.Lerp(-14f, -2f, rint);   // louder in heavier rain
            }
            else Silence(pl);
        }

        static void Silence(AudioStreamPlayer3D pl) { if (pl != null && pl.Playing) pl.Stop(); }
    }
}
