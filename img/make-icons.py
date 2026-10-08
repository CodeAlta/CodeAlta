"""Derives the desktop icons from img/CodeAlta.png (512x512): run from the repository root."""
import io, os, struct
from PIL import Image, ImageFilter

source = Image.open("img/CodeAlta.png").convert("RGBA")
assert source.size == (512, 512), source.size

def png(size, picture=source, **options):
    data = io.BytesIO()
    (picture if size == 512 else picture.resize((size, size), Image.LANCZOS)).save(data, format="PNG", optimize=True, **options)
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

def square():
    """The tile without its rounded corners: a pixel outside the tile takes the color the tile has on the way to the center.
    macOS 26 gives every icon its own rounded shape, at the size of its neighbours, but only to a picture that is
    opaque up to its edges: any other is shrunk onto a grey tile."""
    filled = source.copy()
    art, pixels = source.load(), filled.load()
    for y in range(512):
        for x in range(512):
            if art[x, y][3] == 255: continue
            dx, dy = 255.5 - x, 255.5 - y
            step = max(abs(dx), abs(dy))
            dx, dy, at = dx / step, dy / step, 0
            while art[round(x + dx * at), round(y + dy * at)][3] != 255: at += 1
            pixels[x, y] = art[round(x + dx * at), round(y + dy * at)]
    return Image.alpha_composite(filled, source)

def family(picture):
    """An icon family of PNG entries, each type naming its pixel size."""
    entries = b"".join(kind + struct.pack(">I", len(data) + 8) + data
                       for kind, data in ((b"ic11", picture(32)), (b"ic12", picture(64)), (b"ic07", picture(128)), (b"ic08", picture(256)), (b"ic09", picture(512))))
    return b"icns" + struct.pack(">I", len(entries) + 8) + entries

# Linux: the tray icon and the launcher's icon.
open("img/alta.png", "wb").write(png(256))
# macOS status item: 18 points drawn from 36 pixels, which the resolution says.
open("img/alta-tray.png", "wb").write(png(36, dpi=(144, 144)))
# macOS application bundle up to macOS 15: the Dock draws the picture as it is.
open("img/alta.icns", "wb").write(family(mac))
# macOS Dock of an application started without its bundle: the picture it sets itself is drawn as it is.
open("img/alta-dock.png", "wb").write(mac(512))
# macOS application bundle from macOS 26: the system cuts the tile to its own shape.
tile = square().convert("RGB")  # no alpha channel: opaque by construction
open("img/alta-full.icns", "wb").write(family(lambda size: png(size, tile)))
for name in ("img/alta.png", "img/alta-tray.png", "img/alta.icns", "img/alta-dock.png", "img/alta-full.icns"):
    print(name, os.path.getsize(name))
