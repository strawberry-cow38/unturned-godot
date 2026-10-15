using System;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>Optional owner-only facing for an accepted source map respawn.
    /// Sent immediately before the existing self PlayerRespawned fact on the
    /// same ReliableOrdered stream. Legacy respawn payloads are unchanged.</summary>
    public struct RespawnPoseEvent
    {
        public uint Tick;
        public float Yaw;
        public void Write(NetPakWriter writer)
        {
            if (!float.IsFinite(Yaw)) throw new ArgumentException("Non-finite respawn yaw");
            writer.WriteUInt32(Tick);
            writer.WriteDegrees(Yaw, NetQuantization.YawBits);
        }
        public static bool TryRead(NetPakReader reader, out RespawnPoseEvent value)
        {
            value = default;
            if (!reader.ReadUInt32(out uint tick)
                || !reader.ReadDegrees(out float yaw, NetQuantization.YawBits)) return false;
            value = new RespawnPoseEvent { Tick = tick, Yaw = yaw };
            return true;
        }
    }

    public sealed partial class ServerCombat
    {
        /// <summary>Trusted host adapter, called only from PlayerRespawned after
        /// reposition and before its broadcast. No client command exposes it.</summary>
        public void PublishRespawnYaw(ushort owner, float yaw, long tick)
        {
            if (!float.IsFinite(yaw) || !_players.TryGetByOwner(owner, out var player)) return;
            _players.ServerDrive(owner, player.Pos, yaw, player.LastProcessedInputSeq, tick);
            var fact = new RespawnPoseEvent { Tick = unchecked((uint)tick), Yaw = player.YawDegrees };
            _sendTo?.Invoke(owner, NetMessagePak.Pack(ReplicationIds.EventRespawnPose, fact.Write));
        }
    }

    public sealed partial class NetWorldClient
    {
        RespawnPoseEvent? _pendingRespawnPose;
        uint _lastRespawnPoseTick;
        bool _hasRespawnPose;
        public event Action<RespawnPoseEvent> RespawnPose;

        void RegisterRespawnPoseEvents() => Events.Register<RespawnPoseEvent>(ReplicationIds.EventRespawnPose,
            RespawnPoseEvent.TryRead, value => {
                if (_hasRespawnPose && unchecked((int)(value.Tick - _lastRespawnPoseTick)) <= 0) return;
                if (_pendingRespawnPose is RespawnPoseEvent pending
                    && unchecked((int)(value.Tick - pending.Tick)) <= 0) return;
                _pendingRespawnPose = value;
            });

        void ReceiveRespawned(PlayerRespawnedEvent value)
        {
            PlayerRespawned?.Invoke(value);
            if (value.PlayerId != PlayerId) return;
            var pending = _pendingRespawnPose;
            _pendingRespawnPose = null;
            if (pending is RespawnPoseEvent pose)
            {
                _hasRespawnPose = true;
                _lastRespawnPoseTick = pose.Tick;
                RespawnPose?.Invoke(pose);
            }
        }

        void ResetRespawnPose()
        {
            _pendingRespawnPose = null;
            _lastRespawnPoseTick = 0;
            _hasRespawnPose = false;
        }
    }
}
