from pathlib import Path
from PIL import Image
import sys


# ------------------------------------------------------------
# Remove white background from PNG images
# ------------------------------------------------------------

WHITE_THRESHOLD = 245
EDGE_SOFTNESS = 10


def make_white_transparent(image: Image.Image) -> Image.Image:
    image = image.convert("RGBA")

    pixels = image.load()
    width, height = image.size

    for y in range(height):
        for x in range(width):
            r, g, b, a = pixels[x, y]

            # Completely white / near-white background
            if r >= WHITE_THRESHOLD and \
               g >= WHITE_THRESHOLD and \
               b >= WHITE_THRESHOLD:

                pixels[x, y] = (r, g, b, 0)

            # Soft transition for anti-aliased edges
            elif (
                r >= WHITE_THRESHOLD - EDGE_SOFTNESS and
                g >= WHITE_THRESHOLD - EDGE_SOFTNESS and
                b >= WHITE_THRESHOLD - EDGE_SOFTNESS
            ):
                distance = min(
                    WHITE_THRESHOLD - r,
                    WHITE_THRESHOLD - g,
                    WHITE_THRESHOLD - b
                )

                alpha = max(
                    0,
                    min(255, int(
                        255 * distance / EDGE_SOFTNESS
                    ))
                )

                pixels[x, y] = (r, g, b, alpha)

    return image


def process_file(path: Path):
    if not path.exists():
        print(f"[SKIP] Not found: {path}")
        return

    try:
        image = Image.open(path)
        result = make_white_transparent(image)

        result.save(
            path,
            format="PNG"
        )

        print(f"[OK] {path}")

    except Exception as ex:
        print(f"[ERROR] {path}: {ex}")


def main():
    if len(sys.argv) < 2:
        print("Usage:")
        print("  python remove_white_background.py image.png")
        print("  python remove_white_background.py folder")
        return

    target = Path(sys.argv[1])

    if target.is_file():
        process_file(target)

    elif target.is_dir():
        files = sorted(target.glob("*.png"))

        if not files:
            print("No PNG files found.")
            return

        for file in files:
            process_file(file)

    else:
        print(f"Not found: {target}")


if __name__ == "__main__":
    main()