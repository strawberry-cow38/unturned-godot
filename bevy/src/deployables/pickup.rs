//! PlayerController.UpdateDeployPickup and its Interact release branch.
//! This state only requests an action; replicas remain until authority removes them.
const PICKUP_SECONDS: f32 = 1.;
const BAR_DEADZONE: f32 = 0.2;

#[derive(Default, Debug)]
pub(super) struct Hold {
    target: Option<u32>,
    elapsed: f32,
}
#[derive(Debug, PartialEq, Eq)]
pub(super) enum Tap {
    Toggle(u32),
    Configure(u32),
}
impl Hold {
    pub fn begin(&mut self, id: u32) {
        self.target = (id != 0).then_some(id);
        self.elapsed = 0.;
    }
    pub fn cancel(&mut self) {
        *self = Self::default();
    }
    pub fn target(&self) -> Option<u32> {
        self.target
    }
    pub fn progress(&self) -> f32 {
        let fraction = (self.elapsed / PICKUP_SECONDS).clamp(0., 1.);
        if self.target.is_some() && fraction >= BAR_DEADZONE {
            fraction
        } else {
            0.
        }
    }
    /// The caller supplies source validation (held, same focused object, alive,
    /// on foot, live body). Looking away cancels rather than transferring time.
    pub fn advance(&mut self, delta: f32, valid: bool) -> Option<u32> {
        if !valid {
            self.cancel();
            return None;
        }
        let id = self.target?;
        if delta.is_finite() {
            self.elapsed += delta.max(0.);
        }
        if self.elapsed < PICKUP_SECONDS {
            return None;
        }
        self.cancel();
        Some(id)
    }
    pub fn release(&mut self, can_toggle: bool, configurable: bool) -> Option<Tap> {
        let target = self.target;
        let short = self.elapsed < PICKUP_SECONDS;
        self.cancel();
        match (target, short, can_toggle, configurable) {
            (Some(id), true, true, _) => Some(Tap::Toggle(id)),
            (Some(id), true, false, true) => Some(Tap::Configure(id)),
            _ => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_deadzone_short_tap_and_completed_hold_do_not_repeat_or_toggle_after_pickup() {
        let mut hold = Hold::default();
        hold.begin(7);
        assert_eq!(hold.advance(0.199, true), None);
        assert_eq!(hold.progress(), 0.);
        hold.advance(0.002, true);
        assert!(hold.progress() > 0.2);
        assert_eq!(hold.release(true, true), Some(Tap::Toggle(7)));
        assert_eq!(hold.progress(), 0.);
        // A same-batch short press/release still toggles without flashing a bar.
        hold.begin(7);
        assert_eq!(hold.release(true, false), Some(Tap::Toggle(7)));
        hold.begin(7);
        assert_eq!(hold.advance(1.01, true), Some(7));
        assert_eq!(hold.release(true, true), None);
        assert_eq!(
            hold.advance(2., true),
            None,
            "holding after one request never auto-retries"
        );
    }
    #[test]
    fn focus_loss_cancels_time_and_a_new_target_starts_fresh() {
        let mut hold = Hold::default();
        hold.begin(4);
        hold.advance(0.9, true);
        hold.advance(0.01, false);
        assert_eq!(hold.target(), None);
        assert_eq!(hold.release(true, false), None);
        hold.begin(5);
        assert_eq!(hold.advance(0.2, true), None);
        assert_eq!(hold.release(false, true), Some(Tap::Configure(5)));
        hold.begin(0);
        assert_eq!(
            hold.advance(10., true),
            None,
            "world scenery without a replicated identity is not pocketable"
        );
    }
}
