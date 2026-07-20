using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // A wandering animal: wraps a RiggedCharacter and roams a small home range. Ambles (Walk clip) to a random nearby
    // point facing the way it moves, then grazes/idles in place (Idle/Eat/Glance) for a few seconds, repeat. Terrain-
    // following, water-avoiding. AnimalField spawns these (rig added as a child) instead of bare RiggedCharacters.
    public partial class AnimalAgent : Node3D
    {
        public RiggedCharacter Rig;
        public Terrain Terr;
        public Vector3 Home;                                        // spawn point; targets stay within HomeRange of it
        public float Foot;                                          // feet-on-terrain offset
        public uint Seed;
        public byte Species;                                        // A5: AnimalCatalog index (deer/pig/cow), set by AnimalField -> published by AnimalNetSync
        public byte NetAnim { get; private set; }                   // A5: current anim byte for the replica (idle/eat/glance/walk)

        Vector3 _target;
        bool _walking;
        double _idleTimer;
        const float Speed = 1.35f, HomeRange = 12f, Arrive = 0.8f;
        static readonly string[] Ambient = { "Idle", "Eat", "Glance_0", "Idle", "Eat", "Glance_1" };

        uint R() { Seed = Seed * 1664525u + 1013904223u; return Seed >> 9; }

        public void Begin() { AddToGroup("animals"); StartIdle(); }   // A5: join the group AnimalNetSync publishes from (host only -- puppets aren't AnimalAgents)

        void StartIdle()
        {
            _walking = false;
            var clip = Ambient[(int)(R() % (uint)Ambient.Length)];
            Rig?.Play(clip);
            NetAnim = clip == "Eat" ? (byte)AnimalNetAnim.Eat : clip.StartsWith("Glance") ? (byte)AnimalNetAnim.Glance : (byte)AnimalNetAnim.Idle;
            _idleTimer = 3.0 + (R() % 600) / 100.0;                 // graze/idle 3-9 s
        }

        void PickTarget()
        {
            float ang = (R() % 628) / 100f;
            float dist = 4f + (R() % 800) / 100f;                   // 4-12 m amble
            float tx = Home.X + Mathf.Cos(ang) * dist, tz = Home.Z + Mathf.Sin(ang) * dist;
            if (Terr != null && Terrain.IsWater(Terr.SampleDominantLayer(tx, tz))) { StartIdle(); return; }   // don't wade in
            _target = new Vector3(tx, 0f, tz);
            _walking = true;
            NetAnim = (byte)AnimalNetAnim.Walk;
            Rig?.Play("Walk");
        }

        public override void _Process(double delta)
        {
            if (Rig == null || !IsInstanceValid(Rig)) return;
            if (_walking)
            {
                var pos = GlobalPosition;
                float dx = _target.X - pos.X, dz = _target.Z - pos.Z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < Arrive) { StartIdle(); return; }
                float inv = 1f / d, step = Mathf.Min(Speed * (float)delta, d);
                float nx = pos.X + dx * inv * step, nz = pos.Z + dz * inv * step;
                float gy = (Terr != null ? Terr.SampleHeight(nx, nz) : pos.Y - Foot) + Foot;
                GlobalPosition = new Vector3(nx, gy, nz);
                LookAt(new Vector3(nx + dx, gy, nz + dz), Vector3.Up);   // face the way we're moving (-Z toward target)
            }
            else
            {
                _idleTimer -= delta;
                if (_idleTimer <= 0) PickTarget();
            }
        }
    }
}
