// Separate test executable: failure injection is absent from the shipped library.
#include <Jolt/Jolt.h>
#include <Jolt/Core/JobSystemThreadPool.h>
#include <atomic>
#include <iostream>
#include <system_error>

namespace
{
int failAt = -1;
std::atomic<int> workers { 0 }, started { 0 }, allocations { 0 };
JPH::AllocateFunction allocate;
JPH::ReallocateFunction reallocate;
JPH::FreeFunction freeMemory;
JPH::AlignedAllocateFunction alignedAllocate;
JPH::AlignedFreeFunction alignedFree;
}

void AJ_TestBeforeThreadStart(int index)
{
    if (index == failAt) throw std::system_error(std::make_error_code(std::errc::resource_unavailable_try_again));
}
void AJ_TestWorkerStarted() { ++workers; ++started; }
void AJ_TestWorkerFinished() { --workers; }
extern "C" int aj_create(uint32_t workers, void **result);
extern "C" int aj_step(void *world, float seconds);
extern "C" void aj_destroy(void *world);
extern "C" uint32_t aj_world_count();

int main()
{
    JPH::RegisterDefaultAllocator();
    allocate = JPH::Allocate; reallocate = JPH::Reallocate; freeMemory = JPH::Free;
    alignedAllocate = JPH::AlignedAllocate; alignedFree = JPH::AlignedFree;
    JPH::Allocate = [](size_t size) { auto p = allocate(size); if (p) ++allocations; return p; };
    JPH::Reallocate = [](void *p, size_t oldSize, size_t newSize) {
        auto result = reallocate(p, oldSize, newSize);
        if (!p && result) ++allocations;
        if (p && newSize == 0) --allocations;
        return result;
    };
    JPH::Free = [](void *p) { if (p) --allocations; freeMemory(p); };
    JPH::AlignedAllocate = [](size_t size, size_t alignment) { auto p = alignedAllocate(size, alignment); if (p) ++allocations; return p; };
    JPH::AlignedFree = [](void *p) { if (p) --allocations; alignedFree(p); };
    for (int index : { 0, 1, 3 })
    {
        failAt = index;
        started = 0;
        bool rejected = false;
        try { JPH::JobSystemThreadPool pool(2048, 8, 4); }
        catch (const std::system_error &) { rejected = true; }
        if (!rejected || workers != 0 || allocations != 0 || started != index) return 1;
    }
    failAt = -1;
    { JPH::JobSystemThreadPool pool(2048, 8, 2); }
    if (workers != 0 || allocations != 0) return 2;
    // Exercise the actual C ABI candidate path while an existing world stays alive.
    // JPH_Init installs the default allocator, so reapply the counting functions
    // after creating the old world and compare the candidate's allocation delta.
    auto countedAllocate = JPH::Allocate; auto countedReallocate = JPH::Reallocate;
    auto countedFree = JPH::Free; auto countedAlignedAllocate = JPH::AlignedAllocate;
    auto countedAlignedFree = JPH::AlignedFree;
    void *old = nullptr;
    if (aj_create(0, &old) != 0) return 3;
    JPH::Allocate = countedAllocate; JPH::Reallocate = countedReallocate;
    JPH::Free = countedFree; JPH::AlignedAllocate = countedAlignedAllocate; JPH::AlignedFree = countedAlignedFree;
    for (int index : { 0, 1, 3 })
    {
        failAt = index;
        void *candidate = nullptr;
        int status = aj_create(4, &candidate);
        if (status != -1 || candidate != nullptr || aj_world_count() != 1
            || workers != 0 || allocations != 0 || aj_step(old, 1.0f / 60) != 0)
        {
            std::cerr << "failure=" << index << " status=" << status << " worlds=" << aj_world_count()
                << " workers=" << workers << " allocations=" << allocations << '\n';
            return 4;
        }
    }
    failAt = -1;
    void *recovered = nullptr;
    if (aj_create(2, &recovered) != 0) return 5;
    aj_destroy(recovered);
    if (workers != 0 || allocations != 0 || aj_world_count() != 1) return 6;
    // The old world's allocation predates tracking; release it with the defaults.
    JPH::RegisterDefaultAllocator();
    aj_destroy(old);
    if (aj_world_count() != 0) return 7;
    std::cout << "{\"failedStartIndices\":[0,1,3],\"candidateFailures\":3,\"recoveryWorkers\":2,\"liveWorkers\":0,\"liveAllocations\":0,\"worlds\":0}\n";
}
