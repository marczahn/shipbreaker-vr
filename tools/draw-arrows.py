# Draws the tutorial tips' thrust arrows in space (thrust_<direction>.png): python draw-arrows.py <out folder>. Pillow only.
import sys, math

class V(tuple):
    def __new__(cls, *a): return tuple.__new__(cls, a[0] if len(a) == 1 else a)
    def __add__(a, b): return V(a[0] + b[0], a[1] + b[1], a[2] + b[2])
    def __sub__(a, b): return V(a[0] - b[0], a[1] - b[1], a[2] - b[2])
    def __mul__(a, k): return V(a[0] * k, a[1] * k, a[2] * k)
    __rmul__ = __mul__
    def __truediv__(a, k): return V(a[0] / k, a[1] / k, a[2] / k)
    def __neg__(a): return V(-a[0], -a[1], -a[2])
    def __matmul__(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return V(a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a): return math.sqrt(a @ a)
def vsum(vs):
    r = V(0.0, 0.0, 0.0)
    for v in vs: r = r + v
    return r
from PIL import Image, ImageDraw
out = sys.argv[1]
N = 1024

# arrow outline in its own plane: u along the arrow, v across it
SHAFT_W, HEAD_W = 0.15, 0.36
outline = [(-0.55, -SHAFT_W), (0.12, -SHAFT_W), (0.12, -HEAD_W), (0.6, 0.0), (0.12, HEAD_W), (0.12, SHAFT_W), (-0.55, SHAFT_W)]
THICK = 0.11

# camera: a little above and in front, looking at the middle; x right, y up, z away
cam = V(0.32, 1.0, -1.75)
target = V(0.0, 0.0, 0.0)
fwd = target - cam; fwd = fwd / norm(fwd)
right = cross(V(0.0, 1.0, 0.0), fwd); right = right / norm(right)
up = cross(fwd, right)
light = V(-0.3, 0.72, -0.75); light = light / norm(light)

def to_cam(p):
    d = p - cam
    return V(d @ right, d @ up, d @ fwd)

def project(p):
    c = to_cam(p)
    return (c[0] / c[2], c[1] / c[2]), c[2]

def arrow(along, across, normal):
    along, across, normal = V(along), V(across), V(normal)
    top = [along * u + across * v + normal * (THICK / 2) for u, v in outline]
    bottom = [along * u + across * v - normal * (THICK / 2) for u, v in outline]
    faces = [(top, normal), (bottom[::-1], -normal)]
    n = len(outline)
    for i in range(n):
        j = (i + 1) % n
        quad = [top[i], top[j], bottom[j], bottom[i]]
        edge = top[j] - top[i]
        side = cross(edge, normal); side = side / norm(side)
        # outward: away from the arrow's middle
        mid = vsum(top) / n
        if side @ ((top[i] + top[j]) / 2 - mid) < 0: side = -side
        faces.append((quad, side))
    return faces

def prepare(faces):
    # keep faces turned to the camera, far ones first
    shown = []
    for pts, normal in faces:
        centre = vsum(pts) / len(pts)
        if normal @ (cam - centre) <= 0: continue
        depth = to_cam(centre)[2]
        shade = 0.5 + 0.55 * max(0.0, normal @ light)
        shown.append((depth, pts, shade))
    shown.sort(key=lambda f: -f[0])
    projected = [[project(p)[0] for p in pts] for _, pts, _ in shown]
    xs = [x for poly in projected for x, _ in poly]; ys = [y for poly in projected for _, y in poly]
    return shown, projected, max(max(xs) - min(xs), max(ys) - min(ys)), ((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2)

def draw(prepared, extent, name):
    shown, projected, _, (cx, cy) = prepared
    scale = 0.88 * N / extent
    img = Image.new("RGBA", (N, N), (0, 0, 0, 0)); d = ImageDraw.Draw(img)
    for (depth, pts, shade), poly in zip(shown, projected):
        g = int(255 * min(1.0, shade))
        d.polygon([(N / 2 + (x - cx) * scale, N / 2 - (y - cy) * scale) for x, y in poly], fill=(g, g, g, 255))
    img.resize((256, 256), Image.LANCZOS).save(f"{out}/thrust_{name}.png")

X, Y, Z = (1, 0, 0), (0, 1, 0), (0, 0, 1)
neg = lambda v: V(-v[0], -v[1], -v[2])
shapes = {
    "forward": arrow(Z, X, Y), "back": arrow(neg(Z), X, Y), "left": arrow(neg(X), Z, Y),
    "right": arrow(X, Z, Y), "up": arrow(Y, X, neg(Z)), "down": arrow(neg(Y), X, neg(Z)),
}
prepared = {k: prepare(v) for k, v in shapes.items()}
extent = max(p[2] for p in prepared.values())
for k, v in prepared.items(): draw(v, extent, k)

names = ["forward", "back", "left", "right", "up", "down"]
prev = Image.new("RGBA", (266 * 6 + 10, 296), (20, 28, 36, 255))
for i, n in enumerate(names):
    prev.alpha_composite(Image.open(f"{out}/thrust_{n}.png"), (10 + i * 266, 20))
prev.save(f"{out}/preview_arrows.png")
print("ok")
