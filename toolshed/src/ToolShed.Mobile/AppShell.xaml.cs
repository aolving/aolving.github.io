using ToolShed.Mobile.Pages;

namespace ToolShed.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Screens that are pushed on top of the tabs rather than being a tab themselves.
        Routing.RegisterRoute("tool", typeof(ToolDetailPage));
        Routing.RegisterRoute("edittool", typeof(EditToolPage));
    }
}
