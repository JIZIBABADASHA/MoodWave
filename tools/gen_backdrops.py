"""
Full-screen backdrops (web/art/bg/<index>.webp) and the player bar's (web/art/bar/<index>.webp): each
channel picture (web/art/<index>.svg) rendered at 2560×1440, blurred into an atmosphere, with a fine grain.

Why pre-rendered: the browser's live blur of a large radius is computed at a reduced resolution and enlarged, and a
smooth dark gradient in 8 bits shows steps; both read as blocks. Here the blur is done in floating point at full
size, and a grain of about one level breaks the steps (dithering) and gives the picture a little texture. The page's
dimming and its darkening veil are done here too (drawn by the browser over the picture they showed as blocks).
Saved lossless (~2 MB each): any lossy codec's block edges show on these gradients.

    python tools/gen_backdrops.py          (needs Microsoft Edge, numpy, scipy, Pillow)
"""
import os
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter, zoom

HERE = os.path.dirname(os.path.abspath(__file__))
ART = os.path.normpath(os.path.join(HERE, "..", "web", "art"))
OUT = os.path.join(ART, "bg")
OUT_BAR = os.path.join(ART, "bar")      # the player bar's: the middle band of the same blur, its own darkening
OUT_CARD = os.path.join(ART, "card")    # the pictures themselves for the cards and the wheel, as images: an SVG with
CARD = 640                              # filters is drawn on the page's thread, and 14 of them made the page stutter
BAR_H = 180                             # px tall, 2560 wide (the bar is 88 px, ×2 for high-dpi screens)
BAR_X = [(0, .30), (.3, .55), (.7, .55), (1, .35)]   # the bar's darkening across, for its light text
W, H = 2560, 1440
SIGMA = 26          # px at 2560 wide: soft, the shapes still there
GRAIN = 1.6         # standard deviation in 8-bit levels, one grain for all three channels (no colour noise)
DIM = 0.85          # the page's dimming of its backdrop, done here
# The page darkens its backdrop towards the edges so the lyrics read. Drawn by the browser over the picture, that
# veil showed as blocks on the smooth backdrops, so it is part of the picture instead (and the page draws none):
# black at these opacities, across (x from 0 to 1) and down (y from 0 to 1).
VEIL_X = [(0, .55), (.35, .25), (.65, .35), (1, .6)]
VEIL_Y = [(0, .1), (1, .45)]
EDGE = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"]


def render(svg_path, png_path, w=None, h=None):
    """The SVG as the page would show it full screen (cover), by Edge without a window."""
    w, h = w or W, h or H
    edge = next(p for p in EDGE if os.path.exists(p))
    with tempfile.TemporaryDirectory() as tmp:
        page = os.path.join(tmp, "p.html")
        with open(page, "w", encoding="utf-8") as f:
            f.write(f'<html><body style="margin:0;overflow:hidden"><img src="file:///{svg_path.replace(os.sep, "/")}" '
                    f'style="width:{w}px;height:{h}px;object-fit:cover;display:block"></body></html>')
        subprocess.run([edge, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
                        f"--user-data-dir={tmp}", f"--window-size={w},{h}", f"--screenshot={png_path}", "file:///" + page.replace(os.sep, "/")],
                       check=True, capture_output=True, timeout=60)


def save(a, path, seed):
    grain = np.random.default_rng(seed).normal(0, GRAIN, size=(a.shape[0], a.shape[1], 1)).astype(np.float32)
    out = np.clip(a + grain + 0.5, 0, 255).astype(np.uint8)
    # lossless: lossy WebP (even at 96) works in 8×8 blocks whose edges show on a smooth gradient as faint lines
    Image.fromarray(out).save(path, "WEBP", lossless=True, quality=100, method=6)


def atmosphere(png_path, out_path, bar_path, seed):
    img = np.asarray(Image.open(png_path).convert("RGB"), dtype=np.float32)[:H, :W]
    # blur a margin-padded copy so the edges do not darken, then crop back
    pad = int(SIGMA * 3)
    big = np.pad(img, ((pad, pad), (pad, pad), (0, 0)), mode="reflect")
    soft = gaussian_filter(big, sigma=(SIGMA, SIGMA, 0))[pad:pad + H, pad:pad + W]
    # the bar: the middle fifth of the blur, squeezed to a strip (smooth, so nothing is lost), darkened across
    band = soft[int(H * .4):int(H * .6)]
    bar = zoom(band, (BAR_H / band.shape[0], 1, 1), order=1)
    bar *= (1 - np.interp(np.linspace(0, 1, W), *zip(*BAR_X)).astype(np.float32))[None, :, None]
    save(bar, bar_path, seed + 100)
    ax = np.interp(np.linspace(0, 1, W), *zip(*VEIL_X)).astype(np.float32)
    ay = np.interp(np.linspace(0, 1, H), *zip(*VEIL_Y)).astype(np.float32)
    soft *= DIM * (1 - ay)[:, None, None] * (1 - ax)[None, :, None]
    save(soft, out_path, seed)


def main():
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(OUT_BAR, exist_ok=True)
    os.makedirs(OUT_CARD, exist_ok=True)
    only = [int(x) for x in sys.argv[1:]] or range(14)
    with tempfile.TemporaryDirectory() as tmp:
        for i in only:
            png = os.path.join(tmp, f"{i}.png")
            render(os.path.join(ART, f"{i}.svg"), png)
            out = os.path.join(OUT, f"{i}.webp")
            bar = os.path.join(OUT_BAR, f"{i}.webp")
            atmosphere(png, out, bar, seed=i)
            card_png = os.path.join(tmp, f"{i}c.png")
            render(os.path.join(ART, f"{i}.svg"), card_png, CARD, CARD)
            Image.open(card_png).convert("RGB").crop((0, 0, CARD, CARD)).save(os.path.join(OUT_CARD, f"{i}.webp"), "WEBP", lossless=True, quality=100, method=6)
            print(i, f"{os.path.getsize(out) // 1024} + {os.path.getsize(bar) // 1024} + {os.path.getsize(os.path.join(OUT_CARD, f'{i}.webp')) // 1024} KB", flush=True)


if __name__ == "__main__":
    main()
