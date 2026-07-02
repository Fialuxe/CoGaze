"""Generate one A4 PNG per QR marker used by CoGaze.

Each QR encodes its marker id verbatim (the string the Quest reports as
MarkerPayloadString), centered on an A4 page at 300 DPI with the id printed
below it so the printed sheet is human-identifiable.

Usage:
    pip install qrcode pillow
    python tools/generate_qr_a4.py [--out OUTPUT_DIR] [--size-mm 150] [--dpi 300]
"""

import argparse
import os

import qrcode
from qrcode.constants import ERROR_CORRECT_H
from PIL import Image, ImageDraw, ImageFont

MARKERS = ["A", "B", "C", "D", "E", "QR_CALIB_A", "QR_CALIB_B"]

# A4 at the chosen DPI (210 x 297 mm).
A4_MM = (210, 297)

# Page margin kept around the QR when maximizing (width is the binding side).
MARGIN_MM = 10


def mm_to_px(mm: float, dpi: int) -> int:
    return round(mm / 25.4 * dpi)


def load_font(size_px: int) -> ImageFont.FreeTypeFont:
    for name in ("arial.ttf", "DejaVuSans.ttf", "DejaVuSans-Bold.ttf"):
        try:
            return ImageFont.truetype(name, size_px)
        except OSError:
            continue
    return ImageFont.load_default()


def make_page(marker: str, size_mm: float, dpi: int) -> Image.Image:
    page_w = mm_to_px(A4_MM[0], dpi)
    page_h = mm_to_px(A4_MM[1], dpi)
    page = Image.new("RGB", (page_w, page_h), "white")

    # High error correction so partial occlusion / glare still decodes.
    qr = qrcode.QRCode(error_correction=ERROR_CORRECT_H, border=4)
    qr.add_data(marker)
    qr.make(fit=True)
    qr_img = qr.make_image(fill_color="black", back_color="white").convert("RGB")

    # size_mm <= 0 means "maximize": fill the page width minus margins. Width is the
    # binding side on A4, so the QR is as large as it can be while staying square.
    if size_mm <= 0:
        target = page_w - 2 * mm_to_px(MARGIN_MM, dpi)
        size_mm = target / dpi * 25.4
    else:
        target = mm_to_px(size_mm, dpi)
    qr_img = qr_img.resize((target, target), Image.NEAREST)

    qr_x = (page_w - target) // 2
    qr_y = (page_h - target) // 2
    page.paste(qr_img, (qr_x, qr_y))

    draw = ImageDraw.Draw(page)

    # Marker id label below the QR.
    label_font = load_font(mm_to_px(12, dpi))
    bbox = draw.textbbox((0, 0), marker, font=label_font)
    text_w = bbox[2] - bbox[0]
    draw.text(((page_w - text_w) // 2, qr_y + target + mm_to_px(8, dpi)),
              marker, fill="black", font=label_font)

    # Printed-size caption above the QR so the operator can verify scale.
    cap = f"QR side = {size_mm:g} mm  (do not rescale when printing)"
    cap_font = load_font(mm_to_px(5, dpi))
    cbox = draw.textbbox((0, 0), cap, font=cap_font)
    draw.text(((page_w - (cbox[2] - cbox[0])) // 2, qr_y - mm_to_px(12, dpi)),
              cap, fill="black", font=cap_font)

    return page


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", default="qr_a4", help="output directory")
    parser.add_argument("--size-mm", type=float, default=0.0,
                        help="QR side length in mm; <=0 maximizes to page width minus "
                             "margins (default: maximize)")
    parser.add_argument("--dpi", type=int, default=300, help="render DPI (default 300)")
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    for marker in MARKERS:
        page = make_page(marker, args.size_mm, args.dpi)
        path = os.path.join(args.out, f"{marker}.png")
        page.save(path, dpi=(args.dpi, args.dpi))
        print(f"wrote {path}  ({page.width}x{page.height}px @ {args.dpi}dpi)")


if __name__ == "__main__":
    main()
