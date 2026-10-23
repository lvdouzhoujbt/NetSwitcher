# -*- coding: utf-8 -*-
# Generate a professional app icon: blue rounded square + white switch arrows
from PIL import Image, ImageDraw

S = 256
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

# vertical gradient background
grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(grad)
top = (36, 124, 222)
bottom = (9, 68, 148)
for y in range(S):
    t = y / (S - 1.0)
    c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    gd.line([(0, y), (S, y)], fill=c)

mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([8, 8, S - 8, S - 8], radius=58, fill=255)
img.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(img)
white = (255, 255, 255, 255)

# up arrow (left side): head triangle + shaft
d.polygon([(76, 122), (128, 122), (102, 60)], fill=white)
d.rectangle([89, 118, 115, 194], fill=white)

# down arrow (right side): shaft + head triangle
d.rectangle([141, 62, 167, 138], fill=white)
d.polygon([(130, 134), (178, 134), (154, 196)], fill=white)

img.save("app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.save("icon_preview.png")
print("icon generated")
