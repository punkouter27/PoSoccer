"""Crops the root FACE_* photos into square 512 px player faces.

Output: Assets/Art/Faces/FACE_<NAME>.png, upright (top of head at the top).
Agent_Face rotates the sprite on the pitch so the chin points where the player
faces. Crop boxes are hand-set: (source file, centre x, centre y, side), all as
fractions of the EXIF-corrected image width/height (side is a fraction of width).

    .venv\Scripts\python.exe -I scripts\import-faces.py [--sheet out.png]
"""
import os, sys
from PIL import Image, ImageOps, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Assets", "Art", "Faces")
SIZE = 512

FACES = {
    "ALAN":    ("FACE_Alan.png",     0.520, 0.29, 0.48),
    "BAILY":   ("FACE_Baily.png",    0.600, 0.44, 0.46),
    "BRENDAN": ("FACE_Brendan.png",  0.500, 0.40, 0.78),
    "DECOY":   ("FACE_Decoy.png",    0.750, 0.16, 0.20),
    "DUPEE":   ("FACE_Dupee.png",    0.500, 0.40, 0.74),
    "JH":      ("FACE_JH.jpg",       0.745, 0.43, 0.13),
    "JOSH":    ("FACE_Josh.png",     0.500, 0.42, 1.00),
    "KIM":     ("FACE_Kim2.jpg",     0.520, 0.31, 0.26),
    "MANZI":   ("FACE_Manzi.jpg",    0.500, 0.40, 0.78),
    "MATT":    ("FACE_Matt3.jpg",    0.570, 0.40, 0.50),
    "MAYA":    ("FACE_Maya.JPG",     0.460, 0.50, 0.80),
    "NICK":    ("FACE_Nick2.jpg",    0.500, 0.40, 0.40),
    "PORTO":   ("FACE_Porto.jpg",    0.370, 0.60, 0.28),
    "SOKOL":   ("FACE_Sokol.webp",   0.500, 0.40, 0.55),
    "TONG":    ("FACE_Tong.png",     0.560, 0.45, 0.60),
    "VELEZ":   ("FACE_Velez.jpg",    0.500, 0.42, 0.66),
}

def crop(name):
    src, cx, cy, side = FACES[name]
    im = ImageOps.exif_transpose(Image.open(os.path.join(ROOT, src))).convert("RGB")
    w, h = im.size
    s = side * w
    box = [cx * w - s / 2, cy * h - s / 2, cx * w + s / 2, cy * h + s / 2]
    # Keep the box inside the image without changing its size where possible.
    dx = max(0, -box[0]) - max(0, box[2] - w)
    dy = max(0, -box[1]) - max(0, box[3] - h)
    box = [box[0] + dx, box[1] + dy, box[2] + dx, box[3] + dy]
    return im.crop(tuple(int(round(v)) for v in box)).resize((SIZE, SIZE), Image.LANCZOS)

def main():
    os.makedirs(OUT, exist_ok=True)
    tiles = []
    for name in FACES:
        face = crop(name)
        face.save(os.path.join(OUT, f"FACE_{name}.png"), optimize=True)
        tiles.append((name, face))
        print(f"FACE_{name}.png")
    if "--sheet" in sys.argv:
        T = 200
        sheet = Image.new("RGB", (8 * T, 2 * (T + 18)), "white")
        d = ImageDraw.Draw(sheet)
        for i, (name, face) in enumerate(tiles):
            x, y = (i % 8) * T, (i // 8) * (T + 18)
            sheet.paste(face.resize((T, T)), (x, y))
            d.text((x + 4, y + T + 3), name, fill="black")
        sheet.save(sys.argv[sys.argv.index("--sheet") + 1])

if __name__ == "__main__":
    main()
