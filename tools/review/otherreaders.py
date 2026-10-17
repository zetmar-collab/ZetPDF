"""Kontrola plików ZetPDF innymi, niezależnymi czytnikami zainstalowanymi w systemie (tylko odczyt):
MuPDF (PyMuPDF) i pypdf. Argument: folder z próbkami."""
import sys, os, io
sys.stdout.reconfigure(encoding='utf-8')
d = sys.argv[1]
cases = [
    ('zaszyfrowany AES-256 ąę.pdf', 'otwórz-ą1', 'właściciel-ź2'),
    ('aes256-ascii.pdf', 'user123', 'owner123'),
    ('aes256-tylko-uprawnienia.pdf', '', 'owner123'),
    ('aes128-ascii.pdf', 'user123', 'owner123'),
]
import pymupdf
import pypdf
fails = 0
def ok(c, what):
    global fails
    print(('OK    ' if c else 'BŁĄD  ') + what)
    if not c: fails += 1
for name, user, owner in cases:
    p = os.path.join(d, name)
    doc = pymupdf.open(p)
    ok(doc.is_encrypted == (user != '') or user == '', f'MuPDF {name}: needs_pass={doc.needs_pass}')
    if user:
        ok(doc.authenticate('zle-haslo') == 0, f'MuPDF {name}: złe hasło odrzucone')
        r = doc.authenticate(user)
        ok(r > 0, f'MuPDF {name}: hasło użytkownika przyjęte (kod {r})')
    t = doc[0].get_text()
    ok('gęślą' in t, f'MuPDF {name}: tekst z polskimi znakami po odszyfrowaniu ({t.strip()[:40]!r})')
    doc2 = pymupdf.open(p)
    r2 = doc2.authenticate(owner)
    ok(r2 >= 4 or (user == '' and r2 >= 1), f'MuPDF {name}: hasło właściciela przyjęte (kod {r2})')
    pix = doc[0].get_pixmap(dpi=40)
    pix.save(os.path.join(d, '_mupdf_' + name + '.png'))
    rd = pypdf.PdfReader(p)
    ok(rd.is_encrypted, f'pypdf {name}: zaszyfrowany')
    res = rd.decrypt(user)
    ok(int(res) > 0, f'pypdf {name}: decrypt hasłem użytkownika = {res!r}')
    ok('gęślą' in rd.pages[0].extract_text(), f'pypdf {name}: tekst')
    print(f'      pypdf /Encrypt: V={rd.trailer["/Encrypt"].get("/V")} R={rd.trailer["/Encrypt"].get("/R")}')
# redakcja i oczyszczanie – trzeci i czwarty silnik
for name, phrases in [('po redakcji ąę.pdf', ['44051401359', 'jan.kowalski@example.com', 'jaźń']), ('po oczyszczeniu ąę.pdf', ['Tajny', 'poufne'])]:
    p = os.path.join(d, name)
    doc = pymupdf.open(p)
    txt = ''.join(pg.get_text() for pg in doc)
    meta = doc.metadata
    xmp = doc.get_xml_metadata()
    for ph in phrases:
        ok(ph not in txt, f'MuPDF {name}: brak „{ph}” w tekście')
        ok(ph not in str(meta) and ph not in xmp, f'MuPDF {name}: brak „{ph}” w metadanych/XMP')
    rd = pypdf.PdfReader(p)
    t2 = ''.join(pg.extract_text() for pg in rd.pages)
    for ph in phrases:
        ok(ph not in t2, f'pypdf {name}: brak „{ph}” w tekście')
    print(f'      MuPDF metadane {name}: {meta}; XMP: {len(xmp)} znaków')
print('WYNIK:', 'OK' if fails == 0 else f'{fails} błędów')
