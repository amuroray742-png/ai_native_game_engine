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
