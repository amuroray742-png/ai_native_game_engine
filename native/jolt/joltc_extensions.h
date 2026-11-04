#pragma once
#include <joltc.h>
#include <Jolt/Jolt.h>
#include <Jolt/Physics/StateRecorder.h>

// Internal C++ extensions compiled into the same joltc translation unit.
// No opaque joltc struct layout is duplicated outside that unit.
JPH_JobSystem *AJ_CreateSingleThreadedJobs();
JPH_PhysicsSystem *AJ_CreatePhysicsSystem(const JPH_PhysicsSystemSettings &settings);
void AJ_Shutdown();
void AJ_SaveState(JPH_PhysicsSystem *system, JPH::StateRecorder &recorder);
bool AJ_RestoreState(JPH_PhysicsSystem *system, JPH::StateRecorder &recorder);
