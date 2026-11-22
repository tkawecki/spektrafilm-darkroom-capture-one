"""Load the pinned reference modules without starting the photo runtime."""
import importlib.util
from pathlib import Path
import sys
import types

ROOT = Path(__file__).resolve().parents[2]
for package in ("spektrafilm", "spektrafilm.profiles", "spektrafilm.utils"):
    module = types.ModuleType(package)
    module.__path__ = [str(ROOT / "upstream-spektrafilm/src" / package.replace(".", "/"))]
    sys.modules[package] = module


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


io = load("spektrafilm.profiles.io", "upstream-spektrafilm/src/spektrafilm/profiles/io.py")
morph = load("spektrafilm.utils.morph_curves", "upstream-spektrafilm/src/spektrafilm/utils/morph_curves.py")
