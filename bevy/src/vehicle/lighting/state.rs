//! Original Vehicle.ApplyBeam/ApplyLampState and siren/beacon clocks.
use serde_json::Value;

#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
pub(super) struct Accepted {
    pub flags: u8,
    pub high: bool,
    pub pattern: u8,
    pub broken: u8,
}
impl Accepted {
    pub fn read(row: &Value) -> Self {
        Self {
            flags: row["flags"].as_u64().unwrap_or(0) as u8,
            high: row["highBeam"].as_bool().unwrap_or(false),
            pattern: row["lightbarPattern"].as_u64().unwrap_or(0).min(2) as u8,
            broken: row["brokenLampMask"].as_u64().unwrap_or(0) as u8 & 127,
        }
    }
    pub fn wreck(self) -> bool {
        self.flags & 32 != 0
    }
    pub fn head(self) -> bool {
        self.flags & 2 != 0 && !self.wreck()
    }
    pub fn tail(self) -> bool {
        self.flags & 4 != 0 && !self.wreck()
    }
    pub fn siren(self) -> bool {
        self.flags & 8 != 0 && !self.wreck()
    }
    pub fn broken(self, slot: u8) -> bool {
        self.broken & (1 << slot) != 0
    }
}

pub(super) struct Clock {
    pub accepted: Accepted,
    pub primary_head_energy: f32,
    pub siren_time: f32,
    pub siren_lit: [bool; 2],
    pub siren_pitch: f32,
    pub siren_db: f32,
    pub beacon_time: f32,
    pub beacon_lit: bool,
    pub nav_on: bool,
    pub dark_bar: [bool; 2],
}
impl Default for Clock {
    fn default() -> Self {
        Self {
            accepted: Accepted::default(),
            primary_head_energy: 2.,
            siren_time: 0.,
            siren_lit: [false; 2],
            siren_pitch: 1.,
            siren_db: 2.,
            beacon_time: 0.,
            beacon_lit: false,
            nav_on: false,
            dark_bar: [false; 2],
        }
    }
}
impl Clock {
    pub fn adopt(&mut self, state: Accepted) {
        // ApplyBeam changes only the source's aliased headlight material. A later
        // ApplyLampState (head/tail transition or break/repair) resets both to2.
        if state.high != self.accepted.high && state.head() {
            self.primary_head_energy = if state.high { 3.2 } else { 2. };
        }
        if state.head() != self.accepted.head()
            || state.tail() != self.accepted.tail()
            || state.broken != self.accepted.broken
        {
            self.primary_head_energy = 2.;
        }
        // Source lightbar repair restores emission but does not restore the
        // albedo that ApplyLampState darkened on break.
        for side in 0..2 {
            self.dark_bar[side] |= state.broken(side as u8 + 4);
        }
        self.accepted = state;
    }
    pub fn step(&mut self, dt: f32, heli: bool, spool: f32, health: f32, pitches: &[f32]) {
        let state = self.accepted;
        self.nav_on = heli && state.flags & 1 != 0 && !state.wreck() && health > 0.;
        let armed = heli && spool > 0.02 && !state.wreck();
        self.beacon_time = if armed {
            (self.beacon_time + dt) % 1.4
        } else {
            0.
        };
        self.beacon_lit = armed && self.beacon_time < 0.12;
        self.siren_lit = [false; 2];
        if state.siren() {
            let pitch = pitches.get(state.pattern as usize).copied().unwrap_or(1.);
            // Audio uses the pre-increment clock in the original physics loop.
            self.siren_pitch = if state.broken(6) {
                (pitch
                    + (self.siren_time * 23.).sin() * 0.18
                    + (self.siren_time * 5.3).sin() * 0.12)
                    .max(0.3)
            } else {
                pitch
            };
            self.siren_db = if state.broken(6) {
                if self.siren_time % 1.7 > 1.35 {
                    -40.
                } else {
                    -12.
                }
            } else {
                2.
            };
            self.siren_time += dt;
            let (left, right) = match state.pattern {
                1 => {
                    let t = self.siren_time % 0.8;
                    let left = t < 0.4;
                    let u = if left { t } else { t - 0.4 };
                    let pop = u < 0.07 || (u >= 0.15 && u < 0.22);
                    (left && pop, !left && pop)
                }
                2 => {
                    let a = self.siren_time % 0.24 < 0.12;
                    (a, !a)
                }
                _ => {
                    let a = self.siren_time % 0.66 < 0.33;
                    (a, !a)
                }
            };
            self.siren_lit = [left && !state.broken(4), right && !state.broken(5)];
        }
    }
    pub fn audible(&self) -> bool {
        self.accepted.siren() && !(4..=6).all(|slot| self.accepted.broken(slot))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_siren_patterns_damage_dropout_and_off_clock_are_preserved() {
        let pitches = [1., 1.12, 0.88];
        let mut clock = Clock::default();
        clock.adopt(Accepted {
            flags: 8,
            ..Default::default()
        });
        clock.step(0.2, false, 0., 100., &pitches);
        assert_eq!(clock.siren_lit, [true, false]);
        clock.step(0.2, false, 0., 100., &pitches);
        assert_eq!(clock.siren_lit, [false, true]);
        clock.adopt(Accepted {
            flags: 8,
            pattern: 1,
            ..Default::default()
        });
        clock.step(0.02, false, 0., 100., &pitches);
        assert_eq!(clock.siren_lit, [false, true]);
        assert_eq!(clock.siren_pitch, 1.12);
        clock.adopt(Accepted {
            flags: 0,
            ..Default::default()
        });
        let t = clock.siren_time;
        clock.step(2., false, 0., 100., &pitches);
        assert_eq!(clock.siren_time, t);
        assert!(!clock.audible());
        clock.adopt(Accepted {
            flags: 8,
            broken: 64,
            ..Default::default()
        });
        clock.siren_time = 1.4;
        clock.step(0.02, false, 0., 100., &pitches);
        assert_eq!(clock.siren_db, -40.);
        assert!(clock.audible());
        clock.adopt(Accepted {
            flags: 8,
            broken: 112,
            ..Default::default()
        });
        clock.step(0.02, false, 0., 100., &pitches);
        assert_eq!(clock.siren_lit, [false, false]);
        assert!(!clock.audible());
    }
    #[test]
    fn source_beacon_follows_disc_but_navigation_follows_engine_and_wreck_is_dark() {
        let mut clock = Clock::default();
        clock.adopt(Accepted {
            flags: 1,
            ..Default::default()
        });
        clock.step(0.02, true, 0.1, 100., &[]);
        assert!(clock.nav_on && clock.beacon_lit);
        clock.adopt(Accepted::default());
        clock.step(0.02, true, 0.1, 100., &[]);
        assert!(!clock.nav_on && clock.beacon_lit);
        clock.step(0.1, true, 0.1, 100., &[]);
        assert!(!clock.beacon_lit);
        clock.step(0.02, true, 0.02, 100., &[]);
        assert_eq!(clock.beacon_time, 0.);
        clock.adopt(Accepted {
            flags: 1 | 2 | 4 | 8 | 32,
            ..Default::default()
        });
        clock.step(0.02, true, 1., 100., &[]);
        assert!(!clock.nav_on && !clock.beacon_lit && !clock.audible());
        assert!(!clock.accepted.head() && !clock.accepted.tail());
    }
    #[test]
    fn high_beam_first_lens_and_later_lamp_state_follow_source_order() {
        let mut clock = Clock::default();
        clock.adopt(Accepted {
            flags: 6,
            ..Default::default()
        });
        clock.adopt(Accepted {
            flags: 6,
            high: true,
            ..Default::default()
        });
        assert_eq!(clock.primary_head_energy, 3.2);
        clock.adopt(Accepted {
            flags: 6,
            high: true,
            broken: 2,
            ..Default::default()
        });
        assert_eq!(clock.primary_head_energy, 2.);
        clock.adopt(Accepted {
            flags: 6,
            high: true,
            ..Default::default()
        });
        assert_eq!(clock.primary_head_energy, 2.);
    }
}
