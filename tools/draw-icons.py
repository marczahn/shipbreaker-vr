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

# controller seen from the side, front to the left, as a filled silhouette; the trigger in the accent colour,
# reaching up halfway into the head
img = Image.new("RGBA", (N, N), CLEAR); d = ImageDraw.Draw(img)
# head
d.rounded_rectangle([260, 300, 760, 450], radius=75, fill=W)
# grip, slanting down and back
d.polygon([(560, 400), (740, 400), (800, 900), (660, 930)], fill=W)
dot(d, (730, 915), 140, W)
# thumbstick on top of the head
d.rectangle([600, 240, 640, 310], fill=W)
d.rounded_rectangle([565, 215, 675, 260], radius=20, fill=W)
# trigger: a curved lever from inside the head's front, curling down and back towards the grip, with a dark rim
d.arc([340, 250, 700, 610], start=100, end=190, fill=CLEAR, width=145)
d.arc([355, 265, 685, 595], start=100, end=188, fill=ACC, width=105)
dot(d, (491, 590), 105, ACC)
# press marks in front of the trigger
c = (520, 430)
for a in (140, 165, 190):
    ang = math.radians(a)
    d.line([(c[0] + 290*math.cos(ang), c[1] + 290*math.sin(ang)), (c[0] + 380*math.cos(ang), c[1] + 380*math.sin(ang))], fill=ACC, width=44)
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
