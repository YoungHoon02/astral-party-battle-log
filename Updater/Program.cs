using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AstralPartyBattleLog.Updater;

// 게임 프로세스에 네트워크와 창을 넣지 않으려고 따로 띄우는 확인 도구.
// 플러그인은 게임을 켤 때 --auto로 부르고, 이용자는 직접 실행해 언제든 확인할 수 있다.
internal static class Program
{
    private const string Title = "Astral Party Battle Log";
    private const string PluginDll = "AstralPartyBattleLog.dll";
    private const string SkipFile = "update-skip.txt";

    [STAThread]
    private static int Main(string[] args)
    {
        bool auto = Array.IndexOf(args, "--auto") >= 0;
        // 게임을 연달아 껐다 켜면 알림 창이 겹쳐 뜬다.
        using var single = new Mutex(true, "AstralPartyBattleLogUpdater", out bool first);
        if (!first) return 0;

        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string dir = AppDomain.CurrentDomain.BaseDirectory;
        Version current = InstalledVersion(dir);
        string shown = ReleaseIndex.Normalize(current).ToString();

        string? latest;
        try
        {
            latest = ReleaseIndex.Newer(Fetch(shown), current);
        }
        catch (Exception e)
        {
            if (!auto) MessageBox.Show($"새 버전을 확인하지 못했습니다.\n\n{e.Message}", Title,
                                       MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        if (latest is null)
        {
            if (!auto) MessageBox.Show($"최신 버전입니다 (v{shown}).", Title,
                                       MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        string skipPath = Path.Combine(dir, SkipFile);
        if (auto && ReadSkipped(skipPath) == latest) return 0;

        using var alert = new AlertForm(latest, shown);
        switch (alert.ShowDialog())
        {
            case DialogResult.OK:
                Process.Start(ReleaseIndex.ReleasesPage);
                break;
            case DialogResult.Ignore:
                try { File.WriteAllText(skipPath, latest); } catch { }
                break;
        }
        return 0;
    }

    // 플러그인 DLL의 버전이 기준이다. 확인 도구만 따로 남은 설치에서도 실제 설치본과 비교하게 한다.
    private static Version InstalledVersion(string dir)
    {
        try
        {
            return AssemblyName.GetAssemblyName(Path.Combine(dir, PluginDll)).Version;
        }
        catch
        {
            return Assembly.GetExecutingAssembly().GetName().Version;
        }
    }

    private static byte[] Fetch(string version)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AstralPartyBattleLogUpdater/{version}");
        using HttpResponseMessage response = http
            .GetAsync(ReleaseIndex.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ReleaseIndex.MaxBytes) throw new InvalidDataException("index too large");

        using Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        var buffer = new byte[ReleaseIndex.MaxBytes + 1];
        int total = 0, read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        if (total > ReleaseIndex.MaxBytes) throw new InvalidDataException("index too large");
        var json = new byte[total];
        Array.Copy(buffer, json, total);
        return json;
    }

    private static string? ReadSkipped(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}
