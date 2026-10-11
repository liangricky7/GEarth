#pragma once
#include <bit>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <vector>

struct DdsMip { uint32_t width, height; size_t offset, size; };
struct DdsTexture { std::vector<DdsMip> mips; size_t bytes{}; };

inline DdsTexture parse_dds(std::span<const uint8_t> data, uint32_t limit = 2048) {
    auto word = [&](size_t at) {
        if (at + 4 > data.size()) throw std::runtime_error("Truncated DDS header");
        return uint32_t(data[at]) | uint32_t(data[at+1]) << 8 | uint32_t(data[at+2]) << 16 | uint32_t(data[at+3]) << 24;
    };
    if (data.size() < 148 || word(0) != 0x20534444 || word(4) != 124 || word(76) != 32 ||
        !(word(80) & 4) || word(84) != 0x30315844 || (word(128) != 98 && word(128) != 99) ||
        word(132) != 3 || (word(136) & 4) || word(140) != 1 || word(24) > 1 || word(112) != 0)
        throw std::runtime_error("Expected a 2D BC7 DDS with a DX10 header");
    uint32_t width = word(16), height = word(12), count = word(28);
    if (!limit || !width || !height || width > 16384 || height > 16384 || !count ||
        count > std::bit_width(width > height ? width : height)) throw std::runtime_error("Invalid DDS dimensions or mip count");
    DdsTexture result; size_t offset = 148;
    for (uint32_t i = 0; i < count; ++i) {
        size_t bytes = size_t((width+3)/4) * ((height+3)/4) * 16;
        if (bytes > data.size() - offset) throw std::runtime_error("Truncated DDS mip payload");
        if (width <= limit && height <= limit) { result.mips.push_back({width,height,offset,bytes}); result.bytes += bytes; }
        offset += bytes; width = width > 1 ? width/2 : 1; height = height > 1 ? height/2 : 1;
    }
    if (result.mips.empty()) throw std::runtime_error("DDS needs smaller mip levels for the texture budget");
    return result;
}

inline void dds_self_test() {
    std::vector<uint8_t> bytes(148+16*3);
    auto put = [&](size_t at, uint32_t v) { for (int i=0;i<4;++i) bytes[at+i]=uint8_t(v>>(8*i)); };
    put(0,0x20534444); put(4,124); put(12,4); put(16,4); put(28,3); put(76,32);
    put(80,4); put(84,0x30315844); put(128,98); put(132,3); put(140,1);
    auto check = [](bool ok) { if (!ok) throw std::runtime_error("DDS self-test failed"); };
    auto mip = parse_dds(bytes,2); check(mip.mips.size()==2 && mip.mips[0].offset==164 && mip.bytes==32);
    auto rejects = [&] { try { parse_dds(bytes); } catch (const std::runtime_error&) { return; } throw std::runtime_error("Bad DDS accepted"); };
    put(140,2); rejects(); put(140,1); put(128,28); rejects(); put(128,98);
    put(16,0); rejects(); put(16,4); put(28,99); rejects(); put(28,3);
    bytes.pop_back(); rejects(); bytes.resize(12); rejects();
}
