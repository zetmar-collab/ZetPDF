# Zasady zespołu ZetPDF (obowiązują każdego agenta)

Przeczytaj przed rozpoczęciem pracy: `POLECENIE_dla_Claude_Code.md` (zadanie), `Foxit_PDF_Editor_raport.md` (zakres funkcji), a po kroku 1 także `docs\ARCHITEKTURA.md` (kontrakty i podział modułów).

## Środowisko
- Windows 11 x64, .NET 8 (SDK 8.0.423 przypięte w `global.json`), WPF, C# 12. Dostępne też SDK 10, ale budujemy na 8.
- Powłoka: PowerShell lub Git Bash. Ścieżka projektu: `C:\Users\Marek\Claude\PDF-Edytor` (albo Twój worktree).
- Budowanie: `dotnet build ZetPDF.sln -c Release`. Testy: `dotnet test ZetPDF.sln -c Release`.
- Repozytorium git. Jeśli pracujesz w worktree, na końcu zrób commit swojej pracy na swojej gałęzi (komunikat po polsku, zakończony linią `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`). Nie scalaj gałęzi i nie pushuj.

## Własność plików
- Edytujesz wyłącznie pliki swojego modułu (lista w `docs\ARCHITEKTURA.md`, sekcja „Własność modułów”).
- Kontrakty w `src\ZetPDF.Core\Contracts\` są zamrożone. Jeśli potrzebujesz zmiany kontraktu, nie zmieniaj go sam: opisz potrzebę w raporcie końcowym (sekcja „Prośby do koordynatora”) i obejdź ją lokalnie, jeśli się da.
- Nie dodawaj projektów do solucji i nie zmieniaj plików innych modułów. Wyjątek: możesz dodać PackageReference w csproj swojego modułu.

## Kod
- Wzorzec MVVM (CommunityToolkit.Mvvm). Ciężkie operacje (render, OCR, zapis, operacje na wielu stronach) asynchronicznie, poza wątkiem UI, z możliwością anulowania tam, gdzie to ma sens, i z postępem.
- Każda operacja zmieniająca dokument idzie przez system poleceń z Cofnij/Ponów opisany w architekturze.
- Żadnych tekstów interfejsu na sztywno w XAML ani w kodzie. Wszystkie teksty w plikach `.resx` (angielski domyślny `Strings.resx` + polski `Strings.pl.resx`) swojego modułu, z kluczami w konwencji z architektury. Każdy klucz musi istnieć w obu językach.
- Żadnych kolorów na sztywno w XAML i kodzie interfejsu. Tylko `DynamicResource` z kluczami pędzli motywu (lista w architekturze). Wyjątek: kolory treści PDF wybierane przez użytkownika (np. kolor wyróżnienia) i czerń redakcji.
- Uchwyty PDFium zawsze zwalniane (`IDisposable`, `using`). Dostęp do PDFium tylko przez warstwę renderującą, która serializuje wywołania (PDFium nie jest wątkowo bezpieczny).
- Zapis pliku: najpierw do pliku tymczasowego w tym samym folderze, potem podmiana (`File.Replace` z kopią `.bak` lub atomowe przeniesienie). Nigdy nie nadpisuj oryginału bez tego.
- Ścieżki z polskimi znakami i spacjami muszą działać (testuj na `C:\...\Próbny folder\zażółć gęślą.pdf`).
- Program nie łączy się sam z siecią. Wyjątki, oba na wyraźne polecenie użytkownika: znacznik czasu TSA pod adresem wpisanym przez użytkownika oraz polecenie Ze strony WWW (Microsoft Edge, DECYZJE D-36). Brak telemetrii, brak sprawdzania aktualizacji.
- Biblioteki: tylko licencje MIT, BSD, Apache 2.0, MS-PL lub podobne permisywne. Zakaz AGPL/GPL/LGPL-only-z-linkowaniem-statycznym (iText 7, MuPDF, Ghostscript itp.). Przed dodaniem biblioteki sprawdź jej licencję na nuget.org / w repozytorium i dopisz ją do swojego raportu.
- Nie wymyślaj API. Jeśli nie wiesz, jak działa metoda biblioteki, sprawdź źródła/dokumentację albo napisz mały test w `%TEMP%` lub w katalogu `scratch\` (ignorowanym), uruchom go i dopiero wtedy używaj.

## Raport końcowy agenta (zwracany koordynatorowi)
1. Co zrobiono (pliki, funkcje).
2. Jak to sprawdzono (build, testy, ręczne uruchomienie). Nie pisz, że coś działa, jeśli tego nie uruchomiłeś.
3. Ograniczenia i rzeczy niezrobione (te same dopisz do swojego pliku `docs\ograniczenia\<moduł>.md`, koordynator złoży je w `docs\OGRANICZENIA.md`).
4. Użyte biblioteki i licencje.
5. Prośby do koordynatora (zmiany kontraktów, konflikty).
