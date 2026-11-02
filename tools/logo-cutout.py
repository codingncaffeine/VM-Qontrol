# Removes the baked-in checkerboard from the logo JPEG: background = low-saturation light pixels in
# connected regions that touch the border or are large (the holes inside the Q ring).
import sys
from collections import deque
from PIL import Image, ImageFilter
import numpy as np
src, out = sys.argv[1], sys.argv[2]
im = Image.open(src).convert('RGB'); a = np.asarray(im).astype(int)
h, w, _ = a.shape
sat = a.max(2) - a.min(2); lum = a.mean(2)
cand = (sat <= 14) & (lum >= 180)
lab = np.zeros((h, w), np.int32); keep = np.zeros((h, w), bool); n = 0
for y0 in range(h):
    for x0 in range(w):
        if not cand[y0, x0] or lab[y0, x0]: continue
        n += 1; q = deque([(y0, x0)]); lab[y0, x0] = n; pts = []; border = False
        while q:
            y, x = q.popleft(); pts.append((y, x))
            if y in (0, h-1) or x in (0, w-1): border = True
            for dy, dx in ((1,0),(-1,0),(0,1),(0,-1)):
                yy, xx = y+dy, x+dx
                if 0 <= yy < h and 0 <= xx < w and cand[yy, xx] and not lab[yy, xx]:
                    lab[yy, xx] = n; q.append((yy, xx))
        if border or len(pts) > 1500:
            for p in pts: keep[p] = True
bg = Image.fromarray((keep*255).astype(np.uint8))
bg = bg.filter(ImageFilter.MaxFilter(3))               # eat the light JPEG fringe
alpha = Image.fromarray(255 - np.asarray(bg)).filter(ImageFilter.GaussianBlur(0.8))
# crop to content with a small margin and pad to a square
al = np.asarray(alpha); ys, xs = np.nonzero(al > 8)
y1, y2, x1, x2 = ys.min(), ys.max()+1, xs.min(), xs.max()+1
rgba = im.copy(); rgba.putalpha(alpha); rgba = rgba.crop((x1, y1, x2, y2))
side = max(rgba.size) + 8
sq = Image.new('RGBA', (side, side), (0,0,0,0)); sq.paste(rgba, ((side-rgba.width)//2, (side-rgba.height)//2))
sq.save(out); print('saved', out, sq.size, 'components', n)
