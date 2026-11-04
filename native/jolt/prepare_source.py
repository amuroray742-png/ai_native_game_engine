"""Apply the bounded, audited restore exception-safety patch to a build copy only."""
import hashlib
from pathlib import Path


def prepare(source: Path, destination: Path):
    original = (source / "Jolt/Physics/Body/BodyManager.cpp").read_text(encoding="utf-8")
    start = original.index("bool BodyManager::RestoreState(StateRecorder &inStream)")
    end = original.index("\nvoid BodyManager::", start)
    method = original[start:end]
    if method.count("LockAllBodies();") != 1 or method.count("UnlockAllBodies();") != 4:
        raise RuntimeError("Pinned Jolt restore implementation changed; review patch before building")
    method = method.replace("UnlockAllBodies();", "")
    method = method.replace("LockAllBodies();", """LockAllBodies();
		// AiNative bridge readers throw on malformed/truncated data. Unlock on every exit.
		auto unlock = [](const BodyManager *owner) { owner->UnlockAllBodies(); };
		std::unique_ptr<const BodyManager, decltype(unlock)> body_lock(this, unlock);""")
    patched = "#include <memory>\n" + original[:start] + method + original[end:]
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(patched, encoding="utf-8")
    return hashlib.sha256(destination.read_bytes()).hexdigest()


def prepare_thread_pool(source: Path, destination: Path):
    original = (source / "Jolt/Core/JobSystemThreadPool.cpp").read_text(encoding="utf-8")
    old = "\tmThreads.reserve(inNumThreads);\n\tfor (int i = 0; i < inNumThreads; ++i)\n\t\tmThreads.emplace_back([this, i] { ThreadMain(i); });"
    if original.count(old) != 1:
        raise RuntimeError("Pinned Jolt thread startup changed; review patch before building")
    new = """	try
	{
		mThreads.reserve(inNumThreads);
		for (int i = 0; i < inNumThreads; ++i)
		{
#ifdef AINATIVE_THREAD_START_TEST
			::AJ_TestBeforeThreadStart(i);
#endif
			mThreads.emplace_back([this, i] {
#ifdef AINATIVE_THREAD_START_TEST
				::AJ_TestWorkerStarted();
#endif
				ThreadMain(i);
#ifdef AINATIVE_THREAD_START_TEST
				::AJ_TestWorkerFinished();
#endif
			});
		}
	}
	catch (...)
	{
		// A constructor cannot leave joinable workers for member destruction.
		StopThreads();
		// StopThreads returns early if even the first worker could not start.
		if (mHeads != nullptr) { Free(mHeads); mHeads = nullptr; }
		throw;
	}"""
    declarations = """#ifdef AINATIVE_THREAD_START_TEST
extern void AJ_TestBeforeThreadStart(int);
extern void AJ_TestWorkerStarted();
extern void AJ_TestWorkerFinished();
#endif
"""
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(declarations + original.replace(old, new), encoding="utf-8", newline="\n")
    return hashlib.sha256(destination.read_bytes()).hexdigest()
