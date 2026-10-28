using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ShouTech.App.Configuration;

namespace ShouTech.App.Services
{
    public interface IDataService
    {
        Task<int> GetTotalCustomersAsync();
        Task<int> GetTotalProductsAsync();
        Task<int> GetTotalPendingApprovalsAsync();
        Task<List<RecentDocument>> GetRecentDocumentsAsync(int count);
    }

    public interface IDataServ : IDataService { }

    public interface ICompanyRegistryService
    {
        Task<List<CompanyInfo>> GetCompaniesAsync();
        Task<bool> HasCompaniesAsync();
    }

    public interface ICoRegSvc : ICompanyRegistryService { }

    public interface IPermissionService
    {
        bool HasPermission(string permissionKey);
    }

	
	public sealed class AccentInfo
	{
		public string Key { get; init; } = "";
		public string Display { get; init; } = "";
		public string Primary { get; init; } = "";
	}
	

    public interface IBackupService
    {
        void PerformBackup();
    }

    public interface INavigationService
    {
        object NavigateTo(string pageKey);
        Task<object> NavigateToAsync(string pageKey);
        object NavigateToModule(string actionTitle, string moduleKey, string? tabName = null, string? tabHeader = null);
        void ClearCache();
    }

    public interface INavServ : INavigationService { }

	public interface ILogServ
	{
		string LogFilePath { get; }
		void LogInfo(string category, string message);
		void LogWarning(string category, string message);
		void LogError(string category, string message, Exception? ex = null);
		void LogDebug(string category, string message);
		void LogLifecycle(string eventName, string message = "");
		void LogOperation(string category, string action, string message);
		void LogAuthAttempt(string username, bool success, string? reason = null);

		/// <summary>Records that a file was loaded (or failed to load) during startup or runtime.</summary>
		void LogFileAccess(string filePath, string purpose, bool success = true, string? reason = null);

		/// <summary>Records a security-relevant event: permission denial, integrity failure, tamper attempt, etc.</summary>
		void LogSecurity(string eventType, string message, string? username = null);
	}

    public interface IAuthService
    {
        Task<AuthResult> LoginAsync(string username, string password);
        Task LogoutAsync();
        Task<UserInfo?> GetCurrentUserAsync();
        bool IsAuthenticated { get; }
    }

    public interface ICoCreateSvc
    {
        Task<CoCreateResult> CreateAsync(NewCoReq request);
    }

    public interface ISessionManager
    {
        string? CurrentUser { get; set; }
        bool IsLoggedIn { get; }
        void Login(string username);
        void Logout();
    }

    public interface IAuditService
    {
        Task LogSystemStartAsync();
        Task LogSystemStopAsync();

        /// <summary>Records which files were read/used while the application was starting up.</summary>
        Task LogStartupFileAsync(string filePath, string purpose, bool success = true, string? reason = null);

        /// <summary>Records a normal action performed by a logged-in user (menu opened, record saved, report printed...).</summary>
        Task LogUserActionAsync(string username, string action, string details = "");

        /// <summary>Records a suspicious or abusive event: denied permission, failed integrity check, license tampering, repeated failed logins...</summary>
        Task LogSecurityViolationAsync(string eventType, string details, string? username = null);
    }

    public class UserModel
    {
        public string UserId { get; set; } = "USER-001";
        public string FullName { get; set; } = "Ø§Ù„Ù…Ø¯ÙŠØ± Ø§Ù„Ø¹Ø§Ù…";
        public string Email { get; set; } = "admin@shoutech.com";
        public string CompanyName { get; set; } = "Ø´Ùˆ ØªÙŠÙƒ Ù„Ù„Ø¨Ø±Ù…Ø¬ÙŠØ§Øª";
        public bool IsAuthenticated { get; set; } = true;
        public List<string> Roles { get; set; } = new();
    }

    public class RecentDocument
    {
        public string DocumentNumber { get; set; } = "";
        public string DocumentType { get; set; } = "";
        public DateTime Date { get; set; } = DateTime.Now;
        public string CustomerName { get; set; } = "";
        public decimal TotalAmount { get; set; }
        public string Status { get; set; } = "";
    }


}
