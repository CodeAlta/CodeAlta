"""Draws the banner of the website and the repository (site/img/alta-banner.png, 1280x640): run from the repository root.
The picture of the window is a capture of the site (site/img/alta-desktop-split-three.webp), only cropped and scaled."""
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

S = 2  # drawn at twice the size, then reduced: smooth edges and text
W, H = 1280 * S, 640 * S
FONTS = "C:/Windows/Fonts/"
def font(name, size): return ImageFont.truetype(FONTS + name, size * S)
CYAN, INDIGO, PURPLE = (0, 209, 255), (79, 70, 229), (168, 85, 247)

def gradient(size, stops):
    """A horizontal gradient through the given colors."""
    width, height = size
    strip = Image.new("RGB", (width, 1))
    for x in range(width):
        at = x / max(1, width - 1) * (len(stops) - 1)
        i = min(int(at), len(stops) - 2)
        t = at - i
        strip.putpixel((x, 0), tuple(round(a + (b - a) * t) for a, b in zip(stops[i], stops[i + 1])))
    return strip.resize((width, height))

def glow(picture, center, radius, color, alpha):
    layer = Image.new("RGBA", picture.size)
    x, y = center
    ImageDraw.Draw(layer).ellipse((x - radius, y - radius, x + radius, y + radius), fill=color + (alpha,))
    return Image.alpha_composite(picture, layer.filter(ImageFilter.GaussianBlur(radius * .6)))

def text(picture, xy, value, face, fill):
    """Draws text in a color, or through a gradient when fill is a list of colors."""
    if isinstance(fill, tuple):
        ImageDraw.Draw(picture).text(xy, value, font=face, fill=fill)
        return
    box = [round(v) for v in ImageDraw.Draw(picture).textbbox(xy, value, font=face)]
    mask = Image.new("L", picture.size)
    ImageDraw.Draw(mask).text(xy, value, font=face, fill=255)
    paint = Image.new("RGBA", picture.size)
    paint.paste(gradient((box[2] - box[0], box[3] - box[1]), fill).convert("RGBA"), box[:2])
    picture.paste(paint, (0, 0), mask)

# Background: the deep blue of the site, lit in cyan and violet.
banner = gradient((W, H), [(8, 28, 44), (9, 18, 32), (11, 14, 28)]).convert("RGBA")
banner = glow(banner, (160 * S, 40 * S), 360 * S, CYAN, 60)
banner = glow(banner, (880 * S, 620 * S), 420 * S, PURPLE, 46)
banner = glow(banner, (1180 * S, 80 * S), 300 * S, INDIGO, 50)

# The window: three sessions side by side, cut by the right and bottom edges of the banner.
left, top, scale = 548 * S, 66 * S, .44 * S
shot = Image.open("site/img/alta-desktop-split-three.webp").convert("RGBA")
shot = shot.resize((round(shot.width * scale), round(shot.height * scale)), Image.LANCZOS)
shot = shot.crop((0, 0, min(shot.width, W - left), min(shot.height, H - top)))
radius = 14 * S
shadow = Image.new("RGBA", (W, H))
ImageDraw.Draw(shadow).rounded_rectangle((left, top + 18 * S, W + radius, H + radius), radius, fill=(0, 0, 0, 150))
banner = Image.alpha_composite(banner, shadow.filter(ImageFilter.GaussianBlur(28 * S)))
banner = glow(banner, (left, top + 120 * S), 120 * S, CYAN, 34)
corners = Image.new("L", shot.size)
ImageDraw.Draw(corners).rounded_rectangle((0, 0, shot.width + radius, shot.height + radius), radius, fill=255)
banner.paste(shot, (left, top), corners)
ImageDraw.Draw(banner).rounded_rectangle((left, top, W + radius, H + radius), radius, outline=(255, 255, 255, 52), width=S)

# The logo and the name: Code in white, Alta in the colors of the site.
x = 64 * S
logo = Image.open("img/CodeAlta.png").convert("RGBA").resize((84 * S, 84 * S), Image.LANCZOS)
banner.alpha_composite(logo, (x, 56 * S))
name = font("segoeuib.ttf", 66)
draw = ImageDraw.Draw(banner)
code_width = draw.textlength("Code", font=name)
text(banner, (x + 104 * S, 52 * S), "Code", name, (255, 255, 255))
text(banner, (x + 104 * S + code_width, 52 * S), "Alta", name, [(122, 232, 255), CYAN, INDIGO, PURPLE])

# The tagline of the home page and what CodeAlta Desktop holds.
title = font("segoeuib.ttf", 50)
text(banner, (x, 176 * S), "Your agents.", title, (255, 255, 255))
text(banner, (x, 236 * S), "One workspace.", title, [CYAN, (99, 102, 241), PURPLE])
text(banner, (x, 320 * S), "Sessions  ·  Worktrees  ·  Automations  ·  Plugins", font("segoeui.ttf", 20), (176, 198, 222))

# The command that installs CodeAlta Desktop, in a box of its own.
draw = ImageDraw.Draw(banner)
mono = font("CascadiaMono.ttf", 21)
command = "dotnet tool install -g CodeAlta"
box_right = round(x + 44 * S + draw.textlength(command, font=mono) + 22 * S)
draw.rounded_rectangle((x, 376 * S, box_right, 432 * S), 12 * S, fill=(16, 30, 46, 235), outline=(98, 128, 160, 120), width=S)
draw.text((x + 20 * S, 391 * S), "$", font=mono, fill=(45, 212, 191))
draw.text((x + 44 * S, 391 * S), command, font=mono, fill=(236, 243, 252))
draw.text((x, 452 * S), "Also in your terminal: CodeAlta TUI", font=font("segoeui.ttf", 17), fill=(132, 152, 176))
draw.text((x, 556 * S), "codealta.github.io", font=font("segoeuisl.ttf", 23), fill=(158, 182, 208))

output = sys.argv[1] if len(sys.argv) > 1 else "site/img/alta-banner.png"
banner.convert("RGB").resize((1280, 640), Image.LANCZOS).save(output, optimize=True)
print(output)
