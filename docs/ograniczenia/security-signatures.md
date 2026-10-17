# Ograniczenia – security-signatures (ZetPDF.Security)

## Ochrona hasłem
- Szyfrowanie stosowane przy zapisie (DECYZJE D-03/D-08) przez `SetEncryptionCommand`; zmiana wymaga pełnych uprawnień. Dokument otwarty hasłem użytkownika: ZetPDF pyta o hasło uprawnień i otwiera plik ponownie (niezapisane zmiany przepadają – z potwierdzeniem).
- AES-128 (R4): hasła tylko ze znakami Latin-1 (norma: PDFDocEncoding; różne programy różnie kodują np. „ą”). Okno blokuje takie hasła dla AES-128; AES-256 (UTF-8) obsługuje polskie znaki.
- PDFsharp 6.2.4 nie ma osobnego przełącznika bitu „ułatwienia dostępu” (bit 10) – w PDF 2.0 i tak traktowany jako ustawiony.
- Przy haśle właściciela i nieznanym niepustym haśle użytkownika: dla R2–R4 hasło użytkownika jest odzyskiwane i zachowywane; dla AES-256 obowiązuje D-08 (komunikat na pasku stanu i we Właściwościach zabezpieczeń).
- Uprawnienia chronią przed zmianami tylko w programach, które je respektują (ZetPDF je egzekwuje).

## Podpisy cyfrowe
- Własna implementacja zapisu przyrostowego (PDFsharp 6.2.4 podpisuje przez pełny zapis – unieważnia wcześniejsze podpisy i gubi szyfrowanie). Format `adbe.pkcs7.detached`, SHA-256, atrybut signingTime; brak profilu PAdES-B-B (ESS signing-certificate-v2) i danych LTV (DSS/VRI, CRL/OCSP w pliku).
- Zapis przyrostowy wcześniejszych podpisów zachowuje tylko dla dokumentu NIEZMIENIONEGO od otwarcia (podpisywany jest oryginalny plik z dysku). Każda edycja w ZetPDF to pełny zapis PDFsharp, który unieważnia istniejące podpisy (zgodnie z prawdą – dokument zmieniono).
- Pliki zaszyfrowane (AES-256, AES-128, RC4) są podpisywane z zachowaniem szyfrowania; /Contents podpisu nie jest szyfrowane (praktyka Acrobat/PDFium).
- Wygląd widocznego podpisu: tekst jako kontury glifów (Segoe UI) – poprawne polskie znaki bez osadzania czcionki, ale tekst wyglądu nie jest zaznaczalny. Obraz: PNG/JPEG/BMP/GIF/TIFF (dekoder WPF), przezroczystość jako SMask.
- Prostokąt podpisu rysowany na jednej stronie; strony z /Rotate – wygląd obracany, by był prosty na ekranie.
- Identyfikator z podpisem własnym: RSA 3072, PFX eksportowany przez .NET (szyfrowanie PKCS#12 domyślne dla Windows). Klucz PFX wczytywany jako efemeryczny (nie trafia do magazynu Windows).
- Certyfikaty z magazynu: tylko CurrentUser\My z kluczem prywatnym, ważne, z użyciem klucza „podpis cyfrowy/niezaprzeczalność” (lub bez rozszerzenia). Karty kryptograficzne – przez dostawcę CNG/CSP (mogą wyświetlić własne okno PIN).

## Znacznik czasu (TSA)
- Jedyne połączenie sieciowe: HTTP(S) POST do adresu z Ustawień, tylko po zaznaczeniu opcji w oknie podpisu lub potwierdzeniu „Znacznik czasu dokumentu”. Wysyłany jest wyłącznie skrót SHA-256.
- Testy używają lokalnej atrapy TSA (bez sieci); z prawdziwym serwerem TSA nie testowano.
- DocTimeStamp (ETSI.RFC3161) bez DSS/VRI.

## Weryfikacja
- Integralność (skrót zakresów /ByteRange vs CMS), zgodność /Contents z luką ByteRange, pokrycie całego pliku, ważność certyfikatu w chwili podpisu (czas ze znacznika czasu, jeśli jest), łańcuch z magazynów Windows bez sieci (AIA, CRL, OCSP wyłączone – unieważnienia nie są sprawdzane, komunikat w szczegółach).
- „Zmieniono po podpisaniu” = istnieją późniejsze rewizje; nie analizujemy, czy zmiany były dozwolone (DocMDP/FieldMDP).
- Nieobsługiwany format `adbe.x509.rsa_sha1` (status Błąd). Obsługiwane: adbe.pkcs7.detached, ETSI.CAdES.detached, adbe.pkcs7.sha1, ETSI.RFC3161.
- Panel weryfikuje plik z dysku, gdy dokument jest niezmieniony; po edycji – bieżącą wersję.

## Porównanie
- Tekst z PdfPig, różnice słowo po słowie (Myers, własna implementacja); dokumenty skanowane bez warstwy tekstu – tylko porównanie graficzne.
- Porównanie graficzne: render 36 DPI, komórki 6 px, tylko pary stron bez /Rotate i o tym samym rozmiarze; obszary pokrywające się ze zmianami tekstu są pomijane.
- Dopasowanie stron heurystyczne (liczba wspólnych słów, monotonicznie).
- Widok wyników w osobnym oknie (karty dokumentów są zarezerwowane dla sesji); skróty Ctrl+. / Ctrl+, działają w oknie wyników i (przez kartę kontekstową) w oknie głównym.

## Kreator działań
- Kroki korzystają z modułów innych agentów przez kontrakty; przed scaleniem ich implementacji krok kończy się komunikatem „Ten krok nie jest dostępny…” (raport per plik).
- „Archiwizuj” NIE tworzy PDF/A (brak walidacji PDF/A, osadzania profili ICC i czcionek w ZetPDF) – uzupełnia metadane i usuwa skrypty; opisano to w opisie działania.
- Pliki zaszyfrowane nie są przetwarzane (błąd w raporcie). Krok OCR – zawsze wszystkie strony. Znak wodny z obrazu nie jest edytowalny w edytorze parametrów (tylko tekst).
- Hasła kroku „Chroń hasłem” nie są zapisywane w JSON – okno pyta o nie przed uruchomieniem.

## Weryfikacja ręczna
- Okna sprawdzone testami (konstrukcja, edytor parametrów) i uruchomieniem aplikacji (start bez błędów w dzienniku); pełnego klikania okien nie wykonano.
