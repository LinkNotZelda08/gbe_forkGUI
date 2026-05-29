using GoldbergGUI.Core.Models;
using MvvmCross.Platforms.Wpf.Presenters.Attributes;
using System.Windows;

// ReSharper disable UnusedType.Global
namespace GoldbergGUI.WPF.Views
{
    /// <summary>
    ///     Interaction logic for MainView.xaml
    /// </summary>
    [MvxContentPresentation(WindowIdentifier = nameof(MainWindow))]
    public partial class MainView
    {
        public MainView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Handles the "🎮" detect button click in the Bindings DataGrid.
        /// Opens <see cref="ControllerDetectDialog"/>, and if input is detected,
        /// writes the result back to the row's <see cref="ControllerBinding.Binding"/>.
        /// </summary>
        private void DetectBinding_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ControllerBinding binding) return;

            var dlg = new ControllerDetectDialog(binding.Binding) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.ResultBinding is not null)
                binding.Binding = dlg.ResultBinding;
        }
    }
}
