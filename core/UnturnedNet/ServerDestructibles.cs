using System;

namespace UnturnedGodot.Net
{
    // Server-side authority for destructible props (retail InteractableObjectRubble). Owns the per-object
    // HEALTH pool and the RESPAWN clock; the replicated DestructibleReplication bitmap only carries the
    // alive result. Engine-free (no Godot) so it lives in the net lib and the same host serves SP-loopback
    // and dedicated. Combat routes an object hit here via DamageObject; a per-tick Tick() respawns objects
    // whose Rubble_Reset timer has elapsed. Metadata (max health + reset ticks per index) is handed in from
    // the game side, which reads it from the object .dat catalog (content/objects/rubble.txt).
    public sealed class ServerDestructibles
    {
        readonly DestructibleReplication _bitmap;
        readonly Action<byte[]> _broadcast;

        float[] _health = Array.Empty<float>();
        float[] _maxHealth = Array.Empty<float>();
        long[] _resetTicks = Array.Empty<long>();
        long[] _respawnAtTick = Array.Empty<long>();   // -1 = not scheduled

        public ServerDestructibles(DestructibleReplication bitmap, Action<byte[]> broadcast)
        {
            _bitmap = bitmap;
            _broadcast = broadcast;
        }

        /// <summary>Size the health/respawn arrays to the destructible index space and seed the bitmap all
        /// alive. Indices left with maxHealth 0 (no metadata supplied -- e.g. an out-of-season holiday slot
        /// that reserves its index but never builds a node) are INDESTRUCTIBLE: DamageObject ignores them.</summary>
        public void ServerInit(int count, long tick)
        {
            _health = new float[count];
            _maxHealth = new float[count];
            _resetTicks = new long[count];
            _respawnAtTick = new long[count];
            for (int i = 0; i < count; i++) _respawnAtTick[i] = -1;
            _bitmap.ServerInit(count, tick);
        }

        /// <summary>Register the retail rubble scalars for one index: section health (Rubble_Health) and the
        /// respawn delay in ticks (Rubble_Reset seconds x 50). Called once per built destructible at boot.</summary>
        public void SetMeta(int index, float maxHealth, long resetTicks)
        {
            if (index < 0 || index >= _maxHealth.Length) return;
            _maxHealth[index] = maxHealth;
            _health[index] = maxHealth;
            _resetTicks[index] = resetTicks;
        }

        public float Health(int index) => index >= 0 && index < _health.Length ? _health[index] : 0f;

        /// <summary>Apply combat damage to a destructible. Returns true if THIS hit destroyed it (health
        /// crossed 0). Idempotent on an already-dead object. On destruction: flip the alive bit + broadcast
        /// the destroyed fact + schedule the respawn (if the object has a finite Rubble_Reset).</summary>
        public bool DamageObject(int index, float amount, long tick)
        {
            if (index < 0 || index >= _health.Length) return false;
            if (_maxHealth[index] <= 0f) return false;      // indestructible / unregistered slot
            if (!_bitmap.IsAlive(index)) return false;      // already broken
            if (amount <= 0f) return false;
            _health[index] -= amount;
            if (_health[index] > 0f) return false;
            _health[index] = 0f;
            _bitmap.ServerSetAlive(index, false, tick);
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventObjectDestroyed, new ObjectDestroyedEvent { Index = (ushort)index }.Write));
            _respawnAtTick[index] = _resetTicks[index] > 0 ? tick + _resetTicks[index] : -1;   // 0/negative reset = never respawns
            return true;
        }

        /// <summary>Respawn any destroyed object whose Rubble_Reset timer has elapsed: refill health, flip the
        /// alive bit back, broadcast the restored fact. Called once per server tick.</summary>
        public void Tick(long tick)
        {
            for (int i = 0; i < _respawnAtTick.Length; i++)
            {
                if (_respawnAtTick[i] < 0 || tick < _respawnAtTick[i]) continue;
                _respawnAtTick[i] = -1;
                _health[i] = _maxHealth[i];
                if (_bitmap.ServerSetAlive(i, true, tick))
                    _broadcast(NetMessagePak.Pack(ReplicationIds.EventObjectRestored, new ObjectRestoredEvent { Index = (ushort)i }.Write));
            }
        }
    }
}
