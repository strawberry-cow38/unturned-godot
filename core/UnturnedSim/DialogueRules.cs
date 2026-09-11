using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>What a dialogue response can be gated on. The full set retail actually uses, counted across all
    /// 142 dialogues rather than guessed from the three I happened to read first: Quest 81, Flag_Bool 29,
    /// Flag_Short 34, Holiday 10, Reputation 2, Experience 1.</summary>
    public enum ENpcConditionType { None, Flag_Bool, Flag_Short, Quest, Holiday, Reputation, Experience }

    /// <summary>Retail's comparison words. Equal is 91 of the 101 that name one, and the 9 that name none mean
    /// Equal too -- an absent Logic is the default, not an error.</summary>
    public enum ENpcLogic { Equal, Not_Equal, Greater_Than, Greater_Than_Or_Equal_To, Less_Than, Less_Than_Or_Equal_To }

    public enum ENpcRewardType { None, Flag_Bool, Flag_Short, Item, Quest }

    /// <summary>Assign writes the value; Increment adds it. 70 of the 90 rewards name neither, and those are
    /// Assign -- the same "absent means the common case" habit the Logic field has.</summary>
    public enum ENpcModification { Assign, Increment }

    public enum ENpcQuestStatus { None, Active, Ready, Completed }

    public sealed class NpcCondition
    {
        public ENpcConditionType Type;
        public ushort Id;
        public short Value;
        public ENpcLogic Logic = ENpcLogic.Equal;
        public ENpcQuestStatus Status;   // Quest conditions compare a STATUS, not a number
        public string Text = "";         // Holiday compares a name
    }

    public sealed class NpcReward
    {
        public ENpcRewardType Type;
        public ushort Id;
        public short Value;
        public ENpcModification Modification = ENpcModification.Assign;
    }

    public sealed class NpcResponse
    {
        public string Text = "";
        public int Dialogue;      // where it goes next; 0 = nowhere
        public string Vendor = ""; // a vendor GUID; empty = not a trade
        public int Quest;
        public NpcCondition[] Conditions = System.Array.Empty<NpcCondition>();
        public NpcReward[] Rewards = System.Array.Empty<NpcReward>();

        /// <summary>A response that goes NOWHERE ends the conversation. That is how retail writes "Goodbye" --
        /// an absence of every target rather than a flag saying so -- and treating it as a missing link instead
        /// of a deliberate exit is how you end up with a dialogue nobody can leave.</summary>
        public bool EndsConversation => Dialogue == 0 && string.IsNullOrEmpty(Vendor) && Quest == 0;
    }

    public sealed class NpcMessage
    {
        public string[] Pages = System.Array.Empty<string>();
        public NpcCondition[] Conditions = System.Array.Empty<NpcCondition>();
    }

    public sealed class NpcDialogue
    {
        public int Id;
        public string Key = "";
        public NpcMessage[] Messages = System.Array.Empty<NpcMessage>();
        public NpcResponse[] Responses = System.Array.Empty<NpcResponse>();
    }

    public sealed class NpcCharacterDef
    {
        public int Id;
        public string Key = "", Name = "";
        public ushort Shirt, Pants, Hat, Vest, Mask, Glasses, Backpack;
        public int Face;
        public string Skin = "", Hair = "";
        public int Dialogue;
    }

    /// <summary>One line of a vendor's list. Retail prices everything in a single currency (experience), which
    /// is exactly what makes master's "items for items" workable without authoring anything: one price list
    /// gives every PAIR an exchange rate. See TradeRules.</summary>
    public sealed class NpcTradeLine
    {
        public string Type = "Item";   // Item | Vehicle
        public ushort Item;            // resolved id (0 for a vehicle, or an unresolved reference)
        public string Guid = "";
        public int Cost;
        public string Spawnpoint = "", Paint = "";
    }

    public sealed class NpcVendorDef
    {
        public string Guid = "", Key = "", Name = "", Description = "";
        public NpcTradeLine[] Selling = System.Array.Empty<NpcTradeLine>();
        public NpcTradeLine[] Buying = System.Array.Empty<NpcTradeLine>();
    }

    /// <summary>What the dialogue evaluator needs to know about the world. An interface rather than concrete
    /// state because the SAME evaluator has to run against a live player in the game layer and against a
    /// fixture in a test, and a condition that can only be checked one of those ways is a condition nobody
    /// checks.</summary>
    public interface INpcWorld
    {
        short GetFlag(ushort id);
        void SetFlag(ushort id, short value);
        ENpcQuestStatus GetQuestStatus(ushort id);
        string ActiveHoliday { get; }
        int Reputation { get; }
        uint Experience { get; }
        void GiveItem(ushort itemId, short amount);
    }

    public static class DialogueRules
    {
        static bool Compare(long a, long b, ENpcLogic logic) => logic switch
        {
            ENpcLogic.Not_Equal => a != b,
            ENpcLogic.Greater_Than => a > b,
            ENpcLogic.Greater_Than_Or_Equal_To => a >= b,
            ENpcLogic.Less_Than => a < b,
            ENpcLogic.Less_Than_Or_Equal_To => a <= b,
            _ => a == b,
        };

        /// <summary>Does this condition hold? An UNKNOWN type passes: a response gated on something this port
        /// has not implemented yet should still be reachable, because the alternative is a conversation that
        /// silently loses half its branches and looks finished. Quests are the live case -- 81 of the 101
        /// conditions are Quest ones and the quest system does not exist, so GetQuestStatus reports None for
        /// everything, which is the truthful answer for a player who has started nothing.</summary>
        public static bool Passes(NpcCondition c, INpcWorld w)
        {
            if (c == null || w == null) return true;
            switch (c.Type)
            {
                case ENpcConditionType.Flag_Bool:
                case ENpcConditionType.Flag_Short:
                    return Compare(w.GetFlag(c.Id), c.Value, c.Logic);
                case ENpcConditionType.Quest:
                    return Compare((int)w.GetQuestStatus(c.Id), (int)c.Status, c.Logic);
                case ENpcConditionType.Holiday:
                    bool same = string.Equals(w.ActiveHoliday ?? "", c.Text ?? "", System.StringComparison.OrdinalIgnoreCase);
                    return c.Logic == ENpcLogic.Not_Equal ? !same : same;
                case ENpcConditionType.Reputation:
                    return Compare(w.Reputation, c.Value, c.Logic);
                case ENpcConditionType.Experience:
                    return Compare(w.Experience, c.Value, c.Logic);
                default:
                    return true;
            }
        }

        public static bool PassesAll(NpcCondition[] cs, INpcWorld w)
        {
            if (cs == null) return true;
            foreach (var c in cs) if (!Passes(c, w)) return false;
            return true;
        }

        /// <summary>The responses this player can actually see. ⚠ Index-preserving is deliberate: the caller
        /// needs to know WHICH response was picked to apply its rewards, and filtering into a fresh list then
        /// indexing by position is how the wrong branch's reward gets granted.</summary>
        public static List<int> AvailableResponses(NpcDialogue d, INpcWorld w)
        {
            var outIdx = new List<int>();
            if (d == null) return outIdx;
            for (int i = 0; i < d.Responses.Length; i++)
                if (PassesAll(d.Responses[i].Conditions, w)) outIdx.Add(i);
            return outIdx;
        }

        /// <summary>The message pages to show: the FIRST message whose conditions hold. Retail gates messages
        /// the same way it gates responses, so an NPC greets you differently once you have done something for
        /// them -- and falls back to the first message rather than going silent if none match.</summary>
        public static NpcMessage MessageFor(NpcDialogue d, INpcWorld w)
        {
            if (d == null || d.Messages.Length == 0) return null;
            foreach (var m in d.Messages) if (PassesAll(m.Conditions, w)) return m;
            return d.Messages[0];
        }

        /// <summary>Grant a response's rewards. Flags and items only -- a Quest reward is recorded as a flag
        /// write it cannot make, and is skipped rather than faked, because a quest silently "granted" into a
        /// system that does not exist is worse than one that visibly did not happen.</summary>
        public static void Grant(NpcReward[] rs, INpcWorld w)
        {
            if (rs == null || w == null) return;
            foreach (var r in rs)
            {
                switch (r.Type)
                {
                    case ENpcRewardType.Flag_Bool:
                    case ENpcRewardType.Flag_Short:
                        short next = r.Modification == ENpcModification.Increment
                                   ? (short)(w.GetFlag(r.Id) + r.Value) : r.Value;
                        w.SetFlag(r.Id, next);
                        break;
                    case ENpcRewardType.Item:
                        w.GiveItem(r.Id, r.Value == 0 ? (short)1 : r.Value);
                        break;
                }
            }
        }
    }

    /// <summary>ITEMS FOR ITEMS (master 2026-09-11: "the shop ui should be more of a trade ui. items for
    /// items"), built out of retail's own numbers rather than an authored barter table.
    ///
    /// Every vendor line already carries a Cost in a single currency, so one price list gives EVERY pair an
    /// exchange rate: if they sell a rifle for 1000 and buy scrap at 250, the rifle costs four scrap. That is
    /// a trade in items with no new content invented and no rate I chose -- which matters, because a barter
    /// rate I made up is a balance decision wearing a mechanic's clothes.
    ///
    /// ⚠ An item the vendor does NOT buy is worth nothing to them. Not "worth its sell price" -- a vendor who
    /// accepts anything at its own asking price is an infinite-money machine the moment two vendors disagree.</summary>
    public static class TradeRules
    {
        public static int ValueOf(NpcVendorDef v, ushort itemId)
        {
            if (v == null) return 0;
            foreach (var b in v.Buying) if (b.Item == itemId) return b.Cost;
            return 0;
        }

        /// <summary>What a pile of offered items is worth to this vendor.</summary>
        public static int OfferValue(NpcVendorDef v, IEnumerable<ushort> offered)
        {
            int t = 0;
            if (offered != null) foreach (var id in offered) t += ValueOf(v, id);
            return t;
        }

        /// <summary>Is this pile enough for that line? Deliberately NOT "exactly enough": you may overpay, the
        /// same way you can hand over a note and not get change, and refusing an overpay would make a trade
        /// impossible whenever no combination adds up exactly.</summary>
        public static bool CanAfford(NpcVendorDef v, NpcTradeLine want, IEnumerable<ushort> offered)
            => want != null && OfferValue(v, offered) >= want.Cost;

        /// <summary>How many of `pay` it takes to cover `want`, or 0 if the vendor will not take that item at
        /// all. Rounds UP -- four-and-a-bit scrap means five.</summary>
        public static int PriceIn(NpcVendorDef v, NpcTradeLine want, ushort pay)
        {
            int unit = ValueOf(v, pay);
            if (unit <= 0 || want == null || want.Cost <= 0) return 0;
            return (want.Cost + unit - 1) / unit;
        }
    }
}
