"""Gera Assets/Fonts/MyTaskAppIcons.ttf, a fonte de ícones de reserva do painel.

Os glifos do painel são caracteres da área de uso privado das fontes de ícone
do Windows (Segoe Fluent Icons / Segoe MDL2 Assets). Fora do Windows essas
fontes não existem, e o caractere simplesmente não aparece — o cabeçalho
ficava sem alfinete, sem "recolher" e sem o menu.

Esta fonte responde pelos MESMOS códigos, com os contornos do Fluent UI System
Icons (MIT, Microsoft). Ela entra no fim da lista do WidgetIconFont: no Windows
a Segoe continua respondendo primeiro e nada muda; nos outros sistemas, quem
desenha é esta.

Ícone novo no app: acrescente o código em ICONS e rode de novo.

    pip install fonttools
    python scripts/generate-icon-font.py
"""

from pathlib import Path
from urllib.parse import quote
from urllib.request import urlopen
import xml.etree.ElementTree as ET

from fontTools.fontBuilder import FontBuilder
from fontTools.misc.timeTools import timestampFromString
from fontTools.pens.areaPen import AreaPen
from fontTools.pens.cu2quPen import Cu2QuPen
from fontTools.pens.recordingPen import RecordingPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.svgLib.path import parse_path

# Commit fixo: a fonte gerada não muda porque o repositório de origem mudou.
UPSTREAM = (
    "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/"
    "a563cf9166f4f91aa617557ed272612b7f0a2f72/assets"
)

# Código da Segoe -> (pasta no repositório de origem, nome do ícone de 20px).
ICONS = {
    0xE710: ("Add", "add_20_regular"),
    0xE711: ("Dismiss", "dismiss_20_regular"),
    0xE712: ("More Horizontal", "more_horizontal_20_regular"),
    0xE718: ("Pin", "pin_20_regular"),
    0xE823: ("Clock", "clock_20_regular"),
    0xE738: ("Subtract", "subtract_20_regular"),
    0xE840: ("Pin", "pin_20_filled"),
    0xE890: ("Eye", "eye_20_regular"),
    0xE8A7: ("Open", "open_20_regular"),
    0xE8EC: ("Tag", "tag_20_regular"),
    0xEA8F: ("Alert", "alert_20_regular"),
    # Tempo trabalhado (ADR-052): ▶/⏹ na linha, ✎/🗑 no histórico.
    0xE768: ("Play", "play_20_regular"),
    0xE71A: ("Stop", "stop_20_regular"),
    0xE70F: ("Edit", "edit_20_regular"),
    0xE74D: ("Delete", "delete_20_regular"),
    # Histórico dos últimos dias (ADR-053), no cabeçalho.
    0xE81C: ("History", "history_20_regular"),
}

FAMILY = "MyTaskApp Icons"

# As métricas copiam as da Segoe: quadrado de 2048, ascendente 2048 e
# descendente 0. Assim o glifo ocupa a mesma caixa nos dois sistemas e os
# botões não mudam de tamanho conforme a fonte que respondeu.
UNITS_PER_EM = 2048
VIEWBOX = 20

FIXED_TIMESTAMP = timestampFromString("Mon Jan  1 00:00:00 2024")

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "src" / "MyTaskApp.Desktop" / "Assets" / "Fonts" / "MyTaskAppIcons.ttf"

SVG_NS = "{http://www.w3.org/2000/svg}"


def fetch_svg(folder: str, name: str) -> str:
    url = f"{UPSTREAM}/{quote(folder)}/SVG/ic_fluent_{name}.svg"
    with urlopen(url) as response:
        return response.read().decode("utf-8")


def draw_glyph(svg: str):
    root = ET.fromstring(svg)
    if root.get("viewBox") != f"0 0 {VIEWBOX} {VIEWBOX}":
        raise ValueError(f"viewBox inesperado: {root.get('viewBox')}")

    # SVG desce em y; fonte sobe. Escala o quadro de 20 para o em de 2048.
    scale = UNITS_PER_EM / VIEWBOX
    outline = RecordingPen()
    flip = TransformPen(outline, (scale, 0, 0, -scale, 0, UNITS_PER_EM))
    for path in root.iter(f"{SVG_NS}path"):
        parse_path(path.get("d"), flip)

    # Os SVGs de origem não concordam no sentido dos contornos. TrueType quer
    # o de fora em sentido horário, que para o AreaPen é área negativa.
    area = AreaPen()
    outline.replay(area)

    pen = TTGlyphPen(None)
    outline.replay(Cu2QuPen(pen, max_err=1.0, reverse_direction=area.value > 0))
    return pen.glyph()


def main() -> None:
    names = [".notdef"]
    glyphs = {".notdef": TTGlyphPen(None).glyph()}
    cmap = {}

    for code, (folder, name) in sorted(ICONS.items()):
        glyph_name = f"uni{code:04X}"
        names.append(glyph_name)
        glyphs[glyph_name] = draw_glyph(fetch_svg(folder, name))
        cmap[code] = glyph_name

    fb = FontBuilder(UNITS_PER_EM, isTTF=True)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    # O lsb tem de ser o xMin do glifo: com 0 o leitor empurra o desenho para
    # a esquerda e o ícone sai descentrado no botão.
    glyf = fb.font["glyf"]
    fb.setupHorizontalMetrics({
        name: (UNITS_PER_EM, getattr(glyf[name], "xMin", 0)) for name in names
    })
    fb.setupHorizontalHeader(ascent=UNITS_PER_EM, descent=0)
    fb.setupOS2(sTypoAscender=UNITS_PER_EM, sTypoDescender=0, sTypoLineGap=0,
                usWinAscent=UNITS_PER_EM, usWinDescent=0)
    fb.setupNameTable({
        "familyName": FAMILY,
        "styleName": "Regular",
        "copyright": "Contornos: Fluent UI System Icons, (c) Microsoft Corporation, licença MIT.",
    })
    fb.setupPost()
    # Datas fixas: rodar de novo sem mudar ícone não muda o binário.
    fb.font["head"].created = FIXED_TIMESTAMP
    fb.font["head"].modified = FIXED_TIMESTAMP
    fb.font.recalcTimestamp = False

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    fb.save(str(OUTPUT))
    print(f"{OUTPUT.relative_to(ROOT)}: {len(cmap)} glifos")


if __name__ == "__main__":
    main()
