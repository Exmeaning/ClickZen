#!/usr/bin/env python3
"""Generates Strings/<lang>/Resources.resw for ClickZen.App from strings.tsv.

strings.tsv columns: key<TAB>zh-CN<TAB>en-US
Keys containing '.' are XAML x:Uid property keys (e.g. Page_Devices_Title.Text).
Run: python src/ClickZen.App/Strings/gen_resw.py
"""
from __future__ import annotations

import csv
import pathlib
import sys
from xml.sax.saxutils import escape

HERE = pathlib.Path(__file__).resolve().parent
LANGS = {"zh-CN": 1, "en-US": 2}

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<!-- Generated from strings.tsv by gen_resw.py. Do not edit by hand. -->
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""


def main() -> int:
    rows = []
    with open(HERE / "strings.tsv", encoding="utf-8", newline="") as f:
        for i, row in enumerate(csv.reader(f, delimiter="\t", quoting=csv.QUOTE_NONE), start=1):
            if not row or row[0].startswith("#") or not row[0].strip():
                continue
            if len(row) != 3:
                print(f"strings.tsv:{i}: expected 3 columns, got {len(row)}", file=sys.stderr)
                return 1
            rows.append(row)

    keys = [r[0] for r in rows]
    dupes = {k for k in keys if keys.count(k) > 1}
    if dupes:
        print(f"duplicate keys: {sorted(dupes)}", file=sys.stderr)
        return 1

    for lang, col in LANGS.items():
        out = HERE / lang / "Resources.resw"
        out.parent.mkdir(parents=True, exist_ok=True)
        parts = [HEADER]
        for r in rows:
            value = r[col].replace("\\n", "\n")
            parts.append(f'  <data name="{escape(r[0])}" xml:space="preserve"><value>{escape(value)}</value></data>\n')
        parts.append("</root>\n")
        out.write_text("".join(parts), encoding="utf-8")
        print(f"wrote {out.relative_to(HERE.parent)} ({len(rows)} strings)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
