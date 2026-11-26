using System.Windows;
using ShouTech.App.Services;

namespace ShouTech.App
{
    /// <summary>
    /// Process-wide runtime context shared by the shell and all secondary windows.
    /// Language and visual identity live on App + services; company/user identity lives here.
    /// </summary>
    public static class AppState
    {
        public static string? CurrentCompanyCode { get; set; }
        public static string? CurrentCompanyName { get; set; }
        public static string? CurrentCompanyNameEn { get; set; }
        public static UserInfo? CurrentUser { get; set; }
		public static string? CurrentDatabaseName { get; set; }
		

        /// <summary>
        /// Main shell kept alive and frozen while a new company login is shown.
        /// It is re-enabled only after successful authentication; cancellation does not
        /// restore the previous company session.
        /// </summary>
        public static Window? InactiveMainWindow { get; set; }

        /// <summary>Localized company caption for status bars and window titles.</summary>
        public static string CurrentCompanyDisplayName
        {
            get
            {
                var lang = App.CurrentLanguageCode ?? "ar";
                if (lang.StartsWith("ar", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(CurrentCompanyName))
                        return CurrentCompanyName!;
                    if (!string.IsNullOrWhiteSpace(CurrentCompanyNameEn))
                        return CurrentCompanyNameEn!;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(CurrentCompanyNameEn))
                        return CurrentCompanyNameEn!;
                    if (!string.IsNullOrWhiteSpace(CurrentCompanyName))
                        return CurrentCompanyName!;
                }

                return CurrentCompanyCode ?? string.Empty;
            }
        }

        public static void SetCompany(string? code, string? nameLocal, string? nameEn)
        {
            CurrentCompanyCode = code;
            CurrentCompanyName = nameLocal;
            CurrentCompanyNameEn = nameEn;
        }

        public static void ClearCompany()
        {
            CurrentCompanyCode = null;
            CurrentCompanyName = null;
            CurrentCompanyNameEn = null;
			CurrentDatabaseName = null;
        }

        public static void ClearCompanySwitchState()
        {
            InactiveMainWindow = null;
        }
		
		public static void SetDatabase(string? databaseName)
		{
			CurrentDatabaseName = databaseName;
		}
    }
}
