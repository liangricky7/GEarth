"""Generate full citizen decision menus from a saved world without inference."""

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from agents.worker import DecisionJob, build_prompt
from ops.spark_gemma_experiment import GEMMA, validate_samples


PROGRAM = """using System.Text.Json;
using Faultline.Core;
var world = JsonSerializer.Deserialize<World>(File.ReadAllText(args[0]), Wire.Json)!;
var groups = world.Agents.Values.Where(agent => agent.Role == "citizen"
    && SocietyLabor.DecidesForItself(agent))
    .OrderBy(agent => agent.CountryId).ThenBy(agent => agent.Occupation).ThenBy(agent => agent.Id)
    .GroupBy(agent => (agent.CountryId, agent.Occupation)).Select(group => group.ToArray()).ToArray();
var agents = Enumerable.Range(0, groups.Max(group => group.Length))
    .SelectMany(index => groups.Where(group => index < group.Length).Select(group => group[index]))
    .Take(int.Parse(args[2]));
var jobs = agents.Select(agent => SocietyEngine.Observe(world, agent.Id, DateTimeOffset.UtcNow)).ToArray();
if (jobs.Any(job => job.Candidates.Length == 0)) throw new Exception("Engine returned an empty menu");
File.WriteAllText(args[1], JsonSerializer.Serialize(jobs, Wire.Json));
Console.WriteLine($"Generated {jobs.Length} decision jobs at day {world.Tick}");
"""


def export(world_file, output, count=32):
    world_file = Path(world_file).resolve()
    with tempfile.TemporaryDirectory(prefix="faultline-prompts-") as directory:
        project = Path(directory)
        core = escape(str(ROOT / "backend/Faultline.Core/Faultline.Core.csproj"), {'"': "&quot;"})
        (project / "Exporter.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<ImplicitUsings>enable</ImplicitUsings></PropertyGroup>'
            f'<ItemGroup><ProjectReference Include="{core}" /></ItemGroup></Project>'
        )
        (project / "Program.cs").write_text(PROGRAM)
        jobs_file = project / "jobs.json"
        subprocess.run(["dotnet", "run", "--project", str(project / "Exporter.csproj"),
                        "--verbosity", "quiet", "--", str(world_file), str(jobs_file), str(count)], check=True)
        samples = []
        for raw_job in json.loads(jobs_file.read_text()):
            job = DecisionJob.model_validate(raw_job)
            samples.append({"payload": build_prompt(job, GEMMA), "candidate_count": len(job.candidates)})
    validate_samples(samples)
    if len(samples) != count:
        raise RuntimeError(f"Expected {count} citizen prompts, received {len(samples)}")
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_text(json.dumps(samples, indent=2) + "\n")
    temporary.chmod(0o600)
    temporary.replace(output)
    print(f"Validated {len(samples)} prompts; menus contain {min(item['candidate_count'] for item in samples)}"
          f"-{max(item['candidate_count'] for item in samples)} actions")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("world_file", type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / ".data/gemma-benchmark-prompts.json")
    parser.add_argument("--count", type=int, default=32)
    options = parser.parse_args()
    if not 1 <= options.count <= 256:
        parser.error("Use 1-256 prompts")
    export(options.world_file, options.output, options.count)
