# -*- coding: utf-8 -*-
"""Dopisuje do zasobów powłoki brakujące etykiety poleceń, kart i grup z katalogu (istniejących nie zmienia).
Uruchomienie po zmianie katalogu:  python tools/catalog/add_labels.py"""
import html
import importlib.util
import os
import re

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
spec = importlib.util.spec_from_file_location("catalog", os.path.join(ROOT, "tools", "catalog", "catalog.py"))
cat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cat)

want = {"pl": {}, "en": {}}
for c in cat.C:
    want["pl"]["Cmd." + c["id"]] = c["pl"]
    want["en"]["Cmd." + c["id"]] = c["en"]
for tab_id, _, pl, en, groups in cat.TABS:
    want["pl"]["Ribbon.Tab." + tab_id] = pl
    want["en"]["Ribbon.Tab." + tab_id] = en
    for group in groups:
        want["pl"]["Ribbon.Group." + group[0]] = group[1]
        want["en"]["Ribbon.Group." + group[0]] = group[2]

for lang, name in (("pl", "Strings.pl.resx"), ("en", "Strings.resx")):
    path = os.path.join(ROOT, "src", "ZetPDF.App", "Resources", name)
    raw = open(path, "rb").read()
    bom = raw[:3] == b"\xef\xbb\xbf"
    text = raw.decode("utf-8-sig")
    have = set(re.findall(r'<data name="([^"]+)"', text))
    add = [k for k in want[lang] if k not in have]
    lines = "".join(f'  <data name="{k}" xml:space="preserve"><value>{html.escape(want[lang][k], quote=False)}</value></data>\n' for k in add)
    text = text.replace("</root>", lines + "</root>")
    open(path, "wb").write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
    print(name, "dodano", len(add))
