# Znane ograniczenia ZetPDF

Koordynator scala tu pliki agentów z `docs\ograniczenia\<agent>.md`. Poniżej ograniczenia znane po weryfikacji bibliotek (architekt, 16.10.2026).

## Biblioteki

1. **PDFsharp ≥ 6.5 ma licencję AGPL** – zostajemy na 6.2.4 (MIT); nowe poprawki PDFsharp nie będą dostępne.
2. **PDFsharp modyfikuje dokument zaszyfrowany tylko z hasłem właściciela** i nie zachowuje szyfrowania przy zapisie. Obejście w architekturze: odszyfrowanie przez PDFium (`FPDF_REMOVE_SECURITY`) do pamięci + ponowne szyfrowanie przy zapisie. Dopóki renderer (PDFium) nie jest zaimplementowany, otwarcie hasłem użytkownika działa tylko w trybie zapasowym PDFsharp, czyli wcale (błąd hasła) – dotyczy wyłącznie szkieletu.
3. **Hasło użytkownika nieznane przy otwarciu hasłem właściciela (AES-256)** – nie da się go odtworzyć; przy zapisie hasło otwierania = hasło właściciela (DECYZJE D-08). Przy otwarciu hasłem użytkownika – nowe losowe hasło właściciela.
4. **PDFium rozróżnia „brak hasła” i „złe hasło” tylko po tym, czy podano hasło** (oba = `FPDF_ERR_PASSWORD`).
5. **PDFsharp nie ma wysokopoziomowego API do tworzenia pól AcroForm** (tylko odczyt/zapis wartości istniejących pól). Tworzenie pól = budowanie słowników PDF ręcznie (zweryfikowane, działa) – wygląd `/AP` trzeba generować samemu lub polegać na `/NeedAppearances` (nie wszystkie czytniki to respektują).
6. **Edycja tekstu w miejscu**: PDFsharp daje tylko parser operatorów (`ContentReader`); czcionki podzbiorowe/CID utrudniają podmianę – spodziewany tryb „usuń/zakryj stary + dodaj nowy” (szczegóły opisze edit-convert-ocr).
7. **Tesseract 5.2.0 nie działa wprost w publikacji single-file** – obejście zaimplementowane (rozpakowanie z zasobów do `%LOCALAPPDATA%\ZetPDF\ocr\5.2.0\`). Model `tessdata_fast` jest szybki, ale słabszy na polskich znakach diakrytycznych (w teście „Zażółć” → „Zazoic”, „gęślą” poprawnie, średnia pewność 0,93) – stąd okno podejrzanych wyników. Lepsze modele (`tessdata_best`) są ~4× większe.
8. **Podpis PDFsharp na dokumencie otwartym z AES-256 zapisuje plik bez szyfrowania** – security-signatures musi ustawić szyfrowanie w tym samym zapisie co podpis lub zaakceptować ograniczenie (podpis + szyfrowanie razem do sprawdzenia).
9. **WPF KeyGesture nie obsługuje skrótów `Shift+litera` ani pojedynczych liter** – obsługa ręczna poza polami tekstowymi (DECYZJE D-12).
10. **Skróty z raportu Foxita nieprzeniesione**: `Alt+3/6/7` (narzędzia; kolizja z akceleratorami wstążki), `Ctrl+4/5/6` (Reflow, Grubość linii, Przeglądarka tekstu – funkcje poza zakresem etapów 1–2), `Ctrl+Shift+F` (wyszukiwanie zaawansowane), `Ctrl+J`, `Ctrl+R`, czytanie na głos (etap 3), e-mail (`Ctrl+Shift+K/L` – bez funkcji sieciowych), `Ctrl+I` (eksport wyróżnionego tekstu), `Shift+I`/`Ctrl+Shift+R` (Wstaw/Zastąp tekst – brak w etapach 1–2).

## Modele i wydajność

11. Każda edycja zapisuje cały plik PDFsharp (model zrzutów). Dla bardzo dużych plików (setki MB) edycje będą wolne; budżet pamięci historii 256 MB/dokument, starsze zrzuty na dysku.
12. Dokument jest w całości w pamięci (bajty) – pliki > ~1 GB nieobsługiwane.
