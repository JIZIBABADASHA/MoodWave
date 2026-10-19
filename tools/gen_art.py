"""
Channel artwork (web/art/<index>.svg), drawn from scratch: one abstract picture per channel, on a theme that fits
it (morning sky, midday sun, sunset, moonlit night, starry midnight, paint, forest light, golden bokeh, lights, silk
haze, cream silk, dance floor, wave). The elements that move on hover are marked with a class and a phase.

    python tools/gen_art.py
"""
import math
import os
import random

OUT = os.path.join(os.path.dirname(__file__), "..", "web", "art")
S = 200  # viewBox size


Q = random.Random(2024)   # animation timings only (a separate sequence: the pictures stay exactly the same)


def lv(cls):
    """Marks an element for the hover animation (moodchannels.css): its class and a phase --d between 0 and 1."""
    return f' class="{cls}" style="--d:{Q.random():.3f}"'


def svg(body, defs=""):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {S} {S}" preserveAspectRatio="xMidYMid slice">'
            f'<defs>{defs}</defs>{body}</svg>')


def lin(id_, stops, x2=0, y2=1):
    s = "".join(f'<stop offset="{o}" stop-color="{c}"{"" if a is None else f" stop-opacity=\"{a}\""}/>' for o, c, a in stops)
    return f'<linearGradient id="{id_}" x1="0" y1="0" x2="{x2}" y2="{y2}">{s}</linearGradient>'


def rad(id_, stops, cx=.5, cy=.5, r=.5):
    s = "".join(f'<stop offset="{o}" stop-color="{c}" stop-opacity="{a}"/>' for o, c, a in stops)
    return f'<radialGradient id="{id_}" cx="{cx}" cy="{cy}" r="{r}">{s}</radialGradient>'


def blur(id_, d):
    return f'<filter id="{id_}" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="{d}"/></filter>'


def bg(fill):
    return f'<rect width="{S}" height="{S}" fill="{fill}"/>'












def midnight():
    r = random.Random(5)
    d = lin("g", [(0, "#01020a", None), (1, "#0b1236", None)]) + blur("b1", 1.4)
    stars = "".join(f'<circle cx="{r.uniform(0, S):.1f}" cy="{r.uniform(0, S):.1f}" r="{r.choice((.5, .6, .8, 1.0)):.1f}" fill="#fff" opacity="{r.uniform(.35, .95):.2f}"{lv("tw")}/>' for _ in range(110))
    # the bright ones: a glow and a white core, of a few sizes, spread over the sky (14, they were too few at 5) …
    q = random.Random(55)
    spots = [(48, 52), (150, 38), (120, 120), (70, 160), (175, 150)]
    while len(spots) < 14:
        x, y = q.uniform(12, S - 12), q.uniform(12, S - 12)
        if all((x - a) ** 2 + (y - b) ** 2 > 30 ** 2 for a, b in spots):
            spots.append((round(x), round(y)))
    bright = ""
    for x, y in spots:
        g, c = q.uniform(2.4, 4.2), q.uniform(1.0, 1.6)
        bright += (f'<g{lv("tw2")}><circle cx="{x}" cy="{y}" r="{g:.1f}" fill="#bcd4ff" opacity=".6" filter="url(#b1)"/>'
                   f'<circle cx="{x}" cy="{y}" r="{c:.1f}" fill="#fff"/></g>')
    # … and between them and the faint ones, clear white points
    mid = "".join(f'<circle cx="{q.uniform(4, S - 4):.1f}" cy="{q.uniform(4, S - 4):.1f}" r="{q.uniform(1.1, 1.4):.1f}" fill="#fff" opacity="1"{lv("tw")}/>' for _ in range(16))
    return svg(bg("url(#g)") + stars + mid + bright, d)








def mellow():
    return bokeh(11, "#3a2203", ["#ffd25a", "#ffbf2e", "#ffe9a3", "#f2a20f"])


def upbeat():
    d = lin("g", [(0, "#ff3b4e", None), (1, "#b0001c", None)], x2=1, y2=.3) + blur("b2", 2.2)
    dots = ""
    for row in range(7):
        for col in range(7):
            k = col / 6
            x, y = 12 + col * 30 + row % 2 * 8, 10 + row * 30 + k * 4
            rr = 3 + k * 7
            # the lights pulse in a wave from left to right (--d by column)
            dots += (f'<g class="dot" style="--d:{col / 7 + row * .04:.3f}"><circle cx="{x:.0f}" cy="{y:.0f}" r="{rr * 1.8:.1f}" fill="#ffd8c2" opacity=".35" filter="url(#b2)"/>'
                     f'<circle cx="{x:.0f}" cy="{y:.0f}" r="{rr:.1f}" fill="#fff6e8"/></g>')
    return svg(bg("url(#g)") + dots, d)






def dance():
    r = random.Random(4)
    cols = ["#19e3ff", "#ff2fb4", "#4dff7a", "#ff9a1f", "#3d7bff"]
    hz, vx = 30, 100  # horizon height and vanishing x
    tiles = ""
    rows = [hz + (S - hz) * (i / 7) ** 1.6 for i in range(8)]
    for i in range(7):
        y0, y1 = rows[i], rows[i + 1]
        for j in range(-6, 7):
            def X(y, k):
                return vx + (k * 34) * (y - hz) / (S - hz) * 1.5
            pts = [(X(y0, j), y0), (X(y0, j + 1), y0), (X(y1, j + 1), y1), (X(y1, j), y1)]
            c = r.choice(cols)
            tiles += f'<polygon points="{" ".join(f"{x:.1f},{y:.1f}" for x, y in pts)}" fill="{c}" opacity="{r.uniform(.55, 1):.2f}" stroke="#16052b" stroke-width="1.6"{lv("tile")}/>'
    d = lin("g", [(0, "#16052b", None), (1, "#3b0f5c", None)]) + lin("fade", [(0, "#16052b", 1), (1, "#16052b", 0)])
    return svg(bg("url(#g)") + tiles + f'<rect width="{S}" height="70" y="{hz - 5}" fill="url(#fade)"/>', d)


def extreme():
    d = (lin("g", [(0, "#cfe8f5", None), (.35, "#3b8fc4", None), (1, "#06244a", None)])
         + lin("w", [(0, "#ffffff", None), (.4, "#bfe9f5", None), (1, "#1d6fa8", None)], x2=1, y2=.4) + blur("b3", 3))
    wave = ('<g class="surf"><path d="M-10 150 C 30 90, 90 40, 150 50 C 190 58, 205 95, 175 110 C 150 122, 130 100, 140 85 '
            'C 110 95, 95 130, 120 160 L 210 210 L -10 210 Z" fill="url(#w)"/>'
            '<path d="M150 50 C 190 58, 205 95, 175 110 C 160 118, 146 112, 140 100" stroke="#fff" stroke-width="5" fill="none" opacity=".85" filter="url(#b3)"/></g>'
            '<path d="M-10 175 C 50 160, 120 185, 210 165 L 210 210 L -10 210 Z" fill="#0a3a6b" opacity=".85"/>')
    r = random.Random(9)
    spray = "".join(f'<circle cx="{r.uniform(120, 200):.0f}" cy="{r.uniform(20, 90):.0f}" r="{r.uniform(.8, 2.4):.1f}" fill="#fff" opacity="{r.uniform(.4, .9):.2f}"{lv("spray")}/>' for _ in range(40))
    return svg(bg("url(#g)") + wave + spray, d)


def shuffle_all():
    return bokeh(21, "#120a24", ["#ff4f8b", "#ffcf3d", "#3ee6c8", "#5b8cff", "#c06bff", "#ff8a3d"], 40)


def cloud(x, y, w, op, fill="#fff", shade="#dfe9f2"):
    """A cumulus of round lobes, shaded top to bottom by a gradient (soft without blur, so it stays crisp when large)."""
    gid = f"c{int(x)}_{int(y)}_{int(w)}"
    d = (f'<linearGradient id="{gid}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{fill}"/>'
         f'<stop offset="1" stop-color="{shade}"/></linearGradient>')
    parts = [f'<circle cx="{x + dx * w:.1f}" cy="{y + dy * w:.1f}" r="{r * w:.1f}"/>'
             for dx, dy, r in ((0, 0, .32), (.3, .06, .26), (-.32, .08, .24), (.58, .14, .18), (-.6, .16, .16), (.12, -.12, .24))]
    base = f'<rect x="{x - .76 * w:.1f}" y="{y + .1 * w:.1f}" width="{1.5 * w:.1f}" height="{.2 * w:.1f}" rx="{.1 * w:.1f}"/>'
    return d, f'<g fill="url(#{gid})" opacity="{op}" filter="url(#b1)"{lv("cl")}>{"".join(parts)}{base}</g>'


def clouds(*specs):
    defs, body = "", ""
    for s in specs:
        d, b = cloud(*s)
        defs += d
        body += b
    return defs, body


def morning():
    cd, cb = clouds((150, 52, 64, .9), (42, 78, 46, .75), (128, 118, 52, .6), (30, 150, 54, .55))
    d = (lin("g", [(0, "#9fd2ee", None), (.5, "#fbefc8", None), (.8, "#fde7b0", None), (1, "#cfeab0", None)])
         + rad("sun", [(0, "#fffbe8", 1), (.12, "#fff4c2", .95), (.4, "#ffe9a0", .35), (1, "#ffe9a0", 0)]) + blur("b1", .6) + cd)
    return svg(bg("url(#g)") + '<circle cx="62" cy="138" r="95" fill="url(#sun)" class="glow"/><circle cx="62" cy="138" r="11" fill="#fffdf2"/>' + cb, d)


def daytime():
    cd, cb = clouds((52, 168, 66, 1), (150, 182, 58, .95), (12, 124, 40, .7))
    d = (lin("g", [(0, "#0a6fc2", None), (1, "#7fd0f6", None)])
         + rad("sun", [(0, "#ffffff", 1), (.1, "#fffbe0", .95), (.35, "#dff4ff", .45), (1, "#bfe8ff", 0)]) + blur("b1", .6) + cd)
    rays = "".join(f'<line x1="140" y1="55" x2="{140 + 160 * math.cos(a):.1f}" y2="{55 + 160 * math.sin(a):.1f}" stroke="#fff" stroke-width="{1.2 if i % 2 else 2.2}" opacity=".3"/>'
                   for i, a in enumerate(i * math.pi / 8 for i in range(16)))
    return svg(bg("url(#g)") + '<g class="rays">' + rays + '</g><circle cx="140" cy="55" r="90" fill="url(#sun)" class="glow"/><circle cx="140" cy="55" r="10" fill="#fff"/>' + cb, d)


def evening():
    d = (lin("g", [(0, "#2b1238", None), (.42, "#b8361f", None), (.72, "#f59a2a", None), (1, "#ffd27a", None)])
         + rad("sun", [(0, "#fff4c0", 1), (.2, "#ffc45a", .85), (1, "#ff7a1a", 0)])
         + lin("cl", [(0, "#4a1a2a", None), (1, "#2a0f22", None)]) + lin("rim", [(0, "#ffb35a", .0), (.5, "#ffcf80", .9), (1, "#ffb35a", 0)], x2=1, y2=0))
    sun = '<circle cx="110" cy="128" r="80" fill="url(#sun)" class="glow"/><circle cx="110" cy="128" r="17" fill="#fff1c4"/>'
    bands = ""
    for x, y, w, h, o in ((38, 72, 86, 7, .85), (160, 94, 94, 6, .8), (88, 114, 118, 5, .75), (24, 146, 72, 4, .6), (176, 150, 60, 4, .55)):
        bands += (f'<g{lv("band")}><path d="M{x - w / 2} {y} Q {x - w / 4} {y - h} {x} {y - h * .7} T {x + w / 2} {y} Z" fill="url(#cl)" opacity="{o}"/>'
                  f'<path d="M{x - w / 2.4} {y - .4} Q {x} {y + h * .45} {x + w / 2.4} {y - .4}" stroke="url(#rim)" stroke-width="1.2" fill="none"/></g>')
    return svg(bg("url(#g)") + sun + bands, d)


def night():
    cd, cb = clouds((60, 122, 74, .7, "#8fb4ea", "#3f6db8"), (162, 152, 64, .6, "#7aa3e0", "#2e5aa6"), (40, 178, 66, .55, "#4e7cc8", "#1c3f86"))
    d = (lin("g", [(0, "#020b26", None), (.6, "#0d3577", None), (1, "#1f62b8", None)])
         + rad("glow", [(0, "#cfe6ff", .9), (.25, "#9cc4f5", .45), (1, "#4f8de0", 0)]) + blur("b1", .6) + cd)
    return svg(bg("url(#g)") + '<circle cx="125" cy="62" r="70" fill="url(#glow)" class="glow"/><circle cx="125" cy="62" r="17" fill="#f4f9ff"/>'
               '<circle cx="119" cy="58" r="3" fill="#dde8f6"/><circle cx="130" cy="67" r="2.2" fill="#e4ecf8"/>' + cb, d)


def energetic():
    """Splashes of bright paint: crisp blots with their drops, over a few soft glows."""
    r = random.Random(7)
    cols = ["#ff2d7a", "#b6ff2e", "#ff8a00", "#00d5ff", "#ffe600", "#c03cff"]
    d = blur("b3", 3)
    glows = "".join(f'<circle cx="{r.uniform(0, S):.0f}" cy="{r.uniform(0, S):.0f}" r="{r.uniform(30, 55):.0f}" fill="{r.choice(cols)}" opacity=".35" filter="url(#b3)"{lv("glow2")}/>' for _ in range(6))
    blots = ""
    for _ in range(11):
        cx, cy, rr, c = r.uniform(0, S), r.uniform(0, S), r.uniform(10, 26), r.choice(cols)
        pts = []
        for k in range(14):
            a = k / 14 * 2 * math.pi
            q = rr * r.uniform(.75, 1.2)
            pts.append((cx + q * math.cos(a), cy + q * math.sin(a)))
        mid = [((pts[k][0] + pts[(k + 1) % 14][0]) / 2, (pts[k][1] + pts[(k + 1) % 14][1]) / 2) for k in range(14)]
        path = f"M{mid[-1][0]:.1f} {mid[-1][1]:.1f} " + " ".join(f"Q{pts[k][0]:.1f} {pts[k][1]:.1f} {mid[k][0]:.1f} {mid[k][1]:.1f}" for k in range(14)) + " Z"
        blots += f'<path d="{path}" fill="{c}" opacity=".92"{lv("blot")}/>'
        for _ in range(r.randint(3, 7)):
            a, dist = r.uniform(0, 2 * math.pi), rr * r.uniform(1.3, 2.4)
            blots += f'<circle cx="{cx + dist * math.cos(a):.1f}" cy="{cy + dist * math.sin(a):.1f}" r="{r.uniform(1, 3.4):.1f}" fill="{c}"{lv("drop")}/>'
    return svg(bg("#140a1e") + glows + blots, d)


def relax():
    """Sunlight through a forest: shafts of light that fade along their length, crisp leaves, motes in the light."""
    r = random.Random(3)
    d = (lin("g", [(0, "#f2fbe4", None), (.5, "#9fd37a", None), (1, "#2f7a2c", None)], x2=.6)
         + lin("ray", [(0, "#fffde8", .75), (1, "#fffde8", 0)], x2=.55, y2=1))
    rays = "".join(f'<polygon points="{x},-10 {x + w},-10 {x + w + 140},{S + 10} {x + 140},{S + 10}" fill="url(#ray)" opacity="{o}"{lv("ray")}/>'
                   for x, w, o in ((-60, 14, .5), (-20, 8, .45), (10, 20, .35), (55, 10, .45), (90, 6, .35)))
    leaves = ""
    for _ in range(18):
        x, y, L = r.uniform(0, S), r.uniform(80, S + 10), r.uniform(10, 22)
        rot = r.uniform(-70, 70)
        leaves += (f'<g{lv("leaf")}><path d="M0 0 Q {L * .5} {-L * .32} {L} 0 Q {L * .5} {L * .32} 0 0 Z" transform="translate({x:.0f} {y:.0f}) rotate({rot:.0f})" '
                   f'fill="{r.choice(["#1f5e22", "#2c7a2c", "#3f8f34"])}" opacity="{r.uniform(.45, .8):.2f}"/></g>')
    motes = "".join(f'<circle cx="{r.uniform(0, S):.0f}" cy="{r.uniform(0, 140):.0f}" r="{r.uniform(.6, 1.6):.1f}" fill="#fffde8" opacity="{r.uniform(.4, .9):.2f}"{lv("mote")}/>' for _ in range(30))
    return svg(bg("url(#g)") + rays + motes + leaves, d)


def bokeh(seed, base, cols, n=34):
    """Out-of-focus lights as a lens draws them: discs with a defined edge and a brighter rim, some nearer and softer."""
    r = random.Random(seed)
    defs = blur("b1", 1.2)
    out = bg(base)
    for k, c in enumerate(cols):
        defs += rad(f"k{k}", [(0, c, .55), (.82, c, .7), (.95, c, .95), (1, c, .0)])
    for _ in range(n):
        x, y, rr = r.uniform(-10, S + 10), r.uniform(-10, S + 10), r.uniform(5, 24)
        k = r.randrange(len(cols))
        soft = ' filter="url(#b1)"' if rr > 17 else ""
        out += f'<circle cx="{x:.0f}" cy="{y:.0f}" r="{rr:.0f}" fill="url(#k{k})" opacity="{r.uniform(.35, .85):.2f}"{soft}{lv("bk")}/>'
    return svg(out, defs)


def emotional():
    """A haze of colour in folds: overlapping waves, each a band shaded across, with a fine light edge."""
    d = lin("g", [(0, "#f6c98d", None), (.45, "#c5509c", None), (1, "#2e0f63", None)])
    waves = ""
    spec = ((62, 22, "#ffe0f0", "#f39ccc", .55), (98, 30, "#ff9fd6", "#c4489c", .6), (136, 22, "#a066ff", "#5a24b0", .7), (172, 28, "#3c1680", "#1a0640", .85))
    for k, (y, a, c1, c2, o) in enumerate(spec):
        d += lin(f"w{k}", [(0, c1, None), (1, c2, None)])
        top = f"M-20 {y} C 40 {y - a}, 90 {y + a}, 140 {y} S 230 {y - a}, 240 {y}"
        waves += f'<g class="wave" style="--d:{k / 4:.2f}"><path d="{top} L 240 {S + 20} L -20 {S + 20} Z" fill="url(#w{k})" opacity="{o}"/>'
        waves += f'<path d="{top}" stroke="#fff" stroke-opacity=".35" stroke-width=".8" fill="none"/></g>'
    return svg(bg("url(#g)") + waves, d)


def lounge():
    """Cream silk: folds laid over each other, each shaded from its crease (shadow) to its crest (sheen)."""
    d = lin("g", [(0, "#f4e6c8", None), (1, "#dcc391", None)], x2=1)
    folds = ""
    xs = (-30, 10, 48, 84, 122, 158, 196)
    for k, x in enumerate(xs):
        gid = f"f{k}"
        d += (f'<linearGradient id="{gid}" gradientUnits="userSpaceOnUse" x1="{x - 6}" y1="0" x2="{x + 46}" y2="0">'
              '<stop offset="0" stop-color="#b8975b"/><stop offset=".18" stop-color="#e2cb9a"/><stop offset=".42" stop-color="#fffaf0"/>'
              '<stop offset=".7" stop-color="#efdcb4"/><stop offset="1" stop-color="#d4b67c"/></linearGradient>')
        edge = f"M{x} -10 C {x + 46} 50, {x - 34} 120, {x + 24} 210"
        folds += f'<g class="fold" style="--d:{k / 7:.2f}"><path d="{edge} L 260 210 L 260 -10 Z" fill="url(#{gid})"/>'
        folds += f'<path d="{edge}" stroke="#a88647" stroke-opacity=".35" stroke-width=".7" fill="none"/></g>'
    return svg(bg("url(#g)") + folds, d)


ART = [morning, daytime, evening, night, midnight, energetic, relax, mellow, upbeat, emotional, lounge, dance, extreme, shuffle_all]

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for i, f in enumerate(ART):
        with open(os.path.join(OUT, f"{i}.svg"), "w", encoding="utf-8") as fh:
            fh.write(f())
    print(len(ART), "pictures in", os.path.normpath(OUT))
