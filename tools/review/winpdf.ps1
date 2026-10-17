param([string]$Pdf, [string]$Password, [string]$Png)
# Niezależny czytnik: Windows.Data.Pdf (silnik PDF systemu Windows, nie PDFium/PdfPig). Uruchamiać w Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asAction = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Pdf)) ([Windows.Storage.StorageFile])
try {
    if ($Password) { $doc = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file, $Password)) ([Windows.Data.Pdf.PdfDocument]) }
    else { $doc = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument]) }
} catch { Write-Output ("OTWARCIE NIEUDANE: " + $_.Exception.InnerException.InnerException.Message); exit 2 }
Write-Output ("Otwarto: strony=" + $doc.PageCount + ", chroniony hasłem=" + $doc.IsPasswordProtected)
if ($Png) {
    $page = $doc.GetPage(0)
    $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    $t = $asAction.Invoke($null, @($page.RenderToStreamAsync($stream))); $t.Wait()
    $net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream.GetInputStreamAt(0))
    $fs = [System.IO.File]::Create($Png); $net.CopyTo($fs); $fs.Close()
    Write-Output ("PNG: " + $Png)
}
