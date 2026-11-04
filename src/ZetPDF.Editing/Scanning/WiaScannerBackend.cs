using System.Runtime.InteropServices;
using ZetPDF.Core.Localization;

namespace ZetPDF.Editing.Scanning;

/// <summary>
/// Skanowanie przez WIA 2.0: obiekty automatyzacji z wiaaut.dll (składnik Windows), typy dynamiczne, bez pakietów.
/// Identyfikatory właściwości i kody błędów z wia.h / WiaDef.h.
/// </summary>
public sealed class WiaScannerBackend : IScannerBackend
{
    private const string ProgId = "WIA.DeviceManager";
    private const int ScannerDeviceType = 1;
    private const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    private const int PropDeviceId = 2;
    private const int PropDeviceName = 7;
    private const int PropDocumentHandlingCapabilities = 3086;
    private const int PropDocumentHandlingSelect = 3088;
    private const int PropPages = 3096;
    private const int PropCurrentIntent = 6146;
    private const int PropHorizontalResolution = 6147;
    private const int PropVerticalResolution = 6148;
    private const int FeederCapable = 1;
    private const int SelectFeeder = 1;
    private const int SelectFlatbed = 2;
    private const int IntentColor = 1, IntentGray = 2, IntentText = 4;

    private const int ErrorPaperJam = unchecked((int)0x80210002);
    private const int ErrorPaperEmpty = unchecked((int)0x80210003);
    private const int ErrorOffline = unchecked((int)0x80210005);
    private const int ErrorBusy = unchecked((int)0x80210006);
    private const int MaxPagesInFeeder = 1000;

    public bool IsAvailable => Type.GetTypeFromProgID(ProgId) is not null;

    public IReadOnlyList<ScannerInfo> ListDevices()
    {
        var list = new List<ScannerInfo>();
        foreach (dynamic info in Manager().DeviceInfos)
        {
            if ((int)info.Type != ScannerDeviceType) continue;
            string id = (string)Property(info.Properties, PropDeviceId)!;
            string name = (string?)Property(info.Properties, PropDeviceName) ?? id;
            bool feeder = false;
            try
            {
                var caps = Property(info.Connect().Properties, PropDocumentHandlingCapabilities);
                feeder = caps is int c && (c & FeederCapable) != 0;
            }
            catch (COMException) { } // urządzenie wyłączone: i tak pokazujemy je na liście, błąd padnie przy skanowaniu
            list.Add(new ScannerInfo(id, name, feeder));
        }
        return list;
    }

    public IReadOnlyList<byte[]> Scan(ScanSettings settings, CancellationToken ct)
    {
        try
        {
            dynamic? device = null;
            foreach (dynamic info in Manager().DeviceInfos)
                if ((string?)Property(info.Properties, PropDeviceId) == settings.DeviceId) device = info.Connect();
            if (device is null) throw new InvalidOperationException("Scanner not found.").WithMessageKey("Edit.Scan.Error.Offline");

            TrySet(device.Properties, PropDocumentHandlingSelect, settings.UseFeeder ? SelectFeeder : SelectFlatbed);
            TrySet(device.Properties, PropPages, settings.UseFeeder ? 0 : 1);
            dynamic? item = null;
            foreach (dynamic i in device.Items) { item = i; break; }
            if (item is null) throw new InvalidOperationException("Scanner has no items.").WithMessageKey("Edit.Scan.Error.Offline");
            TrySet(item.Properties, PropCurrentIntent, settings.Mode switch { ScanColorMode.Gray => IntentGray, ScanColorMode.Mono => IntentText, _ => IntentColor });
            TrySet(item.Properties, PropHorizontalResolution, settings.Dpi);
            TrySet(item.Properties, PropVerticalResolution, settings.Dpi);

            var pages = new List<byte[]>();
            for (int n = 0; n < MaxPagesInFeeder; n++)
            {
                ct.ThrowIfCancellationRequested();
                try { pages.Add(Bytes(item.Transfer(FormatBmp))); }
                catch (COMException ex) when (ex.HResult == ErrorPaperEmpty && pages.Count > 0) { break; }
                if (!settings.UseFeeder) break;
            }
            return pages;
        }
        catch (COMException ex)
        {
            throw Translate(ex);
        }
    }

    private static dynamic Manager()
    {
        var type = Type.GetTypeFromProgID(ProgId) ?? throw new InvalidOperationException("WIA is not available.").WithMessageKey("Edit.Scan.Unavailable");
        return Activator.CreateInstance(type)!;
    }

    private static byte[] Bytes(dynamic imageFile)
    {
        dynamic vector = imageFile.FileData;
        object data = vector.BinaryData;
        return (byte[])data;
    }

    private static object? Property(dynamic properties, int id)
    {
        foreach (dynamic p in properties)
            if ((int)p.PropertyID == id) return (object?)p.Value;
        return null;
    }

    /// <summary>Urządzenia nie obsługują wszystkich wartości; niedostępne ustawienie zostaje domyślne.</summary>
    private static void TrySet(dynamic properties, int id, int value)
    {
        foreach (dynamic p in properties)
        {
            if ((int)p.PropertyID != id) continue;
            try { p.Value = value; }
            catch (COMException) { }
            return;
        }
    }

    internal static Exception Translate(COMException ex) => ex.HResult switch
    {
        ErrorPaperEmpty => new InvalidOperationException(ex.Message, ex).WithMessageKey("Edit.Scan.Error.NoPaper"),
        ErrorPaperJam => new InvalidOperationException(ex.Message, ex).WithMessageKey("Edit.Scan.Error.Jam"),
        ErrorOffline => new InvalidOperationException(ex.Message, ex).WithMessageKey("Edit.Scan.Error.Offline"),
        ErrorBusy => new InvalidOperationException(ex.Message, ex).WithMessageKey("Edit.Scan.Error.Busy"),
        _ => new InvalidOperationException(ex.Message, ex).WithMessageKey("Edit.Scan.Error.General"),
    };
}
