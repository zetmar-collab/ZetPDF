# ZetPDF

Edytor PDF dla Windows 10/11 x64 (C# / .NET 8 / WPF). Wersja **0.2.0-test**.
Interfejs po polsku i angielsku, motyw jasny i ciemny, bez telemetrii. Program sam nie łączy się z siecią; wyjątki działają tylko na wyraźne polecenie użytkownika: znacznik czasu, strona WWW do PDF, dane unieważnień certyfikatów.

## Pobieranie

Gotowy instalator i samodzielny plik `ZetPDF.exe` są w zakładce [Releases](../../releases).
Pliki nie są podpisane cyfrowo, więc Windows SmartScreen może pokazać ostrzeżenie („Więcej informacji” → „Uruchom mimo to”).

## Funkcje

- Podgląd: karty dokumentów, powiększanie i układy stron, wyszukiwanie, miniatury, zakładki, druk, czytanie na głos, widok równoległy.
- Strony: wstawianie, usuwanie, wyodrębnianie, obracanie, kadrowanie, podział, scalanie, znak wodny, nagłówek i stopka, numeracja Bates.
- Komentarze, stemple, pomiary, kalkulator księgowy, eksport/import XFDF.
- Formularze: wypełnianie i tworzenie pól (także kod QR/PDF417), dane FDF/XFDF/CSV, JavaScript formularzy w piaskownicy, Wypełnij i podpisz.
- Edycja tekstu i obiektów, łącza, konwersje (obrazy, TXT, DOCX), OCR (Tesseract, pol/eng).
- Ochrona: szyfrowanie AES-256, redakcja z trwałym usunięciem treści, oczyszczanie dokumentu, podpisy cyfrowe z weryfikacją, porównanie dokumentów, Kreator działań.
- Ułatwienia dostępu: sprawdzanie reguł i podstawowe automatyczne tagowanie; indeks pełnotekstowy folderu.
- Tworzenie PDF ze skanera, strony WWW (przez zainstalowaną przeglądarkę Edge) i plików Office (przez zainstalowany Microsoft Office); eksport do XLSX i HTML.
- PDF/A-2b: własne sprawdzanie reguł i konwersja (bez certyfikowanej walidacji).
- Rozpoznawanie pól formularza, druk wsadowy i broszury, panel warstw, autozapis z odzyskiwaniem po awarii.
- Podpisy długoterminowe (dane unieważnień w pliku), opcja domyślnie wyłączona.

Ograniczenia opisuje [docs/OGRANICZENIA.md](docs/OGRANICZENIA.md), wyniki kontroli [docs/RAPORT_KONTROLI.md](docs/RAPORT_KONTROLI.md).

## Budowanie

Wymagania: .NET SDK 8.0.4xx (przypięty w `global.json`), Windows x64, opcjonalnie Inno Setup 6 do instalatora.

```powershell
dotnet build ZetPDF.sln -c Release
dotnet test ZetPDF.sln -c Release
pwsh tools\publish\publish.ps1        # dist\ZetPDF.exe + README + licencje
pwsh installer\build-installer.ps1    # installer\Output\ZetPDF-Setup-<wersja>.exe
```

## Licencje bibliotek

Program korzysta wyłącznie z bibliotek o licencjach permisywnych (MIT, BSD, Apache 2.0); lista w [docs/LICENCJE.md](docs/LICENCJE.md).
PDFsharp jest celowo przypięty do wersji 6.2.4 (MIT) – nowsze wydania 6.5+ są na licencji AGPL.

Kod ZetPDF: © Marek Zettel, wszelkie prawa zastrzeżone (repozytorium nie udziela licencji na kod programu).
