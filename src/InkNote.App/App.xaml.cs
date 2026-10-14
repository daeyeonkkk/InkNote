using Microsoft.UI.Xaml;

namespace InkNote.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // 예기치 못한 오류는 %LOCALAPPDATA%\InkNote\crash.log에 남깁니다.
        UnhandledException += (_, e) =>
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkNote");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:O}] {e.Exception}\n\n");
            }
            catch
            {
                // 로그를 못 남겨도 원래 오류 처리를 방해하지 않습니다.
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // "연결 프로그램"이나 명령줄로 PDF 경로가 넘어오면 바로 엽니다.
        string[] cmd = Environment.GetCommandLineArgs();
        string? path = cmd.Skip(1).FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        _window = new MainWindow(path);
        _window.Activate();
    }
}
