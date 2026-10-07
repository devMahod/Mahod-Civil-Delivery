"""Render the Mahod Civil Delivery 1.4.2 Hebrew user guide (guide.html) to PDF with Microsoft Edge
headless, the way nataly/build-guide.ps1 prints a guide, and every page to PNG for review.

Usage (the project venv; never a bare python):
  <venv>\\python.exe build_guide.py <source commit> --out <dir>

The 12-character commit is printed on the cover and on the last page as the build id
("1.4.2+<commit>"), the same id MCD_CHECK shows: pass the commit the installer is built from.
Writes <dir>/MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf (the file name build-setup.ps1 requires)
and <dir>/pages/page-NN.png. The logo is the plugin's own asset, inlined.
"""
import argparse
import base64
import hashlib
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import fitz  # PyMuPDF

VERSION = "1.4.2"
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
LOGO = REPO / "MahodAI.Civil3D.Plugin" / "assets" / "mahod_logo_white.png"
EDGE = [Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
        Path(r"C:\Program Files\Microsoft\Edge\Application\msedge.exe")]


# A Latin run continues over a space only into another Latin word or number, and over . , : only
# inside a token ("1.4.2", "Civil 3D 2026"), so "CL. XREF" stays two runs.
LATIN_RUN = re.compile(r"[A-Za-z0-9%](?:[A-Za-z0-9_+\-/\\%*]|[ ](?=[A-Za-z0-9%])|[.,:](?=[A-Za-z0-9]))*")


def isolate_latin(html: str) -> str:
    """Wrap every run of Latin text in the body in a direction-isolated span (<bdi>), so two Latin terms
    with only punctuation between them ("CL — SampleLine") can never merge into one left-to-right run
    whose order a Hebrew reader would see reversed. Text already inside a .k/.ltr span, <style>, <title>
    or an attribute is left alone; runs without a Latin letter (plain numbers) are left alone."""
    head, sep, body = html.partition("<body>")
    out, protect = [], []
    for part in re.split(r"(<[^>]+>)", body):
        if part.startswith("<"):
            m = re.match(r"<(/?)(\w+)", part)
            if m and m.group(2) == "span":
                if m.group(1):
                    if protect:
                        protect.pop()
                else:
                    protect.append('class="k"' in part or 'class="ltr"' in part)
            out.append(part)
            continue
        if any(protect):
            out.append(part)
            continue
        out.append(LATIN_RUN.sub(
            lambda r: f"<bdi>{r.group(0)}</bdi>" if re.search(r"[A-Za-z]", r.group(0)) else r.group(0), part))
    return head + sep + "".join(out)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("commit")
    ap.add_argument("--out", required=True, type=Path)
    a = ap.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}", a.commit):
        sys.exit("pass the full 40-character source commit")
    edge = next((e for e in EDGE if e.exists()), None)
    if edge is None:
        sys.exit("Microsoft Edge not found")

    html = (HERE / "guide.html").read_text(encoding="utf-8")
    html = isolate_latin(html.replace("@@BUILD@@", a.commit[:12]))
    html = html.replace('src="logo.png"', 'src="data:image/png;base64,' + base64.b64encode(LOGO.read_bytes()).decode() + '"')
    if "@@" in html:
        sys.exit("an unreplaced @@ token is left in the guide")

    a.out.mkdir(parents=True, exist_ok=True)
    pdf = a.out / f"MAHOD_CIVIL_DELIVERY_GUIDE_HE_{VERSION}.pdf"
    if pdf.exists():
        pdf.unlink()
    with tempfile.TemporaryDirectory(prefix="mcd-guide-") as work:
        page = Path(work) / "guide.html"
        page.write_text(html, encoding="utf-8")
        subprocess.run([str(edge), "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                        "--disable-background-networking", "--no-pdf-header-footer",
                        f"--user-data-dir={Path(work) / 'profile'}", f"--print-to-pdf={pdf}", page.as_uri()],
                       check=True, timeout=120, capture_output=True)
        for _ in range(120):
            if pdf.exists() and pdf.stat().st_size > 10000:
                break
            time.sleep(0.5)
        time.sleep(1)
    if not pdf.exists() or pdf.read_bytes()[:5] != b"%PDF-":
        sys.exit("Edge did not produce the guide PDF")

    pages = a.out / "pages"
    pages.mkdir(exist_ok=True)
    for old in pages.glob("page-*.png"):
        old.unlink()
    doc = fitz.open(pdf)
    text = "".join(p.get_text() for p in doc)
    for needle in (f"1.4.2+{a.commit[:12]}", "MCD_CIVIL_DELIVERY", "MAHOD_USAGE_OFF=1", f"Mahod_Civil_Delivery_Setup_{VERSION}.exe"):
        if needle not in text:
            sys.exit(f"the rendered guide is missing {needle!r}")
    for i, p in enumerate(doc):
        p.get_pixmap(dpi=100).save(pages / f"page-{i + 1:02d}.png")
    sha = hashlib.sha256(pdf.read_bytes()).hexdigest().upper()
    print(f"pages {doc.page_count}")
    print(f"guide {pdf} {pdf.stat().st_size} bytes SHA-256 {sha}")


if __name__ == "__main__":
    main()
