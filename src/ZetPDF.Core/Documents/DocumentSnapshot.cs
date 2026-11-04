using System.Security.Cryptography;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Documents;

/// <summary>
/// Niezmienny zrzut dokumentu: bajty PDF BEZ szyfrowania + ustawienia szyfrowania stosowane przy zapisie.
/// Dane trzymane w pamięci; historia Cofnij/Ponów może przenieść starsze zrzuty do pliku tymczasowego
/// (dla dokumentów zaszyfrowanych plik jest chroniony DPAPI – bieżący użytkownik).
/// <see cref="GetBytes"/> zwraca KOPIĘ – można ją swobodnie modyfikować.
/// </summary>
public sealed class DocumentSnapshot
{
    private static long _nextVersion;
    private readonly SnapshotData _data;

    private DocumentSnapshot(SnapshotData data, PdfEncryptionSettings? encryption)
    {
        _data = data;
        Encryption = encryption;
        Version = Interlocked.Increment(ref _nextVersion);
    }

    /// <summary>Tworzy zrzut. Tablica przechodzi na własność zrzutu – nie modyfikuj jej później.</summary>
    public static DocumentSnapshot FromBytes(byte[] pdf, PdfEncryptionSettings? encryption = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return new DocumentSnapshot(new SnapshotData(pdf), encryption);
    }

    /// <summary>Unikalny, rosnący numer wersji (każdy zrzut ma inny).</summary>
    public long Version { get; }

    public long Length => _data.Length;

    /// <summary>Szyfrowanie do zastosowania przy zapisie (null = zapis bez szyfrowania).</summary>
    public PdfEncryptionSettings? Encryption { get; }

    /// <summary>Kopia bajtów PDF (bez szyfrowania).</summary>
    public byte[] GetBytes() => _data.Read();

    public Stream OpenRead() => new MemoryStream(GetBytes(), writable: false);

    /// <summary>Nowy zrzut z innymi danymi i tym samym szyfrowaniem. Tablica przechodzi na własność zrzutu.</summary>
    public DocumentSnapshot WithData(byte[] pdf) => new(new SnapshotData(pdf), Encryption);

    /// <summary>Nowy zrzut z tymi samymi danymi (współdzielonymi) i innym szyfrowaniem.</summary>
    public DocumentSnapshot WithEncryption(PdfEncryptionSettings? encryption) => new(_data, encryption);

    internal bool IsInMemory => _data.IsInMemory;
    internal bool SharesDataWith(DocumentSnapshot other) => ReferenceEquals(_data, other._data);
    internal void SpillToDisk(string directory, bool protect) => _data.Spill(directory, protect);
    internal void Release() => _data.Release();

    private sealed class SnapshotData
    {
        private readonly object _gate = new();
        private byte[]? _memory;
        private string? _file;
        private bool _protected;

        public SnapshotData(byte[] memory)
        {
            _memory = memory;
            Length = memory.LongLength;
        }

        public long Length { get; }

        public bool IsInMemory { get { lock (_gate) return _memory is not null; } }

        public byte[] Read()
        {
            lock (_gate)
            {
                if (_memory is not null) return (byte[])_memory.Clone();
                if (_file is null) throw new ObjectDisposedException(nameof(DocumentSnapshot), "Snapshot data was released.");
                var bytes = File.ReadAllBytes(_file);
                return _protected ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
            }
        }

        public void Spill(string directory, bool protect)
        {
            lock (_gate)
            {
                if (_memory is null) return;
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"snap-{Guid.NewGuid():N}.bin");
                var payload = protect ? ProtectedData.Protect(_memory, null, DataProtectionScope.CurrentUser) : _memory;
                try { File.WriteAllBytes(path, payload); }
                catch
                {
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    throw;
                }
                _file = path;
                _protected = protect;
                _memory = null;
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                if (_file is not null)
                {
                    try { File.Delete(_file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    _file = null;
                }
                // Pamięć zwalnia GC; po Release dane z pliku są niedostępne.
            }
        }
    }
}
