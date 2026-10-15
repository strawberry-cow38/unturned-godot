using System.Text.Json;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>One bounded worker for the unchanged source UDP browser query.
/// A new token supersedes a prior result. Cancellation discards its result; the
/// original socket timeout still owns the running query's lifetime.</summary>
sealed class ServerProbe
{
    Task<SourceServerMenu.ServerStatus> _task;
    long _sequence, _token, _runningToken;
    string _host;
    ushort _port;
    int _nonce;
    bool _cancelled;

    public object Start(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Probe host is required");
        if (port < 1 || port > ushort.MaxValue) throw new ArgumentException("Probe port must be 1..65535");
        _token = ++_sequence; _host = host.Trim(); _port = (ushort)port; _cancelled = false;
        LaunchIfReady();
        return new { token = _token, contentHash = NetContent.Hash.ToString("X16"),
            connectAttempts = SourceServerMenu.ConnectAttempts, rejectRetryLimit = SourceServerMenu.RejectRetryLimit,
            linkTimeoutSeconds = SourceServerMenu.LinkTimeoutSeconds };
    }

    void LaunchIfReady()
    {
        if (_cancelled || _task != null && !_task.IsCompleted || _runningToken == _token) return;
        string host = _host; ushort port = _port;
        uint nonce = (uint)++_nonce ^ (uint)Environment.TickCount;
        _runningToken = _token;
        _task = Task.Run(() => SourceServerMenu.StatusQueryFull(host, port, nonce));
    }

    public object Poll(long token)
    {
        if (token != _token || _cancelled || _token == 0) return new { token, pending = false, cancelled = true };
        LaunchIfReady();
        if (_runningToken != token || !_task.IsCompleted) return new { token, pending = true, cancelled = false };
        var s = _task.GetAwaiter().GetResult();
        bool compatible = s.Ok && s.Version == NetContent.Hash;
        string pingRefusal = s.Ok ? SourceServerMenu.PingGateRefusal(s.MaxPing, s.Ping) : null;
        string refusal = !s.Ok ? NetRejectText.Describe(NetRejectReason.None)
            : !compatible ? NetRejectText.Describe(NetRejectReason.ContentMismatch) : pingRefusal;
        return new { token, pending = false, cancelled = false, contentHash = NetContent.Hash.ToString("X16"), status = new {
            ok = s.Ok, ping = s.Ping, players = s.Players, max = s.Max, version = s.Version.ToString("X16"),
            protocol = s.Protocol, pvp = s.Pvp, passworded = s.Passworded, hasFlags = s.HasFlags,
            maxPing = s.MaxPing, map = s.Map, gamemode = s.Gamemode, motd = s.Motd, iconBytes = s.IconBytes,
            compatible, pingRefusal, refusal,
        } };
    }

    public object Cancel(long? token = null)
    {
        if (token == null || token == _token) _cancelled = true;
        return new { token = token ?? _token, cancelled = token == null || token == _token };
    }
}

public sealed partial class BridgeHost
{
    readonly ServerProbe _serverProbe = new();
}
