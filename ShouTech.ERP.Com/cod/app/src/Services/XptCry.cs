using System;
using System.IO;
using System.Security.Cryptography;

namespace ShouTech.App.Services;

/// <summary>
/// Internal encrypted container used for branch-to-branch data exchange and for
/// invoice design templates. Files produced here can only be opened by another
/// licensed installation of ShouTech ERP on the same trust boundary.
///
/// Binary layout (little-endian):
///   [0..3]   Magic "SHX1"
///   [4]      Format version (1)
///   [5]      Payload kind (0 = generic data, 1 = invoice template)
///   [6..17]  12-byte nonce
///   [18..33] 16-byte GCM authentication tag
///   [34..]   Ciphertext
///
/// The header bytes [0..17] are bound as additional authenticated data, so any
/// tampering with kind/version/nonce is detected at decrypt time.
///
/// Key management: a 256-bit master key is generated once per installation and
/// stored DPAPI-protected (CurrentUser scope). An external key provider can be
/// plugged in via <see cref="SetKeyProvider"/> to source the key from the
/// native security core (sec/) in hardened builds.
/// </summary>
public static class XptCry
{
    public const uint Magic = 0x31584853; // "SHX1"
    public const byte FormatVersion = 1;
    public const byte KindGenericData = 0;
    public const byte KindInvoiceTemplate = 1;
    public const byte KindDatasetIdentity = 2;

    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int HeaderLen = 4 + 1 + 1 + NonceLen;

    private static readonly object KeyLock = new();
    private static Func<byte[]>? _externalKeyProvider;
    private static byte[]? _cachedKey;

    /// <summary>Raised when a file fails integrity or authenticity checks.</summary>
    public sealed class XptCryException : Exception
    {
        public XptCryException(string message) : base(message) { }
        public XptCryException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Installs an external key provider (e.g. the native security core).
    /// Must be called once at application startup before any encrypt/decrypt.
    /// </summary>
    public static void SetKeyProvider(Func<byte[]> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (KeyLock)
        {
            _externalKeyProvider = provider;
            _cachedKey = null;
        }
    }

    /// <summary>Encrypts a payload into the internal container format.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, byte kind)
    {
        var key = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var tag = new byte[TagLen];
        var cipher = new byte[plain.Length];

        var header = BuildHeader(kind, nonce);

        using (var aes = new AesGcm(key, TagLen))
        {
            aes.Encrypt(nonce, plain, cipher, tag, header);
        }

        var output = new byte[HeaderLen + TagLen + cipher.Length];
        Buffer.BlockCopy(header, 0, output, 0, HeaderLen);
        Buffer.BlockCopy(tag, 0, output, HeaderLen, TagLen);
        Buffer.BlockCopy(cipher, 0, output, HeaderLen + TagLen, cipher.Length);
        return output;
    }

    /// <summary>Decrypts a container. Throws <see cref="XptCryException"/> on any tampering.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> container, byte expectedKind)
    {
        if (container.Length < HeaderLen + TagLen)
            throw new XptCryException("Container is too small to be valid.");

        if (BitConverter.ToUInt32(container.Slice(0, 4)) != Magic)
            throw new XptCryException("Container magic mismatch.");

        if (container[4] != FormatVersion)
            throw new XptCryException($"Unsupported container version {container[4]}.");

        if (container[5] != expectedKind)
            throw new XptCryException("Container kind mismatch.");

        var header = container.Slice(0, HeaderLen).ToArray();
        var nonce = container.Slice(6, NonceLen).ToArray();
        var tag = container.Slice(HeaderLen, TagLen).ToArray();
        var cipher = container.Slice(HeaderLen + TagLen).ToArray();

        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(GetKey(), TagLen);
            aes.Decrypt(nonce, cipher, tag, plain, header);
        }
        catch (CryptographicException ex)
        {
            throw new XptCryException("Container integrity check failed.", ex);
        }
        return plain;
    }

    /// <summary>Encrypts a payload and writes it to disk atomically.</summary>
    public static void EncryptToFile(string path, ReadOnlySpan<byte> plain, byte kind)
    {
        var container = Encrypt(plain, kind);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, container);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads and decrypts a container file.</summary>
    public static byte[] DecryptFromFile(string path, byte expectedKind)
        => Decrypt(File.ReadAllBytes(path), expectedKind);

    // ------------------------------------------------------------------
    // Key management
    // ------------------------------------------------------------------

    private static byte[] BuildHeader(byte kind, byte[] nonce)
    {
        var header = new byte[HeaderLen];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), Magic);
        header[4] = FormatVersion;
        header[5] = kind;
        Buffer.BlockCopy(nonce, 0, header, 6, NonceLen);
        return header;
    }

    private static byte[] GetKey()
    {
        lock (KeyLock)
        {
            if (_cachedKey != null)
                return _cachedKey;

            _cachedKey = _externalKeyProvider != null
                ? ValidateKey(_externalKeyProvider())
                : LoadOrCreateLocalKey();
            return _cachedKey;
        }
    }

    private static byte[] ValidateKey(byte[] key)
    {
        if (key.Length != 32)
            throw new XptCryException("External key provider must return a 256-bit key.");
        return key;
    }

    /// <summary>
    /// Local fallback: random 256-bit master key generated once per installation,
    /// persisted DPAPI-protected (CurrentUser). Replace by SetKeyProvider wiring
    /// to the native security core for hardened builds.
    /// </summary>
    private static byte[] LoadOrCreateLocalKey()
    {
        var path = KeyFilePath();
        try
        {
            if (File.Exists(path))
            {
                var protectedBytes = File.ReadAllBytes(path);
                return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            }
        }
        catch
        {
            // Fall through and regenerate; tampered key files are never trusted.
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        return key;
    }

    private static string KeyFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShouTech", "ERP", "sec", "xpt.key");
}
