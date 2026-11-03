using Microsoft.UI.Xaml.Controls;
using UnoUML.Core;

namespace UnoUML.App;

public sealed partial class MainPage
{
    private void AddSaltFieldActions(SourceFieldEdit field)
    {
        if (field.Kind == "salt-state")
        {
            SelectionTitle.Text = field.Text == "true" ? "Checked" : "Unchecked";
        }
        if (doc.SaltChoices(field).Count > 0)
            Properties.Children.Add(Item("Choose dropdown value…", "edit", () => _ = ChooseSaltValueAsync(field)));
    }

    private void ToggleSaltState(SourceFieldEdit field)
    {
        if (field != FieldSelection || renderedRevision != revision || resourceHistoryBusy) return;
        Edit(() => doc.ApplySourceField(field, field.Text == "true" ? "false" : "true"));
    }

    private async Task ChooseSaltValueAsync(SourceFieldEdit field)
    {
        var choices = doc.SaltChoices(field);
        if (choices.Count == 0) return;
        int selected = 0;
        for (int i = 0; i < choices.Count; i++)
            if (choices[i] == field.Text) { selected = i; break; }
        var picker = new ComboBox { Header = "Selected value", ItemsSource = choices, SelectedIndex = selected, MinWidth = 240 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Selected value");
        if (await AskAsync("Dropdown value", picker, "Apply") == ContentDialogResult.Primary && picker.SelectedIndex >= 0)
            Edit(() => doc.SelectSaltChoice(field, picker.SelectedIndex));
    }
}
