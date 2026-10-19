using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>A strip mall's plan (BuildingGenSpec.Strip): one of a few fixed styles drawn off measured real ones (strawberry's
    /// direction for buildings: "a few fixed styles, which have been specifically approved as sensible layouts"), Auto for a
    /// seeded one. Convenience: the unanchored convenience strip of 1985-2005, 60 ft deep, a restaurant and a convenience store
    /// at its ends -- the commonest, 59% of US centres (the research's L6). PostWar: the 1950s local strip, 80 ft deep, a
    /// hardware store at its end (L3). MiniMall: the 1973-90 L on a corner lot, a convenience store in its knee (L5). Taxpayer:
    /// the 1920s main-street row, its shops straight onto the sidewalk with no canopy (L1).</summary>
    public enum StripStyle { Auto, Convenience, PostWar, MiniMall, Taxpayer }

    /// <summary>
    /// STRIP MALLS (strawberry, 2026-10-19, on the strip research: "and yes keep working on this"). One storey, a row of
    /// TENANTS side by side, each its own shop with nothing between them but a demising wall -- no door from one into the next
    /// (GenRoom.Unit is the tenant, and Connect's MayJoin keeps every door inside one, as it keeps a flat's). What holds across
    /// the real ones, and so here: bays about 20 ft (four cells) wide, 60 to 80 ft deep; the sales floor at the front, the
    /// stock room and one toilet at the back; a back door straight onto the alley, no shared rear corridor; a canopy along
    /// the front over the walk (but on the 1920s row, whose shops front the sidewalk); a flat roof behind a parapet; the two
    /// end units glazed round the corner, and usually a restaurant, a dry cleaner, a drugstore or a convenience store.
    ///
    /// Each plan is a fixed grid of the research's (notes/strip_*: 1 cell = 3 m game = 5 ft real), the alley's row first and the
    /// street's last, every cell a room code and a tenant. A plan is resized only by whole bays -- an inner tenant dropped -- and
    /// may be laid mirror-wise; nothing else about it moves. Its rooms are the grid's: a run of one code in one tenant is one
    /// room (S1..S9 the sales floor, SR stock, BA toilet, HL hall, OF office, WK work room, KT kitchen, DN dining). Each tenant
    /// draws what it sells (a Business, its shop's palette with it) -- a restaurant's slot a diner or a cafe -- and gets a
    /// front door centred in its frontage, a back door out of its rearmost room, and Connect's doors inside it from its sales
    /// floor. Laid out with the alley on the south (z = 0) and the street on the north, as the grid reads; turned half round
    /// when a front on the south is asked for. From the rooms on it is the house pipeline, as a block of flats': the walls
    /// (BuildWalls glazes every tenant's front, StripFacing), the canopy (AddPorches, AddCanopies), the furniture, the
    /// parapets, the half turn.
    /// </summary>
    public static partial class BuildingGen
    {
        /// <summary>One strip plan: its grid of room codes (Rows, two letters a cell, space separated; "." outside, "CW" the
        /// canopy's walk), its tenants (Units: a letter a cell, '.' for none), what each tenant of the research's sells where we
        /// have that shop (Prefer, by letter), the fewest tenants it is cut down to, and whether its shops front the sidewalk
        /// with no canopy (the street then the north side of its last row).</summary>
        sealed class StripLayout
        {
            public StripStyle Style;
            public string Name;
            public string[] Rows;
            public string[] Units;
            public Business[] Prefer;
            public int MinUnits;
            public bool Sidewalk;
        }

        static string[] Tokens(string row) => row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        /// <summary>A tenant's block of rows: each row so many times, the alley's first.</summary>
        static string[] Block(params (int N, string Row)[] runs) => runs.SelectMany(r => Enumerable.Repeat(r.Row, r.N)).ToArray();

        /// <summary>A row of tenants side by side, west to east, each its own block of columns (every block as deep), and in
        /// front of them all the canopy's walk, a row of CW -- or none, for a row on the sidewalk.</summary>
        static StripLayout RowOf(StripStyle style, string name, int minUnits, bool canopy, params (Business Prefer, string[] Block)[] units)
        {
            int n = units[0].Block.Length;
            if (units.Any(u => u.Block.Length != n)) throw new InvalidOperationException($"BuildingGen: the {name}'s tenants are not all {n} rows deep");
            var rows = new List<string>(); var us = new List<string>();
            for (int r = 0; r < n; r++)
            {
                rows.Add(string.Join(" ", units.Select(u => u.Block[r])));
                us.Add(string.Concat(units.Select((u, i) => new string((char)('a' + i), Tokens(u.Block[r]).Length))));
            }
            if (canopy) { int c = us[0].Length; rows.Add(string.Join(" ", Enumerable.Repeat("CW", c))); us.Add(new string('.', c)); }
            return new StripLayout { Style = style, Name = name, Rows = rows.ToArray(), Units = us.ToArray(), Prefer = units.Select(u => u.Prefer).ToArray(), MinUnits = minUnits, Sidewalk = !canopy };
        }

        static StripLayout[] _stripLayouts;
        /// <summary>The plans, built on first use (never in a static initialiser: a slip in one would take every building down
        /// with it).</summary>
        static StripLayout[] StripLayouts => _stripLayouts ??= MakeStripLayouts();

        static StripLayout[] MakeStripLayouts() => new[]
        {
            // L6, the unanchored convenience strip (1985-2005): 60 ft deep, 20-30 ft bays, eight tenants -- pizza and subs at
            // the west end (its kitchen behind a short hall to two toilets), a dry cleaner, a nail salon, tax prep (an office
            // behind), a video rental, check cashing (an office behind), a liquor store, a convenience store at the east end.
            // Cut to as few as four, its two ends always kept. Bent from the research, within the room caps and so that every
            // room is reached through rooms you walk through (Connect): the restaurant's dining room is six rows, not seven --
            // the seventh is a hall along its back to the toilets and the kitchen, where the toilets' hall was two cells; the
            // two 30-ft shops (S5, S8) have a work room across their back three rows -- the c-store's stock and cooler, the
            // video shop's back room -- where the sales floor ran to the rear hall (6 x 9 cells is past a shop floor's cap of
            // 40); and tax prep's and check cashing's back office is two columns, a hall the other two down to the rear, where
            // it was the bay's width (an office is a dead end: across the bay it walled the toilet off from the shop).
            // Its tenants as strawberry has them (2026-10-19): the pizzeria a Pizza, the dry cleaner a Laundromat, the nail salon
            // Clothes, tax prep an Accountant, the video rental a Video, check cashing an Office, the liquor store a Liquor, the
            // convenience store a Grocer -- each the slot's first draw (StripKeepsTenant), any other shop or office the rest.
            RowOf(StripStyle.Convenience, "convenience strip", 4, true,
                (Business.Pizza, Block((3, "SR SR KT KT KT KT"), (2, "BA BA HL HL BA BA"), (1, "HL HL HL HL HL HL"), (6, "DN DN DN DN DN DN"))),
                (Business.Laundromat, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (9, "S2 S2 S2 S2"))),
                (Business.Clothes, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (9, "S3 S3 S3 S3"))),
                (Business.Accountant, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (3, "OF OF HL HL"), (6, "S4 S4 S4 S4"))),
                (Business.Video, Block((2, "SR SR HL HL BA BA"), (1, "SR SR HL HL HL HL"), (3, "WK WK WK WK WK WK"), (6, "S5 S5 S5 S5 S5 S5"))),
                (Business.Office, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (3, "OF OF HL HL"), (6, "S6 S6 S6 S6"))),
                (Business.Liquor, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (9, "S7 S7 S7 S7"))),
                (Business.Grocer, Block((2, "SR SR HL HL BA BA"), (1, "SR SR HL HL HL HL"), (3, "WK WK WK WK WK WK"), (6, "S8 S8 S8 S8 S8 S8")))),

            // L3, the post-war local strip (1950-58): 80 ft deep, a rear alley, a canopy; a dry cleaner at the south end (its
            // pressing plant behind), insurance, appliance repair, a beauty salon, a shoe store with its repair bench, then the
            // hardware store. Cut to as few as five. Bent from the research: the hardware ANCHOR (S6 + S7, 13 columns, 156 cells of
            // sales floor -- four times a shop floor's cap) is two bays of one tenant, its main floor and its paint and plumbing
            // side each a shop floor within the cap, open to each other (Strip: a wide doorway), a work room behind the side's for
            // receiving -- and its garden side, the last five columns, a tenant of its own, the north end cap, laid as the south
            // one is; and insurance's and the beauty salon's back office is two columns with a hall beside it (as L6's).
            // (the dry cleaner a Laundromat, insurance and real estate an Office, the rest as the research's)
            RowOf(StripStyle.PostWar, "post-war strip", 5, true,
                (Business.Laundromat, Block((2, "SR SR SR BA BA"), (1, "HL HL HL HL HL"), (5, "WK WK WK WK WK"), (8, "S1 S1 S1 S1 S1"))),
                (Business.Office, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (3, "OF OF HL HL"), (10, "S2 S2 S2 S2"))),
                (Business.Hardware, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (4, "WK WK WK WK"), (9, "S3 S3 S3 S3"))),
                (Business.Clothes, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (3, "OF OF HL HL"), (10, "S4 S4 S4 S4"))),
                (Business.Clothes, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (4, "WK WK WK WK"), (9, "S5 S5 S5 S5"))),
                (Business.Hardware, Block((2, "SR SR BA BA SR SR BA BA"), (1, "SR SR HL HL SR SR HL HL"), (3, "OF OF HL HL WK WK WK WK"),
                                          (1, "S6 S6 S6 S6 WK WK WK WK"), (9, "S6 S6 S6 S6 S7 S7 S7 S7"))),
                (Business.Botanist, Block((2, "BA BA SR SR SR"), (1, "HL HL HL HL HL"), (5, "WK WK WK WK WK"), (8, "S8 S8 S8 S8 S8")))),

            // L5, the mini-mall (1973-90): an L on an ex-gas-station corner, its N leg eight rows deep -- Chinese takeout at the
            // west end, a donut shop, a nail salon, a dry cleaner, a video rental, and the convenience store in the knee -- and its
            // E leg running on toward the main street, a laundromat (its service chase behind the dryers) and a beauty salon on
            // the corner; the canopy round the inside of the L, the lot in it. As the research drew it (every room within its
            // cap and reached through rooms you walk through); cut to as few as five, the N leg's middle shops going first.
            new StripLayout
            {
                Style = StripStyle.MiniMall, Name = "mini-mall", MinUnits = 5,
                // (the takeout a Diner, the dry cleaner and the laundromat Laundromats, the video rental a Video)
                Prefer = new[] { Business.Diner, Business.Cafe, Business.Clothes, Business.Laundromat, Business.Video, Business.Grocer, Business.Laundromat, Business.Clothes },
                Rows = Block(
                    (2, "SR SR KT KT KT KT SR SR BA BA SR SR BA BA SR SR BA BA SR SR BA BA SR SR SR BA BA SR SR SR"),
                    (1, "BA BA KT KT KT KT S2 S2 S2 S2 S3 S3 S3 S3 S4 S4 S4 S4 S5 S5 S5 S5 HL HL HL HL HL HL HL HL"),
                    (1, "BA BA HL HL HL HL S2 S2 S2 S2 S3 S3 S3 S3 S4 S4 S4 S4 S5 S5 S5 S5 S6 S6 S6 S6 S6 S6 S6 S6"),
                    (4, "S1 S1 S1 S1 S1 S1 S2 S2 S2 S2 S3 S3 S3 S3 S4 S4 S4 S4 S5 S5 S5 S5 S6 S6 S6 S6 S6 S6 S6 S6"),
                    (1, "CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW CW S7 S7 S7 S7 SR SR"),
                    (1, ".  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  CW S7 S7 S7 S7 SR SR"),
                    (4, ".  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  CW S7 S7 S7 S7 HL HL"),
                    (2, ".  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  CW S7 S7 S7 S7 BA BA"),
                    (3, ".  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  CW S8 S8 S8 S8 SR SR"),
                    (2, ".  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  CW S8 S8 S8 S8 BA BA")),
                Units = Block(
                    (8, "aaaaaabbbbccccddddeeeeffffffff"),
                    (8, "........................gggggg"),
                    (5, "........................hhhhhh")),
            },

            // L1, the taxpayer row (1920s main street): no canopy, its shops straight onto the sidewalk; a corner grocer at the
            // west end (its back stock and meat block behind), a drug store (the prescription room behind), a hardware store, a
            // shoe store. Cut to as few as three. Bent from the research as L3's: the drug store's and the shoe store's back office
            // is two columns with a hall beside it.
            RowOf(StripStyle.Taxpayer, "taxpayer row", 3, false,
                (Business.Grocer, Block((2, "SR SR SR BA BA"), (1, "HL HL HL HL HL"), (5, "WK WK WK WK WK"), (8, "S1 S1 S1 S1 S1"))),
                (Business.Grocer, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (3, "OF OF HL HL"), (10, "S2 S2 S2 S2"))),
                (Business.Hardware, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (4, "WK WK WK WK"), (9, "S3 S3 S3 S3"))),
                (Business.Clothes, Block((2, "SR SR BA BA"), (1, "SR SR HL HL"), (3, "OF OF HL HL"), (10, "S4 S4 S4 S4")))),
        };

        /// <summary>What a strip's tenant may be that is not a restaurant: every shop but those that need a kitchen, the strip's own
        /// (a laundromat, a video store, a liquor store) and the cheap offices.</summary>
        static readonly Business[] StripShops = { Business.Clothes, Business.Grocer, Business.Guns, Business.Hardware, Business.Botanist, Business.Cafe,
                                                  Business.Laundromat, Business.Video, Business.Liquor, Business.Lawyer, Business.Accountant, Business.Office };
        /// <summary>What a restaurant's slot -- a tenant with a kitchen or a dining room -- may be: the diner family's.</summary>
        static readonly Business[] StripFood = { Business.Diner, Business.Pizza, Business.Cafe };
        /// <summary>Percent of tenants that sell what the research's tenant in that slot did, where we have the shop for it -- in a
        /// working town (Working); in a poor or an affluent one, StripLeanKeeps where the town leans to it, StripOffKeeps where not.</summary>
        const int StripKeepsTenant = 55, StripLeanKeeps = 70, StripOffKeeps = 30;
        /// <summary>How much likelier a tenant the town leans to is drawn than any other.</summary>
        const int StripLean = 3;
        /// <summary>What a town of this wealth leans to (the coordinator, 2026-10-19, with the town generator's owner): a poor
        /// town's strips to liquor, laundromats, pizza, video and cheap offices; an affluent town's to lawyers and accountants,
        /// cafes, clothes and botanists; a working town's to nothing -- the plain mix.</summary>
        static bool Leans(TownWealth w, Business b) => w switch
        {
            TownWealth.Poor => b is Business.Liquor or Business.Laundromat or Business.Pizza or Business.Video or Business.Office,
            TownWealth.Affluent => b is Business.Lawyer or Business.Accountant or Business.Cafe or Business.Clothes or Business.Botanist,
            _ => false,
        };

        static BuildingGenResult Strip(BuildingGenSpec spec, BuildingGenResult res, Rng rng, DoorPlanner connect)
        {
            res.Roof = RoofKind.Flat; res.RoofPitch = 0f; res.Floors = 1;
            // ---- the plans to choose among: the one asked for, else every one -- the convenience strip about half the time
            // first (the commonest by far), the others after it in a seeded order -- each cut to any number of its tenants
            // (whole bays only: an inner tenant whose columns are all its own, the bays dropped in a seeded order) and faced
            // any way it was asked to: its street on the side the town's parking is (FrontSide), south when none was asked,
            // as every building's front is (the plan is laid out with its street on the north and turned half round for the
            // south; east and west, laid out turned a quarter -- not the L of the mini-mall, whose wing must be north of its
            // full-length part: BuildingGenResult.Wing)
            var order = new List<StripStyle>();
            if (spec.Strip != StripStyle.Auto) order.Add(spec.Strip);
            else
            {
                int roll = rng.Int(100);
                order.Add(roll < 55 ? StripStyle.Convenience : roll < 70 ? StripStyle.PostWar : roll < 85 ? StripStyle.MiniMall : StripStyle.Taxpayer);
                foreach (var (st, _) in rng.Keyed(StripLayouts.Select(l => l.Style).Where(st => st != order[0])).OrderBy(x => x.Key)) order.Add(st);
            }
            var asked = spec.FrontSide == Sides.None ? Sides.South : spec.FrontSide;
            var fronts = new[] { Sides.South, Sides.North, Sides.East, Sides.West }.Where(sd => (asked & sd) != 0).ToList();
            var cuts = new List<StripCut>();
            for (int o = 0; o < order.Count; o++)
            {
                var lay = StripLayouts.FirstOrDefault(l => l.Style == order[o]);
                if (lay == null) { res.Notes.Add($"no {order[o]} strip plan: a convenience strip"); lay = StripLayouts[0]; }
                var c = StripCut.Of(lay, rng);
                var faces = fronts.Where(fs => !(c.LShaped && fs is Sides.East or Sides.West)).ToList();
                // (an L asked for by name faces the way it can; a seeded one is passed over for a plan that faces the way asked)
                if (faces.Count == 0) { if (spec.Strip == StripStyle.Auto) continue; faces.Add(Sides.South); }
                for (int n = c.Least; n <= c.Letters.Count; n++)
                    foreach (var fs in faces) cuts.Add(c.With(o, n, fs));
            }
            bool fit = spec.FitPlot && spec.Width > 0 && spec.Depth > 0;
            StripCut cut;
            if (fit)
            {
                // a town's plot (FitPlot): Width x Depth is a cap -- the plan asked for (or the seed's) with as many tenants as
                // fit, else the next plan; the closest, said so, when none fits
                int Over(StripCut q) => Math.Max(0, q.CX - spec.Width) + Math.Max(0, q.CZ - spec.Depth);
                cut = cuts.OrderBy(Over).ThenBy(q => q.Order).ThenByDescending(q => q.Units).ThenBy(q => fronts.IndexOf(q.Front)).First();
                var gave = new List<string>();
                if (cut.Order > 0) gave.Add($"a {cut.Lay.Name}, not a {StripLayouts.First(l => l.Style == order[0]).Name}");
                if (cut.Units < cut.Letters.Count) gave.Add($"{cut.Units} tenants, not {cut.Letters.Count}");
                if (fronts.Count > 0 && cut.Front != fronts[0]) gave.Add($"its front {cut.Front}, not {fronts[0]}");
                res.Notes.Add(Over(cut) > 0 ? $"could not fit the {spec.Width}x{spec.Depth} plot: {cut.CX}x{cut.CZ} is the closest of {cuts.Count} plans"
                                            : $"fitted the {spec.Width}x{spec.Depth} plot as {cut.CX}x{cut.CZ}" + (gave.Count > 0 ? ": " + string.Join(", ", gave) : ""));
            }
            else
            {
                // the seed's plan, its front the first asked; as long as the length asked for along the street holds (whole bays,
                // the nearest), else a seeded number of tenants
                int first = cuts.Min(q => q.Order);
                var mine = cuts.Where(q => q.Order == first && q.Front == cuts.First(f => f.Order == first).Front).ToList();
                bool ns = mine[0].Front is Sides.North or Sides.South;
                int want = ns ? spec.Width : spec.Depth;
                if (want > 0)
                {
                    cut = mine.OrderBy(q => Math.Abs(q.Len - want)).ThenByDescending(q => q.Units).First();
                    if (cut.Len != want) res.Notes.Add($"{cut.Len} cells long, not the {want} asked for: a {cut.Lay.Name} is {string.Join(", ", mine.Select(q => q.Len).Distinct())} long");
                }
                else cut = mine[rng.Int(mine.Count)];
                int deep = ns ? spec.Depth : spec.Width;
                if (deep > 0 && deep != cut.Depth) res.Notes.Add($"a {cut.Lay.Name} is {cut.Depth} cells deep, as its plan is: not the {deep} asked for");
            }
            {
                var lay0 = cut.Lay;
                if (fronts.Count > 0 && !fronts.Contains(cut.Front)) res.Notes.Add($"a {lay0.Name} fronts north or south only: not {spec.FrontSide}");
                if (spec.MinFloors > 1) res.Notes.Add("a strip is one storey");
                if (spec.Basement == BasementChoice.Require) res.Notes.Add("a strip is a slab on grade: no basement");
                if (spec.BackSide != Sides.None && (spec.BackSide & Turned(cut.Front)) == 0)
                    res.Notes.Add($"a strip's back doors are on its alley, opposite its front: not {spec.BackSide}");
            }
            var layout = cut.Lay;
            var front = cut.Front;
            // (laid out with the street on the north, or east or west; a south front is the north's turned half round, last)
            var laid = front == Sides.South ? Sides.North : front;
            bool canopy = !layout.Sidewalk && spec.Porches is PorchChoice.Auto or PorchChoice.Front or PorchChoice.Both;
            if (!layout.Sidewalk && !canopy) res.Notes.Add("no canopy over its walk: no porch was to be had in front of it");
            // mirror-wise now and then: its west end at the east
            bool mirror = rng.Chance(50);

            // ---- the plan's cells: each of the grid's kept, turned to its front -- the street on the north (x along the row,
            // z from the alley), the east (x from the alley, z along it the other way) or the west -- and moved so the building's
            // own cells start at (0, 0); the canopy's walk may stand outside that
            var cols = Enumerable.Range(0, cut.C0).Where(c => !cut.Drop.Contains(cut.FirstOwner(c))).ToList();
            if (mirror) cols.Reverse();
            int R = cut.R, Lc = cols.Count;
            var cell = new Dictionary<(int X, int Z), (string Code, char Who)>();
            foreach (var (c, i) in cols.Select((c, i) => (c, i)))
                for (int r = 0; r < R; r++)
                {
                    string t = cut.Tok[r][c];
                    if (t == ".") continue;
                    var at = laid switch { Sides.East => (r, Lc - 1 - i), Sides.West => (R - 1 - r, i), _ => (i, r) };
                    cell[at] = (t, cut.Who(r, c));
                }
            int bx0 = cell.Where(kv => StripBuilt(kv.Value.Code)).Min(kv => kv.Key.X), bz0 = cell.Where(kv => StripBuilt(kv.Value.Code)).Min(kv => kv.Key.Z);
            cell = cell.ToDictionary(kv => (kv.Key.X - bx0, kv.Key.Z - bz0), kv => kv.Value);
            int W = cell.Where(kv => StripBuilt(kv.Value.Code)).Max(kv => kv.Key.X) + 1, D = cell.Where(kv => StripBuilt(kv.Value.Code)).Max(kv => kv.Key.Z) + 1;
            string Code(int x, int z) => cell.TryGetValue((x, z), out var v) ? v.Code : null;
            char Tenant(int x, int z) => cell.TryGetValue((x, z), out var v) && StripBuilt(v.Code) ? v.Who : '.';

            // ---- the rooms: each run of one code in one tenant, its cells as rectangles (a row's run carried on down while it
            // keeps its ends), the biggest its main one; tenants numbered west to east
            var unitOf = Enumerable.Range(0, W).SelectMany(x => Enumerable.Range(0, D).Select(z => Tenant(x, z))).Where(ch => ch != '.').Distinct()
                                   .Select((ch, i) => (ch, i)).ToDictionary(p => p.ch, p => p.i);
            int units = unitOf.Count;
            var owner = new GenRoom[W, D];
            var rooms = new List<GenRoom>();
            for (int z = 0; z < D; z++)
                for (int x = 0; x < W; x++)
                {
                    if (!StripBuilt(Code(x, z)) || owner[x, z] != null) continue;
                    string code = Code(x, z); char who = Tenant(x, z);
                    var cells = new HashSet<(int X, int Z)>();
                    var q = new Queue<(int X, int Z)>(); q.Enqueue((x, z)); cells.Add((x, z));
                    while (q.Count > 0)
                    {
                        var (cx, cz) = q.Dequeue();
                        foreach (var (nx, nz) in new[] { (cx + 1, cz), (cx - 1, cz), (cx, cz + 1), (cx, cz - 1) })
                            if (nz < D && Code(nx, nz) == code && Tenant(nx, nz) == who && cells.Add((nx, nz))) q.Enqueue((nx, nz));
                    }
                    var rects = Rectangles(cells).OrderByDescending(r => (r.X1 - r.X0) * (r.Z1 - r.Z0)).ThenBy(r => r.Z0).ThenBy(r => r.X0).ToList();
                    var kind = StripRoomKind(code, cells.Count);
                    var room = new GenRoom { Kind = kind, Floor = 0, Unit = unitOf[who], X0 = rects[0].X0, Z0 = rects[0].Z0, X1 = rects[0].X1, Z1 = rects[0].Z1 };
                    room.More.AddRange(rects.Skip(1));
                    foreach (var (cx, cz) in cells) owner[cx, cz] = room;
                    rooms.Add(room);
                }
            // (each within its kind's cap -- the plans are drawn so: a note, should one not be)
            foreach (var r in rooms.Where(r => r.Cells > CapCells(r.Kind, BuildingKind.Strip)))
                res.Notes.Add($"{r.Kind} of tenant {r.Unit} is {r.Cells} cells, past its kind's {CapCells(r.Kind, BuildingKind.Strip)}");

            // ---- the outline: a rectangle, or an L whose full-length part is the alley's rows and whose wing runs on toward
            // the street (BuildingGenResult.Wing)
            bool Full(int z) => Enumerable.Range(0, W).All(x => owner[x, z] != null);
            if (Enumerable.Range(0, D).All(Full)) res.Shape = FootprintShape.Rect;
            else
            {
                int zj = Enumerable.Range(0, D).First(z => !Full(z));
                var wing = Enumerable.Range(zj, D - zj).SelectMany(z => Enumerable.Range(0, W).Where(x => owner[x, z] != null).Select(x => (x, z))).ToList();
                int wx0 = wing.Min(p => p.x), wx1 = wing.Max(p => p.x) + 1;
                bool ok = zj > 0 && Enumerable.Range(0, zj).All(Full)
                          && Enumerable.Range(zj, D - zj).All(z => Enumerable.Range(0, W).All(x => (owner[x, z] != null) == (x >= wx0 && x < wx1)));
                if (!ok) throw new InvalidOperationException($"BuildingGen: the {layout.Name}'s outline is neither a rectangle nor an L");
                res.Shape = FootprintShape.L;
                res.Wing = (wx0, zj, wx1, D);
            }

            var g = new Grid(W, D, 0, W);
            foreach (var r in rooms) g.Paint(r);
            var runs = Runs(g);

            // ---- what each tenant sells: an anchor -- a tenant of more than one sales floor -- what its plan has it (the post-war
            // strip's hardware store), first; then a restaurant's slot (a kitchen or a dining room) a diner, a pizzeria or a cafe,
            // any other the research's tenant's business now and then and else any shop or office that needs no kitchen -- never
            // the same as a neighbour's -- and the palette of its shop it wears; the strip's walls wear one of its tenants'
            var sells = new Business[units];
            Business PreferOf(int u) { char who = unitOf.First(p => p.Value == u).Key; return who - 'a' < layout.Prefer.Length ? layout.Prefer[who - 'a'] : Business.None; }
            bool Anchor(int u) => rooms.Count(r => r.Unit == u && r.Kind is RoomKind.Shop or RoomKind.DiningRoom) > 1 && PreferOf(u) != Business.None;
            for (int u = 0; u < units; u++) if (Anchor(u)) sells[u] = PreferOf(u);
            // (by the town's wealth, seeded: the slot's own tenant kept more often where the town leans to it, less where not, and
            // the rest drawn with what it leans to StripLean times as likely; a working town's the plain mix)
            var wealth = spec.Wealth;
            int Keeps(Business b) => wealth == TownWealth.Working ? StripKeepsTenant : Leans(wealth, b) ? StripLeanKeeps : StripOffKeeps;
            Business Draw(Business[] pool)
            {
                if (wealth == TownWealth.Working) return pool[rng.Int(pool.Length)];
                int total = pool.Sum(b => Leans(wealth, b) ? StripLean : 1), at = rng.Int(total);
                foreach (var b in pool) { at -= Leans(wealth, b) ? StripLean : 1; if (at < 0) return b; }
                return pool[^1];
            }
            for (int u = 0; u < units; u++)
            {
                if (Anchor(u)) continue;
                var prefer = PreferOf(u);
                bool kitchen = rooms.Any(r => r.Unit == u && r.Kind is RoomKind.Kitchen or RoomKind.DiningRoom);
                for (int t = 0; t < 6; t++)
                {
                    sells[u] = kitchen ? (StripFood.Contains(prefer) && rng.Chance(Keeps(prefer)) ? prefer : Draw(StripFood))
                             : prefer != Business.None && StripShops.Contains(prefer) && rng.Chance(Keeps(prefer)) ? prefer : Draw(StripShops);
                    if ((u == 0 || sells[u] != sells[u - 1]) && (u + 1 == units || sells[u] != sells[u + 1])) break;
                }
            }
            foreach (var b in sells)
            {
                res.UnitBusiness.Add(b);
                var pals = ShopPalettes(b);
                res.UnitPalette.Add(pals[rng.Int(pals.Length)]);
            }
            res.Business = Business.None;
            res.Palette = res.UnitPalette[rng.Int(res.UnitPalette.Count)];

            // ---- each tenant's sales floor and its street door: its run of outside wall onto the canopy's walk (or the
            // sidewalk), the door centred in it -- the middle of the bay, the shop windows either side (BuildWalls)
            bool Street(Run r)
            {
                int n = 0, onto = 0;
                for (int s = r.S0; s < r.S1; s++, n++)
                {
                    var sd = g.Out(r.AlongX, r.Line, s);
                    int ox = r.AlongX ? s : sd == Sides.East ? r.Line : r.Line - 1, oz = r.AlongX ? (sd == Sides.North ? r.Line : r.Line - 1) : s;
                    // (the canopy's walk; or, where its shops front the sidewalk, outside on its street side)
                    if (Code(ox, oz) is string c ? c == "CW" : layout.Sidewalk && sd == laid) onto++;
                }
                return onto * 2 > n;
            }
            var doors = new List<GenDoor>();
            var outsideDoors = new List<GenDoor>();
            // (a storefront's door is plate glass -- strawberry: "plate glass doors (that fit/match the current style)", its
            // pane the windows' breakable glass -- bronze-framed on the 1920s taxpayer row, the era's storefront metal,
            // aluminium on the post-war strips; a back door stays the plain one)
            string glassDoor = layout.Style == StripStyle.Taxpayer ? StorefrontDoorBronze : StorefrontDoor;
            var sales = new GenRoom[units];
            var frontSide = new Sides[units];
            for (int u = 0; u < units; u++)
            {
                // (each of its sales floors on the street its own door -- an anchor's two, "in + out" -- the biggest frontage's
                // first, the floor its doors inside are traced from)
                var streetRuns = rooms.Where(r => r.Unit == u && r.Kind is RoomKind.Shop or RoomKind.DiningRoom)
                                      .Select(r => runs.Where(q => q.B == null && q.A == r && Street(q)).OrderByDescending(q => q.S1 - q.S0).FirstOrDefault())
                                      .Where(q => q.A != null).OrderByDescending(q => q.S1 - q.S0).ToList();
                if (streetRuns.Count == 0) throw new InvalidOperationException($"BuildingGen: tenant {u} of the {layout.Name} has no sales floor on the street");
                foreach (var sr in streetRuns)
                {
                    var (d, _) = OutDoor(g, sr, doors, DoorRole.Front);
                    if (d == null) { res.Notes.Add($"tenant {u}'s {sr.A.Kind} has no room for a street door"); continue; }
                    if (sales[u] == null) { sales[u] = sr.A; frontSide[u] = g.Out(sr.AlongX, sr.Line, sr.S0); }
                    d.DoorProp = glassDoor;
                    doors.Add(d); outsideDoors.Add(d);
                }
                if (sales[u] == null) throw new InvalidOperationException($"BuildingGen: tenant {u} of the {layout.Name} has no room for a street door");
            }
            // ---- an end cap's side door: the end unit's sales floor, glazed round the corner (BuildWalls), has a door on its end
            // wall too -- the research's "end caps also a side door" (L6), the corner grocer's "side/corner door" (L1), the
            // dry cleaner's "also on its S side wall" (L3) -- in its longest run of end wall, where it clears the doors so far;
            // never on a side asked to have no windows (a party wall in a row: the same frame BuildWalls reads, laid)
            var blind = front == Sides.South ? Turned(spec.NoWindows) : spec.NoWindows;
            for (int u = 0; u < units; u++)
            {
                var alley = Turned(frontSide[u]);
                var end = runs.Where(q => q.B == null && q.A.Unit == u && q.A.Kind is RoomKind.Shop or RoomKind.DiningRoom && !Street(q)
                                          && g.Out(q.AlongX, q.Line, q.S0) is var sd && sd != frontSide[u] && sd != alley && (sd & blind) == 0)
                              .OrderByDescending(q => q.S1 - q.S0).FirstOrDefault();
                if (end.A == null) continue;
                var (d, crowded) = OutDoor(g, end, doors, DoorRole.Front);
                if (d == null || crowded) continue;
                d.DoorProp = glassDoor;
                doors.Add(d); outsideDoors.Add(d);
            }
            // ---- and its back door: onto the alley (the side its street door's is not), out of its rear vestibule where it has
            // one, else its stock room, else whatever reaches the alley (BackDoorOrder) -- clear of every corner a door is by
            var backOrder = BackDoorOrder(BuildingKind.Strip);
            for (int u = 0; u < units; u++)
            {
                var alley = Turned(frontSide[u]);
                GenDoor back = null, crowdedBack = null;
                foreach (var q in runs.Where(q => q.B == null && q.A.Unit == u && g.Out(q.AlongX, q.Line, q.S0) == alley && !Street(q))
                                      .OrderBy(q => Array.IndexOf(backOrder, q.A.Kind) is int i && i >= 0 ? i : backOrder.Length).ThenByDescending(q => q.S1 - q.S0))
                {
                    var (d, crowded) = OutDoor(g, q, doors, DoorRole.Back);
                    if (d == null) continue;
                    if (!crowded) { back = d; break; }
                    crowdedBack ??= d;
                }
                back ??= crowdedBack;
                if (back == null) { res.Notes.Add($"tenant {u} ({res.UnitBusiness[u]}) has no back door: no room of it reaches the alley"); continue; }
                doors.Add(back); outsideDoors.Add(back);
            }
            // ---- the doors inside each tenant: Connect's, from its sales floor -- a tree through the rooms you walk through, every
            // other room a dead end off one -- tenant by tenant, every door so far in the list so a new one keeps off their corners
            for (int u = 0; u < units; u++)
            {
                var rs = rooms.Where(r => r.Unit == u).ToList();
                var before = doors.ToList();
                bool ok = false;
                for (int t = 0; t < 8 && !ok; t++)
                {
                    doors.Clear(); doors.AddRange(before);
                    bool turned = false;
                    ok = connect(g, rs, 0, sales[u], doors, (0, 0, 0, 0), Sides.None, ref turned);
                }
                if (!ok) throw new InvalidOperationException($"BuildingGen: tenant {u} of the {layout.Name} did not connect for seed {spec.Seed}: {string.Join(", ", rs)}");
                // (Connect's last pass takes a door out of a corner another crowds and lays it again elsewhere -- but a front or a
                // back door it leaves out: they were laid first and clear, so they go back where they were)
                foreach (var d in outsideDoors) if (!doors.Contains(d)) doors.Add(d);
            }
            // (an anchor's sales floors -- one store's departments -- open into each other, as wide as their wall lets them:
            // the research's "S6<->S7 wide opening")
            foreach (var d in doors.Where(d => d.B != null && d.A.Kind == RoomKind.Shop && d.B.Kind == RoomKind.Shop && d.A.Unit == d.B.Unit))
                d.Open = d.Wide = true;

            // ---- the canopy: each run of the walk's cells along one outside wall (a corner cell, against two, the first's)
            if (canopy)
                foreach (var run in CanopyRuns(cell.Where(kv => kv.Value.Code == "CW").Select(kv => kv.Key), (x, z) => x >= 0 && x < W && z >= 0 && z < D && owner[x, z] != null))
                    res.StripCanopy.Add(run);

            res.CellsX = W; res.CellsZ = D;
            res.Footprints.Add((0, W));
            res.Bay = (0, 0, 0, 0);
            res.Plan = $"strip, {layout.Name}{(mirror ? " mirrored" : "")} fronting {front}: {string.Join(", ", res.UnitBusiness)}";
            // (what a town needs to stand it: its street side -- the shop fronts, their doors, the canopy -- and its alley side,
            // the back doors', as it will face once turned)
            res.StreetSide = front; res.AlleySide = Turned(front);
            res.Layout.AddRange(rooms);
            res.Doors.AddRange(doors);
            foreach (var r in rooms) res.Rooms.Add(new RoomDesignation(r.Kind, (r.X0 + r.X1) * 0.5f * Cell, (r.Z0 + r.Z1) * 0.5f * Cell, 0));
            // ---- the taxpayer row's cellar (the research: "expect a cellar under the row (hatch/stair per unit, often one
            // shared cellar)"): the shared one, under the whole row, reached by a hatch from outside -- a flight down from one
            // tenant would open it to that shop and not the next -- on the basement's own streams (PlanBasement), unless the
            // plot forbids one; a store or bare, as a business's is; its well never on a side kept blind (the side doors'
            // frame). A well is 9.6 m along its wall (its 3 m landing, a 6 m flight, its two walls) and keeps off each door's
            // porch, so it never fits between back doors a bay (12 m) apart: a row with neighbours at both ends has no wall
            // for one and no cellar (said so). Every other strip is a slab on grade.
            if (layout.Style == StripStyle.Taxpayer && spec.Basement != BasementChoice.Forbid)
            {
                var cellar = spec.Copy();
                cellar.Basement = BasementChoice.Require; cellar.BasementAccess = BasementAccess.Hatch;
                PlanBasement(res, cellar, new List<Grid> { g }, hatchOnly: true, avoid: blind);
            }
            if (spec.PlanOnly) return res;

            // ---- the street on the south: the building laid out with it on the north, turned half round, last
            bool turn = front == Sides.South;
            res.NoWindowSides = turn ? Turned(spec.NoWindows) : spec.NoWindows;
            // walls, the canopy, furniture, the taxpayer row's cellar -- no chimney -- then the parapets, as every flat roof's
            BuildOut(res, new List<Grid> { g }, 1, 0, spec, spec.Porches, chimney: false);
            SolveCorners(res.Walls, RoofShapes.AddParapets(res.Walls).Contains);
            SettleShared(res);
            RailStairs(res);
            GuardStairs(res);
            if (turn) TurnHalf(res);
            return res;
        }

        /// <summary>A plan's grid cell that is the building's (not outside, not the canopy's walk).</summary>
        static bool StripBuilt(string t) => t != null && t != "." && t != "CW";

        /// <summary>One way of cutting a strip plan: which of its bays are dropped, so how many tenants it has, how long it is along
        /// its street and how deep, and -- faced one way -- its footprint in cells along X and Z.</summary>
        sealed class StripCut
        {
            public StripLayout Lay;
            public string[][] Tok;
            public int R, C0, Least, Order, Units, Len, Depth, CX, CZ;
            public List<char> Letters, Pool;
            public HashSet<char> Drop = new HashSet<char>();
            public Sides Front;
            public bool LShaped;
            public char Who(int r, int c) => StripBuilt(Tok[r][c]) ? Lay.Units[r][c] : '.';
            public char FirstOwner(int c) { for (int r = 0; r < R; r++) if (Who(r, c) != '.') return Who(r, c); return '.'; }

            /// <summary>The plan read: its tenants, which are bays (a tenant of one sales floor, its own whole columns, not at an
            /// end: an anchor of two is kept), the fewest it is cut to, and the seeded order its bays go in.</summary>
            public static StripCut Of(StripLayout lay, Rng rng)
            {
                var c = new StripCut { Lay = lay, Tok = lay.Rows.Select(Tokens).ToArray() };
                c.R = c.Tok.Length; c.C0 = c.Tok[0].Length;
                if (c.Tok.Any(t => t.Length != c.C0) || lay.Units.Length != c.R || lay.Units.Any(u => u.Length != c.C0))
                    throw new InvalidOperationException($"BuildingGen: the {lay.Name}'s grid is ragged");
                int R = c.R, C0 = c.C0;
                c.Letters = Enumerable.Range(0, C0).SelectMany(x => Enumerable.Range(0, R).Select(r => c.Who(r, x))).Where(ch => ch != '.').Distinct().ToList();
                List<int> ColsOf(char u) => Enumerable.Range(0, C0).Where(x => Enumerable.Range(0, R).Any(r => c.Who(r, x) == u)).ToList();
                static bool Sales(string t) => t == "DN" || (t.Length == 2 && t[0] == 'S' && char.IsDigit(t[1]));
                bool Bay(char u)
                {
                    var cs = ColsOf(u);
                    return cs.Count > 0 && cs[0] > 0 && cs[^1] < C0 - 1 && cs[^1] - cs[0] + 1 == cs.Count
                           && cs.All(x => Enumerable.Range(0, R).All(r => c.Who(r, x) == u || c.Who(r, x) == '.'))
                           && cs.SelectMany(x => Enumerable.Range(0, R).Where(r => c.Who(r, x) == u).Select(r => c.Tok[r][x])).Where(Sales).Distinct().Count() == 1;
                }
                var bays = c.Letters.Where(Bay).ToList();
                c.Least = Math.Clamp(lay.MinUnits, c.Letters.Count - bays.Count, c.Letters.Count);
                c.Pool = rng.Keyed(bays).OrderBy(x => x.Key).Select(x => x.Item).ToList();
                c.Depth = Enumerable.Range(0, R).Count(r => c.Tok[r].Any(StripBuilt));
                // (an L: a row of its building cells short of its full width)
                c.LShaped = Enumerable.Range(0, R).Any(r => c.Tok[r].Any(StripBuilt) && !c.Tok[r].All(StripBuilt));
                return c;
            }

            /// <summary>This cut with `n` tenants -- its first bays in the seeded order dropped -- faced `front`, `order` its plan's
            /// place among those tried.</summary>
            public StripCut With(int order, int n, Sides front)
            {
                var c = (StripCut)MemberwiseClone();
                c.Order = order; c.Units = n; c.Front = front;
                c.Drop = new HashSet<char>(Pool.Take(Letters.Count - n));
                c.Len = Enumerable.Range(0, C0).Count(x => !c.Drop.Contains(c.FirstOwner(x)));
                (c.CX, c.CZ) = front is Sides.North or Sides.South ? (c.Len, Depth) : (Depth, c.Len);
                return c;
            }
        }

        /// <summary>A strip's room kind for a code of its plan's: S1..S9 a shop floor, SR stock, BA a toilet, HL a hall, OF an
        /// office, WK a work room (a store room, where one is small enough to be: a work bench wants room), KT a kitchen, DN a
        /// restaurant's dining room.</summary>
        static RoomKind StripRoomKind(string code, int cells) => code switch
        {
            "SR" => RoomKind.Storage,
            "BA" => RoomKind.Bathroom,
            "HL" => RoomKind.Hall,
            "OF" => RoomKind.Office,
            "WK" => cells <= MaxCells(RoomKind.Storage) ? RoomKind.Storage : RoomKind.Workshop,
            "KT" => RoomKind.Kitchen,
            "DN" => RoomKind.DiningRoom,
            _ when code.Length == 2 && code[0] == 'S' && char.IsDigit(code[1]) => RoomKind.Shop,
            _ => throw new InvalidOperationException($"BuildingGen: no strip room code {code}"),
        };

        /// <summary>A set of cells as rectangles: each row's runs, a run carried on down the rows while it keeps both its ends.</summary>
        static List<(int X0, int Z0, int X1, int Z1)> Rectangles(HashSet<(int X, int Z)> cells)
        {
            var rects = new List<(int X0, int Z0, int X1, int Z1)>();
            var open = new List<(int X0, int X1, int Z0)>();
            int z0 = cells.Min(c => c.Z), z1 = cells.Max(c => c.Z);
            for (int z = z0; z <= z1 + 1; z++)
            {
                var row = new List<(int X0, int X1)>();
                var xs = cells.Where(c => c.Z == z).Select(c => c.X).OrderBy(x => x).ToList();
                for (int i = 0; i < xs.Count;)
                {
                    int j = i;
                    while (j + 1 < xs.Count && xs[j + 1] == xs[j] + 1) j++;
                    row.Add((xs[i], xs[j] + 1));
                    i = j + 1;
                }
                var next = new List<(int X0, int X1, int Z0)>();
                foreach (var o in open)
                    if (row.Contains((o.X0, o.X1))) next.Add(o);
                    else rects.Add((o.X0, o.Z0, o.X1, z));
                foreach (var r in row) if (!open.Any(o => o.X0 == r.X0 && o.X1 == r.X1)) next.Add((r.X0, r.X1, z));
                open = next;
            }
            return rects;
        }

        /// <summary>A door in an outside run, nearest the run's middle -- at a cell's middle or a lattice point, kept off the
        /// walls meeting it as Connect's are -- and off every corner another door is by where it can be (`Crowded` when it could
        /// not). Null when the run is too short for a door at all.</summary>
        static (GenDoor Door, bool Crowded) OutDoor(Grid g, Run r, List<GenDoor> doors, DoorRole role)
        {
            float lo = r.S0 * Cell + g.Meet(r.AlongX, r.Line, r.S0) + HalfDoor, hi = r.S1 * Cell - g.Meet(r.AlongX, r.Line, r.S1) - HalfDoor;
            if (lo > hi) { if (lo - hi > 1e-3f) return (null, false); hi = lo; }
            float mid = (r.S0 + r.S1) * 0.5f * Cell;
            var spots = Enumerable.Range(r.S0, r.S1 - r.S0).Select(c => (c + 0.5f) * Cell)
                                  .Concat(Enumerable.Range(r.S0 + 1, Math.Max(0, r.S1 - r.S0 - 1)).Select(c => c * (float)Cell))
                                  .Select(p => Math.Clamp(p, lo, hi)).Distinct().OrderBy(p => MathF.Abs(p - mid)).ThenBy(p => p).ToList();
            foreach (float p in spots)
                if (!doors.Any(d => d.Floor == 0 && SharesCorner(r.AlongX, r.Line, p, d.AlongX, d.Line, d.At)))
                    return (new GenDoor { Floor = 0, A = r.A, B = null, AlongX = r.AlongX, Line = r.Line, At = p, Role = role }, false);
            return (new GenDoor { Floor = 0, A = r.A, B = null, AlongX = r.AlongX, Line = r.Line, At = spots[0], Role = role }, true);
        }

        /// <summary>The runs of a plan's canopy cells (CW) along the outside walls they stand against: by each wall's line and
        /// the side they stand out on, merged where they run on. A cell against two walls -- an L's inside corner -- goes with
        /// the first it is found against (the wall to its south or north), so each cell is roofed once.</summary>
        static IEnumerable<(bool AlongX, int Line, int S0, int S1, int E)> CanopyRuns(IEnumerable<(int X, int Z)> walk, Func<int, int, bool> built)
        {
            var cells = new List<(bool AlongX, int Line, int S, int E)>();
            foreach (var (x, z) in walk.OrderBy(c => c.Z).ThenBy(c => c.X))
                {
                    if (built(x, z - 1)) cells.Add((true, z, x, 1));          // in front of a north face
                    else if (built(x, z + 1)) cells.Add((true, z + 1, x, -1)); // a south face
                    else if (built(x - 1, z)) cells.Add((false, x, z, 1));     // an east face
                    else if (built(x + 1, z)) cells.Add((false, x + 1, z, -1));// a west face
                }
            foreach (var grp in cells.GroupBy(c => (c.AlongX, c.Line, c.E)))
            {
                var ss = grp.Select(c => c.S).OrderBy(s => s).ToList();
                for (int i = 0; i < ss.Count;)
                {
                    int j = i;
                    while (j + 1 < ss.Count && ss[j + 1] == ss[j] + 1) j++;
                    yield return (grp.Key.AlongX, grp.Key.Line, ss[i], ss[j] + 1, grp.Key.E);
                    i = j + 1;
                }
            }
        }

        /// <summary>Which way a strip's outside run faces for its windows (BuildWalls): its tenant's front -- the side its street
        /// door is in, every tenant its own (an L's legs face two ways) -- the back opposite that, else a side.</summary>
        static Face StripFacing(BuildingGenResult res, Grid g, GenRoom room, Sides sd)
        {
            var own = res.Doors.FirstOrDefault(d => d.Role == DoorRole.Front && d.Floor == room.Floor && d.A.Unit == room.Unit);
            if (own == null) return Face.Side;
            var fs = g.Out(own.AlongX, own.Line, (int)MathF.Floor(own.At / Cell));
            return sd == fs ? Face.Front : sd == Turned(fs) ? Face.Back : Face.Side;
        }

        /// <summary>The storefront doors' leaves (astraclaw's, game/ObjectDoor.PlateGlass.cs): aluminium-framed, and the
        /// bronze-framed one. Each carries its own swinging, breakable pane, so the opening is the plain door archetype.</summary>
        public const string StorefrontDoor = "Door_PlateGlass", StorefrontDoorBronze = "Door_PlateGlass_Bronze";

        /// <summary>Canopy posts stand no further apart than this along the walk: a tenant's frontage is split in an odd number of
        /// spans (so its door, in the middle, is between two posts) no longer than it.</summary>
        public const float CanopySpan = 6.5f;

        /// <summary>A strip's canopy (AddPorches' in its place): over each run of the walk in front of the shops (StripCanopy), a
        /// flat slab one cell deep out from the wall's face and a little past, its top level with the wall head so its
        /// underside is the shop windows' heads (OpeningTop); posts along its outer edge where one tenant's frontage meets the
        /// next and between, the spans odd so every door has a span of its own; footings under its open edges, and the walk under
        /// it -- a concrete deck, as a porch's is a floor -- recorded with the porches so the yard keeps off it. At the
        /// building's outside corners it runs flush with the corner; at an L's knee up to the other wall's face, or to the edge
        /// of the run round the corner, which is the one roofed there.</summary>
        static void AddCanopies(BuildingGenResult res, Grid ground)
        {
            float he = Ext * 0.5f, depth = Cell, ovp = 0.3f, top = Storey, P = PorchPost;
            int concrete = FloorTexels.TryGetValue(res.Palette ?? "", out var ft) ? ft.Concrete : -1;
            var outs = Outline(ground, 0f);
            var runs = res.StripCanopy;
            foreach (var (alongX, line, s0, s1, e) in runs)
            {
                float face = line * Cell + e * he;
                // the cell just past an end of the run, on its outside row: a building's (an L's knee), another run's, or open
                int ext = e > 0 ? line : line - 1;
                bool Bldg(int s) => (alongX ? ground.At(s, ext) : ground.At(ext, s)) != null;
                // (a run square to this one whose outside row or column holds that cell)
                (bool AlongX, int Line, int S0, int S1, int E)? Other(int s)
                {
                    foreach (var o in runs)
                        if (o.AlongX != alongX && (o.E > 0 ? o.Line : o.Line - 1) == s && ext >= o.S0 && ext < o.S1) return o;
                    return null;
                }
                var wall = outs.FirstOrDefault(o => o.AlongX == alongX && o.Line == line && o.A0 <= (s0 + 0.5f) * Cell && (s0 + 0.5f) * Cell <= o.A1);
                // where each end stops, and whether a post and a footing close it
                (float At, bool Open, float Roof) End(int s, int dir)
                {
                    float edge = s * Cell;
                    if (Bldg(dir < 0 ? s - 1 : s)) return (edge - dir * he, false, edge - dir * he);                 // an L's knee
                    if (Other(dir < 0 ? s - 1 : s) is { } o)
                    {
                        float oFace = o.Line * Cell + o.E * he;
                        return (oFace + o.E * depth, false, oFace + o.E * (depth + ovp));                           // the run round the corner
                    }
                    bool corner = wall != null && MathF.Abs((dir < 0 ? wall.A0 : wall.A1) - edge) < 0.01f;
                    return corner ? (edge + dir * he, true, edge + dir * he) : (edge, true, edge);                    // flush with the corner
                }
                var (a0, open0, r0) = End(s0, -1);
                var (a1, open1, r1) = End(s1, 1);
                (float X0, float Z0, float X1, float Z1) Rect(float al0, float al1, float ac0, float ac1)
                {
                    float c0 = MathF.Min(ac0, ac1), c1 = MathF.Max(ac0, ac1), l0 = MathF.Min(al0, al1), l1 = MathF.Max(al0, al1);
                    return alongX ? (l0, c0, l1, c1) : (c0, l0, c1, l1);
                }
                var deck = Rect(a0, a1, face, face + e * depth);
                res.Porches.Add(deck);
                var walk = SlabPlan(deck.X0, deck.X1, deck.Z0, deck.Z1, 0f, SurfaceKind.Floor);
                walk.Texel = concrete;
                res.Walls.Add(walk);
                var roof = Rect(r0, r1, face, face + e * (depth + ovp));
                res.Walls.Add(SlabPlan(roof.X0, roof.X1, roof.Z0, roof.Z1, top, SurfaceKind.Roof));
                // the posts: at each open end, where the tenant inside changes, and between -- an odd number of spans a frontage
                int Unit(int s) => (alongX ? ground.At(s, e > 0 ? line - 1 : line) : ground.At(e > 0 ? line - 1 : line, s))?.Unit ?? -1;
                var stops = new List<float> { MathF.Min(a0, a1) + P * 0.5f };
                for (int s = s0 + 1; s < s1; s++) if (Unit(s) != Unit(s - 1)) stops.Add(s * Cell);
                stops.Add(MathF.Max(a0, a1) - P * 0.5f);
                var posts = new List<float>();
                for (int i = 0; i + 1 < stops.Count; i++)
                {
                    float u = stops[i], v = stops[i + 1];
                    int n = 1;
                    while ((v - u) / n > CanopySpan) n += 2;
                    for (int k = 0; k < n; k++) posts.Add(u + (v - u) * k / n);
                }
                posts.Add(stops[^1]);
                float across = face + e * (depth - P * 0.5f);
                for (int k = 0; k < posts.Count; k++)
                {
                    if ((k == 0 && !open0) || (k == posts.Count - 1 && !open1)) continue;
                    float at = posts[k];
                    var w = alongX ? Wall(at - P * 0.5f, across, at + P * 0.5f, across, 0f, P) : Wall(across, at - P * 0.5f, across, at + P * 0.5f, 0f, P);
                    w.Height = top - Slab;
                    res.Walls.Add(w);
                }
                // footings under its open edges, their tops the walk's underside (as a porch's: AddPorches)
                const float tf = 0.35f;
                float outer = face + e * (depth - tf * 0.5f);
                float lo = MathF.Min(a0, a1), hi = MathF.Max(a0, a1);
                var footings = new List<(float, float, float, float)> { alongX ? (lo, outer, hi, outer) : (outer, lo, outer, hi) };
                if (open0) footings.Add(alongX ? (lo + tf * 0.5f, face - e * he, lo + tf * 0.5f, face + e * depth) : (face - e * he, lo + tf * 0.5f, face + e * depth, lo + tf * 0.5f));
                if (open1) footings.Add(alongX ? (hi - tf * 0.5f, face - e * he, hi - tf * 0.5f, face + e * depth) : (face - e * he, hi - tf * 0.5f, face + e * depth, hi - tf * 0.5f));
                foreach (var (x0, z0, x1, z1) in footings)
                {
                    var f = Wall(x0, z0, x1, z1, -WallOpenings.FoundationDepth, tf);
                    f.Height = WallOpenings.FoundationDepth - Slab; f.Kind = SurfaceKind.Foundation;
                    res.Walls.Add(f);
                }
            }
        }
    }
}
