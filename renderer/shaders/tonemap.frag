#version 450
layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(push_constant) uniform DisplaySettings { vec4 settings; vec4 sun; } push;
layout(location = 0) out vec4 outColor;

// A 3x3 tent at one mip level hides the blocky texels of the small levels.
vec3 tent(vec2 uv, float lod)
{
    vec2 t = 1.0 / vec2(textureSize(bloomImage, int(lod)));
    vec3 sum = textureLod(bloomImage, uv, lod).rgb * 4.0;
    sum += (textureLod(bloomImage, uv + vec2(t.x, 0), lod).rgb + textureLod(bloomImage, uv - vec2(t.x, 0), lod).rgb
          + textureLod(bloomImage, uv + vec2(0, t.y), lod).rgb + textureLod(bloomImage, uv - vec2(0, t.y), lod).rgb) * 2.0;
    sum += textureLod(bloomImage, uv + t, lod).rgb + textureLod(bloomImage, uv - t, lod).rgb
         + textureLod(bloomImage, uv + vec2(t.x, -t.y), lod).rgb + textureLod(bloomImage, uv + vec2(-t.x, t.y), lod).rgb;
    return sum / 16.0;
}

vec3 bloom(vec2 uv)
{
    vec3 sum = vec3(0.0);
    for (int lod = 1; lod <= 6; ++lod) sum += tent(uv, float(lod));
    return sum / 6.0;
}

void main() {
    vec2 uv = gl_FragCoord.xy / vec2(textureSize(hdrImage, 0));
    vec3 radiance = texelFetch(hdrImage, ivec2(gl_FragCoord.xy), 0).rgb;
    if (push.settings.w > 0) radiance = mix(radiance, bloom(uv), push.settings.w);
    if (push.sun.z > 0) {
        vec2 d = (uv - push.sun.xy) * vec2(push.sun.w, 1);
        float r = length(d);
        float glare = 6.0 * exp(-r / .008) + .5 * exp(-r / .035) + .3 * exp(-abs(d.y) * 300.0 - abs(d.x) * 7.0);
        radiance += vec3(1.0, .93, .82) * glare * push.sun.z;
    }
    vec3 x = max(radiance * push.settings.x, 0);
    // Narkowicz's published ACES fit, followed by the sRGB transfer function.
    vec3 mapped = clamp((2.51 * x * x + .03 * x) / (2.43 * x * x + .59 * x + .14), 0, 1);
    vec3 display;
    for (int channel = 0; channel < 3; ++channel)
        display[channel] = mapped[channel] <= .0031308 ? 12.92 * mapped[channel]
            : 1.055 * pow(mapped[channel], 1.0 / 2.4) - .055;
    outColor = vec4(display, 1);
}
