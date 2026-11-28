# Draws the tutorial tips' pictograms of the mod (trigger, roll): python draw-icons.py <out folder>. Pillow only.
import sys, math
from PIL import Image, ImageDraw
out = sys.argv[1]
N = 1024
W = (255, 255, 255, 255)
ACC = (255, 176, 0, 255)
CLEAR = (0, 0, 0, 0)
LW = 60

def save(img, name):
    img.resize((256, 256), Image.LANCZOS).save(f"{out}/{name}.png")

def arrowhead(d, tip, ang, size, color):
    a1, a2 = ang + math.radians(150), ang - math.radians(150)
    d.polygon([tip, (tip[0] + size*math.cos(a1), tip[1] + size*math.sin(a1)), (tip[0] + size*math.cos(a2), tip[1] + size*math.sin(a2))], fill=color)

def dot(d, p, w, color):
    d.ellipse([p[0]-w/2, p[1]-w/2, p[0]+w/2, p[1]+w/2], fill=color)

# controller seen from the side, front to the left, as a filled silhouette; its button in the accent colour, straight,
# on the grip's front edge halfway down (Raik, 2026-11-27: not the curved trigger any more)
img = Image.new("RGBA", (N, N), CLEAR); d = ImageDraw.Draw(img)
# head
d.rounded_rectangle([260, 300, 760, 450], radius=75, fill=W)
# grip, slanting down and back
d.polygon([(560, 400), (740, 400), (800, 900), (660, 930)], fill=W)
dot(d, (730, 915), 140, W)
# thumbstick on top of the head
d.rectangle([600, 240, 640, 310], fill=W)
d.rounded_rectangle([565, 215, 675, 260], radius=20, fill=W)
# the button: a short straight bar (a third of what it was), halfway down the grip on its front edge, half in front of
# it and half on it (Raik, 2026-11-27), with a dark rim
top, bottom = (560, 400), (660, 930)
ax = (bottom[0] - top[0], bottom[1] - top[1]); n = math.hypot(*ax); ax = (ax[0]/n, ax[1]/n)
mid = (top[0] + ax[0]*0.5*n, top[1] + ax[1]*0.5*n)
half = 40
a = (mid[0] - ax[0]*half, mid[1] - ax[1]*half); b = (mid[0] + ax[0]*half, mid[1] + ax[1]*half)
for width, color in ((110, CLEAR), (74, ACC)):
    d.line([a, b], fill=color, width=width)
    dot(d, a, width, color); dot(d, b, width, color)
# press marks in front of the grip, pointing at the button
out_dir = (-ax[1], ax[0]) if -ax[1] < 0 else (ax[1], -ax[0])
for k in (-1, 0, 1):
    along = (mid[0] + ax[0]*k*90, mid[1] + ax[1]*k*90)
    tilt = math.radians(-k*18)
    dx = out_dir[0]*math.cos(tilt) - out_dir[1]*math.sin(tilt); dy = out_dir[0]*math.sin(tilt) + out_dir[1]*math.cos(tilt)
    d.line([(along[0] + dx*110, along[1] + dy*110), (along[0] + dx*200, along[1] + dy*200)], fill=ACC, width=44)
save(img, "trigger")

# roll: an arc bowing downwards, its heads pointing up left and up right
img = Image.new("RGBA", (N, N), CLEAR); d = ImageDraw.Draw(img)
c, r = (512, 180), 420
a0, a1 = 38, 142
d.arc([c[0]-r, c[1]-r, c[0]+r, c[1]+r], start=a0, end=a1, fill=W, width=LW + 10)
for a, way in ((a0, -1), (a1, 1)):
    ang = math.radians(a)
    end = (c[0] + r*math.cos(ang), c[1] + r*math.sin(ang))
    # along the circle away from its bottom: decreasing angle at the right end, increasing at the left
    t = (math.sin(ang), -math.cos(ang)) if way < 0 else (-math.sin(ang), math.cos(ang))
    t = (-t[0], -t[1]) if t[1] > 0 else t
    tip = (end[0] + 170*t[0], end[1] + 170*t[1])
    arrowhead(d, tip, math.atan2(t[1], t[0]), 230, W)
save(img, "roll")

prev = Image.new("RGBA", (256*3 + 40, 296), (20, 28, 36, 255))
for i, n in enumerate(("trigger", "roll")):
    prev.alpha_composite(Image.open(f"{out}/{n}.png"), (10 + i*266, 20))
prev.save(f"{out}/preview.png")
print("ok")
