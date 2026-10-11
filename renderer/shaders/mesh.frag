#version 450
#extension GL_GOOGLE_include_directive : require
#include "scene_data.glsl"
#include "material.glsl"
layout(location = 0) in vec3 worldPosition;
layout(location = 1) in vec3 worldNormal;
layout(location = 2) in vec2 uv;
layout(location = 3) in vec4 color;
layout(location = 4) in vec4 tangent;
layout(location = 0) out vec4 outColor;
const float PI = 3.14159265359;
float visibility(vec3 geometricNormal) {
    if (scene.shadowSettings.x < .5) return 1;
    vec4 clip = scene.sunViewProjection * vec4(worldPosition + geometricNormal * scene.shadowSettings.w, 1);
    vec3 p = clip.xyz / clip.w;
    p.xy = p.xy * .5 + .5;
    if (any(lessThan(p, vec3(0))) || any(greaterThan(p, vec3(1)))) return 1;
    float result = 0;
    for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
            result += texture(shadowMap, vec3(p.xy + vec2(x,y) * scene.shadowSettings.y, p.z - scene.shadowSettings.z));
    return result / 9;
}
uint pcg(uint v) {
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}
uint cell_hash(ivec3 c) { return pcg(uint(c.x) + pcg(uint(c.y) + pcg(uint(c.z)))); }
float unit(uint h) { return float(h) * (1.0 / 4294967295.0); }
// One scale of settlements: at most one light per 3D cell on the unit sphere, present with probability
// `density`. A light narrower than the pixel footprint spreads over the pixel at constant total energy,
// so distant points stay stable instead of shimmering. Returns warm-to-white radiance.
vec3 settlements(vec3 p, float frequency, float radius, float density, float pixel, uint salt) {
    vec3 q = p * frequency;
    ivec3 base = ivec3(floor(q));
    vec3 sum = vec3(0);
    for (int z = -1; z <= 1; ++z) for (int y = -1; y <= 1; ++y) for (int x = -1; x <= 1; ++x) {
        ivec3 c = base + ivec3(x, y, z);
        uint h = cell_hash(c) ^ salt;
        if (unit(pcg(h)) > density) continue;
        vec3 point = (vec3(c) + vec3(unit(pcg(h + 1u)), unit(pcg(h + 2u)), unit(pcg(h + 3u)))) / frequency;
        float size = radius * mix(.35, 1., unit(pcg(h + 4u)));
        float spread = max(size, pixel);
        float d2 = dot(p - point, p - point);
        vec3 tint = mix(vec3(1, .58, .24), vec3(1, .86, .62), unit(pcg(h + 5u)));
        sum += tint * (size * size) / (spread * spread) * exp(-d2 / (spread * spread));
    }
    return sum;
}
vec3 city_lights(vec3 p, float density, float pixel) {
    // Sparse by design: most land stays dark, and lights concentrate where density is high.
    float d2 = density * density;
    vec3 light = settlements(p, 55., .003, density * .4, pixel, 0x9e3779b9u) * 1.4
               + settlements(p, 170., .0011, density * .22, pixel, 0x85ebca6bu) * .8
               + settlements(p, 480., .00045, d2 * .25, pixel, 0xc2b2ae35u) * .3
               + settlements(p, 1400., .00016, d2 * .7, pixel, 0x27d4eb2fu) * .55;
    // Faint sprawl haze only at the densest cores (capitals).
    return light + vec3(1, .6, .28) * pow(density, 10.) * .1;
}
void main() {
    // Evaluate before discard/branches so every fragment in the derivative quad participates.
    vec3 dx = dFdx(worldPosition), dy = dFdy(worldPosition);
    vec2 ux = dFdx(uv), uy = dFdy(uv);
    // Pixel footprint on the unit globe, taken before any branch (derivatives need uniform control flow).
    float pixel = length(fwidth(normalize(worldPosition)));
    // Vertex colour alpha is ambient occlusion (baked sky visibility, 1 when not baked), not opacity.
    vec4 base = texture(colorTex, uv) * material.colorFactors * vec4(color.rgb, 1);
    float occlusion = color.a;
    if (base.a < material.alphaMask.x) discard;
    vec3 geometricNormal = normalize(worldNormal) * (gl_FrontFacing ? 1 : -1);
    vec3 n = geometricNormal;
    vec3 v = normalize(scene.cameraPosition.xyz - worldPosition);
    vec3 l = normalize(scene.sunlightDirection.xyz);
    if (material.materialFlags.y>2.5) {
        // Trade-route ribbon: soft across its width (uv.y), dashes flowing along it (uv.x) with replay time.
        float across = 1 - pow(abs(uv.y * 2 - 1), 2);
        float dash = .35 + .65 * smoothstep(.55, 1, fract(uv.x * material.extra[0].x - scene.nightSettings.z * material.extra[0].y));
        outColor = vec4(material.colorFactors.rgb, material.colorFactors.a * across * dash);
        return;
    }
    if (material.materialFlags.y>1.5) {
        // Cheap limb glow, not scattering.  Back faces would double the blend around the silhouette.
        if (!gl_FrontFacing) discard;
        float rim=pow(1-clamp(dot(n,v),0,1),7);
        float day=smoothstep(-.15,.35,dot(n,l))*scene.nightSettings.y;
        // Looking toward a sun behind the planet, the limb glows warm (forward scattering).
        float forward=pow(max(dot(-v,l),0),60)*smoothstep(0,.6,dot(n,l))*scene.nightSettings.y;
        float moon=smoothstep(-.1,.4,dot(n,normalize(scene.moonLight.xyz)))*min(scene.moonLight.w,1.);
        outColor=vec4(mix(vec3(.3,.65,1),vec3(1,.9,.75)*2.5,forward),min(1,rim*(day*.9+.08+moon*.35+forward*2.5)));
        return;
    }
    if (material.materialFlags.y>.5) {
        float cloud=texture(colorTex,uv).r;
        outColor=vec4(vec3(.18+max(dot(n,l),0)*.85),smoothstep(.05,.8,cloud)*.8);
        return;
    }
    if (material.materialFlags.z>.5) {
        outColor=vec4(base.rgb+material.emission.rgb,base.a);
        return;
    }
    if (material.materialFlags.x > 0) {
        vec3 t = tangent.xyz;
        float handedness = tangent.w;
        if (abs(handedness) < .5) {
            float determinant = ux.x * uy.y - ux.y * uy.x;
            t = (dx * uy.y - dy * ux.y) * sign(determinant);
            vec3 b = (dy * ux.x - dx * uy.x) * sign(determinant);
            handedness = sign(dot(cross(n, t), b));
        }
        t -= n * dot(n, t);
        vec3 sampled = texture(normalTex, uv).xyz * 2 - 1;
        sampled.xy *= material.materialFlags.x;
        if (dot(t, t) > 1e-20 && abs(handedness) > .5) {
            t = normalize(t);
            n = normalize(mat3(t, cross(n, t) * handedness, n) * sampled);
        }
    }
    vec2 mr = texture(metalRoughTex, uv).bg * material.metal_rough_factors.xy;
    float metallic = clamp(mr.x, 0, 1);
    float roughness = clamp(mr.y, .05, 1);
    bool specGloss = material.extra[1].y > .5;
    vec4 sg = texture(metalRoughTex,uv) * material.extra[0];
    if (specGloss) { metallic=0; roughness=clamp(1-sg.a,.05,1); }
    float ocean = 0, daylight = 1, cityLights = 0;
    if (material.materialFlags.w>.5) {
        // Globe only: normal-map alpha is ocean coverage; water is a broad-glint dielectric.
        ocean = texture(normalTex, uv).a;
        cityLights = texture(metalRoughTex, uv).r;
        metallic = 0;
        roughness = mix(.85, .45, ocean);
        // Bent normals must not catch sun past the terminator.
        daylight = smoothstep(-.02, .12, dot(geometricNormal, l));
    }
    vec3 h = normalize(v + l);
    float nl = max(dot(n,l), 0), nv = max(dot(n,v), .001), nh = max(dot(n,h), 0);
    vec3 f0 = mix(vec3(mix(.04, .02, ocean)), base.rgb, metallic);
    if (specGloss) f0 = clamp(sg.rgb,vec3(0),vec3(1));
    vec3 fresnel = f0 + (1 - f0) * pow(1 - max(dot(v,h), 0), 5);
    float a2 = pow(roughness, 4);
    float denom = nh * nh * (a2 - 1) + 1;
    float distribution = a2 / max(PI * denom * denom, .000001);
    float k = pow(roughness + 1, 2) / 8;
    float geometry = (nv / (nv * (1-k) + k)) * (nl / max(nl * (1-k) + k, .0001));
    vec3 specular = distribution * geometry * fresnel / max(4 * nv * nl, .0001);
    vec3 diffuse = (1 - fresnel) * (1 - metallic) * base.rgb / PI;
    vec3 direct = (diffuse + specular) * scene.sunlightColor.rgb * nl * daylight * visibility(geometricNormal);
    if (scene.moonLight.w > 0) {
        // Dim cool moonlight through the same BRDF, so relief and the ocean sheen stay readable at night.
        vec3 lm = normalize(scene.moonLight.xyz), hm = normalize(v + lm);
        float nlm = max(dot(n, lm), 0), nhm = max(dot(n, hm), 0);
        float dm = nhm * nhm * (a2 - 1) + 1;
        vec3 fm = f0 + (1 - f0) * pow(1 - max(dot(v, hm), 0), 5);
        float gm = (nv / (nv * (1 - k) + k)) * (nlm / max(nlm * (1 - k) + k, .0001));
        vec3 moonSpecular = a2 / max(PI * dm * dm, .000001) * gm * fm / max(4 * nv * nlm, .0001);
        direct += ((1 - fm) * (1 - metallic) * base.rgb / PI + moonSpecular) * nlm * vec3(.6, .7, 1) * scene.moonLight.w;
    }
    for (int i=0;i<int(scene.lightInfo.x);++i) {
        SceneLight lamp=scene.lights[i];
        vec3 delta=lamp.positionRange.xyz-worldPosition;
        float distance2=dot(delta,delta), range=lamp.positionRange.w;
        if (range>0 && distance2>=range*range) continue;
        vec3 direction=delta*inversesqrt(max(distance2,.0001));
        float attenuation=1/max(distance2,.04);
        if (range>0) attenuation*=pow(clamp(1-pow(sqrt(distance2)/range,4),0,1),2);
        if (lamp.innerType.y>.5) {
            float cone=dot(-direction,lamp.directionOuter.xyz);
            attenuation*=pow(clamp((cone-lamp.directionOuter.w)/max(lamp.innerType.x-lamp.directionOuter.w,.0001),0,1),2);
        }
        float ndl=max(dot(n,direction),0);
        if (ndl<=0 || attenuation<.000001) continue;
        vec3 halfVector=normalize(v+direction);
        float ndh=max(dot(n,halfVector),0), denominator=ndh*ndh*(a2-1)+1;
        vec3 f=f0+(1-f0)*pow(1-max(dot(v,halfVector),0),5);
        float g=(nv/(nv*(1-k)+k))*(ndl/max(ndl*(1-k)+k,.0001));
        vec3 brdf=(1-f)*(1-metallic)*base.rgb/PI+a2/max(PI*denominator*denominator,.000001)*g*f/max(4*nv*ndl,.0001);
        direct+=brdf*ndl*lamp.colorIntensity.rgb*lamp.colorIntensity.w*attenuation*scene.lightInfo.y;
    }
    // Ambient fill is an explicit approximation; replace with IBL when environment assets are available.
    // Lightweight GI: sky visibility scales the ambient, over a 30% floor standing in for light that enters through
    // the windows and bounces around the interior (unbaked meshes have occlusion 1 and are unchanged).
    vec3 ambient = (base.rgb * (1 - metallic) + f0 * .25) * scene.ambientColor.rgb * (.55 + .45 * max(n.y, 0)) * mix(.3, 1., occlusion);
    if (scene.lightInfo.z>.5) {
        vec3 reflected=reflect(-v,n);
        vec3 environment=mix(vec3(.18,.14,.1),vec3(.4,.6,.95),smoothstep(-.2,.5,reflected.y));
        ambient+=environment*f0*scene.ambientColor.rgb*(1-.6*roughness);
    }
    vec3 emission = material.emission.rgb;
    if (material.extra[1].x > .5) emission *= texture(emissiveTex,uv).rgb;
    if (cityLights > 0) {
        // Globe at the origin: the nearest capital owns this pixel, matching world_country().
        vec3 radial = normalize(worldPosition);
        int owner = 0; float best = -2;
        for (int i = 0; i < 10; ++i) {
            float d = dot(radial, scene.cityLights[i].xyz);
            if (d > best) { best = d; owner = i; }
        }
        float night = 1 - smoothstep(-.05, .25, dot(geometricNormal, l)) * scene.nightSettings.y;
        if (night > .001)
            emission += city_lights(radial, cityLights, pixel) * (1 - ocean) * scene.cityLights[owner].w * scene.nightSettings.x * night;
    }
    float transmission = material.extra[1].z;
    if (transmission > 0) base.a *= mix(1.,.12,transmission);
    outColor = vec4(direct + ambient + emission, base.a);
}
