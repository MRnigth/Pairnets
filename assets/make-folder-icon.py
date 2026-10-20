"""Renders assets/pairnets-folder.svg to assets/pairnets-folder.ico (PNG-in-ICO, 256/64/48/32/24/16) and
assets/pairnets-folder-256.png with headless Chrome
and ffmpeg. Windows (CHROME defaults to the usual install path). Commit the generated .ico."""
import os, struct, subprocess, tempfile, pathlib
here = pathlib.Path(__file__).resolve().parent
chrome = os.environ.get("CHROME", r"C:\Program Files\Google\Chrome\Application\chrome.exe")
svg = (here / "pairnets-folder.svg").read_text(encoding="utf-8")
sizes = [256, 64, 48, 32, 24, 16]
pngs = []
with tempfile.TemporaryDirectory() as tmp:
    # Chrome will not make a window this small: draw at 256 on a bigger page, 4x sharp, then scale down.
    page = pathlib.Path(tmp, "folder.html")
    page.write_text("<!doctype html><html><body style='margin:0;background:transparent'>"
                    + svg.replace('height="256"', 'height="256" style="display:block"', 1) + "</body></html>", encoding="utf-8")
    shot = pathlib.Path(tmp, "page.png")
    subprocess.run([chrome, "--headless", "--no-sandbox", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                    "--user-data-dir=" + os.path.join(tmp, "profile"), "--default-background-color=00000000", "--force-device-scale-factor=4",
                    "--window-size=600,600", f"--screenshot={shot}", page.as_uri()],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)
    for size in sizes:
        out = pathlib.Path(tmp, f"{size}.png")
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", str(shot), "-vf",
                        f"crop=1024:1024:0:0,scale={size}:{size}:flags=lanczos", "-pix_fmt", "rgba", str(out)], check=True, timeout=60)
        pngs.append(out.read_bytes())
        if size == 256:
            (here / "pairnets-folder-256.png").write_bytes(out.read_bytes())  # the Mac and Linux folder icon
offset = 6 + 16 * len(sizes)
header = struct.pack("<HHH", 0, 1, len(sizes))
entries = b""
for size, data in zip(sizes, pngs):
    entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
(here / "pairnets-folder.ico").write_bytes(header + entries + b"".join(pngs))
print("wrote pairnets-folder.ico", [len(p) for p in pngs])
