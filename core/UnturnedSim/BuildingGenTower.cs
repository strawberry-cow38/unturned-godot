using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnturnedSim
{
    /// <summary>A tower's plan (BuildingGenSpec.Tower): one of the researched styles strawberry approved (2026-10-19: "go for main
    /// street office, setback, and international for now. good blend of simple + shaped + advanced buildings"), Auto for a seeded
    /// one. MainStreet: the Concord-type office block of the 1890s-1930s, six storeys, shops and a banking room on the ground, a
    /// lift with its stair wrapped round it, a rear light well. Setback: the 1920s-30s skyscraper (OKC First National), thirty
    /// storeys -- a thirteen-storey U round a light court over a two-storey banking hall, a narrower tower over its front from the
    /// fourteenth, stepping in again near the top to a machinery crown -- eight lifts in two banks of four. Slab: the 1950s-70s
    /// International Style slab (Lever House, Inland Steel), twenty-one office storeys of glass over a colonnade on pilotis, an end
    /// core of six lifts, a second stair mid-slab, a two-storey mechanical penthouse on top.</summary>
    public enum TowerStyle { Auto, MainStreet, Setback, Slab }

    /// <summary>
    /// TALL BUILDINGS (strawberry, 2026-10-19, on the six researched tower types: "add support for rooms being more than 1 floor in
    /// height (as many as it wants) leave elevator shafts as empty shafts for now. go for the full height. go for main street office,
    /// setback, and international for now"). Each is a fixed plan of the research's (notes: towers/0[1-3]_*.txt, 1 cell = 3 m game =
    /// 5 ft real; a storey the game's 4.25 m, so thirty storeys stand about 128 m), every storey's rooms off its floor type's list of
    /// rectangles -- CODE[label]@rRcC:HxW, row 0 the back (the alley), the last row the street, as the research drew them -- at the
    /// FULL researched height: the main-street block 6 storeys, the setback 30, the slab 24 (ground, 21 office storeys and a
    /// two-storey penthouse). The plans are kept as drawn but where a bend is said, at the plan.
    ///
    /// What is new here, and general (BuildingGen.cs, Furnish.cs): a room may stand through more than one storey (GenRoom.Span) --
    /// its walls the full height, its windows on every storey of it, its floor only at its foot, its furniture there, its lights
    /// under whatever covers its top -- which is what a banking hall two storeys tall is, a machinery crown, a penthouse, and an
    /// empty lift shaft (RoomKind.Shaft): a void through every storey it passes, walled round, its pit the floor it starts on, capped
    /// by whatever floor or roof is over its top, open onto its lift hall on every storey it serves (no cab, no leaf: you can fall
    /// down it). A storey may be any shape and stand anywhere over the one below (BuildingGenResult.Traced: floors and roofs traced
    /// off the storeys' grids -- a setback's terrace, a light well, a slab over a colonnade). And stair cores (BuildingGenResult.Cores):
    /// the house's switchback, stacked straight up every storey of a core in the same cells, any number of cores, either way along
    /// the plan -- fitted into the research's 2 x 4 cell stair rooms as three cells of flights and landing and one of floor, where its
    /// door is.
    ///
    /// The rooms: OC a cellular office (Office), OF an open office (Office: one room a storey, its rectangles together -- an open
    /// office is one space, and as four the back one had no door), CF a conference room (Office), BR the pantry (BreakRoom), HL and
    /// LB halls, corridors, lift halls and lobbies (one Hall a storey wherever they meet -- circulation is one space), ST a stair
    /// room (Stairwell), EL a lift shaft (Shaft), MC a janitor's, electric or riser closet (Storage) -- or a pipe chase, a sealed
    /// Shaft --, SR a store or a vault (Storage), BA a restroom (Bathroom), SH a shop or a banking hall (Shop, a tenant's: its own
    /// Business and palette, as a strip's), LD a loading dock (Warehouse: its vehicle door), PL a plant room -- the setback's crown,
    /// the slab's penthouse -- (Workshop). The doors: a street door into the lobby and into each shop, its service doors, each stair
    /// room's door into its free cell (laid, so it never opens onto the flights), each shaft's opening onto its lift hall, and the
    /// rest Connect's, storey by storey -- every office and closet off the corridors as a house's are. From the rooms on it is the
    /// house pipeline: BuildWalls (every face a street's: a window to every cell, BuildingGenResult.FacadeArch), the corner solve,
    /// Furnish, the parapets.
    /// </summary>
    public static partial class BuildingGen
    {
        /// <summary>A tower's open office has a desk to about every TowerDeskArea m2, up to TowerDesks (Furnish's Office, OfficeFloor).</summary>
        const int TowerDesks = 12;
        const float TowerDeskArea = 45f;

        /// <summary>The rooms a tower's plans hold past the house caps (CapCells), gated to the tower as the strip's dining room is: an
        /// open office floor (the slab's, 139 cells), a banking hall (the setback's, 12 x 18 cells), a plant room (the crown, 84; the
        /// penthouse, 136) -- and an office bent to four rows over the banking hall (20). Null: the kind's own cap.</summary>
        static int? TowerCap(RoomKind k) => k switch
        {
            RoomKind.Office => 160, RoomKind.Shop => 220, RoomKind.Workshop => 160, RoomKind.Shaft => int.MaxValue,
            _ => null,
        };

        /// <summary>One rectangle of a plan's storey, as the research lists it: CODE[label]@rRcC:HxW -- H rows by W columns at row R
        /// (0 the back) and column C (0 the left as drawn) -- and, after it, '/x' the tenant it is a room of (a shop's or a bank's own
        /// rooms), '*n' the storeys it stands through (a tall room, listed on its bottom storey only).</summary>
        sealed class TowerRect
        {
            public string Code, Label = "";
            public int R, C, H, W, Span = 1;
            public char Tenant = '.';
            public TowerRect Copy() => (TowerRect)MemberwiseClone();
            public bool Has(int r, int c) => r >= R && r < R + H && c >= C && c < C + W;
            public override string ToString() => $"{Code}[{Label}]@r{R}c{C}:{H}x{W}";
        }

        static readonly Regex TowerToken = new Regex(@"([A-Z]{2})(?:\[([^\]]*)\])?@r(\d+)c(\d+):(\d+)x(\d+)(?:/([a-z]))?(?:\*(\d+))?");
        static List<TowerRect> TowerRects(string s) => TowerToken.Matches(s).Select(m => new TowerRect
        {
            Code = m.Groups[1].Value, Label = m.Groups[2].Value, R = int.Parse(m.Groups[3].Value), C = int.Parse(m.Groups[4].Value),
            H = int.Parse(m.Groups[5].Value), W = int.Parse(m.Groups[6].Value),
            Tenant = m.Groups[7].Success ? m.Groups[7].Value[0] : '.', Span = m.Groups[8].Success ? int.Parse(m.Groups[8].Value) : 1,
        }).ToList();

        /// <summary>A door to the street a plan has besides its shops' (each of which gets its own): into the room at plan cell
        /// (R, C), in its outside wall on its plan face -- 'F' the street's (the last row's side), 'B' the alley's, 'L' and 'R' the
        /// sides as drawn -- Count of them along that wall; Back for a service door. Into a stair room, in its free cell.</summary>
        sealed class TowerDoor { public int R, C, Count = 1; public char Face; public bool Back; }

        /// <summary>One tower plan: its grid's size, its storeys (each the rectangles standing on it), which lifts open on which
        /// storeys, its facade's window, its street and service doors, and the bends it makes from the research.</summary>
        sealed class TowerPlan
        {
            public TowerStyle Style;
            public string Name;
            public int Rows, Cols;
            public List<TowerRect>[] Storeys;
            /// <summary>Does the lift of this label open onto its hall on storey f (a high-rise bank passes the low storeys blind)?</summary>
            public Func<string, int, bool> Opens = (_, _) => true;
            public int FacadeArch = ArchWindow;
            public float FacadeMax = float.MaxValue;
            public List<TowerDoor> Doors = new List<TowerDoor>();
        }

        static TowerPlan[] _towerPlans;
        /// <summary>The plans, built on first use (never in a static initialiser: a slip in one would take every building down).</summary>
        static TowerPlan[] TowerPlans => _towerPlans ??= new[] { MainStreetPlan(), SetbackPlan(), SlabPlan() };

        /// <summary>A plan's name with its article: "a setback skyscraper", "an International Style slab".</summary>
        static string A(string name) => ("aeiouAEIOU".IndexOf(name[0]) >= 0 ? "an " : "a ") + name;

        static TowerDoor Door(int r, int c, char face, int count = 1, bool back = false) => new TowerDoor { R = r, C = c, Face = face, Count = count, Back = back };

        /// <summary>The main-street office block (Concord-type, 1890s-1930s), 19 x 10 cells, six storeys as real: on the ground a 15-ft
        /// entry bay from the street to the lift lobby, a shop either side -- the corner one a banking room (Concord's 1891 tenant:
        /// a bank, its back hall, vault, staff WC and two offices behind) -- and two rear offices off a service hall; one lift with
        /// its stair wrapped round it behind; above, cellular offices either side of a double-loaded corridor, the restrooms lit by a
        /// rear light well. Its punched windows a masonry front's (FacadeMax). Its back door the stair's, onto the alley ("deliveries
        /// via the rear alley door"), and the bank's corner door. As the research drew it.</summary>
        static TowerPlan MainStreetPlan()
        {
            var ground = TowerRects("ST[stair wraps lift]@r0c7:2x4 HL[stair landing]@r2c7:3x1 EL[lift 1]@r2c8:2x2 MC[janitor/riser]@r2c10:2x1 LB[entry+lift lobby]@r4c8:6x3 "
                + "SH[shop]@r5c0:5x8/a SH[corner bank hall]@r5c11:5x8/b HL[rear service hall]@r2c0:1x7 OC[rear office]@r0c0:2x3 OC[rear office]@r0c3:2x4 "
                + "SR[shop stockroom]@r3c0:2x3/a BA[shop WC]@r3c3:2x2/a SR[stock]@r3c5:2x2/a HL[bank back hall]@r4c11:1x8/b SR[vault]@r0c11:2x2/b BA[staff WC]@r2c11:2x2/b "
                + "OC[bank office]@r0c13:4x3/b OC[bank office]@r0c16:4x3/b");
            var upper = TowerRects("ST[stair wraps lift]@r0c7:2x4 HL[stair landing]@r2c7:2x1 EL[lift 1]@r2c8:2x2 MC[janitor/riser]@r2c10:2x1 BA[W]@r2c3:2x2 BA[M]@r2c5:2x2 "
                + "HL[E-W corridor]@r4c0:1x19 OC@r0c0:4x3 OC@r0c11:4x3 OC@r0c14:4x3 OC@r0c17:4x2 OC@r5c0:5x3 OC@r5c3:5x3 OC@r5c6:5x2 OC@r5c8:5x3 OC@r5c11:5x2 "
                + "OC@r5c13:5x3 OC@r5c16:5x3");
            var storeys = new List<TowerRect>[6];
            storeys[0] = ground;
            for (int f = 1; f < 6; f++) storeys[f] = upper.Select(r => r.Copy()).ToList();
            return new TowerPlan
            {
                Style = TowerStyle.MainStreet, Name = "main-street office block", Rows = 10, Cols = 19, Storeys = storeys,
                FacadeArch = ArchWindow, FacadeMax = 1.9f,
                Doors = { Door(4, 8, 'F'), Door(5, 11, 'R'), Door(0, 7, 'B', back: true) },
            };
        }

        /// <summary>The setback skyscraper (1920s-30s, OKC First National type), 30 x 28 cells, thirty storeys as real -- the
        /// research's cap suggestion not taken (strawberry: "go for the full height"). Ground: a 30-ft entrance mid-front into a
        /// lift-hall arcade the width of the building (a door at each end onto the side streets), six front shops and eight
        /// side-street shops, each a tenant, the banking hall behind the core, a loading dock and two service corridors off the
        /// alley. Storeys 2-13 the U round the light court, cellular offices off a corridor down each leg; 14 up the tower over the
        /// front bar, set back from both side streets, its roof below a terrace with a parapet. Eight lifts in two banks of four: the
        /// low bank serves 1-13 and is capped at 13 (its shafts closets and stores above); the high bank serves the lobby and 14-27,
        /// passing 2-13 blind. Two stairs ~80 ft apart, every storey to the crown.
        /// Bends from the research, and why: (1) the banking hall is two storeys tall (GenRoom.Span; the real one was), so storey 2
        /// over it is the hall's void where the research had the U's inner legs' offices -- those ten go, and the two at the back of
        /// the legs take the row the hall leaves them (4 x 5 cells, past an office's cap: TowerCap); the light court's floor is then
        /// the hall's skylit roof at storey 3, its back over the loading dock a roof at storey 2. (2) The setback schedule's further
        /// steps, from its notes as fractions of the height: storeys 21-27 step in to the core's width (cols 5-24, the corner
        /// offices dropped), and 28-30 are the crown -- one plant room three storeys tall over the core (lift machinery, tank,
        /// beacon: "not occupied"), the stairs topping out into it and the high bank's shafts capped by its floor; the research's
        /// crown (cols 9-20) is the core's width less the stairs, cols 8-21, so both stairs reach it. (3) The two stores at the back
        /// corners are the corner side-street shops' stock rooms: off the boiler room, the only room beside them, nothing could
        /// reach them.</summary>
        static TowerPlan SetbackPlan()
        {
            const string core = "ST[stair A]@r16c6:4x2 ST[stair B]@r16c22:4x2 MC[janitor]@r20c6:2x2 MC[electric]@r20c22:2x2 MC[pipe/vent shaft]@r16c8:6x1 MC[pipe/vent shaft]@r16c21:6x1 "
                + "HL[lift hall A]@r16c11:6x2 HL[lift hall B]@r16c17:6x2 BA[M]@r16c9:2x2 BA[M]@r16c13:2x2 BA[W]@r16c15:2x2 BA[W]@r16c19:2x2 ";
            const string lifts = "EL[low 1]@r18c9:2x2 EL[low 2]@r20c9:2x2 EL[low 3]@r18c13:2x2 EL[low 4]@r20c13:2x2 EL[high 1]@r18c15:2x2 EL[high 2]@r20c15:2x2 EL[high 3]@r18c19:2x2 EL[high 4]@r20c19:2x2 ";
            var ground = TowerRects(core + lifts
                + "SH@r23c0:5x4/a SH@r23c4:5x4/b SH@r23c8:5x4/c LB[entrance]@r23c12:5x6 SH@r23c18:5x4/d SH@r23c22:5x4/e SH@r23c26:5x4/f LB[lift-hall front / arcade]@r22c0:1x30 "
                + "SH[side-street shop]@r17c0:5x5/g SH[side-street shop]@r17c25:5x5/h SH[side-street shop]@r12c0:5x5/i SH[side-street shop]@r12c25:5x5/j "
                + "SH[side-street shop]@r7c0:5x5/k SH[side-street shop]@r7c25:5x5/l SH[side-street shop]@r2c0:5x5/m SH[side-street shop]@r2c25:5x5/n "
                + "SR[store]@r0c0:2x3/m MC[boiler/coal]@r0c3:2x2 SR[store]@r0c27:2x3/n MC[boiler/coal]@r0c25:2x2 HL[service corridor W]@r0c5:22x1 HL[service corridor E]@r0c24:22x1 "
                + "SH[banking hall (skylit, under the light court)]@r4c6:12x18/o*2 HL[rear service hall]@r3c6:1x18 LD[loading dock off alley]@r0c6:3x18");
            var baseFloor = TowerRects(core + lifts
                + "OC@r23c0:5x3 OC@r23c3:5x3 OC@r23c6:5x3 OC@r23c9:5x3 OC@r23c12:5x3 OC@r23c15:5x3 OC@r23c18:5x3 OC@r23c21:5x3 OC@r23c24:5x3 OC@r23c27:5x3 "
                + "HL[front corridor]@r22c0:1x30 HL[W leg corridor]@r0c5:22x1 HL[E leg corridor]@r0c24:22x1 OC@r0c0:3x5 OC@r0c25:3x5 OC@r3c0:3x5 OC@r3c25:3x5 OC@r6c0:3x5 "
                + "OC@r6c25:3x5 OC@r9c0:3x5 OC@r9c25:3x5 OC@r12c0:3x5 OC@r12c25:3x5 OC@r15c0:3x5 OC@r15c25:3x5 OC@r18c0:2x5 OC@r18c25:2x5 OC@r20c0:2x5 OC@r20c25:2x5 "
                + "OC@r0c6:3x5 OC@r0c19:3x5 OC@r3c6:3x5 OC@r3c19:3x5 OC@r6c6:3x5 OC@r6c19:3x5 OC@r9c6:3x5 OC@r9c19:3x5 OC@r12c6:2x5 OC@r12c19:2x5 OC@r14c6:2x5 OC@r14c19:2x5");
            var tower = TowerRects(core
                + "MC[ex-shaft: telephone]@r18c9:2x2 SR[store]@r20c9:2x2 MC[ex-shaft: mail/elec]@r18c13:2x2 SR[store]@r20c13:2x2 "
                + "EL[high 1]@r18c15:2x2 EL[high 2]@r20c15:2x2 EL[high 3]@r18c19:2x2 EL[high 4]@r20c19:2x2 OC@r23c2:5x3 OC@r23c5:5x3 OC@r23c8:5x3 OC@r23c11:5x3 "
                + "OC@r23c14:5x2 OC@r23c16:5x3 OC@r23c19:5x3 OC@r23c22:5x3 OC@r23c25:5x3 HL[front corridor]@r22c2:1x26 HL[stair passage W]@r16c5:6x1 HL[stair passage E]@r16c24:6x1 "
                + "OC[rear corner]@r16c2:6x3 OC[rear corner]@r16c25:6x3");
            // storey 2 over the banking hall (bend 1): the inner legs' offices over it go, the back two take its row
            var overHall = baseFloor.Where(r => !(r.Code == "OC" && (r.C == 6 || r.C == 19) && r.R >= 3 && r.R < 16)).Select(r => r.Copy()).ToList();
            foreach (var r in overHall.Where(r => r.Code == "OC" && (r.C == 6 || r.C == 19) && r.R == 0)) r.H = 4;
            // the upper setback (bend 2): what of the tower stands in cols [c0, c1), a corridor cut to it
            List<TowerRect> Within(int c0, int c1) => tower.Where(r => r.C + r.W > c0 && r.C < c1 && (r.Code == "HL" || (r.C >= c0 && r.C + r.W <= c1)))
                                                         .Select(r => { var q = r.Copy(); int a = Math.Max(c0, q.C), b = Math.Min(c1, q.C + q.W); q.C = a; q.W = b - a; return q; }).ToList();
            var crown = TowerRects("ST[stair A]@r16c6:4x2 ST[stair B]@r16c22:4x2 MC[janitor]@r20c6:2x2 MC[electric]@r20c22:2x2 PL[lift machinery, tank, beacon]@r16c8:6x14*3");
            var storeys = new List<TowerRect>[30];
            for (int f = 0; f < 30; f++)
                storeys[f] = f == 0 ? ground : f == 1 ? overHall : f <= 12 ? baseFloor.Select(r => r.Copy()).ToList()
                           : f <= 19 ? tower.Select(r => r.Copy()).ToList() : f <= 26 ? Within(5, 25) : f == 27 ? crown : new List<TowerRect>();
            return new TowerPlan
            {
                Style = TowerStyle.Setback, Name = "setback skyscraper", Rows = 28, Cols = 30, Storeys = storeys,
                // the high bank opens on the lobby and from the 14th storey up; the low bank everywhere it stands
                Opens = (label, f) => !label.StartsWith("high") || f == 0 || f >= 13,
                FacadeArch = ArchWindow, FacadeMax = 1.9f,
                Doors = { Door(23, 12, 'F', 2), Door(22, 0, 'L'), Door(22, 29, 'R'), Door(0, 5, 'B', back: true), Door(0, 24, 'B', back: true) },
            };
        }

        /// <summary>The International Style slab (1950s-70s, Lever House / Inland Steel type), 36 x 12 cells (the plaza in front of it,
        /// the research's rows 12-19, is open ground and no part of it), 24 storeys as real (Lever's 24): the ground only about a
        /// third enclosed -- the end core, a glass lobby beside it, a loading bay and a store -- the rest a colonnade on pilotis under
        /// the slab's soffit, stair B standing alone in it and discharging there; 21 office storeys of glass (as much glass as a cell
        /// takes: ArchTallWindow every cell), private offices along the back off a spine corridor, the typing pool, the open office,
        /// the pantry and the conference room along the front; a two-storey mechanical penthouse, louvred, over the core end. Six
        /// lifts (3 + 3 facing the lift lobby) to the top office storey, capped by the penthouse floor; stair A in the core to the
        /// penthouse, stair B to the top office storey.
        /// Bends from the research, and why: the open office's four rectangles are one open-plan room a storey (an open office is
        /// one space, and as four Private rooms the back one had no door); the penthouse -- "2-3 storey", over the core end -- is cols
        /// 0-11, two storeys, one plant room through both, beside stair A.</summary>
        static TowerPlan SlabPlan()
        {
            const string core = "ST[stair A (core)]@r0c0:2x4 BA[M]@r0c4:2x2 BA[M]@r0c6:2x2 HL[core back corridor]@r2c0:1x8 EL[lift]@r3c1:2x2 EL[lift]@r7c1:2x2 EL[lift]@r3c3:2x2 EL[lift]@r7c3:2x2 "
                + "EL[lift]@r3c5:2x2 EL[service lift]@r7c5:2x2 MC[shaft]@r3c0:2x1 MC[shaft]@r7c0:2x1 HL[core side link]@r3c7:2x1 HL[core side link]@r7c7:2x1 HL[core side link]@r5c7:2x1 "
                + "HL[core front corridor]@r9c0:1x8 MC[electric]@r10c0:2x2 MC[telephone/janitor]@r10c2:2x2 BA[W]@r10c4:2x2 BA[W]@r10c6:2x2 ";
            var ground = TowerRects(core + "LB[lift lobby]@r5c0:2x7 LB[glass lobby]@r2c8:8x8 ST[stair B (mid-slab), discharges into the colonnade]@r6c20:2x4 "
                + "LD[service/loading off the side street]@r0c8:2x4 SR[building store]@r0c12:2x3");
            var upper = TowerRects(core + "HL[lift lobby]@r5c0:2x7 OC@r0c8:5x3 OC@r0c11:5x3 OC@r0c14:5x3 OC@r0c17:5x3 OC@r0c20:5x3 OC@r0c23:5x3 OC@r0c26:5x3 OC@r0c29:5x3 "
                + "OC@r0c32:5x2 OC@r0c34:5x2 HL[spine corridor]@r5c8:1x28 BR[coffee/pantry]@r6c8:3x3 CF[conference]@r9c8:3x4 ST[stair B (mid-slab)]@r6c20:2x4 "
                + "OF[open office (secretarial pool)]@r6c11:3x9 OF[open office]@r6c24:3x12 OF[open office]@r9c12:3x24 OF[open office]@r8c20:1x4");
            var penthouse = TowerRects("ST[stair A (core)]@r0c0:2x4 PL[mechanical penthouse]@r0c4:2x8*2 PL[mechanical penthouse]@r2c0:10x12*2");
            var storeys = new List<TowerRect>[24];
            for (int f = 0; f < 24; f++)
                storeys[f] = f == 0 ? ground : f <= 21 ? upper.Select(r => r.Copy()).ToList() : f == 22 ? penthouse : new List<TowerRect>();
            return new TowerPlan
            {
                Style = TowerStyle.Slab, Name = "International Style slab", Rows = 12, Cols = 36, Storeys = storeys,
                FacadeArch = ArchTallWindow,
                Doors = { Door(2, 8, 'F'), Door(2, 15, 'R'), Door(6, 20, 'F') },
            };
        }

        /// <summary>A tower's room kind for a plan code (the class doc has the list), its label telling a pipe chase from a closet.</summary>
        static RoomKind TowerKind(TowerRect r) => r.Code switch
        {
            "OC" or "OF" or "CF" => RoomKind.Office,
            "BR" => RoomKind.BreakRoom,
            "HL" or "LB" => RoomKind.Hall,
            "ST" => RoomKind.Stairwell,
            "EL" => RoomKind.Shaft,
            "MC" => r.Label.Contains("shaft") && !r.Label.StartsWith("ex-") ? RoomKind.Shaft : RoomKind.Storage,
            "SR" => RoomKind.Storage,
            "BA" => RoomKind.Bathroom,
            "SH" => RoomKind.Shop,
            "LD" => RoomKind.Warehouse,
            "PL" => RoomKind.Workshop,
            _ => throw new InvalidOperationException($"BuildingGen: no tower room code {r.Code}"),
        };

        /// <summary>Codes whose touching rectangles on a storey are one room (of one tenant): circulation, and an open office.</summary>
        static string TowerMerge(string code) => code is "HL" or "LB" ? "HL" : code is "OF" or "PL" ? code : null;

        static BuildingGenResult Tower(BuildingGenSpec spec, BuildingGenResult res, Rng rng, DoorPlanner connect)
        {
            res.Roof = RoofKind.Flat; res.RoofPitch = 0f;
            // ---- the plan: the one asked for, else a seeded one -- the main-street block most often (the commonest by far in a
            // town), the setback and the slab after it -- each faced any way asked (its street on the side asked, south when none
            // was; laid out with its street on the north, east or west, and a south front turned half round last), and now and
            // then mirror-wise. On a town's plot (FitPlot), the first that fits it in that order, else the closest, said so.
            var order = new List<TowerStyle>();
            if (spec.Tower != TowerStyle.Auto) order.Add(spec.Tower);
            else
            {
                int roll = rng.Int(100);
                order.Add(roll < 50 ? TowerStyle.MainStreet : roll < 75 ? TowerStyle.Setback : TowerStyle.Slab);
                foreach (var (st, _) in rng.Keyed(TowerPlans.Select(p => p.Style).Where(st => st != order[0])).OrderBy(x => x.Key)) order.Add(st);
            }
            var asked = spec.FrontSide == Sides.None ? Sides.South : spec.FrontSide;
            var fronts = new[] { Sides.South, Sides.North, Sides.East, Sides.West }.Where(sd => (asked & sd) != 0).ToList();
            bool mirror = rng.Chance(50);
            TowerPlan plan = null; Sides front = fronts[0];
            if (spec.FitPlot && spec.Width > 0 && spec.Depth > 0)
            {
                var cands = order.Select((st, i) => (Plan: TowerPlans.First(p => p.Style == st), i)).SelectMany(p => fronts.Select(fs => (p.Plan, p.i, Front: fs))).ToList();
                (int X, int Z) Size(TowerPlan p, Sides fs) => fs is Sides.North or Sides.South ? (p.Cols, p.Rows) : (p.Rows, p.Cols);
                int Over((TowerPlan Plan, int i, Sides Front) c) { var (x, z) = Size(c.Plan, c.Front); return Math.Max(0, x - spec.Width) + Math.Max(0, z - spec.Depth); }
                var best = cands.OrderBy(Over).ThenBy(c => c.i).ThenBy(c => fronts.IndexOf(c.Front)).First();
                plan = best.Plan; front = best.Front;
                var (bx, bz) = Size(plan, front);
                res.Notes.Add(Over(best) > 0 ? $"could not fit the {spec.Width}x{spec.Depth} plot: {bx}x{bz} is the closest of {cands.Count} towers"
                                             : $"fitted the {spec.Width}x{spec.Depth} plot as {bx}x{bz}" + (best.i > 0 ? $": {A(plan.Name)}, not {A(TowerPlans.First(p => p.Style == order[0]).Name)}" : ""));
            }
            else
            {
                plan = TowerPlans.First(p => p.Style == order[0]);
                int len = front is Sides.North or Sides.South ? plan.Cols : plan.Rows, deep = front is Sides.North or Sides.South ? plan.Rows : plan.Cols;
                if (spec.Width > 0 && spec.Width != (front is Sides.North or Sides.South ? len : deep) || spec.Depth > 0 && spec.Depth != (front is Sides.North or Sides.South ? deep : len))
                    res.Notes.Add($"{A(plan.Name)} is {(front is Sides.North or Sides.South ? $"{len}x{deep}" : $"{deep}x{len}")} cells, as its plan is: not the {spec.Width}x{spec.Depth} asked for");
            }
            if (spec.Basement == BasementChoice.Require) res.Notes.Add("a tower has no basement here");
            if (spec.BackSide != Sides.None && (spec.BackSide & Turned(front)) == 0) res.Notes.Add($"a tower's service doors are on its alley, opposite its front: not {spec.BackSide}");
            var laid = front == Sides.South ? Sides.North : front;
            int R = plan.Rows, C = plan.Cols, F = plan.Storeys.Length;
            int W = laid is Sides.North ? C : R, D = laid is Sides.North ? R : C;
            // a plan cell to the laid-out one: the street on the north (x along the row, z from the alley), the east (x from the
            // alley) or the west; a column mirrored first
            (int X, int Z) Laid(int r, int c)
            {
                int cm = mirror ? C - 1 - c : c;
                return laid switch { Sides.East => (r, C - 1 - cm), Sides.West => (R - 1 - r, cm), _ => (cm, r) };
            }
            (int X0, int Z0, int X1, int Z1) LaidRect(TowerRect q)
            {
                var a = Laid(q.R, q.C); var b = Laid(q.R + q.H - 1, q.C + q.W - 1);
                return (Math.Min(a.X, b.X), Math.Min(a.Z, b.Z), Math.Max(a.X, b.X) + 1, Math.Max(a.Z, b.Z) + 1);
            }
            // a plan face to the side it faces once laid
            Sides Face(char face)
            {
                var (dr, dc) = face switch { 'F' => (1, 0), 'B' => (-1, 0), 'L' => (0, -1), _ => (0, 1) };
                if (mirror) dc = -dc;
                var (dx, dz) = laid switch { Sides.East => (dr, -dc), Sides.West => (-dr, dc), _ => (dc, dr) };
                return dx > 0 ? Sides.East : dx < 0 ? Sides.West : dz > 0 ? Sides.North : Sides.South;
            }
            var streetSide = Face('F');
            var alleySide = Face('B');

            // ---- the rooms, storey by storey: each rectangle its own room, but for circulation and an open office, whose touching
            // rectangles on a storey (of one tenant) are one room; a lift shaft or a pipe chase one room through every storey its
            // rectangle stands on, one after the other; a tall room ('*n') one room through n storeys from the one it is listed on
            var tenants = plan.Storeys.SelectMany(s => s).Select(q => q.Tenant).Where(t => t != '.').Distinct().OrderBy(t => t).ToList();
            int UnitOf(char t) => t == '.' ? -1 : tenants.IndexOf(t);
            var rooms = new List<GenRoom>();
            var label = new Dictionary<GenRoom, string>();
            var onFloor = new List<GenRoom>[F];
            var shafts = new List<(GenRoom Room, TowerRect At)>();
            var stairs = new Dictionary<(int, int, int, int), List<GenRoom>>();   // a stair room per storey, by its cells
            for (int f = 0; f < F; f++)
            {
                onFloor[f] = new List<GenRoom>();
                var rects = plan.Storeys[f];
                var used = new HashSet<TowerRect>();
                foreach (var q in rects)
                {
                    if (used.Contains(q)) continue;
                    var kind = TowerKind(q);
                    var lr = LaidRect(q);
                    if (kind == RoomKind.Shaft)
                    {
                        // one shaft through the storeys its rectangle stands on in a row: carried on from the storey below's
                        used.Add(q);
                        var below = shafts.FirstOrDefault(s => s.Room.Floor + s.Room.Span == f && s.At.R == q.R && s.At.C == q.C && s.At.H == q.H && s.At.W == q.W);
                        if (below.Room != null) { below.Room.Span++; continue; }
                        var shaft = new GenRoom { Kind = RoomKind.Shaft, Floor = f, X0 = lr.X0, Z0 = lr.Z0, X1 = lr.X1, Z1 = lr.Z1 };
                        shafts.Add((shaft, q)); rooms.Add(shaft); label[shaft] = q.Label; onFloor[f].Add(shaft);
                        continue;
                    }
                    // a room's rectangles: this one, and for circulation and an open office every one of its code and tenant touching
                    var group = new List<TowerRect> { q };
                    if (TowerMerge(q.Code) is string mc)
                        for (bool grew = true; grew;)
                        {
                            grew = false;
                            foreach (var o in rects.Where(o => !group.Contains(o) && TowerMerge(o.Code) == mc && o.Tenant == q.Tenant))
                                if (group.Any(g0 => Touch(g0, o))) { group.Add(o); grew = true; }
                        }
                    foreach (var g0 in group) used.Add(g0);
                    var cells = new HashSet<(int X, int Z)>();
                    foreach (var g0 in group)
                    {
                        var r0 = LaidRect(g0);
                        for (int x = r0.X0; x < r0.X1; x++) for (int z = r0.Z0; z < r0.Z1; z++) cells.Add((x, z));
                    }
                    var rs = Rectangles(cells).OrderByDescending(r => (r.X1 - r.X0) * (r.Z1 - r.Z0)).ThenBy(r => r.Z0).ThenBy(r => r.X0).ToList();
                    var room = new GenRoom { Kind = kind, Floor = f, Unit = UnitOf(q.Tenant), Span = q.Span, X0 = rs[0].X0, Z0 = rs[0].Z0, X1 = rs[0].X1, Z1 = rs[0].Z1 };
                    room.More.AddRange(rs.Skip(1));
                    rooms.Add(room); label[room] = group.Count == 1 ? q.Label : string.Join(" + ", group.Select(g0 => g0.Label).Where(l => l != "").Distinct());
                    onFloor[f].Add(room);
                    if (kind == RoomKind.Stairwell)
                    {
                        var key = (lr.X0, lr.Z0, lr.X1, lr.Z1);
                        if (!stairs.TryGetValue(key, out var st)) stairs[key] = st = new List<GenRoom>();
                        st.Add(room);
                    }
                }
            }
            static bool Touch(TowerRect a, TowerRect b)
                => (a.R < b.R + b.H && b.R < a.R + a.H && (a.C + a.W == b.C || b.C + b.W == a.C))
                   || (a.C < b.C + b.W && b.C < a.C + a.W && (a.R + a.H == b.R || b.R + b.H == a.R));

            // ---- each storey's grid: its own rooms, and every tall room standing through it from below (its void: walled, unfloored)
            var grids = new List<Grid>();
            for (int f = 0; f < F; f++)
            {
                var g = new Grid(W, D, 0, W);
                foreach (var r in rooms.Where(r => r.Floor <= f && f < r.Floor + r.Span)) g.Paint(r);
                grids.Add(g);
            }
            // (the grid a storey is doored on: without the voids, so no door ever opens into one -- and the one Connect doors it
            // on, without its stair rooms too, whose doors are laid into their free cells: Connect's could open onto the flights)
            var doorGrids = new List<Grid>();
            var connectGrids = new List<Grid>();
            for (int f = 0; f < F; f++)
            {
                var g = new Grid(W, D, 0, W);
                foreach (var r in rooms.Where(r => r.Floor == f)) g.Paint(r);
                doorGrids.Add(g);
                var cg = new Grid(W, D, 0, W);
                foreach (var r in rooms.Where(r => r.Floor == f && r.Kind != RoomKind.Stairwell)) cg.Paint(r);
                connectGrids.Add(cg);
            }

            // ---- the stair cores: each stair rectangle stacked storey on storey, a switchback in three of its four cells along it
            // and its door into the fourth, its free cell: at the end whose free cells have a hall beside them on the most storeys (a
            // door has somewhere to go), else the end that puts the landing against an outside wall; its first flight along the
            // side that is an outside wall, where one is (the second flight's far edge stands too close to one)
            var coreOf = new Dictionary<GenRoom, (StairCore Core, List<(int X, int Z)> Free)>();
            foreach (var (key, st) in stairs)
            {
                var (x0, z0, x1, z1) = key;
                bool alongX = x1 - x0 > z1 - z0;
                if ((alongX ? x1 - x0 : z1 - z0) != 4 || (alongX ? z1 - z0 : x1 - x0) != 2)
                    throw new InvalidOperationException($"BuildingGen: {A(plan.Name)}'s stair room is not 2 x 4 cells: {x1 - x0} x {z1 - z0}");
                st.Sort((a, b) => a.Floor.CompareTo(b.Floor));
                for (int i = 1; i < st.Count; i++)
                    if (st[i].Floor != st[i - 1].Floor + 1) throw new InvalidOperationException($"BuildingGen: {A(plan.Name)}'s stair at {key} skips a storey");
                List<(int X, int Z)> FreeCells(bool low) => alongX ? new List<(int, int)> { (low ? x0 : x1 - 1, z0), (low ? x0 : x1 - 1, z0 + 1) }
                                                                   : new List<(int, int)> { (x0, low ? z0 : z1 - 1), (x0 + 1, low ? z0 : z1 - 1) };
                IEnumerable<(int X, int Z)> Around((int X, int Z) c) => new[] { (c.X + 1, c.Z), (c.X - 1, c.Z), (c.X, c.Z + 1), (c.X, c.Z - 1) };
                int Score(bool low)
                {
                    int s = 0;
                    foreach (var room in st)
                    {
                        var g = doorGrids[room.Floor];
                        if (FreeCells(low).SelectMany(Around).Any(n => g.At(n.X, n.Z) is GenRoom h && h != room && h.Kind == RoomKind.Hall)) s += 10;
                        // the landing's end wall -- the far end's -- outside
                        var endCells = alongX ? new[] { (low ? x1 : x0 - 1, z0), (low ? x1 : x0 - 1, z0 + 1) } : new[] { (x0, low ? z1 : z0 - 1), (x0 + 1, low ? z1 : z0 - 1) };
                        if (endCells.All(n => grids[room.Floor].At(n.Item1, n.Item2) == null)) s += 1;
                    }
                    return s;
                }
                bool freeLow = Score(true) >= Score(false);
                // the first flight's side: the one that is outside on more storeys (the low one when neither is)
                int Outside(bool lowSide) => st.Count(room => (alongX ? Enumerable.Range(x0, 4).Select(x => (x, lowSide ? z0 - 1 : z1)) : Enumerable.Range(z0, 4).Select(z => (lowSide ? x0 - 1 : x1, z)))
                                                              .Any(n => grids[room.Floor].At(n.Item1, n.Item2) == null));
                var core = new StairCore
                {
                    F0 = st[0].Floor, F1 = st[^1].Floor, AlongX = alongX, LandingHigh = freeLow, FlightLow = Outside(true) >= Outside(false),
                    X0 = alongX ? (freeLow ? x0 + 1 : x0) : x0, X1 = alongX ? (freeLow ? x1 : x1 - 1) : x1,
                    Z0 = alongX ? z0 : (freeLow ? z0 + 1 : z0), Z1 = alongX ? z1 : (freeLow ? z1 : z1 - 1),
                };
                res.Cores.Add(core);
                foreach (var room in st) coreOf[room] = (core, FreeCells(freeLow));
            }

            // ---- what each tenant sells: a bank's banking room an office floor (desks, a counter: Business.Office); any other shop
            // what a strip's tenant may be (StripShops), leaning by the town's wealth as a strip's do, never the same as the tenant
            // before; each wears its shop's palette on its floors, the tower's walls an office's (PalettesFor)
            var wealth = spec.Wealth;
            for (int u = 0; u < tenants.Count; u++)
            {
                var mine = rooms.Where(r => r.Unit == u).ToList();
                Business b;
                if (mine.Any(r => r.Kind == RoomKind.Shop && label[r].Contains("bank"))) b = Business.Office;
                else
                {
                    b = Business.None;
                    for (int t = 0; t < 6 && (b == Business.None || (u > 0 && res.UnitBusiness[u - 1] == b)); t++)
                    {
                        if (wealth == TownWealth.Working) b = StripShops[rng.Int(StripShops.Length)];
                        else
                        {
                            int total = StripShops.Sum(s => Leans(wealth, s) ? StripLean : 1), at = rng.Int(total);
                            foreach (var s in StripShops) { at -= Leans(wealth, s) ? StripLean : 1; if (at < 0) { b = s; break; } }
                        }
                    }
                }
                res.UnitBusiness.Add(b);
                var pals = ShopPalettes(b);
                res.UnitPalette.Add(pals[rng.Int(pals.Length)]);
            }
            res.Business = Business.None;
            res.FacadeArch = plan.FacadeArch; res.FacadeMaxWindow = plan.FacadeMax;

            // ---- the doors, storey by storey
            var doors = new List<GenDoor>();
            var notes = new List<string>();
            GenRoom At(int f, int r, int c) { var (x, z) = Laid(r, c); return doorGrids[f].At(x, z); }
            // a door in a room's free stair cell, onto `onto` (a hall, a room, or null for the street): at the middle of the cell's
            // edge, kept off the walls meeting it as Connect's are
            GenDoor StairDoor(GenRoom stair, int f, Func<GenRoom, bool> onto, Sides face = Sides.None)
            {
                var g = doorGrids[f];
                foreach (var (cx, cz) in coreOf[stair].Free)
                    foreach (var (nx, nz, alongX, line, s) in new[] { (cx + 1, cz, false, cx + 1, cz), (cx - 1, cz, false, cx, cz), (cx, cz + 1, true, cz + 1, cx), (cx, cz - 1, true, cz, cx) })
                    {
                        var n = g.At(nx, nz);
                        if (n == stair || !onto(n)) continue;
                        if (n == null && face != Sides.None && g.Out(alongX, line, s) != face) continue;
                        // (a cell between an outside wall's corner and a partition is 2.4 m clear, under a door's 2.5: the door
                        // narrows to it, down to StairDoorLeast, as a basement's stair room's does to its landing)
                        float a0 = s * Cell + g.Meet(alongX, line, s), a1 = (s + 1) * Cell - g.Meet(alongX, line, s + 1);
                        float width = MathF.Min(ArchSizes[ArchDoor].W, a1 - a0);
                        if (width < StairDoorLeast) continue;
                        float at = Math.Clamp((s + 0.5f) * Cell, a0 + width * 0.5f, a1 - width * 0.5f);
                        return n == null ? new GenDoor { Floor = f, A = stair, B = null, AlongX = alongX, Line = line, At = at, Role = DoorRole.Front, Width = width }
                                         : new GenDoor { Floor = f, A = n, B = stair, AlongX = alongX, Line = line, At = at, Width = width };
                    }
                return null;
            }
            var fixedDoors = new List<GenDoor>();
            // each tenant's street door: its sales floor's longest outside wall on the street, else a side street -- never the alley.
            // A tenant with none (the setback's banking hall, behind the core) is entered from the lobby: its sales floor is its way in
            var sales = new GenRoom[tenants.Count];
            for (int u = 0; u < tenants.Count; u++)
            {
                var floor = rooms.Where(r => r.Unit == u && r.Kind == RoomKind.Shop && r.Floor == 0).OrderByDescending(r => r.Cells).FirstOrDefault()
                            ?? rooms.Where(r => r.Unit == u && r.Floor == 0).OrderByDescending(r => r.Cells).First();
                sales[u] = floor;
                var run = Runs(doorGrids[0]).Where(q => q.B == null && q.A == floor && doorGrids[0].Out(q.AlongX, q.Line, q.S0) != alleySide)
                                            .OrderBy(q => doorGrids[0].Out(q.AlongX, q.Line, q.S0) == streetSide ? 0 : 1).ThenByDescending(q => q.S1 - q.S0).FirstOrDefault();
                GenDoor d = run.A != null ? OutDoor(doorGrids[0], run, doors, DoorRole.Front).Door : null;
                if (d == null) { floor.Entry = true; continue; }
                doors.Add(d); fixedDoors.Add(d);
            }
            // the street and service doors the plan has (into a stair room: in its free cell)
            foreach (var pd in plan.Doors)
            {
                var room = At(0, pd.R, pd.C);
                if (room == null) throw new InvalidOperationException($"BuildingGen: {A(plan.Name)}'s street door at r{pd.R}c{pd.C} is in no room");
                var face = Face(pd.Face);
                var role = pd.Back ? DoorRole.Back : DoorRole.Front;
                if (room.Kind == RoomKind.Stairwell)
                {
                    var d = StairDoor(room, 0, n => n == null, face);
                    if (d == null) { notes.Add($"no room for the {room.Kind}'s door to the {face}"); continue; }
                    d.Role = role; doors.Add(d); fixedDoors.Add(d);
                    continue;
                }
                // (the run of the room's outside wall on that face that the plan's rectangle is on)
                var (px, pz) = Laid(pd.R, pd.C);
                var runs0 = Runs(doorGrids[0]).Where(q => q.B == null && q.A == room && doorGrids[0].Out(q.AlongX, q.Line, q.S0) == face)
                                              .OrderBy(q => q.AlongX ? Math.Abs(q.Line - pz) + (px >= q.S0 && px < q.S1 ? 0 : 100) : Math.Abs(q.Line - px) + (pz >= q.S0 && pz < q.S1 ? 0 : 100)).ToList();
                if (runs0.Count == 0) { notes.Add($"the {label[room]} has no outside wall to the {face} for its door"); continue; }
                for (int k = 0; k < pd.Count; k++)
                {
                    var (d, _) = OutDoor(doorGrids[0], runs0[0], doors, role);
                    if (d == null) { notes.Add($"no room for a door into the {label[room]} to the {face}"); break; }
                    doors.Add(d); fixedDoors.Add(d);
                }
            }
            if (!doors.Any(d => d.Role == DoorRole.Front)) throw new InvalidOperationException($"BuildingGen: the {plan.Name} has no street door");
            // every storey's stair doors: from a hall beside its free cell, else from any room you walk through (the crown, the
            // penthouse), else -- on the ground, where it stands alone -- from the street, as the plan's door there is
            foreach (var (room, (core, free)) in coreOf)
            {
                int f = room.Floor;
                if (doors.Any(d => d.Floor == f && d.B != null && (d.A == room || d.B == room))) continue;
                var d = StairDoor(room, f, n => n != null && n.Kind == RoomKind.Hall && n.Unit < 0)
                        ?? StairDoor(room, f, n => n != null && AccessOf(n.Kind) == Access.Through && n.Kind != RoomKind.Stairwell && n.Unit < 0);
                // (a stair standing alone, its door the street's)
                if (d == null && doors.Any(o => o.Floor == f && o.B == null && o.A == room)) continue;
                if (d == null) { notes.Add($"storey {f}: the stair room at [{room.X0},{room.Z0}] has nowhere for its door"); continue; }
                doors.Add(d); fixedDoors.Add(d);
            }
            var fixedPairs = new HashSet<(GenRoom, GenRoom)>(fixedDoors.Where(d => d.B != null).SelectMany(d => new[] { (d.A, d.B), (d.B, d.A) }));
            void Restore()
            {
                // (Connect's last pass moves a door crowding a corner anywhere along its wall -- a stair's onto its flights -- and takes
                // a street door out: the laid ones go back where they were)
                doors.RemoveAll(d => d.B != null && fixedPairs.Contains((d.A, d.B)) && !fixedDoors.Contains(d));
                foreach (var d in fixedDoors) if (!doors.Contains(d)) doors.Add(d);
            }
            bool turned = false;
            for (int f = 0; f < F; f++)
            {
                var cg = connectGrids[f];
                // each tenant's rooms off its sales floor (the ground's)
                if (f == 0)
                    for (int u = 0; u < tenants.Count; u++)
                    {
                        var rs = rooms.Where(r => r.Unit == u && r.Floor == f).ToList();
                        if (rs.Count < 2) continue;
                        if (!connect(cg, rs, f, sales[u], doors, (0, 0, 0, 0), Sides.None, ref turned)) notes.Add($"tenant {u} ({res.UnitBusiness[u]}): not every room could be given a door");
                        Restore();
                    }
                // the shared rooms in pieces that join -- through a room you walk through -- each off its hall (else its biggest room
                // you walk through): a storey's corridors and lobbies are one hall, so a piece of its own is the stair standing alone
                // (its stair rooms are doored already, into their free cells: Connect sees none of them)
                var shared = rooms.Where(r => r.Floor == f && r.Unit < 0 && r.Kind is not (RoomKind.Shaft or RoomKind.Stairwell)).ToList();
                var runs = Runs(cg);
                var piece = new Dictionary<GenRoom, int>();
                foreach (var r in shared)
                {
                    if (piece.ContainsKey(r)) continue;
                    int id = piece.Count == 0 ? 0 : piece.Values.Max() + 1;
                    var q = new Queue<GenRoom>(); q.Enqueue(r); piece[r] = id;
                    while (q.Count > 0)
                    {
                        var a = q.Dequeue();
                        foreach (var run in runs.Where(run => run.B != null && (run.A == a || run.B == a)))
                        {
                            var b = run.A == a ? run.B : run.A;
                            if (!shared.Contains(b) || piece.ContainsKey(b)) continue;
                            if (AccessOf(a.Kind) != Access.Through && AccessOf(b.Kind) != Access.Through) continue;
                            piece[b] = id; q.Enqueue(b);
                        }
                    }
                }
                foreach (var grp in shared.GroupBy(r => piece[r]))
                {
                    var rs = grp.ToList();
                    var root = rs.Where(r => r.Kind == RoomKind.Hall).OrderByDescending(r => r.Cells).FirstOrDefault()
                               ?? rs.Where(r => AccessOf(r.Kind) == Access.Through).OrderByDescending(r => r.Cells).FirstOrDefault();
                    if (root == null) { notes.Add($"storey {f}: {string.Join(", ", rs.Select(r => label[r] is var l && l != "" ? l : r.Kind.ToString()))} reached by nothing"); continue; }
                    if (rs.Count > 1 && !connect(cg, rs, f, root, doors, (0, 0, 0, 0), Sides.None, ref turned))
                        notes.Add($"storey {f}: not every room off the {(root.Kind == RoomKind.Hall ? "hall" : label[root])} could be given a door");
                    Restore();
                }
            }
            // each lift shaft's opening onto its lift hall, on every storey it serves -- an open doorway, no cab and no leaf; a pipe
            // chase is sealed: the wall that faces another lift across the hall (a bank's lifts face each other, "2+2 facing a lift
            // hall"), then its longest wall with a hall, then the one with the deepest hall in front of it
            foreach (var (shaft, rect) in shafts)
            {
                if (rect.Code != "EL") continue;
                for (int f = shaft.Floor; f < shaft.Floor + shaft.Span; f++)
                {
                    if (!plan.Opens(rect.Label, f)) continue;
                    var g = grids[f];
                    var cands = Runs(g).Where(q => q.B != null && (q.A == shaft || q.B == shaft) && (q.A == shaft ? q.B : q.A) is GenRoom h && h.Kind == RoomKind.Hall && h.Floor == f).ToList();
                    if (cands.Count == 0) { notes.Add($"storey {f}: the {rect.Label} shaft has no lift hall to open onto"); continue; }
                    // how far the hall runs straight out from the run's middle, and whether a lift stands where it ends
                    (int Depth, bool Faces) Out(Run q)
                    {
                        var hall = q.A == shaft ? q.B : q.A;
                        int mid = (q.S0 + q.S1) / 2, n = 0;
                        // (which way the hall lies across the line)
                        int dir = q.AlongX ? (g.At(mid, q.Line) == hall ? 1 : -1) : (g.At(q.Line, mid) == hall ? 1 : -1);
                        GenRoom past = null;
                        for (int k = 0; k < 4; k++)
                        {
                            int a = dir > 0 ? q.Line + k : q.Line - 1 - k;
                            past = q.AlongX ? g.At(mid, a) : g.At(a, mid);
                            if (past != hall) break;
                            n++;
                        }
                        return (n, past != null && past != hall && past.Kind == RoomKind.Shaft && shafts.Any(s => s.Room == past && s.At.Code == "EL"));
                    }
                    var best = cands.OrderByDescending(q => Out(q).Faces).ThenByDescending(q => q.S1 - q.S0).ThenByDescending(q => Out(q).Depth).First();
                    var hallRoom = best.A == shaft ? best.B : best.A;
                    float lo = best.S0 * Cell + g.Meet(best.AlongX, best.Line, best.S0) + HalfDoor, hi = best.S1 * Cell - g.Meet(best.AlongX, best.Line, best.S1) - HalfDoor;
                    float at = Math.Clamp((best.S0 + best.S1) * 0.5f * Cell, lo, Math.Max(lo, hi));
                    doors.Add(new GenDoor { Floor = f, A = hallRoom, B = shaft, AlongX = best.AlongX, Line = best.Line, At = at, Open = true });
                }
            }
            res.Notes.AddRange(notes);

            // ---- the building: its storeys' extents, its rooms, its stair cores (BuildWalls lays them), its storeys traced
            res.Floors = F; res.CellsX = W; res.CellsZ = D;
            for (int f = 0; f < F; f++)
            {
                var xs = rooms.Where(r => r.Floor <= f && f < r.Floor + r.Span).SelectMany(r => r.Rects).ToList();
                res.Footprints.Add(xs.Count == 0 ? (0, W) : (xs.Min(q => q.X0), xs.Max(q => q.X1)));
            }
            res.Traced = true;
            res.StairShape = StairShape.Switchback;
            res.Bay = (0, 0, 0, 0);
            res.Shape = FootprintShape.Rect;
            // (each room within its kind's cap, a tower's own past the house's: TowerCap -- a note should one not be)
            foreach (var r in rooms.Where(r => r.Cells > CapCells(r.Kind, BuildingKind.Tower)))
                res.Notes.Add($"{r.Kind} on storey {r.Floor} is {r.Cells} cells, past a tower's {CapCells(r.Kind, BuildingKind.Tower)}");
            res.Plan = $"tower, {plan.Name}{(mirror ? " mirrored" : "")} fronting {front}: {F} storeys" + (tenants.Count > 0 ? $", {string.Join(", ", res.UnitBusiness)}" : "");
            res.StreetSide = front; res.AlleySide = Turned(front);
            res.Layout.AddRange(rooms);
            res.Doors.AddRange(doors);
            foreach (var r in rooms) res.Rooms.Add(new RoomDesignation(r.Kind, (r.X0 + r.X1) * 0.5f * Cell, (r.Z0 + r.Z1) * 0.5f * Cell, r.Floor));
            if (spec.PlanOnly) return res;

            // ---- the street on the south: laid out with it on the north, turned half round, last
            bool turn = front == Sides.South;
            res.NoWindowSides = turn ? Turned(spec.NoWindows) : spec.NoWindows;
            // walls, furniture -- no porch, no chimney, no basement -- then what stands under a storey over open ground, then the
            // parapets on every flat roof, as every flat roof's
            BuildOut(res, grids, 1, 3, spec, PorchChoice.None, chimney: false);
            Pilotis(res, grids);
            SolveCorners(res.Walls, RoofShapes.AddParapets(res.Walls).Contains);
            SettleShared(res);
            // (and the editor's corner pass, every wall, as the editor runs it -- here, where a wall grown at its start keeps its
            // openings where they were: SettleShared stops a partition short under each storey's slab where a void's or a stair
            // hole's edge crosses it, in pieces, and a partition meeting another at such a cut meets a piece's end, a corner, which
            // the editor would otherwise grow it to, its openings sliding -- a lift shaft's wall at every storey but the top)
            SolveCorners(res.Walls, _ => true);
            RailStairs(res);
            GuardStairs(res);
            if (turn) TurnHalf(res);
            return res;
        }

        /// <summary>The narrowest a stair room's door may come out, metres (StairDoor).</summary>
        const float StairDoorLeast = 2.0f;

        /// <summary>A pilotis's square, and how far apart they stand at most, in cells, each way.</summary>
        const float PilotisSide = Ext;
        const int PilotisEvery = 4;

        /// <summary>What stands under a storey over open ground (a slab over its colonnade: "posts/columns under the slab's soffit"):
        /// a concrete deck on the ground under all of it, out to the outer faces of the walls over it and up to the ground storey's
        /// walls' (recorded with the porches, so a yard keeps off it); and on it columns -- PilotisSide square, from the ground to the
        /// soffit -- on the lattice points every PilotisEvery cells each way from the open ground's corner, under its edges and through
        /// it, where every cell round the point is open ground or outside: none in the doorway of anything enclosed.</summary>
        static void Pilotis(BuildingGenResult res, List<Grid> grids)
        {
            float he = Ext * 0.5f;
            int concrete = FloorTexels.TryGetValue(res.Palette ?? "", out var ft) ? ft.Concrete : -1;
            int W = res.CellsX, D = res.CellsZ;
            for (int f = 1; f < grids.Count; f++)
            {
                // open ground under storey f: its cells over no storey below it
                bool Open(int x, int z) => grids[f].At(x, z) != null && Enumerable.Range(0, f).All(b => grids[b].At(x, z) == null);
                var cells = new List<(int X, int Z)>();
                for (int x = 0; x < W; x++) for (int z = 0; z < D; z++) if (Open(x, z)) cells.Add((x, z));
                if (cells.Count == 0) continue;
                bool Ground(int x, int z) => Enumerable.Range(0, f).Any(b => grids[b].At(x, z) != null);
                foreach (var (x0, z0, x1, z1) in TraceRects(W, D, he, Open, (x, z) => !Open(x, z) && !Ground(x, z), Ground))
                {
                    var deck = SlabPlan(x0, x1, z0, z1, 0f, SurfaceKind.Floor);
                    deck.Texel = concrete;
                    res.Walls.Add(deck);
                    res.Porches.Add((x0, z0, x1, z1));
                }
                int mx = cells.Min(c => c.X), mz = cells.Min(c => c.Z);
                float top = f * Storey - Slab;
                for (int px = mx; px <= W; px += PilotisEvery)
                    for (int pz = mz; pz <= D; pz += PilotisEvery)
                    {
                        var round = new[] { (px - 1, pz - 1), (px, pz - 1), (px - 1, pz), (px, pz) };
                        if (!round.Any(c => Open(c.Item1, c.Item2)) || round.Any(c => Ground(c.Item1, c.Item2))) continue;
                        var post = Wall(px * Cell - PilotisSide * 0.5f, pz * Cell, px * Cell + PilotisSide * 0.5f, pz * Cell, 0f, PilotisSide);
                        post.Height = top;
                        res.Walls.Add(post);
                    }
            }
        }
    }
}
