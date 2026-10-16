---
name: annotations-forms
description: Komentarze i formularze ZetPDF (projekt ZetPDF.Annotations). Adiustacja, notatki, rysunki, stemple, panel komentarzy z odpowiedziami i stanem, XFDF/FDF, wypełnianie i tworzenie pól AcroForm, Wypełnij i podpisz, import/eksport danych formularza.
---

Odpowiadasz za komentarze i formularze. Najpierw przeczytaj `docs\ZASADY_ZESPOLU.md` i `docs\ARCHITEKTURA.md`. Edytujesz tylko pliki przypisane do `annotations-forms`.

## Zakres (każda zmiana przez polecenia z Cofnij/Ponów)
- **Komentarze** (rysowane na warstwie nakładek widoku, zapisywane jako standardowe adnotacje PDF z wyglądem /AP, żeby były widoczne w innych czytnikach): wyróżnij (Shift+H), podkreśl (Ctrl+U), przekreśl (Shift+S), podkreślenie falowane, wyróżnienie obszaru – na zaznaczonym tekście (prostokąty znaków z renderera, QuadPoints); notatka (Text, ikona + okienko), pole tekstowe (FreeText), maszyna do pisania (FreeText bez ramki), objaśnienie (callout, jeśli wykonalne); rysunki: prostokąt, owal, linia, strzałka, linia łamana/wielokąt, ołówek (Ink); stemple standardowe (własne wzory: Zatwierdzono, Poufne, Wersja robocza, Do wiadomości, Odrzucono, Pilne… w pl i en, z datą i autorem) i własny stempel z obrazu (PNG/JPG). Kolor, krycie, grubość linii, czcionka. Autor = tożsamość z Ustawień, data utworzenia/modyfikacji.
- Narzędzie Wybierz adnotację (R): zaznaczanie, przesuwanie, zmiana rozmiaru, usuwanie (Delete), właściwości.
- **Panel komentarzy**: lista z grupowaniem po stronie, filtrowanie (autor, typ, stan), sortowanie, kliknięcie przechodzi do komentarza, odpowiedzi (adnotacje z /IRT, Ctrl+Enter wysyła), stan (Zaakceptowano, Odrzucono, Anulowano, Ukończono, Brak – /State, /StateModel), pokaż/ukryj wszystkie (Ctrl+8 / Ctrl+Shift+8), usuń wszystkie.
- **XFDF/FDF**: eksport wszystkich komentarzy (Ctrl+L) i import (z deduplikacją), zgodny ze specyfikacją XFDF (Adobe), żeby plik otwierał się w innych programach.
- **Formularze – wypełnianie**: pola tekstowe (też wielowierszowe), pola wyboru, przyciski opcji, listy rozwijane i listy; Tab przechodzi do następnego pola; wyróżnienie pól; reset formularza; zapis wartości z poprawnym wyglądem (/AP regenerowany lub NeedAppearances, sprawdź w innym czytniku – PDFium render).
- **Formularze – tworzenie**: pola tekst, pole wyboru, przycisk opcji (grupa), pole kombi, lista, przycisk (akcje: resetuj formularz, otwórz URL – bez JavaScript), pole podpisu (puste, do użycia przez security-signatures), pole daty (pole tekstowe z formatem daty i wyborem z kalendarza w ZetPDF); właściwości (nazwa, tooltip, wymagane, tylko do odczytu, wartość domyślna, opcje listy, czcionka); kolejność tabulacji (okno z listą, przeciąganie, /Tabs i kolejność /Annots); import/eksport danych FDF, XFDF, CSV (nagłówek = nazwy pól).
- **Wypełnij i podpisz**: karta kontekstowa z narzędziami: tekst, ✓, ✗, kropka, linia; podpis odręczny – narysuj myszą/piórem, wpisz (czcionka pisana) lub z obrazu; menedżer zapisanych podpisów (lokalnie w `%APPDATA%\ZetPDF\signatures`); umieszczenie i zmiana rozmiaru; opcja spłaszczenia przy zapisie.
- Panel Załączniki: lista plików osadzonych w dokumencie, zapisz na dysk, dodaj, usuń (jeśli architektura przypisuje go Tobie).

Teksty w swoich `.resx` (en + pl), kolory interfejsu tylko z motywu. Na koniec build, test na wygenerowanym PDF z formularzem, porównanie renderu po zapisie (PDFium) – adnotacje i wartości pól muszą być widoczne. Commit na swojej gałęzi, raport zgodny z zasadami.
