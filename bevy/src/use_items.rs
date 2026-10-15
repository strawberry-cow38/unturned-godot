//! Source hand-use timelines. Only the server spends items or changes vitals.
use crate::{entities::ItemVisual, weapons::Buttons};
use bevy::prelude::*;
use serde_json::Value;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Cell {
    pub id: u16,
    pub page: u8,
    pub x: u8,
    pub y: u8,
}
impl Cell {
    pub fn held(snapshot: &Value) -> Option<Self> {
        let held = &snapshot["weapon"]["held"];
        let id = held["id"].as_u64()? as u16;
        (id != 0).then(|| Self {
            id,
            page: held["page"].as_u64().unwrap_or(0) as u8,
            x: held["x"].as_u64().unwrap_or(0) as u8,
            y: held["y"].as_u64().unwrap_or(0) as u8,
        })
    }
}
fn cells(snapshot: &Value) -> impl Iterator<Item = (Cell, &Value)> {
    snapshot["inventory"]["pages"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|p| {
            let page = p["index"].as_u64()? as u8;
            (page < 7).then_some((page, p))
        })
        .flat_map(|(page, p)| {
            p["items"].as_array().into_iter().flatten().map(move |i| {
                (
                    Cell {
                        id: i["id"].as_u64().unwrap_or(0) as u16,
                        page,
                        x: i["x"].as_u64().unwrap_or(0) as u8,
                        y: i["y"].as_u64().unwrap_or(0) as u8,
                    },
                    i,
                )
            })
        })
}
fn count(snapshot: &Value, id: u16) -> u32 {
    cells(snapshot)
        .filter(|(c, _)| c.id == id)
        .map(|(_, i)| i["amount"].as_u64().unwrap_or(1) as u32)
        .sum()
}
#[derive(Message, Clone, Debug, PartialEq)]
pub enum HandEvent {
    Start(u16),
    Released(u16),
    Finish(u16),
    Cancel,
}
#[derive(Debug, PartialEq)]
pub enum UseIntent {
    Consume(Cell),
    Throw { item: u16, strong: bool },
    Hold(Cell),
    Tool(bool),
    Frozen,
}
#[derive(Clone, Copy, Debug)]
struct Use {
    cell: Cell,
    elapsed: f32,
    duration: f32,
    release_at: f32,
    throw: bool,
    strong: bool,
    armed: bool,
    sent: bool,
    sent_at: f32,
    before: u32,
    selection: u64,
}
#[derive(Resource, Default)]
pub struct HandUse {
    active: Option<Use>,
    last: Option<Cell>,
    revert: Option<Cell>,
    pending: Option<Cell>,
    last_kind: u8,
    pending_age: f32,
    tool_active: bool,
}
impl HandUse {
    pub fn busy(&self) -> bool {
        self.active.is_some()
    }
    pub fn visual_item(&self) -> Option<u16> {
        self.active.map(|u| u.cell.id)
    }
    pub fn hide_item(&self) -> bool {
        self.active.is_some_and(|u| u.throw && u.sent)
    }
    pub fn cancel(&mut self, events: &mut Vec<HandEvent>) {
        if self.active.take().is_some() {
            events.push(HandEvent::Cancel);
        }
        self.pending = None;
    }
    /// Runs at the same fixed rate as the authoritative inventory/movement host.
    pub fn step(
        &mut self,
        dt: f32,
        snapshot: &Value,
        item: Option<&ItemVisual>,
        motion: &Value,
        buttons: &Buttons,
        ready: bool,
        allowed: bool,
    ) -> (Vec<UseIntent>, Vec<HandEvent>) {
        let mut intents = Vec::new();
        let mut events = Vec::new();
        let held = Cell::held(snapshot);
        let selection = snapshot["weapon"]["selectionVersion"].as_u64().unwrap_or(0);
        let kind = item.map_or(0, |i| {
            if i.is_consumable {
                1
            } else if !i.throwable.is_null() {
                2
            } else {
                0
            }
        });
        if self.active.is_some_and(|u| {
            (Some(u.cell) != held || u.selection != selection) && !(u.sent && held.is_none())
        }) {
            self.cancel(&mut events);
        }
        if held != self.last {
            if item.is_some_and(|i| i.is_consumable || !i.throwable.is_null())
                && self.active.is_none()
            {
                // Repeated re-equips retain the original fallback, including a gun's exact cell.
                if self.pending != held && self.last_kind != kind {
                    self.revert = self.last;
                }
            }
            if held.is_some() && self.pending != held {
                self.pending = None;
            }
            self.last = held;
            self.last_kind = kind;
        }
        let tool = allowed
            && ready
            && buttons.fire
            && item.is_some_and(|i| i.melee["repair"].as_bool() == Some(true));
        if tool || self.tool_active {
            intents.push(UseIntent::Tool(tool));
        }
        self.tool_active = tool;
        if !allowed {
            self.cancel(&mut events);
            return (intents, events);
        }
        if self.pending == held {
            self.pending = None;
        }
        if self.pending.is_some() {
            self.pending_age += dt;
            if self.pending_age > 1. {
                self.pending = None;
            }
        }
        if self.active.is_none()
            && self.pending.is_none()
            && ready
            && (buttons.press || buttons.strong)
        {
            if let (Some(cell), Some(item)) = (held, item) {
                let throw = !item.throwable.is_null();
                if throw || (item.is_consumable && buttons.press) {
                    if !throw
                        && cells(snapshot)
                            .any(|(c, i)| c == cell && i["frozen"].as_u64().unwrap_or(0) > 0)
                    {
                        intents.push(UseIntent::Frozen);
                        return (intents, events);
                    }
                    let duration = if throw {
                        motion["useSeconds"]
                            .as_f64()
                            .filter(|v| *v >= 0.1)
                            .unwrap_or(1.)
                    } else {
                        item.consume_animation["seconds"]
                            .as_f64()
                            .filter(|v| *v > 0.)
                            .unwrap_or(2.2)
                    } as f32;
                    self.active = Some(Use {
                        cell,
                        elapsed: 0.,
                        duration,
                        release_at: duration
                            * motion["releaseFraction"].as_f64().unwrap_or(0.6) as f32,
                        throw,
                        strong: !buttons.strong,
                        armed: throw && !buttons.strong,
                        sent: false,
                        sent_at: 0.,
                        before: count(snapshot, cell.id),
                        selection,
                    });
                    events.push(HandEvent::Start(cell.id));
                }
            }
        }
        if let Some(active) = &mut self.active {
            active.elapsed += dt;
            if active.armed && !buttons.fire {
                active.armed = false;
            }
            if !active.sent
                && ((!active.throw && active.elapsed >= active.duration)
                    || (active.throw && !active.armed && active.elapsed >= active.release_at))
            {
                active.sent = true;
                active.sent_at = active.elapsed;
                intents.push(if active.throw {
                    UseIntent::Throw {
                        item: active.cell.id,
                        strong: active.strong,
                    }
                } else {
                    UseIntent::Consume(active.cell)
                });
                events.push(HandEvent::Released(active.cell.id));
            }
            // Observe the real deletion; a queued reliable request is not acceptance.
            let accepted = active.sent && count(snapshot, active.cell.id) < active.before;
            if active.sent && active.elapsed >= active.duration && accepted {
                let id = active.cell.id;
                let next = cells(snapshot)
                    .find(|(c, _)| c.id == id)
                    .map(|(c, _)| c)
                    .or_else(|| {
                        self.revert
                            .filter(|want| cells(snapshot).any(|(c, _)| c == *want))
                    });
                if let Some(next) = next {
                    self.pending = Some(next);
                    self.pending_age = 0.;
                    intents.push(UseIntent::Hold(next));
                }
                self.active = None;
                events.push(HandEvent::Finish(id));
            } else if active.sent && active.elapsed - active.sent_at > 1.0 && !accepted {
                self.active = None;
                events.push(HandEvent::Cancel);
            }
        }
        (intents, events)
    }
}
pub fn throw_origin(eye: Vec3, direction: Vec3, hit_distance: Option<f32>, gap: f32) -> Vec3 {
    eye + direction * hit_distance.map_or(1., |d| (d - gap).max(0.))
}
pub fn throw_velocity(direction: Vec3, inherited: Vec3, strong: bool, motion: &Value) -> Vec3 {
    let scale = if strong {
        1.
    } else {
        motion["weakScale"].as_f64().unwrap_or(600. / 1100.) as f32
    };
    (direction * (motion["speed"].as_f64().unwrap_or(16.) as f32 * scale)
        + Vec3::Y * (motion["lift"].as_f64().unwrap_or(5.) as f32 * scale)
        + inherited)
        .clamp_length_max(47.5)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn fixture(id: u16, throw: bool, amount: u8) -> (Value, ItemVisual, Value) {
        let snapshot = json!({"weapon":{"held":{"id":id,"page":2,"x":0,"y":0}},"inventory":{"pages":[{"index":2,"items":[{"id":id,"x":0,"y":0,"amount":amount}]}]}});
        let item=serde_json::from_value(json!({"id":id,"name":"fixture","type":"food","isConsumable":!throw,
            "throwable":if throw {json!({"kind":"Explosive"})}else{Value::Null},"consumeAnimation":{"seconds":1.5}})).unwrap();
        (
            snapshot,
            item,
            json!({"useSeconds":1.63,"releaseFraction":0.6,"weakScale":600./1100.}),
        )
    }
    #[test]
    fn held_grenade_does_not_cook_and_release_uses_current_aim() {
        let (s, item, m) = fixture(254, true, 2);
        let mut use_ = HandUse::default();
        for tick in 0..250 {
            let (intents, _) = use_.step(
                0.02,
                &s,
                Some(&item),
                &m,
                &Buttons {
                    press: tick == 0,
                    fire: true,
                    ..default()
                },
                true,
                true,
            );
            assert!(intents.is_empty());
        }
        assert_eq!(
            use_.step(0.02, &s, Some(&item), &m, &Buttons::default(), true, true)
                .0,
            vec![UseIntent::Throw {
                item: 254,
                strong: true
            }]
        );
        assert!(use_.hide_item());
        assert!(
            use_.step(0.02, &s, Some(&item), &m, &Buttons::default(), true, true)
                .0
                .is_empty()
        );
        assert_eq!(
            throw_origin(Vec3::ZERO, Vec3::NEG_Z, Some(0.2), 0.5),
            Vec3::ZERO
        );
        assert_eq!(
            throw_origin(Vec3::ZERO, Vec3::NEG_Z, None, 0.5),
            Vec3::NEG_Z
        );
        assert_eq!(
            throw_velocity(Vec3::X, Vec3::ZERO, true, &m),
            Vec3::new(16., 5., 0.)
        );
        assert!(throw_velocity(Vec3::X, Vec3::splat(80.), false, &m).length() <= 47.5001);
    }
    #[test]
    fn tap_waits_for_release_marker_and_item_change_cancels_pending_throw() {
        let (mut s, item, m) = fixture(254, true, 2);
        let mut use_ = HandUse::default();
        for tick in 0..48 {
            assert!(
                use_.step(
                    0.02,
                    &s,
                    Some(&item),
                    &m,
                    &Buttons {
                        press: tick == 0,
                        fire: tick == 0,
                        ..default()
                    },
                    true,
                    true
                )
                .0
                .is_empty()
            );
        }
        assert_eq!(
            use_.step(0.02, &s, Some(&item), &m, &Buttons::default(), true, true)
                .0,
            vec![UseIntent::Throw {
                item: 254,
                strong: true
            }]
        );
        let mut use_ = HandUse::default();
        use_.step(
            0.02,
            &s,
            Some(&item),
            &m,
            &Buttons {
                press: true,
                fire: true,
                ..default()
            },
            true,
            true,
        );
        s["weapon"]["held"] = Value::Null;
        assert_eq!(
            use_.step(2., &s, None, &m, &Buttons::default(), true, true),
            (vec![], vec![HandEvent::Cancel])
        );
    }
    #[test]
    fn eating_waits_for_animation_and_real_deletion_before_rearming() {
        let (mut s, item, m) = fixture(13, false, 2);
        let mut use_ = HandUse::default();
        for tick in 0..75 {
            assert!(
                use_.step(
                    0.02,
                    &s,
                    Some(&item),
                    &m,
                    &Buttons {
                        press: tick == 0,
                        ..default()
                    },
                    true,
                    true
                )
                .0
                .is_empty()
            );
        }
        let cell = Cell::held(&s).unwrap();
        assert_eq!(
            use_.step(0.02, &s, Some(&item), &m, &Buttons::default(), true, true)
                .0,
            vec![UseIntent::Consume(cell)]
        );
        assert!(
            use_.step(0.02, &s, Some(&item), &m, &Buttons::default(), true, true)
                .0
                .is_empty()
        );
        s["inventory"]["pages"][0]["items"][0]["amount"] = json!(1);
        assert_eq!(
            use_.step(0.02, &s, Some(&item), &m, &Buttons::default(), true, true)
                .0,
            vec![UseIntent::Hold(cell)]
        );
        s["inventory"]["pages"][0]["items"][0]["frozen"] = json!(1);
        assert_eq!(
            use_.step(
                0.02,
                &s,
                Some(&item),
                &m,
                &Buttons {
                    press: true,
                    ..default()
                },
                true,
                true
            )
            .0,
            vec![UseIntent::Frozen]
        );
    }
    #[test]
    fn switching_food_preserves_rifle_fallback_and_rejected_rearm_does_not_lock_input() {
        let (mut s, food, m) = fixture(13, false, 1);
        let gun = Cell {
            id: 4,
            page: 0,
            x: 0,
            y: 0,
        };
        s["inventory"]["pages"]
            .as_array_mut()
            .unwrap()
            .push(json!({"index":0,"items":[{"id":4,"x":0,"y":0,"amount":1}]}));
        let mut hand = HandUse {
            last: Some(gun),
            ..default()
        };
        hand.step(0.02, &s, Some(&food), &m, &Buttons::default(), true, true);
        let mut other = food.clone();
        other.id = 14;
        s["weapon"]["held"]["id"] = json!(14);
        s["inventory"]["pages"][0]["items"][0]["id"] = json!(14);
        hand.step(
            0.02,
            &s,
            Some(&other),
            &m,
            &Buttons {
                press: true,
                ..default()
            },
            true,
            true,
        );
        hand.step(2., &s, Some(&other), &m, &Buttons::default(), true, true);
        s["weapon"]["held"] = Value::Null;
        s["inventory"]["pages"][0]["items"] = json!([]);
        assert_eq!(
            hand.step(0.02, &s, None, &m, &Buttons::default(), true, true)
                .0,
            vec![UseIntent::Hold(gun)]
        );
        assert_eq!(hand.pending, Some(gun));
        hand.step(1.1, &s, None, &m, &Buttons::default(), true, true);
        assert_eq!(hand.pending, None);
    }
    #[test]
    fn exact_instance_replacement_cancels_use_and_repair_has_a_release_intent() {
        let (mut s, mut item, m) = fixture(13, false, 1);
        let mut hand = HandUse::default();
        s["weapon"]["selectionVersion"] = json!(3);
        hand.step(
            0.02,
            &s,
            Some(&item),
            &m,
            &Buttons {
                press: true,
                ..default()
            },
            true,
            true,
        );
        s["weapon"]["selectionVersion"] = json!(4);
        assert_eq!(
            hand.step(2., &s, Some(&item), &m, &Buttons::default(), true, true)
                .1,
            vec![HandEvent::Cancel]
        );
        item.is_consumable = false;
        item.melee = json!({"repair":true,"repeated":true});
        assert_eq!(
            hand.step(
                0.02,
                &s,
                Some(&item),
                &m,
                &Buttons {
                    fire: true,
                    ..default()
                },
                true,
                true
            )
            .0,
            vec![UseIntent::Tool(true)]
        );
        assert_eq!(
            hand.step(0.02, &s, Some(&item), &m, &Buttons::default(), true, true)
                .0,
            vec![UseIntent::Tool(false)]
        );
    }
}
