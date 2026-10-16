//! Source half-second magazine jobs. Timers belong to the open dashboard;
//! ammunition belongs to the server. A job waits for its exact request result
//! and the corresponding inventory echo before asking for the next round.
use super::super::UiIntent;
use super::{api, commands, model::*};
use serde::Deserialize;
use serde_json::json;

pub(super) const INTERVAL: f32 = 0.5;
/// Adapter bound, not a source timer: a request the bridge never queued (for
/// example, no completed camera for a ground step) gets no reply, and a later
/// retry would be a stale ID on the shared ledger. The job retires instead of
/// spinning its wheel forever; a late reply then matches no job.
pub(super) const REPLY_TIMEOUT: f32 = 10.0;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Completion {
    pub request_id: u32,
    pub magazine_token: u64,
    pub accepted_tick: u32,
    pub applied: bool,
    pub amount: u16,
}
#[derive(Debug)]
enum Pending {
    /// Request ID, and whether a ground row took part.
    Reply(u32, bool),
    /// Accepted tick that the owner snapshot (and, for a ground step, the
    /// Nearby projection) must reach before the next source stack is chosen.
    Echo(u32, bool),
}
#[derive(Debug)]
pub(super) struct Operation {
    pub magazine: api::Address,
    pub unloading: bool,
    pub round_id: u16,
    pub batch: u16,
    pub done: u16,
    pub elapsed: f32,
    capacity: i32,
    pending: Option<Pending>,
    /// Seconds an unanswered request has waited; see REPLY_TIMEOUT.
    waiting: f32,
}
impl Operation {
    pub fn progress(&self) -> f32 {
        if self.batch == 0 {
            return 0.;
        }
        // The accepted round count drives the wheel; elapsed time does not
        // paint an invented round while the authority request is outstanding.
        f32::from(self.done) / f32::from(self.batch)
    }
}
#[derive(Default, Debug)]
pub(super) struct Magazines {
    pub operations: Vec<Operation>,
    next_request: u32,
}
impl Magazines {
    /// Only a new world/session resets IDs. Closing or reopening Inventory must
    /// not reuse an ID that the host remembers, and source jobs survive Close.
    pub fn reset_session(&mut self) {
        *self = Self::default();
    }

    fn start(&mut self, row: &api::Item, batch: u16, round_id: u16, unloading: bool) -> bool {
        if !(row.address.addressable() || row.address.ground())
            || !row.magazine
            || batch == 0
            || round_id == 0
        {
            return false;
        }
        // Source keys a replacement on the magazine. A ground row can be
        // repacked to another cell, so it is matched by its world identity.
        self.operations.retain(|op| {
            if op.magazine.ground() || row.address.ground() {
                !op.magazine.same_instance(row.address)
            } else {
                op.magazine.cell() != row.address.cell()
            }
        });
        self.operations.push(Operation {
            magazine: row.address,
            unloading,
            round_id,
            batch,
            done: 0,
            elapsed: 0.,
            capacity: row.magazine_capacity,
            pending: None,
            waiting: 0.,
        });
        true
    }
    pub fn load(&mut self, row: &api::Item, load: &api::Load) -> bool {
        load.allowed && self.start(row, load.batch, load.round_id, false)
    }
    pub fn unload(&mut self, view: &api::View, cell: Cell) -> bool {
        let (Some(row), Some(tile)) = (view.at(cell), view.tile(cell)) else {
            return false;
        };
        self.start(row, tile.amount, row.round_id, true)
    }

    /// Returns the original magazine capacity for one accepted round's foley.
    /// Duplicate/replayed replies cannot increment the wheel or replay sound.
    pub fn receive(&mut self, fact: &Completion, _view: &api::View) -> Option<i32> {
        let index = self.operations.iter().position(|op| {
            op.magazine.token == fact.magazine_token
                && matches!(op.pending, Some(Pending::Reply(id, _)) if id == fact.request_id)
        })?;
        if !fact.applied {
            self.operations.remove(index);
            return None;
        }
        let op = &mut self.operations[index];
        op.done += 1;
        let capacity = Some(op.capacity);
        if op.done >= op.batch || (op.unloading && fact.amount == 0) {
            self.operations.remove(index);
        } else {
            let ground = matches!(op.pending, Some(Pending::Reply(_, true)));
            op.pending = Some(Pending::Echo(fact.accepted_tick, ground));
        }
        capacity
    }

    /// Source FindStack walks page and item order for each round, AREA (page
    /// 8) included. Ground rows carry their received world identity and route
    /// through command 88; owned and container scopes keep 81/85.
    pub fn step(&mut self, open: bool, delta: f32, view: &api::View) -> Vec<UiIntent> {
        if !open {
            return vec![];
        }
        let mut requests = Vec::new();
        for index in (0..self.operations.len()).rev() {
            let op = &mut self.operations[index];
            let Some(row) = (if op.magazine.ground() {
                view.items
                    .iter()
                    .find(|r| r.address.same_instance(op.magazine))
            } else {
                view.at(op.magazine.cell())
                    .filter(|r| r.address == op.magazine)
            }) else {
                self.operations.remove(index);
                continue;
            };
            // A repaint advances a ground row's revision; keep the newest.
            op.magazine = row.address;
            let cell = op.magazine.cell();
            let Some(tile) = view.tile(cell) else {
                continue;
            };
            op.elapsed += delta.max(0.);
            match op.pending {
                Some(Pending::Reply(..)) => {
                    op.waiting += delta.max(0.);
                    if op.waiting > REPLY_TIMEOUT {
                        self.operations.remove(index);
                    }
                    continue;
                }
                Some(Pending::Echo(tick, ground))
                    if (view.accepted_tick.wrapping_sub(tick) as i32) < 0
                        || (ground && (view.nearby_tick.wrapping_sub(tick) as i32) < 0) =>
                {
                    continue;
                }
                Some(Pending::Echo(..)) => op.pending = None,
                None => {}
            }
            if op.elapsed < INTERVAL {
                continue;
            }
            if (op.unloading && tile.amount == 0)
                || (!op.unloading && i32::from(tile.amount) >= row.magazine_capacity)
            {
                self.operations.remove(index);
                continue;
            }
            let round = if op.unloading {
                api::Address {
                    id: row.round_id,
                    ..Default::default()
                }
            } else {
                let Some(round) = view.items.iter().find(|r| {
                    r.address.id == op.round_id
                        && (r.address.addressable() || r.address.ground())
                        && view.tile(r.address.cell()).is_some_and(|t| t.amount > 0)
                }) else {
                    self.operations.remove(index);
                    continue;
                };
                round.address
            };
            if round.id == 0 {
                self.operations.remove(index);
                continue;
            }
            self.next_request = self.next_request.wrapping_add(1).max(1);
            let ground = op.magazine.ground() || round.ground();
            op.pending = Some(Pending::Reply(self.next_request, ground));
            op.waiting = 0.;
            op.elapsed -= INTERVAL;
            let scoped = op.magazine.crate_id != 0 || round.crate_id != 0;
            let mut args = json!({
                "requestId":self.next_request, "magPage":op.magazine.page,"magX":op.magazine.x,"magY":op.magazine.y,
                "magId":op.magazine.id,"magToken":op.magazine.token,"roundPage":round.page,"roundX":round.x,"roundY":round.y,
                "roundId":round.id,"roundToken":round.token,"unloading":op.unloading
            });
            if scoped || ground {
                args["magCrateId"] = json!(op.magazine.crate_id);
                args["roundCrateId"] = json!(round.crate_id);
            }
            if ground {
                // One received scan/revision names every ground part; the
                // host rechecks camera, range, LOS and full bytes per step.
                let at = if op.magazine.ground() { op.magazine } else { round };
                args["scanId"] = json!(at.scan_id);
                args["revision"] = json!(at.revision);
                args["magWorldId"] = json!(op.magazine.world_id);
                args["roundWorldId"] = json!(round.world_id);
            }
            requests.push(commands::action(
                if ground {
                    "nearbyMagazineTransfer"
                } else if scoped {
                    "viewedMagazineTransfer"
                } else {
                    "magazineTransfer"
                },
                args,
            ));
        }
        requests
    }
}

#[cfg(test)]
mod tests;
