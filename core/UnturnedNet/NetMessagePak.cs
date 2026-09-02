using System;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Shared shape for both the command and event planes (MP_PLAN §2.3): every message on the wire is an
    /// id byte followed by a hand-written payload. This just packs that byte[] ready for SendReliable /
    /// SendUnreliableSequenced; CommandRegistry/EventRegistry own unpacking + dispatch on the receive side.
    /// </summary>
    public static class NetMessagePak
    {
        /// <summary>The biggest message Pack will build: the reliable channel's fragment ceiling
        /// (NetSession.SendReliable refuses anything larger), so a payload that cannot be SENT fails HERE, by
        /// name, instead of being packed and then dropped at the transport.</summary>
        public const int MaxMessageBytes = NetProtocol.MaxReliableMessageBytes;

        /// <summary>Pack a message, GROWING the buffer until the payload fits. `bufferSize` is only the first
        /// guess (256 covers every command and event except the profile picture); a payload that overflows it
        /// is re-written into a buffer 4x larger, up to MaxMessageBytes, and only then thrown -- never
        /// truncated.
        ///
        /// Until 2026-09-02 an overflow was SILENT: NetPakWriter.WriteBytes sets BufferOverflow and writes
        /// nothing, Pack ignored the flag and shipped whatever had fit -- a well-formed datagram whose
        /// length prefix promised bytes that were not there. That is how the profile picture died:
        /// SetProfileCommand (name + up to 64 KB of PNG) went through the 256-byte default, the server's
        /// TryRead failed on the missing bytes, CommandRegistry counted it MalformedRejected and dropped the
        /// WHOLE command -- the NAME included -- so every joiner on the dedicated server rendered as "player"
        /// with the checkerboard avatar (strawberry: "custom names and pfps arent showing on the server").
        /// No 128x128 PNG can fit in 256 bytes (the smallest flat-colour one is ~361), so it failed for
        /// everyone with a picture, every time, while the test suite's 69-byte synthetic PNG sailed through.
        /// The writer must be pure (it is re-run on growth); every Write in this codebase is.</summary>
        public static byte[] Pack(byte messageId, Action<NetPakWriter> writePayload, int bufferSize = 256)
        {
            int size = Math.Clamp(bufferSize, 16, MaxMessageBytes);
            while (true)
            {
                var w = new NetPakWriter { buffer = new byte[size] };
                w.Reset();
                w.WriteUInt8(messageId);
                writePayload?.Invoke(w);
                w.Flush();
                if ((w.errors & NetPakWriter.EErrorFlags.BufferOverflow) == 0)
                {
                    var result = new byte[w.writeByteIndex];
                    Buffer.BlockCopy(w.buffer, 0, result, 0, w.writeByteIndex);
                    return result;
                }
                if (size >= MaxMessageBytes)
                    throw new InvalidOperationException($"NetMessagePak.Pack: message id {messageId} does not fit in {MaxMessageBytes} bytes (the reliable-channel ceiling); it cannot be sent");
                size = (int)Math.Min((long)size * 4, MaxMessageBytes);
            }
        }
    }
}
