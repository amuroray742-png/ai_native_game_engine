#include <Jolt/Jolt.h>
#include "joltc_extensions.h"
#include <Jolt/Core/TempAllocator.h>
#include <Jolt/Physics/PhysicsSystem.h>
#include <Jolt/Physics/StateRecorderImpl.h>
#include <Jolt/Physics/Collision/ContactListener.h>
#include <atomic>
#include <thread>
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
std::atomic<uint64_t> workerContactCount { 0 };
std::mutex registryMutex;
extern const JPH_ContactListener_Procs contactProcs;
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
        Require(JPH_Init(), "joltc initialization failed");
        JPH_ContactListener_SetProcs(&contactProcs);
    }
    ++worldCount;
}

void ReleaseFoundation()
{
    std::lock_guard<std::mutex> lock(foundationMutex);
    if (--worldCount == 0)
    {
        JPH_ContactListener_SetProcs(nullptr);
        AJ_Shutdown();
    }
}

// joltc owns the layer filters after successful system creation.
void DestroySystem(JPH_PhysicsSystem *system)
{
    std::lock_guard<std::mutex> lock(registryMutex);
    JPH_PhysicsSystem_Destroy(system);
}
template<class T, void (*Destroy)(T *)> using Owner = std::unique_ptr<T, decltype(Destroy)>;

struct Entry { Definition definition; BodyID body; };
struct World;
struct World
{
    TempAllocatorImpl temporary { 16 * 1024 * 1024 };
    Owner<JPH_JobSystem, JPH_JobSystem_Destroy> jobs { nullptr, JPH_JobSystem_Destroy };
    Owner<JPH_ContactListener, JPH_ContactListener_Destroy> listener { nullptr, JPH_ContactListener_Destroy };
    Owner<JPH_PhysicsSystem, DestroySystem> system { nullptr, DestroySystem };
    std::map<uint64_t, Entry> bodies;
    std::map<uint32_t, uint64_t> identities;
    std::array<uint8_t, capacity> generations {};
    std::array<bool, capacity> occupied {};
    std::vector<Contact> contacts;
    std::mutex contactsMutex;
    std::atomic<bool> contactFault { false };
    const uint32_t workerThreads;
    std::thread::id stepThread;

    explicit World(uint32_t workers) : workerThreads(workers)
    {
        Require(workers <= 16, "workerThreads must be between 0 and 16");
        Owner<JPH_BroadPhaseLayerInterface, JPH_BroadPhaseLayerInterface_Destroy> layers(
            JPH_BroadPhaseLayerInterfaceTable_Create(2, 2), JPH_BroadPhaseLayerInterface_Destroy);
        JPH_BroadPhaseLayerInterfaceTable_MapObjectToBroadPhaseLayer(layers.get(), 0, 0);
        JPH_BroadPhaseLayerInterfaceTable_MapObjectToBroadPhaseLayer(layers.get(), 1, 1);
        Owner<JPH_ObjectLayerPairFilter, JPH_ObjectLayerPairFilter_Destroy> pairs(
            JPH_ObjectLayerPairFilterTable_Create(2), JPH_ObjectLayerPairFilter_Destroy);
        JPH_ObjectLayerPairFilterTable_EnableCollision(pairs.get(), 0, 1);
        JPH_ObjectLayerPairFilterTable_EnableCollision(pairs.get(), 1, 1);
        Owner<JPH_ObjectVsBroadPhaseLayerFilter, JPH_ObjectVsBroadPhaseLayerFilter_Destroy> broad(
            JPH_ObjectVsBroadPhaseLayerFilterTable_Create(layers.get(), 2, pairs.get(), 2), JPH_ObjectVsBroadPhaseLayerFilter_Destroy);
        JPH_PhysicsSystemSettings settings { capacity, 0, 4096, 4096, 0, layers.get(), pairs.get(), broad.get() };
        {
            std::lock_guard<std::mutex> lock(registryMutex);
            system.reset(AJ_CreatePhysicsSystem(settings));
        }
        Require(system != nullptr, "Could not create joltc system");
        layers.release(); pairs.release(); broad.release();
        JPH_Vec3 gravity { 0, -9.81f, 0 };
        JPH_PhysicsSystem_SetGravity(system.get(), &gravity);
        AJ_EnableDeterminism(system.get());
        listener.reset(JPH_ContactListener_Create(this));
        JPH_PhysicsSystem_SetContactListener(system.get(), listener.get());
        if (workers == 0) jobs.reset(AJ_CreateSingleThreadedJobs());
        else
        {
            JobSystemThreadPoolConfig config { 2048, 8, int32_t(workers) };
            jobs.reset(JPH_JobSystemThreadPool_Create(&config));
        }
    }
    ~World()
    {
        JPH_PhysicsSystem_SetContactListener(system.get(), nullptr);
        auto *interface = JPH_PhysicsSystem_GetBodyInterface(system.get());
        for (const auto &item : bodies)
        {
            auto id = item.second.body.GetIndexAndSequenceNumber();
            JPH_BodyInterface_RemoveBody(interface, id);
            JPH_BodyInterface_DestroyBody(interface, id);
        }
        // Stop workers before destroying the system or the per-world allocator.
        jobs.reset();
    }

    void AddContact(uint64_t a, uint64_t b, uint32_t sa, uint32_t sb, int kind)
    {
        if (b < a) { std::swap(a, b); std::swap(sa, sb); }
        std::lock_guard<std::mutex> lock(contactsMutex);
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
        JPH_Vec3 size { definition.size.x, definition.size.y, definition.size.z };
        Owner<JPH_Shape, JPH_Shape_Destroy> shape(definition.shape == 0
            ? reinterpret_cast<JPH_Shape *>(JPH_SphereShape_Create(size.x))
            : reinterpret_cast<JPH_Shape *>(JPH_BoxShape_Create(&size, 0.0f)), JPH_Shape_Destroy);
        JPH_RVec3 position { definition.position.x, definition.position.y, definition.position.z };
        JPH_Quat orientation { definition.rotation.x, definition.rotation.y, definition.rotation.z, definition.rotation.w };
        Owner<JPH_BodyCreationSettings, JPH_BodyCreationSettings_Destroy> settings(
            JPH_BodyCreationSettings_Create3(shape.get(), &position, &orientation,
                definition.dynamic ? JPH_MotionType_Dynamic : JPH_MotionType_Static, definition.dynamic), JPH_BodyCreationSettings_Destroy);
        JPH_BodyCreationSettings_SetUserData(settings.get(), definition.entity);
        JPH_BodyCreationSettings_SetFriction(settings.get(), definition.friction);
        JPH_BodyCreationSettings_SetRestitution(settings.get(), definition.restitution);
        JPH_BodyCreationSettings_SetOverrideMassProperties(settings.get(), JPH_OverrideMassProperties_CalculateInertia);
        JPH_MassProperties mass {};
        mass.mass = definition.mass;
        JPH_BodyCreationSettings_SetMassPropertiesOverride(settings.get(), &mass);
        auto *interface = JPH_PhysicsSystem_GetBodyInterface(system.get());
        JPH_Body *body = JPH_BodyInterface_CreateBodyWithID(interface, id.GetIndexAndSequenceNumber(), settings.get());
        Require(body != nullptr, "Could not create body");
        try
        {
            bodies.emplace(definition.entity, Entry { definition, id });
            identities.emplace(id.GetIndexAndSequenceNumber(), definition.entity);
            JPH_BodyInterface_AddBody(interface, id.GetIndexAndSequenceNumber(), definition.dynamic ? JPH_Activation_Activate : JPH_Activation_DontActivate);
        }
        catch (...)
        {
            if (JPH_BodyInterface_IsAdded(interface, id.GetIndexAndSequenceNumber()))
                JPH_BodyInterface_RemoveBody(interface, id.GetIndexAndSequenceNumber());
            JPH_BodyInterface_DestroyBody(interface, id.GetIndexAndSequenceNumber());
            bodies.erase(definition.entity);
            identities.erase(id.GetIndexAndSequenceNumber());
            throw;
        }
        occupied[id.GetIndex()] = true;
        generations[id.GetIndex()] = id.GetSequenceNumber();
    }
};

void OnContactAdded(void *context, const JPH_Body *a, const JPH_Body *b,
    const JPH_ContactManifold *manifold, JPH_ContactSettings *)
{
    auto &owner = *static_cast<World *>(context);
    if (std::this_thread::get_id() != owner.stepThread) ++workerContactCount;
    try { owner.AddContact(JPH_Body_GetUserData(const_cast<JPH_Body *>(a)), JPH_Body_GetUserData(const_cast<JPH_Body *>(b)),
        JPH_ContactManifold_GetSubShapeID1(manifold), JPH_ContactManifold_GetSubShapeID2(manifold), 1); }
    catch (...) { owner.contactFault = true; }
}
void OnContactRemoved(void *context, const JPH_SubShapeIDPair *pair)
{
    auto &owner = *static_cast<World *>(context);
    auto a = owner.identities.find(pair->Body1ID);
    auto b = owner.identities.find(pair->Body2ID);
    if (a == owner.identities.end() || b == owner.identities.end()) { owner.contactFault = true; return; }
    try { owner.AddContact(a->second, b->second, pair->subShapeID1, pair->subShapeID2, 2); }
    catch (...) { owner.contactFault = true; }
}
const JPH_ContactListener_Procs contactProcs { nullptr, OnContactAdded, nullptr, OnContactRemoved };

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
API const char *aj_version() { return "jolt-5.6.0;e77f175;joltc=2bbcf73;abi=3;float32;workers=0..16;deterministic;sse4;capacity=1024"; }
API uint64_t aj_worker_contact_count() { return workerContactCount.load(); }
API uint32_t aj_world_count() { std::lock_guard<std::mutex> lock(foundationMutex); return worldCount; }
API int aj_create(uint32_t workerThreads, void **result)
{
    return Guard([&] {
        Require(result != nullptr, "Null result");
        *result = nullptr;
        Require(workerThreads <= 16, "Invalid worker count");
        AcquireFoundation();
        try { *result = new World(workerThreads); }
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
        JPH_BodyInterface_RemoveBody(JPH_PhysicsSystem_GetBodyInterface(world.system.get()), body.GetIndexAndSequenceNumber());
        JPH_BodyInterface_DestroyBody(JPH_PhysicsSystem_GetBodyInterface(world.system.get()), body.GetIndexAndSequenceNumber());
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
        JPH_Vec3 value { impulse.x, impulse.y, impulse.z };
        JPH_BodyInterface_AddImpulse(JPH_PhysicsSystem_GetBodyInterface(world.system.get()), body.GetIndexAndSequenceNumber(), &value);
    });
}
API int aj_step(void *handle, float seconds)
{
    return Guard([&] {
        Require(std::isfinite(seconds) && seconds > 0 && seconds <= 1, "Invalid step duration");
        auto &world = Get(handle);
        world.stepThread = std::this_thread::get_id();
        Require(AJ_Update(world.system.get(), seconds, &world.temporary, world.jobs.get()) == JPH_PhysicsUpdateError_None, "Physics capacity exhausted");
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
        auto *interface = JPH_PhysicsSystem_GetBodyInterface(world.system.get());
        int index = 0;
        for (const auto &entry : world.bodies)
        {
            uint32_t id = entry.second.body.GetIndexAndSequenceNumber();
            JPH_RVec3 p; JPH_Quat q; JPH_Vec3 v, a;
            JPH_BodyInterface_GetPositionAndRotation(interface, id, &p, &q);
            JPH_BodyInterface_GetLinearAndAngularVelocity(interface, id, &v, &a);
            destination[index++] = { entry.first, { p.x, p.y, p.z }, { q.x, q.y, q.z, q.w },
                { v.x, v.y, v.z }, { a.x, a.y, a.z }, JPH_BodyInterface_IsActive(interface, id) ? 1 : 0,
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
        uint32_t magic = 0x334a4e41, count = uint32_t(world.bodies.size());
        recorder.Write(magic);
        recorder.Write(world.workerThreads);
        recorder.WriteBytes(world.generations.data(), world.generations.size());
        recorder.Write(count);
        for (const auto &entry : world.bodies)
        {
            recorder.WriteBytes(&entry.second.definition, sizeof(Definition));
            recorder.Write(entry.second.body.GetIndexAndSequenceNumber());
        }
        AJ_SaveState(world.system.get(), recorder);
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
            Reader recorder(bytes, size_t(length));
            uint32_t magic, count;
            recorder.Read(magic);
            Require(magic == 0x334a4e41, "Unsupported physics state");
            uint32_t workers;
            recorder.Read(workers);
            Require(workers <= 16, "Invalid worker count");
            auto world = std::make_unique<World>(workers);
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
            Require(AJ_RestoreState(world->system.get(), recorder) && recorder.IsEOF(), "Invalid Jolt state");
            *result = world.release();
        }
        catch (...) { ReleaseFoundation(); throw; }
    });
}
