"""Compatibility wrapper for the one canonical employee-package builder."""
from pathlib import Path
import subprocess

script = Path(__file__).with_name("build-nataly.ps1")
raise SystemExit(subprocess.call([
    "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)
]))
