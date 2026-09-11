using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>Server authority for GESTURES (v42). Sibling of ServerForage: engine-free, so the same host
    /// serves SP-loopback and dedicated, and the rules it enforces are the ones in GestureRules rather than a
    /// second copy that can drift from the client's.
    ///
    /// WHY THE SERVER OWNS THIS AT ALL, when a wave is cosmetic. SURRENDER is not cosmetic: you cannot be
    /// handcuffed unless you are in it (UseableArrestStart), and a retail sentry holds fire on a player who is
    /// (InteractableSentry.cs:433). A client that could assert its own gesture could assert ARREST_START on
    /// itself and become uncuffable, or drop out of real handcuffs by claiming NONE. So the client ASKS, and
    /// what lands on everyone's screen is the server's answer.
    ///
    /// TWO-STEP, and deliberately. A request is PARKED here and resolved by the appearance publisher on its
    /// next tick, because the admission test needs the player's stance and whether their hands are full -- and
    /// those arrive on the input packet, which the publisher already holds and this does not. Resolving where
    /// the inputs are is one lookup; threading the inputs in here would be a second path to the same facts.</summary>
    public static class ServerGestures
    {
        static readonly Dictionary<ushort, EPlayerGesture> _pending = new();

        /// <summary>A client has asked for a gesture. Nothing is decided here -- see the class note.</summary>
        public static void Request(ushort playerId, EPlayerGesture want) => _pending[playerId] = want;

        public static bool TryTakePending(ushort playerId, out EPlayerGesture want)
        {
            if (!_pending.TryGetValue(playerId, out want)) return false;
            _pending.Remove(playerId);   // one request, one decision: a refused gesture is not retried forever
            return true;
        }

        /// <summary>Resolve a parked request against the player's real situation. Returns the gesture byte to
        /// publish, which may be what they were already showing (refused) or NONE (a STOP that landed).
        ///
        /// The Broadcast filter is the load-bearing bit: INVENTORY_START is a looping gesture and would
        /// otherwise latch onto the entity, so every other player would watch you rummage for as long as your
        /// bag was open. Filtering here rather than listing exceptions keeps the wire agreeing with the table.</summary>
        public static byte Resolve(byte currentGesture, EPlayerGesture want, EPlayerStance stance, bool handsFull,
                                   out EPlayerGesture oneShot)
        {
            oneShot = EPlayerGesture.NONE;
            var current = (EPlayerGesture)currentGesture;
            if (!GestureRules.CanRequest(want, current, stance, handsFull)) return currentGesture;
            var def = GestureRules.Of(want);
            // A ONE-SHOT changes no state -- Apply leaves it alone -- so it would vanish here entirely. Reported
            // back instead, for the caller to broadcast as an event: a wave has no state to sit in, and the
            // publisher is the only place that knows the request was allowed.
            if (!def.Loops && def.Broadcast && def.Clip != null) oneShot = want;
            var next = GestureRules.Apply(current, want);
            return (byte)(GestureRules.Of(next).Broadcast ? next : EPlayerGesture.NONE);
        }

        /// <summary>A gesture applied BY the server rather than asked for: a captor's cuffs, and later the
        /// pickup twitch. Skips the request whitelist for the same reason PlayerController.ForceGesture does --
        /// that list bounds what a CLIENT may ask, and this is not a client asking.</summary>
        public static byte Force(byte currentGesture, EPlayerGesture g)
            => (byte)GestureRules.Apply((EPlayerGesture)currentGesture, g);

        /// <summary>Drop a disconnecting player's parked request, so a reconnect with the same id cannot inherit
        /// a gesture the previous session asked for.</summary>
        public static void Forget(ushort playerId) => _pending.Remove(playerId);

        public static void ResetForTest() => _pending.Clear();
    }
}
