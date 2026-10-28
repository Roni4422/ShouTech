using System.Windows;

namespace ShouTech.App.ViewModels
{
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
}