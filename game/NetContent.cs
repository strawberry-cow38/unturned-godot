using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // The content identity the Phase 4 handshake carries (MP_PLAN §2.2 Connect{..., contentHash} /
    // §4 Phase 4 "version + content hash -> Accept"). One string, hashed the same way by every host in
    // this build -- bump the string whenever shipped content becomes incompatible across builds. Once
    // clients actually load the server's map (full-client milestone), the map name/manifest folds in here.
    public static class NetContent
    {
        public const string Identity = "unturned-godot content v1";
        public static readonly ulong Hash = NetHash.HashString(Identity);
    }
}
