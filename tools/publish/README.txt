ZetPDF 0.1.0-test
=================

[PL]

Co to jest
  ZetPDF to edytor PDF dla Windows (wersja testowa 0.1.0-test). Jeden plik ZetPDF.exe – bez instalacji
  i bez instalowania .NET (środowisko .NET 8, PDFium i Tesseract OCR są w środku).

Wymagania
  - Windows 10 lub 11, 64-bitowy (x64).
  - Ok. 300 MB wolnego miejsca (przy pierwszym uruchomieniu .NET rozpakowuje biblioteki natywne do folderu
    tymczasowego, a przy pierwszym OCR dane Tesseract trafiają do %LOCALAPPDATA%\ZetPDF\ocr\5.2.0\).
  - Ikony: czcionka Segoe Fluent Icons (Windows 11) lub Segoe MDL2 Assets (Windows 10) – są w systemie.

Uruchamianie
  - Dwukrotne kliknięcie ZetPDF.exe, przeciągnięcie pliku PDF na okno albo z wiersza poleceń:
      ZetPDF.exe "C:\Dokumenty\plik.pdf" "drugi plik.pdf"
      ZetPDF.exe "zaszyfrowany.pdf" -pwdHASŁO        (hasło dla wszystkich podanych plików)
  - Język (polski/angielski) i motyw (jasny/ciemny/jak w systemie): Plik > Ustawienia (Ctrl+K), działa od razu.
  - Windows może przy pierwszym uruchomieniu pokazać ostrzeżenie SmartScreen (plik nie jest podpisany cyfrowo).

Nowe w etapie 3 (dodatki)
  - Pomiary: odległość, obwód, pole, skala i kalibracja, eksport CSV (Komentarz > Pomiary).
  - Kalkulator księgowy: suma liczb z zaznaczonego tekstu (format polski), wstawienie wyniku na stronę.
  - Pole kodu kreskowego/QR (QR, PDF417, DataMatrix) kodujące wartości pól formularza.
  - JavaScript w formularzach (obliczenia, formaty, walidacja) w piaskownicy z limitami; domyślnie tylko
    wbudowane funkcje AF*, dowolny skrypt dopiero po zgodzie (Formularz > Skrypty formularza). Skrypty dokumentu
    (przy otwarciu) nigdy nie są wykonywane.
  - Czytanie na głos (Ułatwienia dostępu > Czytanie na głos) – głosy SAPI zainstalowane w Windows, bez sieci.
  - Sprawdzanie ułatwień dostępu (tytuł, język, tagi, tekst alternatywny i inne) z naprawą i raportem HTML/TXT.
  - Widok równoległy dwóch dokumentów w jednej karcie (Widok > Porównanie), z synchronizacją przewijania.
  - Indeks pełnotekstowy folderu i wyszukiwanie zaawansowane (Widok > Wyszukiwanie) z eksportem CSV;
    indeks w %LOCALAPPDATA%\ZetPDF\index\ (pliki zaszyfrowane hasłem są pomijane).

Gdzie są dane
  - Ustawienia:        %APPDATA%\ZetPDF\settings.json (podpisy, stempele, działania Kreatora – obok)
  - Indeks folderów:  %LOCALAPPDATA%\ZetPDF\index\*.zidx
  - Dziennik (log):    %LOCALAPPDATA%\ZetPDF\logs\zetpdf-RRRRMMDD.log (przechowywany 14 dni)
  - Pliki tymczasowe:  %TEMP%\ZetPDF\<numer procesu>\ (usuwane przy zamknięciu programu)
  - Zmienna środowiskowa ZETPDF_HOME=<folder> przenosi wszystkie te foldery (np. wersja przenośna, testy).

Prywatność
  - Brak telemetrii, brak sprawdzania aktualizacji, brak kont. Program nie łączy się sam z siecią.
  - Dwa wyjątki, zawsze na Twoje polecenie:
    1) znacznik czasu podpisu cyfrowego (TSA) pod adresem, który sam wpiszesz w Ustawieniach – tylko wtedy,
       gdy zaznaczysz tę opcję przy podpisywaniu (wysyłany jest wyłącznie skrót SHA-256);
    2) polecenie Konwertuj > Ze strony WWW: po wpisaniu adresu i zatwierdzeniu w oknie stronę pobiera
       zainstalowana przeglądarka Microsoft Edge (osobny, tymczasowy profil usuwany po zakończeniu).
  - Łącza w dokumentach otwierają się w przeglądarce dopiero po potwierdzeniu.

Znane ograniczenia
  - Opis: docs\OGRANICZENIA.md w repozytorium (m.in. edycja tekstu w niektórych czcionkach tylko przez zakrycie,
    redakcja zamienia stronę na obraz, PDF->DOCX podstawowy, wbudowany w Windows podgląd PDF nie otwiera AES-256).

Licencje
  - Folder LICENSES\ zawiera teksty licencji bibliotek i noty o prawach autorskich (THIRD-PARTY-NOTICES.txt).
  - BRAKUJE w nim pełnych tekstów licencji FreeType (FTL), IJG/libjpeg-turbo, libpng i Unicode/ICU
    (komponenty wkompilowane w PDFium). Przed dystrybucją komercyjną trzeba dołączyć plik LICENSE z projektu
    https://github.com/bblanchon/pdfium-binaries (wersja 157.0.8100). Noty wymagane przez te licencje są
    w LICENSES\PDFium-ThirdParty.txt i w programie: Pomoc > Licencje bibliotek.

Wersja: 0.1.0-test (wersja do testów, nie do produkcji).


[EN]

What it is
  ZetPDF is a PDF editor for Windows (test build 0.1.0-test). A single ZetPDF.exe – no installation and no .NET
  installation required (.NET 8 runtime, PDFium and Tesseract OCR are embedded).

Requirements
  - Windows 10 or 11, 64-bit (x64).
  - About 300 MB of free disk space (on first start .NET extracts native libraries to the temp folder; on first
    OCR the Tesseract data goes to %LOCALAPPDATA%\ZetPDF\ocr\5.2.0\).
  - Icons use the Segoe Fluent Icons (Windows 11) or Segoe MDL2 Assets (Windows 10) system font.

How to run
  - Double-click ZetPDF.exe, drop a PDF onto the window, or from the command line:
      ZetPDF.exe "C:\Documents\file.pdf" "second file.pdf"
      ZetPDF.exe "encrypted.pdf" -pwdPASSWORD        (password used for all given files)
  - Language (Polish/English) and theme (light/dark/system): File > Settings (Ctrl+K), applied immediately.
  - Windows SmartScreen may warn on first start (the file is not code-signed).

New in stage 3 (add-ons)
  - Measuring: distance, perimeter, area, scale and calibration, CSV export (Comment > Measure).
  - Accounting calculator: sums numbers from the selected text (Polish format), inserts the result on the page.
  - Barcode/QR field (QR, PDF417, DataMatrix) encoding form field values.
  - JavaScript in forms (calculations, formats, validation) in a sandbox with limits; by default only built-in AF*
    functions, any script only after consent (Form > Form Scripts). Document-level scripts are never run.
  - Read Aloud (Accessibility > Read Aloud) – SAPI voices installed in Windows, no network.
  - Accessibility check (title, language, tags, alternative text and more) with fixes and an HTML/TXT report.
  - Parallel view of two documents in one tab (View > Compare), with synchronized scrolling.
  - Full-text folder index and advanced search (View > Find) with CSV export; the index is stored in
    %LOCALAPPDATA%\ZetPDF\index\ (password-protected files are skipped).

Where data is stored
  - Settings:     %APPDATA%\ZetPDF\settings.json (signatures, stamps, Action Wizard actions next to it)
  - Folder index: %LOCALAPPDATA%\ZetPDF\index\*.zidx
  - Log:          %LOCALAPPDATA%\ZetPDF\logs\zetpdf-YYYYMMDD.log (kept for 14 days)
  - Temp files:   %TEMP%\ZetPDF\<process id>\ (removed on exit)
  - Environment variable ZETPDF_HOME=<folder> relocates all of them (portable use, tests).

Privacy
  - No telemetry, no update checks, no accounts. The program never connects to the network on its own.
  - Two exceptions, always on your command: (1) the digital signature timestamp (TSA), see below;
    (2) Convert > From Web Page: after you enter an address and confirm in the window, the installed Microsoft Edge
    downloads the page (separate temporary profile, removed afterwards).
  - The only exception: the digital signature time stamp (TSA) at the address you enter in Settings, only when
    you tick that option while signing (only a SHA-256 hash is sent).
  - Links in documents open in the browser only after confirmation.

Known limitations
  - See docs\OGRANICZENIA.md in the repository (Polish).

Licenses
  - The LICENSES\ folder contains license texts and copyright notices (THIRD-PARTY-NOTICES.txt).
  - MISSING: full license texts of FreeType (FTL), IJG/libjpeg-turbo, libpng and Unicode/ICU (components compiled
    into PDFium). Before commercial distribution add the LICENSE file from
    https://github.com/bblanchon/pdfium-binaries (version 157.0.8100). The notices required by these licenses are
    in LICENSES\PDFium-ThirdParty.txt and in the program: Help > Third-party licenses.

Version: 0.1.0-test (test build, not for production use).
