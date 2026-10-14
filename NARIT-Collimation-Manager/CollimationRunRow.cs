using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace NARIT_Collimation_Manager;

public sealed class CollimationRunRow : INotifyPropertyChanged
{
    private string _selectedScrew;
    private string _turnText;
    private double? _score;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Sequence { get; }

    public string SelectedScrew
    {
        get => _selectedScrew;
        set
        {
            if (_selectedScrew == value)
            {
                return;
            }

            _selectedScrew = value;
            OnPropertyChanged();
        }
    }

    public string TurnText
    {
        get => _turnText;
        set
        {
            if (_turnText == value)
            {
                return;
            }

            _turnText = value;
            OnPropertyChanged();
        }
    }

    public double? Score
    {
        get => _score;
        private set
        {
            if (_score == value)
            {
                return;
            }

            _score = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScoreDisplay));
        }
    }

    public string ScoreDisplay => Score.HasValue
        ? $"{Score.Value.ToString("0.00", CultureInfo.InvariantCulture)} / 10"
        : "-";

    public CollimationRunRow(int sequence, string selectedScrew, string turnText)
    {
        Sequence = sequence;
        _selectedScrew = selectedScrew;
        _turnText = turnText;
    }

    public void NormalizeTurnText()
    {
        if (!double.TryParse(TurnText, NumberStyles.Float, CultureInfo.InvariantCulture, out double turnValue))
        {
            TurnText = "0.0";
            return;
        }

        turnValue = Math.Round(Math.Max(0.0, turnValue), 1, MidpointRounding.AwayFromZero);
        TurnText = turnValue.ToString("0.0", CultureInfo.InvariantCulture);
    }

    public void RunEvaluation()
    {
        double turnValue = ParseTurnValue();
        double targetTurn = SelectedScrew switch
        {
            "Screw 1" => 0.8,
            "Screw 2" => 1.2,
            "Screw 3" => 1.6,
            _ => 1.0
        };

        double turnPenalty = Math.Abs(turnValue - targetTurn) * 3.15;
        double sequencePenalty = ((Sequence - 1) % 3) * 0.18;
        double rawScore = 10.0 - turnPenalty - sequencePenalty;

        Score = Math.Round(Math.Clamp(rawScore, 1.0, 10.0), 2, MidpointRounding.AwayFromZero);
    }

    private double ParseTurnValue()
    {
        if (!double.TryParse(TurnText, NumberStyles.Float, CultureInfo.InvariantCulture, out double turnValue))
        {
            return 0.0;
        }

        return turnValue;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
