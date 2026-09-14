namespace SDG.Unturned
{
    /// <summary>MONEY IS ONE STACK WITH A VALUE, not seven items with counts (strawberry 2026-09-14: "the
    /// loonie ($1), toonie($2), $5, $10, $20, $50, $100 collapse into one 'Canadian Dollars' stack... theres
    /// not a stack xX or whatever, theres a $x number, and the inventory icon changes depending on the value
    /// of the stack").
    ///
    /// ⭐ THE CARRIER IS THE LOONIE, and that is the whole trick rather than an arbitrary pick: the loonie is
    /// the $1 unit, so a stack of N of them IS $N. `Item.amount` therefore means dollars LITERALLY -- no
    /// reinterpretation, no parallel field, and every piece of machinery that already moves, splits, merges,
    /// drops, saves and replicates a stack keeps working untouched because it is still just a stack.
    ///
    /// All seven ids are real shipped items (1051-1057, Type Supply, 1x1); nothing here is invented.</summary>
    public static class Currency
    {
        /// <summary>The id every denomination collapses INTO. The $1 coin, so amount == dollars.</summary>
        public const ushort StackId = 1056;
        public const string DisplayName = "Canadian Dollars";

        /// <summary>What one of these is WORTH in dollars, or 0 if it is not money at all.</summary>
        public static int ValueOf(ushort id) => id switch
        {
            1056 => 1,     // Loonie
            1057 => 2,     // Toonie
            1051 => 5,
            1052 => 10,
            1053 => 20,
            1054 => 50,
            1055 => 100,
            _ => 0,
        };

        public static bool IsCurrency(ushort id) => ValueOf(id) > 0;

        /// <summary>Denominations largest-first, which is the order both the icon rule and any future
        /// make-change routine want.</summary>
        public static readonly ushort[] Denominations = { 1055, 1054, 1053, 1052, 1051, 1057, 1056 };

        /// <summary>WHICH NOTE OR COIN A STACK LOOKS LIKE: the largest denomination the stack could actually
        /// pay out. $137 shows the $100 note, $7 shows the $5, $3 shows the toonie, $1 the loonie.
        ///
        /// It is "largest that FITS" rather than anything cleverer because that is the one rule a player can
        /// read off the icon without being told it -- the picture is the biggest thing in the pile.</summary>
        public static ushort IconIdFor(int dollars)
        {
            foreach (var id in Denominations)
                if (dollars >= ValueOf(id)) return id;
            return StackId;   // $0: an empty wallet still has to draw as something
        }

        /// <summary>⚠ A STACK CANNOT HOLD MORE THAN THIS, because `Item.amount` is a BYTE -- the same ceiling
        /// every other stack in the game has. Money overflows into a second stack exactly like ammo does. It is
        /// a real limit rather than a chosen one: raising it means widening amount on the wire and in saves,
        /// which is a protocol change and not a balance tweak.</summary>
        public const int MaxPerStack = byte.MaxValue;
    }
}
