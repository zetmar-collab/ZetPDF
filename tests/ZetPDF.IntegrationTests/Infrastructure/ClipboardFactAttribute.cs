using System.Runtime.InteropServices;

namespace ZetPDF.IntegrationTests.Infrastructure;

/// <summary>
/// Test wymagający schowka systemowego. Gdy schowek jest niedostępny dla procesu (np. zablokowana sesja Windows –
/// OpenClipboard zwraca błąd przez cały czas próby), test jest jawnie POMIJANY z podaniem przyczyny, a nie zaliczany.
/// Przy dostępnym schowku asercje testu działają bez zmian.
/// </summary>
public sealed class ClipboardFactAttribute : FactAttribute
{
    public ClipboardFactAttribute()
    {
        if (!ClipboardProbe.IsAvailable()) Skip = "Schowek systemowy niedostępny (OpenClipboard nie powiodło się – np. zablokowana sesja Windows).";
    }
}

internal static class ClipboardProbe
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();

    /// <summary>Do 3 s prób (inny program może chwilowo trzymać schowek).</summary>
    public static bool IsAvailable()
    {
        for (int i = 0; i < 30; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                CloseClipboard();
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>Wywołanie operacji na schowku z ponawianiem przy CLIPBRD_E_CANT_OPEN (schowek chwilowo zajęty).</summary>
    public static void Retry(Action action)
    {
        for (int i = 0; ; i++)
        {
            try { action(); return; }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0 && i < 50) { Thread.Sleep(100); }
        }
    }
}
