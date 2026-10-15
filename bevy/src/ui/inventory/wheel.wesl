import bevy_ui_render::ui_vertex_output::UiVertexOutput;
@group(1) @binding(0) var<uniform> size:vec4<f32>;
@group(1) @binding(1) var<uniform> state:vec4<f32>;
const TAU:f32=6.28318530718;
fn over(a:vec4<f32>,b:vec4<f32>)->vec4<f32>{let alpha=a.a+b.a*(1.0-a.a);if alpha<0.00001{return vec4<f32>(0.0);}return vec4<f32>((a.rgb*a.a+b.rgb*b.a*(1.0-a.a))/alpha,alpha);}
fn linear(c:vec3<f32>)->vec3<f32>{return select(c/12.92,pow((c+0.055)/1.055,vec3<f32>(2.4)),c>vec3<f32>(0.04045));}
// Original DrawAnnularSector makes floor(angle/0.12)+1 straight segments.
fn polygon_radius(radius:f32,angle:f32,total:f32)->f32 {
    let steps=max(2.0,floor(total/0.12)+1.0);let step=total/steps;
    return radius*cos(step*0.5)/cos((angle-floor(angle/step)*step)-step*0.5);
}
fn sector(p:vec2<f32>,inner:f32,outer:f32,total:f32)->f32 {
    let a=atan2(p.x,-p.y);let angle=select(a,a+TAU,a<0.0);
    if total<=0.0||angle>total{return 0.0;}
    let r=length(p);return select(0.0,1.0,r>=polygon_radius(inner,angle,total)&&r<=polygon_radius(outer,angle,total));
}
fn line_distance(p:vec2<f32>,a:vec2<f32>,b:vec2<f32>)->f32 {let d=b-a;return length(p-a-d*clamp(dot(p-a,d)/max(dot(d,d),0.00001),0.0,1.0));}
// DrawArc's point_count=40 includes both endpoints.
fn arc(p:vec2<f32>,radius:f32,total:f32,width:f32)->f32 {
    if total<=0.0{return 0.0;}var dist=100000.0;
    for(var i=0;i<39;i++){let a=total*f32(i)/39.0;let b=total*f32(i+1)/39.0;
        dist=min(dist,line_distance(p,vec2<f32>(sin(a),-cos(a))*radius,vec2<f32>(sin(b),-cos(b))*radius));}
    return 1.0-smoothstep(width*0.5-0.5,width*0.5+0.5,dist);
}
@fragment fn fragment(in:UiVertexOutput)->@location(0) vec4<f32> {
    if state.w<=0.0{return vec4<f32>(0.0);}let p=(in.uv-vec2<f32>(0.5))*size.xy;
    let small=min(size.x,size.y);var color=vec4<f32>(0.0);
    if state.y<0.5 {
        let outer=small*0.40;let inner=outer*0.58;
        color=vec4<f32>(0.0,0.0,0.0,0.55*(1.0-smoothstep(outer+2.5,outer+3.5,length(p))));
        color=over(vec4<f32>(1.0,1.0,1.0,0.16*sector(p,inner,outer,TAU)),color);
        let tint=select(vec3<f32>(0.35,0.90,0.45),vec3<f32>(0.95,0.45,0.20),state.z>0.5);
        color=over(vec4<f32>(tint,sector(p,inner,outer,TAU*clamp(state.x,0.0,1.0))),color);
    }else{
        let radius=small*0.34;color=vec4<f32>(0.0,0.0,0.0,0.45*arc(p,radius,TAU,4.0));
        let tint=select(vec3<f32>(1.0,0.82,0.35),vec3<f32>(0.45,0.78,1.0),state.z>0.5);
        color=over(vec4<f32>(tint,arc(p,radius,TAU*clamp(state.x,0.0,1.0),3.0)),color);
    }
    return vec4<f32>(linear(color.rgb),color.a*state.w);
}
