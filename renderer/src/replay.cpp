#include "replay.h"
#include <algorithm>
#include <sstream>
#include <unordered_map>

Replay load_replay(std::istream& input, const ProceduralWorld& world) {
    Replay replay;
    std::unordered_map<std::string,int> countries, routes;
    for (int i=0;i<FictionalCountryCount;++i) countries[world.countries[i].id]=i;
    auto fail=[](int line,const std::string& message) { throw std::runtime_error(fmt::format("Replay line {}: {}",line,message)); };
    auto country=[&](int line,const std::string& id) {
        auto found=countries.find(id);
        if (found==countries.end()) fail(line,"unknown country "+id);
        return found->second;
    };
    std::string text, kind;
    int line=0, seen=0;
    bool ended=false;
    while (std::getline(input,text)) {
        ++line;
        if (!text.empty() && text.back()=='\r') text.pop_back();
        std::istringstream row(text);
        if (!(row>>kind)) continue;
        if (line==1) {
            int version=0;
            if (kind!="faultline-replay" || !(row>>version) || version!=1) fail(line,"not a faultline-replay 1 file");
        } else if (kind=="run") {
            row>>replay.runId>>replay.mode>>replay.seed;
        } else if (kind=="country") {
            std::string id, government; float latitude, longitude;
            if (!(row>>id>>latitude>>longitude>>government)) fail(line,"malformed country");
            int index=country(line,id);
            if (glm::length(globe_position(latitude,longitude)-world.countries[index].center)>.001f)
                fail(line,id+" is not at the renderer's capital; update procedural_world.cpp to the run's geography");
            replay.governments[index]=government; ++seen;
        } else if (kind=="route") {
            Replay::Route route; std::string from, to;
            if (!(row>>route.id>>from>>to>>route.travelTicks) || route.travelTicks<1) fail(line,"malformed route");
            route.from=country(line,from); route.to=country(line,to);
            routes[route.id]=int(replay.routes.size()); replay.routes.push_back(route);
        } else if (kind=="day") {
            Replay::Day day;
            if (!(row>>day.tick)) fail(line,"malformed day");
            day.routeOpen=replay.days.empty() ? std::vector<uint8_t>(replay.routes.size(),1) : replay.days.back().routeOpen;
            replay.days.push_back(std::move(day));
        } else if (kind=="end") {
            row>>replay.status; ended=true;
        } else {
            if (replay.days.empty()) fail(line,kind+" before the first day");
            auto& day=replay.days.back();
            std::string id;
            if (kind=="nation") {
                Replay::Nation nation;
                if (!(row>>id>>nation.residents>>nation.unemployed>>nation.approval>>nation.gini>>nation.wars)) fail(line,"malformed nation");
                day.nations[country(line,id)]=nation;
            } else if (kind=="ship") {
                Replay::Shipment shipment; std::string route;
                if (!(row>>id>>shipment.good>>shipment.quantity>>route>>shipment.remaining)) fail(line,"malformed ship");
                auto found=routes.find(route);
                if (found==routes.end()) fail(line,"unknown route "+route);
                shipment.route=found->second; day.shipments.push_back(shipment);
            } else if (kind=="border") {
                std::string route; int open=0;
                if (!(row>>route>>open)) fail(line,"malformed border");
                auto found=routes.find(route);
                if (found==routes.end()) fail(line,"unknown route "+route);
                day.routeOpen[found->second]=uint8_t(open!=0);
            } else if (kind=="event") {
                Replay::Event event;
                if (!(row>>id)) fail(line,"malformed event");
                event.country=country(line,id);
                std::getline(row>>std::ws,event.text); day.events.push_back(std::move(event));
            } else fail(line,"unknown record "+kind);
        }
    }
    if (seen!=FictionalCountryCount) fail(line,"expected all ten countries");
    if (replay.days.empty() || !ended) fail(line,"no days or missing end line (truncated file?)");
    return replay;
}
float replay_progress(int travelTicks, int remaining, float fraction) {
    return std::clamp((float(travelTicks-remaining)+fraction)/float(std::max(travelTicks,1)),0.f,1.f);
}
void replay_self_test() {
    auto check=[](bool good,const char* message) { if (!good) throw std::runtime_error(message); };
    auto world=generate_world(64,32);
    std::string countries;
    const char* lines[]={"aster 40 -105","beryl -18 -58","cinder 50 15","dune 25 42","ember -27 135",
        "fjord 64 -22","grove 3 23","haven 25 110","ionia 8 80","jade 35 140"};
    for (auto line : lines) countries+=fmt::format("country {} monarchy\n",line);
    std::istringstream good("faultline-replay 1\nrun r1 RULE 7\n"+countries+"route aster_to_jade aster jade 4\n"
        "day 0\nnation jade 9 1 0.5 0.25 2\nship c1 food 3 aster_to_jade 2\nborder aster_to_jade 0\nevent jade Jade closes the Aster-Jade route\n"
        "day 1\nday 2\nborder aster_to_jade 1\nend completed\n");
    auto replay=load_replay(good,world);
    check(replay.mode=="RULE" && replay.days.size()==3 && replay.status=="completed","Replay header and days");
    check(!replay.days[0].routeOpen[0] && !replay.days[1].routeOpen[0] && replay.days[2].routeOpen[0],"Replay border state carries across days");
    check(replay.days[0].nations[9].residents==9 && replay.days[0].nations[9].wars==2,"Replay nation row");
    check(replay.days[0].shipments.size()==1 && replay.routes[0].to==9,"Replay shipment route");
    check(replay.days[0].events[0].text=="Jade closes the Aster-Jade route","Replay caption keeps spaces");
    std::string movedCountries=countries;
    movedCountries.replace(movedCountries.find("40 -105"),7,"41 -105");
    std::istringstream moved("faultline-replay 1\n"+movedCountries+"day 0\nend completed\n");
    bool rejected=false;
    try { load_replay(moved,world); } catch (const std::runtime_error&) { rejected=true; }
    check(rejected,"Replay with different geography must be rejected");
    std::istringstream truncated("faultline-replay 1\n"+countries+"day 0\n");
    rejected=false;
    try { load_replay(truncated,world); } catch (const std::runtime_error&) { rejected=true; }
    check(rejected,"Truncated replay must be rejected");
    check(replay_progress(4,4,0)==0 && replay_progress(4,2,.5f)==.625f && replay_progress(4,0,1)==1,"Replay progress");
    fmt::print("Replay parsing, geography check and progress passed\n");
}
