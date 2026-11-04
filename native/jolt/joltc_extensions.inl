// Included after pinned joltc.cpp. These are the capabilities absent from its C API.
#include "joltc_extensions.h"
#include <Jolt/Core/JobSystemSingleThreaded.h>
#include <memory>

void AJ_Shutdown()
{
    // joltc leaves registry capacity allocated after its last world is removed.
    // Release it before its allocator/foundation is shut down.
    {
        decltype(s_PhysicsSystems) empty;
        s_PhysicsSystems.swap(empty);
    }
    JPH_Shutdown();
}

JPH_PhysicsSystem *AJ_CreatePhysicsSystem(const JPH_PhysicsSystemSettings &settings)
{
    // The pinned C constructor uses raw new across Init and registry insertion.
    // Keep the candidate private and leave filter ownership with the caller on failure.
    auto system = std::make_unique<JPH_PhysicsSystem>();
    auto physics = std::make_unique<JPH::PhysicsSystem>();
    auto *layers = reinterpret_cast<JPH::BroadPhaseLayerInterface *>(settings.broadPhaseLayerInterface);
    auto *pairs = reinterpret_cast<JPH::ObjectLayerPairFilter *>(settings.objectLayerPairFilter);
    auto *broad = reinterpret_cast<JPH::ObjectVsBroadPhaseLayerFilter *>(settings.objectVsBroadPhaseLayerFilter);
    physics->Init(settings.maxBodies, settings.numBodyMutexes, settings.maxBodyPairs,
        settings.maxContactConstraints, *layers, *broad, *pairs);
    s_PhysicsSystems[physics.get()] = system.get();
    system->broadPhaseLayerInterface = layers;
    system->objectLayerPairFilter = pairs;
    system->objectVsBroadPhaseLayerFilter = broad;
    system->physicsSystem = physics.release();
    return system.release();
}

JPH_JobSystem *AJ_CreateSingleThreadedJobs()
{
    return reinterpret_cast<JPH_JobSystem *>(new JPH::JobSystemSingleThreaded(2048));
}

void AJ_EnableDeterminism(JPH_PhysicsSystem *system)
{
    auto settings = system->physicsSystem->GetPhysicsSettings();
    settings.mDeterministicSimulation = true;
    system->physicsSystem->SetPhysicsSettings(settings);
}

JPH_PhysicsUpdateError AJ_Update(JPH_PhysicsSystem *system, float seconds,
    JPH::TempAllocator *allocator, JPH_JobSystem *jobs)
{
    // joltc's normal Update uses a process-wide allocator. Each AiNative world
    // owns an allocator so separate worlds can update concurrently.
    return static_cast<JPH_PhysicsUpdateError>(system->physicsSystem->Update(
        seconds, 1, allocator, reinterpret_cast<JPH::JobSystem *>(jobs)));
}

void AJ_SaveState(JPH_PhysicsSystem *system, JPH::StateRecorder &recorder)
{
    system->physicsSystem->SaveState(recorder);
}

bool AJ_RestoreState(JPH_PhysicsSystem *system, JPH::StateRecorder &recorder)
{
    return system->physicsSystem->RestoreState(recorder);
}
