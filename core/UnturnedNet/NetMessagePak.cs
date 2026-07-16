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
        public static byte[] Pack(byte messageId, Action<NetPakWriter> writePayload, int bufferSize = 256)
        {
            var w = new NetPakWriter { buffer = new byte[bufferSize] };
            w.Reset();
            w.WriteUInt8(messageId);
            writePayload?.Invoke(w);
            w.Flush();
            var result = new byte[w.writeByteIndex];
            Buffer.BlockCopy(w.buffer, 0, result, 0, w.writeByteIndex);
            return result;
        }
    }
}
