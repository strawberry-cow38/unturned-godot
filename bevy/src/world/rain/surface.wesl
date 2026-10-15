// Source puddles.gdshaderinc and rain_impacts.gdshaderinc, shared by props/terrain.
struct RainUniform { roof_rect:vec4<f32>,weather:vec4<f32>,slant_origin:vec4<f32> }
fn roof_shelter(roof:texture_2d<f32>,rect:vec4<f32>,point:vec3<f32>,margin:f32)->f32 {
    if rect.z<=0.0{return 0.0;}
    let uv=(point.xz-rect.xy)/(2.0*rect.z)+0.5;
    if any(uv<vec2<f32>(0.0))||any(uv>vec2<f32>(1.0)){return 0.0;}
    let size=vec2<i32>(textureDimensions(roof));let t=uv*vec2<f32>(size)-0.5;let b=vec2<i32>(floor(t));let f=fract(t);
    var sum=0.0;
    for(var j=0;j<2;j++){for(var i=0;i<2;i++){
        let c=clamp(b+vec2<i32>(i,j),vec2<i32>(0),size-1);
        let under=select(0.0,1.0,point.y<textureLoad(roof,c,0).r-margin);
        sum+=under*mix(1.0-f.x,f.x,f32(i))*mix(1.0-f.y,f.y,f32(j));
    }}return sum;
}
fn pud_hash(p:vec2<f32>)->f32{var p3=fract(vec3<f32>(p.x,p.y,p.x)*0.1031);p3+=dot(p3,p3.yzx+33.33);return fract((p3.x+p3.y)*p3.z);}
fn pud_grad(p:vec2<f32>)->vec2<f32>{let h=pud_hash(p)*6.2831853;return vec2<f32>(cos(h),sin(h));}
fn pud_noise(p:vec2<f32>)->f32{
    let i=floor(p);let f=fract(p);let u=f*f*f*(f*(f*6.0-15.0)+10.0);
    let a=dot(pud_grad(i),f);let b=dot(pud_grad(i+vec2<f32>(1.0,0.0)),f-vec2<f32>(1.0,0.0));
    let c=dot(pud_grad(i+vec2<f32>(0.0,1.0)),f-vec2<f32>(0.0,1.0));let d=dot(pud_grad(i+vec2<f32>(1.0)),f-vec2<f32>(1.0));
    return mix(mix(a,b,u.x),mix(c,d,u.x),u.y)*0.8+0.5;
}
fn puddle_mask(xz:vec2<f32>,level:f32,up:f32,range:f32)->f32{
    if level<=0.01{return 0.0;}
    let depth=pud_noise(xz*0.19)*0.72+pud_noise(xz*0.62)*0.28;
    let line=mix(0.10,0.46,clamp(level,0.0,1.0));let aa=max(fwidth(depth)*1.5,0.004);
    return (1.0-smoothstep(line-aa,line+aa,depth))*up*range*smoothstep(0.0,0.12,level);
}
fn h21(input:vec2<f32>)->f32{var p=input-floor(input/256.0)*256.0;p=fract(p*vec2<f32>(127.32,311.7));p+=dot(p,p+34.53);return fract(p.x*p.y);}
fn impacts(xz:vec2<f32>,time:f32,amount:f32,away:vec2<f32>,slant:vec2<f32>)->vec2<f32>{
    var ring=0.0;var crown=0.0;let gate=1.0-clamp(amount,0.0,1.0)*0.20;let want=dot(away,away)>0.5;
    let caxis=away+slant;let caxlen=length(caxis);var cdir=away;if caxlen>0.0001{cdir=caxis/caxlen;}let clen=max(caxlen,0.35);
    for(var k=0;k<2;k++){
        let sc=1.6+f32(k)*2.8;let g=xz*sc+f32(k)*21.0;let base=floor(g);
        for(var dy=-1;dy<=1;dy++){for(var dx=-1;dx<=1;dx++){
            let id=base+vec2<f32>(f32(dx),f32(dy));let seed=h21(id);let tt=time*(0.65+seed*0.7)+seed;
            let life=fract(tt);let q=id+floor(tt)*13.7;let center=id+vec2<f32>(h21(q+1.3),h21(q+7.7));
            let fire=step(gate,h21(q))*(0.55+seed*0.45);let d=g-center;let radius=life*(0.16+seed*0.20);
            ring+=(1.0-smoothstep(0.0,0.05,abs(length(d)-radius)))*(1.0-life)*fire;
            if !want{continue;}let up=1.0-smoothstep(0.0,0.34,life);if up<=0.0{continue;}
            let len=0.150*sc*up*clen;let along=clamp(dot(d,cdir),0.0,len);let lateral=length(d-cdir*along);
            let taper=1.0-along/max(len,0.0001);let width=mix(0.008,0.042,taper);
            crown+=(1.0-smoothstep(0.0,width,lateral))*taper*up*fire;
        }}
    }return vec2<f32>(ring,crown);
}
