//! The source StepHeli/StepPlane clock, shared by physical aircraft and remote
//! puppets. This advances no controls, fuel, authority, forces, or world pose.
use super::*;

impl Flight {
    pub(in crate::vehicle) fn advance_presentation(
        &mut self,
        spec: &VehicleSpec,
        running: bool,
        exploded: bool,
        dt: f32,
    ) {
        let running = running && !exploded;
        let want = if running { 1. } else { 0. };
        let spool_up = if spec.heli && !self.instant_start && self.ignition_length > 0.1 {
            self.ignition_length * 0.74
        } else {
            3.2
        };
        self.spool = approach(
            self.spool,
            want,
            dt / if want > self.spool { spool_up } else { 5.5 },
        );
        self.ignition_remaining = (self.ignition_remaining - dt).max(0.);
        if running && self.spool < 0.05 && !self.ignition_fired {
            self.ignition_fired = true;
            self.ignition_starts = self.ignition_starts.wrapping_add(1);
            self.ignition_remaining = if self.instant_start {
                0.
            } else {
                self.ignition_length * 0.74
            };
        } else if !running && self.spool < 0.01 {
            self.ignition_fired = false;
        }
        let phase_scale = if exploded { 0. } else { self.spool };
        self.main_phase = (self.main_phase
            + dt * phase_scale * (0.30 + 0.70 * self.main_health) * 46.)
            % std::f32::consts::TAU;
        self.tail_phase = (self.tail_phase
            + dt * phase_scale * (0.30 + 0.70 * self.tail_health) * 120.)
            % std::f32::consts::TAU;
        self.prop_phase = (self.prop_phase + dt * phase_scale * 150.) % std::f32::consts::TAU;
    }
}

#[cfg(test)]
mod tests;
