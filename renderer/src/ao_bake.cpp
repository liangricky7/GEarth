#include "ao_bake.h"
#include <algorithm>
#include <execution>
#include <numeric>

namespace {
struct Box {
    glm::vec3 lo{1e30f}, hi{-1e30f};
    void grow(glm::vec3 p) { lo = glm::min(lo, p); hi = glm::max(hi, p); }
};
// Flat BVH: a leaf holds `count` triangles from `first` in the reordered index list; an inner node's children
// are at index+1 and `first` (right child).
struct BvhNode { Box box; uint32_t first{}, count{}; };

struct Bvh {
    std::vector<BvhNode> nodes;
    std::vector<uint32_t> order;
    std::span<const glm::vec3> tris;

    explicit Bvh(std::span<const glm::vec3> triangles) : tris(triangles) {
        uint32_t count = uint32_t(triangles.size() / 3);
        order.resize(count); std::iota(order.begin(), order.end(), 0u);
        std::vector<glm::vec3> centroids(count);
        for (uint32_t i = 0; i < count; ++i) centroids[i] = (tris[i*3]+tris[i*3+1]+tris[i*3+2]) / 3.f;
        nodes.reserve(count * 2 / 3 + 1);
        build(0, count, centroids);
    }
    // ponytail: median split, not SAH; good enough for a one-off bake, switch to binned SAH if bakes get slow.
    uint32_t build(uint32_t begin, uint32_t end, const std::vector<glm::vec3>& centroids) {
        uint32_t index = uint32_t(nodes.size());
        nodes.push_back({});
        Box box, centreBox;
        for (uint32_t i = begin; i < end; ++i) {
            for (int c = 0; c < 3; ++c) box.grow(tris[order[i]*3+c]);
            centreBox.grow(centroids[order[i]]);
        }
        nodes[index].box = box;
        if (end - begin <= 4) { nodes[index].first = begin; nodes[index].count = end - begin; return index; }
        glm::vec3 extent = centreBox.hi - centreBox.lo;
        int axis = extent.x > extent.y ? (extent.x > extent.z ? 0 : 2) : (extent.y > extent.z ? 1 : 2);
        uint32_t middle = (begin + end) / 2;
        std::nth_element(order.begin()+begin, order.begin()+middle, order.begin()+end,
            [&](uint32_t a, uint32_t b) { return centroids[a][axis] < centroids[b][axis]; });
        build(begin, middle, centroids);
        nodes[index].first = build(middle, end, centroids);
        return index;
    }
    static bool hit_box(const Box& box, glm::vec3 origin, glm::vec3 inverse, float far) {
        glm::vec3 t0 = (box.lo - origin) * inverse, t1 = (box.hi - origin) * inverse;
        glm::vec3 near = glm::min(t0, t1), farV = glm::max(t0, t1);
        float enter = std::max({near.x, near.y, near.z, 0.f}), leave = std::min({farV.x, farV.y, farV.z, far});
        return enter <= leave;
    }
    // Möller-Trumbore, any hit closer than `far`.
    bool hit_triangle(uint32_t t, glm::vec3 origin, glm::vec3 direction, float far) const {
        glm::vec3 a = tris[t*3], e1 = tris[t*3+1]-a, e2 = tris[t*3+2]-a, p = glm::cross(direction, e2);
        float det = glm::dot(e1, p);
        if (std::abs(det) < 1e-12f) return false;
        float invDet = 1.f / det;
        glm::vec3 s = origin - a;
        float u = glm::dot(s, p) * invDet;
        if (u < 0 || u > 1) return false;
        glm::vec3 q = glm::cross(s, e1);
        float v = glm::dot(direction, q) * invDet;
        if (v < 0 || u + v > 1) return false;
        float distance = glm::dot(e2, q) * invDet;
        return distance > 0 && distance < far;
    }
    bool occluded(glm::vec3 origin, glm::vec3 direction, float far) const {
        glm::vec3 inverse = 1.f / glm::vec3(direction.x ? direction.x : 1e-12f, direction.y ? direction.y : 1e-12f,
            direction.z ? direction.z : 1e-12f);
        uint32_t stack[64]; int top = 0; stack[top++] = 0;
        while (top) {
            const BvhNode& node = nodes[stack[--top]];
            if (!hit_box(node.box, origin, inverse, far)) continue;
            if (node.count) {
                for (uint32_t i = 0; i < node.count; ++i) if (hit_triangle(order[node.first+i], origin, direction, far)) return true;
            } else if (top < 62) {
                stack[top++] = node.first;
                stack[top++] = uint32_t(&node - nodes.data()) + 1;
            }
        }
        return false;
    }
};
}

void bake_sky_visibility(std::span<const glm::vec3> triangles, std::span<const glm::vec3> points,
    std::span<const glm::vec3> normals, float maxDistance, int rays, std::span<float> out) {
    if (triangles.empty()) { std::fill(out.begin(), out.end(), 1.f); return; }
    Bvh bvh(triangles);
    // Fixed cosine-weighted hemisphere directions (Fibonacci spiral), rotated per vertex to trade banding for noise.
    std::vector<glm::vec3> directions(rays);
    for (int i = 0; i < rays; ++i) {
        float r = std::sqrt((i + .5f) / rays), phi = i * 2.39996323f;
        directions[i] = {r*std::cos(phi), r*std::sin(phi), std::sqrt(std::max(0.f, 1 - r*r))};
    }
    std::vector<uint32_t> indices(points.size()); std::iota(indices.begin(), indices.end(), 0u);
    std::for_each(std::execution::par, indices.begin(), indices.end(), [&](uint32_t i) {
        glm::vec3 n = glm::length(normals[i]) > 0 ? glm::normalize(normals[i]) : glm::vec3(0, 1, 0);
        // Orthonormal basis (Duff et al. 2017), spun by a per-vertex hash.
        float sign = std::copysign(1.f, n.z), a = -1.f / (sign + n.z), b = n.x * n.y * a;
        glm::vec3 t{1 + sign*n.x*n.x*a, sign*b, -sign*n.x}, s{b, sign + n.y*n.y*a, -n.y};
        float spin = float((i * 2654435761u) >> 8) / float(1u << 24) * 6.2831853f, c = std::cos(spin), d = std::sin(spin);
        glm::vec3 u = t*c + s*d, w = -t*d + s*c;
        glm::vec3 origin = points[i] + n * (maxDistance * .002f);
        int open = 0;
        for (const auto& dir : directions) open += !bvh.occluded(origin, u*dir.x + w*dir.y + n*dir.z, maxDistance);
        out[i] = float(open) / float(rays);
    });
}

void ao_bake_self_test() {
    auto check = [](bool good, const char* message) { if (!good) throw std::runtime_error(message); };
    // A closed unit box (12 triangles) and an open floor quad.
    std::vector<glm::vec3> box;
    glm::vec3 c[8];
    for (int i = 0; i < 8; ++i) c[i] = {i&1 ? 1.f : -1.f, i&2 ? 1.f : -1.f, i&4 ? 1.f : -1.f};
    const int faces[6][4] = {{0,4,6,2},{1,3,7,5},{0,1,5,4},{2,6,7,3},{0,2,3,1},{4,5,7,6}};
    for (auto& f : faces) box.insert(box.end(), {c[f[0]], c[f[1]], c[f[2]], c[f[0]], c[f[2]], c[f[3]]});
    glm::vec3 inside[1] = {{0, -.9f, 0}}, up[1] = {{0, 1, 0}};
    float result[1];
    bake_sky_visibility(box, inside, up, 10.f, 32, result);
    check(result[0] == 0, "A point inside a closed box must see no sky");
    std::vector<glm::vec3> floor{{-5,0,-5},{5,0,-5},{5,0,5},{-5,0,-5},{5,0,5},{-5,0,5}};
    glm::vec3 above[1] = {{0, .01f, 0}};
    bake_sky_visibility(floor, above, up, 10.f, 32, result);
    check(result[0] == 1, "A point on an open floor must see the whole sky");
    // A wall beside the point blocks roughly half the hemisphere.
    std::vector<glm::vec3> wall = floor;
    wall.insert(wall.end(), {{.05f,0,-5},{.05f,5,-5},{.05f,5,5},{.05f,0,-5},{.05f,5,5},{.05f,0,5}});
    bake_sky_visibility(wall, above, up, 10.f, 64, result);
    check(result[0] > .25f && result[0] < .75f, "A point at the foot of a wall must see part of the sky");
    fmt::print("Sky-visibility bake: closed box, open floor and wall corner passed\n");
}
