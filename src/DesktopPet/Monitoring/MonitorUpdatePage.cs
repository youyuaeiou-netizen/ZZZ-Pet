using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using DesktopPet.Monitoring;
using DesktopPet.Tools;
using W = System.Windows.Controls;

namespace DesktopPet;

internal sealed class MonitorUpdatePage : W.StackPanel
{
    private PetUpdateManifest? _update;
    public MonitorUpdatePage(MonitorService service)
    {
        Children.Add(ToolsWindow.Text("Pet 整包更新", 16));
        Children.Add(MonitorSettings.Text(service, "更新来源（HTTPS）", () => service.Config.UpdateManifestUrl ?? "",
            value => { service.Config.UpdateManifestUrl = value.Length == 0 ? null : value; _update = null; },
            value => value.Length == 0 || PetUpdateManifest.ValidUrl(value), "请输入不含用户凭据的 HTTPS 地址。", 2048));
        var status = ToolsWindow.Text(""); Children.Add(status);
        var check = ToolsWindow.Button("检查 Pet 更新", async () =>
        {
            if (service.Config.UpdateManifestUrl is not string endpoint) { status.Text = "尚未配置更新来源。"; return; }
            status.Text = "正在检查…";
            try
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 128 * 1024 };
                using var response = await http.GetAsync(endpoint); response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException();
                var update = PetUpdateManifest.Parse(await response.Content.ReadAsStringAsync());
                var version = typeof(MonitorWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
                _update = PetUpdateManifest.IsNewer(update.Version, version) ? update : null;
                status.Text = _update is null ? "已是当前版本。" : $"Pet {update.Version}\n{update.Notes}\nSHA-256: {update.Sha256}";
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or System.Text.Json.JsonException)
            { _update = null; status.Text = "更新检查失败，当前 Pet 继续运行。"; }
        }); Children.Add(check);
        Children.Add(ToolsWindow.Button("打开整包下载", () =>
        {
            if (_update is null) { status.Text = "请先检查可用更新。"; return; }
            try { Process.Start(new ProcessStartInfo(_update.PackageUrl) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { status.Text = "无法打开默认浏览器。"; }
        }));
    }
}
