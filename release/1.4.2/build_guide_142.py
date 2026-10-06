"""Mahod Civil Delivery 1.4.2 guide: the 1.4.0 guide PDF + one page, "מה נשלח ל-Mahod AI".

Why not nataly/build-guide.ps1: the 1.4.0 guide was not built from this repository. The developer
package ships neither its PDF nor the screenshots nataly/guide/GUIDE_HE.html inlines (nataly/guide/img
is absent), and that template still describes the 1.2.x MahodAI-fork install (MHD_* commands,
MahodAI.bundle), while release/release.json pins the 1.2.91 lane. So 1.4.2 keeps the engineers'
1.4.0 guide byte-for-byte and appends the new section, rendered from the SAME source the template
carries (nataly/guide/GUIDE_HE.html: its <style> and the section between
"<h2>מה נשלח ל-Mahod AI</h2>" and "<h2>הסרה</h2>"), printed by Microsoft Edge exactly as
build-guide.ps1 prints the guide.

Usage (project venv, never bare python):
  <venv>\\python.exe build_guide_142.py --base <MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.0.pdf> --out <dir>
      [--expected-base-sha256 <hex>]   (default: the 1.4.0 guide named in README_DEVELOPERS_HE.md)

Writes <dir>/MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf, <dir>/addendum.pdf and a PNG of the new page
(<dir>/qa/) to look at before release. Prints the SHA-256 that build-setup.ps1 must be given.
"""
import argparse
import hashlib
import re
import subprocess
import sys
import time
from pathlib import Path

import fitz  # PyMuPDF
from pypdf import PdfReader, PdfWriter

VERSION = "1.4.2"
BASE_SHA256 = "8816E9629A6DAEB01AB71FE114D5BEB24E0AF5B3D5B73F8F8EDF5EB91D83F1D4"
REPO = Path(__file__).resolve().parents[2]
TEMPLATE = REPO / "nataly" / "guide" / "GUIDE_HE.html"
SECTION_START = "<h2>מה נשלח ל-Mahod AI</h2>"
SECTION_END = "<h2>הסרה</h2>"
EDGE = [Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
        Path(r"C:\Program Files\Microsoft\Edge\Application\msedge.exe")]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def addendum_html() -> str:
    template = TEMPLATE.read_text(encoding="utf-8")
    style = re.search(r"<style>.*?</style>", template, re.S)
    start, end = template.find(SECTION_START), template.find(SECTION_END)
    if not style or start < 0 or end < start:
        sys.exit("GUIDE_HE.html: <style> or the usage section is missing")
    section = template[start:end].strip()
    return f"""<!doctype html>
<html lang="he" dir="rtl">
<head><meta charset="utf-8"><title>Mahod Civil Delivery {VERSION}</title>
{style.group(0)}
</head>
<body>
<section class="cont">
<h1>תוספת לגרסה {VERSION}</h1>
<p>גרסה {VERSION} מוסיפה דיווח שימוש ל-Mahod Impact. כל שאר הפרקים במדריך זה תקפים כפי שהם.
קובץ ההתקנה: <span class="k">Mahod_Civil_Delivery_Setup_{VERSION}.exe</span>.</p>
{section}
<div class="footer">Mahod Civil Delivery {VERSION} · מהוד הנדסה · תוספת למדריך המשתמש</div>
</section>
</body>
</html>
"""


def print_pdf(html_path: Path, pdf_path: Path, profile: Path) -> None:
    edge = next((e for e in EDGE if e.exists()), None)
    if edge is None:
        sys.exit("Microsoft Edge not found; the guide PDF is printed by Edge (build-guide.ps1)")
    if pdf_path.exists():
        pdf_path.unlink()
    subprocess.run([str(edge), "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                    f"--user-data-dir={profile}", f"--print-to-pdf={pdf_path}", html_path.as_uri()],
                   check=True, timeout=120)
    for _ in range(120):
        if pdf_path.exists() and pdf_path.stat().st_size > 0:
            break
        time.sleep(0.5)
    time.sleep(1)
    if not pdf_path.exists() or pdf_path.read_bytes()[:5] != b"%PDF-":
        sys.exit("Edge did not produce the addendum PDF")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", required=True, type=Path)
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--expected-base-sha256", default=BASE_SHA256)
    a = ap.parse_args()

    base_sha = sha256(a.base)
    if base_sha != a.expected_base_sha256.upper():
        sys.exit(f"base guide SHA-256 {base_sha} is not the expected {a.expected_base_sha256.upper()}")
    a.out.mkdir(parents=True, exist_ok=True)
    (a.out / "qa").mkdir(exist_ok=True)

    html = a.out / "addendum.html"
    html.write_text(addendum_html(), encoding="utf-8-sig")
    addendum = a.out / "addendum.pdf"
    print_pdf(html, addendum, a.out / "qa" / "edge-profile")

    writer = PdfWriter()
    base = PdfReader(str(a.base))
    for page in base.pages:
        writer.add_page(page)
    extra = PdfReader(str(addendum))
    for page in extra.pages:
        writer.add_page(page)
    writer.add_metadata({"/Title": f"Mahod Civil Delivery {VERSION} — מדריך למשתמש"})
    guide = a.out / f"MAHOD_CIVIL_DELIVERY_GUIDE_HE_{VERSION}.pdf"
    with guide.open("wb") as f:
        writer.write(f)

    doc = fitz.open(guide)
    if doc.page_count != len(base.pages) + len(extra.pages):
        sys.exit("merged page count is wrong")
    for i in range(len(base.pages), doc.page_count):
        doc[i].get_pixmap(dpi=110).save(a.out / "qa" / f"guide_{VERSION}_p{i + 1}.png")
    text = "".join(doc[i].get_text() for i in range(len(base.pages), doc.page_count))
    for needle in ("MAHOD_USAGE_OFF=1", "cad-usage", VERSION):
        if needle not in text:
            sys.exit(f"addendum text is missing {needle!r}")
    print(f"base pages {len(base.pages)} + addendum {len(extra.pages)} = {doc.page_count}")
    print(f"guide {guide} {guide.stat().st_size} bytes SHA-256 {sha256(guide)}")


if __name__ == "__main__":
    main()
