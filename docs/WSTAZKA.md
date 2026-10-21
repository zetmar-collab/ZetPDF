# Wstążka ZetPDF – karty, grupy i polecenia

> Wygenerowano przez `tools/catalog/catalog.py` (jedyne źródło prawdy: ten skrypt → `src/ZetPDF.Core/Commands/*.g.cs`). Teksty: klucz `Cmd.<Id>` (etykieta), `Cmd.<Id>.Tip` (podpowiedź, opcjonalnie), `Ribbon.Tab.<Id>`, `Ribbon.Group.<Id>` w `src/ZetPDF.App/Resources/Strings*.resx` (ui-shell).
> Skróty w notacji WPF `KeyGesture` (`D0` = klawisz 0, `OemPlus` = `+`). Kilka skrótów rozdzielonych `;`. Klawisze jednoliterowe działają tylko po włączeniu w Ustawieniach i nigdy, gdy fokus jest w polu tekstowym.

## Pasek szybkiego dostępu

`File.Open`, `File.Save`, `Edit.Undo`, `Edit.Redo`, `File.Print`

## Plik (backstage)

| Id | PL | EN | Moduł (agent) | Skrót |
|---|---|---|---|---|
| `File.Open` | Otwórz | Open | Shell (ui-shell) | Ctrl+O |
| `File.Save` | Zapisz | Save | Shell (ui-shell) | Ctrl+S |
| `File.SaveAs` | Zapisz jako | Save As | Shell (ui-shell) | Ctrl+Shift+S |
| `File.Revert` | Przywróć | Revert | Shell (ui-shell) |  |
| `File.Close` | Zamknij | Close | Shell (ui-shell) | Ctrl+W, Ctrl+F4 |
| `File.CloseAll` | Zamknij wszystko | Close All | Shell (ui-shell) | Ctrl+Shift+W |
| `File.Properties` | Właściwości dokumentu | Document Properties | Shell (ui-shell) | Ctrl+D |
| `File.Print` | Drukuj | Print | Viewer (renderer-viewer) | Ctrl+P |
| `File.CreateFromFile` | Z pliku | From File | Editing (edit-convert-ocr) | Ctrl+N |
| `File.CombineFiles` | Połącz pliki | Combine Files | Editing (edit-convert-ocr) |  |
| `File.CreateBlank` | Pusty dokument | Blank Document | Pages (pages-engine) |  |
| `File.CreateFromClipboard` | Ze schowka | From Clipboard | Editing (edit-convert-ocr) |  |
| `File.OptimizeReduceSize` | Zmniejsz rozmiar pliku | Reduce File Size | Pages (pages-engine) |  |
| `File.OptimizeAdvanced` | Optymalizacja zaawansowana | Advanced Optimization | Pages (pages-engine) |  |
| `File.ActionWizard` | Kreator działań | Action Wizard | Security (security-signatures) |  |
| `File.Settings` | Ustawienia | Settings | Shell (ui-shell) | Ctrl+K |
| `File.Exit` | Zakończ | Exit | Shell (ui-shell) | Ctrl+Q |

## Karta `Home` – Początek / Home

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Home.Tools` Narzędzia | `Tool.Hand` | Ręka | Hand | Viewer (renderer-viewer) | Toggle |  | H |
| `Home.Tools` Narzędzia | `Tool.SelectText` | Zaznacz tekst | Select Text | Viewer (renderer-viewer) | Toggle |  | V |
| `Home.Tools` Narzędzia | `Tool.SelectAnnotation` | Wybierz adnotację | Select Annotation | Annotations (annotations-forms) | Toggle |  | R |
| `Home.Tools` Narzędzia | `Tool.Snapshot` | Migawka | Snapshot | Viewer (renderer-viewer) | Toggle |  |  |
| `Home.Zoom` Powiększenie | `View.ZoomIn` | Powiększ | Zoom In | Viewer (renderer-viewer) | Button | Ctrl+OemPlus, Ctrl+Add |  |
| `Home.Zoom` Powiększenie | `View.ZoomOut` | Pomniejsz | Zoom Out | Viewer (renderer-viewer) | Button | Ctrl+OemMinus, Ctrl+Subtract |  |
| `Home.Zoom` Powiększenie | `View.ZoomTo` | Powiększ do… | Zoom To… | Viewer (renderer-viewer) | Button | Ctrl+M |  |
| `Home.Zoom` Powiększenie | `View.FitPage` | Dopasuj stronę | Fit Page | Viewer (renderer-viewer) | Button | Ctrl+D0 |  |
| `Home.Zoom` Powiększenie | `View.FitWidth` | Dopasuj szerokość | Fit Width | Viewer (renderer-viewer) | Button | Ctrl+D2 |  |
| `Home.Zoom` Powiększenie | `View.FitVisible` | Dopasuj widoczne | Fit Visible | Viewer (renderer-viewer) | Button | Ctrl+D3 |  |
| `Home.Zoom` Powiększenie | `View.ActualSize` | Rzeczywisty rozmiar | Actual Size | Viewer (renderer-viewer) | Button | Ctrl+D1 |  |
| `Home.View` Widok | `View.RotateLeft` | Obróć widok w lewo | Rotate View Left | Viewer (renderer-viewer) | Button | Ctrl+Shift+OemMinus |  |
| `Home.View` Widok | `View.RotateRight` | Obróć widok w prawo | Rotate View Right | Viewer (renderer-viewer) | Button | Ctrl+Shift+OemPlus |  |
| `Home.Edit` Edycja | `Edit.EditText` | Edytuj tekst | Edit Text | Editing (edit-convert-ocr) | Toggle |  | T |
| `Home.Edit` Edycja | `Edit.EditObject` | Edytuj obiekt | Edit Object | Editing (edit-convert-ocr) | Toggle |  | O |
| `Home.Edit` Edycja | `Comment.Typewriter` | Maszyna do pisania | Typewriter | Annotations (annotations-forms) | Toggle | Shift+T |  |
| `Home.Edit` Edycja | `Comment.Highlight` | Wyróżnij | Highlight | Annotations (annotations-forms) | Toggle | Shift+H |  |
| `Home.Pages` Strony | `Pages.InsertFromFile` | Wstaw z pliku | Insert from File | Pages (pages-engine) | Button | Ctrl+Shift+I |  |
| `Home.Pages` Strony | `Pages.InsertBlank` | Pusta strona | Blank Page | Pages (pages-engine) | Button | Ctrl+Shift+T |  |
| `Home.Pages` Strony | `Nav.AddBookmark` | Dodaj zakładkę | Add Bookmark | Viewer (renderer-viewer) | Button | Ctrl+B |  |
| `Home.Recognize` Rozpoznawanie | `Ocr.QuickRecognize` | Szybkie rozpoznawanie | Quick Recognition | Editing (edit-convert-ocr) | Button |  |  |
| `Home.Sign` Podpis | `FillSign.Open` | Wypełnij i podpisz | Fill & Sign | Annotations (annotations-forms) | Button |  |  |

## Karta `Convert` – Konwertuj / Convert

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Convert.Create` Utwórz | `File.CreateFromFile` | Z pliku | From File | Editing (edit-convert-ocr) | Button | Ctrl+N |  |
| `Convert.Create` Utwórz | `File.CombineFiles` | Połącz pliki | Combine Files | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Create` Utwórz | `File.CreateBlank` | Pusty dokument | Blank Document | Pages (pages-engine) | Button |  |  |
| `Convert.Create` Utwórz | `File.CreateFromClipboard` | Ze schowka | From Clipboard | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Export` Eksport | `Convert.ToImages` | Do obrazów | To Images | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Export` Eksport | `Convert.ToText` | Do tekstu (TXT) | To Text (TXT) | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Export` Eksport | `Convert.ToDocx` | Do Word (DOCX) | To Word (DOCX) | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Ocr` OCR | `Ocr.RecognizeDocument` | Rozpoznaj tekst (OCR) | Recognize Text (OCR) | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Ocr` OCR | `Ocr.RecognizeRegion` | OCR zaznaczonego obszaru | OCR Selected Area | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Ocr` OCR | `Ocr.QuickRecognize` | Szybkie rozpoznawanie | Quick Recognition | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Ocr` OCR | `Ocr.SuspectResults` | Podejrzane wyniki | Suspect Results | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Color` Kolor | `Convert.Grayscale` | Skala szarości | Grayscale | Pages (pages-engine) | Button |  |  |

## Karta `Edit` – Edytuj / Edit

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Edit.Content` Edycja treści | `Edit.EditText` | Edytuj tekst | Edit Text | Editing (edit-convert-ocr) | Toggle |  | T |
| `Edit.Content` Edycja treści | `Edit.EditObject` | Edytuj obiekt | Edit Object | Editing (edit-convert-ocr) | Toggle |  | O |
| `Edit.Content` Edycja treści | `Edit.AddText` | Dodaj tekst | Add Text | Editing (edit-convert-ocr) | Toggle |  |  |
| `Edit.Content` Edycja treści | `Edit.AddImage` | Dodaj obraz | Add Image | Editing (edit-convert-ocr) | Button |  |  |
| `Edit.Content` Edycja treści | `Edit.AddShape` | Dodaj kształt | Add Shape | Editing (edit-convert-ocr) | Menu |  |  |
| `Edit.Content` Edycja treści | `Edit.FindReplace` | Wyszukaj i zamień | Find and Replace | Editing (edit-convert-ocr) | Button | Ctrl+T |  |
| `Edit.Links` Łącza i nawigacja | `Edit.Link` | Łącze | Link | Editing (edit-convert-ocr) | Toggle |  | L |
| `Edit.Links` Łącza i nawigacja | `Edit.CreateLinksFromUrls` | Utwórz łącza z adresów URL | Create Links from URLs | Editing (edit-convert-ocr) | Button |  |  |
| `Edit.Links` Łącza i nawigacja | `Edit.RemoveAllLinks` | Usuń wszystkie łącza | Remove All Links | Editing (edit-convert-ocr) | Button |  |  |
| `Edit.Links` Łącza i nawigacja | `Nav.AddBookmark` | Dodaj zakładkę | Add Bookmark | Viewer (renderer-viewer) | Button | Ctrl+B |  |
| `Edit.Links` Łącza i nawigacja | `Edit.BookmarksFromHeadings` | Zakładki z nagłówków | Bookmarks from Headings | Editing (edit-convert-ocr) | Button |  |  |
| `Edit.Attachments` Załączniki | `Attach.AddFile` | Załącz plik | Attach File | Annotations (annotations-forms) | Button |  |  |

## Karta `Organize` – Organizuj / Organize

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Organize.Pages` Strony | `Pages.InsertFromFile` | Wstaw z pliku | Insert from File | Pages (pages-engine) | Button | Ctrl+Shift+I |  |
| `Organize.Pages` Strony | `Pages.InsertBlank` | Pusta strona | Blank Page | Pages (pages-engine) | Button | Ctrl+Shift+T |  |
| `Organize.Pages` Strony | `Pages.Delete` | Usuń strony | Delete Pages | Pages (pages-engine) | Button | Ctrl+Shift+D |  |
| `Organize.Pages` Strony | `Pages.Extract` | Wyodrębnij | Extract | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Replace` | Zastąp | Replace | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Duplicate` | Duplikuj | Duplicate | Pages (pages-engine) | Button | Ctrl+Alt+D |  |
| `Organize.Pages` Strony | `Pages.Move` | Przenieś | Move | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Swap` | Zamień | Swap | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Reverse` | Odwróć kolejność | Reverse Order | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Interleave` | Przeplatanie | Interleave | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Split` | Podziel dokument | Split Document | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Merge` | Scal pliki PDF | Merge PDFs | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.RotateLeft` | Obróć w lewo | Rotate Left | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.RotateRight` | Obróć w prawo | Rotate Right | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Rotate` | Obracanie stron… | Rotate Pages… | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Crop` | Kadruj strony | Crop Pages | Pages (pages-engine) | Button |  | C |
| `Organize.Transform` Podział i przekształcenia | `Pages.Resize` | Zmień rozmiar stron | Resize Pages | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Flatten` | Spłaszcz | Flatten | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.Watermark` | Znak wodny | Watermark | Pages (pages-engine) | Menu |  |  |
| ↳ menu | `Stamp.Watermark.Add` | Dodaj znak wodny | Add Watermark | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Watermark.Update` | Aktualizuj znak wodny | Update Watermark | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Watermark.RemoveAll` | Usuń wszystkie znaki wodne | Remove All Watermarks | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.Background` | Tło | Background | Pages (pages-engine) | Menu |  |  |
| ↳ menu | `Stamp.Background.Add` | Dodaj tło | Add Background | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Background.Update` | Aktualizuj tło | Update Background | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Background.RemoveAll` | Usuń wszystkie tła | Remove All Backgrounds | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.HeaderFooter` | Nagłówek i stopka | Header & Footer | Pages (pages-engine) | Menu |  |  |
| ↳ menu | `Stamp.HeaderFooter.Add` | Dodaj nagłówek i stopkę | Add Header & Footer | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.HeaderFooter.Update` | Aktualizuj nagłówek i stopkę | Update Header & Footer | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.HeaderFooter.RemoveAll` | Usuń nagłówki i stopki | Remove Headers & Footers | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.PageNumbers` | Numeracja stron | Page Numbers | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.Bates` | Numeracja Bates | Bates Numbering | Pages (pages-engine) | Menu |  |  |
| ↳ menu | `Stamp.Bates.Add` | Dodaj numerację Bates | Add Bates Numbering | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Bates.RemoveAll` | Usuń numerację Bates | Remove Bates Numbering | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Pages.PageLabels` | Format numerów stron | Page Number Format | Pages (pages-engine) | Button |  |  |

## Karta `Comment` – Komentarz / Comment

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Comment.Markup` Adiustacja tekstu | `Comment.Highlight` | Wyróżnij | Highlight | Annotations (annotations-forms) | Toggle | Shift+H |  |
| `Comment.Markup` Adiustacja tekstu | `Comment.AreaHighlight` | Wyróżnienie obszaru | Area Highlight | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Markup` Adiustacja tekstu | `Comment.Underline` | Podkreśl | Underline | Annotations (annotations-forms) | Toggle | Ctrl+U |  |
| `Comment.Markup` Adiustacja tekstu | `Comment.Squiggly` | Podkreślenie falowane | Squiggly Underline | Annotations (annotations-forms) | Toggle | Shift+U |  |
| `Comment.Markup` Adiustacja tekstu | `Comment.StrikeOut` | Przekreśl | Strikethrough | Annotations (annotations-forms) | Toggle | Shift+S |  |
| `Comment.Text` Notatki i tekst | `Comment.Note` | Notatka | Note | Annotations (annotations-forms) | Toggle | Ctrl+Shift+N | S |
| `Comment.Text` Notatki i tekst | `Comment.Typewriter` | Maszyna do pisania | Typewriter | Annotations (annotations-forms) | Toggle | Shift+T |  |
| `Comment.Text` Notatki i tekst | `Comment.TextBox` | Pole tekstowe | Text Box | Annotations (annotations-forms) | Toggle | Shift+E | X |
| `Comment.Text` Notatki i tekst | `Comment.Callout` | Objaśnienie | Callout | Annotations (annotations-forms) | Toggle | Shift+C |  |
| `Comment.Drawing` Rysowanie | `Comment.Rectangle` | Prostokąt | Rectangle | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Oval` | Owal | Oval | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Line` | Linia | Line | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Arrow` | Strzałka | Arrow | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Polygon` | Wielokąt | Polygon | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Polyline` | Linia łamana | Polyline | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Pencil` | Ołówek | Pencil | Annotations (annotations-forms) | Toggle | Shift+P |  |
| `Comment.Stamps` Stemple | `Comment.Stamp` | Stempel | Stamp | Annotations (annotations-forms) | Menu |  | K |
| `Comment.Stamps` Stemple | `Comment.CustomStamp` | Stempel z obrazu | Stamp from Image | Annotations (annotations-forms) | Button |  |  |
| `Comment.Measure` Pomiary | `Measure.Distance` | Odległość | Distance | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Measure` Pomiary | `Measure.Perimeter` | Obwód | Perimeter | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Measure` Pomiary | `Measure.Area` | Pole powierzchni | Area | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Measure` Pomiary | `Measure.Scale` | Skala i kalibracja | Scale & Calibration | Annotations (annotations-forms) | Button |  |  |
| `Comment.Measure` Pomiary | `Measure.Export` | Eksportuj pomiary | Export Measurements | Annotations (annotations-forms) | Button |  |  |
| `Comment.Measure` Pomiary | `Measure.Calculator` | Kalkulator księgowy | Accounting Calculator | Annotations (annotations-forms) | Button |  |  |
| `Comment.Manage` Zarządzanie | `Comment.Import` | Importuj komentarze | Import Comments | Annotations (annotations-forms) | Button |  |  |
| `Comment.Manage` Zarządzanie | `Comment.ExportAll` | Eksportuj komentarze | Export Comments | Annotations (annotations-forms) | Button | Ctrl+L |  |
| `Comment.Manage` Zarządzanie | `Comment.ShowAll` | Pokaż wszystkie komentarze | Show All Comments | Annotations (annotations-forms) | Button | Ctrl+D8 |  |
| `Comment.Manage` Zarządzanie | `Comment.HideAll` | Ukryj wszystkie komentarze | Hide All Comments | Annotations (annotations-forms) | Button | Ctrl+Shift+D8 |  |
| `Comment.Manage` Zarządzanie | `Comment.DeleteAll` | Usuń wszystkie komentarze | Delete All Comments | Annotations (annotations-forms) | Button |  |  |
| `Comment.Manage` Zarządzanie | `View.PanelComments` | Komentarze | Comments | Shell (ui-shell) | Button |  |  |

## Karta `View` – Widok / View

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `View.Layout` Układ stron | `View.SinglePage` | Jedna strona | Single Page | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Layout` Układ stron | `View.Continuous` | Ciągły | Continuous | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Layout` Układ stron | `View.TwoPages` | Dwie strony | Two Pages | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Layout` Układ stron | `View.TwoPagesContinuous` | Dwie strony ciągłe | Two Pages Continuous | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Mode` Tryb | `View.ReadingMode` | Tryb odczytu | Reading Mode | Viewer (renderer-viewer) | Toggle | Ctrl+H |  |
| `View.Mode` Tryb | `View.FullScreen` | Pełny ekran | Full Screen | Viewer (renderer-viewer) | Toggle | F11 |  |
| `View.Mode` Tryb | `View.RotateLeft` | Obróć widok w lewo | Rotate View Left | Viewer (renderer-viewer) | Button | Ctrl+Shift+OemMinus |  |
| `View.Mode` Tryb | `View.RotateRight` | Obróć widok w prawo | Rotate View Right | Viewer (renderer-viewer) | Button | Ctrl+Shift+OemPlus |  |
| `View.Navigation` Nawigacja | `Nav.GoToPage` | Przejdź do strony | Go to Page | Viewer (renderer-viewer) | Button | Ctrl+G |  |
| `View.Navigation` Nawigacja | `Nav.FirstPage` | Pierwsza strona | First Page | Viewer (renderer-viewer) | Button | Ctrl+Home |  |
| `View.Navigation` Nawigacja | `Nav.PreviousPage` | Poprzednia strona | Previous Page | Viewer (renderer-viewer) | Button | Ctrl+PageUp |  |
| `View.Navigation` Nawigacja | `Nav.NextPage` | Następna strona | Next Page | Viewer (renderer-viewer) | Button | Ctrl+PageDown |  |
| `View.Navigation` Nawigacja | `Nav.LastPage` | Ostatnia strona | Last Page | Viewer (renderer-viewer) | Button | Ctrl+End |  |
| `View.Find` Wyszukiwanie | `Nav.Find` | Znajdź | Find | Viewer (renderer-viewer) | Button | Ctrl+F |  |
| `View.Find` Wyszukiwanie | `Nav.FindNext` | Znajdź następny | Find Next | Viewer (renderer-viewer) | Button | F3 |  |
| `View.Find` Wyszukiwanie | `Nav.FindPrevious` | Znajdź poprzedni | Find Previous | Viewer (renderer-viewer) | Button | Shift+F3 |  |
| `View.Find` Wyszukiwanie | `Index.Search` | Wyszukiwanie zaawansowane | Advanced Search | Editing (edit-convert-ocr) | Button | Ctrl+Shift+F |  |
| `View.Find` Wyszukiwanie | `Index.BuildFolder` | Indeks pełnotekstowy folderu | Full-text Index of Folder | Editing (edit-convert-ocr) | Button |  |  |
| `View.Compare` Porównanie | `Compare.Start` | Porównaj dokumenty | Compare Documents | Security (security-signatures) | Button |  |  |
| `View.Compare` Porównanie | `View.ParallelView` | Widok równoległy | Parallel View | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Compare` Porównanie | `View.SyncScroll` | Synchronizuj widoki | Synchronize Views | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Read` Czytanie na głos | `Read.Activate` | Aktywuj czytanie | Activate Read Aloud | Viewer (renderer-viewer) | Toggle | Ctrl+Shift+Y |  |
| `View.Read` Czytanie na głos | `Read.CurrentPage` | Przeczytaj bieżącą stronę | Read Current Page | Viewer (renderer-viewer) | Button | Ctrl+Shift+V |  |
| `View.Read` Czytanie na głos | `Read.FromPage` | Czytaj od bieżącej strony | Read from Current Page | Viewer (renderer-viewer) | Button | Ctrl+Shift+B |  |
| `View.Read` Czytanie na głos | `Read.Pause` | Pauza | Pause | Viewer (renderer-viewer) | Button | Ctrl+Shift+P |  |
| `View.Read` Czytanie na głos | `Read.Stop` | Zatrzymaj | Stop | Viewer (renderer-viewer) | Button | Ctrl+Shift+E |  |
| `View.Panels` Panele | `View.TogglePanel` | Panel boczny | Side Panel | Shell (ui-shell) | Toggle | F4 |  |
| `View.Panels` Panele | `View.PanelThumbnails` | Miniatury | Thumbnails | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.PanelBookmarks` | Zakładki | Bookmarks | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.PanelComments` | Komentarze | Comments | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.PanelAttachments` | Załączniki | Attachments | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.PanelSignatures` | Podpisy | Signatures | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.ToggleRibbon` | Zwiń wstążkę | Collapse Ribbon | Shell (ui-shell) | Toggle | Ctrl+F11 |  |

## Karta `Form` – Formularz / Form

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Form.Fields` Pola formularza | `Form.TextField` | Pole tekstowe | Text Field | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.CheckBox` | Pole wyboru | Check Box | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.RadioButton` | Przycisk opcji | Radio Button | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.ComboBox` | Pole kombi | Combo Box | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.ListBox` | Pole listy | List Box | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.Button` | Przycisk | Button | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.SignatureField` | Pole podpisu | Signature Field | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.DateField` | Pole daty | Date Field | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.BarcodeField` | Pole kodu kreskowego | Barcode Field | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Design` Projektowanie | `Tool.SelectAnnotation` | Wybierz adnotację | Select Annotation | Annotations (annotations-forms) | Toggle |  | R |
| `Form.Design` Projektowanie | `Form.TabOrder` | Kolejność tabulacji | Tab Order | Annotations (annotations-forms) | Button |  |  |
| `Form.Design` Projektowanie | `Form.HighlightFields` | Wyróżnij pola | Highlight Fields | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Design` Projektowanie | `Form.JavaScript` | Skrypty formularza | Form Scripts | Annotations (annotations-forms) | Button |  |  |
| `Form.Data` Dane | `Form.ImportData` | Importuj dane | Import Data | Annotations (annotations-forms) | Button |  |  |
| `Form.Data` Dane | `Form.ExportData` | Eksportuj dane | Export Data | Annotations (annotations-forms) | Button |  |  |
| `Form.Data` Dane | `Form.Reset` | Resetuj formularz | Reset Form | Annotations (annotations-forms) | Button |  |  |

## Karta `Protect` – Ochrona / Protect

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Protect.Security` Zabezpieczenia | `Security.PasswordProtect` | Ochrona hasłem | Password Protection | Security (security-signatures) | Button |  |  |
| `Protect.Security` Zabezpieczenia | `Security.RemoveSecurity` | Usuń zabezpieczenia | Remove Security | Security (security-signatures) | Button |  |  |
| `Protect.Security` Zabezpieczenia | `Security.Properties` | Właściwości zabezpieczeń | Security Properties | Security (security-signatures) | Button |  |  |
| `Protect.Redaction` Redakcja | `Redact.MarkText` | Zaznacz tekst do redakcji | Mark Text for Redaction | Editing (edit-convert-ocr) | Toggle |  |  |
| `Protect.Redaction` Redakcja | `Redact.MarkArea` | Zaznacz obszar do redakcji | Mark Area for Redaction | Editing (edit-convert-ocr) | Toggle |  |  |
| `Protect.Redaction` Redakcja | `Redact.SearchAndRedact` | Wyszukaj i redaguj | Search & Redact | Editing (edit-convert-ocr) | Button |  |  |
| `Protect.Redaction` Redakcja | `Redact.Apply` | Zastosuj redakcje | Apply Redactions | Editing (edit-convert-ocr) | Button |  |  |
| `Protect.HiddenData` Ukryte dane | `Sanitize.Document` | Oczyszczanie dokumentu | Sanitize Document | Pages (pages-engine) | Button |  |  |
| `Protect.Signatures` Podpisy | `FillSign.Open` | Wypełnij i podpisz | Fill & Sign | Annotations (annotations-forms) | Button |  |  |
| `Protect.Signatures` Podpisy | `Sign.PlaceSignature` | Podpisz cyfrowo | Sign Digitally | Security (security-signatures) | Button |  |  |
| `Protect.Signatures` Podpisy | `Sign.Validate` | Weryfikuj podpisy | Validate Signatures | Security (security-signatures) | Button |  |  |
| `Protect.Signatures` Podpisy | `Sign.Timestamp` | Znacznik czasu dokumentu | Document Timestamp | Security (security-signatures) | Button |  |  |
| `Protect.Signatures` Podpisy | `Sign.DigitalIds` | Identyfikatory cyfrowe | Digital IDs | Security (security-signatures) | Button |  |  |

## Karta `Accessibility` – Ułatwienia dostępu / Accessibility

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Access.Check` Sprawdzanie | `Access.FullCheck` | Pełne sprawdzenie | Full Check | Editing (edit-convert-ocr) | Button |  |  |
| `Access.Check` Sprawdzanie | `Access.Report` | Raport ułatwień dostępu | Accessibility Report | Editing (edit-convert-ocr) | Button |  |  |
| `Access.Check` Sprawdzanie | `Access.SetProperties` | Tytuł, język i tekst alternatywny | Title, Language & Alt Text | Editing (edit-convert-ocr) | Button |  |  |
| `Access.Read` Czytanie na głos | `Read.Activate` | Aktywuj czytanie | Activate Read Aloud | Viewer (renderer-viewer) | Toggle | Ctrl+Shift+Y |  |
| `Access.Read` Czytanie na głos | `Read.CurrentPage` | Przeczytaj bieżącą stronę | Read Current Page | Viewer (renderer-viewer) | Button | Ctrl+Shift+V |  |
| `Access.Read` Czytanie na głos | `Read.FromPage` | Czytaj od bieżącej strony | Read from Current Page | Viewer (renderer-viewer) | Button | Ctrl+Shift+B |  |
| `Access.Read` Czytanie na głos | `Read.Pause` | Pauza | Pause | Viewer (renderer-viewer) | Button | Ctrl+Shift+P |  |
| `Access.Read` Czytanie na głos | `Read.Stop` | Zatrzymaj | Stop | Viewer (renderer-viewer) | Button | Ctrl+Shift+E |  |

## Karta `Help` – Pomoc / Help

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Help.Main` Pomoc | `Help.Shortcuts` | Skróty klawiszowe | Keyboard Shortcuts | Shell (ui-shell) | Button | F1 |  |
| `Help.Main` Pomoc | `Help.Licenses` | Licencje bibliotek | Third-party Licenses | Shell (ui-shell) | Button |  |  |
| `Help.Main` Pomoc | `Help.OpenLogFolder` | Folder dziennika | Log Folder | Shell (ui-shell) | Button |  |  |
| `Help.Main` Pomoc | `Help.About` | O programie | About | Shell (ui-shell) | Button |  |  |

## Karta `FillSign` – Wypełnij i podpisz / Fill & Sign (kontekstowa)

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `FillSign.Tools` Narzędzia | `FillSign.Text` | Tekst | Text | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Tools` Narzędzia | `FillSign.Check` | Znacznik ✓ | Check Mark ✓ | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Tools` Narzędzia | `FillSign.Cross` | Krzyżyk ✗ | Cross ✗ | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Tools` Narzędzia | `FillSign.Dot` | Kropka | Dot | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Tools` Narzędzia | `FillSign.Line` | Linia | Line | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Signatures` Podpisy | `FillSign.Signature` | Podpis odręczny | Handwritten Signature | Annotations (annotations-forms) | Menu |  |  |
| `FillSign.Signatures` Podpisy | `FillSign.ManageSignatures` | Zarządzaj podpisami | Manage Signatures | Annotations (annotations-forms) | Button |  |  |
| `FillSign.Signatures` Podpisy | `FillSign.Flatten` | Spłaszcz przy zapisie | Flatten on Save | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.CloseGroup` Zamknij | `FillSign.Close` | Zamknij Wypełnij i podpisz | Close Fill & Sign | Annotations (annotations-forms) | Button |  |  |

## Karta `CompareResults` – Wyniki porównania / Compare Results (kontekstowa)

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `CompareResults.Navigate` Zmiany | `Compare.Previous` | Poprzednia zmiana | Previous Change | Security (security-signatures) | Button | Ctrl+OemComma |  |
| `CompareResults.Navigate` Zmiany | `Compare.Next` | Następna zmiana | Next Change | Security (security-signatures) | Button | Ctrl+OemPeriod |  |
| `CompareResults.Report` Raport | `Compare.Report` | Raport porównania | Comparison Report | Security (security-signatures) | Button |  |  |
| `CompareResults.Report` Raport | `Compare.Close` | Zamknij porównanie | Close Comparison | Security (security-signatures) | Button |  |  |

## Polecenia bez miejsca na wstążce (tylko skrót / menu kontekstowe)

| Id | PL | EN | Moduł | Skrót |
|---|---|---|---|---|
| `Edit.Copy` | Kopiuj | Copy | Viewer (renderer-viewer) | Ctrl+C |
| `Edit.SelectAll` | Zaznacz wszystko | Select All | Viewer (renderer-viewer) | Ctrl+A |
| `Shell.CommandSearch` | Wyszukaj polecenie | Search Commands | Shell (ui-shell) | Alt+Q |
| `Shell.NextDocument` | Następny dokument | Next Document | Shell (ui-shell) | Ctrl+Tab |
| `Shell.PreviousDocument` | Poprzedni dokument | Previous Document | Shell (ui-shell) | Ctrl+Shift+Tab |

## Liczba poleceń według modułu

| Moduł | Agent | Liczba |
|---|---|---|
| Shell | ui-shell | 25 |
| Viewer | renderer-viewer | 37 |
| Pages | pages-engine | 40 |
| Annotations | annotations-forms | 56 |
| Editing | edit-convert-ocr | 29 |
| Security | security-signatures | 13 |

Razem: 200 poleceń, 12 kart, 45 grup.
