#include "event_replay.h"
#include <simdjson.h>
#include <imgui.h>
#include <algorithm>
#include <fstream>
#include <set>
#include <sstream>
#include <map>

namespace {
std::string string(simdjson::dom::element e) { return std::string(e.get_string().value()); }
int integer(simdjson::dom::element e) {
    auto n=e.get_int64().value();
    if (n<0 || n>10000000) throw std::runtime_error("Event integer out of range");
    return int(n);
}
bool visible(glm::vec3 p, glm::vec3 eye) {
    // Ray/planet intersection: raised arcs can remain visible beyond the surface horizon.
    glm::vec3 ray=p-eye; float length=glm::length(ray); ray/=length;
    float b=glm::dot(eye,ray), d=b*b-glm::dot(eye,eye)+1;
    return d<0 || -b-std::sqrt(d)>=length-.0001f;
}
glm::vec3 arc(glm::vec3 from, glm::vec3 to, float t) {
    return globe_interpolate(from,to,t)*(1.018f+.09f*std::sin(t*3.14159265f));
}
}
EventReplay EventReplay::parse(std::string_view json, const ProceduralWorld& world, bool allowEmpty) {
    simdjson::dom::parser parser;
    auto document=parser.parse(json.data(),json.size()).value();
    EventReplay replay;
    std::set<std::string> ids; std::set<int> sequences;
    auto country=[&](const std::string& id) {
        for (int i=0;i<FictionalCountryCount;++i) if (id==world.countries[i].id) return i;
        return -1;
    };
    auto rows=document;
    std::map<std::string,int> routeIds;
    bool wrapped=document.is_object();
    if (wrapped) {
        if (string(document["format"])!="faultline.events.v1") throw std::runtime_error("Unknown event format");
        replay.run=string(document["run_id"]);
        replay.speed=2.f;
        std::set<int> countries;
        for (auto row : document["countries"].get_array().value()) {
            int c=country(string(row["id"]));
            double lat=row["lat"].get_double().value(), lon=row["lon"].get_double().value();
            if (c<0 || !countries.insert(c).second || !std::isfinite(lat) || !std::isfinite(lon) ||
                std::abs(lat)>90 || std::abs(lon)>180 || glm::length(globe_position(float(lat),float(lon))-world.countries[c].center)>.001f)
                throw std::runtime_error("Event geography does not match the renderer's capitals");
        }
        if (countries.size()!=FictionalCountryCount) throw std::runtime_error("Expected all ten event countries");
        for (auto row : document["routes"].get_array().value()) {
            Route route{country(string(row["from"])),country(string(row["to"])),integer(row["travel_ticks"])};
            if (route.from<0 || route.to<0 || route.from==route.to || route.travelTicks<1 ||
                !routeIds.emplace(string(row["id"]),int(replay.routes.size())).second)
                throw std::runtime_error("Invalid event route");
            replay.routes.push_back(route);
        }
        rows=document["events"].value();
    }
    for (auto row : rows.get_array().value()) {
        if (replay.events.size()>=250000) throw std::runtime_error("Event replay exceeds 250000 records");
        Event e; auto run=string(row["run_id"]);
        if (replay.run.empty()) replay.run=run;
        if (run!=replay.run) throw std::runtime_error("Cannot combine simulation runs");
        e.id=string(row["event_id"]); e.type=string(row["event_type"]);
        e.tick=integer(row["tick"]); e.sequence=integer(row["seq"]);
        if (!ids.insert(e.id).second || !sequences.insert(e.sequence).second) throw std::runtime_error("Duplicate event ID or sequence");
        e.detail=simdjson::minify(row);
        auto payload=row["payload"].value();
        for (auto entity : row["entity_ids"].get_array().value()) {
            auto id=string(entity); int c=country(id);
            if (c>=0 && e.from<0) e.from=c;
            if (id.starts_with("policy_")) e.policy=id;
            if (id.starts_with("cargo_")) e.cargo=id;
            auto colon=id.find(':');
            if (colon!=std::string::npos) { e.from=country(id.substr(0,colon)); e.to=country(id.substr(colon+1)); }
        }
        if (e.type=="conflict_started") { e.from=country(string(payload["first"])); e.to=country(string(payload["second"])); }
        if (e.shipment()) {
            auto plan=payload["plan"].value();
            e.travelTicks=integer(plan["travel_ticks"]);
            if (e.type=="shipment_dispatched") { e.good=string(payload["good"]); e.quantity=integer(payload["quantity"]); }
            for (auto edge : plan["edge_ids"].get_array().value()) {
                auto found=routeIds.find(string(edge));
                if (found==routeIds.end()) throw std::runtime_error("Shipment references an unknown route");
                int r=found->second;
                if (!e.path.empty() && replay.routes[e.path.back()].to!=replay.routes[r].from)
                    throw std::runtime_error("Disconnected shipment route");
                e.path.push_back(r);
            }
            if (e.path.empty() || e.cargo.empty() || e.travelTicks<1) throw std::runtime_error("Invalid shipment plan");
            e.from=replay.routes[e.path.front()].from; e.to=replay.routes[e.path.back()].to;
        }
        if (e.type=="agent_migrated") { e.from=country(string(payload["from"])); e.to=country(string(payload["to"])); }
        if (e.type=="diplomatic_response") e.open=!payload["at_war"].get_bool().value();
        if (e.type=="resource_crisis") e.expires=integer(payload["until_tick"]);
        if (e.type=="diplomatic_offer") {
            int sender=country(string(payload["proposed_by"]));
            if (sender==e.to) std::swap(e.from,e.to);
            if (sender!=e.from) throw std::runtime_error("Trade offer sender does not match its country pair");
            e.expires=integer(payload["proposal_expires_tick"])+1;
        }
        if (e.type=="border_changed") {
            auto edge=string(payload["edge_id"]); auto split=edge.find("_to_");
            if (split==std::string::npos) throw std::runtime_error("Unknown border edge convention");
            e.from=country(edge.substr(0,split)); e.to=country(edge.substr(split+4));
            e.open=payload["open"].get_bool().value();
            auto expiry=payload["expires_tick"];
            if (!expiry.error() && !expiry.value().is_null()) e.expires=integer(expiry.value());
        }
        bool pair=e.type=="conflict_started" || e.type=="diplomatic_offer" || e.type=="border_changed" || e.type=="agent_migrated";
        bool policy=e.type=="policy_proposed" || e.type=="policy_enacted" || e.type=="policy_blocked";
        if ((pair && (e.from<0 || e.to<0)) || (policy && (e.from<0 || e.policy.empty())))
            throw std::runtime_error("Unmapped country or policy in event "+e.id);
        replay.lastTick=std::max(replay.lastTick,e.tick);
        replay.events.push_back(std::move(e));
    }
    if (replay.events.empty() && !allowEmpty) throw std::runtime_error("Empty event replay");
    std::sort(replay.events.begin(),replay.events.end(),[](auto& a,auto& b) { return a.tick==b.tick ? a.sequence<b.sequence : a.tick<b.tick; });
    std::map<std::string,int> cargo;
    std::map<std::pair<int,std::string>,int> policies;
    std::map<std::pair<int,int>,int> borders;
    std::map<std::pair<int,int>,int> conflicts, offers;
    for (int i=0;i<int(replay.events.size());++i) {
        auto& e=replay.events[i];
        if (wrapped && e.sequence!=i+1) throw std::runtime_error("Incomplete or unordered event sequence");
        if (e.type=="shipment_dispatched" && !cargo.emplace(e.cargo,i).second) throw std::runtime_error("Duplicate cargo dispatch");
        if (e.type=="shipment_rerouted" || e.type=="shipment_delivered" || e.type=="shipment_failed") {
            auto found=cargo.find(e.cargo);
            if (found==cargo.end()) throw std::runtime_error("Shipment update without a recorded dispatch");
            auto& dispatch=replay.events[found->second];
            if (dispatch.expires>=0 || e.tick<dispatch.tick) throw std::runtime_error("Invalid cargo update time");
            dispatch.expires=e.tick;
            dispatch.delivered=e.type=="shipment_delivered";
            if (e.type=="shipment_rerouted") {
                e.good=dispatch.good; e.quantity=dispatch.quantity; found->second=i;
            } else { e.from=dispatch.from; e.to=dispatch.to; }
        }
        auto pair=std::minmax(e.from,e.to);
        if (e.type=="conflict_started") conflicts[pair]=i;
        if (e.type=="diplomatic_offer") offers[pair]=i;
        if (e.type=="diplomatic_response") {
            if (e.from<0 || e.to<0) throw std::runtime_error("Unmapped diplomatic response");
            if (auto found=offers.find(pair); found!=offers.end()) replay.events[found->second].expires=e.tick;
            if (auto found=conflicts.find(pair); e.open && found!=conflicts.end()) replay.events[found->second].expires=e.tick;
        }
        auto key=std::make_pair(e.from,e.policy);
        if (e.type=="policy_proposed") policies[key]=i;
        if (e.type=="policy_enacted" || e.type=="policy_blocked") {
            auto found=policies.find(key);
            if (found!=policies.end()) replay.events[found->second].expires=e.tick;
        }
        if (e.type=="border_changed") {
            auto pair=std::make_pair(e.from,e.to);
            auto found=borders.find(pair);
            if (found!=borders.end()) {
                auto& previous=replay.events[found->second];
                previous.expires=previous.expires<0 ? e.tick : std::min(previous.expires,e.tick);
            }
            borders[pair]=i;
        }
        if (e.from>=0 && (e.type.starts_with("policy_") || e.shipment() || e.type=="shipment_delivered" || e.type=="shipment_failed" || e.type=="diplomatic_response" ||
            e.type=="border_changed" || e.type=="conflict_started" || e.type=="diplomatic_offer" || e.type=="diplomatic_tension" ||
            e.type=="resource_crisis" || e.type=="agent_migrated" || e.type=="business_started" || e.type=="business_closed" || e.type=="election_result"))
            replay.mappedEvents.push_back(i);
    }
    return replay;
}
EventReplay EventReplay::load(const std::filesystem::path& path,const ProceduralWorld& world) {
    if (std::filesystem::file_size(path)>64*1024*1024) throw std::runtime_error("Event JSON exceeds 64 MiB");
    std::ifstream input(path,std::ios::binary); if (!input) throw std::runtime_error("Cannot open event JSON");
    std::ostringstream text; text<<input.rdbuf();
    if (input.bad()) throw std::runtime_error("Cannot read event JSON");
    return parse(text.str(),world);
}
void EventReplay::advance(float seconds) {
    if (playing) { time=std::min(float(lastTick),time+seconds*speed); if (time>=lastTick) playing=false; }
}
bool EventReplay::active(size_t index) const {
    const auto& e=events[index];
    if (e.tick>time || (e.expires>=0 && time>=e.expires)) return false;
    if (e.type=="policy_proposed" || e.type=="resource_crisis" || e.shipment()) return true;
    if (e.type=="border_changed") return !e.open;
    return e.type=="conflict_started" || e.type=="diplomatic_offer" ||
        (time<e.tick+1.5f && (e.type=="policy_enacted" || e.type=="policy_blocked" || e.type=="agent_migrated" ||
        e.type=="business_started" || e.type=="business_closed" || e.type=="election_result" || e.type=="diplomatic_tension"));
}
std::pair<int,float> EventReplay::shipment_progress(const Event& e) const {
    float weight=0; for (int r : e.path) weight+=routes[r].travelTicks;
    // Only dispatch/delivery boundaries are recorded; intermediate positions are illustrative.
    float elapsed=std::clamp((time-e.tick)/float(e.delivered ? std::max(1,e.expires-e.tick) : e.travelTicks),0.f,1.f)*weight;
    for (size_t leg=0;leg<e.path.size();++leg) {
        const auto& route=routes[e.path[leg]];
        if (elapsed<=route.travelTicks || leg+1==e.path.size())
            return {e.path[leg],std::clamp(elapsed/route.travelTicks,0.f,1.f)};
        elapsed-=route.travelTicks;
    }
    throw std::runtime_error("Shipment has no route");
}
void EventReplay::draw(const ProceduralWorld& world,const std::vector<DemoRoute>& vehicleRoutes,const glm::mat4& matrix,glm::vec3 eye,bool panels,float& yaw,float& pitch,bool showShipments) {
    auto size=ImGui::GetIO().DisplaySize;
    if (panels) {
        ImGui::SetNextWindowPos(ImVec2(std::max(18.f,size.x-338),18),ImGuiCond_Always);
        ImGui::SetNextWindowSize(ImVec2(320,std::max(260.f,size.y-36)),ImGuiCond_Always);
        ImGui::Begin("Recorded events",nullptr,ImGuiWindowFlags_NoMove|ImGuiWindowFlags_NoResize);
        ImGui::TextWrapped("%s",run.c_str());
        ImGui::Text("%zu records | ticks 0-%d",events.size(),lastTick);
        ImGui::Checkbox("Play",&playing); ImGui::SameLine();
        if (ImGui::Button("Restart")) { time=0; selected=-1; playing=true; }
        ImGui::SliderFloat("Tick",&time,0,float(lastTick),"%.2f");
        ImGui::SliderFloat("Ticks/sec",&speed,.1f,20.f);
        int inTransit=0;
        for (int i : mappedEvents) if (events[i].shipment() && active(i)) ++inTransit;
        ImGui::Text("%d recorded shipments in transit",inTransit);
        ImGui::TextWrapped(routes.empty() ? "Arcs illustrate recorded relationships. No shipment routes in this file."
            : "Ships and planes carry recorded cargo; vehicle types and movement between events are illustrative.");
        ImGui::TextWrapped("Recorded playback | No live connection or daily world-state frames.");
        ImGui::TextWrapped("Cargo: food green, energy orange, tools blue, medicine pink. Rings mark country events.");
        ImGui::Checkbox("Show all event records",&showAll);
        ImGui::Separator();
        ImGui::BeginChild("event list",ImVec2(0,std::max(100.f,size.y*.3f)),true);
        int count=showAll ? int(std::upper_bound(events.begin(),events.end(),time,[](float t,const Event& e) { return t<e.tick; })-events.begin())
            : int(std::upper_bound(mappedEvents.begin(),mappedEvents.end(),time,[&](float t,int i) { return t<events[i].tick; })-mappedEvents.begin());
        ImGuiListClipper clipper;
        clipper.Begin(count);
        while (clipper.Step()) for (int row=clipper.DisplayStart;row<clipper.DisplayEnd;++row) {
            int i=showAll ? count-1-row : mappedEvents[count-1-row];
            const auto& e=events[i];
            auto label=fmt::format("{}: {}{}##{}",e.tick,e.type,
                e.from>=0 ? fmt::format(" | {}",world.countries[e.from].name) : "",e.sequence);
            if (ImGui::Selectable(label.c_str(),selected==i)) {
                selected=i;
                if (e.from>=0) { auto p=world.countries[e.from].center; yaw=std::atan2(p.x,p.z); pitch=std::asin(p.y); }
            }
        }
        ImGui::EndChild();
        if (selected>=0 && events[selected].tick<=time) {
            const auto& event=events[selected];
            if (event.from>=0) ImGui::TextWrapped("%s%s%s",world.countries[event.from].name,
                event.to>=0 ? " -> " : "",event.to>=0 ? world.countries[event.to].name : "");
            ImGui::TextWrapped("%s",events[selected].id.c_str());
            ImGui::TextWrapped("%s",events[selected].detail.c_str());
        } else ImGui::TextWrapped("Select an event to inspect its recorded payload and focus its country.");
        ImGui::End();
    }
    auto* draw=ImGui::GetBackgroundDrawList();
    auto project=[&](glm::vec3 p,ImVec2& screen) {
        if (!visible(p,eye)) return false;
        auto clip=matrix*glm::vec4(p,1); if (clip.w<=0) return false;
        glm::vec2 ndc=glm::vec2(clip)/clip.w;
        if (std::abs(ndc.x)>1 || std::abs(ndc.y)>1) return false;
        screen=ImVec2((ndc.x*.5f+.5f)*size.x,(ndc.y*.5f+.5f)*size.y); return true;
    };
    std::set<int> drawnRoutes;
    for (int i : mappedEvents) {
        if (!active(i)) continue;
        const auto& e=events[i]; if (e.from<0) continue;
        bool offer=e.type=="diplomatic_offer", conflict=e.type=="conflict_started", border=e.type=="border_changed", shipment=e.shipment();
        auto color=offer ? IM_COL32(255,192,60,230) : conflict || border || e.type=="resource_crisis" || e.type=="policy_blocked" ? IM_COL32(255,85,65,230) : IM_COL32(140,220,255,230);
        float pulse=1+ .15f*std::sin(time*9);
        ImVec2 at;
        if (shipment) {
            if (!showShipments) continue;
            color=e.good=="food" ? IM_COL32(100,245,135,240) : e.good=="energy" ? IM_COL32(255,185,60,240)
                : e.good=="medicine" ? IM_COL32(255,135,200,240) : IM_COL32(100,190,255,240);
            for (int r : e.path) if (drawnRoutes.insert(r).second) {
                const auto& route=vehicleRoutes[r];
                for (int segment=0;segment<48;++segment) {
                    ImVec2 a,b;
                    if (project(route_position(route,segment/48.f),a) &&
                        project(route_position(route,(segment+1)/48.f),b))
                        draw->AddLine(a,b,IM_COL32(150,205,230,115),1);
                }
            }
            auto [route,progress]=shipment_progress(e);
            if (project(route_position(vehicleRoutes[route],progress),at)) {
                if (selected==i) draw->AddCircle(at,10,color,24,1.5f);
                if (!ImGui::GetIO().WantCaptureMouse && ImGui::IsMouseHoveringRect(ImVec2(at.x-7,at.y-7),ImVec2(at.x+7,at.y+7))) {
                    ImGui::SetTooltip("%s: %d %s\n%s -> %s",e.cargo.c_str(),e.quantity,e.good.c_str(),world.countries[e.from].name,world.countries[e.to].name);
                    if (ImGui::IsMouseClicked(0)) selected=i;
                }
            }
            continue;
        }
        for (int c : {e.from,conflict ? e.to : -1}) if (c>=0 && project(world.countries[c].center*1.012f,at)) {
            draw->AddCircle(at,12*pulse,color,32,2);
            if (e.type=="policy_proposed" || e.type=="policy_enacted")
                draw->AddText(ImVec2(at.x+15,at.y+13),color,e.type=="policy_proposed" ? "Pending policy" : "Policy enacted");
            else if (e.type=="resource_crisis") draw->AddText(ImVec2(at.x+15,at.y+13),color,"Resource crisis");
            if (ImGui::IsMouseHoveringRect(ImVec2(at.x-12,at.y-12),ImVec2(at.x+12,at.y+12)) && !ImGui::GetIO().WantCaptureMouse) {
                ImGui::SetTooltip("%s | tick %d",e.type.c_str(),e.tick);
                if (ImGui::IsMouseClicked(0)) selected=int(i);
            }
        }
        if (e.to<0) continue;
        auto point=[&](float t) { return arc(world.countries[e.from].center,world.countries[e.to].center,t); };
        for (int segment=0;segment<96;++segment) {
            if (offer && segment%4>=2) continue;
            ImVec2 a,b; if (project(point(segment/96.f),a) && project(point((segment+1)/96.f),b)) draw->AddLine(a,b,color,conflict ? 2*pulse : 1.5f);
        }
        if (offer && project(point(std::fmod(std::max(0.f,time-e.tick)*.4f,1.f)),at)) draw->AddCircleFilled(at,4,color);
        if (border && project(point(.5f),at)) {
            draw->AddLine(ImVec2(at.x-6,at.y-6),ImVec2(at.x+6,at.y+6),color,3);
            draw->AddLine(ImVec2(at.x-6,at.y+6),ImVec2(at.x+6,at.y-6),color,3);
        }
    }
}
void event_replay_self_test() {
    ProceduralWorld world; const char* ids[]={"aster","beryl","cinder","dune","ember","fjord","grove","haven","ionia","jade"};
    for(int i=0;i<10;++i) world.countries[i].id=ids[i];
    auto check=[](bool ok) { if(!ok) throw std::runtime_error("Event replay self-test failed"); };
    auto json=R"([{"run_id":"test","event_id":"a","seq":1,"tick":1,"event_type":"border_changed","entity_ids":[],"payload":{"edge_id":"dune_to_ionia","open":false,"expires_tick":7}}])";
    auto replay=EventReplay::parse(json,world);
    check(!replay.active(0)); replay.time=2; check(replay.active(0)); replay.time=7; check(!replay.active(0)); replay.time=0; check(!replay.active(0));
    check(replay.events[0].from==3 && replay.events[0].to==8);
    replay.time=0; replay.advance(100); check(replay.time==1 && !replay.playing);
    bool rejected=false; try { EventReplay::parse("[]",world); } catch (...) { rejected=true; } check(rejected);
    std::string record=json; record=record.substr(1,record.size()-2);
    for (auto invalid : {"["+record+","+record+"]", std::string("not json")}) {
        rejected=false; try { EventReplay::parse(invalid,world); } catch (...) { rejected=true; } check(rejected);
    }
    auto unknown=std::string(json); unknown.replace(unknown.find("dune_to_ionia"),13,"dune_to_unknown");
    rejected=false; try { EventReplay::parse(unknown,world); } catch (...) { rejected=true; } check(rejected);
    auto policies=EventReplay::parse(R"([
        {"run_id":"test","event_id":"p","seq":1,"tick":1,"event_type":"policy_proposed","entity_ids":["aster","policy_1"],"payload":{}},
        {"run_id":"test","event_id":"q","seq":2,"tick":4,"event_type":"policy_enacted","entity_ids":["aster","policy_1"],"payload":{}}
    ])",world);
    policies.time=2; check(policies.active(0) && !policies.active(1));
    policies.time=4; check(!policies.active(0) && policies.active(1));
    policies.time=2; check(policies.active(0) && !policies.active(1));
    std::string countries;
    for (int i=0;i<10;++i) {
        world.countries[i].center=globe_position(0,float(i*20));
        countries+=fmt::format("{}{{\"id\":\"{}\",\"lat\":0,\"lon\":{}}}",i ? "," : "",ids[i],i*20);
    }
    auto shipments=std::string(R"({"format":"faultline.events.v1","run_id":"test","countries":[)")+countries+R"(],
        "routes":[{"id":"ab","from":"aster","to":"beryl","travel_ticks":2},{"id":"bc","from":"beryl","to":"cinder","travel_ticks":4}],
        "events":[
        {"run_id":"test","event_id":"s","seq":1,"tick":2,"event_type":"shipment_dispatched","entity_ids":["cargo_1"],"payload":{"plan":{"edge_ids":["ab","bc"],"travel_ticks":6},"good":"food","quantity":5}},
        {"run_id":"test","event_id":"p","seq":2,"tick":3,"event_type":"policy_proposed","entity_ids":["aster","policy_1"],"payload":{}},
        {"run_id":"test","event_id":"b","seq":3,"tick":4,"event_type":"policy_blocked","entity_ids":["aster","policy_1"],"payload":{}},
        {"run_id":"test","event_id":"c","seq":4,"tick":5,"event_type":"resource_crisis","entity_ids":["beryl"],"payload":{"until_tick":9}},
        {"run_id":"test","event_id":"s2","seq":5,"tick":6,"event_type":"shipment_dispatched","entity_ids":["cargo_2"],"payload":{"plan":{"edge_ids":["ab","bc"],"travel_ticks":6},"good":"food","quantity":3}},
        {"run_id":"test","event_id":"d","seq":6,"tick":8,"event_type":"shipment_delivered","entity_ids":["cargo_1"],"payload":{}},
        {"run_id":"test","event_id":"end","seq":7,"tick":10,"event_type":"run_completed","entity_ids":[],"payload":{}}]})";
    auto cargo=EventReplay::parse(shipments,world);
    check(cargo.mappedEvents.size()==6 && cargo.events[0].expires==8 && cargo.events[5].to==2);
    cargo.time=1; check(!cargo.active(0));
    cargo.time=4; check(cargo.active(0) && !cargo.active(1) && cargo.active(2));
    check(cargo.shipment_progress(cargo.events[0])==std::pair(0,1.f));
    cargo.time=6;
    check(cargo.shipment_progress(cargo.events[0])==std::pair(1,.5f));
    check(cargo.active(3) && cargo.active(4));
    cargo.time=10; check(!cargo.active(0) && !cargo.active(3) && cargo.active(4));
    cargo.time=3; check(cargo.active(0) && cargo.active(1) && !cargo.active(4));
    auto badRoute=shipments;
    badRoute.replace(badRoute.find("\"ab\",\"bc\""),9,"\"ab\",\"missing\"");
    auto badLocation=shipments;
    badLocation.replace(badLocation.find("\"lat\":0"),7,"\"lat\":10");
    auto orphan=shipments;
    orphan.replace(orphan.rfind("cargo_1"),7,"cargo_unknown");
    auto gap=shipments;
    gap.replace(gap.find("\"seq\":7"),7,"\"seq\":8");
    auto selfRoute=shipments;
    selfRoute.replace(selfRoute.find("\"to\":\"beryl\""),12,"\"to\":\"aster\"");
    for (const auto& invalid : {badRoute,badLocation,orphan,gap,selfRoute}) {
        rejected=false; try { EventReplay::parse(invalid,world); } catch (...) { rejected=true; } check(rejected);
    }
    auto offer=EventReplay::parse(R"([{"run_id":"test","event_id":"o","seq":1,"tick":238,"event_type":"diplomatic_offer","entity_ids":["jade","dune:jade"],"payload":{"proposed_by":"jade","proposal_expires_tick":298}}])",world);
    offer.time=298.9f; check(offer.active(0)); offer.time=299; check(!offer.active(0));
    auto reroute=shipments;
    reroute.replace(reroute.find("shipment_dispatched"),19,"shipment_rerouted");
    rejected=false; try { EventReplay::parse(reroute,world); } catch (...) { rejected=true; } check(rejected);
    auto lifecycle=shipments;
    auto ending=lifecycle.rfind("]}");
    lifecycle.insert(ending,R"(,
        {"run_id":"test","event_id":"r","seq":8,"tick":11,"event_type":"shipment_rerouted","entity_ids":["cargo_2"],"payload":{"plan":{"edge_ids":["bc"],"travel_ticks":4}}},
        {"run_id":"test","event_id":"f","seq":9,"tick":13,"event_type":"shipment_failed","entity_ids":["cargo_2"],"payload":{}})");
    auto changed=EventReplay::parse(lifecycle,world);
    changed.time=12; check(!changed.active(4) && changed.active(7) && changed.events[7].good=="food" && changed.events[7].quantity==3);
    check(changed.shipment_progress(changed.events[7])==std::pair(1,.25f));
    changed.time=13; check(!changed.active(7));
    changed.time=7; check(changed.active(4) && !changed.active(7));
    auto peace=EventReplay::parse(R"([
        {"run_id":"test","event_id":"w","seq":1,"tick":1,"event_type":"conflict_started","entity_ids":["aster:jade"],"payload":{"first":"aster","second":"jade"}},
        {"run_id":"test","event_id":"o","seq":2,"tick":2,"event_type":"diplomatic_offer","entity_ids":["jade","aster:jade"],"payload":{"proposed_by":"jade","proposal_expires_tick":62}},
        {"run_id":"test","event_id":"r","seq":3,"tick":4,"event_type":"diplomatic_response","entity_ids":["aster:jade"],"payload":{"accept":true,"at_war":false}}
    ])",world);
    peace.time=3; check(peace.active(0) && peace.active(1));
    peace.time=4; check(!peace.active(0) && !peace.active(1));
    peace.time=2; check(peace.active(0) && peace.active(1));
    std::string year="[";
    for (int i=1;i<=100001;++i) year+=fmt::format(
        "{}{{\"run_id\":\"year\",\"event_id\":\"e{}\",\"seq\":{},\"tick\":{},\"event_type\":\"recorded\",\"entity_ids\":[],\"payload\":{{}}}}",
        i==1 ? "" : ",",i,i,i*365/100001);
    year+="]";
    auto fullYear=EventReplay::parse(year,world);
    check(fullYear.events.size()==100001 && fullYear.lastTick==365);
    check(!visible({0,0,-1.02f},{0,0,3}) && visible({0,0,1.02f},{0,0,3}));
    fmt::print("Event replay: parsing, shipment routes/delivery, policy/crisis expiry, backward seek, geography, end clamp and occlusion passed\n");
}
