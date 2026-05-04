using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace BibAdmin
{
    public class ClockDriftAlertWindow : Window
    {
        private static readonly List<ClockDriftAlertWindow> _active = new();
        private static readonly object _lock = new();

        private const double W = 340;
        private const double Gap = 8;

        public ClockDriftAlertWindow(string pcNumber, double offsetSeconds)
        {
            Width = W;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.Height;

            PositionWindow();

            var root = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 170, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black }
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            titlePanel.Children.Add(new TextBlock
            {
                Text = "🕐",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            titlePanel.Children.Add(new TextBlock
            {
                Text = $"{pcNumber} — дрейф системных часов",
                Foreground = new SolidColorBrush(Color.FromRgb(85, 170, 255)),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetRow(titlePanel, 0);
            grid.Children.Add(titlePanel);

            double absOffset = Math.Abs(offsetSeconds);
            string direction = offsetSeconds > 0
                ? $"отстаёт от сервера на {absOffset:F0}с"
                : $"опережает сервер на {absOffset:F0}с";

            string info =
                $"Клиент {direction}.\n" +
                $"Таймер сессии будет скорректирован автоматически.\n" +
                $"Рекомендуется синхронизировать время на ПК.";

            var infoText = new TextBlock
            {
                Text = info,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 204)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(infoText, 1);
            grid.Children.Add(infoText);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var btnDismiss = MakeButton("✓  Принято", Color.FromRgb(40, 60, 90), Color.FromRgb(55, 100, 160));
            btnDismiss.Click += (s, e) => SafeClose();
            btnPanel.Children.Add(btnDismiss);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            root.Child = grid;
            Content = root;

            Closed += (s, e) =>
            {
                lock (_lock) { _active.Remove(this); RepositionAll(); }
            };

            lock (_lock) { _active.Add(this); }
        }

        public void SafeClose()
        {
            try { Dispatcher.Invoke(Close); } catch { }
        }

        private void PositionWindow()
        {
            var area = SystemParameters.WorkArea;
            int index;
            lock (_lock) { index = _active.Count; }
            Left = area.Left + (area.Width - W) / 2;
            Top = area.Top + area.Height * 0.30 + (ActualHeight + Gap) * index;
        }

        private static void RepositionAll()
        {
            lock (_lock)
            {
                var area = SystemParameters.WorkArea;
                for (int i = 0; i < _active.Count; i++)
                {
                    var w = _active[i];
                    w.Dispatcher.Invoke(() =>
                    {
                        w.Left = area.Left + (area.Width - W) / 2;
                        w.Top = area.Top + area.Height * 0.30 + (w.ActualHeight + Gap) * i;
                    });
                }
            }
        }

        private static Button MakeButton(string text, Color bg, Color hover)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 11,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.MouseEnter += (s, e) => btn.Background = new SolidColorBrush(hover);
            btn.MouseLeave += (s, e) => btn.Background = new SolidColorBrush(bg);
            return btn;
        }
    }
}
