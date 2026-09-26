"""클래스 아이콘(assets/class_icons)으로 텍스트 중간에 넣을 이모지용 배지(assets/class_emojis)를 만든다.

원본 아이콘은 흰색·투명 배경이라 라이트 테마에서 보이지 않으므로, 중간 회색 원 위에 얹어 두 테마에서 같은 모양으로 보이게 한다.
128×128 PNG, 아이콘은 원 지름의 70%. 4배 크기로 그린 뒤 축소해 가장자리를 부드럽게 한다.

사용: python3 scripts/make_class_emojis.py  (Pillow 필요: pip install pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "class_icons"
DST = ROOT / "assets" / "class_emojis"
SIZE = 128
SUPERSAMPLE = 4
BADGE_COLOR = "#4E5058"
ICON_RATIO = 0.70


def make_badge(icon_path: Path) -> Image.Image:
    big = SIZE * SUPERSAMPLE
    badge = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(badge).ellipse((0, 0, big - 1, big - 1), fill=BADGE_COLOR)

    icon = Image.open(icon_path).convert("RGBA")
    icon = icon.crop(icon.getchannel("A").getbbox())
    scale = big * ICON_RATIO / max(icon.size)
    icon = icon.resize((max(1, round(icon.width * scale)), max(1, round(icon.height * scale))), Image.LANCZOS)
    badge.alpha_composite(icon, ((big - icon.width) // 2, (big - icon.height) // 2))
    return badge.resize((SIZE, SIZE), Image.LANCZOS)


def main() -> None:
    DST.mkdir(exist_ok=True)
    icons = sorted(SRC.glob("*.png"))
    for icon_path in icons:
        make_badge(icon_path).save(DST / icon_path.name, optimize=True)
    print(f"{len(icons)}개 생성: {DST}")


if __name__ == "__main__":
    main()
