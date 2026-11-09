"""Local source packages for development and isolated verification checkouts."""
import json
from pathlib import Path
import shutil


def local_packages(project):
    packages = project / "Packages"
    dependencies = json.loads((packages / "manifest.json").read_bytes())["dependencies"]
    result = {}
    for name, reference in dependencies.items():
        if not name.startswith("com.ainative."):
            continue
        expected = "file:../../../../packages/" + name
        if reference != expected:
            raise ValueError(f"Expected repository package reference {name}: {expected}")
        if (packages / name).exists():
            raise ValueError(f"Embedded package shadows repository source: {packages / name}. "
                             "Move it outside Packages after preserving any local edits.")
        source = (packages / reference.removeprefix("file:")).resolve()
        if json.loads((source / "package.json").read_bytes())["name"] != name:
            raise ValueError(f"Package identity differs: {source}")
        result[name] = source
    return result


def copy_verification_project(project, workspace):
    # Preserve relative file: references without pointing a test Editor at the live source tree.
    sources = local_packages(project)
    target = workspace / "examples/unity" / project.name
    for directory in ("Assets", "Packages", "ProjectSettings"):
        shutil.copytree(project / directory, target / directory)
    for name, source in sources.items():
        shutil.copytree(source, workspace / "packages" / name)
    local_packages(target)
    return target
