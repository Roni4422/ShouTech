// ========================================================================
// FILE: Common/Code128.cs
// PROJECT: SHOUTECH ERP — Code-128 barcode encoding, rendering, printing
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ShouTech.App.Common
{
    /// <summary>
    /// Code-128 barcode encoder (symbol sets B and C with automatic switching).
    /// Produces the symbol sequence (start, data, checksum, stop) for any
    /// printable-ASCII payload. Non-encodable characters are sanitized to '?'.
    /// </summary>
    public static class Code128
    {
        // Module-width patterns, alternating bar/space, always starting with a bar.
        // Index 106 is the stop symbol (7 modules including the final 2-wide bar).
        private static readonly string[] Patterns =
        {
            "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213",
            "221312","231212","112232","122132","122231","113222","123122","123221","223211","221132",
            "221231","213212","223112","312131","311222","321122","321221","312212","322112","322211",
            "212123","212321","232121","111323","131123","131321","112313","132113","132311","211313",
            "231113","231311","112133","112331","132131","113123","113321","133121","313121","211331",
            "231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
            "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214",
            "112412","122114","122411","142112","142211","241211","221114","413111","241112","134111",
            "111242","121142","121241","114212","124112","124211","411212","421112","421211","212141",
            "214121","412121","111143","111341","131141","114113","114311","411113","411311","113141",
            "114131","311141","411131","211412","211214","211232","2331112"
        };

        /// <summary>
        /// Encodes <paramref name="data"/> into Code-128 symbol values
        /// (0–105) followed by the stop symbol (106).
        /// </summary>
        public static int[] EncodeToSymbols(string data)
        {
            data ??= string.Empty;
            if (data.Length == 0) data = "0";

            var symbols = new List<int>();
            var i = 0;
            var codeC = false;

            int DigitRun(int pos)
            {
                var n = 0;
                while (pos + n < data.Length && char.IsDigit(data[pos + n])) n++;
                return n;
            }

            // Start symbol: prefer set C when the payload opens with digits.
            var openingRun = DigitRun(0);
            if (openingRun >= 4 || (openingRun >= 2 && openingRun == data.Length))
            {
                symbols.Add(105); // Start C
                codeC = true;
            }
            else
            {
                symbols.Add(104); // Start B
            }

            while (i < data.Length)
            {
                if (codeC)
                {
                    var run = DigitRun(i);

                    if (run >= 2)
                    {
                        var pairs = run / 2;
                        for (var k = 0; k < pairs; k++)
                        {
                            symbols.Add(
                                (data[i] - '0') * 10 + (data[i + 1] - '0'));
                            i += 2;
                        }

                        // An odd trailing digit continues in set B.
                        if (run % 2 == 1)
                        {
                            symbols.Add(100); // Code B
                            codeC = false;
                        }

                        continue;
                    }

                    symbols.Add(100); // Code B
                    codeC = false;
                    continue;
                }

                var digitRun = DigitRun(i);

                if (digitRun >= 4)
                {
                    // Keep an odd-length digit run aligned for set C.
                    if (digitRun % 2 == 1)
                    {
                        symbols.Add(CharToB(data[i]));
                        i++;
                    }

                    symbols.Add(99); // Code C
                    codeC = true;
                    continue;
                }

                var value = CharToB(data[i]);
                symbols.Add(value >= 0 ? value : CharToB('?'));
                i++;
            }

            // Checksum: weighted sum modulo 103.
            var sum = symbols[0];
            for (var k = 1; k < symbols.Count; k++)
                sum += symbols[k] * k;

            symbols.Add(sum % 103);
            symbols.Add(106); // Stop

            return symbols.ToArray();
        }

        private static int CharToB(char c)
        {
            var v = c - 32;
            return v is >= 0 and <= 95 ? v : -1;
        }

        /// <summary>
        /// Renders the barcode as a WPF canvas of bars on a quiet-zone
        /// background. Suitable for on-screen preview and PrintVisual.
        /// Optional brushes let the barcode designer restyle the symbol.
        /// </summary>
        public static FrameworkElement BuildVisual(
            string data,
            double moduleWidth = 2,
            double barHeight = 56,
            double quietZone = 12,
            Brush? barBrush = null,
            Brush? backgroundBrush = null)
        {
            var symbols = EncodeToSymbols(data);

            var totalModules = symbols.Sum(s => Patterns[s].Length == 7
                ? 13
                : 11);

            var canvas = new Canvas
            {
                Width = quietZone * 2 + totalModules * moduleWidth,
                Height = barHeight,
                Background = backgroundBrush ?? Brushes.White
            };

            var bars = barBrush ?? Brushes.Black;
            bars.Freeze();

            var x = quietZone;

            foreach (var symbol in symbols)
            {
                var pattern = Patterns[symbol];
                var bar = true;

                foreach (var w in pattern)
                {
                    var width = (w - '0') * moduleWidth;

                    if (bar)
                    {
                        var rect = new Rectangle
                        {
                            Width = width,
                            Height = barHeight,
                            Fill = bars,
                            SnapsToDevicePixels = true
                        };
                        canvas.Children.Add(rect);
                        Canvas.SetLeft(rect, x);
                        Canvas.SetTop(rect, 0);
                    }

                    x += width;
                    bar = !bar;
                }
            }

            return canvas;
        }

        /// <summary>
        /// Builds a valid EAN-13 value from a numeric prefix and a sequence
        /// number: prefix + zero-padded sequence, completed with the standard
        /// EAN-13 checksum digit. Used by the bulk barcode generator.
        /// </summary>
        public static string BuildEan13(string prefix, long sequence)
        {
            prefix = new string((prefix ?? string.Empty)
                .Where(char.IsDigit)
                .ToArray());

            if (prefix.Length > 12)
                prefix = prefix[..12];

            var body = prefix + sequence.ToString(
                CultureInfo.InvariantCulture)
                .PadLeft(Math.Max(1, 12 - prefix.Length), '0');
            if (body.Length > 12)
                body = body[..12];
            body = body.PadRight(12, '0');

            var sum = 0;
            for (var i = 0; i < 12; i++)
            {
                var digit = body[i] - '0';
                sum += (i % 2 == 0) ? digit : digit * 3;
            }

            var check = (10 - (sum % 10)) % 10;
            return body + check.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Builds and prints material barcode labels through the system print
    /// dialog. Fully offline; no external dependencies.
    /// </summary>
    public static class BarcodeLabelPrinter
    {
        /// <summary>
        /// Builds a printable label visual containing the material name,
        /// the Code-128 barcode, the human-readable code and an optional
        /// price line.
        /// </summary>
        public static FrameworkElement BuildLabel(
            string materialName,
            string code,
            string? priceText = null)
        {
            var layout = new StackPanel
            {
                Background = Brushes.White,
                Margin = new Thickness(12)
            };

            if (!string.IsNullOrWhiteSpace(materialName))
            {
                layout.Children.Add(new TextBlock
                {
                    Text = materialName,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 320,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }

            layout.Children.Add(Code128.BuildVisual(code, 2, 56));

            layout.Children.Add(new TextBlock
            {
                Text = code,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });

            if (!string.IsNullOrWhiteSpace(priceText))
            {
                layout.Children.Add(new TextBlock
                {
                    Text = priceText,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            layout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            layout.Arrange(new Rect(layout.DesiredSize));

            return layout;
        }

        /// <summary>
        /// Builds one printable page visual containing several copies of the
        /// same label laid out in fixed columns. Used by "Create barcode"
        /// (print N copies of one material) and by the bulk generator.
        /// </summary>
        public static FrameworkElement BuildCopiesPage(
            string materialName,
            string code,
            string? priceText,
            int copies,
            int columns = 3)
        {
            if (copies < 1) copies = 1;
            if (columns < 1) columns = 1;

            var page = new WrapPanel
            {
                Background = Brushes.White,
                Margin = new Thickness(16)
            };

            for (var i = 0; i < copies; i++)
            {
                page.Children.Add(BuildLabel(materialName, code, priceText));
            }

            page.Measure(new Size(760, double.PositiveInfinity));
            page.Arrange(new Rect(new Point(0, 0), page.DesiredSize));

            return page;
        }

        /// <summary>
        /// Shows the system print dialog and prints the label when the user
        /// confirms. Returns true when the label was sent to the printer.
        /// </summary>
        public static bool Print(
            Window? owner,
            string jobTitle,
            string materialName,
            string code,
            string? priceText = null)
        {
            var dialog = new PrintDialog();

            bool? result;
            try
            {
                result = dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    owner,
                    ex.Message,
                    jobTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            if (result != true) return false;

            var label = BuildLabel(materialName, code, priceText);

            try
            {
                dialog.PrintVisual(label, jobTitle);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    owner,
                    ex.Message,
                    jobTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Shows the system print dialog and prints <paramref name="copies"/>
        /// copies of the same label on one page when the user confirms.
        /// Returns true when the page was sent to the printer.
        /// </summary>
        public static bool PrintCopies(
            Window? owner,
            string jobTitle,
            string materialName,
            string code,
            string? priceText,
            int copies,
            int columns = 3)
        {
            if (copies <= 1)
                return Print(owner, jobTitle, materialName, code, priceText);

            var dialog = new PrintDialog();

            bool? result;
            try
            {
                result = dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    owner,
                    ex.Message,
                    jobTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            if (result != true) return false;

            var page = BuildCopiesPage(materialName, code, priceText, copies, columns);

            try
            {
                dialog.PrintVisual(page, jobTitle);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    owner,
                    ex.Message,
                    jobTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>Formats a price line for the label, or null when no price.</summary>
        public static string? FormatPrice(decimal? price, string? currency)
        {
            if (price is null or <= 0) return null;

            var amount = price.Value.ToString("0.##", CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(currency)
                ? amount
                : amount + " " + currency;
        }
    }
}
