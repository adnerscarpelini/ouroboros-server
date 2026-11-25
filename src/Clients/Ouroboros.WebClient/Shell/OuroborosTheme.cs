using MudBlazor;

namespace Ouroboros.WebClient.Shell;

// Identidade visual: #ff6200 é a cor de acento (botões, links e destaques), nos temas claro e escuro. Texto branco
// sobre esse laranja tem contraste de cerca de 3:1, abaixo do mínimo para texto normal; por isso a AppBar é escura
// e o laranja não é usado como fundo de textos longos.
internal static class OuroborosTheme
{
    public const string Accent = "#ff6200";

    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Accent,
            AppbarBackground = "#1c1c1e",
            AppbarText = "#ffffff",
            Background = "#f7f7f8",
        },
        PaletteDark = new PaletteDark
        {
            Primary = Accent,
            AppbarBackground = "#121214",
            AppbarText = "#ffffff",
        },
    };
}
