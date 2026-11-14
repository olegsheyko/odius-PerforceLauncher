using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.ViewModels;

/// <summary>Подпись месяца над картой: текст и отступ слева в пикселях.</summary>
public sealed record MonthLabel(string Text, double X);

/// <summary>Одна клетка карты: день и сколько в него было сабмитов.</summary>
public sealed class ActivityCellViewModel
{
    public ActivityCellViewModel(DateTime date, int count, int level, bool visible)
    {
        Date = date;
        Count = count;
        Level = level;
        IsVisible = visible;
        Brush = ActivityViewModel.LevelBrushes[Math.Clamp(level, 0, 4)];
        Tooltip = !visible ? "" : count == 0 ? $"No commits on {date:ddd, d MMM yyyy}" : $"{count} commit{(count == 1 ? "" : "s")} on {date:ddd, d MMM yyyy}";
    }

    public DateTime Date { get; }
    public int Count { get; }
    public int Level { get; }
    public bool IsVisible { get; }
    public Brush Brush { get; }
    public string Tooltip { get; }
}

/// <summary>
/// Карта частоты сабмитов, как «contributions» на GitHub: 53 недели по 7 дней (неделя с понедельника), чем больше коммитов, тем ярче клетка.
/// Можно смотреть всю команду или только свои коммиты.
/// </summary>
public sealed class ActivityViewModel : ObservableObject
{
    public const int Weeks = 53;
    /// <summary>Шаг клеток в пикселях (клетка 10 + отступы по 1 с обеих сторон).</summary>
    public const double CellPitch = 12;
    private static readonly string[] DayLabels = { "Mon", "", "Wed", "", "Fri", "", "" };

    /// <summary>Яркость клетки по уровню 0..4: от еле заметной до платиновой (в тон остальному интерфейсу).</summary>
    public static readonly Brush[] LevelBrushes = Enumerable.Range(0, 5).Select(i =>
    {
        byte[] alpha = { 0x14, 0x44, 0x78, 0xB4, 0xF2 };
        var b = new SolidColorBrush(Color.FromArgb(alpha[i], 0xF2, 0xF2, 0xF3));
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    private IReadOnlyList<CommitActivity> _commits = Array.Empty<CommitActivity>();
    private string _me = "";
    private bool _mineOnly;
    private bool _busy;
    private bool _loaded;
    private string _error = "";
    private string _summary = "";
    private IReadOnlyList<ActivityCellViewModel> _cells = Array.Empty<ActivityCellViewModel>();
    private IReadOnlyList<MonthLabel> _monthLabels = Array.Empty<MonthLabel>();
    private DateTime _loadedAt = DateTime.MinValue;

    public IReadOnlyList<string> RowLabels => DayLabels;
    public IReadOnlyList<ActivityCellViewModel> Cells { get => _cells; private set => Set(ref _cells, value); }
    /// <summary>Подпись месяца над первой неделей месяца, остальные пустые.</summary>
    public IReadOnlyList<MonthLabel> MonthLabels { get => _monthLabels; private set => Set(ref _monthLabels, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
    public bool IsLoaded { get => _loaded; private set => Set(ref _loaded, value); }
    public string Error { get => _error; set => Set(ref _error, value); }
    public DateTime LoadedAt => _loadedAt;

    /// <summary>Только мои коммиты (иначе вся команда).</summary>
    public bool MineOnly
    {
        get => _mineOnly;
        set { if (Set(ref _mineOnly, value)) Rebuild(); }
    }

    public string Me => _me;

    /// <summary>Первое число видимого диапазона: понедельник за 52 недели до текущей недели.</summary>
    public static DateTime RangeStart(DateTime today) => MondayOf(today).AddDays(-7 * (Weeks - 1));

    private static DateTime MondayOf(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public void SetData(IReadOnlyList<CommitActivity> commits, string me)
    {
        _commits = commits;
        _me = me;
        _loadedAt = DateTime.UtcNow;
        IsLoaded = true;
        Error = "";
        Rebuild();
    }

    public void Clear()
    {
        _commits = Array.Empty<CommitActivity>();
        IsLoaded = false;
        Error = "";
        Summary = "";
        Cells = Array.Empty<ActivityCellViewModel>();
        MonthLabels = Array.Empty<MonthLabel>();
        _loadedAt = DateTime.MinValue;
    }

    private void Rebuild() => Build(DateTime.Today);

    internal void Build(DateTime today)
    {
        if (!IsLoaded) return;
        var start = RangeStart(today);
        var shown = _mineOnly && _me.Length > 0 ? _commits.Where(c => string.Equals(c.User, _me, StringComparison.OrdinalIgnoreCase)) : _commits;
        var perDay = shown.Where(c => c.Time.Date >= start && c.Time.Date <= today).GroupBy(c => c.Time.Date).ToDictionary(g => g.Key, g => g.Count());

        // Уровни яркости по квартилям непустых дней: один «рекордный» день не делает все остальные бледными.
        var nonZero = perDay.Values.OrderBy(v => v).ToList();
        int Threshold(double q) => nonZero.Count == 0 ? 0 : nonZero[Math.Min(nonZero.Count - 1, (int)(nonZero.Count * q))];
        int t1 = Threshold(0.25), t2 = Threshold(0.5), t3 = Threshold(0.75);
        int LevelOf(int c) => c == 0 ? 0 : c <= t1 ? 1 : c <= t2 ? 2 : c <= t3 ? 3 : 4;

        // Построчно (понедельник..воскресенье), в каждой строке 53 недели: так их раскладывает UniformGrid.
        var cells = new List<ActivityCellViewModel>(7 * Weeks);
        for (var row = 0; row < 7; row++)
            for (var week = 0; week < Weeks; week++)
            {
                var date = start.AddDays(week * 7 + row);
                var visible = date <= today;
                var count = visible && perDay.TryGetValue(date, out var c) ? c : 0;
                cells.Add(new ActivityCellViewModel(date, count, visible ? LevelOf(count) : 0, visible));
            }
        Cells = cells;

        var labels = new List<MonthLabel>();
        var lastMonth = -1;
        for (var week = 0; week < Weeks; week++)
        {
            var monday = start.AddDays(week * 7);
            // Подпись ставим на неделю, где месяц начался; в самом начале, если месяц почти закончился, пропускаем.
            if (monday.Month != lastMonth && !(week == 0 && monday.Day > 20))
                labels.Add(new MonthLabel(monday.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture), week * CellPitch));
            lastMonth = monday.Month;
        }
        MonthLabels = labels;

        var total = perDay.Values.Sum();
        var activeDays = perDay.Count;
        var streak = 0;
        // Текущая серия: подряд идущие дни с коммитами, считая от сегодня (если сегодня ещё пусто, то от вчера).
        for (var d = perDay.ContainsKey(today) ? today : today.AddDays(-1); perDay.ContainsKey(d); d = d.AddDays(-1)) streak++;
        var who = _mineOnly && _me.Length > 0 ? "Your" : "Team";
        Summary = total == 0
            ? $"{who} commits: none in the last year"
            : $"{who} commits: {total} in the last year · {activeDays} active day{(activeDays == 1 ? "" : "s")}" + (streak > 1 ? $" · {streak}-day streak" : "");
    }
}
