import bevy_ui_render::ui_vertex_output::UiVertexOutput;
@group(1) @binding(0) var<uniform> size: vec4<f32>;
@group(1) @binding(1) var<uniform> state: vec4<f32>;
@group(1) @binding(2) var<uniform> pops: vec4<f32>;
const PI:f32=3.14159265359;
fn over(a:vec4<f32>,b:vec4<f32>)->vec4<f32>{
    let alpha=a.a+b.a*(1.0-a.a);
    if alpha<0.00001{return vec4<f32>(0.0);}
    return vec4<f32>((a.rgb*a.a+b.rgb*b.a*(1.0-a.a))/alpha,alpha);
}
fn arc(r:f32,at:f32,width:f32)->f32{return 1.0-smoothstep(width*0.5-0.6,width*0.5+0.6,abs(r-at));}
fn srgb(c:vec3<f32>)->vec3<f32>{return select(c/12.92,pow((c+0.055)/1.055,vec3<f32>(2.4)),c>vec3<f32>(0.04045));}
@fragment fn fragment(in:UiVertexOutput)->@location(0) vec4<f32>{
    if size.z<=0.0{return vec4<f32>(0.0);}
    let p=(in.uv-vec2<f32>(0.5))*size.xy/size.z;
    let r=length(p);let inner=85.5*state.w;let base=326.8*state.w;
    // The shared canvas backdrop already frosts/dims the scene and base HUD.
    var col=vec4<f32>(0.0);
    if r>=inner-7.0&&r<=base+7.0{col=over(vec4<f32>(0.03,0.035,0.05,0.8),col);}
    col=over(vec4<f32>(1.0,1.0,1.0,0.1*arc(r,base+7.0,2.0)),col);
    let a=atan2(p.y,p.x);let n=fract((a+PI*0.75)/(2.0*PI))*4.0;
    let i=u32(floor(n));let pop=pops[i];let outer=base+16.0*pop;
    let selectable=i!=3u||state.y>0.5;
    var ci=vec4<f32>(0.10,0.105,0.12,0.62);var co=vec4<f32>(0.12,0.125,0.14,0.62);
    if selectable{ci=mix(vec4<f32>(0.10,0.115,0.15,0.90),vec4<f32>(0.14,0.24,0.38,0.94),pop);co=mix(vec4<f32>(0.17,0.19,0.23,0.90),vec4<f32>(0.26,0.44,0.68,0.96),pop);}
    if r>=inner&&r<=outer{col=over(mix(ci,co,clamp((r-inner)/(outer-inner),0.0,1.0)),col);}
    let edge=min(fract(n),1.0-fract(n));
    if selectable&&edge>0.013{
        var kind=vec3<f32>(0.0);var tab=false;
        if i==3u{kind=vec3<f32>(0.90,0.42,0.36);tab=true;}
        else if state.y>0.5&&i==u32(state.z){kind=vec3<f32>(0.50,0.84,0.52);tab=true;}
        if tab{col=over(vec4<f32>(kind,arc(r,inner+4.0,6.0)*(0.75+0.25*pop)),col);}
    }
    if pop>0.01&&edge>0.007{
        for(var g=3;g>=1;g--){col=over(vec4<f32>(0.4,0.62,0.9,pop*0.12*f32(4-g)*arc(r,outer+f32(g)*4.0,8.0)),col);}
        col=over(vec4<f32>(0.72,0.86,1.0,pop*arc(r,outer-2.0,4.0)),col);
    }
    if r>inner+1.0&&r<base+6.0{let distance=edge*PI*0.5*r;col=over(vec4<f32>(0.02,0.02,0.03,0.85*(1.0-smoothstep(0.65,1.85,distance))),col);}
    let cancel=state.x<0.0;
    if r<inner{col=over(select(vec4<f32>(0.05,0.06,0.08,0.96),vec4<f32>(0.18,0.30,0.46,0.96),cancel),col);}
    col=over(select(vec4<f32>(1.0,1.0,1.0,0.16),vec4<f32>(0.72,0.86,1.0,1.0),cancel)*vec4<f32>(1.0,1.0,1.0,arc(r,inner,select(2.0,3.5,cancel))),col);
    if !cancel&&(state.x<2.5||state.y>0.5){
        let angle=-PI*0.5+state.x*PI*0.5;let dir=vec2<f32>(cos(angle),sin(angle));
        let along=dot(p,dir);let side=abs(dot(p,vec2<f32>(-dir.y,dir.x)));
        if along>=inner-13.0&&along<=inner+1.0&&side<11.0*(inner+1.0-along)/14.0{col=over(vec4<f32>(0.72,0.86,1.0,1.0),col);}
    }
    return vec4<f32>(srgb(col.rgb),col.a*size.w);
}
