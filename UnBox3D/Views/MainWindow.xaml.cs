using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Control = System.Windows.Forms.Control;
using UnBox3D.Rendering;
using UnBox3D.Rendering.OpenGL;
using UnBox3D.Utils;
using TextBox = System.Windows.Controls.TextBox;

namespace UnBox3D.Views
{
    public partial class MainWindow : Window
    {
        private IBlenderInstaller _blenderInstaller;
        private IGLControlHost? _controlHost;
        private ILogger? _logger;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger?.Info("MainWindow loaded. Initializing OpenGL...");

                var loadingWindow = new LoadingWindow
                {
                    StatusHint = "Installing Blender...",
                    Owner = this,
                    IsProgressIndeterminate = false
                };
                loadingWindow.Show();

                if (_blenderInstaller != null)
                {
                    var progress = new Progress<double>(value =>
                    {
                        loadingWindow.UpdateProgress(value * 100);
                        loadingWindow.UpdateStatus($"Installing Blender... {Math.Round(value * 100)}%");
                    });

                    await _blenderInstaller.CheckAndInstallBlender(progress);
                }
                else
                {
                    _logger?.Warn("Blender installer dependency was null; skipping installation check.");
                }

                loadingWindow.Close();

                if (_controlHost != null)
                {
                    openGLHost.Child = (Control)_controlHost;
                    _logger?.Info("GLControlHost successfully attached to WindowsFormsHost.");
                    StartUpdateLoop();
                }
                else
                {
                    _logger?.Warn("GLControlHost not initialized; skipping rendering start.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error($"Error initializing OpenGL: {ex.Message}");
                System.Windows.MessageBox.Show($"Error initializing OpenGL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                _logger?.Info("MainWindow is closing. Performing cleanup...");
                _controlHost?.Cleanup();
                (_controlHost as IDisposable)?.Dispose();
                _logger?.Info("Cleanup completed successfully.");
            }
            catch (Exception ex)
            {
                _logger?.Error($"Error during cleanup: {ex.Message}");
            }
        }

        public void Initialize(IGLControlHost controlHost, ILogger logger, IBlenderInstaller blenderInstaller)
        {
            _controlHost = controlHost ?? throw new ArgumentNullException(nameof(controlHost));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blenderInstaller = blenderInstaller ?? throw new ArgumentNullException(nameof(blenderInstaller));
        }

        private async void StartUpdateLoop()
        {
            var sw = new Stopwatch();
            while (IsLoaded)
            {
                _controlHost?.Render();
                await Task.Delay(16);
            }
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;

            if (!char.IsDigit(e.Text[0]) && e.Text != ".")
            {
                e.Handled = true;
                return;
            }

            if (e.Text == "." && textBox.Text.Contains("."))
            {
                e.Handled = true;
                return;
            }
        }

        private void NumericTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Back || e.Key == Key.Delete || e.Key == Key.Left ||
                e.Key == Key.Right || e.Key == Key.Tab)
            {
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
            {
                var textBox = sender as TextBox;

                if (System.Windows.Clipboard.ContainsText())
                {
                    string clipboardText = System.Windows.Clipboard.GetText();

                    if (!IsValidDecimalInput(clipboardText))
                    {
                        e.Handled = true;
                        return;
                    }

                    string resultText = textBox.Text.Substring(0, textBox.SelectionStart) +
                                        clipboardText +
                                        textBox.Text.Substring(textBox.SelectionStart + textBox.SelectionLength);

                    if (resultText.Count(c => c == '.') > 1)
                    {
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        private bool IsValidDecimalInput(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;

            bool hasDecimal = false;

            foreach (char c in input)
            {
                if (c == '.')
                {
                    if (hasDecimal)
                        return false;
                    hasDecimal = true;
                }
                else if (!char.IsDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        private void NumericTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            int cursorPosition = textBox.SelectionStart;
            string originalText = textBox.Text;

            if (string.IsNullOrEmpty(textBox.Text) || textBox.Text == ".")
                return;

            if (float.TryParse(textBox.Text, out float value) &&
                textBox.DataContext is ViewModels.MainViewModel viewModel)
            {
                if (textBox.Name.Contains("Width"))
                    viewModel.PageWidth = value;
                else if (textBox.Name.Contains("Height"))
                    viewModel.PageHeight = value;
            }

            if (textBox.Text != originalText)
            {
                int charsAdded = textBox.Text.Length - originalText.Length;
                cursorPosition += charsAdded > 0 ? charsAdded : 0;
            }

            textBox.SelectionStart = cursorPosition;
        }

        private void NumericTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;

            if (string.IsNullOrWhiteSpace(textBox.Text) || textBox.Text == ".")
            {
                textBox.Text = "0";

                if (textBox.DataContext is ViewModels.MainViewModel viewModel)
                {
                    if (textBox.Name.Contains("Width"))
                        viewModel.PageWidth = 0;
                    else if (textBox.Name.Contains("Height"))
                        viewModel.PageHeight = 0;
                }
            }
        }

        private void MeshThreshold_ValueChanged(object sender, EventArgs e)
        { 
            if (sender is System.Windows.Controls.Slider slider)
            {
                Debug.WriteLine(slider.Value);
                if (DataContext is ViewModels.MainViewModel vm)
                {
                    vm.SmallMeshThreshold = (float)slider.Value;
                    vm.ApplyMeshThreshold();
                }
            }
        }

        // Settings menu item click (use DI)
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var settings = App.Services.GetRequiredService<SettingsWindow>();
            settings.Owner = this;
            settings.ShowDialog();
        }
    }
}
