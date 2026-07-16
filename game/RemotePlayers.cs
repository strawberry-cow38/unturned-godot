using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Remote player avatars in a REAL world (MP_PLAN §4 Phase 4: "remote avatars via the existing
    // CharacterModel puppet path -- ClientNode.cs already proves it"). Watches a NetWorldClient's player
    // replicas and keeps one puppet per remote player: spawned on first sight, position-smoothed toward
    // the newest replicated transform (25 Hz snapshots -> per-frame exponential glide, snapping across
    // teleport-sized jumps), freed when the player leaves. The LOCAL player never gets a puppet -- that's
    // the PlayerController shell (loopback) or the prediction path (remote client).
    public partial class RemotePlayers : Node3D
    {
        public NetWorldClient Client;

        const float GlideRate = 14f;      // 1/s exponential approach to the replicated target
        const float SnapDistance = 5f;    // beyond this the glide would look like skating -> snap

        readonly Dictionary<ushort, Node3D> _avatars = new();
        static readonly Color Skin = new Color(0.85f, 0.70f, 0.55f);
        static readonly Color Tint = new Color(1.00f, 0.72f, 0.45f);   // remote = orange, like the net demos

        public int PuppetCount => _avatars.Count;
        public bool TryGetPuppet(ushort playerId, out Node3D avatar) => _avatars.TryGetValue(playerId, out avatar);

        public override void _Process(double delta)
        {
            if (Client == null) return;
            float a = 1f - Mathf.Exp(-GlideRate * (float)delta);

            foreach (var e in Client.Players.All)
            {
                if (e.OwnerPlayerId == Client.PlayerId) continue;   // self is the shell, never a puppet
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                if (!_avatars.TryGetValue(e.OwnerPlayerId, out var av))
                {
                    av = CharacterModel.Loaded ? CharacterModel.Build(Tint) : Humanoid.Build(Skin, Tint, Tint * 0.6f);
                    AddChild(av);
                    av.Position = target;
                    _avatars[e.OwnerPlayerId] = av;
                }
                av.Position = av.Position.DistanceTo(target) > SnapDistance ? target : av.Position.Lerp(target, a);
                av.Rotation = new Vector3(0f, Mathf.DegToRad(e.YawDegrees), 0f);
            }

            if (_avatars.Count > 0)   // a player left -> free the stale puppet
            {
                List<ushort> stale = null;
                foreach (var kv in _avatars)
                    if (!Client.Players.TryGetByOwner(kv.Key, out _)) (stale ??= new List<ushort>()).Add(kv.Key);
                if (stale != null)
                    foreach (var id in stale) { _avatars[id].QueueFree(); _avatars.Remove(id); }
            }
        }
    }
}
