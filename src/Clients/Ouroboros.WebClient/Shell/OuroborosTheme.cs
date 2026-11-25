using MudBlazor;

namespace Ouroboros.WebClient.Shell;

// Paleta MudBlazor alinhada aos tokens de wwwroot/app.css, para que os componentes da biblioteca (menu, popovers,
// diálogos) tenham a mesma identidade da página. #ff6200 é o acento nos dois temas; o texto sobre ele é escuro
// (contraste de cerca de 6,6:1), porque texto branco sobre esse laranja fica em torno de 3:1.
internal static class OuroborosTheme
{
    public const string Accent = "#ff6200";
    private const string OnAccent = "#0a0a0b";

    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = Accent,
            PrimaryContrastText = OnAccent,
            Background = "#09090b",
            Surface = "#111114",
            AppbarBackground = "#09090b",
            AppbarText = "#fafafa",
            DrawerBackground = "#111114",
            TextPrimary = "#fafafa",
            TextSecondary = "#a1a1aa",
            LinesDefault = "rgba(255,255,255,0.08)",
            Divider = "rgba(255,255,255,0.08)",
            ActionDefault = "#a1a1aa",
        },
        PaletteLight = new PaletteLight
        {
            Primary = Accent,
            PrimaryContrastText = OnAccent,
            Background = "#fafafa",
            Surface = "#ffffff",
            AppbarBackground = "#fafafa",
            AppbarText = "#09090b",
            TextPrimary = "#09090b",
            TextSecondary = "#52525b",
            LinesDefault = "rgba(9,9,11,0.08)",
            Divider = "rgba(9,9,11,0.08)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "ui-sans-serif", "system-ui", "-apple-system", "Segoe UI", "Roboto", "sans-serif"],
            },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "12px",
        },
    };
}
