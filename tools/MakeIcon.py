"""Create the shared Windows icon for the desktop app and native launcher."""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


def draw_icon(size: int) -> Image.Image:
    scale = 4
    side = size * scale
    icon = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    background = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    brush = ImageDraw.Draw(background)

    def point(x: float, y: float) -> tuple[int, int]:
        return round(x * side), round(y * side)

    brush.rounded_rectangle(
        [point(0.035, 0.035), point(0.965, 0.965)],
        radius=round(0.22 * side),
        fill="#122431",
        outline="#497484",
        width=max(1, round(0.012 * side)),
    )
    brush.rounded_rectangle(
        [point(0.09, 0.09), point(0.91, 0.91)],
        radius=round(0.18 * side),
        fill="#182E3C",
    )
    icon.alpha_composite(background)

    glow = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    glow_brush = ImageDraw.Draw(glow)
    trace = [point(0.18, 0.53), point(0.35, 0.53), point(0.45, 0.25),
             point(0.56, 0.75), point(0.66, 0.44), point(0.82, 0.44)]
    glow_brush.line(trace, fill=(77, 235, 202, 145), width=round(0.12 * side), joint="curve")
    icon.alpha_composite(glow.filter(ImageFilter.GaussianBlur(round(0.035 * side))))

    brush = ImageDraw.Draw(icon)
    width = round(0.064 * side)
    brush.line(trace, fill="#78F0D1", width=width, joint="curve")
    radius = width // 2
    for x, y in (trace[0], trace[-1]):
        brush.ellipse((x - radius, y - radius, x + radius, y + radius), fill="#78F0D1")
    x, y = trace[2]
    brush.ellipse((x - round(0.045 * side), y - round(0.045 * side),
                   x + round(0.045 * side), y + round(0.045 * side)), fill="#F5C978")
    return icon.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    assets = root / "Assets"
    assets.mkdir(exist_ok=True)
    sizes = (16, 24, 32, 48, 64, 128, 256)
    icon = draw_icon(256)
    icon.save(assets / "companion-icon.png")
    icon.save(assets / "companion.ico", format="ICO", sizes=[(size, size) for size in sizes])


if __name__ == "__main__":
    main()
