"""Generate controlled Word repair samples without Office or user documents."""
import argparse
import os
from pathlib import Path
from xml.sax.saxutils import escape
from zipfile import ZIP_DEFLATED, ZipFile


def paragraph(text, properties=""):
    return (f"<w:p><w:pPr>{properties}</w:pPr><w:r><w:rPr>"
            '<w:rFonts w:ascii="Microsoft YaHei UI" w:eastAsia="Microsoft YaHei UI"/>'
            '<w:sz w:val="22"/></w:rPr>'
            f'<w:t xml:space="preserve">{escape(text)}</w:t></w:r></w:p>')


def create(path, modified):
    heading = '<w:pStyle w:val="Heading1"/>'
    long_text = "这是用于核对窄窗口和宽窗口的中文段落：首行位置与后续行分别计算，悬挂缩进不能把整段继续向右推，换行后仍需保持列表层级和正文可读。"
    blocks = [
        paragraph("第一章 每日商店", heading),
        paragraph("每日可刷新5次" if modified else "每日可刷新3次"),
        paragraph("首行缩进：" + long_text, '<w:ind w:left="360" w:firstLine="360"/>'),
        paragraph("悬挂缩进：" + long_text, '<w:ind w:left="720" w:hanging="360"/>'),
        paragraph("列表项：" + long_text,
                  '<w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr>'
                  '<w:ind w:left="720" w:hanging="360"/>'),
        paragraph("第二章 规则重写", heading),
    ]
    if modified:
        blocks += [paragraph(f"新方案 {index}：通过每周挑战获取宝箱，并在领取后记录奖励。") for index in range(1, 6)]
    else:
        blocks += [paragraph(f"旧配置 {index}：在商店中使用金币购买普通商品，重复兑换上限按道具单独计算。") for index in range(1, 21)]
    blocks += [paragraph("第三章 后续稳定锚点", heading), paragraph("此段内容保持不变，左右应重新对齐。")]
    document = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
                '<w:body>' + ''.join(blocks) + '<w:sectPr/></w:body></w:document>')
    styles = ('<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
              '<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/>'
              '<w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="30"/></w:rPr>'
              '</w:style></w:styles>')
    numbering = ('<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
                 '<w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/>'
                 '<w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/></w:lvl></w:abstractNum>'
                 '<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>')
    rel_ns = "http://schemas.openxmlformats.org/package/2006/relationships"
    office_rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/"
    with ZipFile(path, "w", ZIP_DEFLATED) as package:
        package.writestr("[Content_Types].xml", '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                         '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                         '<Default Extension="xml" ContentType="application/xml"/>'
                         '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
                         '<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>'
                         '<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>'
                         '</Types>')
        package.writestr("_rels/.rels", f'<Relationships xmlns="{rel_ns}"><Relationship Id="rId1" Type="{office_rel}officeDocument" Target="word/document.xml"/></Relationships>')
        package.writestr("word/_rels/document.xml.rels", f'<Relationships xmlns="{rel_ns}"><Relationship Id="rIdStyles" Type="{office_rel}styles" Target="styles.xml"/><Relationship Id="rIdNumbering" Type="{office_rel}numbering" Target="numbering.xml"/></Relationships>')
        package.writestr("word/document.xml", document)
        package.writestr("word/styles.xml", styles)
        package.writestr("word/numbering.xml", numbering)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    local_data = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local" / "share"))
    parser.add_argument(
        "--output-dir", type=Path,
        default=local_data / "Differ" / "WorkspaceMaintenance" / "word-fixtures",
        help="Destination directory (default: user-local Differ/WorkspaceMaintenance/word-fixtures).")
    output = parser.parse_args().output_dir.expanduser().resolve()
    output.mkdir(parents=True, exist_ok=True)
    for side, modified in (("base", False), ("mine", True)):
        path = output / f"word-review.{side}.docx"
        create(path, modified)
        print(path)
