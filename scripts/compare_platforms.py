"""Compare independent CI evidence, without requiring cross-platform reproducibility."""
import json
from pathlib import Path
import subprocess
import sys

linux, windows, output = [Path(value).resolve() for value in sys.argv[1:]]
output.mkdir(parents=True, exist_ok=False)
host = linux / "examples/AiNative.Examples.Runner/bin/Debug/net10.0/AiNative.Examples.Runner.dll"
results = []
for game, name in (("grid-game", "fixed"), ("physics-sandbox", "physics"),
                   ("physics-workers-0", "workers-0"), ("physics-workers-2", "workers-2"), ("physics-workers-4", "workers-4")):
    command = ["dotnet", str(host), "compare", "--expected", str(windows / f"TestResults/v2/{name}.replay.json"),
               "--actual", str(linux / f"TestResults/v2/{name}.replay.json")]
    result = subprocess.run(command, capture_output=True, timeout=60)
    (output / (game + ".stdout.json")).write_bytes(result.stdout)
    (output / (game + ".stderr.txt")).write_bytes(result.stderr)
    payload = json.loads(result.stdout)
    if result.returncode not in (0, 3) and not (result.returncode == 2 and payload["code"] == "ComparisonInputMismatch"):
        raise RuntimeError("Comparison tool failed")
    results.append({"game": game, "exitCode": result.returncode, "code": payload["code"]})
(output / "summary.json").write_text(json.dumps({"compatibilityClaim": "same build and environment only", "results": results}, indent=2) + "\n", encoding="utf-8")
