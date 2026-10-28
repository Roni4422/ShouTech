using ShouTech.App.Services;

namespace ShouTech.App.Localization
{
    public static class Loc
    {
        public static string Get(string key) =>
            Resolve().GetString(key);

        public static string Get(string key, string defaultValue) =>
            Resolve().GetString(key, defaultValue);

        private static ILocSvc Resolve()
        {
            if (System.Windows.Application.Current is App app)
                return app.Localization;

            return AppHost.Localization;
        }
    }
}
