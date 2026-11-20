using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class LearningView:UserControl
{
    public LearningView(){InitializeComponent();}
    internal ComboBox Scope=>ScopeBox;
    internal ComboBox Mode=>ModeBox;
    internal ComboBox Kind=>KindBox;
    internal ComboBox StateFilter=>StateBox;
    internal CheckBox Capture=>CaptureBox;
    internal CheckBox Learn=>LearnBox;
    internal CheckBox Activate=>ActivateBox;
    internal CheckBox Share=>ShareBox;
    internal CheckBox Contribute=>ContributeBox;
    internal CheckBox Remote=>RemoteBox;
    internal NumberBox Batch=>BatchBox;
    internal TextBlock Status=>StatusText;
    internal TextBlock Coverage=>CoverageText;
    internal TextBlock Capacity=>CapacityText;
    internal Button History=>HistoryButton;
    internal TextBlock Service=>ServiceText;
    internal TextBlock SettingsInfo=>SettingsStatus;
    internal TextBlock Title=>DetailTitle;
    internal TextBox Editor=>EntryEditor;
    internal TextBox Evidence=>EvidenceText;
    internal ListView Rows=>RowsList;
    internal Button SaveSettings=>SaveSettingsButton;
    internal Button Process=>ProcessButton;
    internal Button Edit=>EditButton;
    internal Button Cancel=>CancelButton;
    internal Button ToggleState=>StateButton;
    internal Button Delete=>DeleteButton;
    internal Button Undo=>UndoButton;
    internal Button Retry=>RetryButton;
    internal Button Back=>BackButton;
    internal Button Refresh=>RefreshButton;
}
