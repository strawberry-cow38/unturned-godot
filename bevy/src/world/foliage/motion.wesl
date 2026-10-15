// Source grass_displace.gdshader, foliage_up.gdshader, wind_sway.gdshader,
// and disturbance.gdshaderinc. All offsets are evaluated from each rest vertex.
struct Motion {
    wind: vec4<f32>,
    origin: vec4<f32>, // xy phase offset, z disturbance count
    settings: vec4<f32>, // x mode: 0 static, 1 grass, 2 flowers, 3 canopy
    positions: array<vec4<f32>,32>,
    responses: array<vec4<f32>,32>,
};
@group(constants::MATERIAL_BIND_GROUP) @binding(101) var<uniform> motion:Motion;

fn disturb_one(world:vec3<f32>, weight:f32, flat:f32, a:vec4<f32>, b:vec4<f32>)->vec3<f32>{
    if a.w<=0.0 {return vec3<f32>(0.0);}
    let away=world.xz-a.xz;
    let distance=length(away);
    if distance<0.0001 {return vec3<f32>(0.0);}
    let vertical=clamp(1.0-abs(world.y-a.y)/b.w,0.0,1.0);
    var influence=clamp(1.0-distance/a.w,0.0,1.0)*vertical;
    if b.z>0.5 {
        let rn=distance/a.w;
        let ring=smoothstep(0.0,0.35,rn)*(1.0-smoothstep(0.55,1.0,rn));
        influence=max(influence*0.35,ring)*vertical;
    } else {influence*=influence;}
    let push=influence*weight*b.x;
    return vec3<f32>(away.x/distance*push,-push*b.y*flat,away.y/distance*push);
}
fn disturb(world:vec3<f32>,weight:f32,flat:f32)->vec3<f32>{
    var offset=vec3<f32>(0.0);
    for(var i=0u;i<u32(motion.origin.z);i++){
        offset+=disturb_one(world,weight,flat,motion.positions[i],motion.responses[i]);
    }
    return offset;
}
fn displaced(world:vec3<f32>,local_height:f32,uv:vec2<f32>)->vec3<f32>{
    let kind=motion.settings.x;
    let wind=motion.wind;
    let dir=vec3<f32>(wind.x,0.0,wind.y);
    if kind>0.5 && kind<1.5 {
        let wh=1.0-uv.y;
        var moved=world+disturb(world,wh*wh,1.0);
        let absolute=moved.xz+motion.origin.xy;
        let gust=sin(wind.w*1.5+absolute.x*0.25+absolute.y*0.2);
        let bend=wh*(0.10+0.35*wind.z)*(0.65+0.35*gust);
        moved+=dir*bend-vec3<f32>(0.0,abs(bend)*0.15,0.0);
        return moved;
    }
    let height=max(local_height,0.0);
    let absolute=world.xz+motion.origin.xy;
    if kind>1.5 && kind<2.5 {
        let gust=sin(wind.w*1.6+absolute.x*0.2+absolute.y*0.17);
        return world+dir*(height*0.08*(0.4+wind.z)*(0.7+0.3*gust));
    }
    if kind>2.5 {
        let gust=sin(wind.w*1.3+absolute.x*0.16+absolute.y*0.14);
        return world+dir*(height*0.05*(0.4+wind.z)*(0.7+0.3*gust))
            +disturb(world,min(height,1.0),0.18);
    }
    return world;
}
