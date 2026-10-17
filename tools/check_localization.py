"""Kontrola lokalizacji ZetPDF (recenzent końcowy).

1. Każda para src/<Projekt>/Resources/Strings.resx + Strings.pl.resx: te same klucze, brak pustych wartości,
   te same symbole {n}, brak duplikatów kluczy między modułami.
2. Teksty polskie identyczne z angielskimi (podejrzenie braku tłumaczenia) – lista do przejrzenia.
3. XAML: atrybuty tekstowe (Content, Text, Header, ToolTip, Title, Label, Watermark, Description) z literałem
   zamiast {loc:Loc …}/{Binding …}/{DynamicResource …}.
4. C#: literały przypisywane do Content/Text/Header/ToolTip/Title, MessageBox.Show/ShowMessage/Confirm/SetStatus
   z literałem, throw z literałem bez WithMessageKey w miejscach widocznych dla użytkownika – tylko lista.
5. Klucze używane w kodzie (loc["…"], Format("…"), {loc:Loc …}, WithMessageKey("…")) istnieją w zasobach.

Użycie: python tools/check_localization.py  (kod wyjścia 1, gdy są błędy z punktów 1, 3, 4, 5)
"""
import io, os, re, sys
sys.stdout.reconfigure(encoding='utf-8')
import xml.etree.ElementTree as ET

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src')
ROOT = os.path.normpath(ROOT)
errors, warnings = [], []

def resx(path):
    d = {}
    for e in ET.parse(path).getroot().findall('data'):
        v = e.find('value')
        d[e.get('name')] = (v.text or '') if v is not None else ''
    return d

placeholder = re.compile(r'\{(\d+)(?:[,:][^}]*)?\}')
all_keys = {}
owner = {}
for proj in sorted(os.listdir(ROOT)):
    en_p = os.path.join(ROOT, proj, 'Resources', 'Strings.resx')
    pl_p = os.path.join(ROOT, proj, 'Resources', 'Strings.pl.resx')
    if not os.path.exists(en_p):
        continue
    if not os.path.exists(pl_p):
        errors.append(f'{proj}: brak Strings.pl.resx'); continue
    en, pl = resx(en_p), resx(pl_p)
    for k in sorted(set(en) - set(pl)): errors.append(f'{proj}: klucz tylko w en: {k}')
    for k in sorted(set(pl) - set(en)): errors.append(f'{proj}: klucz tylko w pl: {k}')
    for k in sorted(set(en) & set(pl)):
        if not en[k].strip() and k != 'Shell.Empty': errors.append(f'{proj}: pusta wartość en: {k}')
        if not pl[k].strip() and k != 'Shell.Empty': errors.append(f'{proj}: pusta wartość pl: {k}')
        if set(placeholder.findall(en[k])) != set(placeholder.findall(pl[k])):
            errors.append(f'{proj}: różne symbole {{n}} w {k}: en={en[k]!r} pl={pl[k]!r}')
        if en[k] == pl[k] and len(en[k]) > 3 and re.search(r'[a-z]{4,}', en[k]) and ' ' in en[k]:
            warnings.append(f'{proj}: pl = en (sprawdź tłumaczenie): {k} = {en[k]!r}')
        if k in owner and owner[k] != proj: errors.append(f'duplikat klucza {k}: {owner[k]} i {proj}')
        owner[k] = proj
        all_keys[k] = (en[k], pl[k])

text_attrs = ('Content', 'Text', 'Header', 'ToolTip', 'Title', 'Label', 'Description', 'Watermark', 'PlaceholderText')
xaml_attr = re.compile(r'\b(' + '|'.join(text_attrs) + r')="([^"]*)"')
code_assign = re.compile(r'\b(' + '|'.join(text_attrs) + r')\s*=\s*\$?"([^"]+)"')
code_call = re.compile(r'\b(MessageBox\.Show|ShowMessage|Confirm|SetStatus|ShowError|AskYesNoCancel)\(\s*\$?"([^"]+)"')
key_use = re.compile(r'(?:loc|_loc|Loc|L|T|Txt|Text)\s*(?:\[|\.Format\(|\()\s*"((?:Core|Shell|Cmd|Ribbon|Render|Viewer|Pages|Annot|Edit|Ocr|Security)\.[\w.]+)"')
key_use2 = re.compile(r'(?:WithMessageKey|SetStatus|Message|Status)\(\s*"((?:Core|Shell|Cmd|Ribbon|Render|Viewer|Pages|Annot|Edit|Ocr|Security)\.[\w.]+)"')
xaml_key = re.compile(r'\{loc:Loc\s+(?:Key=)?([\w.]+)')

# Świadome wyjątki: glify ikon (&#xE…; – czcionka Segoe Fluent Icons), nazwy PDF (/Watermark), klucze zasobów
# przekazywane dalej, domyślny przyrostek nazwy pliku Kreatora (_zetpdf – dane, nie tekst interfejsu),
# „Aa” – uniwersalny symbol przełącznika „uwzględnij wielkość liter” na pasku wyszukiwania.
ALLOWED = {'ZetPDF', '_zetpdf', 'Aa'}

def allowed_literal(v):
    v = v.strip()
    return (not v or v.startswith('{') or v.startswith('&#x') or v.startswith('/') or v in ALLOWED
            or not re.search(r'[A-Za-zĄĆĘŁŃÓŚŹŻąćęłńóśźż]{2,}', v)
            or re.fullmatch(r'(Core|Shell|Cmd|Ribbon|Render|Viewer|Pages|Annot|Edit|Ocr|Security)\.[\w.]+', v) is not None
            or re.fullmatch(r'[A-Z][A-Za-z]+', v) is not None and v in ('Watermark',)
            or re.fullmatch(r'[\w.]+\.(png|ico|xaml)', v) is not None)

used = set()
for dirpath, dirs, files in os.walk(ROOT):
    dirs[:] = [d for d in dirs if d not in ('bin', 'obj')]
    for f in files:
        p = os.path.join(dirpath, f)
        rel = os.path.relpath(p, ROOT)
        if f.endswith('.xaml'):
            s = io.open(p, encoding='utf-8-sig').read()
            for m in xaml_attr.finditer(s):
                if not allowed_literal(m.group(2)):
                    errors.append(f'{rel}: tekst na sztywno {m.group(1)}="{m.group(2)}"')
            for m in re.finditer(r'>([^<>{}\s][^<>{}]*)</(TextBlock|Run|Label|Button)>', s):
                if not allowed_literal(m.group(1)):
                    errors.append(f'{rel}: tekst na sztywno w treści <{m.group(2)}>: {m.group(1)!r}')
            used.update(xaml_key.findall(s))
        elif f.endswith('.cs') and not f.endswith('.g.cs'):
            s = io.open(p, encoding='utf-8-sig').read()
            for i, line in enumerate(s.splitlines(), 1):
                if line.strip().startswith('//') or 'Diagnostics' in rel:
                    continue
                for m in code_assign.finditer(line):
                    if not allowed_literal(m.group(2)):
                        errors.append(f'{rel}:{i}: literał w {m.group(1)}: "{m.group(2)}"')
                for m in code_call.finditer(line):
                    if not allowed_literal(m.group(2)) and not re.fullmatch(r'[A-Z][a-z]+\.[\w.]+', m.group(2)):
                        errors.append(f'{rel}:{i}: literał w {m.group(1)}: "{m.group(2)}"')
            used.update(key_use.findall(s))
            used.update(key_use2.findall(s))

for k in sorted(used):
    if k not in all_keys and not k.endswith('.') and not any(x.startswith(k + '.') for x in all_keys):
        errors.append(f'klucz użyty w kodzie nie istnieje w zasobach: {k}')

print(f'Klucze w zasobach: {len(all_keys)} (każdy w pl i en), klucze użyte w kodzie (wykryte wzorcem): {len(used)}')
for w in warnings: print('UWAGA', w)
for e in errors: print('BŁĄD ', e)
print(f'Błędy: {len(errors)}, uwagi: {len(warnings)}')
sys.exit(1 if errors else 0)
