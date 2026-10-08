"""Package the built installer and reviewed Hebrew guide, without build logs or source files."""
import argparse
import hashlib
import json
import shutil
import zipfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--guide", required=True, type=Path)
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1]
    version = (project / "VERSION").read_text().strip()
    setup = project / "out" / f"Mahod_Civil_Delivery_Setup_{version}.exe"
    manifest = json.loads((project / "out/stage/payload/MANIFEST.json").read_text(encoding="utf-8-sig"))
    guide_hash = hashlib.sha256(args.guide.read_bytes()).hexdigest().upper()
    bundled_hash = manifest["files"]["Help/MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf"] if "Help/MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf" in manifest["files"] else manifest["files"]["Help\\MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf"]
    if guide_hash != bundled_hash.upper():
        raise ValueError("The distribution guide must match the guide embedded in the installer")
    if setup.read_bytes()[:2] != b"MZ" or args.guide.read_bytes()[:5] != b"%PDF-":
        raise ValueError("Missing or invalid installer/guide")
    destination = project / "out/distribution"
    destination.mkdir(parents=True, exist_ok=True)
    shipped = [destination / setup.name, destination / f"Mahod_Civil_Delivery_Guide_HE_{version}.pdf"]
    for source, target in zip([setup, args.guide], shipped):
        shutil.copyfile(source, target)
    archive = destination / f"Mahod_Civil_Delivery_{version}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for path in shipped:
            bundle.write(path, path.name)
    with zipfile.ZipFile(archive) as bundle:
        if bundle.namelist() != [path.name for path in shipped] or bundle.testzip():
            raise ValueError("Distribution archive verification failed")
        for path in shipped:
            if bundle.read(path.name) != path.read_bytes():
                raise ValueError("Distribution file changed")
    shipped.append(archive)
    sums = "".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n" for path in shipped)
    (destination / "SHA256SUMS.txt").write_text(sums, encoding="utf-8")
    print(f"Verified two-file ZIP: {archive}")


if __name__ == "__main__":
    main()
