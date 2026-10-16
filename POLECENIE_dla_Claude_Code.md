# Zadanie: zbuduj edytor PDF dla Windows (roboczo "ZetPDF")

Pracujesz w folderze `C:\Users\Marek\Claude\PDF-Edytor`. Leży tu plik `Foxit_PDF_Editor_raport.md` z analizą Foxit PDF Editor 14. Przeczytaj go w całości, zanim zaczniesz.

Masz zbudować własny edytor PDF o podobnym zakresie funkcji. To nie jest kopia Foxita: własna nazwa, własna ikona, własny układ i wygląd, własny kod. Nie kopiuj nazw plików, ikon, tekstów ani zasobów Foxita. Zakres funkcji bierz z raportu, ale oprzyj się na tym, co da się uczciwie zrobić z bibliotek open source.

## 1. Wymagania twarde

- Platforma: Windows 10/11 x64.
- Wynik końcowy: jeden działający plik `ZetPDF.exe` (self-contained, single-file, bez instalowania .NET) w folderze `dist\`, do testów.
- Interfejs w dwóch językach: polski i angielski. Przełączanie w Ustawieniach, bez restartu. Wszystkie teksty w plikach zasobów (`.resx` lub słowniki), żadnych tekstów na sztywno w kodzie. Domyślnie język systemu.
- Motyw jasny i ciemny, plus opcja "jak w systemie". Przełączanie na żywo, zapamiętywane. Dotyczy też okien dialogowych i panelu podglądu stron.
- Interfejs w układzie wstążki (karty z grupami przycisków), pasek szybkiego dostępu, panel boczny z kartami (miniatury, zakładki, komentarze, załączniki), wiele otwartych dokumentów w kartach, pasek stanu z numerem strony i powiększeniem. Własny wygląd, nie klon Office ani Foxita.
- Nic nie wysyła danych w sieć. Brak telemetrii. Wszystko działa lokalnie i offline.

## 2. Stos technologiczny (trzymaj się tego, chyba że masz mocny powód)

- C# / .NET 8, WPF, wzorzec MVVM. Do wstążki i motywów możesz użyć biblioteki o licencji MIT/Apache (np. Fluent.Ribbon, albo własne kontrolki).
- Renderowanie i odczyt PDF: PDFium (licencja BSD) przez PDFiumCore lub PDFiumSharp.
- Modyfikacja struktury PDF (strony, metadane, zakładki, szyfrowanie, adnotacje): PDFsharp (MIT) i/lub PdfPig (Apache 2.0). Zanim użyjesz jakiejkolwiek biblioteki, sprawdź jej licencję. Nie używaj bibliotek AGPL/GPL (iText 7, MuPDF), bo program ma pozostać możliwy do sprzedaży.
- OCR: Tesseract (Apache 2.0), dane językowe `pol` i `eng` dołączone do programu.
- Podpisy cyfrowe: System.Security.Cryptography / BouncyCastle (MIT).
- Kod kreskowy/QR: ZXing.Net.
- Zapisz w `docs\LICENCJE.md` listę wszystkich użytych bibliotek z licencjami.

Jeśli któraś funkcja z listy poniżej jest niewykonalna na tych bibliotekach, nie udawaj. Zrób wersję ograniczoną i opisz ograniczenie w `docs\OGRANICZENIA.md`.

## 3. Zakres funkcji

Podzielone na etapy. Etap 1 i 2 są obowiązkowe w tej wersji. Etap 3 rób dopiero po zielonym przeglądzie etapów 1 i 2. Funkcje wymagające chmury, kont, subskrypcji i integracji firmowych z raportu (Foxit DMS, eSign, DocuSign, iManage, SharePoint, Purview/MIP, Asystent AI z kredytami, wtyczki ECM) pomijamy.

### Etap 1: rdzeń (obowiązkowy)
1. Otwieranie (też przeciągnij i upuść, plik z linii poleceń, PDF chroniony hasłem), zapis, zapis jako, ostatnie pliki, wiele kart.
2. Podgląd: powiększanie, dopasuj stronę/szerokość, układ (jedna strona, ciągły, dwie strony), obrót widoku, pełny ekran, tryb odczytu, przejdź do strony.
3. Wyszukiwanie tekstu w dokumencie (Ctrl+F) z podświetlaniem, zaznaczanie i kopiowanie tekstu, migawka obszaru do schowka.
4. Panel miniatur ze zmianą kolejności stron przeciąganiem. Panel zakładek (dodaj, edytuj, usuń, zagnieżdżaj).
5. Organizacja stron (karta Organizuj z raportu): wstaw z pliku, pusta strona, usuń, wyodrębnij, zastąp, duplikuj, przenieś, odwróć kolejność, obróć, kadruj, zmień rozmiar, podziel dokument (co N stron / według zakładek), scal wiele PDF.
6. Znak wodny (tekst/obraz), tło, nagłówek i stopka, numeracja stron, numeracja Bates.
7. Komentarze: wyróżnij, podkreśl, przekreśl, notatka, pole tekstowe, maszyna do pisania, rysunki (prostokąt, owal, linia, strzałka, ołówek), stemple (standardowe i własny z obrazu). Panel komentarzy z listą, odpowiedziami i stanem. Eksport/import komentarzy (XFDF).
8. Wypełnianie formularzy AcroForm (pola tekstowe, wyboru, opcji, listy). Wypełnij i podpisz: tekst, ✓, ✗, podpis odręczny z rysunku lub obrazu.
9. Ochrona: hasło otwierania i hasło uprawnień (AES-256), usuwanie zabezpieczeń (przy znanym haśle).
10. Drukowanie (zakres, skalowanie, kilka stron na arkuszu).
11. Ustawienia: język, motyw, tożsamość (autor komentarzy), domyślne powiększenie, skróty klawiszowe zgodne z sekcją 20 raportu tam, gdzie to sensowne.
12. Cofnij/Ponów dla wszystkich operacji edycyjnych.

### Etap 2: edycja i konwersja
1. Edytuj tekst: edycja istniejących bloków tekstu i dodawanie nowego tekstu (z doborem czcionki, rozmiaru, koloru). Jeśli edycja w miejscu nie jest niezawodna, zaimplementuj dodawanie tekstu i zakrywanie starego, a ograniczenie opisz.
2. Edytuj obiekty: przesuwanie, skalowanie, obracanie, usuwanie obrazów. Dodawanie obrazów i kształtów.
3. Łącza (URL, strona), automatyczne wykrywanie adresów URL, zakładki z nagłówków.
4. Konwersja: obrazy (JPG/PNG/TIFF/BMP) do PDF, PDF do obrazów, PDF do tekstu (TXT), PDF do DOCX w wersji podstawowej (tekst i obrazy; opisz jakość), scalanie plików różnych typów, utworzenie PDF ze schowka.
5. OCR: bieżący plik i zaznaczony obszar, język pol/eng, wynik jako ukryta warstwa tekstu. Podgląd i poprawianie niepewnych wyników.
6. Redakcja: zaznacz obszar/tekst, szukaj i redaguj wzorce (PESEL, NIP, e-mail, telefon, numer karty), zastosuj redakcję tak, żeby treść była trwale usunięta ze strumienia (nie tylko zakryta czarnym prostokątem). Ostrzeżenie o nieodwracalności.
7. Oczyszczanie dokumentu: usuń metadane, komentarze, załączniki, skrypty, ukryte warstwy.
8. Optymalizator: zmniejszanie rozmiaru (kompresja obrazów, usuwanie nieużywanych obiektów), konwersja do skali szarości.
9. Podpisy cyfrowe z certyfikatu PFX/magazynu Windows, widoczne pole podpisu, weryfikacja podpisów, znacznik czasu TSA (adres serwera ustawiany przez użytkownika).
10. Formularze: tworzenie pól (tekst, pole wyboru, opcje, lista, przycisk, podpis, data), kolejność tabulacji, import/eksport danych (FDF/XFDF/CSV).
11. Porównanie dwóch PDF (widok obok siebie, zmiany tekstu).
12. Kreator działań w wersji uproszczonej: zapisywane sekwencje operacji uruchamiane na wielu plikach (przetwarzanie wsadowe), z gotowymi działaniami: "Przygotuj do dystrybucji", "Zoptymalizuj dla Internetu", "Archiwizuj".

### Etap 3: dodatki (po przeglądzie)
Pomiary i skala, kalkulator księgowy, czytanie na głos (System.Speech), sprawdzanie ułatwień dostępu (podstawowe reguły: tytuł, język, tagi, tekst alternatywny), kod kreskowy/QR w formularzu, indeks pełnotekstowy dla folderu, widok równoległy dwóch dokumentów, JavaScript w formularzach (tylko jeśli da się bezpiecznie osadzić silnik).

## 4. Sposób pracy: zespół agentów

Pracuj jako koordynator. Najpierw stwórz w `.claude\agents\` definicje agentów (pliki `.md` z frontmatterem), potem uruchamiaj ich przez narzędzie Agent. Agenci pracujący na osobnych modułach nie mogą edytować tych samych plików; interfejsy między modułami ustal wcześniej (krok 1).

Agenci:
1. **architekt**: czyta raport, tworzy `docs\ARCHITEKTURA.md` (podział na projekty/moduły, interfejsy, model dokumentu, system poleceń z Cofnij/Ponów, lista bibliotek z licencjami), szkielet solucji `ZetPDF.sln` i kontrakty (interfejsy), które implementują pozostali. Sprawdza, czy biblioteki faktycznie obsługują zaplanowane funkcje, i zgłasza braki.
2. **ui-shell**: okno główne, wstążka, karty dokumentów, panele boczne, pasek stanu, system motywów jasny/ciemny, system lokalizacji pl/en (zasoby), okno Ustawień. Dostarcza zasoby tekstów w obu językach dla wszystkich poleceń.
3. **renderer-viewer**: widok dokumentu (PDFium), powiększanie, układy stron, wyszukiwanie, zaznaczanie tekstu, miniatury, zakładki, drukowanie.
4. **pages-engine**: operacje na stronach i oznaczenia stron (znak wodny, tło, nagłówek/stopka, Bates), scalanie, dzielenie, optymalizator, oczyszczanie.
5. **annotations-forms**: komentarze, stemple, formularze (wypełnianie i tworzenie), Wypełnij i podpisz, XFDF/FDF.
6. **edit-convert-ocr**: edycja tekstu i obiektów, konwersje, OCR (Tesseract), redakcja.
7. **security-signatures**: hasła i szyfrowanie, podpisy cyfrowe, weryfikacja, znacznik czasu, porównanie dokumentów, Kreator działań.
8. **tester**: pisze testy jednostkowe i integracyjne (xUnit) na zestawie przykładowych PDF (wygeneruj własne: tekstowy, ze skanem, z formularzem, zaszyfrowany, wielostronicowy, z polskimi znakami). Testuje każdą funkcję etapu 1 i 2, uruchamia `dotnet test`, zgłasza błędy koordynatorowi.
9. **recenzent-kodu (agent końcowy)**: patrz sekcja 5.

Kolejność:
1. Architekt tworzy szkielet i kontrakty. Koordynator zatwierdza.
2. Agenci 2 i 3 równolegle (powłoka i podgląd). Po nich buduje się i sprawdza, że program uruchamia się i otwiera PDF.
3. Agenci 4, 5, 6, 7 równolegle, każdy w swoim module. Po każdym buduj całość (`dotnet build -c Release`) i napraw konflikty.
4. Tester przechodzi przez wszystkie funkcje, błędy wracają do właściwych agentów.
5. Recenzent końcowy (sekcja 5).
6. Publikacja pliku exe.

Zadania rozpisz na liście zadań i odznaczaj na bieżąco. Nie przechodź do następnego kroku z niezbudowanym kodem.

## 5. Agent końcowy: kontrola poprawności

Agent `recenzent-kodu` uruchamia się na końcu, pracuje na świeżym kontekście (nie widział procesu budowy) i niczego nie zakłada. Sprawdza:
1. `dotnet build -c Release` bez błędów, ostrzeżenia przejrzane, krytyczne naprawione.
2. `dotnet test`: wszystkie testy przechodzą. Wynik w `docs\RAPORT_TESTOW.md`.
3. Kod: wyjątki nieobsłużone przy otwieraniu uszkodzonych/zaszyfrowanych PDF, wycieki pamięci i niezwalniane uchwyty PDFium, praca na wątku UI (blokowanie interfejsu przy dużych plikach, np. 500 stron), operacje na plikach bez kopii zapasowej, ścieżki z polskimi znakami i spacjami.
4. Lokalizacja: skrypt sprawdza, że każdy klucz tekstu istnieje w pl i en, brak tekstów na sztywno w XAML i kodzie, brak ucięć tekstu w obu językach.
5. Motywy: żaden element nie ma na sztywno ustawionego koloru; sprawdź kontrast tekstu w obu motywach, w tym w oknach dialogowych.
6. Bezpieczeństwo: redakcja faktycznie usuwa treść (wyodrębnij tekst z zredagowanego pliku i potwierdź, że fraza zniknęła), oczyszczanie usuwa metadane, szyfrowanie AES-256 otwiera się w innym czytniku, brak połączeń sieciowych poza TSA ustawionym przez użytkownika, brak sekretów w kodzie.
7. Licencje: każda biblioteka z `docs\LICENCJE.md` zgodna z użyciem komercyjnym; brak AGPL/GPL.
8. Test dymny exe: uruchom `dist\ZetPDF.exe`, otwórz przykładowy PDF, wykonaj po jednej operacji z każdej karty wstążki, przełącz język i motyw, zapisz i otwórz zapisany plik. Jeśli możesz zrobić zrzuty ekranu obu motywów i obu języków, zapisz je w `docs\zrzuty\`.
9. Każdy znaleziony błąd: opisz, napraw (lub oddaj odpowiedniemu agentowi), uruchom ponownie testy. Powtarzaj, aż lista będzie pusta albo zostaną tylko świadomie odroczone sprawy opisane w `docs\OGRANICZENIA.md`.

Raport końcowy `docs\RAPORT_KONTROLI.md`: co sprawdzono, co znaleziono, co naprawiono, co nie działa lub działa z ograniczeniami. Pisz rzeczowo, bez ukrywania niedociągnięć. Nie raportuj funkcji jako działającej, jeśli jej nie uruchomiłeś.

## 6. Publikacja

```
dotnet publish src\ZetPDF.App -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

Jeśli natywne biblioteki (PDFium, Tesseract) nie dają się upakować w jeden plik, zostaw `ZetPDF.exe` plus niezbędne DLL i foldery w `dist\` i napisz to wprost. Dołącz `dist\README.txt` z krótkim opisem uruchomienia (po polsku i angielsku). Wersja w oknie "O programie": 0.1.0-test.

## 7. Zasady

- Pracuj samodzielnie do końca, pytaj tylko gdy zablokuje cię decyzja, której nie da się podjąć rozsądnym domyślnym wyborem. Wybór opisz w `docs\DECYZJE.md`.
- Nie wymyślaj: jeśli czegoś nie wiesz o API biblioteki, sprawdź dokumentację lub przetestuj w małym przykładzie.
- Nie kasuj i nie modyfikuj plików poza folderem projektu. Przykładowe PDF do testów generuj własne, nie używaj prywatnych dokumentów.
- Na koniec podaj mi: ścieżkę do exe, listę funkcji, które działają, listę ograniczeń i jak uruchomić testy.
