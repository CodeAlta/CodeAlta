"""Derives the desktop icons from img/CodeAlta.png (512x512): run from the repository root."""
import io, os, struct
from PIL import Image, ImageFilter

source = Image.open("img/CodeAlta.png").convert("RGBA")
assert source.size == (512, 512), source.size

def png(size, **options):
    data = io.BytesIO()
    (source if size == 512 else source.resize((size, size), Image.LANCZOS)).save(data, format="PNG", optimize=True, **options)
    return data.getvalue()

def mac(size):
    """The tile on the macOS icon grid: 824 of 1024 points, centered, over a soft shadow.
    The Dock draws an icon as large as its canvas, so a tile that fills it looks bigger than its neighbours."""
    tile = round(size * 824 / 1024)
    art = source.resize((tile, tile), Image.LANCZOS)
    origin = (size - tile) // 2
    shade = Image.new("RGBA", (tile, tile))
    shade.putalpha(art.getchannel("A").point(lambda alpha: round(alpha * 0.3)))
    shadow = Image.new("RGBA", (size, size))
    shadow.paste(shade, (origin, origin + round(size * 10 / 1024)))
    shadow = shadow.filter(ImageFilter.GaussianBlur(size * 10 / 1024))
    front = Image.new("RGBA", (size, size))
    front.paste(art, (origin, origin))
    data = io.BytesIO()
    Image.alpha_composite(shadow, front).save(data, format="PNG", optimize=True)
    return data.getvalue()

# Linux: the tray icon and the launcher's icon.
open("img/alta.png", "wb").write(png(256))
# macOS status item: 18 points drawn from 36 pixels, which the resolution says.
open("img/alta-tray.png", "wb").write(png(36, dpi=(144, 144)))
# macOS application bundle and Dock: an icon family of PNG entries, each type naming its pixel size.
entries = b"".join(kind + struct.pack(">I", len(data) + 8) + data
                   for kind, data in ((b"ic11", mac(32)), (b"ic12", mac(64)), (b"ic07", mac(128)), (b"ic08", mac(256)), (b"ic09", mac(512))))
open("img/alta.icns", "wb").write(b"icns" + struct.pack(">I", len(entries) + 8) + entries)
for name in ("img/alta.png", "img/alta-tray.png", "img/alta.icns"):
    print(name, os.path.getsize(name))
