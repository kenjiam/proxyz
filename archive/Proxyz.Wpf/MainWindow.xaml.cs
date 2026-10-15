using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using Proxyz.App.ViewModels;

namespace Proxyz.App;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); ViewModel = viewModel; DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true; Hide();
        }
        base.OnClosing(e);
    }
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Status)) return;
        var peer = UIElementAutomationPeer.FromElement(StatusText) ?? UIElementAutomationPeer.CreatePeerForElement(StatusText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            if (ViewModel.RefreshCommand.CanExecute(null)) ViewModel.RefreshCommand.Execute(null);
            e.Handled = true; return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        int tab = e.Key switch { Key.D1 => 0, Key.D2 => 1, Key.D3 => 2, Key.D4 => 3, _ => -1 };
        if (tab >= 0) { ViewModel.SelectedTab = tab; e.Handled = true; return; }
        if (e.Key == Key.M)
        {
            ViewModel.SelectedTab = 0;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ProfileChoice.Focus()));
            e.Handled = true;
        }
    }
}
