using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.ViewModels;

/// <summary>Строка вкладки «Checked Out»: один открытый файл.</summary>
public sealed class CheckoutFileViewModel
{
    public CheckoutFileViewModel(CheckedOutFile file, string folder, Action<string?> copy)
    {
        File = file;
        Folder = folder;
        CopyPathCommand = new RelayCommand(_ => copy(file.DepotPath));
        CopyNameCommand = new RelayCommand(_ => copy(file.FileName));
    }

    public System.Windows.Input.ICommand CopyPathCommand { get; }
    public System.Windows.Input.ICommand CopyNameCommand { get; }

    public CheckedOutFile File { get; }
    public string Name => File.FileName;
    /// <summary>Папка без общего для всех файлов начала пути (//depot/ветка/).</summary>
    public string Folder { get; }
    public string DepotPath => File.DepotPath;
    public bool IsLocked => File.IsLocked;
    public string Action => File.Action;
    public string ChangeText => File.Change == 0 ? "default" : $"CL {File.Change}";
    public string Client => File.Client;
    public string Detail => $"#{File.Rev} · {File.Type}";

    /// <summary>Цвет пометки действия: правка — нейтральный, добавление — зелёный, удаление — красный, остальное — янтарный.</summary>
    public Brush ActionBrush => File.Action switch
    {
        "edit" => Brushes.Silver,
        "add" or "branch" or "import" => Ok,
        "delete" or "move/delete" => Danger,
        _ => Warn,
    };

    private static readonly Brush Ok = Frozen(0x3D, 0xD6, 0x8C);
    private static readonly Brush Danger = Frozen(0xF2, 0x55, 0x5A);
    private static readonly Brush Warn = Frozen(0xF5, 0xB5, 0x47);

    internal static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Группа вкладки «Checked Out»: все файлы одного пользователя; раскрывается и сворачивается.</summary>
public sealed class UserCheckoutViewModel : ObservableObject
{
    private bool _isExpanded;

    public UserCheckoutViewModel(string user, bool isMe, IReadOnlyList<CheckoutFileViewModel> files, bool expanded, Action<string?> copy)
    {
        CopyUserCommand = new RelayCommand(_ => copy(user));
        User = user;
        IsMe = isMe;
        Files = files;
        _isExpanded = expanded;
    }

    public string User { get; }
    public System.Windows.Input.ICommand CopyUserCommand { get; }
    public bool IsMe { get; }
    public IReadOnlyList<CheckoutFileViewModel> Files { get; }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public string Initials
    {
        get
        {
            var parts = User.Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}"
                : User.Length >= 2 ? User[..2].ToUpper() : User.ToUpper();
        }
    }

    /// <summary>Цвет аватарки стабилен для пользователя (не зависит от порядка в списке).</summary>
    public Brush AvatarBrush
    {
        get
        {
            var hash = User.ToLowerInvariant().Aggregate(17, (h, c) => unchecked(h * 31 + c));
            var hue = (uint)hash % 360;
            return new SolidColorBrush(FromHsv(hue, 0.45, 0.62));
        }
    }

    public int Count => Files.Count;
    public string CountText => Files.Count == 1 ? "1 file" : $"{Files.Count} files";
    public int LockedCount => Files.Count(f => f.IsLocked);
    public string LockedText => $"{LockedCount} locked";
    public bool HasLocked => LockedCount > 0;

    /// <summary>Рабочие пространства пользователя: «oleg_StealDrive» или «3 workspaces».</summary>
    public string WorkspacesText
    {
        get
        {
            var clients = Files.Select(f => f.Client).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return clients.Count == 1 ? clients[0] : $"{clients.Count} workspaces";
        }
    }

    public string WorkspacesTooltip => string.Join(Environment.NewLine,
        Files.GroupBy(f => f.Client, StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key}: {g.Count()}"));

    public override string ToString() => User;

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
