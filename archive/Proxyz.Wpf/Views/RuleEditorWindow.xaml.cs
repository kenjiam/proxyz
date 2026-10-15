using System.Windows;
using System.Windows.Automation.Peers;
using Proxyz.App.ViewModels;
using Proxyz.Core;
using Proxyz.Core.Models;

namespace Proxyz.App.Views;

public partial class RuleEditorWindow : Window
{
    private readonly AppConfig _config;
    private readonly RuleEditorModel _model;
    public NetworkRule? Result { get; private set; }
    public RuleEditorWindow(NetworkRule rule, AppConfig config, NetworkSnapshot snapshot)
    {
        InitializeComponent(); _config = config; _model = new(rule, config, snapshot); DataContext = _model;
        Loaded += (_, _) => NameBox.Focus();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rule = _model.Build();
            var errors = Validation.Rule(rule, _config);
            if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
            Result = rule; DialogResult = true;
        }
        catch (ArgumentException error)
        {
            _model.Error = error.Message;
            var peer = UIElementAutomationPeer.FromElement(ErrorSummary) ?? UIElementAutomationPeer.CreatePeerForElement(ErrorSummary);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            if (string.IsNullOrWhiteSpace(_model.Name)) NameBox.Focus();
            else if (_model.SelectedProfile is null) ProfileBox.Focus();
            else ModeBox.Focus();
        }
    }
}
