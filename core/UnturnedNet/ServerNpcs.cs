using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>Conversations, quests and trades on the SERVER (master 2026-09-11: "do server auth dialogue,
    /// quests, trades").
    ///
    /// Before this the whole NPC layer was client-side: the client decided which response it was allowed to
    /// pick, whether a quest could be handed in, and whether a pile of goods covered a price. Every one of those
    /// is a claim about the player's own inventory and progress, which is exactly the set of claims a client
    /// must never be the authority on -- "I completed the quest, pay me" is a free-items primitive wearing a
    /// conversation's name.
    ///
    /// THE STATE LIVES HERE AND NOWHERE ELSE. Flags, quest status and kill counters are per-player and the
    /// server holds them; the client is sent a copy so it can DRAW the log, and its copy is never read back.
    /// Items and experience are not duplicated at all -- this reads them through the same InventoryReplication
    /// and SkillsReplication the rest of the server already mutates, so a quest that wants eleven barnacles asks
    /// the one bag that actually exists.
    ///
    /// Validation is the SAME code the client runs, not a second implementation: DialogueRules and QuestRules
    /// are in core precisely so the answer cannot differ. A client that asks for a response it could not see
    /// gets refused by the identical predicate that hid it.</summary>
    public sealed class ServerNpcs
    {
        /// <summary>One player's NPC state, exposed as the very interface the dialogue and quest evaluators
        /// already take. Nothing here knows it is on a server; it is the same INpcWorld a singleplayer session
        /// builds, backed by the server's own inventory and skills instead of the local player's.</summary>
        public sealed class PlayerNpc : INpcWorld
        {
            readonly ServerNpcs _s;
            readonly ushort _owner;
            internal PlayerNpc(ServerNpcs s, ushort owner) { _s = s; _owner = owner; }

            public readonly Dictionary<ushort, short> Flags = new Dictionary<ushort, short>();
            public readonly Dictionary<ushort, ENpcQuestStatus> Quests = new Dictionary<ushort, ENpcQuestStatus>();
            public readonly Dictionary<(ushort, int), int> Progress = new Dictionary<(ushort, int), int>();
            public int Rep;

            /// <summary>The dialogue this player is standing in, or 0. Held SERVER-side because every response
            /// is validated against it: without it a client could send "I pick response 3 of dialogue 59" while
            /// standing in a field, and the only thing stopping it would be its own good manners.</summary>
            public int OpenDialogue;

            public short GetFlag(ushort id) => Flags.TryGetValue(id, out var v) ? v : (short)0;
            public void SetFlag(ushort id, short value) { Flags[id] = value; _s.Touch(_owner); }

            public ENpcQuestStatus GetQuestStatus(ushort id)
            {
                var raw = Quests.TryGetValue(id, out var st) ? st : ENpcQuestStatus.None;
                if (raw != ENpcQuestStatus.Active) return raw;
                // Ready is DERIVED here for the same reason it is on the client: nothing watches the bag, so a
                // quest whose last item just arrived becomes ready by being asked.
                var def = _s.QuestOf(id);
                return def != null && QuestRules.CanTurnIn(def, this) ? ENpcQuestStatus.Ready : ENpcQuestStatus.Active;
            }
            public void SetQuestStatus(ushort id, ENpcQuestStatus status) { Quests[id] = status; _s.Touch(_owner); }
            public int QuestProgress(ushort questId, int index) => Progress.TryGetValue((questId, index), out var n) ? n : 0;
            public void AddQuestProgress(ushort questId, int index, int by)
            { Progress[(questId, index)] = QuestProgress(questId, index) + by; _s.Touch(_owner); }

            public string ActiveHoliday => _s.ActiveHoliday?.Invoke() ?? "";
            public int Reputation => Rep;
            public void AddReputation(int amount) { Rep += amount; _s.Touch(_owner); }

            // ---- the things the server already owns, read through their owners rather than mirrored ----
            public uint Experience => _s.Skills != null && _s.Skills.TryGet(_owner, out var e) ? e.Skills.experience : 0u;

            /// <summary>⚠ THROUGH ServerAward, not PlayerSkills.AwardExperience. That method is the choke point
            /// every other server XP source already uses, and it stamps the entry's changed-tick -- writing the
            /// pool directly awards the experience and then never tells anybody, so the client's skill panel
            /// keeps the old number until something unrelated moves.</summary>
            public void AddExperience(int amount)
            {
                if (amount <= 0) return;
                _s.AwardXp?.Invoke(_owner, amount);
            }

            public int CountItem(ushort itemId)
                => _s.Inventories != null && _s.Inventories.TryGet(_owner, out var e) ? e.Inventory.getItemCount(itemId) : 0;

            public void TakeItem(ushort itemId, int amount)
            {
                if (_s.Inventories == null || !_s.Inventories.TryGet(_owner, out var e)) return;
                e.Inventory.removeItemAmount(itemId, amount);
                _s.Inventories.ServerMarkDirty(_owner);
            }

            public void GiveItem(ushort itemId, short amount)
            {
                if (_s.Inventories == null || !_s.Inventories.TryGet(_owner, out var e)) return;
                int n = System.Math.Max((short)1, amount);
                // tryAddItem CAN FAIL on a full bag, and the failure is silent by design. A reward that cannot
                // fit is dropped rather than retried: the alternative is a quest that will not complete because
                // your bag is full, which reads as the quest being broken.
                for (int i = 0; i < n; i++) if (!e.Inventory.tryAddItem(new Item(itemId))) { Undelivered++; break; }
                _s.Inventories.ServerMarkDirty(_owner);
            }

            /// <summary>Reward items that would not fit. Counted rather than ignored, so "the quest paid me
            /// nothing" has an answer other than a shrug.</summary>
            public int Undelivered;

            /// <summary>A vendor to open, for exactly the next state push. See Choose.</summary>
            public string PendingVendor = "";
        }

        readonly Dictionary<ushort, PlayerNpc> _byOwner = new Dictionary<ushort, PlayerNpc>();

        public InventoryReplication Inventories;
        public SkillsReplication Skills;

        /// <summary>Catalog lookups, injected rather than referenced: core cannot see the game layer's
        /// NpcCatalog, and a server that loaded its own copy would be a second source of truth for the thing
        /// both sides have to agree on exactly.</summary>
        public System.Func<int, NpcDialogue> DialogueOf;
        public System.Func<int, NpcQuestDef> QuestOfId;
        public System.Func<string, NpcVendorDef> VendorOf;
        public System.Func<string> ActiveHoliday;

        /// <summary>(owner) when their flags, quests or progress changed -- the host turns it into a unicast.</summary>
        public System.Action<ushort> Changed;

        /// <summary>Award experience through whatever the server's own choke point is. A Func because that call
        /// needs the current tick, which is the session's business and not this system's.</summary>
        public System.Action<ushort, int> AwardXp;

        internal NpcQuestDef QuestOf(int id) => QuestOfId?.Invoke(id);

        // ⚠ COALESCED. Touch is called by every individual mutation -- each flag a reward sets, each quest
        // status, the dialogue move -- so one "choose" pushed the player's whole state three times, and a quest
        // granting ten flags would have pushed it ten. Dirty-mark inside a command, send once at the end of it.
        // Outside a command (nothing opened a batch) it still sends immediately, so no caller has to remember.
        readonly HashSet<ushort> _dirty = new HashSet<ushort>();
        int _batch;

        internal void Touch(ushort owner)
        {
            if (_batch > 0) { _dirty.Add(owner); return; }
            Changed?.Invoke(owner);
        }

        /// <summary>Run `body` as one command: every state change inside it produces exactly one push.</summary>
        public void Batch(System.Action body)
        {
            _batch++;
            try { body(); }
            finally
            {
                _batch--;
                if (_batch == 0 && _dirty.Count > 0)
                {
                    foreach (var o in _dirty) Changed?.Invoke(o);
                    _dirty.Clear();
                }
            }
        }

        public PlayerNpc For(ushort owner)
        {
            if (!_byOwner.TryGetValue(owner, out var p)) _byOwner[owner] = p = new PlayerNpc(this, owner);
            return p;
        }

        public bool Has(ushort owner) => _byOwner.ContainsKey(owner);
        public void Remove(ushort owner) => _byOwner.Remove(owner);
        public int Count => _byOwner.Count;

        // ---- conversation ------------------------------------------------------------------------------------
        /// <summary>Open a dialogue. The client names WHICH -- it is looking at the person -- but the server
        /// decides whether that dialogue exists and records that this player is in it, which is what every
        /// later response is checked against.</summary>
        public bool Open(ushort owner, int dialogueId)
        {
            var d = DialogueOf?.Invoke(dialogueId);
            if (d == null) return false;
            For(owner).OpenDialogue = dialogueId;
            // ⚠ TOUCH. Accepting the request and telling nobody is the whole failure: the client sends Talk and
            // then waits for the state that says which dialogue it is in, because it is no longer allowed to
            // decide that itself. Without this the server quietly agreed and the panel never opened -- caught by
            // running the SP harness, which turns out to be a listen-server and therefore the MP path all along.
            Touch(owner);
            return true;
        }

        public void Close(ushort owner)
        {
            if (!_byOwner.TryGetValue(owner, out var p)) return;
            p.OpenDialogue = 0;
            Touch(owner);   // same reason as Open: the client's copy of "am I in a conversation" is the server's
        }

        /// <summary>Take a response. Refuses anything the player could not have been offered -- wrong dialogue,
        /// out of range, or a condition that does not pass -- using the same predicate that decided whether to
        /// draw it. Returns the dialogue to move to, or 0 to end the conversation.</summary>
        public bool Choose(ushort owner, int dialogueId, int responseIndex, out int next, out string vendor)
        {
            next = 0; vendor = "";
            var p = For(owner);
            // ⚠ THE OPEN DIALOGUE IS CHECKED, not just that the id names a real one. Otherwise a client can
            // answer any conversation in the game from anywhere, which is how a quest gets handed in by a
            // player who never met the person holding it.
            if (p.OpenDialogue == 0 || p.OpenDialogue != dialogueId) return false;
            var d = DialogueOf?.Invoke(dialogueId);
            if (d == null || (uint)responseIndex >= (uint)d.Responses.Length) return false;
            var r = d.Responses[responseIndex];
            if (!DialogueRules.PassesAll(r.Conditions, p)) return false;

            DialogueRules.Grant(r.Rewards, p);
            if (r.Quest != 0)
            {
                var q = QuestOf(r.Quest);
                if (q != null)
                {
                    var was = p.GetQuestStatus((ushort)q.Id);
                    if (was == ENpcQuestStatus.Ready) QuestRules.TurnIn(q, p);
                    else if (was == ENpcQuestStatus.None) QuestRules.Give(q, p);
                }
            }
            if (!string.IsNullOrEmpty(r.Vendor))
            {
                // A vendor is ONE-SHOT: parked for the next state push and cleared by it. Left on the player it
                // would ride every later change and reopen the shop each time a flag moved.
                vendor = r.Vendor;
                p.PendingVendor = r.Vendor;
                Touch(owner);
                p.PendingVendor = "";
                return true;
            }
            next = r.Dialogue;
            p.OpenDialogue = next;          // 0 ends it, which is how retail writes "Goodbye"
            Touch(owner);
            return true;
        }

        // ---- trade -------------------------------------------------------------------------------------------
        /// <summary>Settle a trade. The client says WHAT it wants and WHAT it is putting up; the server checks
        /// the player actually holds every one of those items, that the vendor takes them, and that they cover
        /// the price -- then moves the goods itself.
        ///
        /// ⚠ THE OFFER IS COUNTED AGAINST THE REAL BAG BEFORE ANYTHING MOVES. A client that offers eleven of an
        /// item it holds three of would otherwise have the first three removed and the rest silently ignored,
        /// and still be paid -- the same "paid out of nothing" hole the affordability check is there to close.</summary>
        public bool Trade(ushort owner, string vendorGuid, int sellIndex, IReadOnlyList<(ushort id, int n)> offer)
        {
            var v = VendorOf?.Invoke(vendorGuid);
            if (v == null || (uint)sellIndex >= (uint)v.Selling.Length) return false;
            var want = v.Selling[sellIndex];
            if (want.Item == 0) return false;                 // a vehicle line: nothing to hand over yet
            var p = For(owner);

            var pile = new Dictionary<ushort, int>();
            if (offer != null)
                foreach (var (id, n) in offer)
                {
                    if (n <= 0) return false;                 // a negative "offer" is an attempt to be paid
                    pile.TryGetValue(id, out int had);
                    pile[id] = had + n;
                }
            foreach (var kv in pile)
            {
                if (TradeRules.ValueOf(v, kv.Key) <= 0) return false;   // they do not take it at all
                if (p.CountItem(kv.Key) < kv.Value) return false;       // and you must actually have it
            }
            if (!TradeRules.CanAfford(v, want, pile)) return false;

            foreach (var kv in pile) p.TakeItem(kv.Key, kv.Value);
            p.GiveItem(want.Item, 1);
            Touch(owner);
            return true;
        }
    }

    // ---- the wire (v47) --------------------------------------------------------------------------------------
    // Every one of these names a thing the player is LOOKING AT and nothing that could be lied about. Which
    // dialogue they opened, which of its responses, which vendor line -- the server owns whether that response
    // was visible, whether the quest is finished, and whether the bag can pay.

    public struct NpcTalkCommand
    {
        public int Dialogue;
        public void Write(NetPakWriter w) => w.WriteUInt16((ushort)Dialogue);
        public static bool TryRead(NetPakReader r, out NpcTalkCommand c)
        {
            c = default;
            if (!r.ReadUInt16(out ushort d)) return false;
            c = new NpcTalkCommand { Dialogue = d };
            return true;
        }
    }

    public struct NpcChooseCommand
    {
        public int Dialogue;
        public byte Response;
        public void Write(NetPakWriter w) { w.WriteUInt16((ushort)Dialogue); w.WriteUInt8(Response); }
        public static bool TryRead(NetPakReader r, out NpcChooseCommand c)
        {
            c = default;
            if (!r.ReadUInt16(out ushort d) || !r.ReadUInt8(out byte i)) return false;
            c = new NpcChooseCommand { Dialogue = d, Response = i };
            return true;
        }
    }

    public struct NpcCloseCommand
    {
        public void Write(NetPakWriter w) { }   // no payload: "I walked away" carries nothing to validate
        public static bool TryRead(NetPakReader r, out NpcCloseCommand c) { c = default; return true; }
    }

    /// <summary>A whole trade in one message. The vendor is named by GUID because that is how vendors are
    /// addressed end to end -- they have no numeric id at all -- and a trade is a rare reliable message, so the
    /// 32 characters cost nothing worth optimising away.</summary>
    public struct NpcTradeCommand
    {
        public const int MaxOffer = 16;   // a pile bigger than this is not a trade, it is a probe
        public string Vendor;
        public byte SellIndex;
        public (ushort Id, byte N)[] Offer;
        public void Write(NetPakWriter w)
        {
            var offer = Offer ?? System.Array.Empty<(ushort, byte)>();
            byte n = (byte)System.Math.Min(offer.Length, MaxOffer);
            w.WriteString(Vendor ?? "");
            w.WriteUInt8(SellIndex);
            w.WriteUInt8(n);
            for (int i = 0; i < n; i++) { w.WriteUInt16(offer[i].Id); w.WriteUInt8(offer[i].N); }
        }
        public static bool TryRead(NetPakReader r, out NpcTradeCommand c)
        {
            c = default;
            if (!r.ReadString(out string vendor)) return false;
            if (!r.ReadUInt8(out byte sell) || !r.ReadUInt8(out byte n)) return false;
            if (n > MaxOffer) return false;   // BEFORE the allocation, not after
            var offer = new (ushort, byte)[n];
            for (int i = 0; i < n; i++)
            {
                if (!r.ReadUInt16(out ushort id) || !r.ReadUInt8(out byte amt)) return false;
                offer[i] = (id, amt);
            }
            c = new NpcTradeCommand { Vendor = vendor, SellIndex = sell, Offer = offer };
            return true;
        }
    }

    /// <summary>The owner's NPC state, whole, on any change. A FULL DUMP rather than a delta: flags and quests
    /// are a few dozen entries that change at conversation speed, and a delta stream would need an ack chain to
    /// survive a dropped packet -- machinery worth an order of magnitude more than the bytes it saves. The
    /// client never reads its copy back; it only draws it.</summary>
    public struct NpcStateEvent
    {
        public const int MaxFlags = 128, MaxQuests = 64, MaxProgress = 64;
        public (ushort Id, short Value)[] Flags;
        public (ushort Id, byte Status)[] Quests;
        public (ushort Quest, byte Index, ushort Value)[] Progress;
        public int Reputation;

        /// <summary>Which dialogue the server has this player standing in, and a vendor it just opened.
        ///
        /// ⚠ THE CLIENT MUST NOT NAVIGATE ITSELF. Every Choose is validated against the dialogue the SERVER
        /// recorded, so a client that advanced on its own prediction and guessed differently would have its
        /// next response refused and the conversation would simply stop -- with nothing on either side saying
        /// why. Sending where it went is one ushort and removes the whole class.</summary>
        public ushort OpenDialogue;
        public string Vendor;

        public void Write(NetPakWriter w)
        {
            var f = Flags ?? System.Array.Empty<(ushort, short)>();
            var q = Quests ?? System.Array.Empty<(ushort, byte)>();
            var pr = Progress ?? System.Array.Empty<(ushort, byte, ushort)>();
            byte nf = (byte)System.Math.Min(f.Length, MaxFlags);
            byte nq = (byte)System.Math.Min(q.Length, MaxQuests);
            byte np = (byte)System.Math.Min(pr.Length, MaxProgress);
            w.WriteInt32(Reputation);
            w.WriteUInt16(OpenDialogue);
            w.WriteString(Vendor ?? "");
            w.WriteUInt8(nf); for (int i = 0; i < nf; i++) { w.WriteUInt16(f[i].Id); w.WriteInt16(f[i].Value); }
            w.WriteUInt8(nq); for (int i = 0; i < nq; i++) { w.WriteUInt16(q[i].Id); w.WriteUInt8(q[i].Status); }
            w.WriteUInt8(np); for (int i = 0; i < np; i++) { w.WriteUInt16(pr[i].Quest); w.WriteUInt8(pr[i].Index); w.WriteUInt16(pr[i].Value); }
        }

        public static bool TryRead(NetPakReader r, out NpcStateEvent e)
        {
            e = default;
            if (!r.ReadInt32(out int rep)) return false;
            if (!r.ReadUInt16(out ushort openDialogue)) return false;
            if (!r.ReadString(out string vendor)) return false;
            if (!r.ReadUInt8(out byte nf) || nf > MaxFlags) return false;
            var f = new (ushort, short)[nf];
            for (int i = 0; i < nf; i++)
            {
                if (!r.ReadUInt16(out ushort id) || !r.ReadInt16(out short v)) return false;
                f[i] = (id, v);
            }
            if (!r.ReadUInt8(out byte nq) || nq > MaxQuests) return false;
            var q = new (ushort, byte)[nq];
            for (int i = 0; i < nq; i++)
            {
                if (!r.ReadUInt16(out ushort id) || !r.ReadUInt8(out byte st)) return false;
                q[i] = (id, st);
            }
            if (!r.ReadUInt8(out byte np) || np > MaxProgress) return false;
            var pr = new (ushort, byte, ushort)[np];
            for (int i = 0; i < np; i++)
            {
                if (!r.ReadUInt16(out ushort qq) || !r.ReadUInt8(out byte ix) || !r.ReadUInt16(out ushort v)) return false;
                pr[i] = (qq, ix, v);
            }
            e = new NpcStateEvent { Reputation = rep, OpenDialogue = openDialogue, Vendor = vendor, Flags = f, Quests = q, Progress = pr };
            return true;
        }
    }
}
