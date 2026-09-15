namespace UnturnedGodot.Net
{
    /// <summary>
    /// The reject byte turned into something a player can act on.
    ///
    /// This exists because the netcode already did its half correctly and nobody read the answer.
    /// NetSession deliberately honours a cross-version Reject ("the handshake escape hatch,
    /// MP_PLAN §2.2") so a wrong-version client LEARNS it was refused, the server blasts the reason
    /// three times, and NetClientSession.RejectReason has held it the whole time -- and no UI ever
    /// displayed it. So a server that was up, healthy and politely explaining itself was
    /// indistinguishable from a dead one (strawberry, 2026-09-15: "the vox unturned server is down
    /// rn" -- it was ticking, 0 players, 18 ms from the status port, and refusing every handshake).
    ///
    /// Engine-free on purpose: it lives in core so L0 can test the strings without booting Godot,
    /// and so the dedicated server can log the same words the client shows.
    /// </summary>
    public static class NetRejectText
    {
        /// <summary>Whether retrying the same address could plausibly succeed without the player changing anything.</summary>
        public static bool IsRetryable(NetRejectReason reason) =>
            reason == NetRejectReason.ServerStarting || reason == NetRejectReason.ServerFull;

        /// <summary>
        /// One line, addressed to the player, naming the fix where there is one.
        /// <paramref name="serverVersion"/> is the refusing server's protocol version, 0 when it did not
        /// say (any server older than v51). <paramref name="ourVersion"/> is this build's.
        /// </summary>
        public static string Describe(NetRejectReason reason, byte serverVersion = 0, byte ourVersion = 0)
        {
            switch (reason)
            {
                case NetRejectReason.VersionMismatch:
                    // The one case where naming both numbers is the whole value: "version mismatch" tells a
                    // player nothing, "the server is on 45, you are on 51" tells them who has to move.
                    return serverVersion != 0 && ourVersion != 0
                        ? $"Different game version — this server runs protocol {serverVersion}, you have {ourVersion}. One of you needs updating."
                        : "Different game version — this server is running a build that cannot talk to yours.";
                case NetRejectReason.ContentMismatch:
                    return "Different game content — this server's installed content does not match yours.";
                case NetRejectReason.ServerFull:
                    return "Server is full.";
                case NetRejectReason.Banned:
                    return "You are banned from this server.";
                case NetRejectReason.ServerStarting:
                    return "Server is still starting up.";
                case NetRejectReason.PingTooHigh:
                    return "Your ping is above this server's limit.";
                case NetRejectReason.WrongPassword:
                    return "Wrong password.";
                case NetRejectReason.None:
                default:
                    // Reached when the link died without a reason byte: a timeout, a firewalled port, an
                    // address with nothing behind it. Deliberately NOT worded as "the server refused you",
                    // because nothing was heard from a server at all -- claiming a refusal we never received
                    // is the same class of lie as showing nothing.
                    return "Could not reach the server — it may be offline, or the address or port may be wrong.";
            }
        }

        /// <summary>Short form for a table cell or a server-browser row.</summary>
        public static string Short(NetRejectReason reason)
        {
            switch (reason)
            {
                case NetRejectReason.VersionMismatch: return "wrong version";
                case NetRejectReason.ContentMismatch: return "wrong content";
                case NetRejectReason.ServerFull: return "full";
                case NetRejectReason.Banned: return "banned";
                case NetRejectReason.ServerStarting: return "starting";
                case NetRejectReason.PingTooHigh: return "ping too high";
                case NetRejectReason.WrongPassword: return "password";
                case NetRejectReason.None:
                default: return "unreachable";
            }
        }
    }
}
