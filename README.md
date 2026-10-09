# Perforce Launcher

Лаунчер для Perforce + Unreal Engine (.NET 9, WPF, тема Fluent). Следит за новыми коммитами,
запускает редактор и IDE, управляет pending changes и показывает историю — для нескольких проектов/воркспейсов.

## Установка
Скачать **`PerforceLauncher-win-Setup.exe`** из [последнего релиза](https://github.com/olegsheyko/odius-PerforceLauncher/releases/latest)
и запустить. Права администратора и .NET ставить не нужно. Понадобится только `p4.exe`
(ставится вместе с P4V или как Helix Command-Line Client).

Windows может показать «Windows защитила ваш компьютер» (установщик не подписан): **Подробнее → Выполнить в любом случае**.

Дальше лаунчер **обновляется сам**: раз в несколько часов проверяет релизы, скачивает только изменения
и ставит новую версию, когда окно скрыто в трей (или по «Restart to Update» в трее / Settings).
Удаление — через «Параметры → Приложения». Есть и Portable-архив без установки (без автообновления).

## Выпуск новой версии
```
git tag v1.2.3
git push origin v1.2.3
```
GitHub Actions (`.github/workflows/release.yml`) соберёт установщик и опубликует релиз; у всей команды он появится сам.

## Сборка для разработки
```
powershell -File build-exe.ps1      # -> dist\PerforceLauncher.exe (не обновляется автоматически)
```
Нужны .NET 9 SDK и `p4` в PATH (или путь в Settings).

## Возможности
- **Проекты** (сайдбар): статус каждого, бейдж с числом новых коммитов.
- **Статус**: Up to date / N new commits / Sign in required; сводка Synced · On server · Local changes.
- **Launch Unreal** (Ctrl+Enter) с версией движка, **Open in Rider / Visual Studio**, **Generate Project Files**, свои команды.
- **Get Latest** с отменой; после синка — подсказка перегенерировать project files, если изменились `.Build.cs`/`.Target.cs`/`.uproject`/`.uplugin`.
- **Incoming / Pending / History**: submit, reconcile, changelist'ы, revert; предупреждение ⚠ для файлов из `.p4ignore`, уже лежащих в депо;
  история с поиском, файлами коммита и «Sync Workspace to This Changelist…».
- **Трей**: один клик — окно; меню «Launch Unreal» для каждого проекта; уведомления, напоминание после входа/сна.
- Один экземпляр (повторный запуск показывает окно), запоминание положения окна, журнал операций (Ctrl+L), F5 — проверить сейчас.

## Архитектура

| Слой | Что внутри | Как расширять |
|---|---|---|
| `Launcher.Core/VersionControl` | `IVersionControl` + `P4VersionControl` (p4 CLI, `-ztag -Mj`), `P4Locator` | Новая VCS = ещё одна реализация интерфейса |
| `Launcher.Core/Launching` | `ILaunchAction`, `LaunchActionRegistry`, `EngineLocator`, `IdeDetector`, `ShellHelper` | Новая кнопка = класс `ILaunchAction` + регистрация в `CreateDefault()`. Свои команды — в профиле (токены `{Root} {UProject} {Solution} {Client} {Engine}`) |
| `Launcher.Core/Services` | `SettingsStore` (JSON в %AppData%\PerforceLauncher), `ChangeWatcher`, `AutostartManager`, `INotifier` | Другие уведомления = реализация `INotifier` |
| `Launcher.App` | MVVM: `MainViewModel`, `ProfileViewModel`, окна, трей, тема в `App.xaml` | Палитра и стили — ресурсы в `App.xaml` |

## Как определяются новые коммиты
`#have` даёт нижнюю границу синхронизации, `p4 cstat` — статус каждого changelist'а (have / partial / need).
Так корректно учитываются changelist'ы, состоящие только из удалений.

## Безопасность
p4 запускается только по абсолютному пути (не из папки приложения и не из «.»), аргументы — через `ArgumentList`,
значения профиля с ведущим `-` отклоняются, описание changelist'а не может подменить поля спецификации.
Пароль идёт в `p4 login` через stdin и не сохраняется. Свои команды из профиля требуют подтверждения при первом запуске.
Единственная запись в реестр — автозапуск (`HKCU\...\Run`), только по галочке в Settings.
