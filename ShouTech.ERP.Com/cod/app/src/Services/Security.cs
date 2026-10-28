// ========================================================================
// FILE: Services/Security.cs
// PROJECT: SHOUTECH ERP — Native SecEng interop, STBK2 envelope, security service
// Merged: SecEngInterop, Stbk2Envelope, SecurityService
// ========================================================================

#nullable enable

using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System;

namespace ShouTech.App.Services
{

    // ========== SecEngInterop.cs ==========
/// <summary>
/// Native interop with SecEng.dll. Only this type may declare DllImport for SecEng.
/// </summary>
internal static class SecEngInterop
{
    private const string Dll = "SecEng";

    internal const uint StatusOk = 0x00000000;
    internal const uint StatusNotInitialized = 0x80040001;
    internal const uint StatusInvalidParameter = 0x80040003;
    internal const uint StatusCryptoAuthTagFailed = 0x8004004A;
    internal const uint StatusNotImplemented = 0x80040007;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CryptoParams
    {
        public uint CbSize;
        public uint Version;
        public IntPtr Key;
        public uint KeyLen;
        public IntPtr Iv;
        public uint IvLen;
        public IntPtr Aad;
        public uint AadLen;
        public uint Flags;
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_InitializeSimple(byte[]? licenseData, uint licenseDataLen);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_Shutdown();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ST_IsInitialized();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ST_IsLicenseValid();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_RandomBytes(byte[] buffer, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_HashSHA256(byte[] data, uint dataLen, byte[] hash32);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_HashSHA512(byte[] data, uint dataLen, byte[] hash64);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_HMAC_SHA256(byte[] key, uint keyLen, byte[] data, uint dataLen, byte[] mac32);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_DeriveKeyPBKDF2(
        byte[] password, uint passwordLen,
        byte[] salt, uint saltLen,
        uint iterations,
        byte[] key, uint keyLen);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_EncryptAES256GCM(
        ref CryptoParams parameters,
        byte[] plaintext, uint plaintextLen,
        byte[] ciphertext, uint ciphertextBufferSize, out uint ciphertextLen,
        byte[] tag16);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_DecryptAES256GCM(
        ref CryptoParams parameters,
        byte[] ciphertext, uint ciphertextLen,
        byte[] tag16,
        byte[] plaintext, uint plaintextBufferSize, out uint plaintextLen);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_ProtectData(
        byte[] plaintext, uint plaintextLen,
        byte[]? entropy, uint entropyLen,
        [MarshalAs(UnmanagedType.I1)] bool machineScope,
        byte[] protectedData, uint protectedBufferSize, out uint protectedLen);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint ST_UnprotectData(
        byte[] protectedData, uint protectedLen,
        byte[]? entropy, uint entropyLen,
        byte[] plaintext, uint plaintextBufferSize, out uint plaintextLen);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void ST_SecureZeroMemory(IntPtr buffer, UIntPtr size);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern uint ST_GetStatusMessage(uint status, char[] buffer, uint bufferSize, out uint requiredSize);
}

    // ========== Stbk2Envelope.cs ==========
/// <summary>
/// Managed STBK2 envelope matching SecEng internal design (magic TBK2, DEK/KEK, AES-256-GCM).
/// </summary>
internal static class Stbk2Envelope
{
    /// <summary>Little-endian 'TBK2' (0x324B4254).</summary>
    public const uint Magic = 0x324B4254;
    public const ushort FormatVersion = 2;
    public const ushort HeaderSize = 256;
    public const ushort AlgorithmAes256Gcm = 1;
    public const int KeyLen = 32;
    public const int NonceLen = 12;
    public const int TagLen = 16;

    // Fixed offsets inside the 256-byte header (pack=1, reserved trimmed to fit 256).
    private const int OffMagic = 0;
    private const int OffFormatVersion = 4;
    private const int OffHeaderSize = 6;
    private const int OffBackupId = 8;
    private const int OffCompanyId = 24;
    private const int OffProvider = 40;       // 32 bytes
    private const int OffCreatedAt = 72;      // u64
    private const int OffAlgorithm = 80;      // u16
    private const int OffReserved0 = 82;      // u16
    private const int OffKeyId = 84;          // 48 bytes
    private const int OffWrappedDek = 132;    // 48 bytes (32 used)
    private const int OffWrappedDekLen = 180; // u32
    private const int OffPayloadNonce = 184;  // 12
    private const int OffPayloadTag = 196;    // 16
    private const int OffMetadataLen = 212;   // u32
    private const int OffPayloadLen = 216;    // u64
    private const int OffReserved = 224;      // 32 bytes (wrap nonce 12 + tag 16 + pad)

    public sealed class HeaderInfo
    {
        public Guid BackupId { get; init; }
        public Guid CompanyId { get; init; }
        public string Provider { get; init; } = "local";
        public string KeyId { get; init; } = "local-backup";
        public DateTime CreatedAtUtc { get; init; }
        public uint MetadataLen { get; init; }
        public ulong PayloadLen { get; init; }
    }

    public static byte[] Seal(
        byte[] kek32,
        Guid companyId,
        string? provider,
        string? keyId,
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<byte> plaintextPayload)
    {
        if (kek32 is not { Length: KeyLen })
            throw new ArgumentException("KEK must be 32 bytes.", nameof(kek32));
        if (plaintextPayload.IsEmpty)
            throw new ArgumentException("Payload is empty.", nameof(plaintextPayload));

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(OffMagic), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(OffFormatVersion), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(OffHeaderSize), HeaderSize);

        var backupId = Guid.NewGuid();
        backupId.TryWriteBytes(header.AsSpan(OffBackupId, 16));
        companyId.TryWriteBytes(header.AsSpan(OffCompanyId, 16));

        WriteAsciiFixed(header.AsSpan(OffProvider, 32), string.IsNullOrWhiteSpace(provider) ? "local" : provider!);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(OffCreatedAt),
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(OffAlgorithm), AlgorithmAes256Gcm);
        WriteAsciiFixed(header.AsSpan(OffKeyId, 48), string.IsNullOrWhiteSpace(keyId) ? "local-backup" : keyId!);

        // Random DEK
        var dek = RandomNumberGenerator.GetBytes(KeyLen);
        var payloadNonce = RandomNumberGenerator.GetBytes(NonceLen);
        var payloadTag = new byte[TagLen];
        var ciphertext = new byte[plaintextPayload.Length];

        try
        {
            using (var aes = new AesGcm(dek, TagLen))
            {
                aes.Encrypt(
                    payloadNonce,
                    plaintextPayload,
                    ciphertext,
                    payloadTag,
                    metadata.IsEmpty ? null : metadata.ToArray());
            }

            // Wrap DEK with KEK (AES-GCM)
            var wrapNonce = RandomNumberGenerator.GetBytes(NonceLen);
            var wrapTag = new byte[TagLen];
            var wrappedDek = new byte[KeyLen];
            using (var aes = new AesGcm(kek32, TagLen))
            {
                aes.Encrypt(wrapNonce, dek, wrappedDek, wrapTag);
            }

            wrappedDek.CopyTo(header.AsSpan(OffWrappedDek, KeyLen));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(OffWrappedDekLen), KeyLen);
            payloadNonce.CopyTo(header.AsSpan(OffPayloadNonce, NonceLen));
            payloadTag.CopyTo(header.AsSpan(OffPayloadTag, TagLen));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(OffMetadataLen), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(OffPayloadLen), (ulong)ciphertext.Length);

            // reserved: wrap nonce (12) + wrap tag (16)
            wrapNonce.CopyTo(header.AsSpan(OffReserved, NonceLen));
            wrapTag.CopyTo(header.AsSpan(OffReserved + NonceLen, TagLen));

            CryptographicOperations.ZeroMemory(wrapNonce);
            CryptographicOperations.ZeroMemory(wrapTag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        var total = HeaderSize + metadata.Length + ciphertext.Length;
        var output = new byte[total];
        Buffer.BlockCopy(header, 0, output, 0, HeaderSize);
        if (metadata.Length > 0)
            metadata.CopyTo(output.AsSpan(HeaderSize));
        Buffer.BlockCopy(ciphertext, 0, output, HeaderSize + metadata.Length, ciphertext.Length);
        return output;
    }

    public static bool TryPeekHeader(ReadOnlySpan<byte> file, out HeaderInfo? info, out string? failure)
    {
        info = null;
        failure = null;
        if (file.Length < HeaderSize)
        {
            failure = "Backup file is too small for STBK2 header.";
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        if (magic != Magic)
        {
            failure = "Invalid backup identity (magic). File is not a ShouTech STBK2 company backup.";
            return false;
        }

        var ver = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(OffFormatVersion));
        if (ver != FormatVersion)
        {
            failure = $"Unsupported STBK2 format version: {ver}.";
            return false;
        }

        var hdrSize = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(OffHeaderSize));
        if (hdrSize != HeaderSize)
        {
            failure = "Unexpected STBK2 header size.";
            return false;
        }

        var algo = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(OffAlgorithm));
        if (algo != AlgorithmAes256Gcm)
        {
            failure = "Unsupported encryption algorithm in STBK2 header.";
            return false;
        }

        var metaLen = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(OffMetadataLen));
        var payloadLen = BinaryPrimitives.ReadUInt64LittleEndian(file.Slice(OffPayloadLen));
        if (HeaderSize + metaLen + (long)payloadLen > file.Length)
        {
            failure = "Backup file is truncated (metadata/payload length mismatch).";
            return false;
        }

        var backupId = new Guid(file.Slice(OffBackupId, 16));
        var companyId = new Guid(file.Slice(OffCompanyId, 16));
        var created = BinaryPrimitives.ReadUInt64LittleEndian(file.Slice(OffCreatedAt));
        var provider = ReadAsciiFixed(file.Slice(OffProvider, 32));
        var keyId = ReadAsciiFixed(file.Slice(OffKeyId, 48));

        info = new HeaderInfo
        {
            BackupId = backupId,
            CompanyId = companyId,
            Provider = provider,
            KeyId = keyId,
            CreatedAtUtc = DateTimeOffset.FromUnixTimeSeconds((long)created).UtcDateTime,
            MetadataLen = metaLen,
            PayloadLen = payloadLen
        };
        return true;
    }

    public static byte[] Open(
        byte[] kek32,
        ReadOnlySpan<byte> file,
        out byte[] metadata,
        out HeaderInfo headerInfo)
    {
        if (kek32 is not { Length: KeyLen })
            throw new ArgumentException("KEK must be 32 bytes.", nameof(kek32));

        if (!TryPeekHeader(file, out var info, out var fail) || info == null)
            throw new InvalidDataException(fail ?? "Invalid STBK2 header.");

        headerInfo = info;
        var metaLen = (int)info.MetadataLen;
        var payloadLen = (int)info.PayloadLen;
        metadata = metaLen == 0
            ? Array.Empty<byte>()
            : file.Slice(HeaderSize, metaLen).ToArray();

        var ciphertext = file.Slice(HeaderSize + metaLen, payloadLen);
        var payloadNonce = file.Slice(OffPayloadNonce, NonceLen).ToArray();
        var payloadTag = file.Slice(OffPayloadTag, TagLen).ToArray();
        var wrappedDek = file.Slice(OffWrappedDek, KeyLen).ToArray();
        var wrapNonce = file.Slice(OffReserved, NonceLen).ToArray();
        var wrapTag = file.Slice(OffReserved + NonceLen, TagLen).ToArray();

        var dek = new byte[KeyLen];
        var plaintext = new byte[payloadLen];
        try
        {
            using (var aes = new AesGcm(kek32, TagLen))
            {
                aes.Decrypt(wrapNonce, wrappedDek, wrapTag, dek);
            }

            using (var aes = new AesGcm(dek, TagLen))
            {
                aes.Decrypt(
                    payloadNonce,
                    ciphertext,
                    payloadTag,
                    plaintext,
                    metaLen == 0 ? null : metadata);
            }
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException(
                "STBK2 integrity verification failed. The file may be corrupted, tampered with, or encrypted with a different key.",
                ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(wrappedDek);
            CryptographicOperations.ZeroMemory(wrapNonce);
            CryptographicOperations.ZeroMemory(wrapTag);
            CryptographicOperations.ZeroMemory(payloadNonce);
            CryptographicOperations.ZeroMemory(payloadTag);
        }

        return plaintext;
    }

    private static void WriteAsciiFixed(Span<byte> dest, string value)
    {
        dest.Clear();
        var bytes = Encoding.ASCII.GetBytes(value);
        var n = Math.Min(bytes.Length, dest.Length - 1);
        bytes.AsSpan(0, n).CopyTo(dest);
    }

    private static string ReadAsciiFixed(ReadOnlySpan<byte> src)
    {
        var end = src.IndexOf((byte)0);
        if (end < 0) end = src.Length;
        return Encoding.ASCII.GetString(src[..end]);
    }
}

    // ========== SecurityService.cs ==========
public sealed class SecurityOperationResult
{
    public bool Success { get; init; }
    public uint StatusCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? DiagnosticId { get; init; }

    public static SecurityOperationResult Ok() => new()
    {
        Success = true,
        StatusCode = SecEngInterop.StatusOk,
        Message = "OK"
    };

    public static SecurityOperationResult Fail(uint code, string message, string? diagnosticId = null) => new()
    {
        Success = false,
        StatusCode = code,
        Message = message,
        DiagnosticId = diagnosticId
    };
}

/// <summary>
/// Central enterprise security facade for the WPF application layer.
/// </summary>
public interface ISecurityService
{
    bool IsNativeAvailable { get; }
    bool IsInitialized { get; }
    bool IsLicenseValid { get; }

    SecurityOperationResult Initialize(byte[]? licenseBlob = null);
    SecurityOperationResult Shutdown();

    byte[]? RandomBytes(int count);
    byte[]? Sha256(byte[] data);
    byte[]? EncryptAes256Gcm(byte[] key32, byte[] nonce12, byte[] plaintext, out byte[]? tag16, byte[]? aad = null);
    byte[]? DecryptAes256Gcm(byte[] key32, byte[] nonce12, byte[] ciphertext, byte[] tag16, byte[]? aad = null);
    byte[]? ProtectData(byte[] plaintext, byte[]? entropy = null, bool machineScope = false);
    byte[]? UnprotectData(byte[] protectedPayload, byte[]? entropy = null);
}

public sealed class SecurityService : ISecurityService
{
    private readonly object _gate = new();
    private bool _nativeLoaded;
    private bool _nativeProbeDone;

    public bool IsNativeAvailable
    {
        get
        {
            EnsureNativeProbed();
            return _nativeLoaded;
        }
    }

    public bool IsInitialized
    {
        get
        {
            if (!IsNativeAvailable)
                return false;
            try { return SecEngInterop.ST_IsInitialized(); }
            catch { return false; }
        }
    }

    public bool IsLicenseValid
    {
        get
        {
            if (!IsNativeAvailable)
                return false;
            try { return SecEngInterop.ST_IsLicenseValid(); }
            catch { return false; }
        }
    }

    public SecurityOperationResult Initialize(byte[]? licenseBlob = null)
    {
        if (!IsNativeAvailable)
            return SecurityOperationResult.Fail(
                SecEngInterop.StatusNotImplemented,
                "SecEng.dll is not available. Configure the signed native security engine for production.");

        lock (_gate)
        {
            try
            {
                var code = SecEngInterop.ST_InitializeSimple(licenseBlob, (uint)(licenseBlob?.Length ?? 0));
                return code == SecEngInterop.StatusOk
                    ? SecurityOperationResult.Ok()
                    : SecurityOperationResult.Fail(code, ResolveMessage(code), "seceng_init");
            }
            catch (Exception ex)
            {
                return SecurityOperationResult.Fail(
                    SecEngInterop.StatusNotInitialized,
                    "Failed to initialize security engine.",
                    ex.GetType().Name);
            }
        }
    }

    public SecurityOperationResult Shutdown()
    {
        if (!IsNativeAvailable)
            return SecurityOperationResult.Ok();

        lock (_gate)
        {
            try
            {
                var code = SecEngInterop.ST_Shutdown();
                return code == SecEngInterop.StatusOk
                    ? SecurityOperationResult.Ok()
                    : SecurityOperationResult.Fail(code, ResolveMessage(code));
            }
            catch
            {
                return SecurityOperationResult.Fail(SecEngInterop.StatusNotInitialized, "Shutdown failed.");
            }
        }
    }

    public byte[]? RandomBytes(int count)
    {
        if (count <= 0)
            return Array.Empty<byte>();

        if (IsNativeAvailable)
        {
            try
            {
                var buffer = new byte[count];
                if (SecEngInterop.ST_RandomBytes(buffer, (uint)count) == SecEngInterop.StatusOk)
                    return buffer;
            }
            catch { /* fall through to managed CSPRNG */ }
        }

        var managed = new byte[count];
        RandomNumberGenerator.Fill(managed);
        return managed;
    }

    public byte[]? Sha256(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (IsNativeAvailable)
        {
            try
            {
                var hash = new byte[32];
                if (SecEngInterop.ST_HashSHA256(data, (uint)data.Length, hash) == SecEngInterop.StatusOk)
                    return hash;
            }
            catch { /* managed fallback */ }
        }

        return SHA256.HashData(data);
    }

    public byte[]? EncryptAes256Gcm(byte[] key32, byte[] nonce12, byte[] plaintext, out byte[]? tag16, byte[]? aad = null)
    {
        tag16 = null;
        ValidateAesArgs(key32, nonce12);
        ArgumentNullException.ThrowIfNull(plaintext);

        if (!IsNativeAvailable)
            return null;

        var ciphertext = new byte[plaintext.Length];
        tag16 = new byte[16];
        var keyHandle = GCHandle.Alloc(key32, GCHandleType.Pinned);
        var ivHandle = GCHandle.Alloc(nonce12, GCHandleType.Pinned);
        GCHandle aadHandle = default;
        try
        {
            var parameters = new SecEngInterop.CryptoParams
            {
                CbSize = (uint)Marshal.SizeOf<SecEngInterop.CryptoParams>(),
                Version = 1,
                Key = keyHandle.AddrOfPinnedObject(),
                KeyLen = 32,
                Iv = ivHandle.AddrOfPinnedObject(),
                IvLen = 12,
                Flags = 0
            };
            if (aad is { Length: > 0 })
            {
                aadHandle = GCHandle.Alloc(aad, GCHandleType.Pinned);
                parameters.Aad = aadHandle.AddrOfPinnedObject();
                parameters.AadLen = (uint)aad.Length;
            }

            var code = SecEngInterop.ST_EncryptAES256GCM(
                ref parameters,
                plaintext, (uint)plaintext.Length,
                ciphertext, (uint)ciphertext.Length, out var ctLen,
                tag16);

            if (code != SecEngInterop.StatusOk)
            {
                tag16 = null;
                return null;
            }

            if (ctLen != ciphertext.Length)
                Array.Resize(ref ciphertext, (int)ctLen);
            return ciphertext;
        }
        finally
        {
            if (aadHandle.IsAllocated) aadHandle.Free();
            ivHandle.Free();
            keyHandle.Free();
        }
    }

    public byte[]? DecryptAes256Gcm(byte[] key32, byte[] nonce12, byte[] ciphertext, byte[] tag16, byte[]? aad = null)
    {
        ValidateAesArgs(key32, nonce12);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(tag16);
        if (tag16.Length != 16)
            throw new ArgumentException("GCM tag must be 16 bytes.", nameof(tag16));

        if (!IsNativeAvailable)
            return null;

        var plaintext = new byte[ciphertext.Length];
        var keyHandle = GCHandle.Alloc(key32, GCHandleType.Pinned);
        var ivHandle = GCHandle.Alloc(nonce12, GCHandleType.Pinned);
        GCHandle aadHandle = default;
        try
        {
            var parameters = new SecEngInterop.CryptoParams
            {
                CbSize = (uint)Marshal.SizeOf<SecEngInterop.CryptoParams>(),
                Version = 1,
                Key = keyHandle.AddrOfPinnedObject(),
                KeyLen = 32,
                Iv = ivHandle.AddrOfPinnedObject(),
                IvLen = 12
            };
            if (aad is { Length: > 0 })
            {
                aadHandle = GCHandle.Alloc(aad, GCHandleType.Pinned);
                parameters.Aad = aadHandle.AddrOfPinnedObject();
                parameters.AadLen = (uint)aad.Length;
            }

            var code = SecEngInterop.ST_DecryptAES256GCM(
                ref parameters,
                ciphertext, (uint)ciphertext.Length,
                tag16,
                plaintext, (uint)plaintext.Length, out var ptLen);

            if (code != SecEngInterop.StatusOk)
                return null;

            if (ptLen != plaintext.Length)
                Array.Resize(ref plaintext, (int)ptLen);
            return plaintext;
        }
        finally
        {
            if (aadHandle.IsAllocated) aadHandle.Free();
            ivHandle.Free();
            keyHandle.Free();
        }
    }

    public byte[]? ProtectData(byte[] plaintext, byte[]? entropy = null, bool machineScope = false)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!IsNativeAvailable)
            return ProtectedData.Protect(plaintext, entropy, machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);

        var buffer = new byte[plaintext.Length + 512];
        var code = SecEngInterop.ST_ProtectData(
            plaintext, (uint)plaintext.Length,
            entropy, (uint)(entropy?.Length ?? 0),
            machineScope,
            buffer, (uint)buffer.Length, out var len);

        if (code == SecEngInterop.StatusOk)
        {
            Array.Resize(ref buffer, (int)len);
            return buffer;
        }

        if (code == 0x80040004) // buffer_too_small
        {
            buffer = new byte[len];
            code = SecEngInterop.ST_ProtectData(
                plaintext, (uint)plaintext.Length,
                entropy, (uint)(entropy?.Length ?? 0),
                machineScope,
                buffer, (uint)buffer.Length, out len);
            if (code == SecEngInterop.StatusOk)
            {
                Array.Resize(ref buffer, (int)len);
                return buffer;
            }
        }

        return null;
    }

    public byte[]? UnprotectData(byte[] protectedPayload, byte[]? entropy = null)
    {
        ArgumentNullException.ThrowIfNull(protectedPayload);
        if (!IsNativeAvailable)
            return ProtectedData.Unprotect(protectedPayload, entropy, DataProtectionScope.CurrentUser);

        var buffer = new byte[protectedPayload.Length];
        var code = SecEngInterop.ST_UnprotectData(
            protectedPayload, (uint)protectedPayload.Length,
            entropy, (uint)(entropy?.Length ?? 0),
            buffer, (uint)buffer.Length, out var len);

        if (code != SecEngInterop.StatusOk)
            return null;

        Array.Resize(ref buffer, (int)len);
        return buffer;
    }

    private void EnsureNativeProbed()
    {
        if (_nativeProbeDone)
            return;

        lock (_gate)
        {
            if (_nativeProbeDone)
                return;
            try
            {
                _ = SecEngInterop.ST_IsInitialized();
                _nativeLoaded = true;
            }
            catch (DllNotFoundException)
            {
                _nativeLoaded = false;
            }
            catch (BadImageFormatException)
            {
                _nativeLoaded = false;
            }
            catch
            {
                // Other native failures still mean the DLL surface was found.
                _nativeLoaded = true;
            }
            _nativeProbeDone = true;
        }
    }

    private static void ValidateAesArgs(byte[] key32, byte[] nonce12)
    {
        ArgumentNullException.ThrowIfNull(key32);
        ArgumentNullException.ThrowIfNull(nonce12);
        if (key32.Length != 32)
            throw new ArgumentException("AES-256 key must be 32 bytes.", nameof(key32));
        if (nonce12.Length != 12)
            throw new ArgumentException("GCM nonce must be 12 bytes.", nameof(nonce12));
        if (key32.All(b => b == 0))
            throw new ArgumentException("AES key must not be all zeros.", nameof(key32));
    }

    private static string ResolveMessage(uint code)
    {
        try
        {
            var buffer = new char[256];
            var st = SecEngInterop.ST_GetStatusMessage(code, buffer, (uint)buffer.Length, out _);
            if (st == SecEngInterop.StatusOk)
                return new string(buffer).TrimEnd('\0');
        }
        catch { /* ignore */ }
        return $"Security status 0x{code:X8}";
    }
}

}
