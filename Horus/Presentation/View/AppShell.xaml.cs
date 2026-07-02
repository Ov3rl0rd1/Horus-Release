namespace Horus.Presentation.View
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            //Routing.RegisterRoute("AuthPage", typeof(AuthPage));
            //Routing.RegisterRoute("RegisterPage", typeof(RegisterPage));

#if ADMIN_MODE
            // Dynamically inject the Admin tab into the existing TabBar
            var adminTab = new Tab { Title = "ADMIN" };
            adminTab.Icon = new FontImageSource
            {
                Glyph = "⚠",
                FontFamily = "OpenSansRegular",
                Size = 22,
                Color = Color.FromArgb("#BF5FFF")
            };
            var adminContent = new ShellContent
            {
                Title = "Admin",
                Route = "AdminPageTab",
                ContentTemplate = new DataTemplate(typeof(AdminPage))
            };
            adminTab.Items.Add(adminContent);

            // Insert before the last item (Settings) so order is HOME / ADMIN / SETTINGS
            if (Items.Count > 0 && Items[0] is TabBar tabBar)
                tabBar.Items.Insert(1, adminTab);
#endif
        }
    }
}
