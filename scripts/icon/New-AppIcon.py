"""
Draws the console's app icon (AppIcon.ico) and a 256 px PNG for the README.

Neutral on purpose (M11): the product ships branded to nobody, so the icon is the default palette's slate and blue
with a magnifier over a person — "type a name, land on the person" — and no company mark. A branded build passes its
own icon to scripts/Build-Release.ps1 -IconPath instead.

Each size is drawn on its own at 8x and scaled down, rather than one image shrunk: below 32 px the person is left out
and the strokes thicken, because a 5-pixel figure inside a 9-pixel lens is noise.

Needs Pillow. Run only when the icon changes:
    python scripts/icon/New-AppIcon.py
"""
import io
import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
ICO = ROOT / 'src' / 'AdminConsole.App' / 'Assets' / 'AppIcon.ico'
PNG = ROOT / 'docs' / 'images' / 'app-icon.png'
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SS = 8  # supersampling

SLATE_TOP = (0x34, 0x40, 0x54)
SLATE_BOTTOM = (0x1F, 0x27, 0x33)  # Branding default chrome
BLUE = (0x58, 0xA6, 0xFF)          # Branding default accent
BLUE_DEEP = (0x2F, 0x81, 0xF7)
LIGHT = (0xE6, 0xED, 0xF3)


def render(size: int) -> Image.Image:
    s = size * SS
    small = size < 32
    img = Image.new('RGBA', (s, s), (0, 0, 0, 0))

    # Background: a rounded square with a soft top light. Small sizes fill the square; larger ones keep a margin so the
    # tile sits like the other icons in the Start menu.
    margin = 0 if small else round(s * 0.055)
    radius = round((s - 2 * margin) * 0.24)
    tile = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    grad = ImageDraw.Draw(tile)
    for y in range(s):
        t = y / (s - 1)
        c = tuple(round(a + (b - a) * t) for a, b in zip(SLATE_TOP, SLATE_BOTTOM))
        grad.line([(0, y), (s, y)], fill=c + (255,))
    mask = Image.new('L', (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - margin - 1, s - margin - 1], radius=radius, fill=255)
    img.paste(tile, (0, 0), mask)
    if not small:
        # A faint light rim, so the tile's edge still reads on a dark taskbar.
        rim = Image.new('RGBA', (s, s), (0, 0, 0, 0))
        ImageDraw.Draw(rim).rounded_rectangle([margin, margin, s - margin - 1, s - margin - 1], radius=radius,
                                              outline=(255, 255, 255, 34), width=max(SS, round(s * 0.008)))
        img.alpha_composite(rim)

    d = ImageDraw.Draw(img)
    inner = s - 2 * margin

    # The lens: centre up and left, so the handle has room to the lower right.
    cx = margin + inner * (0.44 if not small else 0.42)
    cy = margin + inner * (0.44 if not small else 0.42)
    r = inner * (0.255 if not small else 0.27)
    w = inner * (0.085 if not small else 0.13)

    # Handle first, so the ring overlaps its root cleanly.
    hw = inner * (0.12 if not small else 0.16)
    a = math.radians(45)
    x0, y0 = cx + (r + w * 0.2) * math.cos(a), cy + (r + w * 0.2) * math.sin(a)
    x1, y1 = margin + inner * 0.80, margin + inner * 0.80
    d.line([(x0, y0), (x1, y1)], fill=BLUE_DEEP + (255,), width=round(hw))
    for (px, py) in [(x1, y1)]:
        d.ellipse([px - hw / 2, py - hw / 2, px + hw / 2, py + hw / 2], fill=BLUE_DEEP + (255,))

    # The lens glass: a faint fill so the person reads as being looked at, then the ring.
    if not small:
        # Composited, not drawn: ImageDraw replaces pixels, so a translucent fill drawn directly punches a hole.
        glass = Image.new('RGBA', (s, s), (0, 0, 0, 0))
        ImageDraw.Draw(glass).ellipse([cx - r, cy - r, cx + r, cy + r], fill=BLUE + (46,))
        img.alpha_composite(glass)
        d = ImageDraw.Draw(img)
    d.ellipse([cx - r - w / 2, cy - r - w / 2, cx + r + w / 2, cy + r + w / 2], outline=BLUE + (255,), width=round(w))

    # The person, clipped to the inside of the lens.
    if not small:
        person = Image.new('RGBA', (s, s), (0, 0, 0, 0))
        p = ImageDraw.Draw(person)
        hr = r * 0.30
        hy = cy - r * 0.22
        p.ellipse([cx - hr, hy - hr, cx + hr, hy + hr], fill=LIGHT + (255,))
        bw, bh = r * 0.62, r * 0.50
        by = cy + r * 0.50
        p.ellipse([cx - bw, by - bh, cx + bw, by + bh * 1.6], fill=LIGHT + (255,))
        clip = Image.new('L', (s, s), 0)
        ri = r - w / 2 - r * 0.04
        ImageDraw.Draw(clip).ellipse([cx - ri, cy - ri, cx + ri, cy + ri], fill=255)
        img.paste(person, (0, 0), Image.composite(person.getchannel('A'), Image.new('L', (s, s), 0), clip))

    return img.resize((size, size), Image.LANCZOS)


def dib(img: Image.Image) -> bytes:
    """A classic 32-bit DIB icon entry (BGRA, bottom-up, empty AND mask): what every shell reads below 256 px."""
    w, h = img.size
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    px = img.tobytes('raw', 'BGRA')
    rows = [px[y * w * 4:(y + 1) * w * 4] for y in range(h)]
    xor = b''.join(reversed(rows))
    and_row = ((w + 31) // 32) * 4
    return header + xor + b'\x00' * (and_row * h)


def png(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, 'PNG', optimize=True)
    return buf.getvalue()


def main() -> None:
    images = {size: render(size) for size in SIZES}
    entries = [(size, png(images[size]) if size >= 256 else dib(images[size])) for size in SIZES]
    out = struct.pack('<HHH', 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        dim = 0 if size >= 256 else size
        out += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out += b''.join(data for _, data in entries)
    ICO.parent.mkdir(parents=True, exist_ok=True)
    ICO.write_bytes(out)
    images[256].save(PNG, 'PNG', optimize=True)
    print(f'{ICO} ({len(out):,} bytes, {len(entries)} sizes); {PNG}')

    # A review sheet, outside the repo: every size at 1:1, then 16/24/32/48 enlarged, on light and on dark.
    import sys
    if len(sys.argv) > 1:
        sheet = Image.new('RGBA', (1300, 760), (255, 255, 255, 255))
        sheet.paste(Image.new('RGBA', (1300, 380), (0x20, 0x20, 0x20, 255)), (0, 380))
        for band in (0, 380):
            x = 10
            for size in SIZES:
                sheet.alpha_composite(images[size], (x, band + 10))
                x += size + 14
            x = 10
            for size in (16, 24, 32, 48):
                big = images[size].resize((size * 4, size * 4), Image.NEAREST)
                sheet.alpha_composite(big, (x, band + 370 - size * 4))
                x += size * 4 + 24
        sheet.save(sys.argv[1])
        print(f'review sheet: {sys.argv[1]}')


if __name__ == '__main__':
    main()
