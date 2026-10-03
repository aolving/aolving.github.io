using System.Globalization;
using ToolShed.Client;
using ToolShed.Contracts;

namespace ToolShed.Mobile.Controls;

/// <summary>
/// A month calendar for choosing loan dates: the first day you tap is the start, the second is the last
/// day, and you can never go backwards. Days that could not make a valid booking are disabled, so a member
/// is never offered a choice the portal would then refuse. The rules live in <see cref="DateSelection"/>,
/// which is shared with the tests; this class only draws them.
/// </summary>
public sealed class BookingCalendarView : ContentView
{
    private static readonly Color Brand = Color.FromArgb("#8A4B1E");
    private static readonly Color BrandDark = Color.FromArgb("#D78B4F");
    private static readonly Color Booked = Color.FromArgb("#9A2F2F");
    private static readonly Color Requested = Color.FromArgb("#8A6D1E");

    private readonly Label _title = new() { FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    private readonly Label _status = new() { FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _previous = new() { Text = "‹", WidthRequest = 48, Padding = 0 };
    private readonly Button _next = new() { Text = "›", WidthRequest = 48, Padding = 0 };
    private readonly Button _clear = new() { Text = "Clear dates", IsVisible = false, HorizontalOptions = LayoutOptions.Start, BackgroundColor = Colors.Transparent, Padding = 0 };
    private readonly Grid _grid = new() { ColumnSpacing = 3, RowSpacing = 3 };

    private DateSelection _selection = new(DateOnly.FromDateTime(DateTime.Today), 1, []);
    private List<HeldRangeDto> _held = [];
    private DateOnly _month = FirstOfMonth(DateOnly.FromDateTime(DateTime.Today));
    private bool _readOnly;

    public BookingCalendarView()
    {
        _clear.SetAppThemeColor(Button.TextColorProperty, Brand, BrandDark);
        _status.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#1C1A17"), Color.FromArgb("#ECE7DF"));
        _title.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#1C1A17"), Color.FromArgb("#ECE7DF"));
        foreach (var arrow in new[] { _previous, _next })
        {
            arrow.BackgroundColor = Brand;
            arrow.TextColor = Colors.White;
        }

        _previous.Clicked += (_, _) => MoveMonth(-1);
        _next.Clicked += (_, _) => MoveMonth(1);
        _clear.Clicked += (_, _) =>
        {
            _selection.Clear();
            Rebuild();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        var header = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        header.Add(_previous, 0, 0);
        header.Add(_title, 1, 0);
        header.Add(_next, 2, 0);

        Content = new VerticalStackLayout { Spacing = 6, Children = { header, _grid, _status, _clear } };
        Rebuild();
    }

    /// <summary>Raised whenever the chosen dates change, including when they are cleared.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The rules and the current choice. Start and End are both set once the dates are chosen.</summary>
    public DateSelection Selection => _selection;

    /// <summary>
    /// Shows a tool's calendar. A read-only calendar (an owner looking at their own tool, or a paused tool)
    /// shows what is booked but cannot be picked from. Loading starts a fresh, empty selection.
    /// </summary>
    public void Load(DateOnly today, int maxLoanDays, IEnumerable<HeldRangeDto> held, bool readOnly)
    {
        _held = held.ToList();
        _readOnly = readOnly;
        _selection = new DateSelection(today, maxLoanDays, _held.Select(h => (h.Start, h.End)));
        _month = FirstOfMonth(today);
        Rebuild();
    }

    private DateOnly FirstMonth => FirstOfMonth(_selection.Today);

    private DateOnly LastMonth => FirstOfMonth(_selection.LastBookableDay);

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    private void MoveMonth(int by)
    {
        _month = _month.AddMonths(by);
        Rebuild();
    }

    private void Rebuild()
    {
        _title.Text = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        _previous.IsEnabled = _month > FirstMonth;
        _next.IsEnabled = _month < LastMonth;
        _status.Text = _readOnly ? "Days in colour are already requested or booked." : _selection.Summary();
        _clear.IsVisible = !_readOnly && _selection.Start is not null;

        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        for (var column = 0; column < 7; column++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        var weekdays = new[] { "M", "T", "W", "T", "F", "S", "S" };
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var column = 0; column < 7; column++)
        {
            var label = new Label { Text = weekdays[column], FontSize = 11, HorizontalOptions = LayoutOptions.Center, TextColor = Color.FromArgb("#7A7368") };
            _grid.Add(label, column, 0);
        }

        var blanks = ((int)_month.DayOfWeek + 6) % 7;
        var daysInMonth = DateTime.DaysInMonth(_month.Year, _month.Month);
        var rows = (blanks + daysInMonth + 6) / 7;
        for (var row = 0; row < rows; row++)
        {
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(42)));
        }

        for (var n = 0; n < daysInMonth; n++)
        {
            var day = _month.AddDays(n);
            var slot = blanks + n;
            _grid.Add(DayButton(day), slot % 7, 1 + slot / 7);
        }
    }

    private Button DayButton(DateOnly day)
    {
        var held = _held.FirstOrDefault(h => h.Start <= day && day <= h.End);
        var pickable = !_readOnly && _selection.CanPick(day);
        var isStart = _selection.Start == day;
        var isEnd = _selection.End == day;
        var inRange = _selection.Start is DateOnly start && day >= start &&
                      (_selection.End is DateOnly end ? day <= end : day == start);

        var button = new Button
        {
            Text = day.Day.ToString(CultureInfo.CurrentCulture),
            FontSize = 14,
            Padding = 0,
            CornerRadius = 6,
            BorderWidth = day == _selection.Today ? 2 : 1,
            IsEnabled = pickable
        };

        // Spoken description for screen readers, which would otherwise hear only a number.
        var state = held is not null
            ? (held.Approved ? "booked" : "requested")
            : isStart ? "start of your loan"
            : isEnd ? "last day of your loan"
            : pickable ? "free" : "not available";
        SemanticProperties.SetDescription(button, $"{day.ToString("dddd d MMMM", CultureInfo.CurrentCulture)}, {state}");

        if (isStart || isEnd)
        {
            button.BackgroundColor = Brand;
            button.TextColor = Colors.White;
            button.BorderColor = Brand;
        }
        else if (inRange)
        {
            button.BackgroundColor = Brand.WithAlpha(0.3f);
            button.BorderColor = Brand;
            button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#1C1A17"), Color.FromArgb("#ECE7DF"));
        }
        else if (held is not null)
        {
            var tint = held.Approved ? Booked : Requested;
            button.BackgroundColor = tint.WithAlpha(0.28f);
            button.BorderColor = tint;
            button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#1C1A17"), Color.FromArgb("#ECE7DF"));
        }
        else
        {
            button.BackgroundColor = Colors.Transparent;
            button.SetAppThemeColor(Button.BorderColorProperty, Color.FromArgb("#DDD6CA"), Color.FromArgb("#3A352F"));
            button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#1C1A17"), Color.FromArgb("#ECE7DF"));
            if (!pickable)
            {
                button.Opacity = 0.4;
            }
        }

        if (pickable)
        {
            button.Clicked += (_, _) =>
            {
                if (_selection.Pick(day))
                {
                    Rebuild();
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }
            };
        }

        return button;
    }
}
