//! Original dominant terrain layers, separate from the renderer's noise-warped
//! splat blending. Layer five retains Terrain.IsWater's source sand proxy.
use super::TerrainPatch;
use bevy::prelude::Vec2;
use serde_json::{Value, json};
use std::{collections::HashSet, sync::Arc};

/// Terrain.SampleDominantLayer, in the current local world frame. Retail keeps
/// its global clamped map grid; generated regions use their original samples.
pub fn sample(patches: &[TerrainPatch], position: Vec2) -> u8 {
    if !position.is_finite() {
        return 255;
    }
    if let Some(field) = patches
        .iter()
        .filter_map(|p| p.splat.as_ref())
        .find(|f| f.reflect_noise_z)
    {
        let x = ((position.x - field.origin.x) / (field.size.x / field.width as f32))
            .round_ties_even()
            .clamp(0., (field.width - 1) as f32) as usize;
        let z = ((field.origin.y + field.size.y - position.y)
            / (field.size.y / field.height as f32))
            .round_ties_even()
            .clamp(0., (field.height - 1) as f32) as usize;
        return dominant(&field.pixels[(field.height - 1 - z) * field.width + x]);
    }
    let Some(patch) = patches
        .iter()
        .filter(|p| p.layers.len() == (p.cells + 1).pow(2))
        .min_by(|a, b| {
            let distance = |p: &TerrainPatch| {
                position.distance_squared(
                    position.clamp(p.origin, p.origin + Vec2::splat(p.cells as f32 * p.spacing)),
                )
            };
            distance(a).total_cmp(&distance(b))
        })
    else {
        return 255;
    };
    let p = (position - patch.origin) / patch.spacing;
    let x = p.x.round_ties_even().clamp(0., patch.cells as f32) as usize;
    let z = p.y.round_ties_even().clamp(0., patch.cells as f32) as usize;
    patch.layers[z * (patch.cells + 1) + x]
}

/// Terrain.Load chooses the first channel whose weight is strictly larger.
/// A fully empty texel therefore resolves to zero, as does a zero-weight tie.
pub(super) fn dominant(weights: &[u8]) -> u8 {
    let (mut layer, mut weight) = (0, 0);
    for (index, &candidate) in weights.iter().enumerate() {
        if candidate > weight {
            layer = index as u8;
            weight = candidate;
        }
    }
    layer
}

/// Retail grids are exported in the original negative-Z row order. Their
/// sample at `origin` is the upper edge, not the centre of a texture texel:
/// Terrain.SampleDominantLayer rounds `(world-origin)/spacing`, then clamps.
/// Procedural RegionData already stores increasing world-X/Z vertex samples.
pub fn authority_grids(patches: &[TerrainPatch]) -> Vec<Value> {
    let mut seen = HashSet::new();
    let mut grids = Vec::new();
    for patch in patches {
        if let Some(field) = patch.splat.as_ref().filter(|f| f.reflect_noise_z) {
            if !seen.insert(Arc::as_ptr(field) as usize) {
                continue;
            }
            let layers: Vec<u8> = (0..field.height)
                .flat_map(|z| {
                    (0..field.width).map(move |x| {
                        dominant(&field.pixels[(field.height - 1 - z) * field.width + x])
                    })
                })
                .collect();
            grids.push(json!({
                "origin":[field.origin.x,field.origin.y+field.size.y],
                "width":field.width,"height":field.height,
                "spacing":[field.size.x/field.width as f32,-field.size.y/field.height as f32],
                "layers":layers,
            }));
        } else {
            let width = patch.cells + 1;
            if patch.layers.len() != width * width {
                continue;
            }
            grids.push(json!({
                "origin":patch.origin.to_array(),"width":width,"height":width,
                "spacing":[patch.spacing,patch.spacing],"layers":patch.layers,
            }));
        }
    }
    grids
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::terrain::SplatField;
    use bevy::prelude::*;

    #[test]
    fn source_dominant_channel_retains_first_equal_weight_and_empty_zero() {
        assert_eq!(dominant(&[0; 8]), 0);
        assert_eq!(dominant(&[1, 7, 2, 7, 1, 7, 0, 0]), 1);
        assert_eq!(dominant(&[1, 7, 2, 7, 1, 8, 0, 0]), 5);
    }

    #[test]
    fn retail_metadata_restores_source_rows_upper_edge_and_shared_grid() {
        let field = Arc::new(SplatField {
            origin: Vec2::new(-1024., -2048.),
            size: Vec2::splat(8.),
            width: 2,
            height: 2,
            pixels: [2, 5, 6, 7]
                .map(|layer| {
                    let mut weights = [0; 8];
                    weights[layer] = 255;
                    weights
                })
                .to_vec(),
            reflect_noise_z: true,
        });
        let patch = TerrainPatch {
            origin: field.origin,
            cells: 1,
            spacing: 4.,
            heights: vec![0.; 4],
            layers: vec![0; 4],
            normals: vec![],
            holes: vec![],
            splat: Some(field),
        };
        for (at, want) in [
            (Vec2::new(-1024., -2040.), 6),
            (Vec2::new(-1022., -2042.), 6),
            (Vec2::new(-1021.9, -2042.1), 5),
            (Vec2::new(-1200., -3000.), 2),
        ] {
            assert_eq!(
                sample(&[patch.clone()], at),
                want,
                "source nearest sample at {at}"
            );
        }
        let grids = authority_grids(&[patch.clone(), patch]);
        assert_eq!(grids.len(), 1);
        assert_eq!(grids[0]["origin"], json!([-1024., -2040.]));
        assert_eq!(grids[0]["spacing"], json!([4., -4.]));
        assert_eq!(grids[0]["layers"], json!([6, 7, 2, 5]));
    }

    #[test]
    fn procedural_metadata_preserves_original_vertex_layers_and_spacing() {
        let patch = TerrainPatch {
            origin: Vec2::new(256., -512.),
            cells: 1,
            spacing: 2.,
            heights: vec![0.; 4],
            layers: vec![2, 5, 0, 7],
            normals: vec![],
            holes: vec![],
            splat: None,
        };
        assert_eq!(
            sample(&[patch.clone()], Vec2::new(257., -511.)),
            2,
            "ties round to even at source grid origin"
        );
        assert_eq!(sample(&[patch.clone()], Vec2::new(258., -512.)), 5);
        assert_eq!(
            sample(&[patch.clone()], Vec2::new(500., -1000.)),
            5,
            "source clamps beyond grid"
        );
        assert_eq!(sample(&[], Vec2::ZERO), 255);
        let grids = authority_grids(&[patch]);
        assert_eq!(grids[0]["origin"], json!([256., -512.]));
        assert_eq!(grids[0]["spacing"], json!([2., 2.]));
        assert_eq!(grids[0]["layers"], json!([2, 5, 0, 7]));
    }
}
