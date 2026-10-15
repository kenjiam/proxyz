using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Proxyz.App.ViewModels;
using Proxyz.Core;
using Proxyz.Core.Models;

namespace Proxyz.App.Views;

public partial class ProfileEditorWindow : Window
{
    private readonly IReadOnlyList<Profile> _profiles;
    private readonly ProfileEditorModel _model;
    public Profile? Result { get; private set; }
    public ProfileEditorWindow(Profile source, IReadOnlyList<Profile> profiles)
    {
        InitializeComponent(); _profiles = profiles; _model = new(source); DataContext = _model;
        Loaded += (_, _) => NameBox.Focus();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var result = _model.Build();
        var errors = Validation.Profile(result, _profiles);
        if (errors.Count == 0) { Result = result; DialogResult = true; return; }
        _model.Error = string.Join(Environment.NewLine, errors);
        var peer = UIElementAutomationPeer.FromElement(ErrorSummary) ?? UIElementAutomationPeer.CreatePeerForElement(ErrorSummary);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        if (string.IsNullOrWhiteSpace(result.Name)) NameBox.Focus();
        else if (result.Settings.Script && errors.Any(x => x.Contains("PAC"))) PacBox.Focus();
        else if (_model.ShowSimple) HostBox.Focus();
        else if (_model.ShowAdvanced) RawBox.Focus();
        else ModeBox.Focus();
    }
}
