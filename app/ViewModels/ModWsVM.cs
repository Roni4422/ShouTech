using System.Collections.Generic;
using ShouTech.App.Navigation;
using ShouTech.App.Interaction;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ShouTech.App.ViewModels
{
// ---- merged from cod/app/src/ViewModels/BaseVM.cs ----
    public abstract class BaseVM : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

// ---- merged from cod/app/src/ViewModels/DockedWinItem.cs ----
    public sealed class DockedWinItem
    {
        public required string Title { get; init; }
        public required Window Window { get; init; }

        // ═══════════════════════════════════════════════════════════
        // v0.2.0 additions — Canvas positioning (used by MWin docking)
        // ═══════════════════════════════════════════════════════════
        public double Left { get; set; }
        public double Top { get; set; } = 4;
        public double ItemWidth { get; set; } = 180;
        public int ZIndex { get; set; }
    }

    public class ModWsVM : BaseVM
    {
		public ModWsVM(string title, string moduleKey, string actionTitle)
		{
			Title = title;
			ModuleKey = moduleKey;
			ActionTitle = actionTitle;
			ModuleDisplayName = RibModMap.GetModuleDisplayName(moduleKey);
			Description = $"{ModuleDisplayName} module - {ActionTitle}";
			StatusHint = "Ready for development - this screen will be connected to the database and business services.";

			ItemActions = GetItemActions();
			AccountActions = GetAccountActions();

			OpenContextActionCommand = new RelayCommand<EntityContextAction?>(OpenContextAction);
		}

        public string Title { get; }
        public string ModuleKey { get; }
        public string ActionTitle { get; }
        public string ModuleDisplayName { get; }
        public string Description { get; }
        public string StatusHint { get; private set; }

        public RelayCommand<EntityContextAction?> OpenContextActionCommand { get; }

        public IReadOnlyList<EntityContextAction> ItemActions { get; }

		public IReadOnlyList<EntityContextAction> AccountActions { get; }

        private static IReadOnlyList<EntityContextAction> GetItemActions()
            => EntityContextActions.For(EntityContextType.Item);

        private static IReadOnlyList<EntityContextAction> GetAccountActions()
            => EntityContextActions.For(EntityContextType.Account);

        private void OpenContextAction(EntityContextAction? action)
        {
            if (action is null)
                return;

            StatusHint = $"Requested: {action.Label}";
            OnPropertyChanged(nameof(StatusHint));
        }
    }
}
