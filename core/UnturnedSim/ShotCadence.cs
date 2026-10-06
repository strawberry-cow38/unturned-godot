using System;

namespace UnturnedSim
{
    /// <summary>
    /// Opt-in firearm cadence for a 50 Hz simulation: a period is exactly 3000/RPM ticks.
    /// Each instance owns its phase. Call Accept only after all other shot checks succeed.
    /// Timely shots preserve phase; shots after the integer due tick discard missed time.
    /// This schedules simulation ticks, not network arrivals or physical weapon timing.
    /// </summary>
    public struct ShotCadence
    {
        private bool active;
        private long lastShotTick;
        // Exact mixed rational deadline: wholeTick + numerator/denominator.
        // Keeping the whole part separate avoids multiplying an absolute tick by RPM.
        private long wholeTick;
        private int numerator;
        private int denominator;
        private int rpm; // zero means the most recent accepted shot used legacy cadence

        public bool Active => active;

        /// <summary>Valid RPM is 1..3000. Querying does not activate or change phase.</summary>
        public bool CanFire(long tick, int RPM)
        {
            ValidateRPM(RPM);
            if (!active)
                return true;
            if (IsCoolingDown(tick))
                return false;
            // A rate change cannot shorten either the existing deadline or the new
            // period measured from the last shot, even if the new rate is faster.
            return rpm == RPM || tick >= checked(lastShotTick + PeriodCeiling(RPM));
        }

        /// <summary>Tests the stored cooldown, including the one-shot-per-tick guard.</summary>
        public bool IsCoolingDown(long tick)
        {
            return active && (tick <= lastShotTick || tick < DeadlineCeiling());
        }

        /// <summary>
        /// Rejects premature acceptance with InvalidOperationException, without mutation.
        /// Tick arithmetic is checked; an unrepresentable future deadline throws OverflowException.
        /// </summary>
        public void AcceptShot(long tick, int RPM)
        {
            if (!CanFire(tick, RPM))
                throw new InvalidOperationException("Shot cadence is still cooling down.");

            long nextWhole;
            int nextNumerator;
            if (active && rpm == RPM && tick == DeadlineCeiling())
            {
                int sum = numerator + 3000;
                nextWhole = checked(wholeTick + sum / RPM);
                nextNumerator = sum % RPM;
            }
            else
            {
                nextWhole = checked(tick + 3000 / RPM);
                nextNumerator = 3000 % RPM;
            }
            // Ensure the integer due tick is representable before committing any state.
            _ = checked(nextWhole + (nextNumerator == 0 ? 0 : 1));
            wholeTick = nextWhole;
            numerator = nextNumerator;
            denominator = RPM;
            rpm = RPM;
            lastShotTick = tick;
            active = true;
        }

        /// <summary>
        /// Bridge used only after RPM mode has activated. Inactive instances remain untouched,
        /// preserving caller-owned legacy-only behavior. Active bridges require the old cooldown
        /// to expire and then store tick + intervalTicks; zero intervals still forbid same-tick shots.
        /// </summary>
        public void AcceptLegacyShot(long tick, int intervalTicks)
        {
            if (intervalTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(intervalTicks));
            if (!active)
                return;
            if (IsCoolingDown(tick))
                throw new InvalidOperationException("Shot cadence is still cooling down.");

            long nextWhole = checked(tick + intervalTicks);
            wholeTick = nextWhole;
            numerator = 0;
            denominator = 1;
            rpm = 0;
            lastShotTick = tick;
        }

        private long DeadlineCeiling()
        {
            return checked(wholeTick + (numerator + denominator - 1) / denominator);
        }

        private static int PeriodCeiling(int RPM) => (3000 + RPM - 1) / RPM;

        private static void ValidateRPM(int RPM)
        {
            if (RPM < 1 || RPM > 3000)
                throw new ArgumentOutOfRangeException(nameof(RPM), "RPM must be between 1 and 3000.");
        }
    }
}
