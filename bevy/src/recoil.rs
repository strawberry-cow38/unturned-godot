//! PlayerController's cumulative recoil: one accepted shot moves the real aim.
//! Positional viewmodel shake is separate; the gun never takes a second roll.
use crate::entities::ItemCatalog;
use bevy::prelude::*;
use serde_json::Value;

#[derive(Resource)]
pub struct Recoil {
    epoch: u64,
    last_event: u64,
    shot: u32,
    idle: f32,
    pending: Vec2,
    stance: String,
    stance_time: f32,
    last_shot_tick: Option<u64>,
}
impl Default for Recoil {
    fn default() -> Self {
        Self {
            epoch: u64::MAX,
            last_event: 0,
            shot: 0,
            idle: 0.,
            pending: Vec2::ZERO,
            stance: String::new(),
            stance_time: 0.,
            last_shot_tick: None,
        }
    }
}
fn num(v: &Value, key: &str, default: f32) -> f32 {
    v[key].as_f64().unwrap_or(default as f64) as f32
}
/// Original GunDef.PatternAt, returned as horizontal/vertical degrees.
fn pattern(gun: &Value, shot: u32) -> Vec2 {
    let n = num(gun, "patternNodes", 30.);
    let climb = num(gun, "patternClimb", 0.);
    if n <= 0. || climb <= 0. {
        return Vec2::ZERO;
    }
    let shot = shot as f32;
    let t = shot.min(n) / n;
    let plateau = num(gun, "patternPlateau", 0.);
    let mut v = climb * t.powf(num(gun, "patternClimbExp", 1.55));
    if shot > n && plateau > 0. {
        let span = num(gun, "patternPlateauSpan", 0.93);
        v += plateau * (1. - (-(shot - n) / (n * if span > 0. { span } else { 0.93 })).exp());
    }
    let drift = num(gun, "patternDrift", 0.);
    let ds = num(gun, "patternDriftStart", 0.);
    let mut h = 0.;
    if drift != 0. {
        if shot > ds && n > ds {
            h = drift * ((shot.min(n) - ds) / (n - ds)).powf(num(gun, "patternDriftExp", 1.9));
        }
        if shot > n && plateau > 0. {
            h += drift * 0.45 * (1. - (-(shot - n) / (n * 1.33)).exp());
        }
    }
    h += num(gun, "patternWiggle", 0.) * (shot / 2.15).sin() * t.powf(1.15);
    let ss = num(gun, "patternSwayStart", 0.);
    if shot > ss {
        h += num(gun, "patternSway", 0.)
            * ((shot - ss) / 2.6).sin()
            * ((shot - ss) / (n - ss).max(1.)).min(1.);
    }
    Vec2::new(h, v)
}
fn roll(seq: u64, salt: u64) -> f32 {
    let x = seq
        .wrapping_add(salt)
        .wrapping_mul(6364136223846793005)
        .wrapping_add(1442695040888963407);
    (x >> 40) as f32 / 16777216.
}
impl Recoil {
    pub fn stance_multiplier(&self) -> f32 {
        if self.stance_time < 0.35 {
            return 1.;
        }
        match self.stance.as_str() {
            "crouch" => 0.85,
            "prone" => 0.7,
            _ => 1.,
        }
    }
    fn advance(&mut self, stance: &str, dt: f32) {
        if self.stance != stance {
            self.stance = stance.into();
            self.stance_time = 0.;
        } else {
            self.stance_time += dt;
        }
        if self.shot > 0 && self.last_shot_tick.is_none() {
            self.idle += dt;
            if self.idle >= 0.35 {
                self.shot = 0;
                self.idle = 0.;
            }
        }
    }
    fn kick(&mut self, gun: &Value, multiplier: f32, sequence: u64) {
        let delta = if num(gun, "patternClimb", 0.) > 0. {
            let previous = pattern(gun, self.shot);
            self.shot += 1;
            self.idle = 0.;
            pattern(gun, self.shot) - previous
        } else {
            let sample = |min, max, salt| {
                let lo = num(gun, min, 0.);
                lo + (num(gun, max, 0.) - lo) * roll(sequence, salt)
            };
            Vec2::new(
                sample("recoilMinX", "recoilMaxX", 4)
                    * if roll(sequence, 6) < 0.5 { -1. } else { 1. },
                sample("recoilMinY", "recoilMaxY", 5),
            )
        };
        self.pending += delta * multiplier * self.stance_multiplier();
    }
    fn drain(&mut self, dt: f32) -> Vec2 {
        let result = self.pending * (18. * dt).min(1.);
        self.pending -= result;
        result
    }
    pub fn update(
        &mut self,
        snapshot: &Value,
        catalog: &ItemCatalog,
        epoch: u64,
        dt: f32,
        paused: bool,
    ) -> Vec2 {
        if self.epoch != epoch {
            *self = Self {
                epoch,
                last_event: snapshot["events"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter_map(|e| e["seq"].as_u64())
                    .max()
                    .unwrap_or(0),
                ..default()
            };
        }
        if !paused {
            self.advance(snapshot["player"]["stance"].as_str().unwrap_or("stand"), dt);
        }
        for event in snapshot["events"].as_array().into_iter().flatten() {
            let sequence = event["seq"].as_u64().unwrap_or(0);
            if sequence <= self.last_event {
                continue;
            }
            self.last_event = sequence;
            if event["kind"] != "fire" || event["data"]["playerId"] != snapshot["playerId"] {
                continue;
            }
            let Some(asset) = event["data"]["gun"].as_str() else {
                continue;
            };
            let Some(item) = catalog
                .items
                .values()
                .find(|i| i.asset.as_deref() == Some(asset))
            else {
                continue;
            };
            let id = item.id;
            if let Some(tick) = event["tick"].as_u64() {
                if self
                    .last_shot_tick
                    .is_some_and(|last| tick.saturating_sub(last) >= 18)
                {
                    self.shot = 0;
                    self.idle = 0.;
                }
                self.last_shot_tick = Some(tick);
            }
            let held = &snapshot["weapon"]["held"];
            let quality = snapshot["inventory"]["pages"]
                .as_array()
                .into_iter()
                .flatten()
                .filter(|p| p["index"] == held["page"])
                .flat_map(|p| p["items"].as_array().into_iter().flatten())
                .find(|i| i["id"] == id && i["x"] == held["x"] && i["y"] == held["y"])
                .map_or(100., |i| num(i, "quality", 100.))
                / 100.;
            let skill = &snapshot["skills"]["groups"][0][1];
            let mastery = skill["mastery"]
                .as_f64()
                .map(|v| v as f32)
                .unwrap_or_else(|| num(skill, "level", 0.) / num(skill, "max", 7.).max(1.));
            let multiplier =
                (1. - mastery * 0.4) * if quality < 0.5 { 2. - quality * 2. } else { 1. };
            self.kick(&item.gun, multiplier, sequence);
        }
        if let (Some(tick), Some(last)) = (snapshot["tick"].as_u64(), self.last_shot_tick) {
            if tick.saturating_sub(last) >= 18 {
                self.shot = 0;
                self.idle = 0.;
                self.last_shot_tick = None;
            }
        }
        if paused { Vec2::ZERO } else { self.drain(dt) }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn aim_walks_the_whole_pattern_and_never_recovers_on_its_own() {
        let gun =
            json!({"patternNodes":30,"patternClimb":50.,"patternDrift":-3.9,"patternWiggle":-1.02});
        let mut r = Recoil::default();
        let mut aim = Vec2::ZERO;
        for shot in 1..=30 {
            r.advance("stand", 0.1);
            r.kick(&gun, 1., shot);
            aim += r.drain(0.1);
        }
        assert!((aim - pattern(&gun, 30)).length() < 0.0001);
        let final_aim = aim;
        for _ in 0..120 {
            r.advance("stand", 1. / 60.);
            aim += r.drain(1. / 60.);
        }
        assert_eq!(aim, final_aim);
        assert_eq!(r.shot, 0);
        r.kick(&gun, 1., 31);
        assert!((r.drain(0.1) - pattern(&gun, 1)).length() < 0.0001);
    }
    #[test]
    fn stance_requires_settling_and_legacy_roll_uses_vertical_y() {
        let gun = json!({"recoilMinY":4.,"recoilMaxY":4.,"recoilMinX":0.,"recoilMaxX":0.});
        let mut r = Recoil::default();
        r.advance("prone", 0.1);
        r.kick(&gun, 0.6 * 1.5, 1);
        assert!((r.drain(0.1).y - 3.6).abs() < 0.0001);
        r.advance("prone", 0.36);
        r.kick(&gun, 1., 2);
        assert!((r.drain(0.1).y - 2.8).abs() < 0.0001);
        r.advance("crouch", 0.01);
        r.kick(&gun, 1., 3);
        assert_eq!(r.drain(0.1).y, 4.);
    }
    #[test]
    fn plateau_changes_only_the_tail_and_keeps_late_belt_rounds_climbing() {
        let mut gun = json!({"patternNodes":20,"patternClimb":35.,"patternPlateau":40.,"patternPlateauSpan":2.});
        let end = pattern(&gun, 20);
        let late = pattern(&gun, 100).y - pattern(&gun, 99).y;
        assert!(late > 0.1);
        assert!(pattern(&gun, 100).y < 85.);
        gun["patternPlateau"] = json!(0.);
        assert_eq!(pattern(&gun, 20), end);
        assert_eq!(pattern(&gun, 100).y, end.y);
    }
    #[test]
    fn duplicate_facts_remote_shots_and_loaded_history_do_not_kick_twice() {
        let item=serde_json::from_value(json!({"id":4,"name":"test","type":"Gun","asset":"eaglefire","gun":{"recoilMinY":3.,"recoilMaxY":3.}})).unwrap();
        let mut catalog = ItemCatalog::default();
        catalog.items.insert(4, item);
        let mut r = Recoil::default();
        let mut s = json!({"playerId":1,"events":[],"player":{"stance":"stand"}});
        r.update(&s, &catalog, 1, 0.1, false);
        s["events"] = json!([{"seq":1,"kind":"fire","data":{"playerId":1,"gun":"eaglefire"}}]);
        assert_eq!(r.update(&s, &catalog, 1, 0.1, false), Vec2::new(0., 3.));
        assert_eq!(r.update(&s, &catalog, 1, 0.1, false), Vec2::ZERO);
        s["events"][0]["seq"] = json!(2);
        s["events"][0]["data"]["playerId"] = json!(2);
        assert_eq!(r.update(&s, &catalog, 1, 0.1, false), Vec2::ZERO);
        s["events"][0]["data"]["playerId"] = json!(1);
        s["events"][0]["seq"] = json!(3);
        assert_eq!(r.update(&s, &catalog, 2, 0.1, false), Vec2::ZERO);
        s["events"][0]["seq"] = json!(4);
        assert_eq!(r.update(&s, &catalog, 2, 0.1, true), Vec2::ZERO);
        s["events"] = json!([]); // the bridge may acknowledge facts while the menu is open
        assert_eq!(r.update(&s, &catalog, 2, 0.1, false), Vec2::new(0., 3.));
    }
    #[test]
    fn batched_fixed_ticks_keep_one_pattern_at_low_render_rates() {
        let gun = json!({"patternNodes":30,"patternClimb":50.});
        let item = serde_json::from_value(
            json!({"id":4,"name":"test","type":"Gun","asset":"eaglefire","gun":gun}),
        )
        .unwrap();
        let mut catalog = ItemCatalog::default();
        catalog.items.insert(4, item);
        let mut r = Recoil::default();
        let mut s = json!({"tick":0,"playerId":1,"events":[],"player":{"stance":"stand"}});
        r.update(&s, &catalog, 1, 0.25, false);
        let mut aim = Vec2::ZERO;
        for frame in 0..6 {
            s["tick"] = json!(frame * 10 + 5);
            s["events"]=json!([0,1].map(|i|json!({"seq":frame*2+i+1,"tick":frame*10+i*5,"kind":"fire","data":{"playerId":1,"gun":"eaglefire"}})));
            aim += r.update(&s, &catalog, 1, 0.25, false);
        }
        assert!((aim - pattern(&gun, 12)).length() < 0.0001);
        s["tick"] = json!(80);
        s["events"] = json!([]);
        r.update(&s, &catalog, 1, 0.25, false);
        s["events"] =
            json!([{"seq":13,"tick":80,"kind":"fire","data":{"playerId":1,"gun":"eaglefire"}}]);
        assert!((r.update(&s, &catalog, 1, 0.25, false) - pattern(&gun, 1)).length() < 0.0001);
    }
}
