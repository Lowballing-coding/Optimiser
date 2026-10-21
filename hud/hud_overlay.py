"""Broadcast-style HUD for the shot trainer.

Draws the shot card, countdown, toasts and controls panel into small transparent,
click-through, topmost Win32 layered windows that follow the Rocket League client.
Rendering is pure Pillow (shared with the --preview path); only the windowing is Win32.

    hud = Hud()          # daemon thread; hud.ok is False on non-Windows or on any failure
    hud.update(state)    # cheap, call as often as you like
    hud.close()

    python hud_overlay.py --preview OUTDIR     # PNG mock-ups over preview_bg.jpg
    python hud_overlay.py --selftest
"""
from __future__ import annotations

import math
import os
import sys
import threading
import time
import traceback
from functools import lru_cache
from typing import NamedTuple

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))

ACCENT = (255, 138, 31)
TEXT = (242, 243, 245)
MUTED = (163, 169, 181)
FAINT = (107, 114, 128)
GOOD = (61, 220, 151)
BAD = (255, 92, 92)
INK = (12, 14, 18)
PANEL = INK + (220,)   # 0.86 rather than 0.80: bright arena geometry bled through and sank FAINT text
LABEL = (138, 145, 158)  # small tracked labels / footer: FAINT is too dim at 12-14px over bright maps
WHITE = (255, 255, 255)
SS = 4  # supersampling factor for shapes

# ---------------------------------------------------------------------------- fonts

# key -> (bundled file, Windows fallback, variable-font instance for the fallback)
_FONTS = {
    'D7': ('BarlowSemiCondensed-Bold.ttf', 'bahnschrift.ttf', 'Bold'),
    'D6': ('BarlowSemiCondensed-SemiBold.ttf', 'bahnschrift.ttf', 'SemiBold'),
    'B4': ('Inter-Regular.ttf', 'segoeui.ttf', None),
    'B5': ('Inter-Medium.ttf', 'segoeui.ttf', None),
}


@lru_cache(maxsize=256)
def _font(key, size):
    file, fallback, weight = _FONTS[key]
    for path in (os.path.join(HERE, 'fonts', file), os.path.join('C:/Windows/Fonts', fallback)):
        try:
            f = ImageFont.truetype(path, size, layout_engine=ImageFont.Layout.BASIC)
        except OSError:
            continue
        if weight and path.endswith(fallback):
            try:
                f.set_variation_by_name(weight)
            except Exception:
                pass
        return f
    return ImageFont.load_default(size)


def F(key, px, s):
    return _font(key, max(1.0, round(px * s * 4) / 4))


def _cap(font):
    return -font.getbbox('H', anchor='ls')[1]


def _baseline(font, line_h):
    """Baseline offset inside a line box of height line_h (CSS half-leading model)."""
    asc, desc = font.getmetrics()
    return (line_h - asc - desc) / 2 + asc


@lru_cache(maxsize=64)
def _digit_cell(font):
    return max(font.getlength(d) for d in '0123456789')


def _pieces(text, font, track=0.0, tab=False):
    """[(x offset, chunk)] and total width. Tracking keeps kerning by measuring prefixes."""
    if tab:
        cell, x, out = _digit_cell(font), 0.0, []
        for ch in text:
            adv = cell if ch.isdigit() else font.getlength(ch)
            out.append((x + (adv - font.getlength(ch)) / 2, ch))
            x += adv
        return out, x
    if track:
        out = [(font.getlength(text[:i]) + i * track, ch) for i, ch in enumerate(text)]
        return out, font.getlength(text) + track * (len(text) - 1)
    return [(0.0, text)], font.getlength(text)


@lru_cache(maxsize=4096)
def _tw(text, font, track=0.0, tab=False):
    return _pieces(text, font, track, tab)[1] if text else 0.0


@lru_cache(maxsize=64)
def _ink(text, font):
    """Real ink box relative to the 'ls' anchor (getbbox's x range includes side bearings)."""
    l, t, r, b = font.getbbox(text, anchor='ls')
    m = Image.new('L', (r - l + 4, b - t + 4))
    ImageDraw.Draw(m).text((2 - l, 2 - t), text, font=font, fill=255, anchor='ls')
    bb = m.getbbox()
    return (l, t, r, b) if not bb else (bb[0] - 2 + l, bb[1] - 2 + t, bb[2] - 2 + l, bb[3] - 2 + t)


# ---------------------------------------------------------------------------- compositing

def _comp(im, layer, x, y):
    """alpha_composite layer onto im at (x, y), clipped to im."""
    x, y = int(x), int(y)
    l, t = max(0, -x), max(0, -y)
    r, b = min(layer.width, im.width - x), min(layer.height, im.height - y)
    if r <= l or b <= t:
        return
    if (l, t, r, b) != (0, 0, layer.width, layer.height):
        layer = layer.crop((l, t, r, b))
    im.alpha_composite(layer, (x + l, y + t))


def _paint(im, mask, x, y, rgba):
    """Composite a solid colour through an L mask (correct over transparent pixels too)."""
    a = rgba[3] if len(rgba) > 3 else 255
    if a < 255:
        mask = mask.point(lambda v: v * a // 255)
    layer = Image.new('RGBA', mask.size, tuple(rgba[:3]) + (0,))
    layer.putalpha(mask)
    _comp(im, layer, x, y)


@lru_cache(maxsize=512)
def _text_mask(text, font, track, tab, fx, fy, pad):
    """Glyph coverage for text whose pen starts at (pad + fx, pad + asc + fy); cached because
    most strings survive many re-renders (only the clock changes every 0.1 s)."""
    pieces, width = _pieces(text, font, track, tab)
    asc, desc = font.getmetrics()
    mask = Image.new('L', (int(width) + 2 * pad + 2, asc + desc + 2 * pad + 2))
    d = ImageDraw.Draw(mask)
    for dx, chunk in pieces:
        d.text((pad + fx + dx, pad + asc + fy), chunk, font=font, fill=255, anchor='ls')
    return mask, width


def _text(im, x, base, text, font, color, track=0.0, tab=False, shadows=(), alpha=255):
    """Draw text with its left edge at x and baseline at base. Returns the advance width.
    shadows: ((blur, alpha, dy), ...) black glows drawn under the text."""
    if not text:
        return 0.0
    asc = font.getmetrics()[0]
    pad = int(font.size * 0.25 + max((b for b, _, _ in shadows), default=0) * 3) + 2
    fx, fy = round(x % 1, 2), round((base - asc) % 1, 2)
    mask, width = _text_mask(text, font, track, tab, fx, fy, pad)
    x0, y0 = math.floor(x) - pad, math.floor(base - asc) - pad
    for blur, a, dy in shadows:
        _paint(im, mask.filter(ImageFilter.GaussianBlur(blur)), x0, y0 + round(dy), (0, 0, 0, alpha * a // 255))
    _paint(im, mask, x0, y0, tuple(color[:3]) + (alpha,))
    return width


def _ss_mask(box, draw_fn, fast=False):
    """Anti-aliased coverage: draw_fn(draw, T, k) paints white into a SS-times mask.
    T(x, y) maps image coordinates into it; T(x, y, 1) is for the far corner of a box, because
    PIL boxes include their end pixel. k is the supersampling factor. Returns (mask, x0, y0)."""
    x0, y0 = math.floor(box[0]) - 1, math.floor(box[1]) - 1
    w, h = math.ceil(box[2]) + 1 - x0, math.ceil(box[3]) + 1 - y0
    big = Image.new('L', (w * SS, h * SS))
    draw_fn(ImageDraw.Draw(big), lambda x, y, end=0: ((x - x0) * SS - end, (y - y0) * SS - end), SS)
    # reduce() is the exact box average; LANCZOS is a touch crisper but 3x slower per frame
    return (big.reduce(SS) if fast else big.resize((w, h), Image.Resampling.LANCZOS)), x0, y0


def _shape(im, box, rgba, draw_fn, fast=False):
    mask, x0, y0 = _ss_mask(box, draw_fn, fast)
    _paint(im, mask, x0, y0, rgba)


@lru_cache(maxsize=64)
def _rr_mask(w, h, r):
    """Anti-aliased rounded-rectangle coverage mask of exactly w x h pixels."""
    w, h = max(1, w), max(1, h)
    r = max(0.0, min(r, w / 2, h / 2))
    big = Image.new('L', (w * SS, h * SS))
    ImageDraw.Draw(big).rounded_rectangle((0, 0, w * SS - 1, h * SS - 1), radius=r * SS, fill=255)
    return big.resize((w, h), Image.Resampling.LANCZOS)


def _ring_mask(w, h, r, bw):
    outer = np.asarray(_rr_mask(w, h, r), np.int16)
    inner = np.zeros_like(outer)
    inner[bw:h - bw, bw:w - bw] = np.asarray(_rr_mask(w - 2 * bw, h - 2 * bw, max(0, r - bw)), np.int16)
    return Image.fromarray(np.clip(outer - inner, 0, 255).astype(np.uint8))


def _rr(im, x, y, w, h, r, fill, border=None, bw=1):
    x, y, w, h = round(x), round(y), round(w), round(h)
    _paint(im, _rr_mask(w, h, r), x, y, fill)
    if border:
        _paint(im, _ring_mask(w, h, r, bw), x, y, border)


def _ease(p):
    return 1 - (1 - p) ** 3


def _margin(s):
    return math.ceil(36 * s)


@lru_cache(maxsize=32)
def _panel(w, h, r, s):
    """Panel (fill + hairline + soft drop shadow) centred in a canvas with _margin(s) all round."""
    m = _margin(s)
    canvas = Image.new('RGBA', (w + 2 * m, h + 2 * m), (0, 0, 0, 0))
    outer = _rr_mask(w, h, r)
    sh = Image.new('L', canvas.size)
    sh.paste(outer, (m, m + round(6 * s)))
    sh = np.asarray(sh.filter(ImageFilter.GaussianBlur(9 * s)), np.float32) * 0.35
    cut = np.zeros_like(sh)
    cut[m:m + h, m:m + w] = np.asarray(outer, np.float32) / 255
    sh *= 1 - cut  # like CSS, the outer shadow never darkens the translucent panel itself
    _paint(canvas, Image.fromarray(sh.round().astype(np.uint8)), 0, 0, (0, 0, 0))
    _paint(canvas, outer, m, m, PANEL)
    _paint(canvas, _ring_mask(w, h, r, max(1, round(s))), m, m, WHITE + (23,))
    return canvas


def _hline(im, x, y, w, rgba, s):
    _paint(im, Image.new('L', (max(1, round(w)), max(1, round(s))), 255), round(x), round(y), rgba)


# ---------------------------------------------------------------------------- text fitting

def _ellipsize(text, font, maxw, track=0.0, force=False):
    if not force and _tw(text, font, track) <= maxw:
        return text
    lo, hi = 0, len(text)
    while lo < hi:  # longest prefix that still fits with the ellipsis
        mid = (lo + hi + 1) // 2
        if _tw(text[:mid].rstrip(' ,.;:-') + '…', font, track) <= maxw:
            lo = mid
        else:
            hi = mid - 1
    return text[:lo].rstrip(' ,.;:-') + '…'


@lru_cache(maxsize=64)
def _wrap(text, font, maxw, max_lines):
    words, lines, cur = [w for w in text[:600].split(' ') if w], [], ''  # NBSP keeps words together
    while words:
        cand = f'{cur} {words[0]}' if cur else words[0]
        if font.getlength(cand) <= maxw:
            cur = cand
            words.pop(0)
        elif cur:
            lines.append(cur)
            cur = ''
        else:  # one word wider than the line: hard-break it
            w, n = words[0], 1
            while n < len(w) and font.getlength(w[:n + 1]) <= maxw:
                n += 1
            lines.append(w[:n])
            words[0] = w[n:]
        if len(lines) > max_lines:
            break
    if cur:
        lines.append(cur)
    if len(lines) > max_lines:
        lines = lines[:max_lines - 1] + [_ellipsize(lines[max_lines - 1], font, maxw, force=True)]
    return tuple(lines)


# ---------------------------------------------------------------------------- state normalisation

def _f(v, d=0.0, lo=-1e9, hi=1e9):
    try:
        v = float(v)
    except (TypeError, ValueError, OverflowError):
        return d
    return d if v != v else min(hi, max(lo, v))


def _i(v, d=0, lo=0, hi=99999):
    return int(_f(v, d, lo, hi))


def _s(v):
    return '' if v is None else ' '.join(str(v).split()).replace("'", '\u2019')


class Card(NamedTuple):
    index: int
    total: int
    title: str
    tip: str
    left: float      # displayed seconds, ceil'd to 0.1 so it reaches 0.0 exactly at the end
    limit: float
    made: int
    taken: int
    urgent: bool
    extended: bool


class Countdown(NamedTuple):
    n: int
    frac: float


class Toast(NamedTuple):
    kind: str
    text: str
    sub: str


def _get(d, k):
    return d.get(k) if isinstance(d, dict) else None


def norm_card(c):
    if isinstance(c, Card):
        return c
    if not isinstance(c, dict):
        return None
    left = _f(_get(c, 'time_left'), 0.0, 0.0, 999.9)
    left = math.ceil(round(left * 10, 6)) / 10
    limit = _f(_get(c, 'time_limit'), 0.0, 0.0, 1e6)
    return Card(_i(_get(c, 'index'), 0, 0, 999), _i(_get(c, 'total'), 0, 0, 999),
                _s(_get(c, 'title')), _s(_get(c, 'tip')), left, limit if limit > 0 else max(left, 1.0),
                _i(_get(c, 'made')), _i(_get(c, 'taken')),
                bool(_get(c, 'urgent')), bool(_get(c, 'extended')))


def norm_countdown(c):
    if isinstance(c, Countdown):
        return c
    if not isinstance(c, dict):
        return None
    return Countdown(_i(_get(c, 'n'), 0, 0, 99), round(_f(_get(c, 'frac'), 0.0, 0.0, 1.0) * 500) / 500)


def norm_toast(t):
    if isinstance(t, Toast):
        return t
    if not isinstance(t, dict):
        return None
    t = Toast(_s(_get(t, 'kind')).lower(), _s(_get(t, 'text')).upper(), _s(_get(t, 'sub')))
    return t if (t.text or t.sub) else None


def normalize(state):
    """Untrusted state dict -> {element: hashable content or None}. Idempotent."""
    st = state if isinstance(state, dict) else {}
    return {'card': norm_card(st.get('card')), 'countdown': norm_countdown(st.get('countdown')),
            'toast': norm_toast(st.get('toast')), 'controls': True if st.get('controls') else None}


# ---------------------------------------------------------------------------- renderers
# Each returns (RGBA image, anchor): anchor is the panel's top-left inside the image
# (circle centre for the countdown). layout() turns that into a client position.

def _label_row(im, x, y, w, text, s, pill=None):
    """Tracked accent label vertically centred (by cap height) in a 20px row, optional right pill."""
    f = F('D6', 15, s)
    row = 20 * s
    base = y + (row + _cap(f)) / 2
    _text(im, x, base, text, f, ACCENT, track=2.5 * s)
    if pill:
        pf = F('D6', 12, s)
        pw = _tw(pill, pf, 1.5 * s) + 16 * s
        px = x + w - pw
        _rr(im, px, y, pw, row, row / 2, ACCENT)
        _text(im, px + 8 * s, y + (row + _cap(pf)) / 2, pill, pf, INK, track=1.5 * s)
    return row


def _runs(im, x, base, runs, maxw):
    """Baseline-aligned text runs [(text, font key, px, colour, tabular[, lead gap px])], shrunk to fit maxw.
    (A numeric lead because Barlow has no hair/thin space glyphs.)"""
    k = 1.0
    while True:
        widths = [r[5] * k if len(r) > 5 else 0 for r in runs]
        widths = [g + _tw(r[0], F(r[1], r[2] * k, 1), 0, r[4]) for g, r in zip(widths, runs)]
        if sum(widths) <= maxw or k < 0.3:
            break
        k *= 0.92
    for (t, fk, px, col, tab, *lead), w in zip(runs, widths):
        _text(im, x + (lead[0] * k if lead else 0), base, t, F(fk, px * k, 1), col, tab=tab)
        x += w


def render_card(card, s=1.0):
    c = card if isinstance(card, Card) else norm_card(card) or norm_card({})
    W, pad = round(440 * s), 24 * s
    inner = W - 2 * pad
    f_title, f_tip = F('D7', 34, s), F('B4', 16, s)
    f_lab = F('D6', 12, s)
    titles = _wrap(c.title, f_title, inner, 2)
    tips = _wrap(c.tip, f_tip, inner, 3)

    # layout pass (top of each block)
    y = pad + 20 * s
    y_title = y + 6 * s
    if titles:
        y = y_title + 38 * s * len(titles)
    y_tip = y + 10 * s
    if tips:
        y = y_tip + 23 * s * len(tips)
    y_div = y + 18 * s
    y_stats = y_div + max(1, round(s)) + 16 * s
    H = round(y_stats + 16 * s + 40 * s + pad)

    m = _margin(s)
    im = _panel(W, H, round(16 * s), s).copy()
    ox, oy = m + pad, m

    label = f'SHOT {c.index:02d} / {c.total:02d}' if c.total else f'SHOT {c.index:02d}'
    _label_row(im, ox, oy + pad, inner, label, s, 'KEEP-UP' if c.extended else None)
    for i, line in enumerate(titles):
        _text(im, ox, oy + y_title + 38 * s * i + _baseline(f_title, 38 * s), line, f_title, TEXT)
    for i, line in enumerate(tips):
        _text(im, ox, oy + y_tip + 23 * s * i + _baseline(f_tip, 23 * s), line, f_tip, MUTED)
    _hline(im, ox, oy + y_div, inner, WHITE + (20,), s)

    col = inner / 3
    lab_base = oy + y_stats + _baseline(f_lab, 16 * s)
    val_base = oy + y_stats + 16 * s + _baseline(F('D7', 40, s), 40 * s)
    rate = f'{min(999, round(100 * c.made / c.taken))}%' if c.taken else '—'
    tcol = ACCENT if c.urgent else TEXT
    values = [
        ('TIME LEFT', [(f'{c.left:.1f}'[:-1], 'D7', 40 * s, tcol, False), (f'{c.left:.1f}'[-1], 'D7', 40 * s, tcol, True),
                       ('s', 'D6', 20 * s, MUTED, False, 2 * s)]),
        ('GOALS', [(str(c.made), 'D7', 40 * s, TEXT, False), (f' / {c.taken}', 'D6', 20 * s, MUTED, False)]),
        ('RATE', [(rate, 'D7', 40 * s, TEXT, False)]),
    ]
    for i, (lab, runs) in enumerate(values):
        x = ox + col * i
        _text(im, x, lab_base, lab, f_lab, LABEL, track=1.8 * s)
        _runs(im, x, val_base, runs, col - 8 * s)

    # time bar: flush with the bottom edge, clipped by the card's rounded corners
    bh = max(1, round(4 * s))
    clip = np.asarray(_rr_mask(W, H, round(16 * s)), np.float32)[H - bh:] / 255
    fill_w = W * min(1.0, c.left / c.limit)
    cover = np.clip(fill_w - np.arange(W, dtype=np.float32), 0, 1)
    _paint(im, Image.fromarray((clip * 255).round().astype(np.uint8)), m, m + H - bh, WHITE + (31,))  # 12%: the empty bar must still read as a bar
    _paint(im, Image.fromarray((clip * cover * 255).round().astype(np.uint8)), m, m + H - bh, ACCENT)
    return im, (m, m)


_RING_R, _RING_W = 70, 6


@lru_cache(maxsize=4)
def _countdown_base(s):
    """Static part of the countdown: circle panel, ring track and the GET READY label."""
    D, m = round(168 * s), _margin(s)
    f_lab = F('D6', 15, s)
    panel = _panel(D, D, D / 2, s)
    gap_top = m + D + 18 * s
    im = Image.new('RGBA', (panel.width, max(panel.height, math.ceil(gap_top + _cap(f_lab) + 32 * s))))  # room for the glow
    im.alpha_composite(panel)
    c, r, sw = m + D / 2, _RING_R * s, _RING_W * s

    def ring(d, T, k):
        d.ellipse((*T(c - r - sw / 2, c - r - sw / 2), *T(c + r + sw / 2, c + r + sw / 2, 1)), outline=255, width=round(sw * k))
    _shape(im, (c - r - sw, c - r - sw, c + r + sw, c + r + sw), WHITE + (26,), ring)
    label = 'GET READY'
    _text(im, c - _tw(label, f_lab, 3 * s) / 2, gap_top + _cap(f_lab), label, f_lab, MUTED, track=3 * s,
          shadows=((10 * s, 90, 0), (4 * s, 200, s)))
    return im, c


def render_countdown(cd, s=1.0, pop=1.0):
    """pop: 0 -> 1 progress of the number's pop-in (scale 1.12 -> 1.0, fade in)."""
    c = cd if isinstance(cd, Countdown) else norm_countdown(cd) or Countdown(0, 0.0)
    base, cx = _countdown_base(s)
    im, cy = base.copy(), cx
    r, sw = _RING_R * s, _RING_W * s
    if c.frac > 0:
        end = -90 + 360 * c.frac

        def arc(d, T, k):  # accent arc from 12 o'clock, clockwise, with round caps
            d.arc((*T(cx - r - sw / 2, cy - r - sw / 2), *T(cx + r + sw / 2, cy + r + sw / 2, 1)), -90, end, fill=255, width=round(sw * k))
            for a in (-90, end):
                px, py = cx + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a))
                d.ellipse((*T(px - sw / 2, py - sw / 2), *T(px + sw / 2, py + sw / 2, 1)), fill=255)
        _shape(im, (cx - r - sw, cy - r - sw, cx + r + sw, cy + r + sw), ACCENT, arc, fast=True)  # redrawn every frame

    e = _ease(max(0.0, min(1.0, pop)))
    f_num = F('D7', 88 * (1.12 - 0.12 * e), s)
    num = str(c.n)
    l, t, rr, b = _ink(num, f_num)  # centre the glyphs, not the em box
    _text(im, cx - (l + rr) / 2, cy - (t + b) / 2, num, f_num, TEXT, alpha=round(255 * e))
    return im, (cx, cy)


_DOT = {'goal': GOOD, 'miss': FAINT, 'conceded': BAD, 'replay': ACCENT, 'clip': GOOD, 'error': BAD}


def render_toast(toast, s=1.0, pulse=0.0):
    """pulse: 0..1 phase of the replay dot's ping."""
    t = toast if isinstance(toast, Toast) else norm_toast(toast) or Toast('', '', '')
    return _toast_img(t, s, round(max(0.0, min(1.0, pulse)) * 20) / 20)


@lru_cache(maxsize=48)
def _toast_img(t, s, pulse):
    H, pad = round(64 * s), 28 * s
    f_txt, f_sub = F('D7', 28, s), F('B5', 16, s)
    tr = 2 * s
    icon = (18 if t.kind == 'clip' else 10) * s
    fixed = 2 * pad + icon + 14 * s
    text = _ellipsize(t.text, f_txt, 900 * s - fixed, tr)
    tw = _tw(text, f_txt, tr)
    sub = ''
    if t.sub:
        room = 900 * s - fixed - tw - 33 * s
        sub = _ellipsize(t.sub, f_sub, room) if room > 40 * s else ''
    W = round(fixed + tw + (33 * s + _tw(sub, f_sub) if sub else 0))
    m = _margin(s)
    im = _panel(W, H, H / 2, s).copy()
    x, mid = m + pad, m + H / 2
    col = _DOT.get(t.kind, FAINT)

    if t.kind == 'clip':
        def check(d, T, k):
            pts = [T(x + icon * a, mid + icon * b) for a, b in ((0.08, 0.02), (0.38, 0.32), (0.92, -0.3))]
            d.line(pts, fill=255, width=round(3.2 * s * k), joint='curve')
            for p in (pts[0], pts[-1]):
                rad = 1.6 * s * k
                d.ellipse((p[0] - rad, p[1] - rad, p[0] + rad, p[1] + rad), fill=255)
        _shape(im, (x, mid - icon, x + icon, mid + icon), col, check)
    else:
        dot = 5 * s
        if t.kind == 'replay' and pulse > 0:
            halo = dot + 7 * s * pulse

            def ping(d, T, k):
                d.ellipse((*T(x + dot - halo, mid - halo), *T(x + dot + halo, mid + halo, 1)), fill=255)
            _shape(im, (x + dot - halo, mid - halo, x + dot + halo, mid + halo), col + (round(90 * (1 - pulse)),), ping)

        def disc(d, T, k):
            d.ellipse((*T(x, mid - dot), *T(x + 2 * dot, mid + dot, 1)), fill=255)
        _shape(im, (x, mid - dot, x + 2 * dot, mid + dot), col, disc)

    x += icon + 14 * s
    _text(im, x, mid + _cap(f_txt) / 2, text, f_txt, GOOD if t.kind == 'goal' else TEXT, track=tr)
    if sub:
        x += tw + 16 * s
        _paint(im, Image.new('L', (max(1, round(s)), round(24 * s)), 255), round(x), round(mid - 12 * s), WHITE + (36,))
        x += 17 * s
        _text(im, x, mid + _cap(f_sub) / 2, sub, f_sub, MUTED)
    return im, (m, m)


CONTROLS = (('r3', 'Retry shot'), ('left', 'Previous shot'), ('right', 'Next shot'),
            ('up', 'Save a clip of the last 20 s'), ('menu', 'Hide controls'))
CONTROLS_FOOTER = 'Difficulty, bot skill and shots: Optimiser\u00a0\u203a\u00a0Shot\u00a0Trainer'


def _glyph(im, kind, cx, cy, s):
    if kind == 'r3':
        f = F('D7', 16, s)
        l, t, r, b = _ink('R3', f)
        _text(im, cx - (l + r) / 2, cy - (t + b) / 2, 'R3', f, TEXT)
    elif kind == 'menu':
        def lines(d, T, k):
            for dy in (-5, 0, 5):
                y = round(cy + dy * s)
                d.rounded_rectangle((*T(cx - 7 * s, y - s), *T(cx + 7 * s, y + s, 1)), radius=k * s, fill=255)
        _shape(im, (cx - 8 * s, cy - 7 * s, cx + 8 * s, cy + 7 * s), TEXT, lines)
    else:  # d-pad: a neutral plus with the pressed arm in accent, sharing the plus's own AA edge
        R, h = 11 * s, 3 * s

        def plus(d, T, k):
            d.rounded_rectangle((*T(cx - R, cy - h), *T(cx + R, cy + h, 1)), radius=1.5 * s * k, fill=255)
            d.rounded_rectangle((*T(cx - h, cy - R), *T(cx + h, cy + R, 1)), radius=1.5 * s * k, fill=255)
        mask, x0, y0 = _ss_mask((cx - R, cy - R, cx + R, cy + R), plus)
        a = np.asarray(mask)
        lo, hi = round(cx - h - x0), round(cx + h - x0)  # centre square edges in mask pixels
        lit = np.zeros_like(a)
        if kind == 'left':
            lit[:, :lo] = a[:, :lo]
        elif kind == 'right':
            lit[:, hi:] = a[:, hi:]
        else:
            lit[:round(cy - h - y0)] = a[:round(cy - h - y0)]
        _paint(im, Image.fromarray(a - lit), x0, y0, WHITE + (82,))
        _paint(im, Image.fromarray(lit), x0, y0, ACCENT)


@lru_cache(maxsize=4)
def _controls_img(s):
    W, pad = round(400 * s), 24 * s
    inner = W - 2 * pad
    f_act, f_foot = F('B5', 17, s), F('B4', 14, s)
    foot = _wrap(CONTROLS_FOOTER, f_foot, inner, 3)
    row, sep = 52 * s, max(1, round(s))
    y_rows = pad + 20 * s + 16 * s
    y_foot = y_rows + len(CONTROLS) * row + (len(CONTROLS) - 1) * sep + 14 * s
    H = round(y_foot + 20 * s * len(foot) + pad)
    m = _margin(s)
    im = _panel(W, H, round(16 * s), s).copy()
    ox, oy = m + pad, m
    _label_row(im, ox, oy + pad, inner, 'TRAINER CONTROLS', s)
    for i, (glyph, action) in enumerate(CONTROLS):
        top = oy + y_rows + i * (row + sep)
        if i:
            _hline(im, ox, top - sep, inner, WHITE + (15,), s)
        mid = top + row / 2
        cw, ch = 48 * s, 34 * s
        _rr(im, ox, mid - ch / 2, cw, ch, 9 * s, WHITE + (20,), WHITE + (36,), max(1, round(s)))
        _glyph(im, glyph, round(ox + cw / 2), round(mid), s)
        _text(im, ox + cw + 16 * s, mid + _cap(f_act) / 2, action, f_act, TEXT)
    for i, line in enumerate(foot):
        _text(im, ox, oy + y_foot + 20 * s * i + _baseline(f_foot, 20 * s), line, f_foot, LABEL)
    return im, (m, m)


def render_controls(s=1.0):
    return _controls_img(s)


def layout(name, img, anchor, cw, ch):
    """Top-left of the element's image in client coordinates (fully shown, no slide)."""
    s = ch / 1080
    ax, ay = anchor
    pw, ph = img.width - 2 * ax, img.height - 2 * ay
    if name == 'card':
        x, y = 40 * s - ax, 40 * s - ay
    elif name == 'countdown':
        x, y = cw / 2 - ax, 470 * s - ay
    elif name == 'toast':
        x, y = (cw - pw) / 2 - ax, 110 * s - ay
    else:
        x, y = cw - 40 * s - pw - ax, (ch - ph) / 2 - ay
    return round(x), round(y)


# ---------------------------------------------------------------------------- animation (platform independent)

class _Elem:
    def __init__(self, fade_in, fade_out, swap_out, swap_in):
        self.fade_in, self.fade_out, self.swap_out, self.swap_in = fade_in, fade_out, swap_out, swap_in
        self.shown = None      # content currently on screen (may be fading out)
        self.p = 0.0           # 0 = gone, 1 = fully in
        self.entering = False
        self.swapping = False
        self.key = None
        self.img = self.anchor = None
        self.pop_n, self.pop_t0 = None, 0.0
        self.render_t = -1e9


def _ident(name, v):
    if name == 'card':
        return v.index, v.total
    return v if name == 'toast' else True


class Scene:
    """Turns successive states into per-element draw commands (image, x, y, alpha 0-255)."""
    ORDER = ('card', 'countdown', 'toast', 'controls')

    def __init__(self):
        self.e = {'card': _Elem(0.20, 0.20, 0.075, 0.075), 'countdown': _Elem(0.18, 0.18, 0.1, 0.1),
                  'toast': _Elem(0.22, 0.18, 0.12, 0.22), 'controls': _Elem(0.20, 0.15, 0.1, 0.1)}
        self.t = None

    def frame(self, state, now, cw, ch):
        dt = 0.0 if self.t is None else min(0.05, max(0.0, now - self.t))
        self.t = now
        st = normalize(state)
        s = (ch / 1080) if ch > 0 else 1.0
        cmds, busy = {}, False
        for name in self.ORDER:
            e, tgt = self.e[name], st[name]
            if tgt is not None and e.shown is not None and _ident(name, tgt) != _ident(name, e.shown):
                e.entering = False
                e.p = max(0.0, e.p - dt / e.swap_out)
                if e.p == 0:
                    e.shown, e.entering, e.swapping = tgt, True, True
            elif tgt is not None:
                if e.shown is None:
                    e.p, e.entering, e.swapping = 0.0, True, False
                e.shown = tgt
                e.p = min(1.0, e.p + dt / (e.swap_in if e.swapping else e.fade_in))
                if e.p == 1:
                    e.entering = e.swapping = False
            elif e.shown is not None:
                e.entering = False
                e.p = max(0.0, e.p - dt / e.fade_out)
                if e.p == 0:
                    e.shown = None
            if e.shown is None:
                cmds[name] = None
                continue
            busy |= 0 < e.p < 1

            v = e.shown
            if name == 'card':
                key, job = (s, v), lambda: render_card(v, s)
            elif name == 'countdown':
                if v.n != e.pop_n:
                    e.pop_n, e.pop_t0 = v.n, now
                pop = round(min(1.0, (now - e.pop_t0) / 0.18), 2)
                busy = True
                key, job = (s, v, pop), lambda: render_countdown(v, s, pop)
            elif name == 'toast':
                pulse = (now % 1.4) / 1.4 if v.kind == 'replay' else 0.0
                busy |= v.kind == 'replay'
                key, job = (s, v, round(pulse * 20)), lambda: render_toast(v, s, pulse)
            else:
                key, job = (s,), lambda: render_controls(s)
            if key != e.key:
                e.img, e.anchor = job()
                e.key, e.render_t = key, now
            if name == 'card':  # a running clock ticks every 0.1 s: sample it at 60 Hz so ticks land evenly
                busy |= now - e.render_t < 0.25

            a = _ease(e.p)
            x, y = layout(name, e.img, e.anchor, cw, ch)
            if e.entering and name == 'toast':
                y -= round(12 * s * (1 - a))
            if e.entering and name == 'controls':
                x += round(16 * s * (1 - a))
            alpha = round(255 * a)
            cmds[name] = (e.img, x, y, alpha) if alpha > 0 else None
        return cmds, busy


# ---------------------------------------------------------------------------- pixels for Win32

def premultiply_bgra(img):
    """RGBA image -> C-contiguous premultiplied BGRA array (what UpdateLayeredWindow wants).
    Pillow's BGRa packer premultiplies and swaps channels in one C pass."""
    return np.frombuffer(img.tobytes('raw', 'BGRa'), np.uint8).reshape(img.height, img.width, 4)


# ---------------------------------------------------------------------------- Win32

class _Win32:
    """All windows belong to the overlay thread; every method runs on that thread."""
    EX = 0x00080000 | 0x20 | 0x8 | 0x80 | 0x08000000  # LAYERED|TRANSPARENT|TOPMOST|TOOLWINDOW|NOACTIVATE
    WS_POPUP = 0x80000000

    def __init__(self, names):
        import ctypes
        self.c = c = ctypes
        P, U32, I, B = c.c_void_p, c.c_uint32, c.c_int, c.c_int

        class POINT(c.Structure):
            _fields_ = [('x', c.c_int32), ('y', c.c_int32)]

        class SIZE(c.Structure):
            _fields_ = [('cx', c.c_int32), ('cy', c.c_int32)]

        class RECT(c.Structure):
            _fields_ = [('left', c.c_int32), ('top', c.c_int32), ('right', c.c_int32), ('bottom', c.c_int32)]

        class BLEND(c.Structure):
            _fields_ = [('BlendOp', c.c_ubyte), ('BlendFlags', c.c_ubyte),
                        ('SourceConstantAlpha', c.c_ubyte), ('AlphaFormat', c.c_ubyte)]

        class BIH(c.Structure):
            _fields_ = [('biSize', U32), ('biWidth', c.c_int32), ('biHeight', c.c_int32), ('biPlanes', c.c_uint16),
                        ('biBitCount', c.c_uint16), ('biCompression', U32), ('biSizeImage', U32),
                        ('biXPelsPerMeter', c.c_int32), ('biYPelsPerMeter', c.c_int32),
                        ('biClrUsed', U32), ('biClrImportant', U32)]

        class BMI(c.Structure):
            _fields_ = [('bmiHeader', BIH), ('bmiColors', U32 * 3)]

        WNDPROC = c.WINFUNCTYPE(c.c_ssize_t, P, c.c_uint, c.c_size_t, c.c_ssize_t)
        ENUMPROC = c.WINFUNCTYPE(B, P, c.c_ssize_t)

        class WNDCLASSEXW(c.Structure):
            _fields_ = [('cbSize', c.c_uint), ('style', c.c_uint), ('lpfnWndProc', WNDPROC),
                        ('cbClsExtra', I), ('cbWndExtra', I), ('hInstance', P), ('hIcon', P), ('hCursor', P),
                        ('hbrBackground', P), ('lpszMenuName', c.c_wchar_p), ('lpszClassName', c.c_wchar_p),
                        ('hIconSm', P)]

        class MSG(c.Structure):
            _fields_ = [('hwnd', P), ('message', c.c_uint), ('wParam', c.c_size_t), ('lParam', c.c_ssize_t),
                        ('time', U32), ('pt', POINT), ('lPrivate', U32)]

        self.POINT, self.SIZE, self.RECT, self.BLEND, self.BIH, self.BMI, self.MSG = POINT, SIZE, RECT, BLEND, BIH, BMI, MSG
        u = c.WinDLL('user32', use_last_error=True)
        g = c.WinDLL('gdi32', use_last_error=True)
        k = c.WinDLL('kernel32', use_last_error=True)

        def sig(fn, res, *args):
            fn.restype, fn.argtypes = res, list(args)
            return fn
        pP, pS, pB, pR, pM = c.POINTER(POINT), c.POINTER(SIZE), c.POINTER(BLEND), c.POINTER(RECT), c.POINTER(MSG)
        self.RegisterClassExW = sig(u.RegisterClassExW, c.c_uint16, c.POINTER(WNDCLASSEXW))
        self.UnregisterClassW = sig(u.UnregisterClassW, B, c.c_wchar_p, P)
        self.CreateWindowExW = sig(u.CreateWindowExW, P, U32, c.c_wchar_p, c.c_wchar_p, U32, I, I, I, I, P, P, P, P)
        self.DestroyWindow = sig(u.DestroyWindow, B, P)
        self.DefWindowProcW = sig(u.DefWindowProcW, c.c_ssize_t, P, c.c_uint, c.c_size_t, c.c_ssize_t)
        self.ShowWindow = sig(u.ShowWindow, B, P, I)
        self.SetWindowPos = sig(u.SetWindowPos, B, P, P, I, I, I, I, c.c_uint)
        self.UpdateLayeredWindow = sig(u.UpdateLayeredWindow, B, P, P, pP, pS, P, pP, U32, pB, U32)
        self.GetDC = sig(u.GetDC, P, P)
        self.ReleaseDC = sig(u.ReleaseDC, I, P, P)
        self.PeekMessageW = sig(u.PeekMessageW, B, pM, P, c.c_uint, c.c_uint, c.c_uint)
        self.TranslateMessage = sig(u.TranslateMessage, B, pM)
        self.DispatchMessageW = sig(u.DispatchMessageW, c.c_ssize_t, pM)
        self.EnumWindows = sig(u.EnumWindows, B, ENUMPROC, c.c_ssize_t)
        self.GetWindowThreadProcessId = sig(u.GetWindowThreadProcessId, U32, P, c.POINTER(U32))
        self.IsWindow = sig(u.IsWindow, B, P)
        self.IsWindowVisible = sig(u.IsWindowVisible, B, P)
        self.IsIconic = sig(u.IsIconic, B, P)
        self.GetForegroundWindow = sig(u.GetForegroundWindow, P)
        self.GetClientRect = sig(u.GetClientRect, B, P, pR)
        self.ClientToScreen = sig(u.ClientToScreen, B, P, pP)
        self.GetClassNameW = sig(u.GetClassNameW, I, P, c.c_wchar_p, I)
        self.CreateCompatibleDC = sig(g.CreateCompatibleDC, P, P)
        self.CreateDIBSection = sig(g.CreateDIBSection, P, P, c.POINTER(BMI), c.c_uint, c.POINTER(P), P, U32)
        self.SelectObject = sig(g.SelectObject, P, P, P)
        self.DeleteObject = sig(g.DeleteObject, B, P)
        self.DeleteDC = sig(g.DeleteDC, B, P)
        self.GetModuleHandleW = sig(k.GetModuleHandleW, P, c.c_wchar_p)
        self.OpenProcess = sig(k.OpenProcess, P, U32, B, U32)
        self.QueryFullProcessImageNameW = sig(k.QueryFullProcessImageNameW, B, P, U32, c.c_wchar_p, c.POINTER(U32))
        self.CloseHandle = sig(k.CloseHandle, B, P)
        self.ENUMPROC = ENUMPROC

        # Physical pixels on Peter's 125 % desktop. Must precede window creation.
        dpi_ok = False
        try:
            dpi_ok = bool(sig(u.SetProcessDpiAwarenessContext, B, P)(P(-4)))
        except AttributeError:
            pass
        if not dpi_ok:
            try:
                sh = c.WinDLL('shcore')
                dpi_ok = sig(sh.SetProcessDpiAwareness, c.c_long, I)(2) == 0
            except (OSError, AttributeError):
                pass
        if not dpi_ok:  # process awareness already fixed elsewhere: still make this thread PMv2
            try:
                sig(u.SetThreadDpiAwarenessContext, P, P)(P(-4))
            except AttributeError:
                pass

        def wndproc(hwnd, msg, wp, lp):
            if msg == 0x84:      # WM_NCHITTEST -> HTTRANSPARENT
                return -1
            if msg == 0x21:      # WM_MOUSEACTIVATE -> MA_NOACTIVATE
                return 3
            return self.DefWindowProcW(hwnd, msg, wp, lp)
        self._wndproc = WNDPROC(wndproc)  # must outlive the windows
        self.hinst = self.GetModuleHandleW(None)
        self.cls = f'OptimiserHud{os.getpid()}_{id(self)}'
        wc = WNDCLASSEXW()
        wc.cbSize = c.sizeof(WNDCLASSEXW)
        wc.lpfnWndProc = self._wndproc
        wc.hInstance = self.hinst
        wc.lpszClassName = self.cls
        self._wc = wc
        if not self.RegisterClassExW(c.byref(wc)):
            raise OSError(f'RegisterClassExW failed ({c.get_last_error()})')
        self.registered, self.win = True, {}
        try:
            for n in names:
                h = self.CreateWindowExW(self.EX, self.cls, 'Optimiser HUD', self.WS_POPUP, 0, 0, 1, 1, None, None, self.hinst, None)
                if not h:
                    raise OSError(f'CreateWindowExW failed ({c.get_last_error()})')
                self.win[n] = {'hwnd': h, 'img': None, 'pos': None, 'a': None, 'shown': False}
        except Exception:
            self.destroy()
            raise
        self.rl = None
        self.ulw_warned = False

    # -- Rocket League tracking
    def _is_rl(self, hwnd, cache):
        pid = self.c.c_uint32()
        self.GetWindowThreadProcessId(hwnd, self.c.byref(pid))
        if pid.value not in cache:
            name = None
            hp = self.OpenProcess(0x1000, False, pid.value)  # PROCESS_QUERY_LIMITED_INFORMATION
            if hp:
                buf, n = self.c.create_unicode_buffer(1024), self.c.c_uint32(1024)
                if self.QueryFullProcessImageNameW(hp, 0, buf, self.c.byref(n)):
                    name = os.path.basename(buf.value).lower()
                self.CloseHandle(hp)
            if name is None:  # access denied: fall back to Unreal's window class
                cb = self.c.create_unicode_buffer(256)
                self.GetClassNameW(hwnd, cb, 256)
                name = 'rocketleague.exe' if cb.value == 'LaunchUnrealUWindowsClient' else ''
            cache[pid.value] = name == 'rocketleague.exe'
        return cache[pid.value]

    def client_area(self, hwnd):
        # Both corners through ClientToScreen: GetClientRect is in the target's own (possibly
        # DPI-virtualised) units, ClientToScreen maps into our physical ones.
        rc = self.RECT()
        if not self.GetClientRect(hwnd, self.c.byref(rc)):
            return None
        tl, br = self.POINT(0, 0), self.POINT(rc.right, rc.bottom)
        if not (self.ClientToScreen(hwnd, self.c.byref(tl)) and self.ClientToScreen(hwnd, self.c.byref(br))):
            return None
        return tl.x, tl.y, br.x - tl.x, br.y - tl.y

    def find_rl(self):
        if self.rl and self.IsWindow(self.rl) and self._is_rl(self.rl, {}):
            return self.rl
        best, cache = [None, 0], {}

        def cb(hwnd, _):
            try:
                if hwnd and self.IsWindowVisible(hwnd) and self._is_rl(hwnd, cache):
                    area = self.client_area(hwnd)
                    if area and area[2] * area[3] > best[1]:
                        best[:] = [hwnd, area[2] * area[3]]
            except Exception:
                pass
            return True
        self.EnumWindows(self.ENUMPROC(cb), 0)
        self.rl = best[0]
        return self.rl

    def rl_in_front(self):
        if not self.rl or self.IsIconic(self.rl):
            return False
        fg = self.GetForegroundWindow()
        return fg == self.rl or any(fg == w['hwnd'] for w in self.win.values())

    def _top(self, hwnd, show=False):  # HWND_TOPMOST; SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE[|SWP_SHOWWINDOW]
        self.SetWindowPos(hwnd, self.c.c_void_p(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | (0x40 if show else 0))

    def raise_all(self):
        for w in self.win.values():
            self._top(w['hwnd'])

    # -- drawing
    def _blend(self, a):
        return self.BLEND(0, 0, max(0, min(255, a)), 1)  # AC_SRC_OVER, AC_SRC_ALPHA

    def _upload(self, hwnd, img, x, y, a):
        c = self.c
        bgra = premultiply_bgra(img)
        h, w = bgra.shape[:2]
        bmi = self.BMI()
        bmi.bmiHeader.biSize = c.sizeof(self.BIH)
        bmi.bmiHeader.biWidth, bmi.bmiHeader.biHeight = w, -h  # negative height = top-down rows
        bmi.bmiHeader.biPlanes, bmi.bmiHeader.biBitCount = 1, 32
        screen = self.GetDC(None)
        mem = self.CreateCompatibleDC(screen)
        bits, hbmp, old, ok = c.c_void_p(), None, None, False
        try:
            hbmp = self.CreateDIBSection(screen, c.byref(bmi), 0, c.byref(bits), None, 0)
            if hbmp and bits.value and mem:
                c.memmove(bits.value, bgra.ctypes.data, w * h * 4)
                old = self.SelectObject(mem, hbmp)
                ok = self.UpdateLayeredWindow(hwnd, screen, c.byref(self.POINT(x, y)), c.byref(self.SIZE(w, h)), mem,
                                              c.byref(self.POINT(0, 0)), 0, c.byref(self._blend(a)), 2)  # ULW_ALPHA
        finally:
            if old:
                self.SelectObject(mem, old)
            if hbmp:
                self.DeleteObject(hbmp)
            if mem:
                self.DeleteDC(mem)
            if screen:
                self.ReleaseDC(None, screen)
        return ok

    def apply(self, cmds, origin, visible):
        c = self.c
        for name, cmd in cmds.items():
            w = self.win[name]
            if not visible or cmd is None:
                if w['shown']:
                    self.ShowWindow(w['hwnd'], 0)  # SW_HIDE
                    w['shown'], w['img'] = False, None
                continue
            img, x, y, a = cmd
            pos = (origin[0] + x, origin[1] + y)
            if img is not w['img']:
                ok = self._upload(w['hwnd'], img, pos[0], pos[1], a)
            elif pos != w['pos'] or a != w['a']:  # fade / slide only: no new pixels
                ok = self.UpdateLayeredWindow(w['hwnd'], None, c.byref(self.POINT(*pos)), None, None, None, 0,
                                              c.byref(self._blend(a)), 2)
            else:
                continue
            if not ok:
                if not self.ulw_warned:
                    print(f'hud_overlay: UpdateLayeredWindow failed ({c.get_last_error()})', file=sys.stderr)
                    self.ulw_warned = True
                w['img'] = None  # retry with a full upload next frame
                continue
            w['img'], w['pos'], w['a'] = img, pos, a
            if not w['shown']:  # show at the top of the topmost band (ShowWindow keeps the old z-order)
                self._top(w['hwnd'], show=True)
                w['shown'] = True

    def pump(self):
        msg = self.MSG()
        while self.PeekMessageW(self.c.byref(msg), None, 0, 0, 1):  # PM_REMOVE
            self.TranslateMessage(self.c.byref(msg))
            self.DispatchMessageW(self.c.byref(msg))

    def destroy(self):
        for w in self.win.values():
            try:
                self.DestroyWindow(w['hwnd'])
            except Exception:
                pass
        self.win = {}
        if getattr(self, 'registered', False):
            self.UnregisterClassW(self.cls, self.hinst)
            self.registered = False


# ---------------------------------------------------------------------------- public API

class Hud:
    def __init__(self):
        self.ok = False
        self._lock = threading.Lock()
        self._state = None
        self._stop = threading.Event()
        self._ready = threading.Event()
        self._thread = None
        if sys.platform != 'win32':
            return
        try:
            self.ok = True
            self._thread = threading.Thread(target=self._run, name='hud-overlay', daemon=True)
            self._thread.start()
            self._ready.wait(3.0)
        except Exception:
            self.ok = False

    def update(self, state):
        if self.ok:
            try:
                with self._lock:
                    self._state = state
            except Exception:
                pass

    def close(self):
        try:
            self._stop.set()
            if self._thread and self._thread is not threading.current_thread():
                self._thread.join(2.0)
        except Exception:
            pass
        self.ok = False

    def _run(self):
        w32 = None
        try:
            w32 = _Win32(Scene.ORDER)
            self._ready.set()
            scene = Scene()
            next_track, area, was_front = 0.0, None, False
            hidden = dict.fromkeys(Scene.ORDER)
            while not self._stop.is_set():
                w32.pump()
                now = time.perf_counter()
                if now >= next_track:
                    next_track = now + 0.5
                    area = w32.client_area(w32.rl) if w32.find_rl() else None
                    if was_front:
                        w32.raise_all()
                front = w32.rl_in_front()
                if front and (not was_front or not area or area[3] <= 0):  # just alt-tabbed in or restored
                    if not was_front:
                        w32.raise_all()
                    area = w32.client_area(w32.rl) or area
                was_front = front
                if not (front and area and area[3] > 0):  # nothing to show: don't render either
                    w32.apply(hidden, (0, 0), False)
                    scene.t = None
                    time.sleep(1 / 30)
                    continue
                with self._lock:  # normalise under the lock in case the caller mutates its dicts
                    state = normalize(self._state)
                cmds, busy = scene.frame(state, now, area[2], area[3])
                w32.apply(cmds, area[:2], True)
                time.sleep(max(0.0, (1 / 60 if busy else 1 / 30) - (time.perf_counter() - now)))
        except Exception:
            print('hud_overlay: disabled after an error:', file=sys.stderr)
            traceback.print_exc()
        finally:
            self.ok = False
            self._ready.set()
            if w32:
                try:
                    w32.destroy()
                except Exception:
                    pass


# ---------------------------------------------------------------------------- preview & selftest

TITLE = 'Ceiling-to-wall air dribble'
TIP = 'Meet it as it rolls down the wall, cushion it, then air dribble past the bot'


def _card(**kw):
    d = {'index': 7, 'total': 10, 'title': TITLE, 'tip': TIP, 'time_left': 9.4, 'time_limit': 16.0,
         'made': 11, 'taken': 14, 'urgent': False, 'extended': False}
    d.update(kw)
    return d


def compose(state, bg, pulse=0.35):
    out = bg.convert('RGBA')
    cw, ch = out.size
    s = ch / 1080
    st = normalize(state)
    parts = []
    if st['card']:
        parts.append(('card', render_card(st['card'], s)))
    if st['countdown']:
        parts.append(('countdown', render_countdown(st['countdown'], s)))
    if st['toast']:
        parts.append(('toast', render_toast(st['toast'], s, pulse)))
    if st['controls']:
        parts.append(('controls', render_controls(s)))
    for name, (img, anchor) in parts:
        _comp(out, img, *layout(name, img, anchor, cw, ch))
    return out.convert('RGB')


def preview(outdir):
    os.makedirs(outdir, exist_ok=True)
    bg = Image.open(os.path.join(HERE, 'preview_bg.jpg')).convert('RGB')
    shots = {
        'live': {'card': _card()},
        'urgent': {'card': _card(time_left=2.3, time_limit=19.5, urgent=True, extended=True)},
        'countdown': {'card': _card(time_left=16.0), 'countdown': {'n': 2, 'frac': 0.6}},
        'goal': {'card': _card(time_left=3.1, made=12, taken=15), 'toast': {'kind': 'goal', 'text': 'GOAL', 'sub': TITLE}},
        'missed': {'card': _card(time_left=0.0, taken=15), 'toast': {'kind': 'miss', 'text': 'MISSED', 'sub': TITLE}},
        'conceded': {'card': _card(time_left=4.2, taken=15), 'toast': {'kind': 'conceded', 'text': 'BOT SCORED', 'sub': 'Back post'}},
        'replay': {'card': _card(), 'toast': {'kind': 'replay', 'text': 'REPLAY', 'sub': TITLE}},
        'clip': {'card': _card(), 'toast': {'kind': 'clip', 'text': 'CLIP SAVED', 'sub': 'Last 20 s'}},
        'error': {'card': _card(), 'toast': {'kind': 'error', 'text': 'CLIP FAILED', 'sub': "OBS isn't open"}},
        'controls': {'card': _card(), 'controls': True},
        'longtext': {'card': _card(index=108, total=120, made=105, taken=120, time_left=12.6,
                                   title='Double-tap off the backboard into a flip reset and a delayed musty flick finish',
                                   tip='Jump early, keep your nose pointed at the ball the whole way up, take the backboard '
                                       'touch with your wheels, then hold the flip until the bot commits to the challenge '
                                       'and only then release it for the flick'),
                     'toast': {'kind': 'goal', 'text': 'GOAL', 'sub': 'An exceptionally long subtitle that must be cut '
                               'short with an ellipsis so the pill never grows wider than nine hundred pixels on screen'}},
    }
    thumbs = []
    for name, state in shots.items():
        im = compose(state, bg)
        im.save(os.path.join(outdir, f'{name}.png'))
        thumbs.append((name, im.resize((640, 360), Image.Resampling.LANCZOS)))
    sheet = Image.new('RGB', (3 * 640 + 4 * 8, 4 * 360 + 5 * 8), (24, 26, 30))
    d = ImageDraw.Draw(sheet)
    for i, (name, im) in enumerate(thumbs):
        x, y = 8 + (i % 3) * 648, 8 + (i // 3) * 368
        sheet.paste(im, (x, y))
        d.rectangle((x, y + 330, x + 96, y + 360), fill=(24, 26, 30))
        d.text((x + 8, y + 350), name, font=F('D6', 16, 1), fill=TEXT, anchor='ls')
    sheet.save(os.path.join(outdir, 'contact.png'))
    print('wrote', len(thumbs) + 1, 'images to', outdir)


def selftest():
    px = premultiply_bgra(Image.fromarray(np.array([[[255, 128, 0, 128], [10, 20, 30, 255], [200, 100, 50, 0]]], np.uint8), 'RGBA'))
    assert px[0, 0].tolist() == [0, 64, 128, 128], px[0, 0]
    assert px[0, 1].tolist() == [30, 20, 10, 255] and px[0, 2].tolist() == [0, 0, 0, 0]
    assert px.flags['C_CONTIGUOUS'] and px.dtype == np.uint8

    m = _margin(1)
    img, anc = render_card(_card(), 1)
    assert img.width == 440 + 2 * m and 200 < img.height - 2 * m < 400 and anc == (m, m), img.size
    img, _ = render_controls(1)
    assert img.width == 400 + 2 * m and img.height - 2 * m < 1080
    img, anc = render_countdown({'n': 3, 'frac': 0.5}, 1)
    assert img.width == 168 + 2 * m and anc == (m + 84, m + 84)
    img, _ = render_toast({'kind': 'goal', 'text': 'GOAL', 'sub': 'x' * 500}, 1)
    assert img.height == 64 + 2 * m and img.width - 2 * m <= 900, img.size

    f = F('D7', 34, 1)
    lines = _wrap('word ' * 200, f, 392, 2)
    assert len(lines) == 2 and lines[-1].endswith('…') and all(f.getlength(l) <= 392 for l in lines)
    lines = _wrap('Supercalifragilisticexpialidocious' * 5, f, 392, 3)
    assert len(lines) == 3 and all(f.getlength(l) <= 392 for l in lines)
    assert _wrap('', f, 392, 2) == () and _wrap('Short', f, 392, 2) == ('Short',)
    assert _ellipsize('fits', f, 392) == 'fits'

    odd = [None, {}, {'title': None, 'tip': '', 'taken': 0}, _card(time_left=50, time_limit=16),
           _card(time_left=-3, time_limit=0), _card(made=10 ** 12, taken=10 ** 15, index=10 ** 9, total=-4),
           _card(time_left=float('nan'), time_limit=float('inf'), title='\n\n', made='7', taken='x'),
           _card(title='W' * 400, tip='Ü' * 900)]
    for c in odd:
        render_card(c, 1)
        render_card(c, 0.75)
    nc = norm_card(_card(time_left=50, time_limit=16))
    assert nc.left == 50.0 and render_card(nc, 1)
    assert norm_card(_card(time_left=-3)).left == 0.0 and norm_card(_card(time_left=9.41)).left == 9.5
    for cd in (None, {}, {'n': None, 'frac': 5}, {'n': -1, 'frac': -2}, {'n': 10 ** 9, 'frac': 'x'}):
        render_countdown(cd, 1, pop=0)
        render_countdown(cd, 1.333, pop=0.5)
    for t in (None, {}, {'kind': None, 'text': None}, {'kind': 'weird', 'text': '', 'sub': 'only sub'},
              {'kind': 'clip', 'text': 'x' * 300}, {'kind': 'replay', 'text': 'REPLAY', 'sub': ''}):
        render_toast(t, 1, 0.5)
    render_controls(0.5)

    # animation: states in, fades, swaps and garbage never raise; alpha settles at 255
    sc, t = Scene(), 0.0
    seq = ([{'card': _card(time_left=16 - i * 0.05)} for i in range(30)]
           + [{'card': _card(index=8)}] * 20 + [{'card': _card(index=8), 'countdown': {'n': 3 - i // 10, 'frac': 1 - (i % 10) / 10}} for i in range(30)]
           + [{'card': _card(index=8), 'toast': {'kind': 'replay', 'text': 'REPLAY'}}] * 20
           + [{'card': _card(index=8), 'toast': {'kind': 'goal', 'text': 'GOAL'}, 'controls': True}] * 20
           + [None, 'garbage', {'card': 5, 'toast': [], 'countdown': 'x'}] * 5 + [{}] * 20)
    seen = set()
    for st in seq:
        t += 1 / 60
        cmds, _ = sc.frame(st, t, 1920, 1080)
        seen |= {n for n, cmd in cmds.items() if cmd and cmd[3] == 255}
        for cmd in cmds.values():
            assert cmd is None or (cmd[0].mode == 'RGBA' and 0 < cmd[3] <= 255)
    assert seen == set(Scene.ORDER), seen
    assert all(v is None for v in sc.frame({}, t + 1, 1920, 1080)[0].values())
    st = normalize({'card': _card(time_left=3.21), 'controls': 1})
    assert normalize(st) == st and st['card'].left == 3.3

    # performance contract and timings, 60 fps
    def play(sc, states):
        out = []
        for st in states:
            cmds, busy = sc.frame(st, (sc.t or 0.0) + 1 / 60, 1920, 1080)
            out.append((cmds, busy, sc.e['card'].key, sc.e['countdown'].key))
        return out

    def renders(fr):
        return sum(a[2] != b[2] for a, b in zip(fr, fr[1:]))
    sc = Scene()
    fr = play(sc, [{'card': _card(time_left=16 - i / 60)} for i in range(61)])
    assert 9 <= renders(fr) <= 11, renders(fr)  # one re-render per 0.1 s tick
    assert all(f[1] for f in fr[1:]), 'a running clock must keep the loop at 60 Hz'
    assert renders(play(sc, [{'card': _card()}] * 60)) == 0  # static state: no re-render
    img, x, y, a = play(sc, [{'card': _card()}])[0][0]['card']
    assert (x, y, a) == (40 - _margin(1), 40 - _margin(1), 255) and layout('card', img, (_margin(1),) * 2, 1920, 1080) == (x, y)

    fr = play(sc, [{'card': _card(index=8)}] * 30)  # index change: dip out, one empty frame, back in
    gone = [i for i, f in enumerate(fr) if f[0]['card'] is None]
    assert len(gone) == 1 and fr[-1][0]['card'][3] == 255, gone
    back = next(i for i, f in enumerate(fr) if f[0]['card'] and f[0]['card'][3] == 255)
    assert back <= 0.15 * 60 + 2, back

    fr = play(sc, [{'toast': {'kind': 'goal', 'text': 'GOAL'}}] * 30)
    ys = [f[0]['toast'][2] for f in fr if f[0]['toast']]
    full = next(i for i, f in enumerate(fr) if f[0]['toast'] and f[0]['toast'][3] == 255)
    assert full <= 0.22 * 60 + 1 and ys == sorted(ys) and 8 <= ys[-1] - ys[0] <= 12, (full, ys)
    fr = play(sc, [{}] * 30)
    assert next(i for i, f in enumerate(fr) if f[0]['toast'] is None) <= 0.18 * 60 + 1

    fr = play(sc, [{'countdown': {'n': 3 - i // 20, 'frac': 0.5}} for i in range(40)])
    assert fr[20][3][1].n == 2 and fr[20][3][2] == 0 and fr[19][3][2] == 1.0  # pop restarts on a new n
    for size in ((0, 0), (2560, 1440), (800, 450)):
        Scene().frame({'card': _card(), 'countdown': {'n': 1}, 'toast': {'text': 'x'}, 'controls': 1}, 1.0, *size)

    h = Hud()
    h.update({'card': _card()})
    h.close()
    assert sys.platform == 'win32' or not h.ok
    print('OK')


if __name__ == '__main__':
    if len(sys.argv) >= 3 and sys.argv[1] == '--preview':
        preview(sys.argv[2])
    elif len(sys.argv) >= 2 and sys.argv[1] == '--selftest':
        selftest()
    else:
        print(__doc__)
