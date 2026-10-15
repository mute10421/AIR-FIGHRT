#ifndef AF_SKY_COMMON
#define AF_SKY_COMMON
// Shared analytic sky (used by the skybox, the ocean reflection and the clouds so everything matches).
// Sun direction / colour are set globally from C# (GraphicsSetup): _AF_SunDir (towards the sun), _AF_SunColor.
float4 _AF_SunDir;
float4 _AF_SunColor;       // colour × intensity / 2 (≈ 1 at noon)
float _AF_Night;           // 0 day … 1 night (the main light is then the moon)

// Cloud shadows: a ground-projected map of the cloud field (built by WorldBuilder), 1 = sunlit, lower = shaded.
TEXTURE2D(_AF_CloudShadow); SAMPLER(sampler_AF_CloudShadow);
float4 _AF_CloudShadowST;   // xy = scale, zw = offset (world xz → uv)
float AF_CloudShadow(float3 wp)
{
    float2 uv = wp.xz * _AF_CloudShadowST.xy + _AF_CloudShadowST.zw;
    return SAMPLE_TEXTURE2D_LOD(_AF_CloudShadow, sampler_AF_CloudShadow, uv, 0).r;
}

// Baked island lighting (WorldBuilder.BakeTerrainLight): x = direct sun (mountain shadows), y = sky visibility (AO).
TEXTURE2D(_AF_TerrainLight); SAMPLER(sampler_AF_TerrainLight);
float4 _AF_TerrainST;
float2 AF_TerrainLight(float3 wp)
{
    float2 uv = wp.xz * _AF_TerrainST.xy + _AF_TerrainST.zw;
    if (any(uv < 0.0) || any(uv > 1.0)) return float2(1, 1);
    return SAMPLE_TEXTURE2D_LOD(_AF_TerrainLight, sampler_AF_TerrainLight, uv, 0).xy;
}
// Ground height under the sea (m) → water depth.
TEXTURE2D(_AF_TerrainHeight); SAMPLER(sampler_AF_TerrainHeight);
float AF_GroundHeight(float3 wp)
{
    float2 uv = wp.xz * _AF_TerrainST.xy + _AF_TerrainST.zw;
    if (any(uv < 0.0) || any(uv > 1.0)) return -500.0;
    return SAMPLE_TEXTURE2D_LOD(_AF_TerrainHeight, sampler_AF_TerrainHeight, uv, 0).r;
}

float3 AF_SunDirection() { return normalize(_AF_SunDir.xyz + float3(0, 1e-4, 0)); }

float3 AF_SkyColorDay(float3 dir)
{
    float3 sunDir = AF_SunDirection();
    float y = dir.y;
    float yc = max(y, 0.0);
    // Rayleigh-like gradient: deep blue zenith → pale hazy horizon
    float3 zenith  = float3(0.11, 0.30, 0.72);
    float3 horizon = float3(0.66, 0.76, 0.90);
    float t = pow(1.0 - yc, 5.0);
    float3 col = lerp(zenith, horizon, t);
    // warmer light when the sun is low
    float sunH = saturate(sunDir.y);
    float3 sunTint = lerp(float3(1.0, 0.55, 0.30), float3(1.0, 0.93, 0.85), saturate(sunH * 2.5));
    float mu = dot(dir, sunDir);
    // Mie forward scattering: broad glow + tight aureole around the sun, strongest near the horizon
    float mie = 0.18 * pow(saturate(mu), 6.0) + 0.55 * pow(saturate(mu), 48.0) + 1.6 * pow(saturate(mu), 600.0);
    col += sunTint * mie * (0.35 + 0.65 * t);
    // horizon haze band toward the sun side
    col += sunTint * 0.12 * t * saturate(mu * 0.5 + 0.5);
    // below the horizon: blend into the far haze / ground colour (matches the fog colour)
    float3 below = float3(0.55, 0.62, 0.70);
    if (y < 0.0) col = lerp(horizon, below, saturate(-y * 8.0));
    return col;
}


// Moonlit night sky: deep blue-black, a faint glow around the moon, slightly lighter haze at the horizon.
float3 AF_SkyColorNight(float3 dir)
{
    float yc = max(dir.y, 0.0);
    float3 zenith = float3(0.0022, 0.0040, 0.011);
    float3 horizon = float3(0.010, 0.014, 0.024);
    float3 col = lerp(zenith, horizon, pow(1.0 - yc, 4.0));
    float mu = dot(dir, AF_SunDirection());
    col += float3(0.020, 0.024, 0.032) * pow(saturate(mu), 24.0) + float3(0.003, 0.004, 0.006) * pow(saturate(mu), 3.0);
    if (dir.y < 0.0) col = lerp(horizon, float3(0.006, 0.008, 0.011), saturate(-dir.y * 8.0));
    return col;
}

float3 AF_SkyColor(float3 dir)
{
    if (_AF_Night >= 0.999) return AF_SkyColorNight(dir);
    if (_AF_Night <= 0.001) return AF_SkyColorDay(dir);
    return lerp(AF_SkyColorDay(dir), AF_SkyColorNight(dir), _AF_Night);
}

float3 AF_SunDisk(float3 dir)
{
    float mu = dot(dir, AF_SunDirection());
    float3 sun = smoothstep(0.99982, 0.99993, mu) * float3(40.0, 37.0, 32.0);
    float3 moon = smoothstep(0.99986, 0.99992, mu) * float3(1.5, 1.6, 1.8);
    return lerp(sun, moon, _AF_Night);
}

float AF_Hash3(float3 p) { return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453); }

// Star field: stable points on a cube-mapped grid, varied brightness/colour, slight twinkle, a faint Milky Way band.
float3 AF_Stars(float3 d)
{
    float3 a = abs(d);
    float3 face; float2 uv;
    if (a.x >= a.y && a.x >= a.z) { uv = d.yz / a.x; face = float3(sign(d.x), 0, 0); }
    else if (a.y >= a.z) { uv = d.xz / a.y; face = float3(0, sign(d.y), 0); }
    else { uv = d.xy / a.z; face = float3(0, 0, sign(d.z)); }
    float2 g = uv * 260.0;
    float2 cell = floor(g);
    float h = AF_Hash3(float3(cell, dot(face, float3(1, 2, 3))));
    float3 col = 0;
    if (h > 0.985)
    {
        float2 pos = cell + 0.25 + 0.5 * float2(AF_Hash3(float3(cell, 7.1)), AF_Hash3(float3(cell, 3.3)));
        float r = length(g - pos);
        float b = pow((h - 0.985) / 0.015, 6.0) * 2.5 + 0.08;
        float tw = 0.75 + 0.25 * sin(_Time.y * (2.0 + 6.0 * h) + h * 100.0);
        float3 tint = lerp(float3(0.75, 0.82, 1.0), float3(1.0, 0.88, 0.72), AF_Hash3(float3(cell, 9.9)));
        col = tint * b * tw * exp(-r * r * 22.0);
    }
    float band = exp(-pow(dot(d, normalize(float3(0.35, 0.45, -0.82))), 2.0) * 30.0);
    col += float3(0.010, 0.011, 0.014) * band * (0.5 + AF_Hash3(floor(d * 400.0)) * 0.5);
    return col;
}
// ---- aerial perspective: height-dependent haze between the eye and a surface point ----
// Distant ground fades into the sky colour in that direction and turns bluish (Rayleigh) — what makes mountains
// 20 km away look 20 km away. Exponential atmosphere, scale height 1.4 km, sea-level visibility ≈ 45 km.
float3 AF_Aerial(float3 col, float3 wp, float3 cam)
{
    float3 d = wp - cam;
    float dist = length(d);
    float3 v = d / max(dist, 1e-3);
    const float H = 1400.0;
    const float beta = 8.5e-5;
    float h0 = max(cam.y, 0.0), h1 = max(wp.y, 0.0);
    float dh = h1 - h0;
    float rho = abs(dh) > 1.0 ? H * (exp(-h0 / H) - exp(-h1 / H)) / dh : exp(-h0 / H);
    float tau = beta * dist * rho;
    float3 T = exp(-tau * float3(0.78, 0.92, 1.18));          // blue light is scattered out first → distant things go blue-grey
    float3 hz = normalize(float3(v.x, max(v.y, 0.0) * 0.35 + 0.015, v.z));
    float3 inscatter = AF_SkyColor(hz);
    return col * T + inscatter * (1.0 - T);
}

#endif
