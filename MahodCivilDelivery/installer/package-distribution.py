"""Package the built installer and reviewed Hebrew guide, without build logs or source files."""
import argparse
import hashlib
import json
import zipfile
from pathlib import Path


def validate_receipt(receipt, version, setup_hash, manifest_hash, guide_hash):
    if (receipt.get("schema") != "mahod-setup-payload-verification/v1"
            or receipt.get("synthetic") is not False
            or receipt.get("version") != version
            or receipt.get("command") != "--self-test-verify-payload-dir"
            or receipt.get("exitCode") != 0):
        raise ValueError("A successful real installer payload verification receipt is required")
    for field, expected in (("setupSha256", setup_hash),
                            ("payloadManifestSha256", manifest_hash),
                            ("guideSha256", guide_hash)):
        if str(receipt.get(field, "")).upper() != expected.upper():
            raise ValueError(f"Installer verification receipt does not match {field}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--guide", required=True, type=Path)
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1]
    version = (project / "VERSION").read_text().strip()
    setup = project / "out" / f"Mahod_Civil_Delivery_Setup_{version}.exe"
    manifest_bytes = (project / "out/stage/payload/MANIFEST.json").read_bytes()
    manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
    setup_bytes = setup.read_bytes()
    guide_bytes = args.guide.read_bytes()
    guide_hash = hashlib.sha256(guide_bytes).hexdigest().upper()
    receipt = json.loads((project / "out" / f"SETUP_PAYLOAD_VERIFICATION_{version}.json").read_text(encoding="utf-8-sig"))
    validate_receipt(receipt, version, hashlib.sha256(setup_bytes).hexdigest(),
                     hashlib.sha256(manifest_bytes).hexdigest(), guide_hash)
    bundled_hash = manifest["files"]["Help/MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf"] if "Help/MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf" in manifest["files"] else manifest["files"]["Help\\MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf"]
    if guide_hash != bundled_hash.upper():
        raise ValueError("The distribution guide must match the guide embedded in the installer")
    if setup_bytes[:2] != b"MZ" or guide_bytes[:5] != b"%PDF-":
        raise ValueError("Missing or invalid installer/guide")
    destination = project / "out/distribution"
    destination.mkdir(parents=True, exist_ok=True)
    shipped = [destination / setup.name, destination / f"Mahod_Civil_Delivery_Guide_HE_{version}.pdf"]
    for content, target in zip([setup_bytes, guide_bytes], shipped):
        target.write_bytes(content)
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
