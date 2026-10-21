# Ograniczenia – edit-convert-ocr (ZetPDF.Editing, ZetPDF.Ocr)

## Edycja tekstu (T), Dodaj tekst, Wyszukaj i zamień
- Blok tekstu = jedna linia (PdfPig). Edycja akapitu wieloliniowego odbywa się linia po linii; nowy tekst z wieloma liniami jest wypisywany od linii bazowej edytowanego bloku w dół (odstęp 1,2 em), bez przelewania reszty strony.
- Stary tekst jest **usuwany ze strumienia treści** na poziomie glifów (glif zastępowany przesunięciem w TJ – reszta linii zostaje na miejscu). Obsługiwane: czcionki proste z `/Widths` (Type1/TrueType) i Type0 z kodowaniem `Identity-H/V`. **Nieobsługiwane** (rezerwa: biały prostokąt zakrywający stary tekst – stary tekst pozostaje w pliku i da się go wyodrębnić): czcionki Type3, CID z innymi CMapami niż Identity, 14 standardowych czcionek bez `/Widths`, tekst wewnątrz formularzy XObject, treść, której parser PDFsharp nie czyta (np. obrazy w linii BI/ID/EI).
- Po edycji treść strony jest zapisywana jako jeden strumień (dotychczasowe strumienie scalone).
- Nowy tekst jest pisany czcionką systemową (osadzoną jako TrueType/Identity-H z ToUnicode); oryginalna czcionka (podzbiór) nie jest ponownie używana – nazwa jest mapowana na rodzinę Windows (Arial, Times New Roman, Courier New, Calibri…; nieznane → Arial).
- Wyszukaj i zamień zastępuje całe linie zawierające frazę (linia przepisywana czcionką systemową w rozmiarze/kolorze/pogrubieniu oryginału). Trafienia rozdzielone między linie są liczone jako „niezamienione”. Tekst obrócony (nie poziomy) nie jest obsługiwany przez wykrywanie linii.

## Edycja obiektów (O), obrazy, kształty
- Wykrywane: obrazy i formularze (operator `Do` na najwyższym poziomie treści strony), ścieżki (kształty) i bloki tekstu BT…ET. Obiekty zagnieżdżone w formularzach XObject są traktowane jako całość formularza; obrazy w linii (BI/ID/EI) nie są wykrywane.
- Identyfikator obiektu jest ważny tylko dla bieżącej wersji dokumentu (narzędzie odświeża listę po każdej zmianie).
- Obrót w narzędziu: o 90° ([ ]) lub z menu kontekstowego; swobodny obrót myszą nie jest dostępny (API przyjmuje dowolną macierz).
- Wymiana obrazu wpisuje nowy obraz w prostokąt starego z zachowaniem proporcji. Kolory kształtów i tekstu – paleta podstawowa w polu edycji tekstu, kształty – kolor z ustawień modułu (domyślnie czerwony obrys, bez wypełnienia; brak okna wyboru koloru kształtu).
- Formaty obrazów: JPG/PNG/TIFF/BMP/GIF przez WIC; wszystko poza JPEG-iem przyjmowanym przez PDFsharp jest przekodowywane do PNG (przezroczystość PNG zachowana jako SMask).

## Łącza i zakładki z nagłówków
- Łącza tworzone jako adnotacje `/Link` z akcją `/URI` lub `/GoTo` (cel `/XYZ` albo `/Fit`). Łącza z celami nazwanymi są odczytywane jako `Other`.
- „Utwórz łącza z adresów URL” rozpoznaje adresy w obrębie jednego słowa PdfPig (adres przełamany między linie nie jest łączony); pomija miejsca, gdzie łącze już istnieje.
- Zakładki z nagłówków: heurystyka (rozmiar ≥ 1,15 × rozmiar podstawowy lub pogrubienie przy rozmiarze podstawowym; poziomy wg rozmiaru). Dokumenty bez różnic w czcionkach (np. skany po OCR) nie dają nagłówków. Zapis zastępuje całe drzewo zakładek (przez `IBookmarkService`, Cofnij przywraca).

## Konwersje
- **PDF → DOCX (podstawowy)**: akapity z linii tekstu (łączenie linii o tym samym rozmiarze i małym odstępie), rozmiar czcionki, pogrubienie, kursywa, kolor, rodzina czcionki (mapowana), obrazy osadzone PNG/JPEG (inne kodowania obrazów pomijane) wstawiane w przybliżonej kolejności pionowej, strony bez tekstu jako obraz strony 150 DPI, podział stron, rozmiar strony pierwszej strony PDF. **Brak**: tabel, kolumn (tekst wielokolumnowy może się przeplatać), pozycjonowania bezwzględnego, nagłówków/stopek, stylów, list, przypisów, hiperłączy, pól formularzy, adnotacji.
- PDF → TXT: kolejność linii od góry do dołu; tryb „z układem” to siatka znaków przybliżona średnią szerokością znaku (tekst wielokolumnowy w trybie bez układu łączy kolumny wierszami).
- PDF → obrazy: render PDFium z adnotacjami i polami; nazwy `{nazwa}_{nr}`; istniejące pliki o tej samej nazwie są zastępowane.
- Obrazy → PDF: rozmiar „jak obraz” wg DPI zapisanego w pliku (brak DPI → 96); A4/Letter z automatyczną orientacją i dopasowaniem.
- Scalanie: PDF zaszyfrowane hasłem nie są obsługiwane w scalaniu (błąd); TXT – UTF-8, w razie błędów dekodowania Windows-1250; czcionka Arial 11 pt, A4.

## OCR
- Tesseract 5.2.0, dane **tessdata_fast** pol + eng (bez zmian – patrz niżej). Render strony PDFium 300 DPI bez adnotacji, skala szarości, segmentacja automatyczna (obszar: pojedynczy blok). Brak automatycznego wykrywania orientacji tekstu (OSD) i prostowania – tekst obrócony na obrazie nie zostanie rozpoznany (strony z `/Rotate` są obsługiwane poprawnie).
- Porównanie danych (test 4 zestawów obrazów, Times New Roman 7–10 pt, 150–300 DPI, szum): tessdata (standard) dla `pol` nie dał wyraźnie lepszej jakości niż tessdata_fast (CER 0–2,1% vs 0–1,4%), tessdata_best był lepszy w jednym przypadku i gorszy w innym; render PDFium 300 DPI rozpoznaje „Zażółć gęślą jaźń” poprawnie także z tessdata_fast. Dane nie zostały podmienione (i nic nie pobierano z sieci).
- Warstwa tekstu: słowa w trybie renderowania 3 (Arial osadzony, Identity-H z ToUnicode), pozycja z prostokąta słowa, rozmiar z wysokości linii, skala pozioma dopasowana do szerokości słowa – zaznaczenie w przeglądarkach może nieznacznie odbiegać od obrazu w pionie (linia bazowa szacowana).
- Ponowne OCR strony zastępuje poprzednią warstwę ZetPDF (strumień ze znacznikiem `%ZetPDF:ocr`); po innych edycjach strony (które scalają strumienie) znacznik znika i ponowne OCR z wyłączonym pomijaniem doda drugą warstwę.
- „Pomiń strony z tekstem” traktuje stronę jako tekstową, gdy PDFium znajduje ≥ 3 litery/cyfry (także z poprzedniej warstwy OCR).
- Anulowanie działa między stronami (rozpoznawanie pojedynczej strony nie jest przerywane w trakcie). Przegląd podejrzanych: do 500 słów naraz. „Podejrzane wyniki” dotyczą ostatniego OCR w bieżącej sesji i są dostępne tylko, jeśli dokument nie zmienił się od tego czasu.

## Redakcja
- **Metoda: rasteryzacja stron z oznaczeniami.** Strona renderowana w 300 DPI (PDFium, bez adnotacji), obszary zamalowane w bitmapie, cała treść i zasoby strony zastąpione jednym obrazem PNG, ukryta warstwa tekstu odtworzona tylko ze słów PDFium spoza obszarów (słowo częściowo w obszarze – zostają tylko znaki spoza obszaru). Usuwane: adnotacje `/Redact`, adnotacje i pola przecinające obszary (widżety także z `/AcroForm`), miniatura strony `/Thumb`, `/PieceInfo`, metadane strony; w dokumencie: wpisy `/Info` i XMP zawierające zredagowane słowa, frazy w tytułach zakładek (zastępowane `***`).
- **Koszt**: strona po redakcji jest obrazem – traci wektory, czcionki, możliwość edycji tekstu, rośnie rozmiar pliku (obraz 2480×3508 px na stronę A4, PNG/Flate); tekst pozostały jest wyszukiwalny przez ukrytą warstwę (pozycje przybliżone); łącza i adnotacje poza obszarami zostają.
- Fraza występująca poza oznaczonymi obszarami (np. na innej stronie, w załącznikach, w nazwach plików załączników, w JavaScript, w polach formularza poza obszarem) nie jest usuwana – redagowane jest tylko to, co oznaczono (plus metadane).
- Wzorce: PESEL (suma kontrolna + miesiąc/dzień), NIP (suma kontrolna; formaty ciągły i z łącznikami), e-mail, telefon PL (9 cyfr, opcjonalnie +48/0048), karta 13–19 cyfr (Luhn), IBAN PL (mod 97). Telefon bez sumy kontrolnej może dawać fałszywe trafienia – dlatego lista trafień jest do zatwierdzenia.

## Interfejs
- Okna budowane w kodzie (bez XAML), teksty z `Resources\Strings*.resx` (pl/en), kolory z kluczy motywu; kolory na liście w polu edycji tekstu to kolory treści PDF.
- Nie wykonano ręcznego przeklikania wszystkich okien w uruchomionej aplikacji – sprawdzono uruchomienie aplikacji z modułem (tryb `--diag-screenshots`, polecenia Edytuj tekst/obiekt aktywne na wstążce) oraz silniki testami automatycznymi.

## Etap 3: Ułatwienia dostępu (Access.*)
- Sprawdzane reguły: tytuł (Info lub XMP `dc:title`), `/DisplayDocTitle`, `/Lang`, `/MarkInfo /Marked`, `/StructTreeRoot`, `/Alt` (lub `/ActualText`) elementów `/Figure` (także przez `/RoleMap`), `/Tabs /S` na stronach z adnotacjami, `/TU` pól końcowych AcroForm, obecność tekstu (strona z obrazem i < 3 literami wg PdfPig = prawdopodobny skan), zakładki dla dokumentów > 20 stron, kolejność odczytu/tagów – zawsze „sprawdź ręcznie”. To podzbiór PDF/UA – brak m.in. kontroli tabel, list, nagłówków, kontrastu, czcionek osadzonych, adnotacji bez treści, artefaktów, zgodności drzewa struktury z treścią (MCID).
- Napraw: tytuł i język (wartości potwierdzane w oknie; propozycje: nazwa pliku, język interfejsu), `/DisplayDocTitle`, `/Tabs /S`, `/TU` z nazwy pola (`_` i `.` → spacje). **Nie naprawiane automatycznie**: tagowanie, drzewo struktury, kolejność odczytu, skany (zalecenie OCR), zakładki (wskazówka: Zakładki z nagłówków). Tekst alternatywny figur – ręcznie w oknie „Tytuł, język i tekst alternatywny” (tylko dokumenty otagowane; figury identyfikowane numerem i stroną, bez podglądu obrazu).
- Tytuł zapisywany w `/Info`; w XMP aktualizowany tylko, jeśli strumień `/Metadata` zawiera już `dc:title` (strumień jest wtedy zapisywany bez kompresji). Nowy XMP nie jest tworzony.
- Panel wyników odświeża się automatycznie po każdej zmianie sprawdzonego dokumentu (także Cofnij). Raport: HTML (bez kolorów, tabela) lub TXT w języku interfejsu.

## Etap 3: Indeks pełnotekstowy i wyszukiwanie zaawansowane (Index.*)
- **Wybór: własny magazyn tekstu zamiast SQLite FTS5.** Indeks folderu to plik `%LOCALAPPDATA%\ZetPDF\index\<skrót ścieżki>.zidx` (GZip, tekst stron z PdfPig, rozmiar i data modyfikacji plików). Wyszukiwanie przegląda znormalizowany tekst w pamięci – obsługuje frazy i fragmenty słów, polskie znaki bez tokenizera, wyszukiwanie bez wielkości liter i opcjonalnie bez diakrytyków (ą=a, ł=l…), i nie wymaga natywnej biblioteki `e_sqlite3` w publikacji single-file. Koszt: wyszukiwanie liniowe – przy bardzo dużych folderach (dziesiątki tysięcy stron) czas rośnie liniowo, a cały tekst indeksu jest wczytywany do pamięci na czas wyszukiwania.
- Tekst: słowa PdfPig w kolejności treści (bez OCR – skany bez warstwy tekstu nie są wyszukiwalne). Fraza nie jest odnajdywana, jeśli w PDF jest przeniesiona z dywizem; wielokrotne spacje w pliku mogą przeszkadzać w wyszukiwaniu fraz.
- Aktualizacja przyrostowa: plik jest ponownie indeksowany, gdy zmienił się rozmiar lub data modyfikacji; zmiana opcji „z podfolderami” buduje indeks od nowa. Anulowanie zapisuje dotąd przetworzone pliki. Pliki zaszyfrowane hasłem otwarcia i nieczytelne są pomijane (lista w komunikacie, liczba w wynikach). Brak automatycznej aktualizacji w tle (trzeba wywołać ponownie „Indeks pełnotekstowy folderu”) i brak usuwania indeksów z poziomu UI (pliki w folderze `index`).
- Wyszukiwanie zaawansowane jest **panelem bocznym** (nie osobnym oknem): bieżący dokument, wszystkie otwarte dokumenty (tekst z bieżącej wersji sesji, także niezapisanej; dokumenty bez prawa kopiowania są pomijane) albo indeks folderu. Limit 2000 wyników na wyszukiwanie. Przejście do trafienia otwiera plik (z pytaniem o hasło, jeśli potrzeba), przewija do strony i podświetla n-te wystąpienie frazy na stronie wg tekstu PDFium (gdy PDFium i PdfPig różnie dzielą tekst, podświetlane jest pierwsze wystąpienie lub nic).
- Eksport CSV: UTF-8 z BOM, separator listy kultury interfejsu (pl: `;`), wszystkie pola w cudzysłowie, wartości zaczynające się od `=`, `+`, `-`, `@`, tabulatora lub CR poprzedzone apostrofem (ochrona przed formułami w arkuszu).
- Okna/panele etapu 3 sprawdzono testami budowania i układu (WPF na wątku STA) oraz testem integracyjnym poleceń; nie przeklikano ich ręcznie w uruchomionej aplikacji (test dymny exe nie obejmuje karty Ułatwienia dostępu).
