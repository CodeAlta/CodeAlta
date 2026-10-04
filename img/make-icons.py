"""Derives the desktop icons from img/CodeAlta.png (512x512): run from the repository root."""
import io, os, struct
from PIL import Image

source = Image.open("img/CodeAlta.png").convert("RGBA")
assert source.size == (512, 512), source.size

def png(size, **options):
    data = io.BytesIO()
    (source if size == 512 else source.resize((size, size), Image.LANCZOS)).save(data, format="PNG", optimize=True, **options)
    return data.getvalue()

# Linux: the tray icon and the launcher's icon.
open("img/alta.png", "wb").write(png(256))
# macOS status item: 18 points drawn from 36 pixels, which the resolution says.
open("img/alta-tray.png", "wb").write(png(36, dpi=(144, 144)))
# macOS application bundle: an icon family of PNG entries, each type naming its pixel size.
entries = b"".join(kind + struct.pack(">I", len(data) + 8) + data
                   for kind, data in ((b"ic11", png(32)), (b"ic12", png(64)), (b"ic07", png(128)), (b"ic08", png(256)), (b"ic09", png(512))))
open("img/alta.icns", "wb").write(b"icns" + struct.pack(">I", len(entries) + 8) + entries)
for name in ("img/alta.png", "img/alta-tray.png", "img/alta.icns"):
    print(name, os.path.getsize(name))
