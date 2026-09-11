using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>A quest: what has to be true to hand it in, and what you get for it.
    ///
    /// This is the thing 81 of the 157 dialogue conditions have been asking about. Without it those branches
    /// are gated on a status nothing can ever set, so the conversations LOOK complete and quietly only ever
    /// show you the same handful of lines -- which is exactly what Chef Leonard was doing when his trade
    /// branch never appeared.</summary>
    public sealed class NpcQuestDef
    {
        public int Id;
        public string Key = "", Guid = "", Name = "", Description = "";
        public NpcCondition[] Conditions = System.Array.Empty<NpcCondition>();
        public NpcReward[] Rewards = System.Array.Empty<NpcReward>();
    }

    /// <summary>Objectives, progress and turn-in.
    ///
    /// KEPT OUT OF DialogueRules.Passes ON PURPOSE. A dialogue condition asks a question the world can answer
    /// on its own ("is flag 61 set"). A quest OBJECTIVE cannot: "kill six zombies" is a count that belongs to
    /// one condition of one quest, so answering it needs to know WHICH quest and WHICH index -- information an
    /// NpcCondition does not carry and should not, since the same struct is a dialogue gate everywhere else.
    /// So Passes keeps answering dialogue and returns true for the tracked kinds (they never appear on one),
    /// and everything that needs the pair goes through here.</summary>
    public static class QuestRules
    {
        /// <summary>Is this condition an OBJECTIVE you make progress on, rather than a gate the world answers?</summary>
        public static bool IsTracked(ENpcConditionType t) =>
            t == ENpcConditionType.Kills_Zombie || t == ENpcConditionType.Kills_Horde
            || t == ENpcConditionType.Kills_Tree || t == ENpcConditionType.Kills_Object;

        /// <summary>How many of objective `i` are done. Reads from wherever that KIND of objective actually
        /// lives -- a flag from the flags, an item from your bag, a kill count from the quest's own counters --
        /// so nothing has to be mirrored into a second place and kept in step.</summary>
        public static int Progress(NpcQuestDef q, int i, INpcWorld w)
        {
            if (q == null || w == null || (uint)i >= (uint)q.Conditions.Length) return 0;
            var c = q.Conditions[i];
            switch (c.Type)
            {
                case ENpcConditionType.Flag_Bool:
                case ENpcConditionType.Flag_Short:
                    return w.GetFlag(c.Id);
                case ENpcConditionType.Item:
                    return w.CountItem(c.Id);
                default:
                    return IsTracked(c.Type) ? w.QuestProgress((ushort)q.Id, i) : 0;
            }
        }

        /// <summary>What the objective wants. Delegates to NpcCondition.Wants so the number the test compares
        /// against and the number the SENTENCE shows cannot be two different things.</summary>
        public static int Target(NpcCondition c) => c?.Wants ?? 0;

        public static bool ObjectiveMet(NpcQuestDef q, int i, INpcWorld w)
        {
            if (q == null || w == null || (uint)i >= (uint)q.Conditions.Length) return false;
            var c = q.Conditions[i];
            // A flag objective keeps its own comparison -- Barnacle_Cleanup is "Flag_Short 156 >= 9", and
            // treating that as a count would pass it at any value above zero.
            if (c.Type == ENpcConditionType.Flag_Bool || c.Type == ENpcConditionType.Flag_Short)
                return DialogueRules.Passes(c, w);
            return Progress(q, i, w) >= Target(c);
        }

        public static bool CanTurnIn(NpcQuestDef q, INpcWorld w)
        {
            if (q == null || w == null) return false;
            for (int i = 0; i < q.Conditions.Length; i++) if (!ObjectiveMet(q, i, w)) return false;
            return true;
        }

        /// <summary>The status a quest SHOULD be in. Ready is derived rather than stored: a quest becomes ready
        /// the moment its last objective completes, and storing that would mean something has to notice. Nothing
        /// would, and the quest would sit Active with every box ticked.</summary>
        public static ENpcQuestStatus StatusOf(NpcQuestDef q, INpcWorld w)
        {
            if (q == null || w == null) return ENpcQuestStatus.None;
            var raw = w.GetQuestStatus((ushort)q.Id);
            if (raw == ENpcQuestStatus.Completed || raw == ENpcQuestStatus.None) return raw;
            return CanTurnIn(q, w) ? ENpcQuestStatus.Ready : ENpcQuestStatus.Active;
        }

        public static void Give(NpcQuestDef q, INpcWorld w)
        {
            if (q == null || w == null) return;
            if (w.GetQuestStatus((ushort)q.Id) != ENpcQuestStatus.None) return;   // taking it twice is not a thing
            w.SetQuestStatus((ushort)q.Id, ENpcQuestStatus.Active);
        }

        /// <summary>Hand it in: take what it consumes, pay out, mark it done. Returns false and changes NOTHING
        /// if it is not actually ready -- a partial turn-in that ate your items and gave nothing back is the
        /// worst failure this code has available to it.</summary>
        public static bool TurnIn(NpcQuestDef q, INpcWorld w)
        {
            if (q == null || w == null) return false;
            if (w.GetQuestStatus((ushort)q.Id) == ENpcQuestStatus.Completed) return false;
            if (!CanTurnIn(q, w)) return false;

            // Consume FIRST, and only what says Reset. An Item objective without it means you merely had to
            // HAVE the thing -- show the man your fish, keep your fish.
            foreach (var c in q.Conditions)
                if (c != null && c.Reset && c.Type == ENpcConditionType.Item)
                    w.TakeItem(c.Id, c.Amount > 0 ? c.Amount : 1);

            DialogueRules.Grant(q.Rewards, w);
            w.SetQuestStatus((ushort)q.Id, ENpcQuestStatus.Completed);
            return true;
        }

        /// <summary>The objective lines a quest log shows, already filled in. Retail ships the sentence per
        /// condition ("Cleanup {0}/{1} Barnacles"), so this fills the placeholders rather than inventing text
        /// from a type name -- and a condition with no line still says something rather than nothing.</summary>
        public static List<(string Text, bool Done)> Objectives(NpcQuestDef q, INpcWorld w)
        {
            var outp = new List<(string, bool)>();
            if (q == null) return outp;
            for (int i = 0; i < q.Conditions.Length; i++)
            {
                var c = q.Conditions[i];
                int have = Progress(q, i, w);
                // Clamped for DISPLAY only: holding 14 of 11 barnacles should read "11/11", not "14/11". The
                // test above is unclamped, so overshooting still completes it.
                outp.Add((c.Describe(System.Math.Min(have, Target(c))), ObjectiveMet(q, i, w)));
            }
            return outp;
        }
    }
}
