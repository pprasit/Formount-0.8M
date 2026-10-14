using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NARIT_Collimation_Manager.UI;

public partial class CollimationManagerControl : UserControl
{
    private static readonly Regex TurnInputRegex = new(@"^\d*([.]\d{0,1})?$");

    public CollimationManagerViewModel ViewModel { get; } = new();

    public CollimationManagerControl()
    {
        InitializeComponent();
        DataContext = ViewModel;
        UpdateAverageScore();
    }

    private void RunRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CollimationRunRow row })
        {
            return;
        }

        row.NormalizeTurnText();
        row.RunEvaluation();
        UpdateAverageScore();
    }

    private void TurnTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            e.Handled = true;
            return;
        }

        string proposed = BuildProposedTurnText(textBox, e.Text);
        e.Handled = !TurnInputRegex.IsMatch(proposed);
    }

    private void TurnTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: CollimationRunRow row })
        {
            return;
        }

        row.NormalizeTurnText();
    }

    private static string BuildProposedTurnText(TextBox textBox, string newText)
    {
        string currentText = textBox.Text ?? string.Empty;
        int selectionStart = textBox.SelectionStart;
        int selectionLength = textBox.SelectionLength;

        if (selectionLength > 0)
        {
            currentText = currentText.Remove(selectionStart, selectionLength);
        }

        return currentText.Insert(selectionStart, newText);
    }

    private void UpdateAverageScore()
    {
        AverageScoreText.Text = ViewModel.GetAverageScoreDisplay();
    }
}
