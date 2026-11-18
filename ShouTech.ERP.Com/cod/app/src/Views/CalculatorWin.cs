using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShouTech.App.Views;

/// <summary>
/// Standard calculator (Tools): display + digit/operator pad, full keyboard
/// support (digits, + - * /, Enter = equals, Escape = clear, Backspace,
/// '.' decimal) and a one-slot memory of the last result (MS/MR). Pure UI —
/// no data, no permissions; global styles only.
/// </summary>
public sealed class CalculatorWin : BaseWin
{
    private readonly TextBox _display = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _closeButton = new();

    private decimal _accumulator;
    private string? _pendingOp;
    private bool _freshEntry = true;
    private decimal _memory;

    public CalculatorWin()
    {
        Width = 360;
        Height = 480;
        MinWidth = 320;
        MinHeight = 360;
        BuildUi();
        Loaded += (_, _) => _display.Focus();
    }

    protected override bool PersistGeometry => false;

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_closeButton));

        var content = new DockPanel();
        _display.IsReadOnly = true;
        _display.FontSize = 26;
        _display.TextAlignment = TextAlignment.Right;
        _display.Text = "0";
        _display.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(_display, Dock.Top);
        content.Children.Add(_display);

        var pad = new UniformGridPad();
        var keys = new[]
        {
            "C", "CE", "Back", "÷",
            "7", "8", "9", "×",
            "4", "5", "6", "-",
            "1", "2", "3", "+",
            "MS", "0", ".", "="
        };
        foreach (var key in keys)
        {
            var button = new Button
            {
                Content = key == "Back" ? "⌫" : key,
                Tag = key,
                Margin = new Thickness(3),
                MinHeight = 36, // Step 12: the pad compresses to the 360-DIP window minimum (was 48, which clipped)
                FontSize = 16
            };
            var captured = key;
            button.Click += (_, _) => Press(captured);
            pad.Children.Add(button);
        }
        content.Children.Add(pad);

        root.Children.Add(content);
        Content = root;
        PreviewKeyDown += OnPreviewKeyDown;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Calculator.Title");
        if (_closeButton.Tag is string key)
            _closeButton.Content = T(key);
        _statusBar.Text = _memory == 0m ? string.Empty : $"M: {Format(_memory)}";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key;
        if (key >= Key.D0 && key <= Key.D9)
        {
            Press(((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture));
            e.Handled = true;
        }
        else if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            Press(((int)key - (int)Key.NumPad0).ToString(CultureInfo.InvariantCulture));
            e.Handled = true;
        }
        else
        {
            switch (key)
            {
                case Key.Add: Press("+"); e.Handled = true; break;
                case Key.Subtract: Press("-"); e.Handled = true; break;
                case Key.Multiply: Press("×"); e.Handled = true; break;
                case Key.Divide: Press("÷"); e.Handled = true; break;
                case Key.OemPlus when Keyboard.Modifiers == ModifierKeys.None: Press("="); e.Handled = true; break;
                case Key.Enter: Press("="); e.Handled = true; break;
                case Key.Escape: Press("C"); e.Handled = true; break;
                case Key.Back: Press("Back"); e.Handled = true; break;
                case Key.Decimal: case Key.OemPeriod: Press("."); e.Handled = true; break;
                case Key.OemMinus: Press("-"); e.Handled = true; break;
            }
        }
    }

    private void Press(string key)
    {
        try
        {
            switch (key)
            {
                case "C":
                    _accumulator = 0m;
                    _pendingOp = null;
                    _display.Text = "0";
                    _freshEntry = true;
                    break;
                case "CE":
                    _display.Text = "0";
                    _freshEntry = true;
                    break;
                case "Back":
                    if (!_freshEntry && _display.Text.Length > 1)
                        _display.Text = _display.Text[..^1];
                    else if (!_freshEntry)
                        _display.Text = "0";
                    break;
                case "MS":
                    _memory = CurrentValue();
                    _statusBar.Text = $"M: {Format(_memory)}";
                    break;
                case "+": case "-": case "×": case "÷":
                    ApplyPending();
                    _pendingOp = key;
                    _freshEntry = true;
                    break;
                case "=":
                    ApplyPending();
                    _pendingOp = null;
                    _freshEntry = true;
                    break;
                case ".":
                    if (_freshEntry)
                    {
                        _display.Text = "0.";
                        _freshEntry = false;
                    }
                    else if (!_display.Text.Contains('.'))
                        _display.Text += ".";
                    break;
                default: // digit
                    if (_freshEntry || _display.Text == "0")
                    {
                        _display.Text = key;
                        _freshEntry = false;
                    }
                    else if (_display.Text.Replace(".", string.Empty).Replace("-", string.Empty).Length < 16)
                        _display.Text += key;
                    break;
            }
        }
        catch (Exception ex)
        {
            _display.Text = "0";
            _accumulator = 0m;
            _pendingOp = null;
            _freshEntry = true;
            _statusBar.Text = ex.Message;
        }
    }

    private void ApplyPending()
    {
        var value = CurrentValue();
        if (_pendingOp is null)
        {
            _accumulator = value;
            return;
        }
        _accumulator = _pendingOp switch
        {
            "+" => _accumulator + value,
            "-" => _accumulator - value,
            "×" => _accumulator * value,
            "÷" => value == 0m ? throw new InvalidOperationException(T("Calculator.DivideByZero")) : _accumulator / value,
            _ => value
        };
        _display.Text = Format(_accumulator);
    }

    private decimal CurrentValue()
        => decimal.TryParse(_display.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static string Format(decimal value)
        => value.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>UniformGrid without the extra using in every window file.</summary>
    private sealed class UniformGridPad : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGridPad() => Columns = 4;
    }
}
