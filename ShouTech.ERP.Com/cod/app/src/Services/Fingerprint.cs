// ========================================================================
// FILE: Services/Fingerprint.cs
// PROJECT: SHOUTECH ERP — Device fingerprint collector + matcher
//
// The comparison fingerprint of the telemetry study §4.4: hardware and
// OS components are read once, normalized, placeholder-filtered and
// hashed individually (SHA-256 over a domain-separated string), so
// raw serials never leave the machine and the vendor registry can
// compare hash-to-hash. The same fingerprint binds the signed license
// state locally (layer 1) and rides the layer-2 compliance report.
//
// Component sources are injectable (IDeviceComponentSource) so the
// matcher is provable against recorded component sets — VM templates,
// OS reinstalls, drive/NIC swaps — without real hardware.
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using ShouTech.Domain;

namespace ShouTech.App.Services
{
    /// <summary>Component type names of the fingerprint (wire-stable).</summary>
    public static class FpComponent
    {
        public const string MachineGuid = "MachineGuid";
        public const string BaseBoardSerial = "BaseBoardSerial";
        public const string BiosSerial = "BiosSerial";
        public const string DriveVolumeSerial = "DriveVolumeSerial";
        public const string NicMac = "NicMac";
        public const string MachineName = "MachineName";

        /// <summary>Strong components: two of these changing (or
        /// MachineGuid + BaseBoardSerial together) is a mismatch.</summary>
        public static readonly IReadOnlySet<string> Strong = new HashSet<string>(StringComparer.Ordinal)
        {
            MachineGuid, BaseBoardSerial
        };

        /// <summary>Components that take part in match scoring.
        /// MachineName is corroborating only (user-editable).</summary>
        public static readonly IReadOnlySet<string> Substantive = new HashSet<string>(StringComparer.Ordinal)
        {
            MachineGuid, BaseBoardSerial, BiosSerial, DriveVolumeSerial, NicMac
        };
    }

    /// <summary>One collected fingerprint: component type → hash hex.</summary>
    public sealed class DeviceFingerprint
    {
        public DeviceFingerprint(IReadOnlyDictionary<string, string> components)
        {
            Components = components;
            SubstantiveCount = components.Keys.Count(FpComponent.Substantive.Contains);
            // Study §4.4: fewer than 3 substantive components present →
            // the fingerprint is marked reduced-quality and wider
            // tolerance applies server-side.
            ReducedQuality = SubstantiveCount < 3;
        }

        public IReadOnlyDictionary<string, string> Components { get; }
        public int SubstantiveCount { get; }
        public bool ReducedQuality { get; }

        /// <summary>Short display form for the license surface: the
        /// first 12 hex chars of the MachineGuid component hash, or of
        /// the joined component hashes when no MachineGuid exists.
        /// A display aid only — never a matching input.</summary>
        public string ShortId
        {
            get
            {
                if (Components.TryGetValue(FpComponent.MachineGuid, out var guid) && guid.Length >= 12)
                    return guid[..12].ToUpperInvariant();
                var joined = string.Join("|", Components.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value));
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..12];
            }
        }
    }

    /// <summary>
    /// Raw component reader. Implementations return RAW values keyed by
    /// component type; normalization, placeholder filtering and hashing
    /// happen in <see cref="FingerprintSvc"/> so every source is judged
    /// by the same rules. A component that cannot be read is simply
    /// absent from the dictionary — never an invented value.
    /// </summary>
    public interface IDeviceComponentSource
    {
        IReadOnlyDictionary<string, string> ReadRawComponents();
    }

    /// <summary>Dictionary-backed source: recorded component sets for
    /// tests, harnesses and support reproduction.</summary>
    public sealed class RecordedComponentSource : IDeviceComponentSource
    {
        private readonly IReadOnlyDictionary<string, string> _raw;

        public RecordedComponentSource(IReadOnlyDictionary<string, string> raw) => _raw = raw;

        public IReadOnlyDictionary<string, string> ReadRawComponents() => _raw;
    }

    /// <summary>
    /// Windows component source: registry MachineGuid, WMI board/BIOS/
    /// drive serials, first active physical NIC MAC, machine name.
    /// Every read is individually guarded; a failure is reported once
    /// per collection (ONL-8504) and the component stays absent. On a
    /// non-Windows host every platform read fails the same way, which
    /// is exactly the reduced-quality path the study designs for.
    /// </summary>
    public sealed class WindowsComponentSource : IDeviceComponentSource
    {
        public IReadOnlyDictionary<string, string> ReadRawComponents()
        {
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            var failures = new List<string>();

            TryRead(failures, FpComponent.MachineGuid, () =>
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid")?.ToString();
            }, raw);

            TryRead(failures, FpComponent.BaseBoardSerial,
                () => WmiFirstValue("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"), raw);
            TryRead(failures, FpComponent.BiosSerial,
                () => WmiFirstValue("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"), raw);
            TryRead(failures, FpComponent.DriveVolumeSerial, () =>
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:";
                var driveId = root.TrimEnd('\\');
                return WmiFirstValue(
                    $"SELECT VolumeSerialNumber FROM Win32_LogicalDisk WHERE DeviceID = '{driveId}'",
                    "VolumeSerialNumber");
            }, raw);

            TryRead(failures, FpComponent.NicMac, () =>
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Unknown)
                        continue;
                    var mac = nic.GetPhysicalAddress();
                    if (mac is not null && mac.GetAddressBytes().Length == 6)
                        return mac.ToString();
                }
                return null;
            }, raw);

            TryRead(failures, FpComponent.MachineName, () => Environment.MachineName, raw);

            if (failures.Count > 0)
            {
                AppHost.Errors.Report(
                    ErrorCodes.FingerprintCollectionFailed,
                    new InvalidOperationException($"Fingerprint components unavailable: {string.Join(", ", failures)}"),
                    "Fingerprint.WindowsComponentSource");
            }

            return raw;
        }

        private static void TryRead(
            List<string> failures,
            string component,
            Func<string?> read,
            Dictionary<string, string> raw)
        {
            try
            {
                var value = read();
                if (!string.IsNullOrWhiteSpace(value))
                    raw[component] = value;
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException
                                       or ManagementException
                                       or System.Runtime.InteropServices.ExternalException
                                       or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                failures.Add(component);
            }
            catch (Exception ex)
            {
                failures.Add(component);
                AppHost.Errors.Report(ErrorCodes.FingerprintCollectionFailed, ex, $"Fingerprint.Read.{component}");
            }
        }

        private static string? WmiFirstValue(string query, string property)
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var value = item[property]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            return null;
        }
    }

    /// <summary>Collects, normalizes and hashes the device fingerprint.</summary>
    public sealed class FingerprintSvc
    {
        /// <summary>Domain separator of study §4.4 — keeps these hashes
        /// from being matched against generic serial-hash databases.</summary>
        public const string DomainSeparator = "SHOUTECH-FP-v1";

        private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
        {
            "",
            "None",
            "N/A",
            "0",
            "To be filled by O.E.M.",
            "To be filled by O.E.M",
            "Default string",
            "Not Applicable",
            "Not Specified",
            "00000000",
            "000000000000",
            "00000000-0000-0000-0000-000000000000",
            // The well-known Phoenix/AMI default UUID found on countless boards.
            "03000200-0400-0500-0006-000700080009",
        };

        private readonly IDeviceComponentSource _source;

        public FingerprintSvc(IDeviceComponentSource? source = null)
            => _source = source ?? new WindowsComponentSource();

        public DeviceFingerprint Collect()
        {
            var raw = _source.ReadRawComponents();
            var hashed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (type, value) in raw)
            {
                var normalized = Normalize(value);
                if (normalized is null)
                    continue; // placeholder / empty → component absent
                hashed[type] = HashComponent(type, normalized);
            }
            return new DeviceFingerprint(hashed);
        }

        /// <summary>Trim + upper-case; returns null for placeholders so
        /// a VM template's shared "serial" can never pose as identity.</summary>
        public static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var normalized = value.Trim().ToUpperInvariant();
            if (Placeholders.Contains(normalized))
                return null;
            // All-same-character strings ("00000000", "XXXX") carry no identity.
            if (normalized.Length >= 4 && normalized.All(c => c == normalized[0]))
                return null;
            return normalized;
        }

        public static string HashComponent(string type, string normalizedValue)
        {
            var bytes = Encoding.UTF8.GetBytes($"{DomainSeparator}|{type}|{normalizedValue}");
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }

    public enum FpMatchKind
    {
        Match,
        Drift,
        Mismatch
    }

    public sealed class FpMatchResult
    {
        public required FpMatchKind Kind { get; init; }
        public required IReadOnlyList<string> ChangedComponents { get; init; }
        /// <summary>Substantive components present in both sets — the
        /// evidence the verdict actually rests on.</summary>
        public required int ComparableCount { get; init; }
    }

    /// <summary>
    /// The study §4.4 scoring, shared by layer-1 license binding and
    /// the layer-2 report: all present substantive components equal →
    /// Match; exactly one substantive component changed → Drift (same
    /// device, the registry follows it); two or more substantive
    /// changed, or MachineGuid AND BaseBoardSerial both changed →
    /// Mismatch. Components absent on either side are not "changed";
    /// but if no substantive component is comparable at all, binding
    /// cannot be verified and the result is Mismatch (fail-closed for
    /// granting, per the licensing design §4.3).
    /// </summary>
    public static class FingerprintMatcher
    {
        public static FpMatchResult Evaluate(
            IReadOnlyDictionary<string, string> bound,
            IReadOnlyDictionary<string, string> current)
        {
            var changed = new List<string>();
            var comparable = 0;

            foreach (var type in FpComponent.Substantive)
            {
                if (!bound.TryGetValue(type, out var boundHash) || !current.TryGetValue(type, out var currentHash))
                    continue;
                comparable++;
                if (!string.Equals(boundHash, currentHash, StringComparison.OrdinalIgnoreCase))
                    changed.Add(type);
            }

            if (comparable == 0)
            {
                return new FpMatchResult
                {
                    Kind = FpMatchKind.Mismatch,
                    ChangedComponents = changed,
                    ComparableCount = 0
                };
            }

            var strongChanged = changed.Count(FpComponent.Strong.Contains);
            var kind = changed.Count == 0
                ? FpMatchKind.Match
                : changed.Count == 1
                    ? FpMatchKind.Drift
                    : FpMatchKind.Mismatch;
            if (strongChanged >= 2)
                kind = FpMatchKind.Mismatch;

            return new FpMatchResult
            {
                Kind = kind,
                ChangedComponents = changed,
                ComparableCount = comparable
            };
        }
    }
}
