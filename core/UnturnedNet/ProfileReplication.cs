using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>Client -> server, once per join: "this is what I am called and what I look like"
    /// (strawberry 2026-08-26). BOTH fields are untrusted input that every other client will render, so
    /// nothing here is taken on faith: the server re-runs ProfileRules on the name and publishes ITS answer,
    /// and it validates the picture's PNG header WITHOUT decoding it.
    ///
    /// The length prefix is read and BOUNDS-CHECKED before a single byte is allocated. A 4-byte length field
    /// under attacker control is the obvious way to make a reader allocate 4 GB, and "the writer would never
    /// send that" is not a property of the reader.</summary>
    public struct SetProfileCommand
    {
        public string Name;
        public byte[] AvatarPng;   // may be null/empty: a player without a picture is normal

        public void Write(NetPakWriter w)
        {
            w.WriteString(Name ?? "");
            int len = AvatarPng?.Length ?? 0;
            if (len > ProfileRules.MaxAvatarBytes) len = 0;   // never emit a payload the peer must refuse
            w.WriteUInt32((uint)len);
            if (len > 0) w.WriteBytes(AvatarPng, len);
        }

        public static bool TryRead(NetPakReader r, out SetProfileCommand cmd)
        {
            cmd = default;
            if (!r.ReadString(out string name)) return false;
            if (!r.ReadUInt32(out uint len)) return false;
            if (len > ProfileRules.MaxAvatarBytes) return false;   // BEFORE the allocation, not after
            byte[] png = null;
            if (len > 0)
            {
                png = new byte[len];
                if (!r.ReadBytes(png, (int)len)) return false;
            }
            cmd = new SetProfileCommand { Name = name, AvatarPng = png };
            return true;
        }
    }

    /// <summary>Server -> client: the bytes behind an avatar hash the client has not seen. Sent rather than
    /// requested -- the server already knows which hashes it has handed each peer, so a request/response
    /// round trip would only add a wire id and a latency hop.</summary>
    public struct AvatarDataEvent
    {
        public ulong Hash;
        public byte[] Png;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt64(Hash);
            int len = Png?.Length ?? 0;
            if (len > ProfileRules.MaxAvatarBytes) len = 0;
            w.WriteUInt32((uint)len);
            if (len > 0) w.WriteBytes(Png, len);
        }

        public static bool TryRead(NetPakReader r, out AvatarDataEvent evt)
        {
            evt = default;
            if (!r.ReadUInt64(out ulong hash)) return false;
            if (!r.ReadUInt32(out uint len)) return false;
            if (len > ProfileRules.MaxAvatarBytes) return false;
            byte[] png = null;
            if (len > 0)
            {
                png = new byte[len];
                if (!r.ReadBytes(png, (int)len)) return false;
            }
            evt = new AvatarDataEvent { Hash = hash, Png = png };
            return true;
        }
    }

    /// <summary>
    /// Who everyone is: display name + avatar hash per player (SystemId 18). Replicated to EVERYONE, unlike
    /// skills -- the whole point is that other players can see it.
    ///
    /// THE HASH TRAVELS ON THE SNAPSHOT; THE BYTES DO NOT. A snapshot is composed every tick for every peer,
    /// and a 128x128 PNG in one would be a few KB per player per tick forever. So the block carries the name
    /// and a 64-bit content hash, and the image itself goes out once per (peer, hash) on the reliable channel
    /// as an AvatarDataEvent. A player who rejoins with the same picture costs nothing the second time.
    ///
    /// The name on the wire is ALWAYS the server's sanitised form. The client sanitises too, so the player
    /// sees what they will get before they join, but the server never uses the client's answer -- it re-runs
    /// ProfileRules on arrival and stores that. See ProfileRules for what "sanitised" defends against.
    /// </summary>
    public sealed class PlayerProfileReplication : IReplicatedSystem
    {
        public sealed class ProfileEntry
        {
            public ushort OwnerPlayerId;
            public string Name = ProfileRules.FallbackName;
            public ulong AvatarHash;      // 0 = no picture
            public long LastChangedTick;

            // ---- server-only (never on the snapshot; replicas hold the bytes they were sent instead) ----
            public byte[] AvatarPng;
        }

        public byte SystemId => ReplicationIds.SystemProfiles;

        readonly Dictionary<ushort, ProfileEntry> _byOwner = new Dictionary<ushort, ProfileEntry>();

        /// <summary>Client side: avatar bytes keyed by hash, filled by AvatarDataEvent. Shared across
        /// players, so two people using the same picture cost one copy.</summary>
        readonly Dictionary<ulong, byte[]> _avatarCache = new Dictionary<ulong, byte[]>();

        public int Count => _byOwner.Count;
        public bool TryGet(ushort ownerPlayerId, out ProfileEntry entry) => _byOwner.TryGetValue(ownerPlayerId, out entry);

        public IEnumerable<ProfileEntry> All => _byOwner.Values;

        /// <summary>Client side: the decoded-ready PNG bytes for a player, if they have arrived yet.</summary>
        public bool TryGetAvatar(ushort ownerPlayerId, out byte[] png)
        {
            png = null;
            return _byOwner.TryGetValue(ownerPlayerId, out var e) && e.AvatarHash != 0
                && _avatarCache.TryGetValue(e.AvatarHash, out png);
        }

        public bool HasAvatarBytes(ulong hash) => hash != 0 && _avatarCache.ContainsKey(hash);

        /// <summary>Client side: adopt bytes that arrived on the reliable channel. The hash is RECOMPUTED
        /// from the bytes rather than believed -- a server that sends mismatched bytes gets them dropped,
        /// which keeps the cache keyed by what it actually holds. The header is re-validated for the same
        /// reason the server validated it: this is the last point before something decodes an image.</summary>
        public bool ClientAcceptAvatar(ulong hash, byte[] png)
        {
            if (hash == 0 || png == null || png.Length == 0) return false;
            if (ProfileRules.CheckAvatarPng(png) != ProfileRules.AvatarVerdict.Ok) return false;
            if (ProfileRules.AvatarHash(png) != hash) return false;
            _avatarCache[hash] = png;
            return true;
        }

        // ---- server side ----

        static long Stamp(long tick) => tick + 1;   // see DeployableReplication.Stamp (compose-boundary off-by-one)

        public ProfileEntry ServerAdd(ushort ownerPlayerId, string rawName, long tick)
        {
            var e = new ProfileEntry
            {
                OwnerPlayerId = ownerPlayerId,
                Name = ProfileRules.SanitizeName(rawName),
                LastChangedTick = Stamp(tick),
            };
            _byOwner[ownerPlayerId] = e;
            return e;
        }

        public void ServerRemove(ushort ownerPlayerId) => _byOwner.Remove(ownerPlayerId);

        /// <summary>Apply a client's SetProfile. Returns false when nothing changed, so the caller does not
        /// stamp a dirty tick or re-broadcast an identical picture.
        ///
        /// The name is sanitised HERE, by the server, on the raw string that arrived -- the client's own pass
        /// is a courtesy to the player, not an input to this. A picture that fails validation leaves the
        /// player's EXISTING avatar alone rather than clearing it: a rejected upload should not also destroy
        /// what was already working.</summary>
        public bool ServerApplyProfile(ushort ownerPlayerId, string rawName, byte[] png, long tick,
                                       out ProfileRules.AvatarVerdict verdict)
        {
            verdict = ProfileRules.AvatarVerdict.Empty;
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return false;

            string name = ProfileRules.SanitizeName(rawName);
            bool changed = !string.Equals(e.Name, name, System.StringComparison.Ordinal);
            e.Name = name;

            if (png != null && png.Length > 0)
            {
                verdict = ProfileRules.CheckAvatarPng(png);
                if (verdict == ProfileRules.AvatarVerdict.Ok)
                {
                    ulong hash = ProfileRules.AvatarHash(png);
                    if (hash != e.AvatarHash) { e.AvatarHash = hash; e.AvatarPng = png; changed = true; }
                }
            }

            if (changed) e.LastChangedTick = Stamp(tick);
            return changed;
        }

        public byte[] ServerAvatarBytes(ulong hash)
        {
            foreach (var e in _byOwner.Values) if (e.AvatarHash == hash) return e.AvatarPng;
            return null;
        }

        // ---- serving the bytes: a ledger, and a rate limit --------------------------------------------
        //
        // WHAT EACH PEER ALREADY HAS. Without this the server re-broadcasts a full picture to everyone every
        // time anyone's profile changes -- including to peers that already hold those exact bytes, and
        // including the picture a rejoining player has had all along. Keyed by HASH rather than by player, so
        // two people using the same picture cost one send, and a player switching back to a previous picture
        // costs none.
        //
        // It is also the cheap half of an abuse fix. A client that alternates between two pictures turns one
        // 64 KB upload into 64 KB x every peer, repeatedly -- a 32x amplifier pointed at the server's uplink.
        // The ledger removes the repeat; ServerProfileAccepted below removes the rate.
        readonly Dictionary<ushort, HashSet<ulong>> _sentTo = new Dictionary<ushort, HashSet<ulong>>();
        readonly Dictionary<ushort, long> _lastProfileTick = new Dictionary<ushort, long>();

        /// <summary>Minimum ticks between profile changes the server will act on, per peer. A profile is set
        /// once on join and then essentially never; anything faster than this is not a player.</summary>
        public const long ProfileCooldownTicks = 100;   // 2 s at 50 Hz

        public long ProfilesRateLimited { get; private set; }
        public long AvatarSendsSkipped { get; private set; }   // pictures a peer already had -- the ledger's whole return

        /// <summary>Rate gate for CommandSetProfile. Returns false when this peer changed its profile too
        /// recently; the caller drops the command entirely rather than applying half of it.</summary>
        public bool ServerProfileAccepted(ushort playerId, long tick)
        {
            if (_lastProfileTick.TryGetValue(playerId, out long last) && tick - last < ProfileCooldownTicks)
            {
                ProfilesRateLimited++;
                return false;
            }
            _lastProfileTick[playerId] = tick;
            return true;
        }

        /// <summary>Does this peer still need the bytes for this hash? Records the send when it does, so the
        /// caller cannot forget to -- an "ask then mark" pair with two call sites is a pair that drifts.</summary>
        public bool ServerClaimAvatarSend(ushort peerPlayerId, ulong hash)
        {
            if (hash == 0) return false;
            if (!_sentTo.TryGetValue(peerPlayerId, out var set)) _sentTo[peerPlayerId] = set = new HashSet<ulong>();
            if (!set.Add(hash)) { AvatarSendsSkipped++; return false; }
            return true;
        }

        /// <summary>Drop a departed peer's ledger. Player ids are RECYCLED, so leaving this behind would tell
        /// the server that a brand new player already has pictures they have never seen -- the same
        /// recycled-id leak the relevancy systems clear on disconnect.</summary>
        public void ServerForgetPeer(ushort peerPlayerId)
        {
            _sentTo.Remove(peerPlayerId);
            _lastProfileTick.Remove(peerPlayerId);
        }

        /// <summary>Test seam: how many distinct pictures this peer has been sent.</summary>
        public int DebugSentCount(ushort peerPlayerId) => _sentTo.TryGetValue(peerPlayerId, out var set) ? set.Count : 0;

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => WriteAll(w, long.MinValue);

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick) => WriteAll(w, baselineTick);

        void WriteAll(NetPakWriter w, long baselineTick)
        {
            var changed = new List<ProfileEntry>();
            foreach (var e in _byOwner.Values) if (e.LastChangedTick > baselineTick) changed.Add(e);
            w.WriteUInt16((ushort)changed.Count);
            foreach (var e in changed)
            {
                w.WriteUInt16(e.OwnerPlayerId);
                w.WriteString(e.Name ?? ProfileRules.FallbackName);
                w.WriteUInt64(e.AvatarHash);
            }
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt16(out ushort count)) return;
            for (int i = 0; i < count; i++)
            {
                if (!r.ReadUInt16(out ushort owner)) return;
                if (!r.ReadString(out string name)) return;
                if (!r.ReadUInt64(out ulong hash)) return;
                if (!_byOwner.TryGetValue(owner, out var e))
                {
                    e = new ProfileEntry { OwnerPlayerId = owner };
                    _byOwner[owner] = e;
                }
                // Sanitise on ARRIVAL as well. The server already did, so this is normally a no-op -- but it
                // is the last line before this string reaches a renderer, and a client that trusts whatever a
                // server sends is a client that a hostile server owns. Costs a string compare per change.
                e.Name = ProfileRules.SanitizeName(name);
                e.AvatarHash = hash;
            }
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            var owners = new List<ushort>(_byOwner.Keys);
            owners.Sort();
            foreach (ushort id in owners)
            {
                var e = _byOwner[id];
                h = NetHash.MixUInt32(h, e.OwnerPlayerId);
                h = NetHash.MixUInt64(h, e.AvatarHash);
                foreach (char c in e.Name ?? "") h = NetHash.MixUInt32(h, c);
            }
            return h;
        }
    }
}
