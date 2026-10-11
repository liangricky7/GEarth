#include "procedural_world.h"
#include <glm/gtc/constants.hpp>
#include <algorithm>
#include <cmath>
#include <execution>
#include <limits>
#include <numeric>
#include <source_location>

namespace {
constexpr float SeaLevel=.035f;
uint32_t hash(uint32_t value) {
    value^=value>>16; value*=0x7feb352d; value^=value>>15; value*=0x846ca68b; return value^(value>>16);
}
float lattice(int x,int y,int z) {
    uint32_t value=hash(uint32_t(x)*73856093u ^ uint32_t(y)*19349663u ^ uint32_t(z)*83492791u ^ 20261010u);
    return float(value&65535u)/32767.5f-1.f;
}
float noise(glm::vec3 p) {
    glm::vec3 cell=glm::floor(p), f=p-cell;
    f=f*f*f*(f*(f*6.f-15.f)+10.f);
    int x=int(cell.x),y=int(cell.y),z=int(cell.z);
    float a=glm::mix(lattice(x,y,z),lattice(x+1,y,z),f.x);
    float b=glm::mix(lattice(x,y+1,z),lattice(x+1,y+1,z),f.x);
    float c=glm::mix(lattice(x,y,z+1),lattice(x+1,y,z+1),f.x);
    float d=glm::mix(lattice(x,y+1,z+1),lattice(x+1,y+1,z+1),f.x);
    return glm::mix(glm::mix(a,b,f.y),glm::mix(c,d,f.y),f.z);
}
uint32_t rgba(glm::vec3 color,float alpha=1) {
    color=glm::clamp(color,glm::vec3(0),glm::vec3(1))*255.f;
    return uint32_t(color.r)|(uint32_t(color.g)<<8)|(uint32_t(color.b)<<16)|(uint32_t(std::clamp(alpha,0.f,1.f)*255.f)<<24);
}
// Faultline's fixed capitals (backend SocietyFactory.Geography); replays are checked against these.
constexpr float CountryLatitude[FictionalCountryCount]={40,-18,50,25,-27,64,3,25,8,35};
constexpr float CountryLongitude[FictionalCountryCount]={-105,-58,15,42,135,-22,23,110,80,140};
const std::array<glm::vec3,FictionalCountryCount>& country_centers() {
    static const auto centers=[] {
        std::array<glm::vec3,FictionalCountryCount> result;
        for (int i=0;i<FictionalCountryCount;++i) result[i]=globe_position(CountryLatitude[i],CountryLongitude[i]);
        return result;
    }();
    return centers;
}
bool sea_segment(glm::vec3 a,glm::vec3 b) {
    // shortcut: 49 water samples per leg; use engine-validated geometry for live shipments.
    for (int i=0;i<=48;++i) if (world_height(globe_interpolate(a,b,i/48.f))>SeaLevel) return false;
    return true;
}
}
float world_height(glm::vec3 direction) {
    // Sampling 3D noise on the sphere avoids a longitude seam and polar stretching.
    glm::vec3 p=glm::normalize(direction)*2.2f+glm::vec3(3.1f,-1.7f,4.3f);
    float height=0, weight=.55f;
    for (int octave=0;octave<7;++octave) { height+=noise(p)*weight; p=p*2.03f+glm::vec3(7.1f,3.4f,-2.8f); weight*=.5f; }
    // Blend toward land around each capital (guaranteed at the capital, fading over ~15 degrees) and lower
    // open ocean, so most land is national without flattening terrain that is already high.
    float uplift=0;
    for (auto center : country_centers()) uplift=std::max(uplift,std::exp((glm::dot(glm::normalize(direction),center)-1.f)/.05f));
    height-=.12f;
    return std::max(height,glm::mix(height,.32f,uplift));
}
int world_country(const ProceduralWorld& world,glm::vec3 direction) {
    int result=0; float best=-2;
    for (int i=0;i<FictionalCountryCount;++i) {
        float distance=glm::dot(direction,world.countries[i].center);
        if (distance>best) { best=distance; result=i; }
    }
    return result;
}
ProceduralWorld generate_world(uint32_t width,uint32_t height) {
    if (width<8 || height<4) throw std::runtime_error("World texture is too small");
    ProceduralWorld world; world.width=width; world.height=height;
    const char* ids[]={"aster","beryl","cinder","dune","ember","fjord","grove","haven","ionia","jade"};
    const char* names[]={"Aster","Beryl","Cinder","Dune","Ember","Fjord","Grove","Haven","Ionia","Jade"};
    const glm::vec3 palette[]={{.35f,.75f,.65f},{.65f,.55f,.85f},{.86f,.55f,.45f},{.83f,.75f,.45f},{.78f,.44f,.63f},
        {.42f,.65f,.83f},{.56f,.76f,.4f},{.83f,.66f,.48f},{.75f,.8f,.83f},{.32f,.48f,.83f}};
    for (int i=0;i<FictionalCountryCount;++i) {
        glm::vec3 center=country_centers()[i];
        world.countries[i]={ids[i],names[i],center,{},palette[i],false};
    }
    size_t count=size_t(width)*height;
    world.pixels.resize(count); world.normals.resize(count); world.lights.assign(count,rgba(glm::vec3(0))); world.countryIds.assign(count,255);
    std::vector<float> elevations(count), surface(count);
    std::vector<uint32_t> rows(height); std::iota(rows.begin(),rows.end(),0u);
    // Noise dominates generation time; rows are independent.
    std::for_each(std::execution::par,rows.begin(),rows.end(),[&](uint32_t y) {
        for (uint32_t x=0;x<width;++x) {
            glm::vec3 p=globe_position(90.f-180.f*(y+.5f)/height,-180.f+360.f*(x+.5f)/width);
            size_t pixel=size_t(y)*width+x;
            float elevation=elevations[pixel]=world_height(p);
            int country=world_country(world,p);
            if (elevation>SeaLevel) {
                world.countryIds[pixel]=uint8_t(country);
                surface[pixel]=elevation;
                float land=elevation-SeaLevel, latitude=std::abs(p.y);
                // Artistic biome cues, not climate: subtropical dry belt broken up by noise, greyer highlands, pale peaks and poles.
                float dry=glm::clamp(1.f-std::abs(latitude-.42f)*4.f+noise(p*3.f+glm::vec3(11.f))*.8f,0.f,1.f);
                glm::vec3 color=glm::mix(glm::vec3(.24f,.34f,.16f),glm::vec3(.6f,.52f,.36f),dry*.85f);
                color=glm::mix(color,glm::vec3(.4f,.36f,.31f),glm::smoothstep(.15f,.35f,land));
                color=glm::mix(color,glm::vec3(.82f,.83f,.85f),std::max(glm::smoothstep(.42f,.56f,land),glm::smoothstep(.86f,.95f,latitude))*.8f);
                world.pixels[pixel]=rgba(glm::mix(color,world.countries[country].color*.8f,.25f));
                float fromCapital=std::acos(std::clamp(glm::dot(p,world.countries[country].center),-1.f,1.f));
                // Smooth settlement density only (capital, settled regions, coasts); mesh.frag scatters the
                // individual cities, towns and villages at screen resolution from it.
                // People live along coasts and river valleys (ridged-noise networks in the lowlands), more in
                // developed regions, most around the capital.
                float core=std::exp(-fromCapital*fromCapital/(2*.03f*.03f));
                float settled=std::exp(-fromCapital/.45f)*glm::smoothstep(-.4f,.5f,noise(p*6.f+glm::vec3(5.f)));
                float coast=std::exp(-land/.006f);
                float valley=std::max(std::pow(1-std::abs(noise(p*18.f+glm::vec3(1.f))),18.f),
                    std::pow(1-std::abs(noise(p*41.f+glm::vec3(8.f))),24.f)*.7f);
                float river=valley*glm::smoothstep(.3f,.04f,land);
                float region=glm::smoothstep(0.f,.6f,noise(p*14.f+glm::vec3(2.f)));
                world.lights[pixel]=rgba(glm::vec3(glm::clamp(core+settled*(coast*.95f+river*.85f+region*.35f),0.f,1.f),0,0));
            } else {
                float depth=SeaLevel-elevation;
                // Stylised ripples: unresolved at orbital range, so they only break up the sun glint.
                surface[pixel]=SeaLevel+.002f*noise(p*70.f)+.001f*noise(p*150.f+glm::vec3(5.f));
                glm::vec3 color=glm::mix(glm::vec3(.07f,.27f,.33f),glm::vec3(.03f,.1f,.24f),glm::smoothstep(0.f,.015f,depth));
                world.pixels[pixel]=rgba(glm::mix(color,glm::vec3(.01f,.04f,.13f),glm::smoothstep(.02f,.25f,depth)));
            }
        }
    });
    std::array<float,FictionalCountryCount> coastScore; coastScore.fill(-2.f);
    for (uint32_t y=0;y<height;++y) for (uint32_t x=0;x<width;++x) {
        size_t pixel=size_t(y)*width+x;
        float elevation=elevations[pixel];
        if (elevation>SeaLevel || elevation<=SeaLevel-.025f) continue;
        glm::vec3 p=globe_position(90.f-180.f*(y+.5f)/height,-180.f+360.f*(x+.5f)/width);
        int country=world_country(world,p);
        float score=glm::dot(p,world.countries[country].center);
        if (score>coastScore[country]) { coastScore[country]=score; world.countries[country].port=p; world.countries[country].hasPort=true; }
    }
    // Tangent-space normals from land relief and sea ripples; alpha is ocean coverage for the globe material.
    // Basis matches sphere_geometry: t=(-sin lon,0,-cos lon), b=cross(n,t)*-1.
    constexpr float Relief=.25f;
    auto land=[&](uint32_t x,uint32_t y) { return surface[size_t(y)*width+x]; };
    std::for_each(std::execution::par,rows.begin(),rows.end(),[&](uint32_t y) {
        float lat=glm::radians(90.f-180.f*(y+.5f)/height);
        float eastStep=2*glm::two_pi<float>()*std::cos(lat)/width, northStep=2*glm::pi<float>()/height;
        uint32_t up=y ? y-1 : 0, down=std::min(y+1,height-1);
        for (uint32_t x=0;x<width;++x) {
            float lon=glm::radians(-180.f+360.f*(x+.5f)/width);
            float east=(land((x+1)%width,y)-land((x+width-1)%width,y))/eastStep;
            float north=(land(x,up)-land(x,down))/(northStep*float(down-up)*.5f);
            glm::vec3 n=globe_position(glm::degrees(lat),glm::degrees(lon));
            glm::vec3 eastAxis{-std::sin(lon),0,-std::cos(lon)}, northAxis=glm::cross(n,eastAxis);
            glm::vec3 bent=glm::normalize(n-Relief*(east*eastAxis+north*northAxis));
            glm::vec3 t=eastAxis, b=-glm::cross(n,t);
            world.normals[size_t(y)*width+x]=rgba(glm::vec3(glm::dot(bent,t),glm::dot(bent,b),glm::dot(bent,n))*.5f+.5f,world.countryIds[size_t(y)*width+x]==255);
        }
    });
    for (uint32_t y=0;y<height;++y) for (uint32_t x=0;x<width;++x) {
        size_t pixel=size_t(y)*width+x; uint8_t id=world.countryIds[pixel];
        if (id==255) continue;
        // Two texels each way keeps borders visible after minification.
        size_t adjacent[]={size_t(y)*width+(x+2)%width,size_t(y)*width+(x+width-2)%width,
            size_t(std::min(y+2,height-1))*width+x,size_t(y>1 ? y-2 : 0)*width+x};
        for (size_t neighbor : adjacent) {
            if (world.countryIds[neighbor]!=255 && world.countryIds[neighbor]!=id) { world.pixels[pixel]=(world.pixels[pixel]>>1&0x7f7f7fu)|0xff000000u; break; }
        }
    }
    return world;
}
std::vector<DemoRoute> world_demo_routes(const ProceduralWorld& world) {
    std::vector<DemoRoute> routes;
    for (int i=0;i<FictionalCountryCount;i+=2) {
        int j=(i+3)%FictionalCountryCount;
        routes.push_back({fmt::format("{} - {} | flight",world.countries[i].name,world.countries[j].name),true,28.f+i,i*.07f,{world.countries[i].center,world.countries[j].center}});
    }
    std::vector<glm::vec3> water;
    for (int i=0;i<256;++i) {
        float y=1.f-2.f*(i+.5f)/256, r=std::sqrt(std::max(0.f,1-y*y)), angle=i*2.39996323f;
        glm::vec3 p{r*std::cos(angle),y,r*std::sin(angle)};
        if (world_height(p)<SeaLevel-.03f) water.push_back(p);
    }
    // shortcut: demo maritime paths use one ocean waypoint; live paths must come from the simulation.
    int ships=0;
    for (int i=0;i<FictionalCountryCount && ships<4;++i) for (int j=i+1;j<FictionalCountryCount && ships<4;++j) {
        if (!world.countries[i].hasPort || !world.countries[j].hasPort) continue;
        glm::vec3 a=world.countries[i].port,b=world.countries[j].port;
        if (sea_segment(a,b)) {
            routes.push_back({fmt::format("{} - {} | sea freight",world.countries[i].name,world.countries[j].name),false,55.f+ships*8,ships*.18f,{a,b}}); ++ships;
        } else {
            for (auto waypoint : water) if (sea_segment(a,waypoint) && sea_segment(waypoint,b)) {
                routes.push_back({fmt::format("{} - {} | sea freight",world.countries[i].name,world.countries[j].name),false,55.f+ships*8,ships*.18f,{a,waypoint,b}}); ++ships; break;
            }
        }
    }
    return routes;
}
void world_self_test() {
    auto check=[](bool good,std::source_location at=std::source_location::current()) {
        if (!good) throw std::runtime_error(fmt::format("Procedural world self-check failed at line {}",at.line()));
    };
    auto world=generate_world(128,64), repeat=generate_world(128,64);
    check(world.pixels==repeat.pixels && world.normals==repeat.normals && world.lights==repeat.lights && world.countryIds==repeat.countryIds);
    for (size_t i=0;i<world.normals.size();++i) {
        check(world.normals[i]>>24==(world.countryIds[i]==255 ? 255u : 0u));
        if (world.countryIds[i]==255) check((world.lights[i]&0xffu)==0);
    }
    check(std::abs(world_height(globe_position(15,-180))-world_height(globe_position(15,180)))<.00001f);
    std::array<int,FictionalCountryCount> counts{};
    int ocean=0;
    for (uint8_t id : world.countryIds) { if (id==255) ++ocean; else { check(id<FictionalCountryCount); ++counts[id]; } }
    check(ocean>0);
    for (int i=0;i<FictionalCountryCount;++i) {
        check(counts[i]>0); check(world_country(world,world.countries[i].center)==i); check(world_height(world.countries[i].center)>SeaLevel);
    }
    auto routes=world_demo_routes(world); int ships=0;
    for (auto& route : routes) {
        check(route.points.size()>=2);
        for (int i=0;i<=20;++i) check(glm::length(route_position(route,i/20.f))>1.f);
        if (!route.plane) { ++ships; for (size_t i=1;i<route.points.size();++i) check(sea_segment(route.points[i-1],route.points[i])); }
    }
    check(ships>0);
    fmt::print("Procedural world: ten countries, seeded texture, seam and sea-route checks passed\n");
}
