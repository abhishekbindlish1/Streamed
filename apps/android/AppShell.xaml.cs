using StreamedTv.Views;

namespace StreamedTv
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            Routing.RegisterRoute("PlayerPage", typeof(PlayerPage));
            CurrentItem = ServiceLocator.RequiresPairing && !ServiceLocator.Auth.IsAuthenticated ? LoginRoot : CatalogRoot;
            Navigating += (_, args) =>
            {
                if (ServiceLocator.RequiresPairing && !ServiceLocator.Auth.IsAuthenticated)
                {
                    var path = args.Target.Location.OriginalString.Split('?')[0];
                    if (path.Contains("MainPage", StringComparison.Ordinal) || path.Contains("PlayerPage", StringComparison.Ordinal)) args.Cancel();
                }
            };
        }
    }
}
