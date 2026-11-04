# ZetPDF 0.1.0-test – znane ograniczenia

Dokument zbiorczy dla użytkownika i testera. Opisuje, czego program nie robi albo robi inaczej, niż można by się spodziewać, pogrupowane według funkcji z etapów 1 i 2; dodatki etapu 3 – rozdział 26. Szczegóły techniczne dla programistów są w plikach modułów `docs\ograniczenia\*.md`, uzasadnienia decyzji – w `docs\DECYZJE.md`.

## 0. Ogólne

- **Model pracy na dokumencie.** Cały plik jest trzymany w pamięci, a każda zmiana zapisuje go od nowa (tak działa Cofnij/Ponów). Przy bardzo dużych plikach (setki MB) każda edycja trwa dłużej; pliki powyżej ok. 1 GB nie są obsługiwane. Historia Cofnij ma budżet 256 MB na dokument – starsze kroki trafiają do folderu tymczasowego (dla plików zaszyfrowanych chronione DPAPI).
- **PDFsharp zostaje na wersji 6.2.4** (nowsze mają licencję AGPL), więc poprawki tej biblioteki z nowszych wersji nie trafią do programu.
- **Program nie łączy się z siecią.** Jedyny wyjątek to znacznik czasu podpisu (TSA) pod adresem wpisanym przez użytkownika – wysyłany jest tylko skrót SHA-256. Łącza w dokumentach otwierają się w przeglądarce systemowej dopiero po potwierdzeniu (tylko http, https, mailto).
- **Okna wyboru plików i folderów** są systemowe – ich język i motyw zależą od Windows, nie od ustawień ZetPDF.
- **Interfejs nie był przeklikany ręcznie na pulpicie** (sesja Windows była zablokowana w czasie budowy i kontroli). Sprawdzono go testami automatycznymi, renderem okien do PNG (`ZetPDF.exe --diag-screenshots`) i uruchomieniem exe z plikami (szczegóły w `docs\RAPORT_KONTROLI.md`).

## 1. Otwieranie, zapis, karty (etap 1.1)

- PDFium rozróżnia „brak hasła” i „złe hasło” tylko po tym, czy hasło podano – w obu przypadkach pojawia się okno hasła.
- Uszkodzony plik: PDFium próbuje naprawy, więc obcięty plik może się otworzyć z częścią stron zamiast komunikatu o błędzie; dalsze zmiany takiego pliku mogą się nie udać (pojawi się komunikat).
- Przy zapisie powstaje plik tymczasowy w tym samym folderze, który potem zastępuje oryginał (kopia `.bak` jest usuwana po sukcesie). W folderze tylko do odczytu albo przy pliku zablokowanym przez inny program zapis kończy się komunikatem, oryginał zostaje nietknięty.
- Folder tymczasowy zmieniony w Ustawieniach działa po ponownym uruchomieniu programu.

## 2. Podgląd (etap 1.2)

- „Dopasuj widoczne” (Ctrl+3) jest przybliżone – dopasowuje szerokość strony bez analizy obszaru treści.
- Przy dużym powiększeniu renderowany jest tylko widoczny fragment strony; przy szybkim przewijaniu przez chwilę widać rozmyty podgląd.
- Miniatury pokazują stronę z obrotem zapisanym w pliku, bez obrotu widoku. Pamiętanych jest ok. 160 obrazów miniatur.
- Tryb odczytu i pełny ekran ukrywają wstążkę i panele; wyjście klawiszem Esc.
- Pasek tytułu okien dialogowych przełącza się na ciemny/jasny dopiero po zmianie aktywnego okna, jeśli motyw zmieniono przy otwartym oknie.
- Brak skrótów Alt+litera dla kart wstążki (KeyTips).
- Nie przeniesiono skrótów z raportu Foxita dla funkcji spoza etapów 1–2 (np. Ctrl+4/5/6, Ctrl+Shift+F, czytanie na głos, e-mail) ani `Alt+3/6/7` (kolidują z wstążką). Skróty jednoliterowe (H, V, T, …) działają dopiero po włączeniu w Ustawieniach i nigdy w polu tekstowym.

## 3. Wyszukiwanie, zaznaczanie, migawka (etap 1.3)

- Wyrazy przeniesione z dywizem („przeno-szone”) nie są łączone przy wyszukiwaniu.
- Zaznaczenie tekstu obróconego o kąt inny niż 0/90/180/270° ma prostokąt otaczający; brak zaznaczania kolumnowego.
- Jeśli schowek Windows jest zajęty przez inny program (lub sesja jest zablokowana), kopiowanie kończy się komunikatem „Schowek jest teraz niedostępny”.

## 4. Miniatury i zakładki (etap 1.4)

- PDFsharp 6.2.4 nie zapisuje licznika `/Count` zakładek – ZetPDF ustawia go sam. Kolory zakładek nie są obsługiwane; zakładki z celem nazwanym, którego nie da się rozwiązać, nie mają strony; zakładki z akcją inną niż „przejdź do strony” (np. adres URL) tracą akcję po edycji drzewa.

## 5. Organizacja stron (etap 1.5)

- Strony wyodrębnione lub scalone z polami formularza mają widoczne pola, ale nowy dokument nie ma formularza AcroForm (pól nie da się wypełnić).
- Kadrowanie z opcją „usuń białe marginesy” pomija strony puste.
- Zmiana rozmiaru stron przelicza położenie komentarzy (prostokąty, linie, rysunki); ramki Trim/Bleed/Art są usuwane.
- Dzielenie według zakładek: strony przed pierwszą zakładką tworzą osobną część. Dzielenie według rozmiaru mierzy faktyczny zapis; strona większa od limitu tworzy osobny plik.
- Scalanie i łączenie plików nie przyjmuje plików zaszyfrowanych hasłem otwarcia (komunikat).

## 6. Znak wodny, tło, nagłówek i stopka, numeracja, Bates (etap 1.6)

- Oznaczenia są rozpoznawalne tylko wtedy, gdy dodał je ZetPDF (własne znaczniki w pliku) – znaków wodnych dodanych w innych programach „Aktualizuj/Usuń” nie zobaczy.
- Numeracja stron jest osobnym oznaczeniem – usuwa się ją w oknie Numeracja stron, nie poleceniem „Usuń nagłówki i stopki”.
- Osadzany jest tylko podzbiór znaków czcionki użytych w oznaczeniu. Obrazy: JPEG, PNG (z przezroczystością), BMP.
- Polecenia Aktualizuj/Usuń są zawsze aktywne; gdy w dokumencie nie ma oznaczeń danego rodzaju, pojawia się komunikat.

## 7. Komentarze (etap 1.7)

- Pokaż/Ukryj komentarze działa tylko w bieżącym widoku (plik się nie zmienia, stan nie jest pamiętany). Miniatury i druk pokazują komentarze niezależnie od przełącznika. W plikach bez formularza AcroForm widżety pól znikają razem z komentarzami.
- Jedna ikona notatki niezależnie od jej typu w pliku; okienka notatek tworzone zamknięte.
- Pole tekstowe: pogrubienie i kursywa nieobsługiwane; prostokąt nie dopasowuje się do zmienionej czcionki; brak edycji w miejscu po dwukliku (tylko okno Właściwości).
- Zmiana rozmiaru stempla z obrazu skaluje istniejący wygląd bez ponownego próbkowania obrazu.
- Stan recenzji: jedna pozycja stanu na autora, bez pełnej historii; model „Marked” nieobsługiwany.
- XFDF: stemple z obrazu i podpisy odręczne eksportowane bez obrazu (po imporcie stempel tekstowy); załączniki pomijane (są w FDF); tekst sformatowany czytany jako zwykły. FDF: własny parser, bez strumieni obiektów i szyfrowanych FDF.

## 8. Formularze i Wypełnij i podpisz (etap 1.8)

- Wypełnianie działa narzędziem Ręka (i Zaznacz tekst); brak formatowania i obliczeń JavaScript. Pola hasła, pola grzebieniowe i wielokrotny wybór na liście nie są obsługiwane przy wypełnianiu (zapisywana pierwsza wartość).
- Wygląd pól zawiera osadzony Arial; inny czytnik, który sam odtwarza wygląd pola przy edycji, może nie mieć polskich znaków spoza WinAnsi.
- „Spłaszcz przy zapisie” spłaszcza tylko zapisywany plik; otwarty dokument zostaje edytowalny, a opcja nie jest pamiętana po zamknięciu.
- Podpis odręczny zapisywany jako PNG z przezroczystością; przy obrazie białe tło zamieniane na przezroczyste.

## 9. Ochrona hasłem (etap 1.9)

- **AES-256 w czytnikach.** Pliki zaszyfrowane przez ZetPDF (AES-256, V5/R6) otwierają się w PDFium, PdfPig, MuPDF i pypdf (sprawdzone). **Wbudowany w Windows silnik `Windows.Data.Pdf` w ogóle nie otwiera AES-256** (także plików z innych programów) – to ograniczenie Windows, nie ZetPDF.
- **Ponowne szyfrowanie RC4 i AES-128 (DECYZJE D-22).** Plik zaszyfrowany RC4 (przestarzałe, niebezpieczne) otwarty hasłem i zapisany jest szyfrowany ponownie **AES-256** z tymi samymi hasłami i uprawnieniami; AES-128 i AES-256 zostają bez zmian. Po zapisie pasek stanu podaje: „Szyfrowanie RC4 … zastąpiono AES-256”. Bardzo stare czytniki (sprzed PDF 1.7 ExtensionLevel 3) mogą nie otworzyć takiego pliku.
- **Plik otwarty samym hasłem otwarcia (DECYZJE D-08).** Hasło uprawnień jest nieznane, więc przy zapisie ZetPDF nadaje nowe losowe hasło uprawnień (te same ograniczenia; odbiorca i tak potrzebuje hasła otwarcia). Pierwotnego hasła uprawnień nie da się zachować bez jego znajomości.
- **Plik z samym hasłem uprawnień, otwarty bez niego (DECYZJE D-23).** Przed pierwszym zapisem program pyta: podaj hasło uprawnień (ochrona zostaje taka jak w pliku), ustaw nowe hasło uprawnień (AES-256) albo zapisz bez ochrony. Wybór obowiązuje do zamknięcia dokumentu lub polecenia Przywróć. Uwaga: plik z pustym hasłem otwarcia może odszyfrować każdy program – ograniczenia działają tylko w czytnikach, które je respektują (ZetPDF je respektuje).
- **Plik otwarty hasłem uprawnień, gdy nieznane jest niepuste hasło otwarcia (AES-256)** – hasła otwarcia nie da się odtworzyć; przy zapisie hasłem otwarcia staje się hasło uprawnień (komunikat na pasku stanu i we Właściwościach zabezpieczeń). Dla RC4/AES-128 hasło otwarcia jest odzyskiwane.
- AES-128: hasła tylko ze znaków Latin-1 (okno blokuje polskie litery); AES-256 obsługuje polskie znaki.
- Zmiana zabezpieczeń dokumentu otwartego hasłem otwarcia wymaga podania hasła uprawnień (dokument otwiera się ponownie; niezapisane zmiany przepadają po potwierdzeniu).
- Bit uprawnień „ułatwienia dostępu” nie jest osobno ustawiany (PDFsharp 6.2.4).

## 10. Drukowanie (etap 1.10)

- Rozdzielczość druku maks. 300 DPI. Orientacja papieru nie jest przełączana w sterowniku – „orientacja auto” obraca treść strony. Podgląd zakłada A4. Druk kilku stron na arkuszu zawsze dopasowuje strony do komórek. Kopie i sortowanie zależą od sterownika drukarki. Druku na fizycznej drukarce nie testowano (sprawdzono tę samą ścieżkę do pliku XPS).

## 11. Ustawienia (etap 1.11)

- Szerokość panelu bocznego zapisywana w osobnej sekcji `Shell` pliku ustawień.
- Brak stylu motywu dla kalendarza (DatePicker/Calendar).

## 12. Cofnij/Ponów (etap 1.12)

- Podpis cyfrowy jest zapisem do pliku i nie podlega Cofnij. Redakcja da się cofnąć tylko do zamknięcia dokumentu (po zapisie treść jest usunięta na stałe).

## 13. Edycja tekstu (etap 2.1)

- Blok tekstu = jedna linia; akapit wieloliniowy edytuje się linia po linii, bez przelewania reszty strony.
- Stary tekst jest usuwany ze strumienia dla czcionek z tabelą szerokości i czcionek CID `Identity-H/V`. **Dla czcionek Type3, innych CID, 14 standardowych bez szerokości, tekstu w formularzach XObject – stary tekst jest tylko zakrywany białym prostokątem i pozostaje w pliku** (da się go wyodrębnić).
- Nowy tekst pisany jest czcionką systemową (nazwa oryginalnej czcionki mapowana na Arial/Times New Roman/Courier New/Calibri…), nie oryginalnym podzbiorem.
- Wyszukaj i zamień przepisuje całe linie; trafienia rozdzielone między linie są liczone jako niezamienione; tekst obrócony nie jest obsługiwany.

## 14. Edycja obiektów (etap 2.2)

- Wykrywane są obrazy i formularze na najwyższym poziomie treści strony, kształty i bloki tekstu; obrazy w linii (BI/ID/EI) nie są wykrywane.
- Obrót o 90° (klawisze [ ] lub menu), bez swobodnego obrotu myszą. Brak okna wyboru koloru kształtu (kolor z ustawień modułu).

## 15. Łącza i zakładki z nagłówków (etap 2.3)

- Łącza z celami nazwanymi są odczytywane jako „inne”. Adres przełamany między linie nie jest rozpoznawany jako URL.
- Zakładki z nagłówków to heurystyka (rozmiar/pogrubienie); skany po OCR nie dają nagłówków. Zapis zastępuje całe drzewo zakładek (Cofnij przywraca).

## 16. Konwersje (etap 2.4)

- **PDF → DOCX (podstawowy):** akapity, rozmiar, pogrubienie, kursywa, kolor, rodzina czcionki, obrazy PNG/JPEG, strony bez tekstu jako obraz 150 DPI. **Brak:** tabel, kolumn (tekst wielokolumnowy może się przeplatać), pozycjonowania, nagłówków/stopek, stylów, list, przypisów, hiperłączy, pól, komentarzy.
- PDF → TXT: kolejność od góry do dołu; tryb „z układem” to przybliżona siatka znaków.
- PDF → obrazy: istniejące pliki o tej samej nazwie są zastępowane.
- Obrazy → PDF: brak DPI w pliku = 96 DPI. Pliki tekstowe: UTF-8, w razie błędów Windows-1250.

## 17. OCR (etap 2.5)

- Tesseract 5.2.0, dane `tessdata_fast` (pol + eng). Brak wykrywania orientacji i prostowania – tekst obrócony na obrazie nie zostanie rozpoznany (strony z obrotem w pliku są obsługiwane).
- Warstwa tekstu: zaznaczenie może nieznacznie odbiegać od obrazu w pionie.
- Ponowne OCR zastępuje poprzednią warstwę ZetPDF tylko, jeśli strona nie była w międzyczasie edytowana.
- Anulowanie działa między stronami. Przegląd podejrzanych słów: do 500 naraz, tylko dla ostatniego OCR, jeśli dokument się nie zmienił.
- Przy pierwszym OCR dane Tesseract są rozpakowywane z exe do `%LOCALAPPDATA%\ZetPDF\ocr\5.2.0\` (ok. 19 MB, bez sieci).

## 18. Redakcja (etap 2.6)

- **Metoda: strona z oznaczeniami jest zamieniana na obraz 300 DPI** z zamalowanymi obszarami i odtworzoną ukrytą warstwą tekstu (bez słów z obszarów). Treść jest trwale usunięta (sprawdzone w PdfPig, PDFium, MuPDF, pypdf i zdekodowanych strumieniach), ale strona traci wektory i czcionki, rośnie rozmiar pliku, a pozycje tekstu wyszukiwalnego są przybliżone.
- Usuwane są tylko oznaczone miejsca (plus wpisy metadanych i tytuły zakładek z tymi słowami). Fraza poza oznaczeniami (inne strony, załączniki, JavaScript, pola formularza poza obszarem) zostaje – do tego służy Oczyszczanie.
- Wzorzec telefonu nie ma sumy kontrolnej i może dawać fałszywe trafienia – lista trafień jest do zatwierdzenia.

## 19. Oczyszczanie dokumentu (etap 2.7)

- PDFsharp przy każdym zapisie dopisuje informację o programie (`Producer`/`Creator` = PDFsharp), daty i minimalny pakiet XMP. Po oczyszczeniu w pliku nie ma tytułu, autora, tematu, słów kluczowych ani starego XMP, ale jest informacja o programie.
- „Łącza do plików zewnętrznych” = akcje Launch, GoToR, GoToE, ImportData, SubmitForm i adresy `file:`; łącza http(s) zostają.

## 20. Optymalizator i skala szarości (etap 2.8)

- Kompresowane są obrazy 8-bitowe szare/RGB; maski, CMYK, JPX, CCITT i obrazy w wyglądach komentarzy zostają bez zmian. Gdy zapis nie zmniejszyłby pliku, zwracany jest oryginał.
- Skala szarości bez rasteryzacji nie zmienia kolorów dodatkowych (Separation/DeviceN), części cieniowań, obrazów w linii, JPEG CMYK, JPX i obrazów 16-bitowych – dla takich plików jest opcja rasteryzacji (150 DPI, tekst przestaje być tekstem).

## 21. Podpisy cyfrowe i znacznik czasu (etap 2.9)

- Format `adbe.pkcs7.detached`, SHA-256; brak profilu PAdES-B-B i danych długoterminowej weryfikacji (LTV).
- Wcześniejsze podpisy zostają ważne tylko wtedy, gdy dokument nie był zmieniany od otwarcia (każda edycja w ZetPDF to pełny zapis, który – zgodnie z prawdą – unieważnia podpisy).
- Tekst widocznego podpisu jest rysowany konturami (nie da się go zaznaczyć).
- Weryfikacja: łańcuch certyfikatów tylko z magazynów Windows, **bez sprawdzania unieważnień** (CRL/OCSP wymagałyby sieci); „zmieniono po podpisaniu” nie rozróżnia zmian dozwolonych (DocMDP/FieldMDP). Format `adbe.x509.rsa_sha1` nieobsługiwany.
- **Znacznika czasu z prawdziwym serwerem TSA nie testowano** (brak sieci) – tylko lokalna atrapa i nieosiągalny adres.
- Certyfikaty z magazynu: tylko CurrentUser\My z kluczem prywatnym; karty kryptograficzne mogą pokazać własne okno PIN.

## 22. Tworzenie pól formularza (etap 2.10)

- Pole daty to pole tekstowe z prywatnym znacznikiem – inne czytniki nie walidują daty ani nie pokazują kalendarza.
- Kolejność tabulacji zapisywana jako kolejność widżetów + `/Tabs /W` (PDF 2.0); starsze czytniki mogą stosować własną.
- Zmiana wartości eksportu istniejącego pola wyboru/opcji nie jest przenoszona; zmiana nazwy dotyczy tylko końcowej części nazwy.

## 23. Porównanie dokumentów (etap 2.11)

- Tekst porównywany słowo po słowie; skany bez warstwy tekstu – tylko porównanie graficzne (36 DPI, tylko strony tego samego rozmiaru bez obrotu). Dopasowanie stron heurystyczne. Wyniki w osobnym oknie.

## 24. Kreator działań (etap 2.12)

- „Archiwizuj” **nie tworzy PDF/A** (uzupełnia metadane i usuwa skrypty).
- Pliki zaszyfrowane nie są przetwarzane (błąd w raporcie). Krok OCR zawsze na wszystkich stronach. Znak wodny z obrazu nie jest edytowalny w edytorze kroków. Hasła kroku „Chroń hasłem” nie są zapisywane – okno pyta o nie przed uruchomieniem.

## 25. Publikacja i licencje

- `ZetPDF.exe` jest jednym plikiem (PDFium i Tesseract w środku). Natywne biblioteki są przy starcie rozpakowywane przez .NET do folderu tymczasowego użytkownika, dane OCR – do `%LOCALAPPDATA%\ZetPDF\ocr\`.
- W `dist\LICENSES\` brakuje pełnych tekstów licencji FreeType, IJG, libpng i Unicode/ICU (komponenty PDFium) – trzeba je dołączyć z repozytorium pdfium-binaries przed dystrybucją komercyjną (`docs\LICENCJE.md`, sekcja 4).

## 26. Etap 3 (dodatki)

Zbiorczo z `docs\ograniczenia\annotations-forms.md` (pkt 17–25), `renderer-viewer.md` (pkt 15–23) i `edit-convert-ocr.md` (sekcje „Etap 3”), z wynikami kontroli recenzenta (`docs\RAPORT_KONTROLI.md`, rozdz. 9).

### 26.1 Pomiary i skala
- Pomiary (odległość, obwód, pole) zapisywane są jako adnotacje wymiarowe ze słownikiem `/Measure` i własnym wyglądem. Tylko skala prostoliniowa (ta sama w osi X i Y), bez kąta, promienia, pola koła, objętości i współrzędnych geograficznych. Skala strony – jeden obszar na całą stronę (obszary z innych programów są odczytywane, nie edytowane).
- Etykieta jest pozioma (nie obraca się z linią); obrót strony nie jest uwzględniany przy jej rozmieszczeniu. Inny czytnik, który sam odtwarza wygląd, może pokazać pomiar inaczej. Eksport XFDF nie przenosi skali (pomiar wraca jako zwykła linia/wielokąt).
- Kalibracja z jednego odcinka; jednostki: mm, cm, m, km, in, ft, yd, pt.

### 26.2 Kalkulator księgowy
- Okno modalne; liczby z zaznaczenia dodawane przy otwarciu (taśma zachowana do zamknięcia programu). Sumowane są wszystkie liczby z zaznaczenia (także np. numery pozycji). Przy niejednoznacznym „1,234”/„1.234” decyduje język interfejsu. Wynik wstawiany jako pole tekstowe (maszyna do pisania).

### 26.3 Pole kodu kreskowego/QR
- QR, PDF417, DataMatrix (ZXing.Net); dane z wybranych pól (tabulatory albo JSON), rysunek wektorowy w wyglądzie pola. Kod odświeża się tylko przy zmianach pól w ZetPDF – inny program go nie zaktualizuje (brak skryptu Acrobata). Zbyt dużo danych – przekreślona ramka.

### 26.4 JavaScript w formularzach
- Silnik Jint działa **w procesie programu** w piaskownicy (bez dostępu do CLR, plików, sieci, `eval`, `new Function`). Limity na skrypt: 750 ms, 250 000 instrukcji, 16 MB, rekursja 64, tablice 100 000, wyrażenia regularne 200 ms, łańcuchy z funkcji wbudowanych do 1 MB, kod do 256 KB; łącznie na zmianę 2 s i 1000 zdarzeń. Po powtarzanych przekroczeniach lub braku pamięci skrypty dokumentu wyłączają się.
- **Domyślnie** wykonywane są tylko wywołania wbudowanych funkcji AF* z literałami; dowolny JavaScript pól – po zgodzie użytkownika (ustawienie dokumentu, niezapisywane). JavaScript dokumentu (przy otwarciu, akcje dokumentu) i skrypty przycisków **nigdy** nie są wykonywane – tylko podgląd.
- Obsługiwane zdarzenia pól: naciśnięcie klawisza (tylko przy zatwierdzeniu wartości), walidacja, obliczenie (kolejność `/CO`), format (tylko tekst wyświetlany). Minimalne API Acrobata (`event`, `getField`, `app.alert` → pasek stanu, wybrane funkcje AF*); brak m.in. `util.printf`, `AFTime_*`, `resetForm/submitForm/print`, akcji myszy.
- Kontrola recenzenta znalazła i naprawiła obejścia limitów pamięci (do kilku GB w jednej instrukcji); ryzyko szczątkowe – nieprzewidziana funkcja wbudowana Jint – dotyczy tylko trybu „dowolny JavaScript”. Pełna izolacja wymagałaby osobnego procesu.
- Komunikaty błędów skryptów (w raporcie skryptów) zawierają techniczny tekst silnika po angielsku (np. „ZetPDF limit: string too long”, komunikaty Jint) – są przeznaczone dla autora formularza.

### 26.5 Czytanie na głos
- System.Speech (SAPI 5): tylko głosy SAPI zainstalowane lokalnie (głosy „OneCore” widoczne tylko w Ustawieniach Windows mogą być niedostępne). Tekst z PDFium w kolejności strumienia treści – dokumenty wielokolumnowe czytane w tej kolejności, tagi nie są używane; skany pomijane.
- Zmiana głosu/szybkości/głośności działa od następnej wypowiedzi. Podświetlane jest słowo zgłoszone przez syntezator (liczby/skróty mogą być dzielone).
- Czytanie zatrzymuje się przy zamknięciu dokumentu, przy każdej zmianie dokumentu i przy zamknięciu programu (sprawdzone w exe na pliku 500 stron: czytanie do końca pozostawione włączone → zatrzymane po zamknięciu dokumentów, proces zakończył się sam).
- Panel ustawień czytania to małe niemodalne okno; jego zamknięcie wyłącza tryb czytania.

### 26.6 Widok równoległy
- Realizowany wewnątrz widoku aktywnej karty; tylko **dwa** dokumenty; drugi – tylko podgląd (narzędzie Ręka, bez edycji). Zamknięcie którejkolwiek karty zamyka widok równoległy. Synchronizacja przewija o tę samą odległość (bez dopasowania numerów stron przy różnych rozmiarach).

### 26.7 Sprawdzanie ułatwień dostępu
- Podzbiór PDF/UA: tytuł, wyświetlanie tytułu, język, tagi, drzewo struktury, tekst alternatywny figur, kolejność tabulacji, opisy pól, obecność tekstu, zakładki; kolejność odczytu zawsze „sprawdź ręcznie”. Brak m.in. tabel, list, nagłówków, kontrastu, czcionek, artefaktów, zgodności drzewa struktury z treścią.
- Automatyczna naprawa tylko: tytuł i język (wartości potwierdzane w oknie), wyświetlanie tytułu, kolejność tabulacji, opisy pól. Tagowanie, kolejność odczytu, skany – nie. Tekst alternatywny – ręcznie, tylko w dokumentach otagowanych.
- Raport HTML/TXT w języku interfejsu; tekst z dokumentu (tytuł, nazwa pliku) jest escapowany w HTML (test z `<script>` w tytule).

### 26.8 Indeks pełnotekstowy i wyszukiwanie zaawansowane
- Własny magazyn tekstu (GZip) zamiast SQLite FTS5 (brak natywnych bibliotek w jednym pliku exe). Wyszukiwanie liniowe – przy dziesiątkach tysięcy stron wolniejsze, cały tekst indeksu w pamięci na czas wyszukiwania. Limit 2000 wyników.
- Tekst z PdfPig bez OCR (skany niewyszukiwalne); frazy przeniesione z dywizem nie są odnajdywane. Indeks aktualizowany ręcznie (brak aktualizacji w tle) i bez usuwania z UI. Pliki zaszyfrowane hasłem otwarcia i nieczytelne są pomijane (lista w komunikacie). Uszkodzony plik indeksu jest ignorowany i budowany od nowa.
- Eksport CSV wyników: separator listy kultury, pola w cudzysłowie, neutralizacja formuł (apostrof przed `=`, `+`, `-`, `@`, tabulatorem, CR). Ta sama ochrona dotyczy eksportu pomiarów i danych formularza do CSV.

### 26.9 Wspólne
- Okna i panele etapu 3 nie były klikane myszą na pulpicie; sprawdzono je testami (budowa okien, polecenia), testem dymnym exe (`--diag-smoke`: Access.FullCheck, Index.Search, Measure.Calculator, Form.JavaScript, View.ParallelView z drugim dokumentem, Read.Activate/CurrentPage/Stop/FromPage bez dźwięku) i zrzutami (`--diag-screenshots`). Nie sprawdzono w exe: rysowania pomiarów myszą, okna skali/kalibracji, tworzenia pola kodu kreskowego, budowania indeksu z okna wyboru folderu, czytania z dźwiękiem na głośnikach.

## 27. Partia 1 nowych funkcji (2026-11-04)

### 27.1 Autozapis i odzyskiwanie
- Kopia może być starsza od stanu w chwili awarii o najwyżej jeden interwał (domyślnie 5 min, zakres 1–120). Nieudany autozapis (np. brak miejsca) trafia tylko do dziennika, bez okna; następny przebieg próbuje ponownie.
- Historia Cofnij/Ponów nie jest odzyskiwana (start od kopii). Nie jest też odzyskiwany przełącznik „Spłaszcz przy zapisie” ani wybór z okna D-23 (okno pojawi się ponownie).
- Dokument chroniony: hasła nie są zapisywane, więc odzyskana kopia dostaje ochronę pliku oryginalnego (po podaniu jego hasła). Jeśli w sesji przed awarią zmieniono hasło lub uprawnienia na innym zaszyfrowanym oryginale, po odzyskaniu obowiązują te z pliku na dysku – trzeba ustawić je ponownie. Gdy oryginał jest niedostępny lub bez szyfrowania, program prosi o nowe hasło otwarcia (AES-256, losowe hasło właściciela, uprawnienia z kopii).
- Kopie są związane z kontem Windows (DPAPI): po resecie hasła konta przez administratora lub na innym komputerze są nieczytelne (komunikat przy odzyskiwaniu; wiersz można odrzucić). Opisy nieczytelne po martwych procesach są usuwane przy starcie bez pytania, bo nie da się ich pokazać.
- Na czas zapisu kopii w pamięci jest druga kopia dokumentu (zaszyfrowany blob). DPAPI działa na całej tablicy, bez strumieniowania.

### 27.2 Jedna instancja
- Plik podany w drugim uruchomieniu, który nie istnieje, jest pomijany bez komunikatu (dane z potoku są niezaufane), a okno tylko wychodzi na wierzch. Bezpośrednie pierwsze uruchomienie nadal pokazuje „nie znaleziono”.
- Kopia uruchomiona jako administrator i kopia zwykła nie łączą się (`CurrentUserOnly` sprawdza właściciela potoku) – działają osobno.
- Wiadomość wysłana w chwili zamykania okna głównego, przed końcem procesu, może przepaść.
- Hasło z `-pwd` jest widoczne na liście procesów jak dotąd; potok tego nie zmienia.

### 27.3 Indeksy pełnotekstowe
- DPAPI wiąże indeks z kontem Windows: na innym koncie lub po resecie hasła konta przez administratora indeks jest nieczytelny i trzeba go zbudować od nowa.
- Przepisanie starego indeksu usuwa plik przez podmianę; jawne dane mogą fizycznie zostać na dysku (SSD, kopie w tle systemu), dopóki system ich nie nadpisze.
- Dopóki program nie zostanie uruchomiony po aktualizacji, stare pliki indeksu pozostają jawne. Punkt 26.8 („bez usuwania z UI”) jest nieaktualny: usuwanie jest w oknie Zarządzaj indeksami.
- Cały tekst indeksu jest w pamięci podczas zapisu i odczytu (DPAPI bez strumieniowania); limit jednego pliku indeksu to około 2 GB po kompresji.

### 27.4 Zapis załączników
- Ostrzeżenie opiera się na rozszerzeniu, nie na zawartości: plik wykonywalny z rozszerzeniem `.txt` nie wywoła ostrzeżenia. Nie obejmuje archiwów (`.zip`, `.iso`) z programami w środku ani dokumentów z makrami.

### 27.5 Okna niemodalne
- Dwa starsze okna niemodalne nadal woła się bezpośrednio przez `Window.Show`, nie przez `IDialogService.ShowModeless`: wyniki porównania (`SecurityCommands`) i panel czytania na głos (`ViewerModule`). Działają, ale nie dostają `WindowThemer.Prepare` z usługi; zapisane jako wyjątki w teście i do przeniesienia osobno.
- Kalkulator: kliknięcie „Dodaj zaznaczenie” dwa razy doda te same liczby dwukrotnie (cofnięcie: „Usuń ostatni”). Okno jest podrzędne wobec głównego, więc może zasłaniać fragment dokumentu i minimalizuje się razem z nim.

## 28. Rozbudowa – partia 2 (2026-11-04)

### 28.1 PDF do XLSX
- Jakość zależy od układu: tabele są wykrywane z położenia słów, bez struktury pliku i bez linii siatki. Kolumny bliżej siebie niż około 0,85 rozmiaru czcionki sklejają się w jedną komórkę. Scalone komórki nie są odtwarzane, a komórka wielowierszowa daje osobne wiersze arkusza. Tabela z jednym wierszem danych i bez nagłówka to dla programu dwa zwykłe wiersze tekstu albo tabela dwuwierszowa, zależnie od odstępów.
- Tekst w dwóch szpaltach jest odrzucany progiem średnio 5 słów w komórce; tabela o dwóch kolumnach z długimi opisami trafi do wierszy tekstu, a krótka szpalta może zostać uznana za tabelę.
- Tylko tekst poziomy. Strony obrócone (`/Rotate`) i tekst pionowy nie są obsługiwane poprawnie. Skany bez OCR dają w arkuszu jedynie informację o braku tekstu.
- Liczby: procenty i wartości z jednostką (`12%`, `12,50 zł`) zostają tekstem, podobnie nawiasowy zapis ujemny `(123)`. Liczba bez wskazówki w dokumencie, np. `1,234`, zostaje tekstem. Zapis `601 234 567` zostanie odczytany jako liczba, bo nie różni się od kwoty; telefon z plusem zostaje tekstem. Formatowanie (kolory, czcionki, obramowania) nie jest przenoszone, a obrazy są pomijane.
- Sprawdzono: testy na plikach z PDFsharp oraz otwarcie wyniku w Excelu 2024 przez automatyzację (bez naprawy pliku, `HasFormula=False` dla tekstu z `=HYPERLINK(...)`) i w openpyxl.

### 28.2 PDF do HTML
- Układ strony nie jest odtwarzany: akapity jeden pod drugim, sortowane pionowo (tekst w szpaltach może się przeplatać, jak w DOCX). Tabele nie są rozpoznawane, a łącza i rysunki wektorowe nie są eksportowane.
- Obrazy tylko te, które zwraca PdfPig z zasobów strony i które da się zapisać jako PNG lub JPEG. JPEG w CMYK trafia do pliku bez konwersji i przeglądarka może pokazać go w złych kolorach. Obrazy wewnątrz formularzy XObject mogą zostać pominięte. Plik może być duży, bo obrazy są w nim zapisane w base64.
- Wyjście sprawdzone testami (`<script>`, `<img onerror>` w treści, `</title>` w metadanych), nie oglądano go w przeglądarce.
