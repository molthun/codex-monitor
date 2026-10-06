"""Static checks that work on both CI platforms."""
import ast
import json
import xml.etree.ElementTree as ET
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
for folder in (ROOT / "linux", ROOT / "windows/tools", ROOT / "tests"):
    for source in folder.rglob("*.py"):
        ast.parse(source.read_text(encoding="utf-8"), filename=str(source))
if sys.platform != "win32":
    for source in [ROOT / "install.sh", *(ROOT / "linux").rglob("*.sh")]:
        subprocess.run(["bash", "-n", str(source)], check=True)
    for source in (ROOT / "linux/extension").rglob("*.js"):
        subprocess.run(["node", "--check", "--input-type=module"], input=source.read_text(), text=True, check=True)
primary = ROOT / "windows/CodexBridge"
payload = ROOT / "windows/Deploy/Payload/CodexBridge"
names = {p.name for p in primary.iterdir() if p.is_file() and p.suffix in (".cs", ".csproj", ".ico", ".json")}
copies = {p.name for p in payload.iterdir() if p.is_file() and p.suffix in (".cs", ".csproj", ".ico", ".json")}
assert names == copies, f"Payload file list differs: {names ^ copies}"
for name in names:
    assert (primary / name).read_bytes() == (payload / name).read_bytes(), f"Payload differs: {name}"
print("Source syntax and payload mirror checks passed.")

version = (ROOT / "VERSION").read_text().strip()
project = ET.parse(primary / "CodexBridge.csproj")
assert project.findtext("PropertyGroup/Version") == version, "Bridge version differs from VERSION"
metadata = json.loads((ROOT / "linux/extension/codex-monitor@molthun.github.io/metadata.json").read_text())
assert metadata["version-name"] == version, "GNOME version differs from VERSION"
assert f'"Version={version}"' in (primary / "SkinBuilder.cs").read_text(), "Skin version differs from VERSION"
print(f"Project version checks passed: {version}")
