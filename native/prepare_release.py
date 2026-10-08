"""Assemble redistributable runtime notices; never include a user's database/config."""
import argparse
import importlib.metadata
import json
from pathlib import Path
import shutil
import sys

parser = argparse.ArgumentParser()
parser.add_argument("--publish", required=True, type=Path)
parser.add_argument("--nuget", required=True, type=Path)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[1]
dest = args.publish.resolve()
dest.mkdir(parents=True, exist_ok=True)
for name in ("LICENSE", "app.ico"):
    shutil.copy2(repo / name, dest / name)
shutil.copy2(repo / "native" / "THIRD_PARTY_NOTICES.md", dest / "THIRD_PARTY_NOTICES.md")
shutil.copy2(repo / "native" / "使用说明.md", dest / "使用说明.md")
licenses = dest / "licenses"
licenses.mkdir(exist_ok=True)
shutil.copytree(repo / "native" / "licenses", licenses, dirs_exist_ok=True)
shutil.copy2(repo / "THIRD_PARTY_NOTICES.md", licenses / "legacy-source-attributions.md")
python_root = Path(sys.base_prefix)
shutil.copy2(python_root / "LICENSE.txt", licenses / "Python-3.11-LICENSE.txt")
packages = ["pynput", "six", "pypinyin", "PyYAML", "cffi", "pycparser", "pyinstaller", "mido", "packaging"]
versions = {}
for name in packages:
    dist = importlib.metadata.distribution(name)
    versions[name] = dist.version
    for file in dist.files or []:
        if any(word in Path(file).name.casefold() for word in ("license", "copying", "notice")):
            source = Path(dist.locate_file(file))
            if source.is_file():
                folder = licenses / f"{name}-{dist.version}"
                folder.mkdir(exist_ok=True)
                shutil.copy2(source, folder / source.name)
    # The LGPL dependency is distributed as editable source alongside its license.
    if name == "pynput":
        source = Path(dist.locate_file("pynput"))
        shutil.copytree(source, licenses / "pynput-1.8.2-source", dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
assets = json.loads((repo / "native" / "WinUI" / "obj" / "project.assets.json").read_text(encoding="utf-8"))
nuget_packages = []
for identity, details in assets["libraries"].items():
    if details.get("type") != "package":
        continue
    folder = args.nuget / details["path"]
    for file in folder.rglob("*"):
        if file.is_file() and any(word in file.name.casefold() for word in ("license", "notice", "copying")):
            relative = file.relative_to(folder)
            target = licenses / identity.replace("/", "-") / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(file, target)
    nuget_packages.append(identity)
runtime = args.nuget / "microsoft.netcore.app.runtime.win-x64" / "8.0.25"
for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
    shutil.copy2(runtime / name, licenses / ("dotnet-8.0.25-" + name))
metadata = {"application": "AutoMusicPlayerLite", "version": "2.2.4", "architecture": "x64",
            "python": sys.version.split()[0], "dotnet": "8.0.25", "dependencies": versions,
            "nuget_packages": nuget_packages, "user_data_included": False, "edition": "public", "bundled_scores": 0}
(dest / "build-info.json").write_text(json.dumps(metadata, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps({"publish": str(dest), "python_dependencies": versions, "licenses": len(list(licenses.rglob('*')))}, ensure_ascii=False))
