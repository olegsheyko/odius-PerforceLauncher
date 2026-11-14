using System;
using System.Collections.Generic;
using System.Linq;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.Services;

namespace PerforceLauncher.App.Infrastructure;

/// <summary>Последние описания коммитов и шаблоны: выбираются из меню в окне описания, чтобы не писать одно и то же.</summary>
public sealed class DescriptionBook
{
    private const int MaxRecent = 15;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;

    public DescriptionBook(AppSettings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
    }

    public IReadOnlyList<string> Recent => _settings.RecentDescriptions;
    public IReadOnlyList<DescriptionTemplate> Templates => _settings.DescriptionTemplates;

    /// <summary>Запомнить описание отправленного коммита (одинаковые не дублируются, свежее выше).</summary>
    public void Remember(string description)
    {
        var d = description.Trim();
        if (d.Length == 0) return;
        _settings.RecentDescriptions.RemoveAll(x => string.Equals(x, d, StringComparison.Ordinal));
        _settings.RecentDescriptions.Insert(0, d);
        if (_settings.RecentDescriptions.Count > MaxRecent) _settings.RecentDescriptions.RemoveRange(MaxRecent, _settings.RecentDescriptions.Count - MaxRecent);
        Save();
    }

    public void SaveTemplate(string name, string text)
    {
        name = name.Trim();
        if (name.Length == 0 || text.Trim().Length == 0) return;
        var existing = _settings.DescriptionTemplates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.Text = text.Trim();
        else _settings.DescriptionTemplates.Add(new DescriptionTemplate { Name = name, Text = text.Trim() });
        Save();
    }

    public void DeleteTemplate(string name)
    {
        _settings.DescriptionTemplates.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Save()
    {
        try { _store.Save(_settings); } catch { /* шаблоны не критичны: не мешаем работе, если файл настроек недоступен */ }
    }
}
