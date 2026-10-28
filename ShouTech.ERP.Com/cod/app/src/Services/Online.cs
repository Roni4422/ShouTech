// ========================================================================
// FILE: Services/Online.cs
// PROJECT: SHOUTECH ERP — Connectivity, device, online license, sync, coordinator
// Merged: IConnSvc, IDevInfoSv, ILicenSvc, IOnlCoord, ISyncSvc,
//         ConnServ, DevInfoSv, LicenSvc, SyncServ, OnlCoord
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using ShouTech.App.Common;
using ShouTech.App.Configuration;

namespace ShouTech.App.Services
{

    // ========== IConnSvc.cs ==========
public enum ConnectivityState
    {
        Unknown,
        Offline,
        Online,
        ServerReachable
    }

    public interface IConnSvc : IDisposable
    {
        ConnectivityState State { get; }
        bool IsNetworkAvailable { get; }
        bool IsServerReachable { get; }
        DateTime LastCheckedUtc { get; }

        event EventHandler<ConnectivityState>? StateChanged;

        void StartMonitoring();
        void StopMonitoring();
        ConnectivityState CheckNow();
    }

    // ========== IDevInfoSv.cs ==========
public sealed class DeviceInfoSnapshot
    {
        public string DeviceId { get; set; } = string.Empty;
        public string HardwareId { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public int ProcessorCount { get; set; }
        public long WorkingSetMb { get; set; }
        public string Culture { get; set; } = string.Empty;
    }

    public interface IDevInfoSv
    {
        DeviceInfoSnapshot Current { get; }
        DeviceInfoSnapshot Collect();
        Task RegisterWithServerAsync();
    }

    public interface IDevInfoSvoSv : IDevInfoSv
    {
    }

    // ========== ILicenSvc.cs ==========
public enum LicenseStatus
    {
        Unknown,
        Valid,
        Expired,
        InvalidHardware,
        NotActivated,
        OfflineGrace
    }

    public sealed class LicenseInfo
    {
        public string LicenseKey { get; set; } = string.Empty;
        public string HardwareId { get; set; } = string.Empty;
        public DateTime ExpiryDate { get; set; } = DateTime.MaxValue;
        public DateTime? ActivatedAt { get; set; }
        public DateTime? LastOnlineCheck { get; set; }
        public LicenseStatus Status { get; set; } = LicenseStatus.Unknown;
        public bool IsOfflineMode { get; set; }
        public SupportGrant? SupportGrant { get; set; }
    }

    public sealed class SupportGrant
    {
        public string GrantId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string HardwareId { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
        public string Scope { get; set; } = "diagnostics";
        public string Signature { get; set; } = string.Empty;
    }

    public interface ILicenSvc
    {
        LicenseInfo Current { get; }
        bool IsValid { get; }
        bool AllowsOfflineOperation { get; }
        bool HasSupportAccess { get; }

        LicenseStatus ValidateOffline();
        bool ValidateSupportAccess();
        Task<LicenseStatus> TryOnlineValidationAsync();
        Task<LicenseStatus> InitializeAsync();
    }

    // ========== IOnlCoord.cs ==========
public sealed class SyncCycleResult
    {
        public bool Success { get; init; }
        public int ItemsSynced { get; init; }
        public int PendingRemaining { get; init; }
        public bool BackupUploaded { get; init; }
        public bool BackupCreated { get; init; }
        public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;
    }

    public interface IOnlCoord : IDisposable
    {
        bool IsRunning { get; }
        bool IsSyncing { get; }
        DateTime? LastSuccessfulSyncUtc { get; }
        int PendingSyncCount { get; }

        event EventHandler<SyncCycleResult>? SyncCycleCompleted;
        event EventHandler<bool>? SyncingChanged;

        void Start();
        void StopCoordinator();
        Task RunOnlineCycleAsync();
        Task InitializeOfflineAsync();
    }

    // ========== ISyncSvc.cs ==========
public enum SyncOperationType
    {
        UserProfile,
        UserPreferences,
        AuditLog,
        DeviceInfo
    }

    public sealed class SyncQueueItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public SyncOperationType Type { get; set; }
        public string PayloadPath { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public int Attempts { get; set; }
    }

    public interface ISyncServ
    {
        int PendingCount { get; }
        IReadOnlyList<SyncQueueItem> PendingItems { get; }

        void EnqueueUserSnapshot();
        Task<int> ProcessQueueAsync();
        Task SnapshotLocalUserDataAsync();
    }

    public interface ISyncSvc : ISyncServ
    {
    }

    // ========== ConnServ.cs ==========
public sealed class ConnServ : IConnSvc
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        private readonly ILogServ _logging;
        private readonly Timer _timer;
        private ConnectivityState _state = ConnectivityState.Unknown;
        private volatile bool _checking;

        public ConnServ(ILogServ logging)
        {
            _logging = logging;
            var interval = Math.Max(15, AppCfgSvc.Current.OnlineServices?.ConnectivityCheckIntervalSeconds ?? 60);
            _timer = new Timer(_ => _ = CheckAsync(), null, Timeout.Infinite, Timeout.Infinite);
            TimerIntervalMs = interval * 1000;
        }

        private int TimerIntervalMs { get; }

        public ConnectivityState State => _state;
        public bool IsNetworkAvailable => _state is ConnectivityState.Online or ConnectivityState.ServerReachable;
        public bool IsServerReachable => _state == ConnectivityState.ServerReachable;
        public DateTime LastCheckedUtc { get; private set; }

        public event EventHandler<ConnectivityState>? StateChanged;

        public void StartMonitoring()
        {
            _ = CheckAsync();
            _timer.Change(TimerIntervalMs, TimerIntervalMs);
        }

        public void StopMonitoring()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public ConnectivityState CheckNow()
        {
            CheckAsync().GetAwaiter().GetResult();
            return _state;
        }

        private async Task CheckAsync()
        {
            if (_checking)
                return;

            _checking = true;
            try
            {
                var next = await ProbeAsync().ConfigureAwait(false);
                LastCheckedUtc = DateTime.UtcNow;

                if (next == _state)
                    return;

                _state = next;
                _logging.LogInfo("Connectivity", $"State changed to {_state}");
                StateChanged?.Invoke(this, _state);
            }
            catch (Exception ex)
            {
                _logging.LogInfo("Connectivity", $"Check failed: {ex.Message}");
            }
            finally
            {
                _checking = false;
            }
        }

        private static async Task<ConnectivityState> ProbeAsync()
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
                return ConnectivityState.Offline;

            var activationUrl = AppCfgSvc.Current.Licensing?.Activation?.ActivationServer;
            var apiUrl = AppCfgSvc.Current.Integrations?.RestApi?.Endpoint;

            var target = !string.IsNullOrWhiteSpace(activationUrl) ? activationUrl : apiUrl;
            if (string.IsNullOrWhiteSpace(target))
                return ConnectivityState.Online;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, NormalizeUrl(target));
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                return response.IsSuccessStatusCode || (int)response.StatusCode < 500
                    ? ConnectivityState.ServerReachable
                    : ConnectivityState.Online;
            }
            catch (HttpRequestException)
            {
                return ConnectivityState.Online;
            }
            catch (TaskCanceledException)
            {
                return ConnectivityState.Online;
            }
        }

        private static string NormalizeUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.GetLeftPart(UriPartial.Authority);

            return url.TrimEnd('/');
        }

        public void Dispose() => _timer.Dispose();
    }

    // ========== DevInfoSv.cs ==========
public sealed class DevInfoSv : IDevInfoSv, IDevInfoSvoSv
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly ILogServ _logging;
        private readonly string _cachePath;
        private DeviceInfoSnapshot? _cached;

        public DevInfoSv(ILogServ logging)
        {
            _logging = logging;
            _cachePath = AppPaths.DeviceJson;
        }

        public DeviceInfoSnapshot Current => _cached ??= LoadOrCollect();

        public DeviceInfoSnapshot Collect()
        {
            var settings = AppCfgSvc.Current.Application;
            var machine = Environment.MachineName;
            var os = Environment.OSVersion.VersionString;
            var user = Environment.UserName;
            var hwid = ComputeHardwareId(machine, os);

            var snapshot = new DeviceInfoSnapshot
            {
                DeviceId = LoadOrCreateDeviceId(hwid),
                HardwareId = hwid,
                MachineName = machine,
                OsVersion = os,
                UserName = user,
                AppVersion = settings?.Version ?? "10.6.0",
                InstanceId = settings?.InstanceId ?? "ST-ERP-001",
                ProcessorCount = Environment.ProcessorCount,
                WorkingSetMb = Environment.WorkingSet / (1024 * 1024),
                Culture = System.Globalization.CultureInfo.CurrentUICulture.Name
            };

            SaveCache(snapshot);
            _cached = snapshot;
            return snapshot;
        }

        public async Task RegisterWithServerAsync()
        {
            var endpoint = AppCfgSvc.Current.OnlineServices?.DeviceRegisterEndpoint;
            var baseUrl = AppCfgSvc.Current.Licensing?.Activation?.ActivationServer;
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(baseUrl))
                return;

            var url = CombineUrl(baseUrl, endpoint);
            var payload = JsonSerializer.Serialize(Current, JsonDefs.Indented);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            try
            {
                using var response = await Http.PostAsync(url, content).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    _logging.LogInfo("DeviceInfo", "Device registered with server");
                else
                    _logging.LogInfo("DeviceInfo", $"Device registration returned {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                _logging.LogInfo("DeviceInfo", $"Device registration skipped: {ex.Message}");
            }
        }

        private DeviceInfoSnapshot LoadOrCollect()
        {
            try
            {
                if (File.Exists(_cachePath))
                {
                    var json = File.ReadAllText(_cachePath);
                    var loaded = JsonSerializer.Deserialize<DeviceInfoSnapshot>(json, JsonDefs.AppSetngRead);
                    if (loaded != null && !string.IsNullOrWhiteSpace(loaded.DeviceId))
                        return loaded;
                }
            }
            catch (Exception ex)
            {
                _logging.LogWarning("DeviceInfo", $"Cache read failed: {ex.Message}");
            }

            return Collect();
        }

        private void SaveCache(DeviceInfoSnapshot snapshot)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot, JsonDefs.Indented));
            }
            catch (Exception ex)
            {
                _logging.LogWarning("DeviceInfo", $"Cache write failed: {ex.Message}");
            }
        }

        private static string LoadOrCreateDeviceId(string hardwareId)
        {
            var idPath = AppPaths.DeviceId;

            try
            {
                if (File.Exists(idPath))
                {
                    var existing = File.ReadAllText(idPath).Trim();
                    if (!string.IsNullOrWhiteSpace(existing))
                        return existing;
                }

                var deviceId = $"ST-{hardwareId[..Math.Min(12, hardwareId.Length)].ToUpperInvariant()}";
                var dir = Path.GetDirectoryName(idPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(idPath, deviceId);
                return deviceId;
            }
            catch
            {
                return $"ST-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
            }
        }

        private static string ComputeHardwareId(string machine, string os)
        {
            var raw = $"{machine}|{os}|{Environment.UserDomainName}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            baseUrl = baseUrl.TrimEnd('/');
            path = path.TrimStart('/');
            return $"{baseUrl}/{path}";
        }
    }

    // ========== LicenSvc.cs ==========
public sealed class LicenSvc : ILicenSvc
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly ILogServ _logging;
        private readonly IDevInfoSv _deviceInfo;
        private readonly string _licensePath;

        public LicenSvc(ILogServ logging, IDevInfoSv deviceInfo)
        {
            _logging = logging;
            _deviceInfo = deviceInfo;
            _licensePath = AppPaths.LicenseLocal;
        }

        public LicenseInfo Current { get; private set; } = new();
        public bool IsValid => Current.Status is LicenseStatus.Valid or LicenseStatus.OfflineGrace;
        public bool AllowsOfflineOperation => AppCfgSvc.Current.Licensing?.Activation?.OfflineActivationAllowed ?? true;
        public bool HasSupportAccess => ValidateSupportAccess();

        public async Task<LicenseStatus> InitializeAsync()
        {
            Current = LoadLocal() ?? CreateFromConfig();
            Current.Status = ValidateOffline();
            SaveLocal(Current);
            await Task.CompletedTask.ConfigureAwait(false);
            return Current.Status;
        }

        public LicenseStatus ValidateOffline()
        {
            var device = _deviceInfo.Current;
            var activation = AppCfgSvc.Current.Licensing?.Activation;

            if (string.IsNullOrWhiteSpace(Current.LicenseKey))
            {
                Current.Status = AllowsOfflineOperation ? LicenseStatus.OfflineGrace : LicenseStatus.NotActivated;
                Current.IsOfflineMode = true;
                return Current.Status;
            }

            if (activation?.HardwareIdBased == true
                && !string.IsNullOrWhiteSpace(Current.HardwareId)
                && !string.Equals(Current.HardwareId, device.HardwareId, StringComparison.OrdinalIgnoreCase))
            {
                Current.Status = LicenseStatus.InvalidHardware;
                Current.IsOfflineMode = true;
                return Current.Status;
            }

            if (Current.ExpiryDate.Date < DateTime.Now.Date)
            {
                Current.Status = LicenseStatus.Expired;
                Current.IsOfflineMode = true;
                return Current.Status;
            }

            Current.Status = AllowsOfflineOperation ? LicenseStatus.OfflineGrace : LicenseStatus.Valid;
            Current.IsOfflineMode = true;
            return Current.Status;
        }

        public bool ValidateSupportAccess()
        {
            var grant = Current.SupportGrant;
            var device = _deviceInfo.Current;
            if (grant == null
                || string.IsNullOrWhiteSpace(grant.GrantId)
                || !string.Equals(grant.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(grant.HardwareId, device.HardwareId, StringComparison.OrdinalIgnoreCase)
                || grant.ExpiresAtUtc <= DateTime.UtcNow)
            {
                return false;
            }

            var publicKey = Environment.GetEnvironmentVariable("SHOUTECH_SUPPORT_PUBLIC_KEY");
            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(grant.Signature))
                return false;

            var signedContent = string.Join("|", grant.GrantId, grant.DeviceId, grant.HardwareId,
                grant.ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), grant.Scope);

            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(publicKey);
                return rsa.VerifyData(
                    Encoding.UTF8.GetBytes(signedContent),
                    Convert.FromBase64String(grant.Signature),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex)
            {
                _logging.LogWarning("Support", $"Support grant validation failed: {ex.Message}");
                return false;
            }
        }

        public async Task<LicenseStatus> TryOnlineValidationAsync()
        {
            var baseUrl = AppCfgSvc.Current.Licensing?.Activation?.ActivationServer;
            var endpoint = AppCfgSvc.Current.OnlineServices?.LicenseValidateEndpoint ?? "/license/validate";
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ValidateOffline();
                return Current.Status;
            }

            var url = $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";
            var payload = JsonSerializer.Serialize(new
            {
                licenseKey = Current.LicenseKey,
                hardwareId = _deviceInfo.Current.HardwareId,
                deviceId = _deviceInfo.Current.DeviceId,
                appVersion = _deviceInfo.Current.AppVersion
            }, JsonDefs.Indented);

            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(url, content).ConfigureAwait(false);
                Current.LastOnlineCheck = DateTime.UtcNow;

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    ApplyServerResponse(body);
                    Current.Status = LicenseStatus.Valid;
                    Current.IsOfflineMode = false;
                    SaveLocal(Current);
                    _logging.LogInfo("License", "Online validation succeeded");
                    return Current.Status;
                }

                _logging.LogInfo("License", $"Online validation returned {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                _logging.LogInfo("License", $"Online validation skipped: {ex.Message}");
            }

            return ValidateOffline();
        }

        private void ApplyServerResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("expiryDate", out var expiry)
                    && DateTime.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                {
                    Current.ExpiryDate = dt;
                }

                if (root.TryGetProperty("licenseKey", out var key))
                    Current.LicenseKey = key.GetString() ?? Current.LicenseKey;

                if (root.TryGetProperty("supportGrant", out var grant)
                    && grant.ValueKind == JsonValueKind.Object)
                {
                    Current.SupportGrant = grant.Deserialize<SupportGrant>(JsonDefs.AppSetngRead);
                }
            }
            catch
            {
                // Keep local license if response is not parseable
            }
        }

        private LicenseInfo? LoadLocal()
        {
            try
            {
                if (!File.Exists(_licensePath))
                    return null;

                var json = File.ReadAllText(_licensePath);
                return JsonSerializer.Deserialize<LicenseInfo>(json, JsonDefs.AppSetngRead);
            }
            catch (Exception ex)
            {
                _logging.LogWarning("License", $"Local license load failed: {ex.Message}");
                return null;
            }
        }

        private LicenseInfo CreateFromConfig()
        {
            var licensing = AppCfgSvc.Current.Licensing;
            DateTime expiry = DateTime.MaxValue;
            if (!string.IsNullOrWhiteSpace(licensing?.ExpiryDate)
                && DateTime.TryParse(licensing.ExpiryDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                expiry = parsed;
            }

            return new LicenseInfo
            {
                LicenseKey = licensing?.DongleInfo?.SerialNumber ?? "LOCAL-DEV",
                HardwareId = _deviceInfo.Current.HardwareId,
                ExpiryDate = expiry,
                ActivatedAt = DateTime.UtcNow,
                Status = LicenseStatus.Unknown,
                IsOfflineMode = true
            };
        }

        private void SaveLocal(LicenseInfo info)
        {
            try
            {
                var dir = Path.GetDirectoryName(_licensePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_licensePath, JsonSerializer.Serialize(info, JsonDefs.Indented));
            }
            catch (Exception ex)
            {
                _logging.LogWarning("License", $"Local license save failed: {ex.Message}");
            }
        }
    }

    // ========== SyncServ.cs ==========
public sealed class SyncServ : ISyncServ, ISyncSvc
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private readonly ILogServ _logging;
        private readonly IDevInfoSv _deviceInfo;
        private readonly string _queuePath;
        private readonly string _snapshotDir;
        private List<SyncQueueItem> _queue = new();

        public SyncServ(ILogServ logging, IDevInfoSv deviceInfo)
        {
            _logging = logging;
            _deviceInfo = deviceInfo;

            var root = AppPaths.Sync;

            _queuePath = Path.Combine(root, "queue.json");
            _snapshotDir = Path.Combine(root, "snapshots");
            LoadQueue();
        }

        public int PendingCount => _queue.Count;
        public IReadOnlyList<SyncQueueItem> PendingItems => _queue;

        public async Task SnapshotLocalUserDataAsync()
        {
            if (!Directory.Exists(_snapshotDir))
                Directory.CreateDirectory(_snapshotDir);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var snapshotPath = Path.Combine(_snapshotDir, $"users_{stamp}.json");

            var userDataRoot = AppPaths.Root;

            var snapshot = new
            {
                capturedAt = DateTime.UtcNow,
                device = _deviceInfo.Current,
                files = CollectExistingUserFiles(userDataRoot)
            };

            await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(snapshot, JsonDefs.Indented))
                .ConfigureAwait(false);

            _queue.Add(new SyncQueueItem
            {
                Type = SyncOperationType.UserProfile,
                PayloadPath = snapshotPath
            });

            SaveQueue();
            _logging.LogInfo("Sync", $"User snapshot queued: {snapshotPath}");
        }

        public void EnqueueUserSnapshot()
        {
            _ = SnapshotLocalUserDataAsync();
        }

        public async Task<int> ProcessQueueAsync()
        {
            if (_queue.Count == 0)
                return 0;

            var baseUrl = ResolveSyncBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl))
                return 0;

            var endpoint = AppCfgSvc.Current.OnlineServices?.SyncEndpoint ?? "/sync";
            var url = $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";
            var processed = 0;

            foreach (var item in _queue.ToList())
            {
                try
                {
                    if (!File.Exists(item.PayloadPath))
                    {
                        _queue.Remove(item);
                        continue;
                    }

                    var payload = await File.ReadAllTextAsync(item.PayloadPath).ConfigureAwait(false);
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var response = await Http.PostAsync(url, content).ConfigureAwait(false);

                    item.Attempts++;
                    if (response.IsSuccessStatusCode)
                    {
                        _queue.Remove(item);
                        processed++;
                        _logging.LogInfo("Sync", $"Uploaded sync item {item.Id}");
                    }
                    else
                    {
                        _logging.LogWarning("Sync", $"Sync item {item.Id} failed with {(int)response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    item.Attempts++;
                    _logging.LogWarning("Sync", $"Sync item {item.Id} error: {ex.Message}");
                }
            }

            SaveQueue();
            return processed;
        }

        private static string? ResolveSyncBaseUrl()
        {
            var activation = AppCfgSvc.Current.Licensing?.Activation?.ActivationServer;
            if (!string.IsNullOrWhiteSpace(activation))
                return activation;

            return AppCfgSvc.Current.Integrations?.RestApi?.Endpoint;
        }

        private static Dictionary<string, string> CollectExistingUserFiles(string root)
        {
            var result = new Dictionary<string, string>();
            var candidates = new[]
            {
                Path.Combine(root, "cfg", "user.dat"),
                Path.Combine(root, "userprefs.json"),
                Path.Combine(root, "cfg", "license.local.json"),
                Path.Combine(root, "cfg", "device.json")
            };

            foreach (var file in candidates.Where(File.Exists))
            {
                try
                {
                    result[Path.GetFileName(file)] = File.ReadAllText(file);
                }
                catch
                {
                    // Skip unreadable files
                }
            }

            return result;
        }

        private void LoadQueue()
        {
            try
            {
                if (!File.Exists(_queuePath))
                    return;

                var json = File.ReadAllText(_queuePath);
                _queue = JsonSerializer.Deserialize<List<SyncQueueItem>>(json, JsonDefs.AppSetngRead) ?? new();
            }
            catch (Exception ex)
            {
                _logging.LogWarning("Sync", $"Queue load failed: {ex.Message}");
                _queue = new();
            }
        }

        private void SaveQueue()
        {
            try
            {
                var dir = Path.GetDirectoryName(_queuePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_queuePath, JsonSerializer.Serialize(_queue, JsonDefs.Indented));
            }
            catch (Exception ex)
            {
                _logging.LogWarning("Sync", $"Queue save failed: {ex.Message}");
            }
        }
    }

    // ========== OnlCoord.cs ==========
/// <summary>
    /// Mandatory hourly backup/sync when online. Startup backup when internet is available.
    /// </summary>
    public sealed class OnlCoord : IOnlCoord
    {
        private readonly IConnSvc _connectivity;
        private readonly ILicenSvc _license;
        private readonly IBkpServ _backup;
        private readonly ISyncServ _sync;
        private readonly IDevInfoSv _deviceInfo;
        private readonly ILogServ _logging;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Timer _backgroundSyncTimer;
        private bool _running;
        private bool _isSyncing;
        private bool _startupBackupPending = true;

        public OnlCoord(
            IConnSvc connectivity,
            ILicenSvc license,
            IBkpServ backup,
            ISyncServ sync,
            IDevInfoSv deviceInfo,
            ILogServ logging)
        {
            _connectivity = connectivity;
            _license = license;
            _backup = backup;
            _sync = sync;
            _deviceInfo = deviceInfo;
            _logging = logging;
            _connectivity.StateChanged += OnConnectivityChanged;

            var settings = AppCfgSvc.Current.OnlineServices;
            var intervalSeconds = Math.Max(300, settings?.BackgroundSyncIntervalSeconds ?? 3600);
            _backgroundSyncTimer = new Timer(_ => _ = RunOnlineCycleAsync(), null, Timeout.Infinite, Timeout.Infinite);
            BackgroundSyncIntervalMs = intervalSeconds * 1000;
        }

        private int BackgroundSyncIntervalMs { get; }

        public bool IsRunning => _running;
        public bool IsSyncing => _isSyncing;
        public DateTime? LastSuccessfulSyncUtc { get; private set; }
        public int PendingSyncCount => _sync.PendingCount;

        public event EventHandler<SyncCycleResult>? SyncCycleCompleted;
        public event EventHandler<bool>? SyncingChanged;

        public async Task InitializeOfflineAsync()
        {
            _deviceInfo.Collect();
            await _license.InitializeAsync().ConfigureAwait(false);
            await _sync.SnapshotLocalUserDataAsync().ConfigureAwait(false);
            _logging.LogInfo("OnlineCoordinator", $"Offline init — license: {_license.Current.Status}");
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;
            _connectivity.StartMonitoring();
            _backgroundSyncTimer.Change(BackgroundSyncIntervalMs, BackgroundSyncIntervalMs);
            _logging.LogInfo("OnlineCoordinator", $"Background backup/sync started (every {BackgroundSyncIntervalMs / 1000}s)");

            _ = RunOnlineCycleAsync(forceStartupBackup: true);
        }

        public void StopCoordinator()
        {
            if (!_running)
                return;

            _running = false;
            _backgroundSyncTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _connectivity.StopMonitoring();
        }

        public Task RunOnlineCycleAsync() => RunOnlineCycleAsync(forceStartupBackup: false);

        private async Task RunOnlineCycleAsync(bool forceStartupBackup)
        {
            if (!_running)
                return;

            if (!await _gate.WaitAsync(0).ConfigureAwait(false))
                return;

            SetSyncing(true);
            SyncCycleResult result = new() { Success = false, PendingRemaining = _sync.PendingCount };

            try
            {
                if (!_connectivity.IsNetworkAvailable)
                    return;

                _logging.LogInfo("OnlineCoordinator", "Online cycle started");

                var settings = AppCfgSvc.Current.OnlineServices;
                var canBackUpCurrentCompany =
                    !string.IsNullOrWhiteSpace(AppState.CurrentCompanyCode) &&
                    !string.IsNullOrWhiteSpace(DatabaseRuntime.GetConnectionString());
                var forceBackup = canBackUpCurrentCompany &&
                                  (forceStartupBackup
                                   || (_startupBackupPending && settings?.BackupOnStartup != false)
                                   || _backup.IsBackupDue());

                if (forceStartupBackup || _startupBackupPending)
                    _startupBackupPending = false;

                if (!canBackUpCurrentCompany && (forceStartupBackup || _backup.IsBackupDue()))
                    _logging.LogInfo(
                        "OnlineCoordinator",
                        "Backup skipped because no company database is currently selected.");

                BackupResult? backupResult = null;
                if (forceBackup && _connectivity.IsServerReachable)
                {
                    backupResult = await _backup.RunOnlineBackupCycleAsync(force: true).ConfigureAwait(false);
                    _logging.LogInfo("OnlineCoordinator",
                        backupResult.Success
                            ? $"Startup/hourly backup: {backupResult.LocalPath}"
                            : "Backup cycle failed");
                }
                else if (forceBackup)
                {
                    backupResult = _backup.PerformRotatedBackup();
                    await _sync.SnapshotLocalUserDataAsync().ConfigureAwait(false);
                }

                if (_connectivity.IsServerReachable)
                {
                    await _license.TryOnlineValidationAsync().ConfigureAwait(false);
                    await _deviceInfo.RegisterWithServerAsync().ConfigureAwait(false);
                }

                await _sync.SnapshotLocalUserDataAsync().ConfigureAwait(false);
                var synced = _connectivity.IsServerReachable
                    ? await _sync.ProcessQueueAsync().ConfigureAwait(false)
                    : 0;

                var uploaded = backupResult?.Uploaded == true;
                if (!uploaded && _connectivity.IsServerReachable && _backup.IsBackupDue())
                    uploaded = await _backup.TryUploadRotatedBackupsAsync().ConfigureAwait(false);

                LastSuccessfulSyncUtc = DateTime.UtcNow;
                result = new SyncCycleResult
                {
                    Success = backupResult?.Success != false,
                    ItemsSynced = synced,
                    PendingRemaining = _sync.PendingCount,
                    BackupUploaded = uploaded,
                    BackupCreated = backupResult?.Success == true
                };
            }
            catch (Exception ex)
            {
                _logging.LogError("OnlineCoordinator", "Online cycle failed", ex);
                result = new SyncCycleResult { Success = false, PendingRemaining = _sync.PendingCount };
            }
            finally
            {
                SetSyncing(false);
                _gate.Release();
                SyncCycleCompleted?.Invoke(this, result);
            }
        }

        private void SetSyncing(bool value)
        {
            if (_isSyncing == value)
                return;

            _isSyncing = value;
            SyncingChanged?.Invoke(this, value);
        }

        private void OnConnectivityChanged(object? sender, ConnectivityState state)
        {
            if (!_running)
                return;

            if (state is ConnectivityState.Online or ConnectivityState.ServerReachable)
                _ = RunOnlineCycleAsync();
        }

        public void Dispose()
        {
            StopCoordinator();
            _connectivity.StateChanged -= OnConnectivityChanged;
            _connectivity.Dispose();
            _backgroundSyncTimer.Dispose();
            _gate.Dispose();
        }
    }

}
