namespace SDG.Unturned
{
    /// <summary>Which parts of a death line the server operator has opted into. Every field defaults to
    /// FALSE, so an unconfigured server says "playername died" and nothing more -- strawberry 2026-09-18:
    /// "by default just 'playername died'". They are INDEPENDENT rather than a ladder: a server may want the
    /// weapon and the range without naming the killer (anonymised PvP reads fine that way), so the weapon and
    /// distance clauses are gated on a killer EXISTING, never on the killer being NAMED.</summary>
    public struct DeathMessageOptions
    {
        public bool ShowKiller;
        public bool ShowWeapon;
        public bool ShowDistance;
        public bool ShowLocation;
    }

    /// <summary>Formats the one chat line a death produces. Pure: no engine, no registry, no clock -- every
    /// fact arrives as an argument, so the whole matrix is L0-testable and the caller owns the lookups.
    ///
    /// ⚠ DISTANCE IS NOT `sourcePos`. ServerCombat.ApplyPlayerDamage already carries a `sourcePos`, and it
    /// looks exactly like the thing to measure from -- but it is the source of the HIT, not the killer. For a
    /// bullet that is the bullet's position at impact, i.e. standing on the victim, so a distance taken from
    /// it reads ~0 m on every kill and looks entirely plausible while being wrong the same amount every time.
    /// The caller must pass the ATTACKER's own position instead. Uniform error is the instrument.</summary>
    public static class DeathMessageRules
    {
        /// <summary>Shown when a name is missing rather than printing an empty gap or a raw id.</summary>
        public const string UnknownName = "a player";

        public static string Format(
            string victimName,
            string killerName,
            string weaponName,
            bool hasKiller,
            float distanceMetres,
            bool hasLocation,
            float x, float y, float z,
            in DeathMessageOptions opt)
        {
            string victim = string.IsNullOrWhiteSpace(victimName) ? UnknownName : victimName.Trim();
            var sb = new System.Text.StringBuilder(victim);

            if (!hasKiller)
            {
                // Environmental: fall, starvation, radiation, out-of-bounds. A weapon or a range would be a
                // lie here, so neither clause is reachable -- only the location can apply.
                sb.Append(" died");
            }
            else
            {
                sb.Append(" was killed");
                if (opt.ShowKiller)
                {
                    string killer = string.IsNullOrWhiteSpace(killerName) ? UnknownName : killerName.Trim();
                    sb.Append(" by ").Append(killer);
                }
                if (opt.ShowWeapon && !string.IsNullOrWhiteSpace(weaponName))
                    sb.Append(" with ").Append(weaponName.Trim());
                if (opt.ShowDistance)
                {
                    // Negative or non-finite would mean the caller could not resolve a position; say nothing
                    // rather than print "from -1m", which reads as a real measurement.
                    if (distanceMetres >= 0f && !float.IsNaN(distanceMetres) && !float.IsInfinity(distanceMetres))
                        sb.Append(" from ").Append(System.Math.Round(distanceMetres, System.MidpointRounding.AwayFromZero)
                            .ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append('m');
                }
            }

            if (opt.ShowLocation && hasLocation)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                // ⚠ AwayFromZero, NOT the default. .NET's Math.Round is banker's rounding (half-to-even), so a
                // player standing at y=2.5 prints "2" -- correct, and surprising to every human reading it in
                // chat. These are numbers people eyeball against a map, so round the way they expect. Caught
                // by the test matrix below, which had assumed away-from-zero without either of us deciding it.
                sb.Append(" at ")
                  .Append(System.Math.Round(x, System.MidpointRounding.AwayFromZero).ToString("0", ci)).Append(", ")
                  .Append(System.Math.Round(y, System.MidpointRounding.AwayFromZero).ToString("0", ci)).Append(", ")
                  .Append(System.Math.Round(z, System.MidpointRounding.AwayFromZero).ToString("0", ci));
            }

            return sb.ToString();
        }
    }
}
