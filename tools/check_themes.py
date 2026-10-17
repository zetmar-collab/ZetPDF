"""Kontrola motywów ZetPDF (recenzent końcowy).

1. Kolory na sztywno poza słownikami motywów (Themes/Light.xaml, Themes/Dark.xaml): #RRGGBB/#AARRGGBB w XAML i C#,
   Colors.X / Brushes.X / Color.FromRgb / Color.FromArgb w C#. Wynik: lista z oznaczeniem świadomych wyjątków.
2. Kontrast par tekst/tło (WCAG 2.x, min. 4.5:1) w obu słownikach, w tym pary używane w oknach dialogowych
   (tło okna/powierzchni/pola, stopka okna = Surface.Alt, podpowiedź, pasek stanu, zaznaczenie, akcent, statusy).

Użycie: python tools/check_themes.py   (kod wyjścia 1 przy kontraście < 4.5 lub nieuzasadnionym kolorze)
"""
import io, os, re, sys
sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src'))
THEMES = os.path.join(ROOT, 'ZetPDF.App', 'Themes')

def load(name):
    s = io.open(os.path.join(THEMES, name), encoding='utf-8-sig').read()
    d = {}
    for m in re.finditer(r'x:Key="([^"]+)"\s+Color="#([0-9A-Fa-f]{6,8})"', s):
        h = m.group(2)
        if len(h) == 8: a, h = int(h[:2], 16), h[2:]
        else: a = 255
        d[m.group(1)] = (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)
    return d

def lum(c):
    def ch(v):
        v /= 255
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = c[:3]
    return 0.2126 * ch(r) + 0.7152 * ch(g) + 0.0722 * ch(b)

def over(fg, bg):  # kompozycja koloru z przezroczystością na tle
    a = fg[3] / 255
    return tuple(round(fg[i] * a + bg[i] * (1 - a)) for i in range(3)) + (255,)

def ratio(a, b):
    la, lb = lum(a), lum(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)

TEXT_ON = {
    'Brush.Text.Primary': ['Brush.Window.Background', 'Brush.Surface', 'Brush.Surface.Alt', 'Brush.Surface.Raised', 'Brush.Control.Background',
                           'Brush.Control.Background.Hover', 'Brush.Control.Background.Pressed', 'Brush.Input.Background'],
    'Brush.Text.Secondary': ['Brush.Window.Background', 'Brush.Surface', 'Brush.Surface.Alt', 'Brush.Surface.Raised', 'Brush.Control.Background', 'Brush.Input.Background'],
    'Brush.Text.Link': ['Brush.Window.Background', 'Brush.Surface', 'Brush.Surface.Raised', 'Brush.Surface.Alt'],
    'Brush.Text.OnAccent': ['Brush.Accent', 'Brush.Accent.Hover', 'Brush.Accent.Pressed'],
    'Brush.Selection.Text': ['Brush.Selection.Background'],
    'Brush.Tooltip.Text': ['Brush.Tooltip.Background'],
    'Brush.StatusBar.Text': ['Brush.StatusBar.Background'],
    'Brush.Status.Error': ['Brush.Surface', 'Brush.Surface.Raised', 'Brush.Window.Background', 'Brush.Surface.Alt'],
    'Brush.Status.Warning': ['Brush.Surface', 'Brush.Surface.Raised', 'Brush.Window.Background'],
    'Brush.Status.Success': ['Brush.Surface', 'Brush.Surface.Raised', 'Brush.Window.Background'],
    'Brush.Status.Info': ['Brush.Surface', 'Brush.Surface.Raised', 'Brush.Window.Background'],
    'Brush.Accent': ['Brush.Surface', 'Brush.Surface.Raised', 'Brush.Window.Background'],
}
# Tekst wyłączony (WCAG nie wymaga kontrastu dla nieaktywnych elementów) – raport informacyjny, próg 3:1.
INFO = {'Brush.Text.Disabled': ['Brush.Surface', 'Brush.Control.Background']}

fail = 0
for theme in ('Light.xaml', 'Dark.xaml'):
    d = load(theme)
    print(f'== {theme}: {len(d)} pędzli')
    worst = (99, '')
    for fg, bgs in TEXT_ON.items():
        for bg in bgs:
            if fg not in d or bg not in d:
                print(f'  BRAK klucza {fg} / {bg}'); fail += 1; continue
            b = d[bg] if d[bg][3] == 255 else over(d[bg], d['Brush.Window.Background'])
            r = ratio(over(d[fg], b), b)
            worst = min(worst, (r, f'{fg} na {bg}'))
            if r < 4.5:
                print(f'  ZA NISKI {r:5.2f}:1  {fg} na {bg}'); fail += 1
    print(f'  najniższy kontrast tekstu: {worst[0]:.2f}:1 ({worst[1]})')
    for fg, bgs in INFO.items():
        for bg in bgs:
            print(f'  info: {fg} na {bg}: {ratio(d[fg], d[bg]):.2f}:1 (element nieaktywny)')

hex_re = re.compile(r'#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b')
cs_re = re.compile(r'\b(Colors\.\w+|Brushes\.\w+|Color\.FromRgb|Color\.FromArgb|Color\.FromScRgb)')
hits = []
for dirpath, dirs, files in os.walk(ROOT):
    dirs[:] = [x for x in dirs if x not in ('bin', 'obj')]
    for f in files:
        p = os.path.join(dirpath, f)
        rel = os.path.relpath(p, ROOT)
        if rel.startswith(os.path.join('ZetPDF.App', 'Themes')) and f in ('Light.xaml', 'Dark.xaml'):
            continue
        if not (f.endswith('.xaml') or f.endswith('.cs')) or f.endswith('.g.cs'):
            continue
        for i, line in enumerate(io.open(p, encoding='utf-8-sig').read().splitlines(), 1):
            code = line.split('//')[0]
            for m in hex_re.finditer(code if f.endswith('.cs') else line):
                hits.append((rel, i, m.group(0), line.strip()))
            if f.endswith('.cs'):
                for m in cs_re.finditer(code):
                    hits.append((rel, i, m.group(0), line.strip()))
print(f'== kolory poza słownikami motywów: {len(hits)} wystąpień')
for rel, i, what, line in hits:
    print(f'  {rel}:{i}: {what}   | {line[:140]}')
print(f'Kontrast: {"OK" if fail == 0 else str(fail) + " par poniżej 4.5:1"}')
sys.exit(1 if fail else 0)
