//! SkillsUI.PlaceGraph/Layout/ClampPan. Coordinates are logical UI pixels.
use super::{Pillar, Tree};
use bevy::prelude::*;
use std::collections::BTreeMap;

pub(super) const NODE: Vec2 = Vec2::new(244., 82.);
pub(super) const HEAD: f32 = 44.;
const COLUMN_GAP: f32 = 48.;
const ROW_GAP: f32 = 56.;
const BAND_GAP: f32 = 72.;

#[derive(Default, PartialEq)]
pub(super) struct Graph {
    pub heads: BTreeMap<String, Vec2>,
    pub nodes: BTreeMap<String, Vec2>,
    pub size: Vec2,
}
impl Graph {
    pub fn new(tree: &Tree) -> Self {
        let pillar = |id: &str| tree.pillars.iter().find(|p| p.id == id);
        let main_tiers = tree
            .nodes
            .iter()
            .filter(|n| pillar(&n.pillar).is_some_and(|p| !p.advanced))
            .map(|n| n.tier + 1)
            .max()
            .unwrap_or(0);
        let main_top = HEAD + ROW_GAP;
        let advanced_head = main_top + main_tiers as f32 * (NODE.y + ROW_GAP) + BAND_GAP;
        let advanced_top = advanced_head + HEAD + ROW_GAP;
        let mut graph = Self::default();
        for p in &tree.pillars {
            graph.heads.insert(
                p.id.clone(),
                Vec2::new(
                    p.column as f32 * (NODE.x + COLUMN_GAP),
                    if p.advanced { advanced_head } else { 0. },
                ),
            );
        }
        for n in &tree.nodes {
            let Some(p) = pillar(&n.pillar) else {
                continue;
            };
            let at = Vec2::new(
                p.column as f32 * (NODE.x + COLUMN_GAP),
                if p.advanced { advanced_top } else { main_top }
                    + n.tier as f32 * (NODE.y + ROW_GAP),
            );
            graph.size = graph.size.max(at + NODE);
            graph.nodes.insert(n.id.clone(), at);
        }
        graph
    }
    pub fn clamp(&self, pan: Vec2, view: Vec2) -> Vec2 {
        let delta = view - self.size;
        pan.clamp(delta.min(Vec2::ZERO), delta.max(Vec2::ZERO))
    }
    pub fn center_if_small(&self, mut pan: Vec2, view: Vec2) -> Vec2 {
        if self.size.x < view.x {
            pan.x = (view.x - self.size.x) * 0.5;
        }
        self.clamp(pan, view)
    }
    pub fn edges(&self, tree: &Tree) -> Vec<(Vec2, Vec2, bool)> {
        let mut edges = vec![];
        let learned = |id: &str| tree.nodes.iter().any(|n| n.id == id && n.learned);
        for n in &tree.nodes {
            let Some(to) = self.nodes.get(&n.id) else {
                continue;
            };
            let to = *to + Vec2::new(NODE.x * 0.5, 0.);
            if n.requires.is_empty() {
                if let Some(from) = self.heads.get(&n.pillar) {
                    let open = tree
                        .pillars
                        .iter()
                        .find(|p| p.id == n.pillar)
                        .is_none_or(|p| p.open);
                    edges.push((*from + Vec2::new(NODE.x * 0.5, HEAD), to, open));
                }
            } else {
                for id in &n.requires {
                    if let Some(from) = self.nodes.get(id) {
                        edges.push((*from + Vec2::new(NODE.x * 0.5, NODE.y), to, learned(id)));
                    }
                }
            }
        }
        for Pillar { id, requires, .. } in tree.pillars.iter().filter(|p| p.advanced) {
            let Some(to) = self.heads.get(id) else {
                continue;
            };
            for id in requires {
                if let Some(from) = self.nodes.get(id) {
                    edges.push((
                        *from + Vec2::new(NODE.x * 0.5, NODE.y),
                        *to + Vec2::new(NODE.x * 0.5, 0.),
                        learned(id),
                    ));
                }
            }
        }
        edges
    }
}

pub(super) struct Layout {
    pub panel: Rect,
    pub clip: Rect,
    pub detail: Rect,
}
impl Layout {
    pub fn new(viewport: Vec2) -> Self {
        let size = viewport - Vec2::splat(32.);
        let bottom = crate::ui::vitals::content_bottom(
            viewport,
            32.,
            16. + size.x - 380. - 32.,
            16. + size.y - 16.,
        ) - 16.;
        Self {
            panel: Rect::from_corners(Vec2::splat(16.), viewport - Vec2::splat(16.)),
            clip: Rect::from_corners(
                Vec2::new(16., 100.),
                Vec2::new(
                    16. + (size.x - 428.).max(200.),
                    100. + (bottom - 100.).max(140.),
                ),
            ),
            detail: Rect::from_corners(
                Vec2::new(size.x - 396., 100.),
                Vec2::new(size.x - 16., 100. + (bottom - 100.).max(140.)),
            ),
        }
    }
}
