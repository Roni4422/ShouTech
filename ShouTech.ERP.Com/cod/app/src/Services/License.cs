// ========================================================================
// FILE: Services/License.cs
// PROJECT: SHOUTECH ERP — License gate & module keys
// Merged: ILicServ + LicServ
// ========================================================================

#nullable enable
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace ShouTech.App.Services
{

    // ========== ILicServ.cs ==========
public enum LicEd : byte
    {
        Demo = 0,
        Viewer = 1,
        Core = 10,
        Professional = 15,
        Enterprise = 20
    }

    public enum LicStat : byte
    {
        Valid = 0,
        GracePeriod = 1,
        Expired = 2,
        Revoked = 3,
        HardwareMismatch = 4,
        InvalidSignature = 5,
        DeviceLimitExceeded = 6,
        NotFound = 7
    }

    public static class ModKey
    {
        public const string Acc = "ACC";
        public const string Inv = "INV";
        public const string Sales = "SALES";
        public const string Purch = "PURCH";
        public const string Hr = "HR";
        public const string Mfg = "MFG";
        public const string Pos = "POS";
        public const string Ai = "AI";
        public const string AdvRpt = "ADV_RPT";
        public const string MultiCur = "MULTI_CUR";
        public const string WhiteLabel = "WL";
    }

    public sealed class LicResult
    {
        public LicStat Status { get; init; }
        public LicEd Edition { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime? GraceEndsAt { get; init; }
        public int? MaxDevices { get; init; }
        public int? ActiveDevices { get; init; }
        public string? Message { get; init; }
        public IReadOnlySet<string> Modules { get; init; } = new HashSet<string>();

        public bool IsUsable => Status is LicStat.Valid or LicStat.GracePeriod;
        public bool IsViewer => Edition == LicEd.Viewer;
        public bool IsDemo => Edition == LicEd.Demo;
        public bool CanWrite => IsUsable && Edition != LicEd.Viewer;
    }

    public interface ILicServ
    {
        Task<LicResult> ValidateAsync(CancellationToken ct = default);
        Task<LicResult> RefreshAsync(CancellationToken ct = default);

        LicEd CurrentEdition { get; }
        bool CanWrite { get; }
        bool IsModuleEnabled(string moduleKey);

        Task EnsureUsableAsync(CancellationToken ct = default);
        Task EnsureCanWriteAsync(CancellationToken ct = default);
        Task EnsureModuleAsync(string moduleKey, CancellationToken ct = default);
    }

    // ========== LicServ.cs ==========
/// <summary>
    /// Enterprise license gate.
    /// Controls Viewer mode + dynamic modules visibility/access.
    /// Native dongle/signature validation will be plugged later via P/Invoke.
    /// </summary>
    public sealed class LicServ : ILicServ
    {
        private LicResult _last = new()
        {
            Status = LicStat.NotFound,
            Edition = LicEd.Demo,
            Message = "No license loaded"
        };

        private readonly HashSet<string> _coreModules = new(StringComparer.OrdinalIgnoreCase)
        {
            ModKey.Acc, ModKey.Inv, ModKey.Sales, ModKey.Purch
        };

        public LicEd CurrentEdition => _last.Edition;
        public bool CanWrite => _last.CanWrite;

        private static bool? _nativeAvailable;

        public async Task<LicResult> ValidateAsync(CancellationToken ct = default)
        {
            await Task.Yield();

            if (TryValidateNative(out var nativeResult))
            {
                _last = nativeResult;
                return _last;
            }

            var modules = new HashSet<string>(_coreModules, StringComparer.OrdinalIgnoreCase);

            _last = new LicResult
            {
                Status = LicStat.Valid,
                Edition = LicEd.Core,
                ExpiresAt = DateTime.UtcNow.AddYears(1),
                MaxDevices = 3,
                ActiveDevices = 1,
                Message = "License valid (Core — development mode, SecEng not loaded)",
                Modules = modules
            };

            return _last;
        }

        /// <summary>
        /// </summary>
        private static bool TryValidateNative(out LicResult result)
        {
            result = new LicResult
            {
                Status = LicStat.NotFound,
                Edition = LicEd.Demo,
                Message = "SecEng native engine not evaluated."
            };

            try
            {
                if (_nativeAvailable == false)
                    return false;

                var licensePath = Path.Combine(AppContext.BaseDirectory, "cfg", "license.dat");
                if (!File.Exists(licensePath))
                    licensePath = Path.Combine(AppContext.BaseDirectory, "license.dat");

                if (!File.Exists(licensePath))
                {
                    if (SecEngInterop.ST_IsInitialized())
                    {
                        _nativeAvailable = true;
                        result = new LicResult
                        {
                            Status = LicStat.NotFound,
                            Edition = LicEd.Demo,
                            Message = "License file not found."
                        };
                        return true;
                    }
                    return false;
                }

                var licenseData = File.ReadAllBytes(licensePath);
                var status = SecEngInterop.ST_InitializeSimple(
                    licenseData, checked((uint)licenseData.Length));

                _nativeAvailable = status == SecEngInterop.StatusOk
                                   || SecEngInterop.ST_IsInitialized();

                if (_nativeAvailable != true)
                    return false;

                if (!SecEngInterop.ST_IsLicenseValid())
                {
                    result = new LicResult
                    {
                        Status = LicStat.NotFound,
                        Edition = LicEd.Demo,
                        Message = "Native license validation failed (signature/HWID/dongle)."
                    };
                    return true;
                }

                result = new LicResult
                {
                    Status = LicStat.Valid,
                    Edition = LicEd.Enterprise,
                    ExpiresAt = DateTime.UtcNow.AddYears(1),
                    MaxDevices = 10,
                    ActiveDevices = 1,
                    Message = "License valid (Enterprise — SecEng verified)",
                    Modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ModKey.Acc, ModKey.Inv, ModKey.Sales, ModKey.Purch,
                        ModKey.Hr, ModKey.Mfg, ModKey.Pos, ModKey.Ai,
                        ModKey.AdvRpt, ModKey.MultiCur, ModKey.WhiteLabel
                    }
                };
                return true;
            }
            catch (DllNotFoundException) { _nativeAvailable = false; return false; }
            catch (EntryPointNotFoundException) { _nativeAvailable = false; return false; }
            catch (BadImageFormatException) { _nativeAvailable = false; return false; }
        }

        public async Task<LicResult> RefreshAsync(CancellationToken ct = default)
        {
            return await ValidateAsync(ct);
        }

        public bool IsModuleEnabled(string moduleKey)
        {
            if (string.IsNullOrWhiteSpace(moduleKey))
                return false;

            // Core modules are always allowed when license is usable and not pure Viewer
            if (_coreModules.Contains(moduleKey))
                return _last.IsUsable && _last.Edition != LicEd.Viewer;

            return _last.Modules.Contains(moduleKey);
        }

        public async Task EnsureUsableAsync(CancellationToken ct = default)
        {
            var r = await ValidateAsync(ct);
            if (!r.IsUsable)
                throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");
        }

        public async Task EnsureCanWriteAsync(CancellationToken ct = default)
        {
            var r = await ValidateAsync(ct);

            if (!r.IsUsable)
                throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");

            if (!r.CanWrite)
                throw new UnauthorizedAccessException("Read-only license (Viewer). Writing is not allowed.");
        }

        public async Task EnsureModuleAsync(string moduleKey, CancellationToken ct = default)
        {
            var r = await ValidateAsync(ct);

            if (!r.IsUsable)
                throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");

            if (!IsModuleEnabled(moduleKey))
                throw new UnauthorizedAccessException($"Module '{moduleKey}' is not licensed.");
        }
    }

}
