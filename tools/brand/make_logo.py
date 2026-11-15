"""Glovebox logo: a grey dash panel with a knob, the glovebox tub tipped open, tall white and
yellow receipts and a slip draped over the front edge.

Usage: python3 tools/brand/make_logo.py <out-dir>, then rasterise with rsvg-convert, for example
  rsvg-convert -w 512 -h 512 <out-dir>/icon.svg -o wwwroot/defaults/glovebox_icon_512.png
render_logo.py renders the wordmark PNGs (needs Playwright and Chrome).

All coordinates are in a 1024 x 1024 space. Writes into the output folder:
  mark.svg          art on transparent (works on light and dark backgrounds)
  mark-small.svg    simplified art for 16-48 px
  icon.svg          art on a light rounded tile (app icon)
  icon-square.svg   full-bleed tile (apple-touch)
  icon-maskable.svg full-bleed tile, art inside the maskable safe zone
  favicon.svg       simplified art, transparent
"""
import math, sys, pathlib

OUT = pathlib.Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)

C = dict(
    frame_top="#8F9297", frame_bot="#74777C", frame_edge="#6A6D72", crease="#62666B",
    knob="#7C8085", knob_ring="#5B5F64", knob_hi="#A3A6AA",
    lip="#696C71", cav_top="#1F2124", cav_bot="#35383C", cav_hi="#A9ACB0",
    side="#34373B", side_rim="#8C8F94", hook="#6E7176",
    front_top="#7A7D82", front_bot="#5F6267", front_rim="#A2A5A9", front_low="#4A4D51",
    handle_ring="#45484C", handle_in="#575A5F", handle_hi="#8F9296", catch="#3B3E42",
    paper="#FFFFFF", paper_shade="#E4E6E9", paper_edge="#D2D5D9", ink="#A6AAAF",
    yellow="#F9D86F", yellow_back="#EEC659", yellow_ink="#D0A147", yellow_edge="#E6BB4E",
    cream="#F6EDCB", cream_edge="#E6DAB0",
)

def zig_points(x0, x1, y, tooth=24, depth=12, up=True):
    """Zigzag from x0 to x1 along y, teeth pointing up (or down)."""
    n = max(2, round(abs(x1 - x0) / tooth))
    t = (x1 - x0) / n
    pts = []
    for i in range(n):
        pts.append((x0 + i * t, y))
        pts.append((x0 + (i + .5) * t, y - depth if up else y + depth))
    pts.append((x1, y))
    return pts

def P(pts):
    return " ".join(f"{x:.1f},{y:.1f}" for x, y in pts)

def slip(x0, y0, w, h, angle, fill, edge=None, content="", top=True, bottom=False, tooth=24, depth=12, extra=""):
    """A receipt drawn unrotated at (x0, y0), then rotated about its top-left corner."""
    pts = zig_points(x0, x0 + w, y0 + depth, tooth, depth, up=True) if top else [(x0, y0), (x0 + w, y0)]
    pts += zig_points(x0 + w, x0, y0 + h, tooth, depth, up=False) if bottom else [(x0 + w, y0 + h), (x0, y0 + h)]
    stroke = f' stroke="{edge}" stroke-width="3" stroke-linejoin="round"' if edge else ""
    return (f'<g transform="rotate({angle} {x0} {y0})"><polygon points="{P(pts)}" fill="{fill}"{stroke}/>'
            f'{extra}{content}</g>')

def ln(x0, x1, y, color, w=11, y1=None):
    return f'<path d="M{x0} {y} L{x1} {y if y1 is None else y1}" stroke="{color}" stroke-width="{w}" stroke-linecap="round"/>'

def defs(uid):
    return f'''<defs>
<linearGradient id="{uid}f" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{C['frame_top']}"/><stop offset="1" stop-color="{C['frame_bot']}"/></linearGradient>
<linearGradient id="{uid}c" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{C['cav_top']}"/><stop offset="1" stop-color="{C['cav_bot']}"/></linearGradient>
<linearGradient id="{uid}d" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{C['front_top']}"/><stop offset="1" stop-color="{C['front_bot']}"/></linearGradient>
<linearGradient id="{uid}p" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="{C['paper_shade']}"/><stop offset=".35" stop-color="{C['paper']}"/><stop offset="1" stop-color="{C['paper']}"/></linearGradient>
</defs>'''

FRAME = ("M120 316 L870 218 C925 211 975 245 975 300 L975 560 C975 600 960 620 930 625 "
         "L160 765 C100 776 60 760 52 720 C42 650 38 520 45 400 C48 345 75 322 120 316 Z")
OPENING = "M150 398 L826 300 C846 297 858 306 864 322 L934 584 C940 606 928 620 904 623 L178 720 C156 723 146 712 145 692 L130 424 C129 410 136 400 150 398 Z"
CAVITY = "M160 410 L822 314 C838 312 846 318 851 332 L918 580 C922 598 914 608 896 610 L182 706 C166 708 160 700 159 684 L146 432 C145 420 150 412 160 410 Z"
# the tub tipped toward you: a dark side wall, the thick top of the front wall, and the front face
SIDE = "M140 410 L286 712 C268 772 246 824 212 868 C180 834 142 712 133 566 C129 492 132 440 140 410 Z"
RIM = "M168 664 L928 594 C944 593 952 604 950 618 L958 640 L286 716 Z"
FRONT = ("M286 714 L952 638 C978 636 992 652 986 674 L968 742 C961 764 944 771 920 775 "
         "L222 874 C206 876 198 868 204 853 C238 800 270 760 286 714 Z")

def art(uid, detail=True):
    g = []
    g.append(f'<path d="{FRAME}" fill="url(#{uid}f)" stroke="{C["frame_edge"]}" stroke-width="3"/>')
    if detail:
        g.append(ln(44, 975, 562, C["crease"], 5, 413))
        g.append(f'<circle cx="920" cy="360" r="21" fill="{C["knob"]}" stroke="{C["knob_ring"]}" stroke-width="7"/>')
        g.append(f'<path d="M906 352 A16 16 0 0 1 922 344" stroke="{C["knob_hi"]}" stroke-width="4" fill="none" stroke-linecap="round"/>')
    g.append(f'<path d="{OPENING}" fill="{C["lip"]}"/>')
    g.append(f'<path d="{CAVITY}" fill="url(#{uid}c)"/>')
    if detail:
        g.append(ln(176, 812, 418, C["cav_hi"], 3, 326))
    # receipts, back to front
    if detail:
        g.append(slip(438, 392, 116, 190, -8, C["paper"], C["paper_edge"], ln(460, 524, 446, C["ink"], 9)))
        g.append(slip(797, 452, 98, 132, 22, C["paper"], C["paper_edge"], f'<circle cx="815" cy="508" r="6.5" fill="{C["ink"]}"/>' + ln(830, 858, 505, C["ink"], 8)))
        g.append(slip(198, 452, 284, 250, 3, C["yellow_back"], None, "", top=False))
        g.append(f'<g transform="rotate(-6 442 478)"><rect x="442" y="478" width="106" height="74" rx="4" fill="{C["cream"]}" stroke="{C["cream_edge"]}" stroke-width="3"/></g>')
        g.append(f'<path d="M497 432 Q530 418 563 430 L566 640 Q536 650 506 646 Z" fill="url(#{uid}p)" stroke="{C["paper_edge"]}" stroke-width="3" stroke-linejoin="round"/>')
        g.append(slip(196, 342, 178, 420, -14, C["paper"], C["paper_edge"],
                      ln(240, 348, 376, C["ink"]) + ln(240, 348, 398, C["ink"]) + ln(240, 302, 430, C["ink"]) + f'<circle cx="246" cy="465" r="8" fill="{C["ink"]}"/>'))
        g.append(slip(625, 278, 215, 300, 16, C["yellow"], C["yellow_edge"],
                      ln(665, 800, 344, C["yellow_ink"], 12) + ln(665, 795, 368, C["yellow_ink"], 12) + ln(665, 745, 392, C["yellow_ink"], 12), bottom=True, tooth=26))
        g.append(slip(283, 528, 168, 220, -12, C["paper"], C["paper_edge"],
                      ln(322, 408, 568, C["ink"], 10) + ln(322, 414, 590, C["ink"], 10) + ln(334, 388, 616, C["ink"], 10), tooth=22))
        g.append(slip(522, 572, 124, 90, -10, C["paper"], C["paper_edge"], "", tooth=20, depth=10))
    else:
        g.append(slip(186, 344, 204, 420, -12, C["paper"], C["paper_edge"], ln(232, 344, 398, C["ink"], 24), tooth=42, depth=22))
        g.append(slip(612, 262, 238, 340, 16, C["yellow"], C["yellow_edge"], ln(662, 792, 346, C["yellow_ink"], 24), bottom=True, tooth=42, depth=22))
    # the tub
    g.append(f'<path d="{RIM}" fill="#43464B"/>')
    g.append(f'<path d="{SIDE}" fill="{C["side"]}"/>')
    g.append(ln(146, 280, 420, C["side_rim"], 5, 700))
    if detail:
        g.append(f'<path d="M244 724 C258 732 262 752 252 770 C246 781 242 790 244 800" stroke="{C["hook"]}" stroke-width="6" fill="none" stroke-linecap="round"/>')
    g.append(f'<path d="{FRONT}" fill="url(#{uid}d)"/>')
    g.append(ln(296, 950, 720, C["front_rim"], 10, 645))
    g.append(f'<path d="M290 722 C276 770 252 812 214 862" stroke="#888B90" stroke-width="7" fill="none" stroke-linecap="round"/>')
    g.append(f'<path d="M232 868 L916 770" stroke="{C["front_low"]}" stroke-width="10" stroke-linecap="round"/>')
    hw, hh = (122, 50) if detail else (176, 70)
    hx, hy = 638, 742
    g.append(f'<g transform="rotate(-7 {hx} {hy})"><rect x="{hx - hw / 2}" y="{hy - hh / 2}" width="{hw}" height="{hh}" rx="{hh / 2}" fill="{C["handle_ring"]}"/>'
             f'<rect x="{hx - hw / 2 + 8}" y="{hy - hh / 2 + 8}" width="{hw - 16}" height="{hh - 16}" rx="{(hh - 16) / 2}" fill="{C["handle_in"]}"/>'
             f'<path d="M{hx - hw / 2 + 18} {hy + hh / 2 - 3} H{hx + hw / 2 - 18}" stroke="{C["handle_hi"]}" stroke-width="3" stroke-linecap="round" opacity=".7"/></g>')
    if detail:
        g.append(f'<g transform="rotate(-6 646 654)"><rect x="624" y="644" width="46" height="22" rx="6" fill="{C["catch"]}"/></g>')
        # the slip draped over the front edge, drawn as one sheet: it rises out of the tub,
        # rolls over the rim and hangs down the face with a torn bottom
        zz = zig_points(870, 734, 692, 22, 11, up=False)[1:]
        drape = ("M648 618 C650 598 668 588 694 586 L842 571 C858 570 865 579 863 592 L870 656 " +
                 " ".join(f"L{x:.1f} {y:.1f}" for x, y in zz) +
                 " L722 642 C717 620 702 609 684 610 C668 611 656 620 648 618 Z")
        g.append(f'<path d="{drape}" fill="#3C3F44" opacity=".4" transform="translate(9 10)"/>')
        g.append(f'<path d="{drape}" fill="{C["paper"]}" stroke="{C["paper_edge"]}" stroke-width="3" stroke-linejoin="round"/>')
        # the bend and the curl's inside
        g.append(f'<path d="M704 607 L852 592" stroke="{C["paper_shade"]}" stroke-width="5" stroke-linecap="round"/>')
        g.append(f'<path d="M656 612 C664 600 684 598 700 606" stroke="#D3D6DA" stroke-width="6" fill="none" stroke-linecap="round"/>')
        g.append(f'<circle cx="754" cy="642" r="7" fill="{C["ink"]}"/>' + ln(778, 828, 633, C["ink"], 9, 627))
    return "".join(g)

BBOX = (28, 200, 974, 690)  # x, y, w, h around the art

def svg(body, viewbox):
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{viewbox}">{body}</svg>\n'

def tile(rounded=True, scale=.92, detail=True, uid="t"):
    bg = (f'<linearGradient id="{uid}bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FAFBFC"/>'
          f'<stop offset="1" stop-color="#E7EAEE"/></linearGradient>')
    rect = f'<rect width="1024" height="1024" rx="{224 if rounded else 0}" fill="url(#{uid}bg)"/>'
    cx, cy = BBOX[0] + BBOX[2] / 2, BBOX[1] + BBOX[3] / 2
    tx, ty = 512 - cx * scale, 512 - cy * scale
    return svg(defs(uid).replace("</defs>", bg + "</defs>") + rect +
               f'<g transform="translate({tx:.1f} {ty:.1f}) scale({scale})">{art(uid, detail)}</g>', "0 0 1024 1024")

vb = " ".join(str(v) for v in BBOX)
(OUT / "mark.svg").write_text(svg(defs("gbm") + art("gbm"), vb))
(OUT / "mark-small.svg").write_text(svg(defs("gbs") + art("gbs", False), vb))
(OUT / "favicon.svg").write_text(svg(defs("gbf") + art("gbf", False), vb))
(OUT / "icon.svg").write_text(tile(True, .88))
(OUT / "icon-square.svg").write_text(tile(False, .9))
(OUT / "icon-maskable.svg").write_text(tile(False, .72))
print("ok")
