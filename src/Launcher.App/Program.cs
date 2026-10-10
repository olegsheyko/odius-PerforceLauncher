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
                // Убираем автозапуск (иначе он указывал бы на удалённый exe). Сохранённые пароли НЕ трогаем:
                // этот хук срабатывает и при переустановке поверх, и стирал пароль, ломая автовход.
                // Удалить пароль можно в Settings → Forget Saved Passwords или в Диспетчере учётных данных.
                try { AutostartManager.Apply(false); } catch { }
                DiagLog.Write("Uninstall/reinstall: autostart entry removed (saved passwords kept).");
            })
            .SetAutoApplyOnStartup(true)   // скачанное обновление ставится при следующем запуске
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
