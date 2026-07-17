using System;
using System.Collections.Generic;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>Counters for the dispatch choke point -- tests assert these instead of poking at server-side
    /// game state that doesn't exist yet in Phase 2.</summary>
    public sealed class CommandRegistryDiagnostics
    {
        public long Dispatched;           // handler was found and invoked (regardless of validation outcome)
        public long UnknownIdRejected;     // first byte didn't match any registered command id
        public long MalformedRejected;     // registered id, but the typed reader couldn't parse the payload
        public long ValidationRejected;    // parsed fine, but the registered validator refused it
        public long HandlerExceptionsCaught; // should stay zero for well-behaved handlers; never rethrown
    }

    /// <summary>
    /// Client -> server plane (MP_PLAN §2.3). Typed messages with hand-written Write/Read (the
    /// MoveInput.Write pattern, PlayerReplication.cs), dispatched by an explicit append-only byte id -- no reflection
    /// RPC. This is also the one validation choke point the plan calls for: TryDispatch takes the sender's
    /// identity as a parameter supplied by the CALLER (the connection/peer that delivered the bytes), never
    /// read out of the payload itself -- a command cannot claim to be from anyone but the connection that
    /// actually sent it. TryDispatch must never throw regardless of what bytes a hostile or buggy client
    /// sends; see CommandEventPlaneTests for the fuzz proof.
    ///
    /// Command id 0 is reserved for the built-in snapshot ack (SnapshotComposer.AckCommandId) -- gameplay
    /// command ids start at 1. Ids are append-only: once shipped, never renumber or reuse a retired one.
    /// </summary>
    public sealed class CommandRegistry
    {
        public delegate void RawHandler(NetPakReader reader, ushort senderPlayerId);
        public delegate bool TryReadCommand<T>(NetPakReader reader, out T command);

        readonly Dictionary<byte, RawHandler> _handlers = new Dictionary<byte, RawHandler>();

        public CommandRegistryDiagnostics Diag { get; } = new CommandRegistryDiagnostics();

        /// <summary>Register the raw form directly. Prefer the typed overload below for gameplay commands;
        /// this is what SnapshotComposer.RegisterAck uses for the tiny built-in ack.</summary>
        public void Register(byte commandId, RawHandler handler)
        {
            if (_handlers.ContainsKey(commandId))
                throw new InvalidOperationException($"CommandRegistry: id {commandId} already registered (append-only -- pick a new id)");
            _handlers[commandId] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>Typed registration: parse via tryRead, validate (sender authority -- e.g. "does this
        /// sender own the target NetId"), then apply. A parse failure or a failed validation is counted and
        /// dropped -- never applied, never thrown.</summary>
        public void Register<T>(byte commandId, TryReadCommand<T> tryRead, Action<ushort, T> apply, Func<ushort, T, bool> validate = null)
        {
            Register(commandId, (reader, sender) =>
            {
                if (!tryRead(reader, out T cmd))
                {
                    Diag.MalformedRejected++;
                    if (NetLog.Enabled) NetLog.Warn($"cmd {commandId} ({typeof(T).Name}) from player {sender} rejected: malformed payload");
                    return;
                }
                if (validate != null && !validate(sender, cmd))
                {
                    Diag.ValidationRejected++;
                    if (NetLog.Enabled) NetLog.Info($"cmd {commandId} ({typeof(T).Name}) from player {sender} rejected: validation refused");
                    return;
                }
                apply(sender, cmd);
            });
        }

        /// <summary>Dispatch one already-received command message (data[0] is the command id). senderPlayerId
        /// must come from the transport/session that delivered it, never parsed out of `data` -- that's the
        /// whole point of the choke point. Never throws: an empty/unknown/malformed message, or a handler
        /// that throws, is rejected and counted, not propagated.</summary>
        public bool TryDispatch(byte[] data, ushort senderPlayerId)
        {
            if (data == null || data.Length < 1) { Diag.MalformedRejected++; return false; }
            byte commandId = data[0];
            if (!_handlers.TryGetValue(commandId, out var handler))
            {
                Diag.UnknownIdRejected++;
                if (NetLog.Enabled) NetLog.Warn($"cmd {commandId} from player {senderPlayerId} rejected: unknown command id");
                return false;
            }

            var reader = new NetPakReader();
            reader.SetBufferSegment(data, data.Length);
            reader.ReadUInt8(out _); // consume the id byte; the handler reads its payload from here on

            Diag.Dispatched++;
            try
            {
                handler(reader, senderPlayerId);
                return true;
            }
            catch (Exception ex)
            {
                // The one seam untrusted client bytes cross into game logic: a handler bug on a fuzzed
                // payload must drop the command, not take the server down.
                Diag.HandlerExceptionsCaught++;
                if (NetLog.Enabled) NetLog.Warn($"cmd {commandId} from player {senderPlayerId} handler threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
