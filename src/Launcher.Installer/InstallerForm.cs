using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PerforceLauncher.Installer;

/// <summary>Окно установщика: выбор папки, проверки, затем Velopack Setup с --installto.</summary>
internal sealed class InstallerForm : Form
{
    // Палитра лаунчера
    private static readonly Color Bg = Color.FromArgb(0x0F, 0x0F, 0x11);
    private static readonly Color Surface = Color.FromArgb(0x18, 0x18, 0x1C);
    private static readonly Color Border = Color.FromArgb(0x2A, 0x2A, 0x30);
    private static readonly Color Text1 = Color.FromArgb(0xF4, 0xF4, 0xF5);
    private static readonly Color Text2 = Color.FromArgb(0xB4, 0xB4, 0xBD);
    private static readonly Color Muted = Color.FromArgb(0x7A, 0x7A, 0x85);
    private static readonly Color Warn = Color.FromArgb(0xF5, 0xB5, 0x47);
    private static readonly Color Accent = Color.FromArgb(0xF2, 0xF2, 0xF3);

    private readonly TextBox _path = new();
    private readonly Label _space = new();
    private readonly Label _error = new();
    private readonly Button _install = new();
    private readonly Button _cancel = new();
    private readonly Button _browse = new();
    private readonly string? _existing = InstallPlan.ExistingLocation();
    private readonly string _version;

    public InstallerForm()
    {
        _version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";
        Text = "Install Perforce Launcher";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Bg;
        ForeColor = Text1;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 392);
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            if (s != null) Icon = new Icon(s);

        var logo = new PictureBox { Location = new Point(32, 28), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
        if (Icon != null) logo.Image = new Icon(Icon, 48, 48).ToBitmap();
        var title = new Label { Text = "Perforce Launcher", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(92, 26) };
        var subtitle = new Label
        {
            Text = (_version.Length > 0 ? $"Version {_version}  ·  " : "") + "Perforce + Unreal Engine for your team",
            ForeColor = Muted, AutoSize = true, Location = new Point(94, 58),
        };

        var caption = new Label { Text = "INSTALL LOCATION", Font = new Font("Segoe UI Semibold", 8.25f), ForeColor = Muted, AutoSize = true, Location = new Point(32, 112) };
        _path.SetBounds(32, 136, 424, 30);
        _path.BorderStyle = BorderStyle.FixedSingle;
        _path.BackColor = Surface;
        _path.ForeColor = Text1;
        _path.Font = new Font("Segoe UI", 10.5f);
        _path.Text = _existing ?? InstallPlan.DefaultLocation;
        _path.TextChanged += (_, _) => Revalidate();

        StyleButton(_browse, "Browse…", primary: false);
        _browse.SetBounds(466, 134, 102, 32);
        _browse.Click += (_, _) => Browse();

        var info = new Label
        {
            Text = _existing != null
                ? "Perforce Launcher is already installed here. Installing again updates it; your projects, settings and saved sign-in are kept."
                : "Installs for your Windows account only, no administrator rights needed. Updates download and install automatically.",
            ForeColor = Text2, Location = new Point(32, 180), Size = new Size(536, 44),
        };
        _space.ForeColor = Muted;
        _space.SetBounds(32, 226, 536, 20);
        _error.ForeColor = Warn;
        _error.SetBounds(32, 254, 536, 58);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 70, BackColor = Color.FromArgb(0x0A, 0x0A, 0x0C) };
        footer.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        StyleButton(_install, _existing != null ? "Reinstall" : "Install", primary: true);
        StyleButton(_cancel, "Cancel", primary: false);
        _install.SetBounds(372, 18, 110, 34);
        _cancel.SetBounds(490, 18, 80, 34);
        _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => Close();
        footer.Controls.AddRange(new Control[] { _install, _cancel });

        Controls.AddRange(new Control[] { logo, title, subtitle, caption, _path, _browse, info, _space, _error, footer });
        AcceptButton = _install;
        CancelButton = _cancel;
        Shown += (_, _) => { _path.SelectionStart = _path.TextLength; _install.Focus(); };
        Revalidate();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Тёмный заголовок окна, как у самого лаунчера (Windows 10 2004+ / 11).</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
    }

    private static void StyleButton(Button b, string text, bool primary)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.BorderSize = 1;
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.FromArgb(0x11, 0x11, 0x13);
            b.FlatAppearance.BorderColor = Accent;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xE2, 0xE2, 0xE5);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xD0, 0xD0, 0xD5);
            b.Font = new Font("Segoe UI Semibold", 9.75f);
        }
        else
        {
            b.BackColor = Surface;
            b.ForeColor = Text1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x22, 0x22, 0x27);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0x1A, 0x1A, 0x1E);
        }
    }

    private void Revalidate()
    {
        var error = InstallPlan.Validate(_path.Text, out _);
        _error.Text = error ?? "";
        _space.Text = InstallPlan.FreeSpaceText(_path.Text);
        _install.Enabled = error == null;
        _install.BackColor = error == null ? Accent : Color.FromArgb(0x3A, 0x3A, 0x40);
    }

    private void Browse()
    {
        var start = Directory.Exists(_path.Text) ? _path.Text : Path.GetDirectoryName(_path.Text.TrimEnd('\\'));
        var picked = FolderPicker.Pick(Handle, start, "Choose where to install Perforce Launcher");
        if (picked == null) return;
        // Как в обычных установщиках: выбрали родительскую папку — ставим в её подпапку PerforceLauncher.
        if (!string.Equals(Path.GetFileName(picked.TrimEnd('\\')), "PerforceLauncher", StringComparison.OrdinalIgnoreCase))
            picked = Path.Combine(picked, "PerforceLauncher");
        _path.Text = picked;
    }

    private async Task InstallAsync()
    {
        if (InstallPlan.Validate(_path.Text, out var target) is { } error) { _error.Text = error; return; }

        var setup = ExtractSetup();
        if (setup == null)
        {
            _error.Text = "This is a development build without the embedded installer. Download the installer from the GitHub release.";
            return;
        }

        SetBusy(true, "Installing… follow the progress window.");
        int exitCode;
        try
        {
            // Путь без завершающего «\»: иначе «\"» в командной строке экранирует кавычку.
            var psi = new ProcessStartInfo(setup, $"--installto \"{target.TrimEnd('\\')}\"") { UseShellExecute = false };
            exitCode = await Task.Run(() =>
            {
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                return p.ExitCode;
            });
        }
        catch (Exception ex)
        {
            SetBusy(false, "Couldn't start the installer: " + ex.Message);
            return;
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(setup)!, recursive: true); } catch { }
        }

        if (exitCode == 0) { Close(); return; }   // Velopack сам запускает лаунчер после установки
        SetBusy(false, $"Installation didn't finish (code {exitCode}). Try again, or install to another folder.");
    }

    private void SetBusy(bool busy, string message)
    {
        _install.Enabled = _browse.Enabled = _path.Enabled = !busy;
        _cancel.Enabled = !busy;
        _error.ForeColor = busy ? Text2 : Warn;
        _error.Text = message;
        UseWaitCursor = busy;
    }

    /// <summary>Достаёт вшитый Velopack Setup.exe во временную папку.</summary>
    private static string? ExtractSetup()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VelopackSetup.exe");
        if (s == null) return null;
        var dir = Path.Combine(Path.GetTempPath(), "PerforceLauncherSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "PerforceLauncher-Setup.exe");
        using (var f = File.Create(path)) s.CopyTo(f);
        return path;
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm());
    }
}
