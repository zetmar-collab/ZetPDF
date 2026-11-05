# ZetPDF 0.1.0-test – raport kontroli końcowej (recenzent)

> Rozdziały 1–8: kontrola etapów 1 i 2. **Rozdział 9: kontrola etapu 3** (2026-10-21).

- **Daty:** 2026-10-17 – 2026-10-21 (sesja przerywana limitem API; praca kontynuowana na tym samym stanie repozytorium).
- **Punkt wyjścia:** `main`, commit 4b4b8e3. Commity recenzenta: efa7c60, 21c63e5, 7a47b0a, 5477b92, 137c769 i commit z tym raportem.
- **Środowisko:** Windows 11 Home 10.0.26200 x64, .NET SDK 8.0.423. Przez większość kontroli **sesja Windows była zablokowana** (proces `LogonUI`, schowek niedostępny, zrzuty pulpitu czarne) – dlatego interfejs sprawdzałem w prawdziwym `ZetPDF.exe` przez tryby diagnostyczne renderujące okna w procesie, a nie klikaniem myszą. Niżej zawsze piszę, co zostało uruchomione w exe, a co tylko w testach.

## 1. Podsumowanie

| Obszar | Wynik |
|---|---|
| Build Release (`--no-incremental`) | 0 błędów, 0 ostrzeżeń |
| Testy (`dotnet test ZetPDF.sln -c Release`) | **595 / 595 zaliczonych, 0 pominiętych**, 3 przebiegi z rzędu z identycznym wynikiem |
| Publikacja | `dist\ZetPDF.exe` – jeden plik, 89 566 981 B (ok. 85,4 MiB), self-contained, PDFium i Tesseract w środku; `dist\README.txt` (pl/en), `dist\LICENSES\` (9 plików) |
| Test dymny exe | 5 uruchomień `--diag-smoke` (formularz, AES-256 z hasłem, uszkodzony, 500 stron, tylko hasło uprawnień) + `--diag-screenshots` (65 PNG, oba motywy i języki) – wszystkie procesy zakończyły się same z kodem 0 |
| Zadania koordynatora D-22, D-23, O-3, test schowka, OGRANICZENIA, LICENCJE, publikacja | wykonane (rozdz. 4) |
| Błędy znalezione i naprawione | 11 (rozdz. 3) |
| Otwarte ograniczenia | rozdz. 7 i `docs\OGRANICZENIA.md` |

## 2. Co sprawdzono i jak

### 2.1 Build i ostrzeżenia
`dotnet build ZetPDF.sln -c Release --no-incremental` – 0 ostrzeżeń (także po wszystkich zmianach). Krytycznych ostrzeżeń (nullability, nieobsłużone Task, przestarzałe API) brak – kompilator ich nie zgłasza, a `Nullable` jest włączone we wszystkich projektach.

### 2.2 Testy
Trzy pełne przebiegi po czystym buildzie – wynik w `docs\RAPORT_TESTOW.md` (rozdz. 0). Wcześniej, przy zablokowanej sesji, 2 testy schowka były jawnie pominięte (patrz 3, R-8) – to jedyna różnica między przebiegami w ciągu całej kontroli.

### 2.3 Przegląd kodu
- **Uchwyty PDFium** (`src\ZetPDF.Rendering`): jedyne miejsce użycia PDFiumCore (grep). `FPDF_LoadMemDocument` → `CloseDocument` w `catch`/`Dispose` (zamknięcie kolejkowane na wątek PDFium, przypięcie `GCHandle` zwalniane razem z dokumentem); `FPDF_LoadPage`, `FPDFText_LoadPage`, `FPDFBitmap_CreateEx`, `FPDFPage_GetAnnot`, `FORM_OnAfterLoadPage` – każde ma parę Close/Destroy w `finally`; środowisko formularza zamykane w `CloseOnThread`. Liczniki uchwytów są sprawdzane testami 60 cykli otwórz/zamknij. **Bez uwag.**
- **Wątek UI:** brak `.Result`/`.Wait()` na wątku UI (grep; `.Result` występuje tylko na zakończonych zadaniach lub właściwościach okien). `GetAwaiter().GetResult()` jest w `ConversionService.PdfToImagesAsync` i `PrintService` – oba na osobnym wątku (`Task.Factory.StartNew`, wątek STA druku). Operacje na dokumencie idą przez `session.ExecuteAsync` (`Task.Run`). Obsługi `async void` (zdarzenia WPF) kończą się w globalnym `DispatcherUnhandledException` (dziennik + okno), nie zamykają programu. W exe: plik 500 stron otwarty, obrócony, zduplikowany, zapisany i otwarty ponownie bez zawieszenia (cały scenariusz z przerwami testu ~25 s); pomiar przerw wątku UI < 1 s robią testy `PrintAndPerformanceTests`.
- **Zapis plików:** dokumenty – `FileService.WriteAllBytesSafeAsync` (plik tymczasowy w tym samym folderze + `File.Replace`/`File.Move`, kopia `.bak` usuwana po sukcesie). Eksporty (TXT, XFDF, załącznik) używają tej samej usługi; bezpośrednie `File.WriteAllBytesAsync` jest tylko gałęzią zapasową, gdy usługi nie ma w DI (w aplikacji jest zawsze). Kreator działań, porównanie, konwersje do obrazów/DOCX: plik tymczasowy + `File.Move(overwrite)` (atomowo, bez `.bak`). **Bez uwag.**
- **Pliki uszkodzone i zaszyfrowane:** w exe uszkodzony plik z polskiej ścieżki daje zlokalizowane okno „Plik … jest uszkodzony lub nie jest dokumentem PDF” (zrzut `docs\zrzuty\exe-uszkodzony-plik-blad.png`), bez wyjątku nieobsłużonego; AES-256 bez hasła – okno hasła; z `-pwd` – otwarcie i pełny scenariusz. Testy: `CorruptedInputTests` (7 rodzajów uszkodzeń × usługi), `EncryptedInputTests`.
- **Ścieżki z polskimi znakami i spacjami:** wszystkie uruchomienia exe na `scratch\recenzja\Próbny folder ąę\…ąę.pdf`, zapis do `… – zapisany przez exe.pdf`, `ZETPDF_HOME` = `…\home ąę`; testy `UndoRedoAndPolishPathTests` (32 operacje).
- **Sieć i sekrety:** grep `HttpClient|WebRequest|WebClient|Socket|TcpClient|Uri("http|Dns.|Process.Start` w `src`: jedyny klient sieciowy to `CmsSignatureBuilder` (TSA pod adresem z Ustawień, tylko na żądanie; test „brak adresu = brak połączenia”). `Process.Start` – Eksplorator (folder dziennika/pliku) i otwarcie łącza http/https/mailto w przeglądarce systemowej **po potwierdzeniu** (łącza w PDF i przyciski formularza). Brak haseł/kluczy/tokenów w kodzie produkcyjnym (hasła są tylko w generatorze próbek testowych). Parsery XML (XFDF) mają `DtdProcessing.Prohibit`, `XmlResolver = null`.

### 2.4 Lokalizacja
Skrypt `tools\check_localization.py` (nowy): pary `Strings.resx`/`Strings.pl.resx` we wszystkich 9 projektach – **1533 klucze, każdy w pl i en**, brak pustych, zgodne `{n}`, brak duplikatów między modułami; brak tekstów na sztywno w XAML (atrybuty i treść) i w C# (przypisania Content/Text/Header/ToolTip/Title, MessageBox/ShowMessage/Confirm/SetStatus z literałem); 530 kluczy użytych w kodzie istnieje w zasobach. Wynik: **0 błędów**. Świadome wyjątki opisane w skrypcie: glify ikon (`&#xE…;`), nazwy PDF (`/Watermark`), przyrostek pliku `_zetpdf` (dane), symbol „Aa” przełącznika wielkości liter. Tester ma równoległy test (`LocalizationThemeShortcutTests`) – zielony.
**Ucięcia tekstu:** przejrzałem zrzuty z exe (okno główne, wszystkie karty wstążki, Ustawienia, O programie, Skróty, komunikaty, okno D-23, okno błędu – pl/en, jasny/ciemny). Znaleziona i naprawiona usterka R-3 (polska wstążka). Po poprawce przy szerokości 1264 px wszystkie grupy są widoczne; część pierwszych grup jest zmniejszona do małych przycisków (ikona + etykieta albo sama ikona z podpowiedzią). Innych ucięć nie stwierdziłem. **Uwaga:** zrzuty okien dialogowych z `RenderTargetBitmap` mają biały pasek na dole/prawej – to artefakt renderu w procesie (rozmiar okna z ramką), a nie widok na ekranie; na prawdziwym pulpicie tego nie weryfikowałem.

### 2.5 Motywy i kontrast
Skrypt `tools\check_themes.py` (nowy):
- Kolory poza `Themes\Light.xaml`/`Dark.xaml`: 0 w XAML; 16 w C#, wszystkie uzasadnione: `Brushes.Transparent` (tło bez koloru), kolory treści PDF wybierane przez użytkownika (atrament podpisu, próbka koloru znaku wodnego, kolor adnotacji), czarny tekst w wyglądzie podpisu cyfrowego (treść PDF), biały papier strony w widoku i podglądzie druku (kolor strony PDF).
- Kontrast WCAG (40 par tekst/tło na motyw, w tym tła okien dialogowych: Surface, Surface.Raised, stopka Surface.Alt, pola Input, podpowiedź, pasek stanu, zaznaczenie, akcent, statusy): **wszystkie ≥ 4,5:1**; najniższy: jasny 4,79:1 (Status.Success na tle okna), ciemny 5,78:1 (Status.Error na Surface.Alt). Tekst nieaktywny (Text.Disabled) 2,86–3,58:1 – dopuszczalne przez WCAG dla elementów wyłączonych.
- Nie liczyłem kontrastu kolorów samej wstążki Fluent.Ribbon (motywy biblioteki `Light`/`Dark`); na zrzutach jest czytelna w obu motywach.

### 2.6 Bezpieczeństwo (uruchomione)
Program `tools\review\ReviewCheck.Program.cs.txt` (prawdziwe usługi z DI, jak w aplikacji) + `tools\review\otherreaders.py` + `tools\review\winpdf.ps1`:
- **Redakcja** (PESEL, e-mail, fraza „jaźń”): po zastosowaniu frazy nie ma w tekście PdfPig, PDFium, **MuPDF**, **pypdf** ani w zdekodowanych strumieniach i ciągach całego pliku; reszta tekstu zostaje; obszary czarne (render Windows.Data.Pdf obejrzany).
- **Oczyszczanie:** /Info (tytuł, temat, słowa kluczowe, autor), XMP (`dc:creator`), załącznik (nazwa i treść), JavaScript – brak w całym pliku (PDFsharp, MuPDF metadata/XMP, pypdf). Zostaje tylko informacja PDFsharp jako Producer/Creator i świeży XMP generowany przez PDFsharp (opisane w OGRANICZENIA).
- **AES-256** (Ochrona hasłem przez `SetEncryptionCommand` + zapis `DocumentManager`, polskie hasła): `/V 5 /R 6 /AESV3`; PDFium i PdfPig – odmowa bez hasła i ze złym hasłem, otwarcie hasłem użytkownika i właściciela, uprawnienia zapisane; **MuPDF i pypdf** (niezależne silniki zainstalowane w systemie, tylko do odczytu) – to samo. Plik zapisany przez `ZetPDF.exe` w teście dymnym (AES-256 otwarty hasłem, zmieniony, zapisany) – pypdf V5/R6, bez hasła odmowa, z hasłem 3 strony z obrotem.
- **Windows.Data.Pdf** (silnik PDF Windows) **nie otwiera żadnego pliku AES-256** – sprawdziłem także plik AES-256 utworzony przez MuPDF: ten sam błąd 0x80048040. AES-128 z ZetPDF otwiera poprawnie. To ograniczenie Windows (opisane w OGRANICZENIA). Przeglądarki Edge nie uruchamiałem z plikiem (wymagałaby interakcji z oknem hasła na zablokowanym pulpicie).

### 2.7 Licencje
`dotnet list package --include-transitive` dla wszystkich projektów `src` i `tests`, licencje z `.nuspec` – `docs\LICENCJE.md` przepisany: 24 pakiety programu (MIT/Apache-2.0, PDFium BSD-3 + komponenty), osobna sekcja pakietów testowych (xUnit Apache-2.0, Test SDK MIT, Newtonsoft MIT). **Brak AGPL/GPL.** PDFsharp 6.2.4 (MIT) – bez zmian.

### 2.8 Test dymny exe (sekcja 5 pkt 8)
Nowy tryb `ZetPDF.exe "<plik>" [-pwd<hasło>] --diag-smoke <folder>`: otwiera plik tymi samymi akcjami co powłoka, wykonuje polecenia przez rejestr poleceń (tak jak kliknięcie przycisku wstążki), okna dialogowe, które się pojawią, zapisuje do PNG i zamyka (= Anuluj), przełącza język i motyw, zapisuje dokument jako nowy plik i otwiera go ponownie. Wyniki: `docs\zrzuty\exe-smoke-*.txt`.

| Karta | Polecenie wykonane w exe | Wynik (formularz ąę.pdf) |
|---|---|---|
| Początek | Powiększ, Zaznacz tekst | wykonane |
| Konwertuj | Szybkie rozpoznawanie (OCR) | wykonane; komunikat (strony mają tekst) |
| Edytuj | Utwórz łącza z adresów URL | wykonane; komunikat |
| Organizuj | Obróć w prawo, Duplikuj | **dokument zmieniony** |
| Komentarz | Ukryj / Pokaż wszystkie komentarze | wykonane |
| Widok | Dwie strony, Obróć widok w prawo | wykonane |
| Formularz | Wyróżnij pola, Resetuj formularz | wykonane; komunikat |
| Ochrona | Właściwości zabezpieczeń | okno otwarte (zrzut) i zamknięte |
| Pomoc | O programie | okno otwarte (wersja 0.1.0-test) |
| Plik | Właściwości dokumentu | okno otwarte |
| – | Cofnij, Ponów | dokument zmieniony |
| – | język en + motyw ciemny, potem pl + jasny | etykiety i kolory przełączone na żywo (zrzuty) |
| – | Zapisz jako → ponowne otwarcie | OK, 2 strony (było 1 + duplikat), „zmodyfikowany” = nie |

Pozostałe uruchomienia: **AES-256 z `-pwd`** – jak wyżej, OCR wyszarzony (brak uprawnienia do modyfikacji treści – poprawnie), zapis zaszyfrowany (sprawdzony pypdf/MuPDF); **500 stron** – OK (501 po duplikacji); **uszkodzony** – zlokalizowany komunikat, brak awarii; **tylko hasło uprawnień** – polecenia zmieniające treść wyszarzone zgodnie z uprawnieniami, przy zapisie pojawiło się okno wyboru D-23 (`docs\zrzuty\exe-d23-wybor-ochrony.png`), zamknięte = zapis anulowany (poprawnie). Dziennik procesu: jedyny błąd to świadomie wywołany komunikat o uszkodzonym pliku.
Tryb `--diag-screenshots` (65 PNG): okno główne z otwartym plikiem, każda karta wstążki, Ustawienia, O programie, Skróty, pytanie o zapis, okno D-23, okno błędu – pl/en × jasny/ciemny. Wybrane w `docs\zrzuty\`.

**Czego NIE uruchomiono w exe:** klikania myszą i klawiatury na pulpicie (sesja zablokowana; nie używałem automatyzacji pulpitu, żeby nie ingerować w sesję użytkownika), przeciągnij i upuść, okien z parametrami wypełnianych przez użytkownika (znak wodny, druk, podpis, OCR obszaru, Kreator – testowane tylko silniki w testach), pełnego ekranu, druku na drukarce, podpisu z prawdziwym serwerem TSA, narzędzi rysowania komentarzy myszą. Te funkcje są pokryte testami usług i poleceń (`docs\RAPORT_TESTOW.md`), nie klikaniem.

## 3. Znalezione błędy i naprawy

| # | Błąd | Jak znaleziony | Naprawa |
|---|---|---|---|
| R-1 | Po dowolnej krótkiej operacji z oknem postępu zamknięcie programu kończyło się `ObjectDisposedException` (niepokazane okno postępu, podrzędne wobec głównego, anulowało zwolniony `CancellationTokenSource`), oknem „nieoczekiwany błąd” i **zawieszonym procesem** | test dymny exe (proces nie kończył się; stos w dzienniku) | `DialogService.RunWithProgressAsync` zawsze zamyka okno (`finally`), `ProgressWindow.RequestCancel` odporne na zwolnione źródło |
| R-2 | `--diag-screenshots` na końcu czekał na pytanie „Zapisać zmiany?” (próbne dokumenty) – proces nie kończył się | uruchomienie exe | próbne dokumenty zamykane przed zamknięciem okna; dodane: plik z linii poleceń, karty wstążki, okno D-23 i błędu |
| R-3 | Polska wstążka (dłuższe etykiety) przy ~1264 px nie mieściła się – Fluent.Ribbon przewijał ją strzałką i ukrywał ostatnie grupy (Rozpoznawanie, Podpis) | zrzut z exe | `RibbonTabItem.ReduceOrder` – grupy najpierw zmniejszają przyciski, potem się zwijają |
| R-4 | O-3: angielskie komunikaty wyjątków w polskim interfejsie (np. „Not an FDF file.”, „Empty XFDF.”, „Page range '…' is outside…”), w tym dopisek „(Parameter 'x')” przy tekstach modułów rzucanych jako `ArgumentException`, oraz angielskie komunikaty .NET w oknie błędu (`IOException`, COM schowka) | przegląd ~100 `throw new …("…")` | `LocalizedError` (klucze w wyjątkach, ok. 60 miejsc, 54 nowe klucze pl/en), okno błędu i błędy w wierszu okien przez `Describe`; komunikat techniczny tylko w szczegółach |
| R-5 | D-23: plik z samym hasłem uprawnień zapisywany po cichu z losowym hasłem właściciela | zgłoszenie testera O-2 | okno wyboru przed zapisem (rozdz. 4) |
| R-6 | D-22: brak informacji o zmianie RC4 → AES-256 | zgłoszenie testera O-1 | komunikat na pasku stanu + OGRANICZENIA |
| R-7 | Import danych formularza / komentarzy z pliku XML, który nie jest XFDF, kończył się „sukcesem” bez zmian | test O-3 | błąd „Plik XFDF jest pusty albo nie jest prawidłowym plikiem XFDF” |
| R-8 | „Niestabilne” testy schowka – faktyczna przyczyna: zablokowana sesja Windows (schowek niedostępny dla wszystkich procesów), dodatkowo równoległy przegląd poleceń wstążki | uruchomienia testów, `OpenClipboard`, `LogonUI` | `ClipboardTests` w kolekcji sekwencyjnej, `[ClipboardFact]` jawnie pomija przy niedostępnym schowku, ponawianie przy chwilowej blokadzie; asercje bez zmian |
| R-9 | Okno komunikatu z więcej niż 3 przyciskami ucinałoby długie polskie etykiety (`MaxWidth` 620) | przegląd przed dodaniem okna D-23 | przy > 3 przyciskach układ pionowy |
| R-10 | `LICENCJE.md` bez `System.Reflection.Emit`, z nieużywanymi ZXing.Net i DiffPlex jako „w użyciu/kandydat” | porównanie z `dotnet list package` | lista przepisana |
| R-11 | Błąd schowka (zajęty) pokazywał angielski komunikat COM | przy R-8 | osobny zlokalizowany komunikat „Schowek jest teraz niedostępny” |

Po każdej naprawie: build Release (0/0) i testy (końcowo 595/595 ×3).

## 4. Zadania koordynatora

1. **D-23** – wykonane: `OwnerProtectionGuard` + okna w `DialogService`, teksty pl/en, 4 testy w `OwnerOnlyProtectionTests` (hasło właściciela po złej próbie – ochrona i uprawnienia zachowane w PDFium/PdfPig; nowe hasło – AES-256; bez ochrony – zwykły PDF; Anuluj – plik nietknięty; brak pytania dla plików z hasłem otwarcia, otwartych hasłem właściciela i nieszyfrowanych). Okno widoczne w exe.
2. **D-22** – wykonane: OGRANICZENIA rozdz. 9, pasek stanu po zapisie, test (pl/en).
3. **O-3** – wykonane (R-4), testy `LocalizedErrorTests` (w tym skan: każdy klucz `WithMessageKey` istnieje w pl i en).
4. **Test schowka** – wykonane (R-8).
5. **`docs\OGRANICZENIA.md`** – złożony z `docs\ograniczenia\*.md`, po polsku, według funkcji etapów 1 i 2, z D-08/D-22/D-23 i wynikami tej kontroli.
6. **`docs\LICENCJE.md`** – wykonane (2.7).
7. **Publikacja** – `tools\publish\publish.ps1` (dotnet publish z `-p:DebugType=None` + README + LICENSES). `dist\`: `ZetPDF.exe`, `README.txt`, `LICENSES\` (MIT, Apache-2.0, BSD-2, BSD-3, PDFium-ThirdParty, zlib, Fluent.Ribbon, tessdata, THIRD-PARTY-NOTICES). **Brakuje** pełnych tekstów licencji FreeType, IJG, libpng i Unicode/ICU – nie ma ich w pakietach NuGet ani na dysku, a plików z sieci nie pobierałem (brak zgody); napisane wprost w README i LICENCJE.
8. **Test dymny exe** – rozdz. 2.8.

## 5. Zmiany w kodzie produkcyjnym (poza naprawami z tabeli)
- Kontrakt: nowa właściwość `init` `PdfEncryptionSettings.GeneratedOwnerPassword` (zgodna wstecz).
- Nowe tryby diagnostyczne exe: `--diag-smoke` (nowy) i rozbudowany `--diag-screenshots`. Nie łączą się z siecią, przywracają ustawienia języka i motywu.

## 6. Narzędzia kontroli w repozytorium
`tools\check_localization.py`, `tools\check_themes.py`, `tools\review\` (program weryfikujący, skrypty MuPDF/pypdf i Windows.Data.Pdf, README), `tools\publish\`.

## 7. Co nie działa lub działa z ograniczeniami (najważniejsze; pełna lista w `docs\OGRANICZENIA.md`)
- Edycja tekstu w czcionkach Type3/CID innych niż Identity/standardowych bez szerokości – stary tekst tylko zakrywany (zostaje w pliku).
- Redakcja zamienia stronę na obraz 300 DPI (treść trwale usunięta, ale strona traci wektory i rośnie plik).
- PDF → DOCX w wersji podstawowej (bez tabel, kolumn, stylów).
- Weryfikacja podpisów bez sprawdzania unieważnień (brak sieci); znacznik czasu nie testowany z prawdziwym TSA.
- „Archiwizuj” nie tworzy PDF/A.
- Po oczyszczeniu zostaje informacja o programie (PDFsharp) w Producer/Creator i świeży XMP PDFsharp.
- Windows.Data.Pdf nie otwiera AES-256 (ograniczenie Windows).
- Brak pełnych tekstów 4 licencji komponentów PDFium w `dist\LICENSES\`.
- Interfejs nie był klikany na pulpicie; okna z parametrami sprawdzone tylko przez testy silników i konstrukcji okien.

## 8. Czego nie dało się sprawdzić
- Zachowania na prawdziwym pulpicie (zablokowana sesja przez większość kontroli): klikanie, przeciąganie plików, pełny ekran, rzeczywisty wygląd okien z ramką i paskiem tytułu (DWM), skalowanie DPI innych niż bieżące.
- Druku na fizycznej drukarce, serwera TSA, kart kryptograficznych.
- Otwarcia plików ZetPDF w Edge/Acrobat (sprawdzono PDFium, PdfPig, MuPDF, pypdf, Windows.Data.Pdf).
- Windows 10 (tylko Windows 11).

---

## 9. Etap 3 – kontrola końcowa (2026-10-21)

- **Punkt wyjścia:** `main` f738a97 (etap 3 scalony: 686 testów, build 0/0). Zakres: `git diff c23d27f..f738a97` (78 plików, ok. 7700 wierszy) – pomiary, kalkulator księgowy, pole kodu kreskowego, JavaScript formularzy (Jint), czytanie na głos, widok równoległy, ułatwienia dostępu, indeks i wyszukiwanie zaawansowane.
- **Środowisko:** jak wyżej (Windows 11, .NET SDK 8.0.423). Interfejs sprawdzany w prawdziwym exe trybami diagnostycznymi, bez klikania na pulpicie.
- **Commity recenzenta:** 51018a4, de59869, adabc6e i commit z tym rozdziałem.

### 9.1 Podsumowanie

| Obszar | Wynik |
|---|---|
| Build Release (`--no-incremental`) | 0 błędów, 0 ostrzeżeń |
| Testy | **752 / 752**, 0 pominiętych, 3 przebiegi z rzędu identyczne (było 686; +66 przypadków recenzenta) |
| Publikacja | `dist\ZetPDF.exe` 91 301 274 B (ok. 87,1 MiB), `tools\publish\publish.ps1` (`-p:DebugType=None`); `dist\LICENSES\` – 12 plików (nowe: `Jint-LICENSE.txt`, `Acornima-LICENSE.txt`); `dist\README.txt` z opisem funkcji etapu 3 (pl/en) |
| Test dymny exe (etap 3) | 4 pliki z folderu `scratch\etap3\Próbny folder – etap 3 ąę\`, `ZETPDF_HOME=…\home ąę`: formularz – **WYNIK: OK**; 500 stron – **OK**; AES-256 z `-pwd` – 1 „problem” = OCR wyszarzony bez uprawnienia (poprawnie, tak samo jak w etapie 1–2); uszkodzony – zlokalizowany komunikat, brak awarii. Każdy proces zakończył się sam (kod 0), brak wiszących procesów `ZetPDF`. Raporty: `docs\zrzuty\etap3-exe-smoke-*.txt` |
| `--diag-screenshots` | 81 PNG (było 65): nowe – karta Ułatwienia dostępu z panelem wyników, panel wyszukiwania zaawansowanego, panel czytania – pl/en × jasny/ciemny; wybrane w `docs\zrzuty\etap3-*.png` |
| Lokalizacja (`tools\check_localization.py`) | 1812 kluczy (w tym nowy `Annot.Script.TooLong`), każdy w pl i en, 0 błędów, 0 uwag |
| Motywy (`tools\check_themes.py`) | brak nowych kolorów poza słownikami (te same 16 uzasadnionych wystąpień co po etapie 2; kolory w kodzie etapu 3 to wyłącznie kolory treści PDF – `PdfColor`); kontrast ≥ 4,5:1 (jasny 4,79, ciemny 5,78) |
| Błędy znalezione i naprawione | 9 (tabela 9.3) |

### 9.2 Co sprawdzono i jak

- **Wątek UI, `.Result`/`.Wait()`:** grep w plikach etapu 3 – `.Result` tylko na zakończonych zadaniach (`IsCompletedSuccessfully`) i właściwościach okien; budowanie indeksu w `Task.Run` z postępem i anulowaniem; skrypty formularza poza wątkiem UI; `async void` tylko w obsłudze zdarzeń, z `try/catch` → `ShowError`.
- **Uchwyty PDFium:** kod etapu 3 nie woła PDFium bezpośrednio (tylko warstwa renderująca). Widok równoległy tworzy drugi `DocumentView` i zwalnia go (`Dispose`) przy zamknięciu pary i przy zamknięciu którejkolwiek karty (`DocumentClosed`).
- **Zapis plików:** raporty HTML/TXT, CSV wyników wyszukiwania i pomiarów – `IFileService.WriteAllBytesSafeAsync` (plik tymczasowy + podmiana); bezpośredni zapis tylko jako zapas bez usługi w DI. Plik indeksu – plik tymczasowy + `File.Move(overwrite)`.
- **Ścieżki z polskimi znakami:** wszystkie uruchomienia exe na `…\Próbny folder – etap 3 ąę\…ąę.pdf` z `ZETPDF_HOME=…\home ąę`; „Zapisz jako … – zapisany przez exe.pdf” i ponowne otwarcie OK; testy indeksu na `Próbny folder – zażółć`.
- **Sieć i sekrety:** grep `HttpClient|WebRequest|WebClient|Socket|Uri("http|Dns.|Process.Start` w plikach etapu 3 – brak nowych połączeń; jedyne `Process.Start` (formularze) to istniejące otwieranie łącza po potwierdzeniu. Jint bez modułów, `fetch`, `XMLHttpRequest`, `require`, `importNamespace` (test). System.Speech – tylko głosy lokalne.
- **Raporty HTML:** raport ułatwień dostępu i raport porównania (etap 2) escapują `& < > "` w każdym tekście pochodzącym z dokumentu (tytuł, język, nazwy plików, słowa). Nowe testy: `<script>` w tytule, `/Lang` z `"><img onerror=…>`, nazwy plików z `<img …>` i `<script>` w porównywanych stronach – w wyniku brak `<script`/`<img`. Innych raportów HTML w programie nie ma (grep). **Bez błędów.**
- **Eksporty CSV (cały program):** grep `csv` w `src` – trzy eksporty: pomiary i dane formularza (Annotations, `CsvText.Cell`: apostrof przed `= + - @ tab CR`, cytowanie przy `, ; tab " CR LF`) oraz wyniki wyszukiwania zaawansowanego (Editing, `FullTextIndex.CsvField`: ta sama neutralizacja, każde pole w cudzysłowie). Security, Pages, Kreator działań nie eksportują CSV. Testy `CsvInjectionTests` i `Csv_export_quotes_fields_and_neutralizes_formulas` zielone. **Bez błędów.**
- **JavaScript w formularzach:** przegląd `ScriptRunner`, `AcroJsPrelude`, `FormCalculation`, `FormScripts` i własne ataki uruchomione w teście z pomiarem czasu i pamięci prywatnej procesu: konstruktor `Function` przez prototyp (także funkcji generatora), `eval`, zanieczyszczenie prototypów (nie przechodzi do kolejnego skryptu – każde zdarzenie ma nowy silnik), getter `event.value` z pętlą (limit instrukcji), rekurencja w getterze (limit rekursji), ogromny literał, podmiana funkcji używanych przez ograniczenia, `valueOf`/getter zwracające inną wartość przy drugim odczycie, wzmacniacze alokacji. Domyślna polityka „tylko AF*” dopuszcza wyłącznie wywołania znanych funkcji z literałami (wyrażenie regularne z limitem czasu). JavaScript dokumentu (`/OpenAction`, `/Names /JavaScript`, `/AA` katalogu) i przycisków nie jest nigdy wykonywany (`FormCalculation` czyta tylko `/AA` pól; test `Document_scripts_are_listed_but_not_executed`). `OutOfMemoryException` łapany i wyłącza skrypty dokumentu (istniejący test `Huge_allocations_are_script_errors_and_process_survives`). Wyniki – tabela 9.3 (R-13…R-15).
- **Indeks:** polska ścieżka i plik zaszyfrowany pomijany (`tajne.pdf` w liście `Encrypted`) – istniejące testy; uszkodzony plik indeksu – nowy test z poprawnym GZip i błędnymi wartościami (R-16); indeks w `IAppPaths.LocalDataDirectory` (respektuje `ZETPDF_HOME`).
- **Czytanie na głos:** test integracyjny „każde polecenie” wołał prawdziwy syntezator (R-12). W exe (tryb diagnostyczny, bez dźwięku): Aktywuj → Przeczytaj bieżącą stronę → stany `Stopped → Speaking → Stopped`, głos „Microsoft Paulina Desktop”, Zatrzymaj → `Stopped`; na pliku 500 stron „Czytaj od bieżącej strony” pozostawione w stanie `Speaking` → po zamknięciu dokumentów `Stopped`, okno zamknięte, proces zakończył się sam. `Get-Process ZetPDF` po wszystkich uruchomieniach – 0 procesów.
- **Licencje:** `dotnet list package --include-transitive` (cała solucja) porównane z `docs\LICENCJE.md` – nowe w programie: ZXing.Net 0.16.11 (Apache-2.0), Jint 4.16.4 (BSD-2-Clause), Acornima 1.7.0 (BSD-3-Clause), System.Speech 8.0.0 (MIT); wszystkie permisywne, **brak AGPL/GPL**. System.Collections.Immutable i System.Reflection.Metadata – tylko projekty testowe. Okno O programie i `dist\LICENSES` uzupełnione (R-18).
- **Ucięcia tekstu:** obejrzane zrzuty nowej karty i paneli (pl/en, jasny/ciemny) oraz okna z testu dymnego (kalkulator, Skrypty formularza, wybór widoku równoległego, widok równoległy). Znaleziona usterka R-17; innych ucięć nie stwierdziłem (przyciski panelu ułatwień dostępu zawijają się do drugiego wiersza w języku polskim – czytelne).

### 9.3 Znalezione błędy i naprawy (etap 3)

| # | Błąd | Jak znaleziony | Naprawa |
|---|---|---|---|
| R-12 | Testy z pełnym kontenerem usług (m.in. „każde polecenie wstążki”) używały prawdziwego syntezatora – na komputerze z głośnikami słychać było czytanie | zgłoszenie koordynatora, przegląd hostów testowych | `SilentSpeechEngineFactory` (System.Speech z `SetOutputToNull` – prawdziwa synteza i zdarzenia słów, bez dźwięku) rejestrowana w 5 kontenerach testowych i w trybach `--diag-*` exe; testy pilnują rejestracji |
| R-13 | Piaskownica JS – obejścia limitu pamięci pojedynczych alokacji: `Math.floor = …` przed `'x'.repeat(3e8)` (600 MB, wynik zwrócony bez błędu), `s.replace(/x/g, tekst)` (1e9 znaków, 4,3 GB szczytowo, 3 s, bez błędu), `new Uint8Array(1e9)` i `ArrayBuffer` (1 GB, bez błędu), `concat`, `toLocaleString`, `JSON.stringify` (1–3 GB przed „Invalid string length”), `valueOf`/getter `length` zwracające inną wartość przy drugim odczycie, podmiana `Function.prototype.call/apply` i `Array` | własne ataki w teście z pomiarem pamięci procesu | funkcje wbudowane przechwycone i związane `bind` przy starcie, argumenty konwertowane raz, `replace`/`replaceAll` zawsze z własną funkcją (rozwinięcie wzorców `$` po stronie preludium + licznik długości), ograniczone `concat`, `toLocaleString`, `fill` (pętla w JS), usunięte `ArrayBuffer`, `SharedArrayBuffer`, `DataView`, `Atomics`, tablice typowane, `WeakRef`, `FinalizationRegistry`; wartość `event.value` > 1 MB odrzucana także w C# |
| R-14 | Ogromny literał w skrypcie pola był parsowany bez limitu (czas i pamięć parsera są poza limitami wykonania Jint) | przegląd | kod > 256 KB nie jest uruchamiany (błąd skryptu, nowy klucz `Annot.Script.TooLong` pl/en) |
| R-15 | W mojej pierwszej poprawce R-13 (zgłoszone przez automatyczny przegląd bezpieczeństwa koordynatora): `JSON.stringify` z ujemnym `space` zaniżał licznik, a `toJSON` dopisane do `Object.prototype` po sprawdzeniu kopii pozwalało natywnemu `stringify` wytworzyć dowolnie duży łańcuch | przegląd koordynatora | własny serializator JSON liczący każdy kawałek wyniku (natywny `JSON.stringify` tylko do cytowania prymitywnych łańcuchów), wcięcie 0..10, twardy limit długości wyniku; testy: ujemny `space` na dużym obiekcie, `Object.prototype.toJSON`/`Array.prototype.toJSON` – błąd limitu; zgodność z czystym Jint w 18 wyrażeniach |
| R-16 | Uszkodzony plik indeksu (poprawny GZip, błędne wartości) wywracał wczytanie indeksu wyjątkiem `ArgumentOutOfRangeException` (zła data, ujemny licznik); licznik z pliku sterował wielkością alokacji | test recenzenta (3× czerwony przed poprawką) | walidacja liczników i stanu, pojemność list niezależna od pliku, łapane `ArgumentException`, `OverflowException`, `DecoderFallbackException` → indeks ignorowany i budowany od nowa |
| R-17 | Panel czytania na głos ucinał polską etykietę „Automatycznie (język dokumentu lub interfejsu)” | zrzut z exe | szerokość panelu 340 → 380 px (zrzut po poprawce: pełny tekst) |
| R-18 | Brak ZXing.Net, Jint, Acornima, System.Speech w oknie O programie i w `THIRD-PARTY-NOTICES.txt`; `LICENCJE.md` opisywał ZXing.Net jako nieużywany | porównanie z `dotnet list package` | wpisy w `ThirdPartyLibraries` (z tekstami licencji), `Jint-LICENSE.txt` (BSD-2, Sebastien Ros), `Acornima-LICENSE.txt` (BSD-3, Adam Simon + nota o kodzie acorn – MIT i Esprima .NET – BSD-3), THIRD-PARTY-NOTICES, LICENCJE.md; test |
| R-19 | `--diag-smoke` i `--diag-screenshots` nie obejmowały etapu 3 | zadanie koordynatora | smoke: Access.FullCheck, Index.Search, Measure.Calculator, Form.JavaScript, View.ParallelView z drugim dokumentem (okno wyboru zatwierdzane OK), Read.Activate/CurrentPage/Stop/FromPage bez dźwięku, stan czytania po zamknięciu dokumentów; zrzuty: karta Ułatwienia dostępu z panelem, panel wyszukiwania, panel czytania |
| R-20 | `docs\ograniczenia\renderer-viewer.md` pkt 20 i `edit-convert-ocr.md` (test dymny bez karty Ułatwienia dostępu) – nieaktualne | przegląd | zaktualizowane; `OGRANICZENIA.md` – nowy rozdz. 26 „Etap 3” |

Po każdej naprawie: build Release 0/0 i testy (końcowo 752/752 ×3).

### 9.4 Co nie działa lub działa z ograniczeniami (etap 3; pełna lista: `docs\OGRANICZENIA.md`, rozdz. 26)
- JavaScript formularzy działa w procesie programu; po poprawkach R-13…R-15 pozostaje ryzyko szczątkowe: nieprzewidziana funkcja wbudowana Jint alokująca dużo pamięci w jednej instrukcji – tylko w trybie „dowolny JavaScript” (za zgodą użytkownika). Komunikaty błędów skryptów są techniczne, po angielsku.
- Czytanie na głos: tylko głosy SAPI; tekst w kolejności strumienia treści (bez tagów).
- Indeks: wyszukiwanie liniowe, bez OCR, bez aktualizacji w tle.
- Ułatwienia dostępu: podzbiór PDF/UA, bez automatycznego tagowania.
- Widok równoległy: dwa dokumenty, drugi tylko do podglądu.
- Linie praw autorskich Jint/Acornima wzięte z metadanych pakietów (bez lat) – przed dystrybucją komercyjną porównać z plikami LICENSE repozytoriów (plików z sieci nie pobierano).

### 9.5 Czego nie sprawdzono
- Czytania na głos z dźwiękiem na głośnikach (świadomie – exe w trybie diagnostycznym i testy kierują mowę do pustego urządzenia; synteza, wybór głosu i zdarzenia słów sprawdzone).
- Klikania myszą: rysowania pomiarów, okna skali i kalibracji, tworzenia pola kodu kreskowego, budowania indeksu z okna wyboru folderu, edycji skryptu w oknie Skrypty formularza, przeciągania rozdzielacza widoku równoległego (pokryte testami usług i budowy okien, nie pulpitem).
- Odczytu kodów QR/PDF417 zewnętrznym skanerem (test dekoduje render PDFium przez ZXing).
- Zgodności pomiarów i skryptów z innym czytnikiem (Acrobat) – brak w systemie.

## 10. Rozbudowa po etapie 3 – wersja 0.2.0-test

**Build i testy (czysty build):** 0 błędów, 0 ostrzeżeń; 1282 testy zaliczone, 0 niepowodzeń, 1 pominięty (Word, brak aplikacji). Tłumaczenia: 0 błędów. Kontrast motywów: OK.

**Test exe (`dist\ZetPDF.exe` 0.2.0-test, foldery z polskimi znakami, osobny profil):** `--diag-smoke`: 500 stron OK; formularz OK; AES-256 z hasłem właściciela OK; AES-256 z hasłem użytkownika: otwarcie i zapis działają, jedno zgłoszenie opisane niżej; plik uszkodzony: okno błędu, bez awarii.

**Instalator `ZetPDF-Setup-0.2.0-test.exe`:** cicha instalacja do folderu z polskimi znakami, uruchomienie i odinstalowanie bez pozostałości. Kreatora nie przeklikano.

**Czego nie sprawdzono:** klikania nowych okien na pulpicie; fizycznej drukarki i druku dwustronnego; skanera; Worda i PowerPointa; pobierania strony z internetu; prawdziwego serwera OCSP/CRL/TSA; zewnętrznej walidacji PDF/A i PAdES (veraPDF i podobne nie są zainstalowane), przez co zgodność z tymi standardami nie jest potwierdzona.

**Poprawki z przeglądu i przeglądu bezpieczeństwa:** opis w `docs\DECYZJE.md` (D-27 do D-46), ograniczenia w `docs\OGRANICZENIA.md` (rozdz. 27–29).
