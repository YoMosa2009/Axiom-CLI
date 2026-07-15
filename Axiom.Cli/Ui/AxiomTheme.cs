using Spectre.Console;

namespace Axiom.Cli.Ui;

// Pulled directly from the GUI app's actual theme resources (Malx_AI/App.xaml and
// WorkplaceChatMessage.cs), not approximated — same "notebook" dark palette and the same
// per-role accent colors the desktop Council chat cards use.
internal static class AxiomTheme
{
    // Base "notebook" theme (App.xaml).
    public static readonly Color Background = Color.FromHex("#171615");
    public static readonly Color Surface = Color.FromHex("#211F1D");
    public static readonly Color TextPrimary = Color.FromHex("#EDE8E3");
    public static readonly Color TextSecondary = Color.FromHex("#B0A89F");
    public static readonly Color Border = Color.FromHex("#302D2A");
    public static readonly Color Gold = Color.FromHex("#B8924A"); // PrimaryAccentBrush

    // Council role accents (WorkplaceChatMessage.cs) — identical to the GUI's chat-card rail.
    public static readonly Color Architect = Color.FromHex("#8B8DF5");
    public static readonly Color Builder = Color.FromHex("#34D178");
    public static readonly Color Critic = Color.FromHex("#F5A623");
    public static readonly Color User = Color.FromHex("#C2C0B6");
    public static readonly Color Sandbox = Color.FromHex("#22B8CE");
    public static readonly Color Memory = Color.FromHex("#A78BFA");
    public static readonly Color SystemMuted = Color.FromHex("#8A8279");

    // Semantic status colors.
    public static readonly Color Error = Color.FromHex("#FF5C5C");
    public static readonly Color Warning = Color.FromHex("#F97316");
    public static readonly Color Success = Builder; // GUI has no dedicated success color; reuses Builder green.

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
