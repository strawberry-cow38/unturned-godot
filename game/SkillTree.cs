namespace UnturnedGodot
{
    /// <summary>What a node hands you when you take it. A node may hand over SEVERAL of these -- strawberry
    /// 2026-09-09: "some nodes may unlock multiple things" -- so this is always an array, never a single value.</summary>
    public enum SkillGrantKind { Recipe, Buff, Ability, Pillar }

    public sealed class SkillGrant
    {
        public SkillGrantKind Kind;
        public string Value;     // recipe id / stat name / sub-pillar id
        public float Amount;     // buffs only
        public string Text;      // optional override for the one-line description

        public string Describe() => Text ?? Kind switch
        {
            SkillGrantKind.Recipe => $"Recipe: {Value}",
            SkillGrantKind.Buff => $"{Value} {(Amount >= 0f ? "+" : "")}{Amount:0.##}",
            SkillGrantKind.Ability => $"Ability: {Value}",
            SkillGrantKind.Pillar => $"Opens: {Value}",
            _ => Value,
        };
    }

    /// <summary>A column of the tree. A PILLAR is one of the main skills across the top; an ADVANCED pillar (a
    /// "sub pillar" -- engineering and friends) sits in the lower band and does not exist for you until the nodes
    /// in <see cref="Requires"/> are taken.</summary>
    public sealed class SkillPillar
    {
        public string Id, Title, Blurb;
        public bool Advanced;
        public string[] Requires = System.Array.Empty<string>();
        public int Column;
    }

    /// <summary>One takeable node. <see cref="Requires"/> is an AND across node ids -- strawberry: "some nodes may
    /// require multiple things to unlock" -- and those ids may live under a DIFFERENT pillar, which is what makes
    /// this a graph rather than three independent ladders.</summary>
    public sealed class SkillNode
    {
        public string Id, Title, Blurb;
        public string Pillar;
        public int Tier;
        public uint Cost;
        public string[] Requires = System.Array.Empty<string>();
        public SkillGrant[] Grants = System.Array.Empty<SkillGrant>();
    }

    /// <summary>The graph itself: pillars, advanced pillars, and the nodes hanging off them.
    ///
    /// PLACEHOLDER CONTENT (strawberry 2026-09-09: "main skills, not sure which ones to have yet, so do some
    /// placeholders"). The NAMES here are stand-ins and expected to be replaced; the SHAPE is not. It deliberately
    /// exercises every rule the design has to support, so replacing the words cannot quietly break the structure:
    ///   - three main pillars, each a short ladder
    ///   - three advanced pillars, each opened by a node rather than available from the start
    ///   - `field_surgery` requires TWO nodes from TWO different pillars
    ///   - `improvised_armour` requires two and grants two
    /// If you only ever wire up one-prerequisite, one-grant nodes, the multi- cases rot untested.</summary>
    public sealed class SkillTree
    {
        public SkillPillar[] Pillars = System.Array.Empty<SkillPillar>();
        public SkillNode[] Nodes = System.Array.Empty<SkillNode>();

        public SkillPillar PillarOf(string id)
        {
            foreach (var p in Pillars) if (p.Id == id) return p;
            return null;
        }

        public SkillNode NodeOf(string id)
        {
            foreach (var n in Nodes) if (n.Id == id) return n;
            return null;
        }

        static SkillGrant Recipe(string v) => new() { Kind = SkillGrantKind.Recipe, Value = v };
        static SkillGrant Buff(string v, float a) => new() { Kind = SkillGrantKind.Buff, Value = v, Amount = a };
        static SkillGrant Ability(string v) => new() { Kind = SkillGrantKind.Ability, Value = v };
        static SkillGrant Opens(string v) => new() { Kind = SkillGrantKind.Pillar, Value = v };

        public static SkillTree Placeholder()
        {
            var t = new SkillTree
            {
                Pillars = new[]
                {
                    new SkillPillar { Id = "might",   Title = "Might",   Column = 0, Blurb = "Placeholder pillar -- carrying, striking, holding ground." },
                    new SkillPillar { Id = "vigor",   Title = "Vigor",   Column = 1, Blurb = "Placeholder pillar -- breath, blood and staying upright." },
                    new SkillPillar { Id = "craft",   Title = "Craft",   Column = 2, Blurb = "Placeholder pillar -- making, mending and improvising." },

                    new SkillPillar { Id = "marksmanship", Title = "Marksmanship", Column = 0, Advanced = true,
                                      Blurb = "Advanced. Opened by Steady Hands.", Requires = new[] { "steady_hands" } },
                    new SkillPillar { Id = "medicine",     Title = "Medicine",     Column = 1, Advanced = true,
                                      Blurb = "Advanced. Opened by Field Dressing.", Requires = new[] { "field_dressing" } },
                    new SkillPillar { Id = "engineering",  Title = "Engineering",  Column = 2, Advanced = true,
                                      Blurb = "Advanced. Opened by Salvage.", Requires = new[] { "salvage" } },
                },
                Nodes = new[]
                {
                    // ---- MIGHT
                    new SkillNode { Id = "heavy_lifter", Pillar = "might", Tier = 0, Cost = 20, Title = "Heavy Lifter",
                        Blurb = "Carry more before it starts to hurt.",
                        Grants = new[] { Buff("Carry weight", 15f) } },
                    new SkillNode { Id = "steady_hands", Pillar = "might", Tier = 1, Cost = 40, Title = "Steady Hands",
                        Blurb = "The sights stop wandering.", Requires = new[] { "heavy_lifter" },
                        Grants = new[] { Buff("Sway", -20f), Opens("Marksmanship") } },
                    new SkillNode { Id = "brawler", Pillar = "might", Tier = 2, Cost = 60, Title = "Brawler",
                        Blurb = "Melee lands harder and swings sooner.", Requires = new[] { "heavy_lifter" },
                        Grants = new[] { Buff("Melee damage", 12f), Buff("Swing rate", 8f) } },

                    // ---- VIGOR
                    new SkillNode { Id = "deep_lungs", Pillar = "vigor", Tier = 0, Cost = 20, Title = "Deep Lungs",
                        Blurb = "Sprint and swim for longer.",
                        Grants = new[] { Buff("Stamina", 20f) } },
                    new SkillNode { Id = "field_dressing", Pillar = "vigor", Tier = 1, Cost = 40, Title = "Field Dressing",
                        Blurb = "Stop your own bleeding properly.", Requires = new[] { "deep_lungs" },
                        Grants = new[] { Recipe("Bandage"), Opens("Medicine") } },
                    new SkillNode { Id = "hardy", Pillar = "vigor", Tier = 2, Cost = 60, Title = "Hardy",
                        Blurb = "Cold, wet and hunger bite more slowly.", Requires = new[] { "deep_lungs" },
                        Grants = new[] { Buff("Warmth", 10f), Buff("Food drain", -10f) } },

                    // ---- CRAFT
                    new SkillNode { Id = "salvage", Pillar = "craft", Tier = 0, Cost = 20, Title = "Salvage",
                        Blurb = "Break things down without ruining the parts.",
                        Grants = new[] { Ability("Scrap for parts"), Opens("Engineering") } },
                    new SkillNode { Id = "sharpening", Pillar = "craft", Tier = 1, Cost = 40, Title = "Sharpening",
                        Blurb = "Put an edge back on a tired blade.", Requires = new[] { "salvage" },
                        Grants = new[] { Recipe("Whetstone") } },
                    new SkillNode { Id = "improvised_armour", Pillar = "craft", Tier = 2, Cost = 80, Title = "Improvised Armour",
                        Blurb = "Plate a vest out of what you had lying about.",
                        // TWO prerequisites, from TWO pillars, granting TWO things -- the multi- case, alive.
                        Requires = new[] { "sharpening", "heavy_lifter" },
                        Grants = new[] { Recipe("Scrap Vest"), Buff("Armour", 8f) } },

                    // ---- MARKSMANSHIP (advanced)
                    new SkillNode { Id = "zeroing", Pillar = "marksmanship", Tier = 0, Cost = 60, Title = "Zeroing",
                        Blurb = "Hold the crosshair where the round lands.",
                        Grants = new[] { Buff("Recoil", -15f) } },
                    new SkillNode { Id = "breath_control", Pillar = "marksmanship", Tier = 1, Cost = 90, Title = "Breath Control",
                        Blurb = "Hold the scope still for a beat longer.", Requires = new[] { "zeroing" },
                        Grants = new[] { Buff("Hold breath", 40f) } },

                    // ---- MEDICINE (advanced)
                    new SkillNode { Id = "splinting", Pillar = "medicine", Tier = 0, Cost = 60, Title = "Splinting",
                        Blurb = "Walk off a broken leg. Slowly.",
                        Grants = new[] { Recipe("Splint") } },
                    new SkillNode { Id = "field_surgery", Pillar = "medicine", Tier = 1, Cost = 120, Title = "Field Surgery",
                        Blurb = "Serious work, away from any hospital.",
                        // Reaches ACROSS the tree: one node from Medicine, one from Craft.
                        Requires = new[] { "splinting", "sharpening" },
                        Grants = new[] { Recipe("Suture Kit"), Ability("Revive downed players") } },

                    // ---- ENGINEERING (advanced)
                    new SkillNode { Id = "wiring", Pillar = "engineering", Tier = 0, Cost = 60, Title = "Wiring",
                        Blurb = "Run power where there wasn't any.",
                        Grants = new[] { Recipe("Cable Spool"), Ability("Rewire junction boxes") } },
                    new SkillNode { Id = "machining", Pillar = "engineering", Tier = 1, Cost = 120, Title = "Machining",
                        Blurb = "Cut parts to fit rather than filing them down.", Requires = new[] { "wiring" },
                        Grants = new[] { Recipe("Machined Parts"), Buff("Repair rate", 25f) } },
                },
            };
            return t;
        }
    }

    /// <summary>What this player has taken, and the rules for taking more. Kept separate from the graph so the
    /// graph stays a constant and the save only ever has to carry a set of ids.</summary>
    public sealed class SkillProgress
    {
        readonly System.Collections.Generic.HashSet<string> _taken = new();

        public System.Collections.Generic.IReadOnlyCollection<string> Taken => _taken;
        public bool Has(string id) => _taken.Contains(id);
        public int Count => _taken.Count;
        public void Clear() => _taken.Clear();
        public void GrantForTest(string id) => _taken.Add(id);

        /// <summary>An advanced pillar is closed until every node that opens it is taken. Main pillars are always
        /// open. Returns true for an unknown pillar so a typo shows as an open column rather than a silent gap.</summary>
        public bool PillarOpen(SkillPillar p)
        {
            if (p == null || !p.Advanced) return true;
            foreach (var r in p.Requires) if (!_taken.Contains(r)) return false;
            return true;
        }

        /// <summary>Everything standing between you and this node, in the order a player would ask: already taken,
        /// is its column even open, are its prerequisites in, can you pay. <paramref name="why"/> is written for
        /// display, so it names the missing thing rather than saying "requirements not met".</summary>
        public bool CanTake(SkillTree tree, SkillNode n, uint xp, out string why)
        {
            why = null;
            if (n == null) { why = "No node."; return false; }
            if (_taken.Contains(n.Id)) { why = "Already learned."; return false; }
            var pillar = tree.PillarOf(n.Pillar);
            if (!PillarOpen(pillar))
            {
                why = $"{pillar?.Title ?? n.Pillar} is not open yet.";
                return false;
            }
            foreach (var r in n.Requires)
                if (!_taken.Contains(r))
                {
                    why = $"Needs {tree.NodeOf(r)?.Title ?? r}.";
                    return false;
                }
            if (xp < n.Cost) { why = $"Needs {n.Cost} XP."; return false; }
            return true;
        }

        /// <summary>Take the node. Returns the XP spent, or 0 if it could not be taken -- the caller deducts, so
        /// this never has to know where the pool lives (single-player pool today, a server's answer later).</summary>
        public uint Take(SkillTree tree, SkillNode n, uint xp)
        {
            if (!CanTake(tree, n, xp, out _)) return 0u;
            _taken.Add(n.Id);
            return n.Cost;
        }
    }
}
