using System;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // Server-side authority for FORAGEABLE resources -- berry bushes and mushrooms (retail
    // InteractableForage + ResourceManager.ReceiveForageRequest). Sibling of ServerDestructibles: the
    // replicated ResourceReplication bitmap carries only the alive result, and this owns the per-resource
    // metadata (is it forageable, what does it give, where is it, how long until it grows back) plus the
    // respawn clock. Engine-free so the same host serves SP-loopback and dedicated.
    //
    // Retail shape, from ResourceAsset.cs + ResourceManager.cs on the box:
    //   - a resource is forageable iff its .dat carries the bare key `Forage` (ResourceAsset.cs:334)
    //   - Health is 1, so one interaction takes it -- there is no chopping a bush down
    //   - Reward_ID is a SINGLE item (no Reward_Min/Max), Reset is 1000 s, Forage_Reward_Experience is 1
    //   - the server checks reach at 400 sq.m (20 m) and refuses a dead or non-forage index
    // The port keeps all of that, including the 20 m: it is generous next to any sensible interact ray,
    // which is the point -- the server is guarding against a forged index, not re-deciding what the client
    // may aim at.
    public sealed class ServerForage
    {
        /// <summary>Retail ReceiveForageRequest: `(point - player.position).sqrMagnitude > 400f` rejects.</summary>
        public const float ReachSq = 400f;

        /// <summary>Retail ResourceAsset.cs:347 -- `Forage_Reward_Experience`, default 1. Every forageable
        /// resource in the retail data leaves it at the default, so this is the value in practice.</summary>
        public const uint RewardExperience = 1;

        readonly ResourceReplication _bitmap;

        ushort[] _reward = Array.Empty<ushort>();      // 0 = not forageable (the whole gate; trees and ore stay 0)
        long[] _resetTicks = Array.Empty<long>();
        long[] _respawnAtTick = Array.Empty<long>();   // -1 = not scheduled
        Vector3[] _pos = Array.Empty<Vector3>();

        public ServerForage(ResourceReplication bitmap) => _bitmap = bitmap;

        /// <summary>Size the metadata arrays to the resource index space. Every index starts NOT forageable;
        /// the game side registers the ones that are, so a resource the server was told nothing about can
        /// never be foraged rather than defaulting to something harvestable.</summary>
        public void ServerInit(int count)
        {
            _reward = new ushort[count];
            _resetTicks = new long[count];
            _respawnAtTick = new long[count];
            _pos = new Vector3[count];
            for (int i = 0; i < count; i++) _respawnAtTick[i] = -1;
        }

        /// <summary>Register one forageable resource: what it gives, where it is, and how long it takes to
        /// grow back (Reset seconds x the tick rate). Called once per instance at world boot.</summary>
        public void SetMeta(int index, ushort rewardItem, Vector3 pos, long resetTicks)
        {
            if (index < 0 || index >= _reward.Length) return;
            _reward[index] = rewardItem;
            _pos[index] = pos;
            _resetTicks[index] = resetTicks;
        }

        public bool IsForageable(int index) => index >= 0 && index < _reward.Length && _reward[index] != 0;
        public ushort RewardItem(int index) => index >= 0 && index < _reward.Length ? _reward[index] : (ushort)0;
        public Vector3 Position(int index) => index >= 0 && index < _pos.Length ? _pos[index] : default;

        /// <summary>Everything the server checks before it hands anything over, in retail's order: a real
        /// index, one it was told is forageable, still standing, and within arm's length of the asker.
        /// Split out from the take so a test can ask "would this be refused" without consuming the plant.</summary>
        public bool CanForage(int index, Vector3 from)
        {
            if (!IsForageable(index)) return false;
            if (!_bitmap.IsAlive(index)) return false;
            return (_pos[index] - from).sqrMagnitude <= ReachSq;
        }

        /// <summary>Take it: schedule the regrow and report the reward item. The ALIVE BIT IS NOT FLIPPED
        /// here -- that belongs to ServerTransactions.SetResourceAlive, which owns the broadcast that goes
        /// with it, and having two writers for one bit is how a resource ends up dead on the server and
        /// standing on every client. Returns 0 if it could not be taken.</summary>
        public ushort Take(int index, Vector3 from, long tick)
        {
            if (!CanForage(index, from)) return 0;
            _respawnAtTick[index] = _resetTicks[index] > 0 ? tick + _resetTicks[index] : -1;
            return _reward[index];
        }

        /// <summary>Indices whose Reset has elapsed, for the caller to flip back alive through the same
        /// authoritative path a harvest goes through. Returns a count and fills `into`, rather than
        /// broadcasting here, for the reason Take does not touch the bitmap.</summary>
        public int CollectRegrown(long tick, System.Collections.Generic.List<int> into)
        {
            into.Clear();
            for (int i = 0; i < _respawnAtTick.Length; i++)
            {
                if (_respawnAtTick[i] < 0 || tick < _respawnAtTick[i]) continue;
                _respawnAtTick[i] = -1;
                into.Add(i);
            }
            return into.Count;
        }
    }
}
