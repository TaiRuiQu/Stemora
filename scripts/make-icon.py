"""Draw the small, code-native waveform application icon. Requires Pillow only to regenerate."""
from pathlib import Path
from PIL import Image, ImageDraw

image = Image.new("RGBA", (256, 256))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((4, 4, 252, 252), radius=48, fill="#0067C0")
for x, height in zip(range(52, 213, 25), [36, 76, 126, 160, 108, 66, 30]):
    draw.rounded_rectangle((x, 128-height//2, x+12, 128+height//2), radius=6, fill="#FFFFFF")
target = Path(__file__).resolve().parents[1] / "src/Stemora.App/Assets/Stemora.ico"
target.parent.mkdir(parents=True, exist_ok=True)
image.save(target, sizes=[(16,16), (24,24), (32,32), (48,48), (64,64), (128,128), (256,256)])
