using System;
using PerforceLauncher.Core.Services;
using Velopack;

namespace PerforceLauncher.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Должно идти первым: при установке/обновлении/удалении установщик запускает exe со служебными
        // аргументами — Velopack обрабатывает их здесь и завершает процесс, не открывая окно.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                // При удалении убираем за собой автозапуск и сохранённые пароли Perforce.
                try { AutostartManager.Apply(false); } catch { }
                try
                {
                    var store = new WindowsCredentialStore();
                    foreach (var target in store.List(SessionKeeper.TargetPrefix)) store.Delete(target);
                }
                catch { }
            })
            .SetAutoApplyOnStartup(true)   // скачанное обновление ставится при следующем запуске
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
