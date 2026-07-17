using System;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Toggleable net-diagnostics logging for chasing MP bug reports: connect/reject/disconnect with
    /// endpoint + reason, rejected commands with sender, reassembly-abuse kicks, desync reports, and the
    /// 1 Hz traffic rollup (NetWorldServer.LogRollup). OFF by default -- every call site guards on
    /// Enabled, so the hot path pays one branch and zero allocation when the toggle is off, and SP (which
    /// never constructs a session) pays nothing at all.
    ///
    /// The core libs are engine-free, so output goes through the Sink/ErrorSink delegates: the game shell
    /// (DedicatedServer/ClientNode/Main) points them at GD.Print/GD.PrintErr, which lands in journald on
    /// the dedicated server. Enable with UG_NETLOG=1 (env) or the --netlog flag; tests may flip Enabled
    /// directly but must restore it (static state, shared across a test run).
    /// </summary>
    public static class NetLog
    {
        public static bool Enabled;
        public static Action<string> Sink = _ => { };
        public static Action<string> ErrorSink = _ => { };

        public static void Info(string message) { if (Enabled) Sink("[NET] " + message); }
        public static void Warn(string message) { if (Enabled) ErrorSink("[NET] " + message); }
    }
}
