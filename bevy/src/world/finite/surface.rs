//! Terrain.SampleHeight / RiverCutWorld over source-normalized grid data.
use super::*;

#[derive(Debug, Clone)]
pub(super) struct River {
    pub half: f32,
    pub depth: f32,
    pub points: Vec<Vec3>,
}
#[derive(Debug, Clone)]
pub(crate) struct Surface {
    pub width: usize,
    pub height: usize,
    pub grid: Vec<f32>,
    pub layers: Vec<u8>,
    pub holes: Vec<bool>,
    pub blend: f32,
    pub drop: f32,
    pub rivers: Vec<River>,
}
impl Surface {
    pub fn decode(bytes: &[u8]) -> Result<Self, ContentError> {
        let mut r = Reader(bytes);
        if r.u32()? != 0x53464755 || r.u32()? != 2 {
            return Err(ContentError("invalid finite surface header".into()));
        }
        let width = r.u32()? as usize;
        let height = r.u32()? as usize;
        if !(257..=2049).contains(&width)
            || !(257..=2049).contains(&height)
            || (width - 1) % 256 != 0
            || (height - 1) % 256 != 0
            || r.0.len() < width * height * 4 + (width - 1) * (height - 1) * 2 + 12
        {
            return Err(ContentError(
                "invalid finite surface dimensions/length".into(),
            ));
        }
        let grid = (0..width * height)
            .map(|_| r.f32())
            .collect::<Result<Vec<_>, _>>()?;
        let count = (width - 1) * (height - 1);
        let layers = r.0[..count].to_vec();
        r.0 = &r.0[count..];
        let raw_holes = &r.0[..count];
        if layers.iter().any(|v| *v > 7) || raw_holes.iter().any(|v| *v > 1) {
            return Err(ContentError("invalid finite layer/hole".into()));
        }
        let holes = raw_holes.iter().map(|v| *v != 0).collect();
        r.0 = &r.0[count..];
        let blend = r.f32()?;
        let drop = r.f32()?;
        let count = r.u32()? as usize;
        if blend != 2.5 || drop != 0.35 || count > 65536 {
            return Err(ContentError("invalid source river metadata".into()));
        }
        let mut rivers = Vec::new();
        for _ in 0..count {
            let half = r.f32()?;
            let depth = r.f32()?;
            let points = r.u32()? as usize;
            if half <= 0.
                || depth < 0.
                || points < 2
                || points > 1_000_000
                || points > r.0.len() / 12
            {
                return Err(ContentError("invalid finite river".into()));
            }
            let points = (0..points)
                .map(|_| Ok(Vec3::from_array(r.floats()?)))
                .collect::<Result<_, ContentError>>()?;
            rivers.push(River {
                half,
                depth,
                points,
            });
        }
        r.end()?;
        Ok(Self {
            width,
            height,
            grid,
            layers,
            holes,
            blend,
            drop,
            rivers,
        })
    }
    pub fn sample(&self, x: f32, z: f32) -> f32 {
        let p = Vec2::new(x / 4., -z / 4.);
        let floor = p.floor();
        let fraction = p - floor;
        let x0 = (floor.x as isize).clamp(0, self.width as isize - 1) as usize;
        let x1 = (floor.x as isize + 1).clamp(0, self.width as isize - 1) as usize;
        let z0 = (floor.y as isize).clamp(0, self.height as isize - 1) as usize;
        let z1 = (floor.y as isize + 1).clamp(0, self.height as isize - 1) as usize;
        // Match Godot.Mathf.Lerp's operation order, before normalized→metres.
        let lerp = |a: f32, b: f32, t: f32| a + (b - a) * t;
        let h0 = lerp(
            self.grid[z0 * self.width + x0],
            self.grid[z0 * self.width + x1],
            fraction.x,
        );
        let h1 = lerp(
            self.grid[z1 * self.width + x0],
            self.grid[z1 * self.width + x1],
            fraction.x,
        );
        lerp(h0, h1, fraction.y) * 2048. - 1024. - self.river_cut(Vec2::new(x, z))
    }
    fn river_cut(&self, p: Vec2) -> f32 {
        let mut best = 0_f32;
        for river in &self.rivers {
            for pair in river.points.windows(2) {
                let a = pair[0].xz();
                let ab = pair[1].xz() - a;
                let t = ((p - a).dot(ab) / ab.length_squared().max(1e-6)).clamp(0., 1.);
                let d = (p - (a + ab * t)).length();
                let blend = river.half * self.blend;
                if d >= blend {
                    continue;
                }
                let rim = river.depth * self.drop;
                let profile = |u: f32| {
                    if u >= 1. {
                        0.
                    } else {
                        let k = 1. - u * u;
                        k * k
                    }
                };
                let cut = if d <= river.half {
                    rim + (river.depth - rim) * profile(d / river.half.max(0.001))
                } else {
                    rim * profile((d - river.half) / (blend - river.half).max(0.001))
                };
                best = best.max(cut);
            }
        }
        best
    }
    pub fn splat(&self) -> SplatField {
        let width = self.width - 1;
        let height = self.height - 1;
        let pixels = (0..height)
            .flat_map(|z| {
                (0..width).map(move |x| {
                    let mut weights = [0; 8];
                    weights[self.layers[(height - 1 - z) * width + x] as usize] = 255;
                    weights
                })
            })
            .collect();
        SplatField {
            origin: Vec2::new(0., -((height * 4) as f32)),
            size: Vec2::new((width * 4) as f32, (height * 4) as f32),
            width,
            height,
            pixels,
            reflect_noise_z: true,
        }
    }
    pub fn patches(&self, splat: Arc<SplatField>) -> Vec<TerrainPatch> {
        let mut patches = Vec::new();
        for tx in 0..(self.width - 1) / 256 {
            for tz in 0..(self.height - 1) / 256 {
                let origin = Vec2::new((tx * 1024) as f32, -(((tz + 1) * 1024) as f32));
                let mut heights = Vec::new();
                let mut layers = Vec::new();
                for z in 0..257 {
                    for x in 0..257 {
                        let p = origin + Vec2::new(x as f32, z as f32) * 4.;
                        heights.push(self.sample(p.x, p.y));
                        let gx = tx * 256 + x;
                        let gz = (tz + 1) * 256 - z;
                        layers.push(
                            self.layers[gz.min(self.height - 2) * (self.width - 1)
                                + gx.min(self.width - 2)],
                        );
                    }
                }
                // Used for bounds, metadata and NPC grid fallback only. Actual
                // rendering/collision use exact source chunks, including quad holes.
                patches.push(TerrainPatch {
                    origin,
                    cells: 256,
                    spacing: 4.,
                    heights,
                    layers,
                    normals: vec![],
                    holes: vec![],
                    splat: Some(splat.clone()),
                });
            }
        }
        patches
    }
}
