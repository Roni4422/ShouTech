using System.Collections.Generic;
using ShouTech.App.Navigation;
using ShouTech.App.Interaction;
using CommunityToolkit.Mvvm.Input;

namespace ShouTech.App.ViewModels
{
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