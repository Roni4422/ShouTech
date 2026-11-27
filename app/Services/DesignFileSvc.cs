using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShouTech.Domain;

namespace ShouTech.App.Services;

/// <summary>
/// Encrypted proprietary design files (services phase B0): report
/// designs (.strd), invoice designs (.stid) and barcode label designs
/// (.stld) export from their designer windows and import back ONLY
/// through this service.
///
/// Format: magic "STDF" + 1 kind byte + 12-byte random nonce +
/// AES-256-GCM ciphertext (tag appended by the algorithm). The GCM
/// authentication tag is the integrity check: a tampered, truncated
/// or foreign file fails decryption and is rejected with the
/// cataloged error DSG-8101; a valid file of another designer kind is
/// rejected with DSG-8102; a decrypted but unusable payload with
/// DSG-8103. The encryption key is the product key below (SHA-256) —
/// the standard model for template files in this product class: the
/// format is ShouTech's and files are readable only by the program.
/// </summary>
public static class DesignFileSvc
{
    public enum Kind : byte
    {
        Report = 1,
        Invoice = 2,
        Label = 3
    }

    public static string ExtensionFor(Kind kind) => kind switch
    {
        Kind.Report => ".strd",
        Kind.Invoice => ".stid",
        Kind.Label => ".stld",
        _ => ".strd"
    };

    public static string FilterFor(Kind kind) => kind switch
    {
        Kind.Report => "ShouTech report design (*.strd)|*.strd",
        Kind.Invoice => "ShouTech invoice design (*.stid)|*.stid",
        Kind.Label => "ShouTech label design (*.stld)|*.stld",
        _ => "ShouTech design (*.strd)|*.strd"
    };

    private static readonly byte[] Magic = "STDF"u8.ToArray();

    private static byte[] Key => SHA256.HashData(
        Encoding.UTF8.GetBytes("ShouTech-Design-File-Key-v1"));

    private sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    /// <summary>Encrypts and writes a design file. Reports failures
    /// through the central error catalog; returns false on failure.</summary>
    public static bool Export(string path, Kind kind, string designName, string payloadJson)
    {
        try
        {
            var envelope = JsonSerializer.Serialize(new Envelope
            {
                Version = 1,
                Name = designName ?? string.Empty,
                Payload = payloadJson ?? string.Empty
            });
            var plain = Encoding.UTF8.GetBytes(envelope);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(nonce, plain, cipher, tag);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(Magic, 0, Magic.Length);
            fs.WriteByte((byte)kind);
            fs.Write(nonce, 0, nonce.Length);
            fs.Write(cipher, 0, cipher.Length);
            fs.Write(tag, 0, tag.Length);
            return true;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DesignFileInvalid, ex, "DesignFileSvc.Export");
            return false;
        }
    }

    /// <summary>
    /// Reads and decrypts a design file of the expected kind. On any
    /// failure the cataloged error is reported and null is returned.
    /// On success, outputs the design's stored name and payload JSON.
    /// </summary>
    public static string? Import(string path, Kind expectedKind, out string designName)
    {
        designName = string.Empty;
        byte[] all;
        try
        {
            all = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DesignFileInvalid, ex, "DesignFileSvc.Import.Read");
            return null;
        }

        try
        {
            // Header: 4 magic + 1 kind + 12 nonce; trailer: 16 tag.
            if (all.Length < 4 + 1 + 12 + 16 ||
                !all.AsSpan(0, 4).SequenceEqual(Magic))
            {
                AppHost.Errors.Report(ErrorCodes.DesignFileInvalid,
                    new InvalidDataException("Bad design-file header."), "DesignFileSvc.Import.Header");
                return null;
            }

            var fileKind = (Kind)all[4];
            if (fileKind != expectedKind)
            {
                AppHost.Errors.Report(ErrorCodes.DesignFileKindMismatch,
                    new InvalidDataException($"Design file kind {fileKind} != {expectedKind}."),
                    "DesignFileSvc.Import.Kind");
                return null;
            }

            var nonce = all.AsSpan(5, 12).ToArray();
            var cipher = all.AsSpan(17, all.Length - 17 - 16).ToArray();
            var tag = all.AsSpan(all.Length - 16, 16).ToArray();
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);

            var envelope = JsonSerializer.Deserialize<Envelope>(
                Encoding.UTF8.GetString(plain));
            if (envelope == null || envelope.Version != 1 ||
                string.IsNullOrWhiteSpace(envelope.Payload))
            {
                AppHost.Errors.Report(ErrorCodes.DesignPayloadInvalid,
                    new InvalidDataException("Design payload missing or unsupported version."),
                    "DesignFileSvc.Import.Payload");
                return null;
            }

            designName = envelope.Name ?? string.Empty;
            return envelope.Payload;
        }
        catch (CryptographicException ex)
        {
            // GCM tag mismatch: tampered or foreign file.
            AppHost.Errors.Report(ErrorCodes.DesignFileInvalid, ex, "DesignFileSvc.Import.Decrypt");
            return null;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DesignPayloadInvalid, ex, "DesignFileSvc.Import.Parse");
            return null;
        }
    }
}
