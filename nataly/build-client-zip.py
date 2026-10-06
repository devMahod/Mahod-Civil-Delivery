"""Compatibility wrapper for the one canonical employee-package builder.

The retired no-installer builder depended on an ephemeral scratchpad under %TEMP% and
produced a different package shape. There is now one supported artifact only:
nataly/out/Mahod_Civil_Delivery_<release>.zip (setup EXE + PDF).
"""
from pathlib import Path
import subprocess
import sys

script = Path(__file__).with_name("build-nataly.ps1")
print("build-client-zip.py is retired; delegating to the canonical EXE+PDF builder.")
raise SystemExit(subprocess.call([
    "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)
]))
