using StreamedTv.Views;

namespace StreamedTv
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            Routing.RegisterRoute("LoginPage", typeof(LoginPage));
            Routing.RegisterRoute("PlayerPage", typeof(PlayerPage));

            // Auth gate: redirect to login if no JWT stored
            if (!ServiceLocator.IsSandbox && !ServiceLocator.Auth.IsAuthenticated)
            {
                Dispatcher.Dispatch(async () =>
                {
                    await GoToAsync("LoginPage").ConfigureAwait(true);
                });
            }
        }
    }
}
