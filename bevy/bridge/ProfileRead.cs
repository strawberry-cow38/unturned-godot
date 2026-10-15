using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    static object ProfileMetadata() => new {
        minNameChars = ProfileRules.MinNameChars, maxNameChars = ProfileRules.MaxNameChars,
        fallbackName = ProfileRules.FallbackName, avatarPixels = ProfileRules.AvatarPixels,
        maxAvatarBytes = ProfileRules.MaxAvatarBytes, faceCount = PlayerProfileReplication.FaceCount,
    };

    static object ReadProfileRules(JsonElement request)
    {
        string name = ProfileRules.SanitizeName(Text(request, "name"), out bool changed);
        return new { rules = ProfileMetadata(), name, changed };
    }

    object ReadProfileAvatar(JsonElement request)
    {
        ushort playerId = checked((ushort)Int(request, "playerId"));
        ulong expected = request.GetProperty("avatarHash").GetUInt64();
        string status = "missing";
        ulong hash = 0;
        byte[] png = null;
        if (_client.State == NetSessionState.Connected && _client.Players.TryGetByOwner(playerId, out _)
            && _client.Profiles.TryGet(playerId, out var profile))
        {
            hash = profile.AvatarHash;
            status = hash != expected ? "stale" : hash == 0 ? "none"
                : _client.Profiles.TryGetAvatar(playerId, out png) ? "ready" : "pending";
        }
        // Both profile and reliable avatar bytes are already source-validated.
        // A stale player/hash pair must not return another accepted picture, and
        // cached bytes for a disconnected/recycled player are not a profile.
        return new { playerId, avatarHash = hash, status, png };
    }
}
