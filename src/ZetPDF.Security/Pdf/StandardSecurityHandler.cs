using System.Security.Cryptography;
using System.Text;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.Pdf;

/// <summary>
/// Standardowy handler zabezpieczeń PDF (ISO 32000-1 7.6.3 i ISO 32000-2 / AES-256): uwierzytelnienie hasła
/// użytkownika lub właściciela, klucz pliku, (de)szyfrowanie napisów i strumieni. Obsługa: RC4 40/128 (R2–R4),
/// AES-128 (AESV2, R4), AES-256 (AESV3, R5 i R6). Używane przez zapis przyrostowy podpisów i analizę zabezpieczeń.
/// </summary>
internal sealed class StandardSecurityHandler
{
    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private enum Cipher { None, Rc4, AesV2, AesV3 }

    private readonly Cipher _stringCipher;
    private readonly Cipher _streamCipher;
    private readonly byte[] _o, _u, _oe, _ue;
    private readonly byte[] _id0;
    private readonly int _keyLength; // bajty

    private StandardSecurityHandler(CosDictionary encrypt, byte[] id0)
    {
        V = (encrypt["V"] as CosNumber)?.AsInt ?? 0;
        R = (encrypt["R"] as CosNumber)?.AsInt ?? 0;
        P = (int)((encrypt["P"] as CosNumber)?.AsLong ?? 0);
        _o = (encrypt["O"] as CosString)?.Bytes ?? Array.Empty<byte>();
        _u = (encrypt["U"] as CosString)?.Bytes ?? Array.Empty<byte>();
        _oe = (encrypt["OE"] as CosString)?.Bytes ?? Array.Empty<byte>();
        _ue = (encrypt["UE"] as CosString)?.Bytes ?? Array.Empty<byte>();
        EncryptMetadata = encrypt["EncryptMetadata"] is not CosBoolean { Value: false };
        _id0 = id0;
        int lengthBits = (encrypt["Length"] as CosNumber)?.AsInt ?? 40;
        _keyLength = V switch { 1 => 5, 5 => 32, _ => Math.Clamp(lengthBits / 8, 5, 16) };
        if (R == 2) _keyLength = 5;

        if (V >= 4)
        {
            var cf = encrypt["CF"] as CosDictionary;
            _stringCipher = CipherFor(cf, encrypt.GetName("StrF"));
            _streamCipher = CipherFor(cf, encrypt.GetName("StmF"));
            if (V == 4 && (_stringCipher == Cipher.AesV2 || _streamCipher == Cipher.AesV2)) _keyLength = 16;
        }
        else
        {
            _stringCipher = _streamCipher = Cipher.Rc4;
        }
        Method = V switch
        {
            5 => PdfEncryptionMethod.Aes256,
            4 => _streamCipher == Cipher.AesV2 || _stringCipher == Cipher.AesV2 ? PdfEncryptionMethod.Aes128 : PdfEncryptionMethod.Rc4_128,
            2 or 3 => _keyLength > 5 ? PdfEncryptionMethod.Rc4_128 : PdfEncryptionMethod.Rc4_40,
            1 => PdfEncryptionMethod.Rc4_40,
            _ => PdfEncryptionMethod.Unknown,
        };
    }

    private static Cipher CipherFor(CosDictionary? cf, string? filterName)
    {
        if (filterName is null or "Identity") return Cipher.None;
        var f = cf?[filterName] as CosDictionary;
        return f?.GetName("CFM") switch
        {
            "AESV2" => Cipher.AesV2,
            "AESV3" => Cipher.AesV3,
            "V2" => Cipher.Rc4,
            "None" => Cipher.None,
            _ => Cipher.Rc4,
        };
    }

    public int V { get; }
    public int R { get; }
    public int P { get; }
    public bool EncryptMetadata { get; }
    public PdfEncryptionMethod Method { get; }

    /// <summary>Klucz pliku (po uwierzytelnieniu).</summary>
    public byte[]? FileKey { get; private set; }

    /// <summary>Czy uwierzytelniono hasłem właściciela.</summary>
    public bool IsOwner { get; private set; }

    /// <summary>Hasło użytkownika odzyskane z hasła właściciela (tylko R2–R4); null, gdy nieznane.</summary>
    public string? RecoveredUserPassword { get; private set; }

    /// <summary>Czy hasło użytkownika jest puste (plik otwiera się bez hasła).</summary>
    public bool UserPasswordEmpty { get; private set; }

    public DocumentPermissions Permissions => Core.Documents.PdfSecurityHelper.FromPValue(P);

    public static StandardSecurityHandler Create(CosDictionary encrypt, byte[] id0)
    {
        var filter = encrypt.GetName("Filter");
        if (filter != "Standard") throw new NotSupportedException($"Security handler '{filter}' is not supported.");
        return new StandardSecurityHandler(encrypt, id0);
    }

    /// <summary>Uwierzytelnia hasło (najpierw jako właściciela, potem jako użytkownika). Zwraca false, gdy hasło złe.</summary>
    public bool Authenticate(string? password)
    {
        password ??= string.Empty;
        UserPasswordEmpty = TryUser(string.Empty, out var emptyKey);
        if (TryOwner(password, out var key))
        {
            FileKey = key;
            IsOwner = true;
            return true;
        }
        if (password.Length == 0 && UserPasswordEmpty)
        {
            FileKey = emptyKey;
            return true;
        }
        if (TryUser(password, out key))
        {
            FileKey = key;
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ R2–R4

    /// <summary>
    /// Warianty kodowania hasła dla R2–R4 (norma: PDFDocEncoding; programy różnie traktują znaki spoza Latin-1:
    /// obcięcie do bajtu, '?' albo UTF-8) – próbujemy wszystkich.
    /// </summary>
    private static IEnumerable<byte[]> PasswordVariants(string password)
    {
        var seen = new List<byte[]>();
        foreach (var v in new[]
        {
            password.Select(c => (byte)c).ToArray(),
            password.Select(c => c < 256 ? (byte)c : (byte)'?').ToArray(),
            Encoding.UTF8.GetBytes(password),
        })
        {
            if (seen.Any(x => x.AsSpan().SequenceEqual(v))) continue;
            seen.Add(v);
            yield return v;
        }
    }

    private static byte[] PadPassword(byte[] password)
    {
        var raw = password.Take(32).ToArray();
        var padded = new byte[32];
        Array.Copy(raw, padded, raw.Length);
        Array.Copy(Padding, 0, padded, raw.Length, 32 - raw.Length);
        return padded;
    }

    private byte[] ComputeKeyR4(byte[] paddedUser)
    {
        using var md5 = MD5.Create();
        var buf = new List<byte>(paddedUser);
        buf.AddRange(_o.Take(32));
        buf.AddRange(BitConverter.GetBytes(P));
        buf.AddRange(_id0);
        if (R >= 4 && !EncryptMetadata) buf.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        var hash = md5.ComputeHash(buf.ToArray());
        if (R >= 3)
            for (int i = 0; i < 50; i++) hash = md5.ComputeHash(hash, 0, _keyLength);
        return hash.Take(_keyLength).ToArray();
    }

    private bool CheckUserKeyR4(byte[] key)
    {
        if (_u.Length < 16) return false;
        if (R == 2)
        {
            var u = Rc4(key, Padding);
            return u.AsSpan().SequenceEqual(_u.AsSpan(0, 32));
        }
        using var md5 = MD5.Create();
        var h = md5.ComputeHash(Padding.Concat(_id0).ToArray());
        var x = Rc4(key, h);
        for (int i = 1; i <= 19; i++) x = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), x);
        return x.AsSpan(0, 16).SequenceEqual(_u.AsSpan(0, 16));
    }

    private bool TryUser(string password, out byte[] key)
    {
        if (V == 5) return TryUserV5(password, out key);
        key = Array.Empty<byte>();
        foreach (var variant in PasswordVariants(password))
        {
            key = ComputeKeyR4(PadPassword(variant));
            if (CheckUserKeyR4(key)) return true;
        }
        return false;
    }

    private bool TryOwner(string password, out byte[] key)
    {
        if (V == 5) return TryOwnerV5(password, out key);
        key = Array.Empty<byte>();
        foreach (var variant in PasswordVariants(password))
            if (TryOwnerR4(variant, out key)) return true;
        return false;
    }

    private bool TryOwnerR4(byte[] password, out byte[] key)
    {
        using var md5 = MD5.Create();
        var h = md5.ComputeHash(PadPassword(password));
        if (R >= 3) for (int i = 0; i < 50; i++) h = md5.ComputeHash(h);
        var okey = h.Take(_keyLength).ToArray();
        byte[] userPadded;
        if (R == 2) userPadded = Rc4(okey, _o.Take(32).ToArray());
        else
        {
            userPadded = _o.Take(32).ToArray();
            for (int i = 19; i >= 0; i--) userPadded = Rc4(okey.Select(b => (byte)(b ^ i)).ToArray(), userPadded);
        }
        key = ComputeKeyR4(userPadded);
        if (!CheckUserKeyR4(key)) return false;
        RecoveredUserPassword = UnpadPassword(userPadded);
        return true;
    }

    private static string UnpadPassword(byte[] padded)
    {
        // hasło = bajty przed początkiem dopełnienia
        for (int len = 0; len <= 32; len++)
        {
            if (padded.AsSpan(len).SequenceEqual(Padding.AsSpan(0, 32 - len)))
                return new string(padded.Take(len).Select(b => (char)b).ToArray());
        }
        return new string(padded.Select(b => (char)b).ToArray());
    }

    // ------------------------------------------------------------------ R5/R6 (AES-256)

    private static byte[] Utf8Password(string password)
    {
        var b = Encoding.UTF8.GetBytes(password);
        return b.Length > 127 ? b.Take(127).ToArray() : b;
    }

    private bool TryUserV5(string password, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (_u.Length < 48) return false;
        var pwd = Utf8Password(password);
        var hash = Hash(pwd, _u.AsSpan(32, 8).ToArray(), Array.Empty<byte>());
        if (!hash.AsSpan(0, 32).SequenceEqual(_u.AsSpan(0, 32))) return false;
        var ik = Hash(pwd, _u.AsSpan(40, 8).ToArray(), Array.Empty<byte>());
        key = AesNoPadding(ik, _ue.Take(32).ToArray(), encrypt: false);
        return true;
    }

    private bool TryOwnerV5(string password, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (_o.Length < 48 || _u.Length < 48) return false;
        var pwd = Utf8Password(password);
        var u48 = _u.Take(48).ToArray();
        var hash = Hash(pwd, _o.AsSpan(32, 8).ToArray(), u48);
        if (!hash.AsSpan(0, 32).SequenceEqual(_o.AsSpan(0, 32))) return false;
        var ik = Hash(pwd, _o.AsSpan(40, 8).ToArray(), u48);
        key = AesNoPadding(ik, _oe.Take(32).ToArray(), encrypt: false);
        return true;
    }

    /// <summary>Algorytm 2.B (R6) lub SHA-256 (R5).</summary>
    private byte[] Hash(byte[] password, byte[] salt, byte[] userData)
    {
        var k = SHA256.HashData(password.Concat(salt).Concat(userData).ToArray());
        if (R < 6) return k;
        int i = 0;
        byte[] e = Array.Empty<byte>();
        while (i < 64 || e[^1] > i - 32)
        {
            var single = password.Concat(k).Concat(userData).ToArray();
            var k1 = new byte[single.Length * 64];
            for (int j = 0; j < 64; j++) Buffer.BlockCopy(single, 0, k1, j * single.Length, single.Length);
            using (var aes = Aes.Create())
            {
                aes.Key = k.AsSpan(0, 16).ToArray();
                e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            }
            int sum = 0;
            for (int j = 0; j < 16; j++) sum += e[j];
            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            i++;
        }
        return k.Take(32).ToArray();
    }

    private static byte[] AesNoPadding(byte[] key, byte[] data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = new byte[16];
        return encrypt ? aes.EncryptCbc(data, iv, PaddingMode.None) : aes.DecryptCbc(data, iv, PaddingMode.None);
    }

    // ------------------------------------------------------------------ szyfrowanie obiektów

    private byte[] ObjectKey(int num, int gen, Cipher cipher)
    {
        var key = FileKey ?? throw new InvalidOperationException("Not authenticated.");
        if (cipher == Cipher.AesV3) return key;
        var buf = new List<byte>(key)
        {
            (byte)num, (byte)(num >> 8), (byte)(num >> 16), (byte)gen, (byte)(gen >> 8),
        };
        if (cipher == Cipher.AesV2) buf.AddRange("sAlT"u8.ToArray());
        var h = MD5.HashData(buf.ToArray());
        return h.Take(Math.Min(key.Length + 5, 16)).ToArray();
    }

    public byte[] DecryptString(byte[] data, int num, int gen) => Decrypt(data, num, gen, _stringCipher);
    public byte[] DecryptStream(byte[] data, int num, int gen) => Decrypt(data, num, gen, _streamCipher);
    public byte[] EncryptString(byte[] data, int num, int gen) => Encrypt(data, num, gen, _stringCipher);
    public byte[] EncryptStream(byte[] data, int num, int gen) => Encrypt(data, num, gen, _streamCipher);

    private byte[] Decrypt(byte[] data, int num, int gen, Cipher cipher)
    {
        switch (cipher)
        {
            case Cipher.None: return data;
            case Cipher.Rc4: return Rc4(ObjectKey(num, gen, cipher), data);
            default:
                if (data.Length < 16) return Array.Empty<byte>();
                using (var aes = Aes.Create())
                {
                    aes.Key = ObjectKey(num, gen, cipher);
                    var iv = data.AsSpan(0, 16);
                    var body = data.AsSpan(16);
                    if (body.Length == 0) return Array.Empty<byte>();
                    if (body.Length % 16 != 0) body = body[..(body.Length - body.Length % 16)];
                    try { return aes.DecryptCbc(body, iv, PaddingMode.PKCS7); }
                    catch (CryptographicException) { return aes.DecryptCbc(body, iv, PaddingMode.None); }
                }
        }
    }

    private byte[] Encrypt(byte[] data, int num, int gen, Cipher cipher)
    {
        switch (cipher)
        {
            case Cipher.None: return data;
            case Cipher.Rc4: return Rc4(ObjectKey(num, gen, cipher), data);
            default:
                using (var aes = Aes.Create())
                {
                    aes.Key = ObjectKey(num, gen, cipher);
                    var iv = RandomNumberGenerator.GetBytes(16);
                    var body = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
                    var result = new byte[16 + body.Length];
                    iv.CopyTo(result, 0);
                    body.CopyTo(result, 16);
                    return result;
                }
        }
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var result = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            result[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return result;
    }
}
