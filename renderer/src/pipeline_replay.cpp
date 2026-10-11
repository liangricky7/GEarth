#include "pipeline_replay.h"
#include <simdjson.h>
#include <imgui.h>
#include <fstream>
#include <sstream>
#include <set>
#include <algorithm>

namespace {
using simdjson::dom::element;
std::string str(element e) { return std::string(e.get_string().value()); }
int integer(element e) {
    auto n=e.get_int64().value();
    if (n<0 || n>10000000) throw std::runtime_error("Invalid renderer integer");
    return int(n);
}
void require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
std::string read(const std::filesystem::path& path) {
    require(std::filesystem::file_size(path)<=256ull*1024*1024,"Renderer file exceeds 256 MiB");
    std::ifstream input(path,std::ios::binary);
    require(bool(input),"Cannot open renderer stream file");
    std::ostringstream data; data<<input.rdbuf(); require(!input.bad(),"Cannot read renderer stream file");
    return data.str();
}
void safe_name(const std::string& value) {
    require(!value.empty() && value.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.")==std::string::npos
        && value!="." && value!="..","Unsafe renderer stream path");
}
struct ReplaySource {
    std::filesystem::path path;
    std::ifstream input;
    std::map<std::string,std::pair<uint64_t,uint64_t>> files;
    std::string index;
    bool packed{};
    int64_t revision{}, sequence{}, frameCount{};
    explicit ReplaySource(const std::filesystem::path& source) : path(source) {
        if (std::filesystem::is_directory(path)) { index=read(path/"index.json"); return; }
        packed=true;
        auto size=std::filesystem::file_size(path);
        require(size<=2ull*1024*1024*1024,"Recording exceeds 2 GiB");
        input.open(path,std::ios::binary);
        char prefix[12]{}; input.read(prefix,sizeof(prefix));
        require(bool(input) && std::string_view(prefix,8)=="GEARTH1\n","Unknown recording format");
        uint32_t length=0;
        for (int i=0;i<4;++i) length|=uint32_t(uint8_t(prefix[8+i]))<<(8*i);
        require(length>0 && length<=8*1024*1024 && size>=12ull+length,"Invalid recording header size");
        std::string header(length,'\0'); input.read(header.data(),length);
        require(bool(input),"Truncated recording header");
        simdjson::dom::parser parser;
        auto metadata=parser.parse(header).value();
        require(str(metadata["export_schema_version"])=="gearth.renderer-export.v1","Unknown recording export contract");
        revision=metadata["through_revision"].get_int64().value();
        sequence=metadata["through_event_sequence"].get_int64().value();
        frameCount=metadata["frame_count"].get_int64().value();
        require(revision>0 && sequence>=0 && frameCount>0 && frameCount<=10000,"Invalid recording coverage");
        std::string frames, events;
        uint64_t offset=12ull+length;
        for (auto entry : metadata["files"].get_array().value()) {
            auto name=str(entry["path"]); safe_name(name);
            auto bytes=entry["bytes"].get_uint64().value();
            require(bytes>0 && bytes<=256ull*1024*1024 && offset<=size && bytes<=size-offset,"Invalid recording file size");
            require(files.size()<10000 && files.emplace(name,std::pair(offset,bytes)).second,"Duplicate or excessive recording files");
            require(name.ends_with(".json") && (name.starts_with("frame-") || name.starts_with("events-")),"Unknown recording file kind");
            auto& list=name.starts_with("frame-") ? frames : events;
            if (!list.empty()) list+=",";
            list+=simdjson::minify(entry["path"].value()); offset+=bytes;
        }
        require(offset==size,"Recording size does not match manifest");
        index="{\"format\":\"gearth.renderer-stream.v1\",\"metadata\":"+header+
            ",\"current_only\":false,\"follow_latest\":false,\"connection\":\"Recording file\",\"frames\":["+frames+"],\"events\":["+events+"]}";
    }
    std::string child(element name) {
        auto value=str(name); safe_name(value);
        if (!packed) return read(path/value);
        auto [offset,size]=files.at(value);
        std::string bytes(size,'\0'); input.seekg(offset); input.read(bytes.data(),size);
        require(bool(input),"Truncated recording data"); return bytes;
    }
};
}

PipelineReplay PipelineReplay::load(const std::filesystem::path& directory, const ProceduralWorld& world) {
    PipelineReplay result;
    ReplaySource source(directory);
    simdjson::dom::parser parser, frameParser, pageParser;
    auto index=parser.parse(source.index).value();
    require(str(index["format"])=="gearth.renderer-stream.v1","Unknown renderer stream format");
    auto metadata=index["metadata"].value();
    require(str(metadata["schema_version"])=="gearth.renderer.v1","Unknown renderer contract");
    result.run=str(metadata["run_id"]); result.status=str(metadata["status"]);
    result.connection=str(index["connection"]);
    result.currentOnly=index["current_only"].get_bool().value();
    result.followLatest=index["follow_latest"].get_bool().value();
    result.liveInput=result.followLatest;
    result.loop=!result.liveInput && !result.currentOnly;
    result.historyGap=!metadata["captured_from_opening"].get_bool().value() || result.currentOnly;
    std::map<std::string,int> countries, routes;
    std::map<std::string,std::string> locations;
    std::map<std::string,std::tuple<std::string,std::string,int>> topology;
    std::vector<int> durations;
    for (int i=0;i<FictionalCountryCount;++i) countries.emplace(world.countries[i].id,i);
    std::string eventJson, routeJson;
    int lastSequence=0;
    int64_t revision=0;
    for (auto file : index["frames"].get_array().value()) {
        auto doc=frameParser.parse(source.child(file)).value();
        require(str(doc["schema_version"])=="gearth.renderer.v1" && str(doc["run_id"])==result.run,"Mixed renderer frame contract/run");
        Frame frame; frame.tick=integer(doc["tick"]); frame.sequence=integer(doc["through_event_sequence"]);
        frame.revision=doc["revision"].get_int64().value();
        require(frame.revision>revision && (result.frames.empty() || frame.tick>=result.frames.back().tick)
            && frame.sequence>=lastSequence,"Renderer frames are out of order");
        revision=frame.revision; lastSequence=frame.sequence;
        auto snapshot=doc["snapshot"].value();
        require(str(snapshot["run"]["id"])==result.run,"Snapshot belongs to another run");
        auto units=doc["units"];
        if (!units.error()) require(integer(units["days_per_tick"])==1 && integer(units["days_per_week"])==7
            && integer(units["minor_units_per_wld"])==100,"Unsupported renderer units");
        std::set<int> geography;
        for (auto country : snapshot["countries"].get_array().value()) {
            int c=countries.at(str(country["id"]));
            double lat=country["lat"].get_double().value(), lon=country["lon"].get_double().value();
            require(geography.insert(c).second && std::isfinite(lat) && std::isfinite(lon) && std::abs(lat)<=90 && std::abs(lon)<=180
                && glm::length(globe_position(float(lat),float(lon))-world.countries[c].center)<.001f,"Frame geography does not match GEarth capitals");
        }
        require(geography.size()==FictionalCountryCount,"Expected all ten frame countries");
        result.mode=str(snapshot["run"]["mode"]);
        frame.partial=str(doc["frame_kind"])=="terminal" && !doc["complete_week"].get_bool().value();
        frame.snapshot=simdjson::minify(snapshot);
        if (result.frames.empty()) {
            for (auto country : snapshot["countries"].get_array().value()) {
                int c=countries.at(str(country["id"]));
                auto name=country["name"];
                result.names[c]=name.error() ? world.countries[c].name : str(name.value());
            }
            for (auto location : snapshot["locations"].get_array().value())
                require(locations.emplace(str(location["id"]),str(location["country_id"])).second,"Duplicate location ID");
            for (auto route : snapshot["routes"].get_array().value()) {
                auto from=locations.at(str(route["from"])), to=locations.at(str(route["to"]));
                require(routes.emplace(str(route["id"]),int(routes.size())).second,"Duplicate route ID");
                int duration=integer(route["travel_ticks"]);
                require(duration>0,"Route duration must be positive");
                durations.push_back(duration);
                topology.emplace(str(route["id"]),std::tuple(str(route["from"]),str(route["to"]),duration));
                // Country IDs are checked against the fixed world before being used in this JSON envelope.
                require(countries.contains(from) && countries.contains(to),"Unknown route country");
                if (!routeJson.empty()) routeJson+=",";
                routeJson+=fmt::format("{{\"id\":{},\"from\":\"{}\",\"to\":\"{}\",\"travel_ticks\":{}}}",
                    simdjson::minify(route["id"].value()),from,to,integer(route["travel_ticks"]));
            }
            eventJson="{\"format\":\"faultline.events.v1\",\"run_id\":"+simdjson::minify(doc["run_id"].value())+
                ",\"countries\":"+simdjson::minify(snapshot["countries"].value())+",\"routes\":["+routeJson+"],\"events\":[";
        }
        std::set<std::string> seenRoutes;
        frame.routeOpen.resize(routes.size());
        for (auto route : snapshot["routes"].get_array().value()) {
            auto id=str(route["id"]);
            require(seenRoutes.insert(id).second && topology.contains(id) && topology.at(id)==
                std::tuple(str(route["from"]),str(route["to"]),integer(route["travel_ticks"])),"Route topology changed within the run");
            frame.routeOpen[routes.at(id)]=route["open"].get_bool().value();
        }
        require(seenRoutes.size()==routes.size(),"Frame is missing routes");
        std::set<int> nations;
        for (auto nation : snapshot["nations"].get_array().value()) {
            int c=countries.at(str(nation["id"]));
            require(nations.insert(c).second,"Duplicate nation ID");
            auto& n=frame.nations[c];
            n.residents=integer(nation["population"]); n.unemployed=integer(nation["unemployed"]);
            n.approval=float(nation["approval"].get_double().value()); n.gini=float(nation["gini_cash"].get_double().value());
            require(n.unemployed<=n.residents && n.approval>=0 && n.approval<=1 && n.gini>=0 && n.gini<=1,"Invalid nation statistics");
        }
        require(nations.size()==FictionalCountryCount,"Frame must contain all ten nations");
        for (auto relation : snapshot["relationships"].get_array().value()) if (relation["at_war"].get_bool().value()) {
            ++frame.nations[countries.at(str(relation["first"]))].wars;
            ++frame.nations[countries.at(str(relation["second"]))].wars;
        }
        for (auto shipment : snapshot["shipments"].get_array().value()) {
            if (str(shipment["status"])!="in_transit") continue;
            Cargo cargo; cargo.id=str(shipment["id"]); cargo.good=str(shipment["good"]);
            cargo.quantity=integer(shipment["quantity"]); cargo.started=frame.tick;
            cargo.edge=integer(shipment["edge_index"]); cargo.remaining=integer(shipment["edge_remaining_ticks"]);
            auto plan=shipment["plan"].value();
            if (!plan.is_null()) for (auto edge : plan["edge_ids"].get_array().value()) cargo.path.push_back(routes.at(str(edge)));
            require(cargo.edge<=int(cargo.path.size()),"Invalid shipment edge index");
            if (cargo.edge<int(cargo.path.size())) require(cargo.remaining<=durations[cargo.path[cargo.edge]],"Cargo remaining time exceeds its route duration");
            require(frame.cargo.emplace(cargo.id,std::move(cargo)).second,"Duplicate cargo ID");
        }
        // Validate stable identities for all complete entity maps. No merging with the previous frame.
        for (auto key : {"agents","banks","loans"}) {
            std::set<std::string> ids;
            for (auto row : snapshot[key].get_array().value())
                require(ids.insert(str(row[std::string_view(key)=="banks" ? "bank_id" : "id"])).second,"Duplicate entity ID");
        }
        result.frames.push_back(std::move(frame));
    }
    require(!result.frames.empty(),"Renderer stream has no frames");
    int sequence=0;
    for (auto file : index["events"].get_array().value()) {
        auto page=pageParser.parse(source.child(file)).value();
        if (source.packed) {
            require(str(page["schema_version"])=="gearth.renderer.v1" && str(page["run_id"])==result.run,"Mixed recording event contract/run");
            page=page["events"].value();
        }
        for (auto event : page.get_array().value()) {
            require(integer(event["seq"])==++sequence && str(event["run_id"])==result.run,"Noncontiguous stream events");
            if (sequence>1) eventJson+=",";
            eventJson+=simdjson::minify(event);
        }
    }
    require(result.currentOnly || sequence==lastSequence,"Incomplete stream event coverage");
    if (source.packed) require(source.frameCount==int64_t(result.frames.size()) && source.revision==revision
        && source.sequence==lastSequence,"Recording coverage does not match manifest");
    eventJson+="]}";
    result.events=EventReplay::parse(eventJson,world,true);
    require(result.events.lastTick<=result.frames.back().tick,"Event occurs after the last frame");
    result.events.lastTick=result.frames.back().tick;
    result.time=float(result.followLatest ? result.frames.back().tick : result.frames.front().tick);
    result.playing=result.followLatest || result.frames.back().tick>result.frames.front().tick;
    if (!result.liveInput) result.speed=std::max(.25f,float(result.frames.back().tick-result.frames.front().tick)/60.f);
    return result;
}

size_t PipelineReplay::frame_index(float tick) const {
    auto found=std::upper_bound(frames.begin(),frames.end(),tick,[](float t,const Frame& f) { return t<f.tick; });
    return found==frames.begin() ? 0 : size_t(found-frames.begin()-1);
}
std::vector<uint8_t> PipelineReplay::routes_at(float tick) const {
    const auto& frame=frames[frame_index(tick)];
    auto open=frame.routeOpen;
    for (const auto& e : events.events) {
        if (e.tick>tick) break;
        if (e.sequence<=frame.sequence || e.type!="border_changed") continue;
        for (size_t r=0;r<events.routes.size();++r)
            if (events.routes[r].from==e.from && events.routes[r].to==e.to)
                open[r]=e.open || (e.expires>=0 && tick>=e.expires);
    }
    return open;
}
std::vector<PipelineReplay::Pose> PipelineReplay::cargo_at(float tick) const {
    const auto& frame=frames[frame_index(tick)];
    auto cargo=frame.cargo;
    for (const auto& e : events.events) {
        if (e.tick>tick) break;
        if (e.sequence<=frame.sequence) continue;
        if (e.shipment()) cargo[e.cargo]={e.cargo,e.good,e.quantity,e.tick,0,0,e.path};
        else if (e.type=="shipment_delivered" || e.type=="shipment_failed") cargo.erase(e.cargo);
    }
    std::vector<Pose> poses;
    for (auto& [id,c] : cargo) {
        if (c.path.empty()) continue;
        if (c.edge==int(c.path.size())) { poses.push_back({id,c.good,c.path.back(),1.f}); continue; }
        int leg=c.edge;
        float elapsed=std::max(0.f,tick-c.started);
        // A zero remaining time is the START of the next edge in the backend, not its end.
        float remaining=c.remaining>0 ? float(c.remaining) : float(events.routes[c.path[leg]].travelTicks);
        while (elapsed>remaining && leg+1<int(c.path.size())) {
            elapsed-=remaining; remaining=float(events.routes[c.path[++leg]].travelTicks);
        }
        float duration=float(events.routes[c.path[leg]].travelTicks);
        poses.push_back({id,c.good,c.path[leg],std::clamp((duration-remaining+elapsed)/duration,0.f,1.f)});
    }
    return poses;
}
void PipelineReplay::advance(float seconds) {
    if (followLatest) time=float(frames.back().tick);
    else if (playing) {
        const float first=float(frames.front().tick), last=float(frames.back().tick);
        time+=seconds*speed;
        if (time>=last) {
            if (loop && last>first) time=first+std::fmod(time-first,last-first);
            else { time=last; playing=false; }
        }
    }
    events.time=time;
}
void PipelineReplay::restart() {
    time=float(frames.front().tick); events.time=time;
    followLatest=false; playing=frames.back().tick>frames.front().tick;
}
void PipelineReplay::draw(const ProceduralWorld& world) {
    auto size=ImGui::GetIO().DisplaySize;
    ImGui::SetNextWindowPos({std::max(18.f,size.x-378),18},ImGuiCond_Always);
    ImGui::SetNextWindowSize({360,std::max(260.f,size.y-36)},ImGuiCond_Always);
    ImGui::Begin("GEarth simulation",nullptr,ImGuiWindowFlags_NoMove|ImGuiWindowFlags_NoResize);
    ImGui::TextUnformatted(liveInput ? "Simulation stream" : "Recorded simulation playback");
    if (historyGap) ImGui::TextWrapped("History gap: earlier states were not captured.");
    const bool ended=time>=float(frames.back().tick);
    ImGui::TextUnformatted(followLatest ? "Following latest state" : ended ? "End of recording" : playing ? "Playing" : "Paused");
    if (ImGui::Button(playing && !followLatest ? "Pause" : "Play")) {
        if (ended) restart();
        else { playing=followLatest || !playing; followLatest=false; }
    }
    ImGui::SameLine(); if (ImGui::Button("Restart")) restart();
    ImGui::SameLine(); ImGui::Checkbox("Loop",&loop);
    if (liveInput) ImGui::Checkbox("Follow incoming data",&followLatest);
    if (ImGui::SliderFloat("Day",&time,float(frames.front().tick),float(frames.back().tick),"%.2f")) followLatest=false;
    const float duration=float(frames.back().tick-frames.front().tick);
    if (duration>0) {
        float seconds=duration/speed;
        if (ImGui::SliderFloat("Replay length",&seconds,10.f,600.f,"%.0f seconds",ImGuiSliderFlags_Logarithmic)) speed=duration/seconds;
        ImGui::Text("%.2f simulated days/sec",speed);
    }
    events.time=time;
    ImGui::TextWrapped("Run: %s | %s | %s",run.c_str(),mode.c_str(),status.c_str());
    if (liveInput) ImGui::TextWrapped("%s",connection.c_str());
    int f=int(frame_index(time)); const auto& frame=frames[f];
    ImGui::Text("State: day %d%s | revision %lld",frame.tick,frame.partial ? " (partial week)" : "",(long long)frame.revision);
    ImGui::TextWrapped("State values are recorded checkpoints. Cargo movement between them and vehicle types are illustrative.");
    if (ImGui::BeginTable("nations",3,ImGuiTableFlags_SizingStretchProp)) {
        ImGui::TableSetupColumn("Country"); ImGui::TableSetupColumn("Jobless"); ImGui::TableSetupColumn("Approval"); ImGui::TableHeadersRow();
        for (int c=0;c<FictionalCountryCount;++c) {
            const auto& n=frame.nations[c];
            ImGui::TableNextRow(); ImGui::TableNextColumn(); ImGui::TextUnformatted(names[c].c_str());
            ImGui::TableNextColumn(); ImGui::Text("%.0f%%",100.f*n.unemployed/std::max(1,n.residents));
            ImGui::TableNextColumn(); ImGui::Text("%.0f%%%s",100.f*n.approval,n.wars ? " war" : "");
        }
        ImGui::EndTable();
    }
    const char* categories[]={"agents","banks","shipments","loans","nations","metrics","events","relationships","routes","policies","countries","locations"};
    if (ImGui::Combo("Inspect",&category,categories,12)) inspectedFrame=-1;
    if (inspectedFrame!=f || (category==6 && inspectedTick!=int(time))) {
        records.clear();
        if (category==6) {
            for (const auto& e : events.events) if (e.tick<=time) records.emplace_back(fmt::format("{}: {} | {}",e.tick,e.type,e.id),e.detail);
        } else {
            simdjson::dom::parser parser;
            auto snapshot=parser.parse(frame.snapshot).value(); auto rows=snapshot[categories[category]].value();
            if (category==5) records.emplace_back("Recorded metrics (money in minor units)",simdjson::minify(rows));
            else for (auto row : rows.get_array().value()) records.emplace_back(str(row[category==1 ? "bank_id" : "id"]),simdjson::minify(row));
        }
        if (inspectedFrame!=f) selected=-1;
        inspectedFrame=f;
        inspectedTick=int(time);
    }
    ImGui::InputText("Find ID",filter,sizeof(filter));
    ImGui::Text("%zu records",records.size());
    ImGui::BeginChild("records",{0,110},true);
    std::vector<int> matches;
    for (int i=0;i<int(records.size());++i) if (records[i].first.find(filter)!=std::string::npos) matches.push_back(i);
    ImGuiListClipper clipper; clipper.Begin(int(matches.size()));
    while (clipper.Step()) for (int row=clipper.DisplayStart;row<clipper.DisplayEnd;++row) {
        int i=matches[row]; if (ImGui::Selectable(records[i].first.c_str(),selected==i)) selected=i;
    }
    ImGui::EndChild();
    ImGui::BeginChild("details",{0,0},true);
    if (selected>=0 && selected<int(records.size())) ImGui::TextWrapped("%s",records[selected].second.c_str());
    ImGui::EndChild(); ImGui::End();
}

void pipeline_replay_self_test() {
    PipelineReplay replay;
    replay.frames.resize(3);
    replay.frames[0].tick=7; replay.frames[1].tick=14; replay.frames[2].tick=15;
    replay.restart(); replay.advance(2);
    require(replay.time==7.5f && replay.events.time==7.5f,"Playback advances continuously in wall-clock time");
    replay.playing=false; replay.advance(10);
    require(replay.time==7.5f,"Pause holds the recorded state");
    replay.playing=true; replay.advance(100);
    require(replay.time==15 && !replay.playing,"Playback stops honestly at the final partial day");
    replay.loop=true; replay.restart(); replay.advance(34);
    require(replay.time==7.5f && replay.playing && replay.frame_index(replay.time)==0,"Loop wraps to the first available day with fractional time preserved");
    replay.advance(64);
    require(replay.time==7.5f,"Loop handles elapsed time longer than the recording");
    replay.followLatest=true; replay.advance(1);
    require(replay.time==15,"Live following remains available");
    replay.restart();
    require(replay.time==7 && replay.events.time==7 && !replay.followLatest && replay.playing,"Restart resets both playback and event clocks");
    replay.frames.resize(1); replay.restart(); replay.advance(10);
    require(replay.time==7 && !replay.playing,"A single snapshot cannot invent a moving timeline");
    fmt::print("Recorded playback timing, pause, end, restart and loop passed\n");
}
