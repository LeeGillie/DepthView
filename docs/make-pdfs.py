"""PDF copies of the user guide (README.md) and the tuning guide (docs/TUNING-GUIDE.md).

The Markdown is the authoritative version; these PDFs are made from it for every release, for
people who want to read offline, print, or pass a guide along. Nothing is edited by hand.

    python docs/make-pdfs.py [--version 1.8.0] [--out dist] [--browser <chrome or edge>]

Needs the Python `markdown` package and a Chromium-based browser (Chrome, Edge, Chromium),
which prints the page headless. The version defaults to <Version> in DepthView.csproj.
Writes DepthView-<version>-User-Guide.pdf and DepthView-<version>-Tuning-Guide.pdf.
"""
import argparse
import datetime
import html
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import markdown

ROOT = Path(__file__).resolve().parent.parent
REPO = "https://github.com/LeeGillie/DepthView"
GUIDES = [
    ("README.md", "User-Guide", "User Guide"),
    ("docs/TUNING-GUIDE.md", "Tuning-Guide", "Tuning Guide"),
]

CSS = """
@page {
  size: Letter;
  margin: 16mm 15mm 18mm 15mm;
  @bottom-left { content: "%(footer)s"; font: 8pt system-ui, sans-serif; color: #777; }
  @bottom-right { content: counter(page) " / " counter(pages); font: 8pt system-ui, sans-serif; color: #777; }
}
html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
body { font: 10pt/1.45 "Segoe UI", system-ui, -apple-system, "Helvetica Neue", Arial, sans-serif;
       color: #1d1f23; margin: 0; }
.edition { font-size: 9pt; color: #555; border-left: 3px solid #2a78d6; padding: 2px 0 2px 10px;
           margin: 0 0 14px 0; }
h1, h2, h3, h4 { line-height: 1.25; break-after: avoid; page-break-after: avoid; color: #111; }
h1 { font-size: 20pt; margin: 0 0 8px 0; }
h2 { font-size: 14.5pt; margin: 22px 0 8px 0; padding-bottom: 3px; border-bottom: 1px solid #ddd; }
h3 { font-size: 12pt; margin: 18px 0 6px 0; }
h4 { font-size: 10.5pt; margin: 14px 0 4px 0; }
p, li { orphans: 3; widows: 3; }
a { color: #1f5fb0; text-decoration: none; }
img { max-width: 100%%; height: auto; break-inside: avoid; page-break-inside: avoid; }
p > img:only-child, p[align=center] { display: block; text-align: center; margin: 8px auto; }
code { font: 8.8pt Consolas, "Cascadia Mono", Menlo, "DejaVu Sans Mono", monospace;
       background: #f3f4f6; padding: 0 3px; border-radius: 3px; }
pre { background: #f5f6f8; border: 1px solid #e3e5e8; border-radius: 4px; padding: 7px 9px;
      white-space: pre-wrap; word-break: break-word; break-inside: avoid; }
pre code { background: none; padding: 0; font-size: 8.4pt; }
table { border-collapse: collapse; margin: 8px 0; font-size: 8.8pt; width: auto; }
th, td { border: 1px solid #d5d8dc; padding: 3px 6px; vertical-align: top; text-align: left; }
th { background: #f0f2f4; }
tr { break-inside: avoid; }
blockquote { margin: 8px 0; padding: 2px 12px; border-left: 3px solid #d0d4d9; color: #444; }
hr { border: 0; border-top: 1px solid #e0e0e0; margin: 16px 0; }
"""


def gh_slug(value, separator="-"):
    """Heading ids as GitHub makes them, so the guides' own tables of contents still work."""
    value = re.sub(r"<[^>]+>", "", value).strip().lower()
    value = re.sub(r"[^\w\- ]", "", value)
    return value.replace(" ", "-")


def csproj_version():
    text = (ROOT / "src/DepthView/DepthView.csproj").read_text(encoding="utf-8")
    return re.search(r"<Version>([^<]+)</Version>", text).group(1).strip()


def find_browser(given):
    if given:
        return given
    if os.environ.get("CHROME"):
        return os.environ["CHROME"]
    for name in ("google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "msedge", "chrome"):
        path = shutil.which(name)
        if path:
            return path
    for path in (
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    ):
        if os.path.exists(path):
            return path
    sys.exit("No Chrome, Edge or Chromium found: pass --browser or set CHROME.")


def to_html(md_path, title, version):
    text = md_path.read_text(encoding="utf-8")
    body = markdown.markdown(
        text,
        extensions=["extra", "sane_lists", "toc"],
        extension_configs={"toc": {"slugify": gh_slug}},
    )
    base = md_path.parent

    # Badges and other remote images: live status, not documentation, and a PDF should not
    # depend on the network to look right.
    body = re.sub(r'<a [^>]*>\s*<img [^>]*src="https?://[^"]*"[^>]*/?>\s*</a>\s*', "", body)

    def image(m):
        src = m.group(2)
        if re.match(r"[a-z]+:", src):
            return m.group(0)
        return m.group(1) + (base / src).resolve().as_uri() + m.group(3)
    body = re.sub(r'(<img [^>]*src=")([^"]+)(")', image, body)

    # Links between repository files point at GitHub: a PDF has no folder beside it.
    def link(m):
        href = html.unescape(m.group(2))
        if re.match(r"[a-z]+:|#", href):
            return m.group(0)
        path, _, frag = href.partition("#")
        rel = os.path.relpath((base / path).resolve(), ROOT).replace(os.sep, "/")
        url = f"{REPO}/blob/main/{rel}" + (f"#{frag}" if frag else "")
        return m.group(1) + html.escape(url) + m.group(3)
    body = re.sub(r'(<a [^>]*href=")([^"]+)(")', link, body)

    source = os.path.relpath(md_path, ROOT).replace(os.sep, "/")
    edition = (f'<p class="edition">DepthView {html.escape(version)} - {html.escape(title)}, '
               f'made {datetime.date.today():%d %B %Y} from <a href="{REPO}/blob/main/{source}">'
               f'{source}</a>, which is the authoritative version.</p>')
    # The edition line goes straight after the document's title.
    body = re.sub(r"(</h1>)", r"\1" + edition.replace("\\", "\\\\"), body, count=1)

    footer = f"DepthView {version} - {title}"
    css = CSS % {"footer": footer.replace('"', "'")}
    return (f'<!doctype html><html lang="en"><head><meta charset="utf-8">'
            f"<title>{html.escape(footer)}</title><style>{css}</style></head>"
            f"<body>{body}</body></html>")


def print_pdf(browser, html_path, pdf_path):
    profile = tempfile.mkdtemp(prefix="dv-pdf-profile-")
    try:
        args = [browser, "--headless=new", "--disable-gpu", "--no-first-run",
                "--no-default-browser-check", f"--user-data-dir={profile}",
                "--no-pdf-header-footer", "--run-all-compositor-stages-before-draw",
                "--virtual-time-budget=20000", f"--print-to-pdf={pdf_path}", html_path.as_uri()]
        if sys.platform.startswith("linux"):
            args.insert(1, "--no-sandbox")
        subprocess.run(args, check=True, timeout=180, capture_output=True)
    finally:
        shutil.rmtree(profile, ignore_errors=True)
    if not pdf_path.exists() or pdf_path.stat().st_size < 10000:
        sys.exit(f"{pdf_path.name}: the browser did not write a PDF.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", default=None)
    ap.add_argument("--out", default="dist")
    ap.add_argument("--browser", default=None)
    a = ap.parse_args()

    version = a.version or csproj_version()
    browser = find_browser(a.browser)
    out = Path(a.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="dv-pdf-"))
    try:
        for source, stem, title in GUIDES:
            md = ROOT / source
            page = work / f"{stem}.html"
            page.write_text(to_html(md, title, version), encoding="utf-8")
            pdf = out / f"DepthView-{version}-{stem}.pdf"
            print_pdf(browser, page, pdf)
            print(f"  {pdf.name:40} {pdf.stat().st_size // 1024:>7,} KB  from {source}")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
