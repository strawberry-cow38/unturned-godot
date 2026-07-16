using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // The player registry that replaces the PlayerController.Local static (MP_PLAN §3.4/§5 item 7: "death
    // of PlayerController.Local -- call sites become iterate-players or nearest-player queries"). Every
    // PlayerController registers on _EnterTree and unregisters on _ExitTree, so QueueFree cleans up and
    // the L1 sandbox teardown leaves nothing stale. In single-player there is exactly one entry, so every
    // converted call site resolves to the same player Local used to be.
    public static class PlayerRegistry
    {
        static readonly List<PlayerController> _players = new();

        public static IReadOnlyList<PlayerController> All => _players;
        public static int Count => _players.Count;

        internal static void Register(PlayerController p) { if (!_players.Contains(p)) _players.Add(p); }
        internal static void Unregister(PlayerController p) => _players.Remove(p);

        /// <summary>The player nearest to a world point (vehicle prompts, interaction range checks). Null
        /// when no players exist (dedicated world before anyone joins, aerial modes).</summary>
        public static PlayerController Nearest(Vector3 pos)
        {
            PlayerController best = null; float bestD = float.MaxValue;
            foreach (var p in _players)
            {
                if (!GodotObject.IsInstanceValid(p)) continue;
                float d = p.GlobalPosition.DistanceSquaredTo(pos);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        /// <summary>Explosion camera shake for every player (each FlinchFromExplosion distance-gates itself,
        /// so far-away players feel nothing -- same result as the old Local-only call with one player).</summary>
        public static void FlinchAllFromExplosion(Vector3 point, float radius, float magnitudeDegrees)
        {
            foreach (var p in _players)
                if (GodotObject.IsInstanceValid(p)) p.FlinchFromExplosion(point, radius, magnitudeDegrees);
        }

        public static void ResetForTests() => _players.Clear();
    }
}
