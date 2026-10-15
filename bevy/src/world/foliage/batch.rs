//! Batched foliage must retain authored LOCAL height after vertices become world-space.
use crate::content::ObjData;
use bevy::prelude::*;
#[derive(Default)]
pub(crate) struct Batch {
    pub data: ObjData,
    heights: Vec<[f32; 2]>,
}
impl Batch {
    pub fn append(&mut self, source: &ObjData, transform: Transform) {
        self.data.append_transformed(source, transform);
        let mirrored = transform.to_matrix().determinant() < 0.;
        for triangle in (0..source.positions.len()).step_by(3) {
            for corner in if mirrored { [0, 2, 1] } else { [0, 1, 2] } {
                self.heights
                    .push([source.positions[triangle + corner][1], 0.]);
            }
        }
    }
    pub fn mesh(&self) -> Mesh {
        self.data
            .mesh()
            .with_inserted_attribute(Mesh::ATTRIBUTE_UV_1, self.heights.clone())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn elevated_reflected_batch_keeps_authored_height_and_matching_winding() {
        let source = crate::content::parse_obj("v 0 0 0\nv 1 2 0\nv 0 4 0\nf 1 2 3\n").unwrap();
        let mut batch = Batch::default();
        batch.append(
            &source,
            Transform::from_xyz(20., 900., -30.).with_scale(Vec3::new(-2., 3., 2.)),
        );
        assert_eq!(batch.heights, vec![[0., 0.], [4., 0.], [2., 0.]]);
        assert_eq!(
            batch
                .data
                .positions
                .iter()
                .map(|p| p[1])
                .collect::<Vec<_>>(),
            vec![900., 912., 906.]
        );
        assert_eq!(batch.mesh().count_vertices(), 3);
    }
}
