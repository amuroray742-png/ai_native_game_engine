"""Prepare the three independent examples for the pinned URP editor migration.

Run ExampleUrpSetup.Apply via Unity CLI in each project after this script.
Generated Editor helpers intentionally share this single source of truth.
"""
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]

def prepare():
    for name in ("GridGame", "PhysicsSandbox", "AssetsDemo"):
        project = ROOT / "examples/unity" / name
        manifest = project / "Packages/manifest.json"
        config = json.loads(manifest.read_bytes())
        config["dependencies"]["com.unity.render-pipelines.universal"] = "17.3.0"
        manifest.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
        editor = project / "Assets/Rendering/Editor"
        editor.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / "scripts/unity/ExampleUrpSetup.cs", editor / "ExampleUrpSetup.cs")
        (editor / "Example.Rendering.Editor.asmdef").write_text(json.dumps({
            "name": "Example.Rendering.Editor",
            "references": ["Unity.RenderPipelines.Universal.Runtime", "Unity.RenderPipelines.Core.Runtime"],
            "includePlatforms": ["Editor"]
        }, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__":
    prepare()
