using System;
using System.Collections.Generic;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    public sealed class EventRegistryDiagnostics
    {
        public long Dispatched;
        public long UnknownIdSkipped;   // e.g. a future server sent an event id this client build predates
        public long MalformedSkipped;
        public long HandlerExceptionsCaught;
    }

    /// <summary>
    /// Server -> client plane (MP_PLAN §2.3): discrete reliable facts that don't belong in continuous state
    /// (DeployablePlaced, EntityDestroyed, HitConfirm, chat, ...). Same explicit-id, no-reflection shape as
    /// CommandRegistry, mirrored for the opposite direction -- an event is a fact the server asserts, so
    /// there is no sender-identity/validation choke point here, but TryDispatch is still exception-safe: a
    /// malformed or unrecognized event must never take a client down.
    /// </summary>
    public sealed class EventRegistry
    {
        public delegate void RawHandler(NetPakReader reader);
        public delegate bool TryReadEvent<T>(NetPakReader reader, out T evt);

        readonly Dictionary<byte, RawHandler> _handlers = new Dictionary<byte, RawHandler>();

        public EventRegistryDiagnostics Diag { get; } = new EventRegistryDiagnostics();

        public void Register(byte eventId, RawHandler handler)
        {
            if (_handlers.ContainsKey(eventId))
                throw new InvalidOperationException($"EventRegistry: id {eventId} already registered (append-only -- pick a new id)");
            _handlers[eventId] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Register<T>(byte eventId, TryReadEvent<T> tryRead, Action<T> apply)
        {
            Register(eventId, reader =>
            {
                if (!tryRead(reader, out T evt)) { Diag.MalformedSkipped++; return; }
                apply(evt);
            });
        }

        /// <summary>Dispatch one already-received event message (data[0] is the event id). Never throws.</summary>
        public bool TryDispatch(byte[] data)
        {
            if (data == null || data.Length < 1) { Diag.MalformedSkipped++; return false; }
            byte eventId = data[0];
            if (!_handlers.TryGetValue(eventId, out var handler)) { Diag.UnknownIdSkipped++; return false; }

            var reader = new NetPakReader();
            reader.SetBufferSegment(data, data.Length);
            reader.ReadUInt8(out _);

            Diag.Dispatched++;
            try
            {
                handler(reader);
                return true;
            }
            catch
            {
                Diag.HandlerExceptionsCaught++;
                return false;
            }
        }
    }
}
