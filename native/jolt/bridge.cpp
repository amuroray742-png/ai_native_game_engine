#include <Jolt/Jolt.h>
#include <Jolt/RegisterTypes.h>
#include <Jolt/Core/Factory.h>
#include <Jolt/Core/TempAllocator.h>
#include <Jolt/Core/JobSystemSingleThreaded.h>
#include <Jolt/Physics/PhysicsSystem.h>
#include <Jolt/Physics/StateRecorderImpl.h>
#include <Jolt/Physics/Collision/Shape/BoxShape.h>
#include <Jolt/Physics/Collision/Shape/SphereShape.h>
#include <Jolt/Physics/Body/BodyCreationSettings.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <stdexcept>
#include <tuple>
#include <vector>

using namespace JPH;
#ifdef _WIN32
#define API extern "C" __declspec(dllexport)
#else
#define API extern "C" __attribute__((visibility("default")))
#endif

// All wire structures have explicit packing; no C++ object crosses the ABI.
#pragma pack(push, 1)
struct Vec { float x, y, z; };
struct Rotation { float x, y, z, w; };
struct Definition
{
    uint64_t entity;
    int32_t shape, dynamic;
    Vec size, position;
    Rotation rotation;
    float mass, friction, restitution;
};
struct BodyView
{
    uint64_t entity;
    Vec position;
    Rotation rotation;
    Vec velocity, angularVelocity;
    int32_t active, dynamic;
};
struct Contact
{
    uint64_t first, second;
    uint32_t firstShape, secondShape;
    int32_t kind;
    auto Key() const { return std::tie(first, second, firstShape, secondShape, kind); }
};
#pragma pack(pop)
static_assert(sizeof(Definition) == 68 && sizeof(BodyView) == 68 && sizeof(Contact) == 28);

namespace
{
constexpr uint32_t capacity = 1024;
std::mutex foundationMutex;
uint32_t worldCount = 0;
thread_local std::string lastError;

void Require(bool condition, const char *message)
{
    if (!condition) throw std::runtime_error(message);
}

void AcquireFoundation()
{
    std::lock_guard<std::mutex> lock(foundationMutex);
    if (worldCount == 0)
    {
        RegisterDefaultAllocator();
        Factory::sInstance = new Factory();
        RegisterTypes();
    }
    ++worldCount;
}

void ReleaseFoundation()
{
    std::lock_guard<std::mutex> lock(foundationMutex);
    if (--worldCount == 0)
    {
        UnregisterTypes();
        delete Factory::sInstance;
        Factory::sInstance = nullptr;
    }
}

class Layers final : public BroadPhaseLayerInterface
{
public:
    uint GetNumBroadPhaseLayers() const override { return 2; }
    BroadPhaseLayer GetBroadPhaseLayer(ObjectLayer layer) const override { return BroadPhaseLayer(uint8(layer)); }
#if defined(JPH_EXTERNAL_PROFILE) || defined(JPH_PROFILE_ENABLED)
    const char *GetBroadPhaseLayerName(BroadPhaseLayer layer) const override { return layer.GetValue() == 0 ? "static" : "dynamic"; }
#endif
};
class PairFilter final : public ObjectLayerPairFilter
{
public:
    bool ShouldCollide(ObjectLayer a, ObjectLayer b) const override { return a != 0 || b != 0; }
};
class BroadFilter final : public ObjectVsBroadPhaseLayerFilter
{
public:
    bool ShouldCollide(ObjectLayer a, BroadPhaseLayer b) const override { return a != 0 || b.GetValue() != 0; }
};

struct Entry { Definition definition; BodyID body; };
struct World;
class Contacts final : public ContactListener
{
public:
    explicit Contacts(World &world) : owner(world) { }
    void OnContactAdded(const Body &, const Body &, const ContactManifold &, ContactSettings &) override;
    void OnContactRemoved(const SubShapeIDPair &) override;
private:
    World &owner;
};

struct World
{
    Layers layers;
    PairFilter pairs;
    BroadFilter broad;
    TempAllocatorImpl temporary { 16 * 1024 * 1024 };
    JobSystemSingleThreaded jobs { 2048 };
    Contacts listener { *this };
    PhysicsSystem system;
    std::map<uint64_t, Entry> bodies;
    std::map<uint32_t, uint64_t> identities;
    std::array<uint8_t, capacity> generations {};
    std::array<bool, capacity> occupied {};
    std::vector<Contact> contacts;
    bool contactFault = false;

    World()
    {
        system.Init(capacity, 0, 4096, 4096, layers, broad, pairs);
        system.SetGravity(Vec3(0, -9.81f, 0));
        system.SetContactListener(&listener);
    }
    ~World()
    {
        system.SetContactListener(nullptr);
        auto &interface = system.GetBodyInterface();
        for (const auto &item : bodies)
        {
            interface.RemoveBody(item.second.body);
            interface.DestroyBody(item.second.body);
        }
    }

    void AddContact(uint64_t a, uint64_t b, uint32_t sa, uint32_t sb, int kind)
    {
        if (b < a) { std::swap(a, b); std::swap(sa, sb); }
        contacts.push_back({ a, b, sa, sb, kind });
    }

    BodyID Find(uint64_t entity) const
    {
        auto found = bodies.find(entity);
        Require(found != bodies.end(), "Unknown physics entity");
        return found->second.body;
    }

    void Add(const Definition &definition, BodyID id)
    {
        Require(definition.entity != 0 && bodies.find(definition.entity) == bodies.end(), "Duplicate or zero entity");
        Require(id.GetIndex() < capacity && !occupied[id.GetIndex()], "Invalid body slot");
        Require(definition.shape == 0 || definition.shape == 1, "Unsupported shape");
        Require(definition.dynamic == 0 || definition.dynamic == 1, "Unsupported motion");
        const float values[] = { definition.size.x, definition.size.y, definition.size.z,
            definition.position.x, definition.position.y, definition.position.z,
            definition.rotation.x, definition.rotation.y, definition.rotation.z, definition.rotation.w,
            definition.mass, definition.friction, definition.restitution };
        for (float value : values) Require(std::isfinite(value), "Non-finite body field");
        Require(definition.size.x > 0 && definition.size.y > 0 && definition.size.z > 0 && definition.mass > 0
            && definition.friction >= 0 && definition.restitution >= 0 && definition.restitution <= 1, "Invalid body dimensions or material");
        Quat rotation(definition.rotation.x, definition.rotation.y, definition.rotation.z, definition.rotation.w);
        Require(rotation.IsNormalized(), "Rotation must be normalized");
        RefConst<Shape> shape = definition.shape == 0
            ? static_cast<Shape *>(new SphereShape(definition.size.x))
            : static_cast<Shape *>(new BoxShape(Vec3(definition.size.x, definition.size.y, definition.size.z), 0.0f));
        BodyCreationSettings settings(shape, RVec3(definition.position.x, definition.position.y, definition.position.z), rotation,
            definition.dynamic ? EMotionType::Dynamic : EMotionType::Static, ObjectLayer(definition.dynamic));
        settings.mUserData = definition.entity;
        settings.mFriction = definition.friction;
        settings.mRestitution = definition.restitution;
        settings.mOverrideMassProperties = EOverrideMassProperties::CalculateInertia;
        settings.mMassPropertiesOverride.mMass = definition.mass;
        Body *body = system.GetBodyInterface().CreateBodyWithID(id, settings);
        Require(body != nullptr, "Could not create body");
        bodies.emplace(definition.entity, Entry { definition, id });
        identities[id.GetIndexAndSequenceNumber()] = definition.entity;
        occupied[id.GetIndex()] = true;
        generations[id.GetIndex()] = id.GetSequenceNumber();
        system.GetBodyInterface().AddBody(id, definition.dynamic ? EActivation::Activate : EActivation::DontActivate);
    }
};

void Contacts::OnContactAdded(const Body &a, const Body &b, const ContactManifold &manifold, ContactSettings &)
{
    try { owner.AddContact(a.GetUserData(), b.GetUserData(), manifold.mSubShapeID1.GetValue(), manifold.mSubShapeID2.GetValue(), 1); }
    catch (...) { owner.contactFault = true; }
}
void Contacts::OnContactRemoved(const SubShapeIDPair &pair)
{
    auto a = owner.identities.find(pair.GetBody1ID().GetIndexAndSequenceNumber());
    auto b = owner.identities.find(pair.GetBody2ID().GetIndexAndSequenceNumber());
    if (a == owner.identities.end() || b == owner.identities.end()) { owner.contactFault = true; return; }
    try { owner.AddContact(a->second, b->second, pair.GetSubShapeID1().GetValue(), pair.GetSubShapeID2().GetValue(), 2); }
    catch (...) { owner.contactFault = true; }
}

class Reader final : public StateRecorder
{
public:
    Reader(const uint8_t *bytes, size_t length) : bytes(bytes), length(length) { }
    void WriteBytes(const void *, size_t) override { throw std::runtime_error("Read-only recorder"); }
    void ReadBytes(void *destination, size_t size) override
    {
        Require(size <= length - position, "Truncated physics state");
        std::memcpy(destination, bytes + position, size);
        position += size;
    }
    bool IsEOF() const override { return position == length; }
    bool IsFailed() const override { return false; }
private:
    const uint8_t *bytes;
    size_t length, position = 0;
};

#include "validate_state.h"

World &Get(void *handle) { Require(handle != nullptr, "Null physics world"); return *static_cast<World *>(handle); }
template<class F> int Guard(F action)
{
    try { action(); lastError.clear(); return 0; }
    catch (const std::exception &error) { lastError = error.what(); return -1; }
    catch (...) { lastError = "Unknown native physics failure"; return -1; }
}
}

API const char *aj_error() { return lastError.c_str(); }
API const char *aj_version() { return "jolt-5.6.0;e77f175;abi=2;float32;single-thread;deterministic;sse4;capacity=1024"; }
API uint32_t aj_world_count() { std::lock_guard<std::mutex> lock(foundationMutex); return worldCount; }
API int aj_create(void **result)
{
    return Guard([&] {
        Require(result != nullptr, "Null result");
        *result = nullptr;
        AcquireFoundation();
        try { *result = new World(); }
        catch (...) { ReleaseFoundation(); throw; }
    });
}
API void aj_destroy(void *handle)
{
    if (handle) { delete static_cast<World *>(handle); ReleaseFoundation(); }
}
API int aj_add(void *handle, const Definition *definition)
{
    return Guard([&] {
        auto &world = Get(handle);
        Require(definition != nullptr, "Null definition");
        uint32_t index = 0;
        while (index < capacity && (world.occupied[index]
            || world.identities.count(BodyID(index, world.generations[index]).GetIndexAndSequenceNumber()) != 0)) ++index;
        Require(index < capacity, "Body capacity exceeded");
        world.Add(*definition, BodyID(index, world.generations[index]));
    });
}
API int aj_remove(void *handle, uint64_t entity)
{
    return Guard([&] {
        auto &world = Get(handle);
        BodyID body = world.Find(entity);
        world.system.GetBodyInterface().RemoveBody(body);
        world.system.GetBodyInterface().DestroyBody(body);
        world.bodies.erase(entity);
        world.occupied[body.GetIndex()] = false;
        ++world.generations[body.GetIndex()];
        // Keep identity until the next update has reported removed contacts.
    });
}
API int aj_impulse(void *handle, uint64_t entity, Vec impulse)
{
    return Guard([&] {
        Require(std::isfinite(impulse.x) && std::isfinite(impulse.y) && std::isfinite(impulse.z), "Invalid impulse");
        auto &world = Get(handle);
        BodyID body = world.Find(entity);
        Require(world.bodies.at(entity).definition.dynamic != 0, "Static body cannot receive impulse");
        world.system.GetBodyInterface().AddImpulse(body, Vec3(impulse.x, impulse.y, impulse.z));
    });
}
API int aj_step(void *handle, float seconds)
{
    return Guard([&] {
        Require(std::isfinite(seconds) && seconds > 0 && seconds <= 1, "Invalid step duration");
        auto &world = Get(handle);
        Require(world.system.Update(seconds, 1, &world.temporary, &world.jobs) == EPhysicsUpdateError::None, "Physics capacity exhausted");
        Require(!world.contactFault, "Contact collection failed");
        std::sort(world.contacts.begin(), world.contacts.end(), [](const Contact &a, const Contact &b) { return a.Key() < b.Key(); });
        world.contacts.erase(std::unique(world.contacts.begin(), world.contacts.end(), [](const Contact &a, const Contact &b) { return a.Key() == b.Key(); }), world.contacts.end());
        world.identities.clear();
        for (const auto &entry : world.bodies) world.identities[entry.second.body.GetIndexAndSequenceNumber()] = entry.first;
    });
}
API int aj_contacts(void *handle, Contact *destination, int capacityValue, int *count)
{
    return Guard([&] {
        auto &world = Get(handle);
        Require(count != nullptr, "Null count");
        *count = int(world.contacts.size());
        if (destination)
        {
            Require(capacityValue >= *count, "Contact buffer too small");
            std::copy(world.contacts.begin(), world.contacts.end(), destination);
            world.contacts.clear();
        }
    });
}
API int aj_bodies(void *handle, BodyView *destination, int capacityValue, int *count)
{
    return Guard([&] {
        auto &world = Get(handle);
        Require(count != nullptr, "Null count");
        *count = int(world.bodies.size());
        if (!destination) return;
        Require(capacityValue >= *count, "Body buffer too small");
        auto &interface = world.system.GetBodyInterface();
        int index = 0;
        for (const auto &entry : world.bodies)
        {
            BodyID id = entry.second.body;
            RVec3 p = interface.GetPosition(id);
            Quat q = interface.GetRotation(id);
            Vec3 v = interface.GetLinearVelocity(id), a = interface.GetAngularVelocity(id);
            destination[index++] = { entry.first, { p.GetX(), p.GetY(), p.GetZ() }, { q.GetX(), q.GetY(), q.GetZ(), q.GetW() },
                { v.GetX(), v.GetY(), v.GetZ() }, { a.GetX(), a.GetY(), a.GetZ() }, interface.IsActive(id) ? 1 : 0,
                entry.second.definition.dynamic };
        }
    });
}
API int aj_save(void *handle, uint8_t *destination, int capacityValue, int *length)
{
    return Guard([&] {
        auto &world = Get(handle);
        Require(world.contacts.empty(), "Drain contacts before saving");
        Require(world.identities.size() == world.bodies.size(), "Advance removed bodies before saving");
        StateRecorderImpl recorder;
        uint32_t magic = 0x324a4e41, count = uint32_t(world.bodies.size());
        recorder.Write(magic);
        recorder.WriteBytes(world.generations.data(), world.generations.size());
        recorder.Write(count);
        for (const auto &entry : world.bodies)
        {
            recorder.WriteBytes(&entry.second.definition, sizeof(Definition));
            recorder.Write(entry.second.body.GetIndexAndSequenceNumber());
        }
        world.system.SaveState(recorder);
        auto data = recorder.GetData();
        Require(length != nullptr && data.size() < 64 * 1024 * 1024, "Invalid snapshot size");
        *length = int(data.size());
        if (destination)
        {
            Require(capacityValue >= *length, "Snapshot buffer too small");
            std::memcpy(destination, data.data(), data.size());
        }
    });
}
API int aj_restore(const uint8_t *bytes, int length, void **result)
{
    return Guard([&] {
        Require(result != nullptr, "Null result");
        *result = nullptr;
        Require(bytes != nullptr && length > 0 && length < 64 * 1024 * 1024, "Invalid snapshot size");
        AcquireFoundation();
        try
        {
            auto world = std::make_unique<World>();
            Reader recorder(bytes, size_t(length));
            uint32_t magic, count;
            recorder.Read(magic);
            Require(magic == 0x324a4e41, "Unsupported physics state");
            recorder.ReadBytes(world->generations.data(), world->generations.size());
            recorder.Read(count);
            Require(count <= capacity, "Invalid body count");
            for (uint32_t index = 0; index < count; ++index)
            {
                Definition definition {};
                uint32_t rawId;
                recorder.ReadBytes(&definition, sizeof(definition));
                recorder.Read(rawId);
                Require((rawId & BodyID::cBroadPhaseBit) == 0, "Invalid BodyID");
                BodyID id(rawId);
                Require(id.GetIndex() < capacity && world->generations[id.GetIndex()] == id.GetSequenceNumber(), "Invalid body generation");
                world->Add(definition, id);
            }
            ValidateJoltState(recorder, *world);
            recorder.SetValidating(true);
            Require(world->system.RestoreState(recorder) && recorder.IsEOF(), "Invalid Jolt state");
            *result = world.release();
        }
        catch (...) { ReleaseFoundation(); throw; }
    });
}
