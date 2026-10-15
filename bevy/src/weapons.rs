//! Native trigger edges; accepted damage/ammunition still belongs to ServerCombat.
use bevy::prelude::*;
use serde_json::Value;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Intent {
    Fire,
    Melee(bool),
    Reload,
    Selector(i32),
}
#[derive(Default)]
pub struct Buttons {
    pub fire: bool,
    pub press: bool,
    pub strong: bool,
    pub reload: bool,
    pub selector: bool,
}
#[derive(Resource, Default)]
pub struct Trigger {
    held: u16,
    selection: Value,
    last_event: u64,
    burst_wait: f32,
    burst_left: u32,
}
pub fn modes(gun: &Value) -> Vec<i32> {
    let mut modes = Vec::new();
    for (key, mode) in [("hasSafety", 0), ("hasSemi", 1), ("hasAuto", 2)] {
        if gun[key].as_bool().unwrap_or(false) {
            modes.push(mode);
        }
    }
    if gun["burstCount"].as_u64().unwrap_or(0) > 0 {
        modes.push(3);
    }
    if modes.is_empty() {
        modes.push(1);
    }
    modes
}
impl Trigger {
    pub fn acknowledge(&mut self, snapshot: &Value) {
        for event in snapshot["events"].as_array().into_iter().flatten() {
            let sequence = event["seq"].as_u64().unwrap_or(0);
            if sequence <= self.last_event {
                continue;
            }
            self.last_event = sequence;
            if self.burst_left > 0
                && event["kind"] == "fire"
                && event["data"]["playerId"] == snapshot["playerId"]
            {
                self.burst_left -= 1;
                if self.burst_left == 0 {
                    self.burst_wait = 0.2;
                }
            }
        }
    }
    pub fn cancel(&mut self) {
        self.burst_left = 0;
    }
    pub fn step(
        &mut self,
        dt: f32,
        held: u16,
        gun: &Value,
        melee: &Value,
        weapon: &Value,
        buttons: Buttons,
        ready: bool,
    ) -> Vec<Intent> {
        let selection = if weapon["selectionVersion"].is_null() {
            weapon["held"].clone()
        } else {
            weapon["selectionVersion"].clone()
        };
        if self.held != held || self.selection != selection {
            *self = Self {
                held,
                selection,
                last_event: self.last_event,
                ..default()
            };
        }
        self.burst_wait = (self.burst_wait - dt).max(0.);
        if !ready {
            self.cancel();
            return Vec::new();
        }
        if weapon["broken"].as_bool().unwrap_or(false) || weapon["canFire"].as_bool() == Some(false)
        {
            self.cancel();
        }
        if gun.is_null() {
            if melee["repair"].as_bool().unwrap_or(false) {
                return Vec::new();
            }
            if held == 0 || !melee.is_null() {
                let repeated = melee["repeated"].as_bool().unwrap_or(false);
                if buttons.press || (repeated && buttons.fire) {
                    return vec![Intent::Melee(false)];
                }
                if buttons.strong && !repeated && !melee["light"].as_bool().unwrap_or(false) {
                    return vec![Intent::Melee(true)];
                }
            }
            return Vec::new();
        }
        let available = modes(gun);
        let mode = weapon["firemode"]
            .as_i64()
            .map(|n| n as i32)
            .filter(|m| available.contains(m))
            .unwrap_or_else(|| {
                if available.contains(&1) {
                    1
                } else if available.contains(&2) {
                    2
                } else {
                    available[0]
                }
            });
        if buttons.selector {
            self.cancel();
            return vec![Intent::Selector(
                available[(available.iter().position(|m| *m == mode).unwrap_or(0) + 1)
                    % available.len()],
            )];
        }
        if buttons.reload {
            self.cancel();
            return vec![Intent::Reload];
        }
        if mode == 0
            || weapon["rechambering"].as_bool().unwrap_or(false)
            || weapon["broken"].as_bool().unwrap_or(false)
            || weapon["canFire"].as_bool() == Some(false)
        {
            self.cancel();
            return Vec::new();
        }
        // Source pump reloads can be interrupted after a shell has arrived.
        if weapon["reloading"].as_bool().unwrap_or(false)
            && !gun["shellReload"].as_bool().unwrap_or(false)
        {
            self.cancel();
            return Vec::new();
        }
        if mode == 3 && buttons.press && self.burst_left == 0 && self.burst_wait <= 0. {
            self.burst_left = gun["burstCount"].as_u64().unwrap_or(3) as u32;
        }
        let wants = (mode == 1 && buttons.press)
            || (mode == 2 && buttons.fire)
            || (mode == 3 && self.burst_left > 0);
        if !wants {
            return Vec::new();
        }
        if weapon["ammo"].as_i64().unwrap_or(0) <= 0 {
            self.cancel();
            return Vec::new();
        }
        // Cadence is enforced by the original server's integer/rational clock.
        // A burst spends a round only when its accepted fire event comes back,
        // so rejected intents cannot truncate the burst or lose fractional RPM.
        vec![Intent::Fire]
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn replacing_an_identical_rifle_or_breaking_it_cancels_queued_burst() {
        let gun = json!({"hasSemi":true,"burstCount":3});
        let mut weapon = json!({"firemode":3,"ammo":30,"selectionVersion":1});
        let mut trigger = Trigger::default();
        assert_eq!(
            trigger.step(
                0.02,
                4,
                &gun,
                &Value::Null,
                &weapon,
                Buttons {
                    press: true,
                    ..default()
                },
                true
            ),
            vec![Intent::Fire]
        );
        weapon["selectionVersion"] = json!(2);
        assert!(
            trigger
                .step(
                    0.02,
                    4,
                    &gun,
                    &Value::Null,
                    &weapon,
                    Buttons::default(),
                    true
                )
                .is_empty()
        );
        trigger.step(
            0.02,
            4,
            &gun,
            &Value::Null,
            &weapon,
            Buttons {
                press: true,
                ..default()
            },
            true,
        );
        weapon["broken"] = json!(true);
        assert!(
            trigger
                .step(
                    0.02,
                    4,
                    &gun,
                    &Value::Null,
                    &weapon,
                    Buttons::default(),
                    true
                )
                .is_empty()
        );
        weapon["broken"] = json!(false);
        assert!(
            trigger
                .step(
                    0.02,
                    4,
                    &gun,
                    &Value::Null,
                    &weapon,
                    Buttons::default(),
                    true
                )
                .is_empty()
        );
    }
    #[test]
    fn semi_holding_does_not_repeat_and_burst_finishes_after_release() {
        let gun = json!({"hasSafety":true,"hasSemi":true,"burstCount":3,"firerate":4});
        let mut trigger = Trigger::default();
        let mut shots = 0;
        for tick in 0..50 {
            shots += trigger
                .step(
                    0.02,
                    4,
                    &gun,
                    &Value::Null,
                    &json!({"firemode":1,"ammo":30}),
                    Buttons {
                        fire: true,
                        press: tick == 0,
                        ..default()
                    },
                    true,
                )
                .iter()
                .filter(|i| **i == Intent::Fire)
                .count();
        }
        assert_eq!(shots, 1);
        let mut times = Vec::new();
        let mut sequence = 0;
        for tick in 0..50 {
            let requested = trigger
                .step(
                    0.02,
                    4,
                    &gun,
                    &Value::Null,
                    &json!({"firemode":3,"ammo":30}),
                    Buttons {
                        press: tick == 0,
                        ..default()
                    },
                    true,
                )
                .contains(&Intent::Fire);
            if requested && tick % 5 == 0 {
                times.push(tick);
                sequence += 1;
                let fact = json!({"playerId":1,"events":[{"seq":sequence,"kind":"fire","data":{"playerId":1}}]});
                trigger.acknowledge(&fact);
                trigger.acknowledge(&fact);
            }
        }
        assert_eq!(times, vec![0, 5, 10]);
    }
    #[test]
    fn selector_uses_real_modes_and_reload_cancels_queued_burst() {
        let gun = json!({"hasSafety":true,"hasSemi":true,"burstCount":3,"firerate":4});
        let mut trigger = Trigger::default();
        let weapon = json!({"firemode":3,"ammo":30});
        assert_eq!(
            trigger.step(
                0.02,
                4,
                &gun,
                &Value::Null,
                &weapon,
                Buttons {
                    selector: true,
                    ..default()
                },
                true
            ),
            vec![Intent::Selector(0)]
        );
        trigger.step(
            0.02,
            4,
            &gun,
            &Value::Null,
            &weapon,
            Buttons {
                press: true,
                ..default()
            },
            true,
        );
        assert_eq!(
            trigger.step(
                0.02,
                4,
                &gun,
                &Value::Null,
                &weapon,
                Buttons {
                    reload: true,
                    ..default()
                },
                true
            ),
            vec![Intent::Reload]
        );
        assert!(
            trigger
                .step(1., 4, &gun, &Value::Null, &weapon, Buttons::default(), true)
                .is_empty()
        );
        assert_eq!(
            trigger.step(
                0.02,
                0,
                &Value::Null,
                &Value::Null,
                &Value::Null,
                Buttons {
                    strong: true,
                    ..default()
                },
                true
            ),
            vec![Intent::Melee(true)]
        );
    }
}
