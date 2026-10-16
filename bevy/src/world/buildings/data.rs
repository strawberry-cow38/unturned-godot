use bevy::prelude::*;
use serde::Deserialize;

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct View {
    pub version: u32,
    pub read_only: bool,
    pub stage_origin: [f32; 3],
    pub text: String,
    pub walls: Vec<Wall>,
    pub ramps: Vec<Ramp>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Wall {
    pub index: usize,
    pub kind: String,
    pub position: [f32; 3],
    pub basis: [[f32; 3]; 3],
    pub front: Part,
    pub back: Part,
    pub trim: Part,
    pub solids: Collision,
    pub trim_solids: Collision,
}
#[derive(Clone, Deserialize)]
pub(super) struct Part {
    pub triangles: Vec<[f32; 3]>,
    pub color: [f32; 4],
}
#[derive(Clone, Deserialize)]
pub(super) struct Collision {
    pub layer: u32,
    pub shapes: Vec<Shape>,
}
#[derive(Clone, Deserialize)]
pub(super) struct Shape {
    pub position: [f32; 3],
    #[serde(rename = "box")]
    pub cuboid: Option<[f32; 3]>,
    pub hull: Option<Vec<[f32; 3]>>,
}
#[derive(Clone, Deserialize)]
pub(super) struct Ramp {
    pub position: [f32; 3],
    pub basis: [[f32; 3]; 3],
    pub size: [f32; 3],
}
pub(super) fn pose(position: [f32; 3], basis: [[f32; 3]; 3]) -> Transform {
    Transform::from_translation(position.into())
        .with_rotation(Quat::from_mat3(&Mat3::from_cols_array_2d(&basis)).normalize())
}
impl View {
    pub(super) fn validate(&self) -> anyhow::Result<()> {
        anyhow::ensure!(
            self.version == 1 && self.read_only,
            "unsupported building source view"
        );
        anyhow::ensure!(
            self.stage_origin.iter().all(|f| f.is_finite()),
            "invalid building stage"
        );
        let valid_pose = |p: [f32; 3], b: [[f32; 3]; 3]| {
            let m = Mat3::from_cols_array_2d(&b);
            p.iter().all(|v| v.is_finite())
                && m.is_finite()
                && (m.determinant() - 1.).abs() < 1e-4
                && (m.transpose() * m - Mat3::IDENTITY)
                    .to_cols_array()
                    .iter()
                    .all(|v| v.abs() < 1e-4)
        };
        for (index, w) in self.walls.iter().enumerate() {
            anyhow::ensure!(
                w.index == index && valid_pose(w.position, w.basis),
                "invalid wall identity/pose"
            );
            anyhow::ensure!(
                w.solids.layer == 1 && w.trim_solids.layer == 64,
                "invalid source wall layers"
            );
            for part in [&w.front, &w.back, &w.trim] {
                anyhow::ensure!(
                    part.triangles.len() % 3 == 0
                        && part.triangles.iter().flatten().all(|v| v.is_finite())
                        && part.color.iter().all(|v| v.is_finite()),
                    "invalid wall mesh"
                );
            }
            for shape in w.solids.shapes.iter().chain(&w.trim_solids.shapes) {
                anyhow::ensure!(
                    shape.position.iter().all(|v| v.is_finite()),
                    "invalid wall shape pose"
                );
                match (&shape.cuboid, &shape.hull) {
                    (Some(size), None) => anyhow::ensure!(
                        size.iter().all(|v| v.is_finite() && *v > 0.),
                        "invalid wall box"
                    ),
                    (None, Some(points)) => anyhow::ensure!(
                        points.len() >= 4 && points.iter().flatten().all(|v| v.is_finite()),
                        "invalid wall hull"
                    ),
                    (None, None) => (), // source empty tapered solid may have no shape
                    _ => anyhow::bail!("ambiguous wall shape"),
                }
            }
        }
        for r in &self.ramps {
            anyhow::ensure!(
                valid_pose(r.position, r.basis) && r.size.iter().all(|v| v.is_finite() && *v > 0.),
                "invalid stair ramp"
            );
        }
        Ok(())
    }
}
