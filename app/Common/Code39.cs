// ========================================================================
// FILE: Common/Code39.cs
// PROJECT: SHOUTECH ERP — Code-39 barcode encoding and rendering
// (services phase B4: the Barcode Designer's second symbology,
// alongside the existing Code-128 encoder in Code128.cs).
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ShouTech.App.Common
{
    /// <summary>
    /// Code-39 encoder. Each character is 9 elements (bar/space
    /// alternating, starting and ending with a bar); a wide element
    /// is 3 narrow units. The pattern table is the AIM/Code-39
    /// standard set (cross-checked against ZXing's
    /// CHARACTER_ENCODINGS). Supported characters: 0-9, A-Z, and
    /// - . space $ / + %. Input is upper-cased; characters outside
    /// the set are dropped (Code 39 cannot represent them). The
    /// start/stop character '*' is added by the encoder.
    /// </summary>
    public static class Code39
    {
        // Element widths per character: 9 digits, 1 = narrow, 3 = wide,
        // alternating bar/space starting with a bar.
        private static readonly Dictionary<char, string> Patterns = new()
        {
            ['0'] = "111331311", ['1'] = "311311113", ['2'] = "113311113",
            ['3'] = "313311111", ['4'] = "111331113", ['5'] = "311331111",
            ['6'] = "113331111", ['7'] = "111311313", ['8'] = "311311311",
            ['9'] = "113311311",
            ['A'] = "311113113", ['B'] = "113113113", ['C'] = "313113111",
            ['D'] = "111133113", ['E'] = "311133111", ['F'] = "113133111",
            ['G'] = "111113313", ['H'] = "311113311", ['I'] = "113113311",
            ['J'] = "111133311", ['K'] = "311111133", ['L'] = "113111133",
            ['M'] = "313111131", ['N'] = "111131133", ['O'] = "311131131",
            ['P'] = "113131131", ['Q'] = "111111333", ['R'] = "311111331",
            ['S'] = "113111331", ['T'] = "111131331", ['U'] = "331111113",
            ['V'] = "133111113", ['W'] = "333111111", ['X'] = "131131113",
            ['Y'] = "331131111", ['Z'] = "133131111",
            ['-'] = "131111313", ['.'] = "331111311", [' '] = "133111311",
            ['$'] = "131313111", ['/'] = "131311313", ['+'] = "131113313",
            ['%'] = "111313313", ['*'] = "131131311"
        };

        /// <summary>The characters Code 39 can encode (input is
        /// upper-cased before filtering).</summary>
        public static string Sanitize(string data)
        {
            if (string.IsNullOrEmpty(data))
                return string.Empty;
            return new string(data.ToUpperInvariant().Where(c => Patterns.ContainsKey(c) && c != '*').ToArray());
        }

        public static bool CanEncode(string data) => Sanitize(data).Length > 0;

        /// <summary>Element-width patterns for '*' + data + '*',
        /// one 9-digit string per character.</summary>
        public static IReadOnlyList<string> EncodeToPatterns(string data)
        {
            var clean = Sanitize(data);
            if (clean.Length == 0)
                throw new ArgumentException(
                    "The value contains no characters Code 39 can encode.", nameof(data));
            var result = new List<string>(clean.Length + 2) { Patterns['*'] };
            foreach (var c in clean)
                result.Add(Patterns[c]);
            result.Add(Patterns['*']);
            return result;
        }

        /// <summary>
        /// Renders the barcode as a WPF canvas of bars on a quiet-zone
        /// background — the same rendering contract as
        /// Code128.BuildVisual, so callers can switch symbologies
        /// freely. Characters are separated by one narrow space.
        /// </summary>
        public static FrameworkElement BuildVisual(
            string data,
            double moduleWidth = 2,
            double barHeight = 56,
            double quietZone = 12,
            Brush? barBrush = null,
            Brush? backgroundBrush = null)
        {
            var patterns = EncodeToPatterns(data);
            var gap = moduleWidth; // one narrow unit between characters
            var elementUnits = patterns.Sum(p => p.Sum(d => d - '0'));
            var totalWidth = quietZone * 2 + (elementUnits + patterns.Count - 1) * moduleWidth;

            var canvas = new Canvas
            {
                Width = totalWidth,
                Height = barHeight,
                Background = backgroundBrush ?? Brushes.White
            };

            var bars = barBrush ?? Brushes.Black;
            bars.Freeze();

            var x = quietZone;
            foreach (var pattern in patterns)
            {
                var bar = true;
                foreach (var d in pattern)
                {
                    var width = (d - '0') * moduleWidth;
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
                x += gap;
            }

            return canvas;
        }
    }
}
