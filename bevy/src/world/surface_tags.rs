//! Collider material metadata from RegionStreamer, independent of terrain paint.
use crate::impact::surfaces::Surface;

pub fn tree(kind: u8) -> Surface {
    if kind == 4 {
        Surface::Grass
    } else {
        Surface::Wood
    }
}
pub fn road(texture: &str) -> Surface {
    // BuildRoadBodies has [paved, trail]. City road 8 and rail bridge
    // underlay both join the paved soup; only RoadKind.Trail is dirt.
    if texture.ends_with("roads/road_5.png") {
        Surface::Dirt
    } else {
        Surface::Concrete
    }
}
pub fn tunnel(kind: &str) -> Surface {
    // MouthData ground is explicitly Dirt even where its splat displays grass.
    if kind.starts_with("terrain:") {
        Surface::Dirt
    } else {
        Surface::Concrete
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn generated_geometry_uses_source_collider_material_not_underlying_paint() {
        assert_eq!(tree(0), Surface::Wood);
        assert_eq!(tree(4), Surface::Grass);
        assert_eq!(road("patch:roads/road_5.png"), Surface::Dirt);
        assert_eq!(road("roads/road_8.png"), Surface::Concrete);
        assert_eq!(road("terrain/layer3.png"), Surface::Concrete);
        assert_eq!(tunnel("terrain:2"), Surface::Dirt);
        assert_eq!(tunnel("floor"), Surface::Concrete);
    }
}
