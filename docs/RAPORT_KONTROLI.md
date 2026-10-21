# ZetPDF 0.1.0-test – raport kontroli końcowej (recenzent)

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
