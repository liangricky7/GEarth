#include "town.h"
#include <simdjson.h>
#include <glm/gtc/constants.hpp>
#include <glm/gtc/matrix_transform.hpp>
#include <algorithm>
#include <map>

namespace {
constexpr float SquareRadius=10, WalkSpeed=1.3f, StepLength=1.6f, MeetGap=.9f, CallRange=6;
void require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
std::string goods_text(const std::vector<std::pair<std::string,int64_t>>& goods) {
    std::string text;
    for (const auto& [good,quantity] : goods) text+=(text.empty() ? "" : " and ")+fmt::format("{} {}",quantity,good);
    return text;
}
}

float Town::random() { seed=seed*1664525u+1013904223u; return float(seed>>8)/16777216.f; }
void Town::wander(Townsperson& person) {
    float angle=random()*glm::two_pi<float>(), radius=SquareRadius*std::sqrt(random());
    person.target={radius*std::sin(angle),radius*std::cos(angle)};
}
void Town::update(float seconds) {
    clock+=seconds;
    for (size_t i=0;i<people.size();++i) {
        auto& person=people[i];
        if (person.talk>0) {
            // End both sides together (the one updated after the chat began is a frame behind).
            if ((person.talk-=seconds)<=0) {
                auto& other=people[person.partner];
                person.talk=other.talk=0; other.partner=person.partner=-1;
                wander(person); wander(other);
            }
            continue;
        }
        glm::vec2 to=person.target-person.position;
        float distance=glm::length(to);
        if (distance<.01f) {
            if (person.partner>=0) {
                // Waiting at the meeting spot; once both are there they face each other and talk for the same time.
                auto& other=people[person.partner];
                glm::vec2 between=other.position-person.position;
                if (glm::length(other.target-other.position)<.01f) {
                    person.talk=other.talk=4+3*random();
                    person.heading=std::atan2(between.x,between.y); other.heading=std::atan2(-between.x,-between.y);
                } else // turn to watch them come over (draw() waves meanwhile)
                    person.heading+=std::remainder(std::atan2(between.x,between.y)-person.heading,glm::two_pi<float>())*std::min(1.f,seconds*8);
                continue;
            }
            // Arrived alone: often call the nearest free person over if they're close (meeting where this person stands
            // spreads chats across the square; meeting halfway would pile everyone into the middle), otherwise wander on.
            int j=-1; float nearest=CallRange;
            for (size_t k=0;k<people.size();++k) {
                float d=glm::length(people[k].position-person.position);
                if (k!=i && people[k].partner<0 && people[k].talk<=0 && d<nearest) { nearest=d; j=int(k); }
            }
            if (j<0 || random()<.4f) { wander(person); continue; }
            auto& other=people[j];
            glm::vec2 across=other.position-person.position;
            across=glm::length(across)>.001f ? glm::normalize(across) : glm::vec2(1,0);
            person.target=person.position; other.target=person.position+across*MeetGap;
            person.partner=j; other.partner=int(i);
            continue;
        }
        float step=std::min(distance,WalkSpeed*seconds);
        person.position+=to/distance*step; person.stride+=step;
        float wanted=std::atan2(to.x,to.y);
        person.heading+=std::remainder(wanted-person.heading,glm::two_pi<float>())*std::min(1.f,seconds*8);
    }
}
void Town::draw(int country, const std::function<void(int,const glm::mat4&)>& box) const {
    auto at=[](const glm::mat4& frame,glm::vec3 position,glm::vec3 size) { return glm::scale(glm::translate(frame,position),size); };
    box(Grass,at(glm::mat4(1),{0,-.5f,0},{400,1,400}));
    box(Stone,at(glm::mat4(1),{0,-.45f,0},{30,1,30}));
    // Market stalls ring the square under the country's colour; houses stand behind them.
    for (int i=0;i<8;++i) {
        float angle=i*glm::two_pi<float>()/8+.2f;
        glm::mat4 stall=glm::rotate(glm::translate(glm::mat4(1),{13*std::sin(angle),0,13*std::cos(angle)}),angle,glm::vec3(0,1,0));
        box(Wood,at(stall,{0,.5f,0},{3,1,1.4f}));
        for (float x : {-1.4f,1.4f}) for (float z : {-.6f,.6f}) box(Wood,at(stall,{x,1.25f,z},{.12f,2.5f,.12f}));
        box(Roof+country,at(stall,{0,2.55f,0},{3.4f,.15f,1.9f}));
        for (float x : {-.8f,0.f,.8f}) box(Cloth,at(stall,{x,1.15f,0},{.45f,.3f,.45f}));
        glm::mat4 house=glm::rotate(glm::translate(glm::mat4(1),{21*std::sin(angle+.4f),0,21*std::cos(angle+.4f)}),angle+.4f,glm::vec3(0,1,0));
        box(Stone,at(house,{0,2,0},{6,4,5}));
        box(Roof+country,at(house,{0,4.3f,0},{6.6f,.6f,5.6f}));
    }
    for (const auto& person : people) {
        glm::mat4 body=glm::rotate(glm::translate(glm::mat4(1),{person.position.x,0,person.position.y}),person.heading,glm::vec3(0,1,0));
        // Procedural walk: limbs swing with distance walked (so feet don't slide), body bobs twice per step.
        bool talking=person.talk>0;
        float phase=person.stride*glm::two_pi<float>()/StepLength;
        float swing=talking ? 0 : std::sin(phase), lift=talking ? 0 : std::abs(std::sin(phase))*.06f;
        auto limb=[&](int color,glm::vec3 pivot,float angle,glm::vec3 size) {
            box(color,at(glm::rotate(glm::translate(body,pivot+glm::vec3(0,lift,0)),angle,glm::vec3(1,0,0)),{0,-size.y*.5f,0},size));
        };
        limb(Pants,{-.11f,.85f,0},swing*.6f,{.16f,.85f,.18f});
        limb(Pants,{.11f,.85f,0},-swing*.6f,{.16f,.85f,.18f});
        box(Shirt+person.shirt,at(body,{0,1.15f+lift,0},{.44f,.62f,.26f}));
        box(Skin+person.skin,at(body,{0,1.62f+lift,0},{.26f,.28f,.26f}));
        // While talking, one arm gestures; while waiting for someone called over, it waves high.
        bool waiting=!talking && person.partner>=0 && glm::length(person.target-person.position)<.01f;
        float gesture=talking ? -1.1f-.35f*std::sin(clock*5+float(person.shirt)) : waiting ? -2.7f+.35f*std::sin(clock*9) : -swing*.5f;
        limb(Shirt+person.shirt,{-.29f,1.42f,0},gesture,{.12f,.6f,.14f});
        limb(Shirt+person.shirt,{.29f,1.42f,0},swing*.5f,{.12f,.6f,.14f});
    }
}

Town town_from_snapshot(std::string_view snapshot, std::string_view previous, std::string_view countryId, size_t limit) {
    std::map<std::string,std::map<std::string,int64_t>> before;
    simdjson::dom::parser parser;
    if (!previous.empty()) for (auto agent : parser.parse(simdjson::padded_string(previous))["agents"].get_array().value()) {
        auto& inventory=before[std::string(agent["id"].get_string().value())];
        for (auto [good,quantity] : agent["inventory"].get_object().value()) inventory[std::string(good)]=quantity.get_int64().value();
    }
    Town town; town.recorded=true;
    std::vector<std::pair<int,Townsperson>> ranked;
    for (auto agent : parser.parse(simdjson::padded_string(snapshot))["agents"].get_array().value()) {
        if (agent["country_id"].get_string().value()!=countryId) continue;
        Townsperson person;
        person.id=std::string(agent["id"].get_string().value());
        person.role=std::string(agent["role"].get_string().value());
        std::map<std::string,int64_t> inventory;
        for (auto [good,quantity] : agent["inventory"].get_object().value()) inventory[std::string(good)]=quantity.get_int64().value();
        std::vector<std::pair<std::string,int64_t>> gained, gave;
        if (auto old=before.find(person.id); old!=before.end()) {
            for (const auto& [good,quantity] : old->second) inventory.try_emplace(good,0);
            for (const auto& [good,quantity] : inventory) {
                auto was=old->second.contains(good) ? old->second.at(good) : 0;
                if (quantity>was) gained.emplace_back(good,quantity-was); else if (quantity<was) gave.emplace_back(good,was-quantity);
            }
        }
        int rank=2;
        if (!gained.empty() && !gave.empty()) { person.line=fmt::format("Traded {} for {}",goods_text(gave),goods_text(gained)); rank=0; }
        else if (!gained.empty()) { person.line=fmt::format("Picked up {}",goods_text(gained)); rank=0; }
        else if (!gave.empty()) { person.line=fmt::format("Let go of {}",goods_text(gave)); rank=0; }
        else if (agent["hungry"].get_bool().value()) { person.line="Anyone selling food? I'm starving"; rank=1; }
        else person.line=fmt::format("Just a {} looking for a deal",person.role);
        ranked.emplace_back(rank,std::move(person));
    }
    // People with something to say come first.
    std::stable_sort(ranked.begin(),ranked.end(),[](const auto& a,const auto& b) { return a.first<b.first; });
    for (auto& [rank,person] : ranked) {
        if (town.people.size()>=limit) break;
        town.people.push_back(std::move(person));
    }
    for (auto& person : town.people) {
        person.shirt=int(std::hash<std::string>{}(person.role)%6); person.skin=int(std::hash<std::string>{}(person.id)%3);
        town.wander(person); person.position=person.target; town.wander(person);
    }
    return town;
}
CountryAgents country_agents(std::string_view snapshot, std::string_view countryId) {
    CountryAgents result;
    std::map<std::string,int> roles;
    simdjson::dom::parser parser;
    for (auto agent : parser.parse(simdjson::padded_string(snapshot))["agents"].get_array().value()) {
        if (agent["country_id"].get_string().value()!=countryId) continue;
        ++result.people; ++roles[std::string(agent["role"].get_string().value())];
        result.hungry+=agent["hungry"].get_bool().value();
        result.health+=float(agent["health"].get_double().value()); result.wellbeing+=float(agent["wellbeing"].get_double().value());
        result.cashMinor+=agent["cash_minor"].get_int64().value();
    }
    if (result.people) { result.health/=float(result.people); result.wellbeing/=float(result.people); }
    result.roles.assign(roles.begin(),roles.end());
    std::stable_sort(result.roles.begin(),result.roles.end(),[](const auto& a,const auto& b) { return a.second>b.second; });
    return result;
}
Town demo_town(size_t count) {
    const char* roles[]={"farmer","miner","smith","merchant","doctor","baker"};
    const char* goods[]={"food","energy","tools","medicine"};
    Town town;
    for (size_t i=0;i<count;++i) {
        Townsperson person;
        person.id=fmt::format("demo-{}",i); person.role=roles[i%6]; person.shirt=int(i%6); person.skin=int(i%3);
        int a=int(town.random()*4)%4, b=(a+1+int(town.random()*3))%4;
        person.line=fmt::format("Trade you {} {} for {} {}?",1+int(town.random()*4),goods[a],1+int(town.random()*4),goods[b]);
        town.wander(person); person.position=person.target; town.wander(person);
        town.people.push_back(std::move(person));
    }
    return town;
}
void town_geometry(const ProceduralWorld& world, std::vector<Vertex>& vertices, std::vector<uint32_t>& indices) {
    std::array<glm::vec3,TownColorCount> colors{};
    colors[Grass]={.36f,.55f,.27f}; colors[Stone]={.72f,.69f,.63f}; colors[Wood]={.55f,.38f,.24f};
    colors[Cloth]={.9f,.8f,.55f}; colors[Pants]={.24f,.26f,.34f};
    const glm::vec3 skins[]={{.96f,.8f,.66f},{.78f,.57f,.4f},{.46f,.31f,.21f}};
    const glm::vec3 shirts[]={{.82f,.28f,.22f},{.24f,.47f,.82f},{.92f,.76f,.22f},{.3f,.66f,.36f},{.6f,.36f,.72f},{.92f,.92f,.88f}};
    for (int i=0;i<3;++i) colors[Skin+i]=skins[i];
    for (int i=0;i<6;++i) colors[Shirt+i]=shirts[i];
    for (int i=0;i<FictionalCountryCount;++i) colors[Roof+i]=world.countries[i].color;
    const glm::vec3 axes[]={{1,0,0},{0,1,0},{0,0,1}};
    for (glm::vec3 color : colors) {
        color=glm::pow(color,glm::vec3(2.2f)); // authored as sRGB
        for (int axis=0;axis<3;++axis) for (float side : {-1.f,1.f}) {
            glm::vec3 n=axes[axis]*side, u=axes[(axis+1)%3], v=axes[(axis+2)%3];
            auto first=uint32_t(vertices.size());
            for (glm::vec2 corner : {glm::vec2(-1,-1),{1,-1},{1,1},{-1,1}}) {
                Vertex vertex; vertex.position=(n+u*corner.x+v*corner.y)*.5f; vertex.normal=n; vertex.color=glm::vec4(color,1);
                vertices.push_back(vertex);
            }
            indices.insert(indices.end(),{first,first+1,first+2,first,first+2,first+3});
        }
    }
}

void town_self_test() {
    const char* now=R"({"agents":[
        {"id":"x2","role":"smith","country_id":"X","inventory":{"food":1},"hungry":true},
        {"id":"x1","role":"farmer","country_id":"X","inventory":{"food":2,"tools":3},"hungry":false},
        {"id":"y1","role":"miner","country_id":"Y","inventory":{},"hungry":false}]})";
    const char* before=R"({"agents":[{"id":"x1","role":"farmer","country_id":"X","inventory":{"food":5,"tools":1},"hungry":false}]})";
    Town town=town_from_snapshot(now,before,"X",10);
    require(town.recorded && town.people.size()==2,"Town shows only the inspected country's agents");
    require(town.people[0].id=="x1" && town.people[0].line=="Traded 3 food for 2 tools","Inventory change becomes a trade line, listed first");
    require(town.people[1].line.find("food")!=std::string::npos,"Hungry agent asks for food");
    require(town_from_snapshot(now,"","X",1).people.size()==1,"Town respects its size limit");
    auto info=country_agents(R"({"agents":[
        {"id":"a","role":"farmer","country_id":"X","hungry":true,"health":80,"wellbeing":50,"cash_minor":150},
        {"id":"b","role":"smith","country_id":"X","hungry":false,"health":60,"wellbeing":70.5,"cash_minor":50},
        {"id":"c","role":"farmer","country_id":"X","hungry":false,"health":70,"wellbeing":60,"cash_minor":100},
        {"id":"d","role":"farmer","country_id":"Y","hungry":true,"health":0,"wellbeing":0,"cash_minor":999}]})","X");
    require(info.people==3 && info.hungry==1 && info.cashMinor==300 && info.health==70 && std::abs(info.wellbeing-60.1667f)<.01f,
        "Country totals cover only that country's agents");
    require(info.roles.size()==2 && info.roles[0]==std::pair<std::string,int>("farmer",2),"Roles are listed most common first");

    Town walk=demo_town(6);
    bool talked=false;
    for (int frame=0;frame<2400;++frame) {
        walk.update(.05f);
        for (size_t i=0;i<walk.people.size();++i) {
            const auto& person=walk.people[i];
            require(glm::length(person.position)<=SquareRadius+MeetGap+.01f,"People stay on the square");
            if (person.talk<=0) continue;
            const auto& other=walk.people[person.partner];
            require(other.partner==int(i) && other.talk>0,"Conversations are mutual");
            glm::vec2 facing{std::sin(person.heading),std::cos(person.heading)};
            require(glm::dot(facing,glm::normalize(other.position-person.position))>.99f,"Talkers face each other");
            talked=true;
        }
    }
    require(talked,"People meet and talk");
    for (const auto& person : walk.people) require(person.stride>5,"Everyone walks");
    // Longest legitimate stop: waiting for a partner called from at most CallRange away (~5 s), then chatting (<= 7 s).
    Town crowd=demo_town(24);
    std::vector<float> still(crowd.people.size());
    for (int frame=0;frame<12000;++frame) {
        std::vector<float> walked(crowd.people.size());
        for (size_t i=0;i<crowd.people.size();++i) walked[i]=crowd.people[i].stride;
        crowd.update(.05f);
        for (size_t i=0;i<crowd.people.size();++i) {
            const auto& person=crowd.people[i];
            still[i]=person.stride>walked[i] ? 0 : still[i]+.05f;
            if (still[i]>CallRange/WalkSpeed+7.5f) {
                fmt::print("Stuck: person {} partner {} talk {:.2f} at ({:.2f},{:.2f}) target ({:.2f},{:.2f})\n",i,person.partner,person.talk,
                    person.position.x,person.position.y,person.target.x,person.target.y);
                require(false,"Nobody stands still for good");
            }
        }
    }
    fmt::print("Town parsing, walking and conversations passed\n");
}
