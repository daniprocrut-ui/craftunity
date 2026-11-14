import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "c#launcher", "icon.ico")
OUT = os.path.join(ROOT, "docs", "icons")

os.makedirs(OUT, exist_ok=True)
image = Image.open(SRC).convert("RGBA")

for size in (32, 180, 192, 512):
    target = os.path.join(OUT, f"icon-{size}.png")
    image.resize((size, size), Image.LANCZOS).save(target)
    print(target, os.path.getsize(target))

print("ok")
sys.exit(0)
