namespace StreamedTv
{
    public static class ServiceLocator
    {
#if SANDBOX
        public static bool IsSandbox => true;
#else
        public static bool IsSandbox => false;
#endif
        public static Services.BackendAuthService Auth { get; private set; }
        public static Services.StreamedCatalogService Catalog { get; private set; }
        public static Services.StreamResolverService Resolver { get; private set; }

        public static void Initialize()
        {
#if SANDBOX
            Services.StreamedEndpoints.BaseUrl = "http://10.0.2.2:8080";
            Auth = new Services.BackendAuthService("http://10.0.2.2:8080");
#else
            Auth = new Services.BackendAuthService();
            Auth.ReloadJwtFromDisk();
#endif
            Catalog = new Services.StreamedCatalogService();
            Resolver = new Services.StreamResolverService(Auth);
        }
    }
}
