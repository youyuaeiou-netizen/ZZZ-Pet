using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Monitoring;

namespace DesktopPet;

public sealed class MonitorWebService : IDisposable
{
    private MonitorHttpServer? _listener;
    private bool _lan, _ipv6;
    private int _port;
    private readonly string _session = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private byte[] _snapshot = "{}"u8.ToArray();
    public string? Error { get; private set; }
    public bool Running => _listener is not null;
    public string Url => $"http://127.0.0.1:{_port}/?session={_session}";
    public string UrlForHost(string host) => $"http://{host}:{_port}/?session={_session}";
    public void Configure(bool enabled, int port, List<MonitorMetric> metrics, MonitorConfig? config = null, List<MonitorDevice>? devices = null)
    {
        config ??= new();
        var language = MonitorLocalizer.Language;
        Volatile.Write(ref _snapshot, JsonSerializer.SerializeToUtf8Bytes(new { Timestamp = DateTimeOffset.UtcNow,
            Language = config.Language, Title = language.Text(config.Language, "艾莲布 · 电脑状态"), Disconnected = language.Text(config.Language, "已断开"),
            Background = config.Background, Foreground = config.Foreground, FontSize = config.FontSize, FontFamily = config.FontFamily,
            Visual = config.Visual, RowSpacing = config.RowSpacing, PanelWidth = config.PanelWidth, UiScale = config.UiScale, Opacity = config.Opacity,
            Items = metrics.Select(m => new { m.Id, m.DeviceId, Group = devices?.FirstOrDefault(d => d.Id == m.DeviceId)?.Name ?? m.DeviceId,
                Name = language.Metric(config.Language, m), m.Value, m.Unit, m.SampledAt,
                Display = language.Text(config.Language, m.Display), Color = MonitorTheme.Color(m, config), Bar = MonitorTheme.BarPercent(m, config),
                BarColor = MonitorTheme.Level(m, config) switch { 2 => config.Visual.BarHigh, 1 => config.Visual.BarMid, _ => config.Visual.BarLow } }) }, MonitorJson.Options));
        if (!enabled) { Stop(); return; }
        if (Running && _port == port && _lan == config.WebLan && _ipv6 == config.WebIpv6) return;
        Stop();
        if (port is < 1024 or > 65535 || port is 8085 or 5173) { Error = "该端口保留给现有服务，请选择其他端口。"; return; }
        try
        {
            _listener = new MonitorHttpServer(port, config.WebLan, config.WebIpv6, Response);
            _port = port; _lan = config.WebLan; _ipv6 = config.WebIpv6; Error = null;
        }
        catch (Exception e) when (e is SocketException or InvalidOperationException) { Error = "网页监控启动失败，端口可能已占用或权限不足。"; }
    }
    private (int, string, byte[]) Response(string path, string query, string method)
    {
        var supplied = query.TrimStart('?').Split('&').FirstOrDefault(p => p.StartsWith("session=", StringComparison.Ordinal))?[8..] ?? "";
        supplied = Uri.UnescapeDataString(supplied);
        if (method != "GET" || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_session))) return (403, "text/plain", []);
        return path switch { "/api/snapshot" => (200, "application/json", Volatile.Read(ref _snapshot)), "/" => (200, "text/html", Encoding.UTF8.GetBytes(Html)), _ => (404, "text/plain", []) };
    }
    private const string Html = """
        <!doctype html><html lang="zh"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>艾莲布 · 电脑状态</title><style>body{background:#a7f3dc;color:#55416b;font:13px 'Microsoft YaHei',sans-serif;padding:24px}
        main{max-width:700px;margin:auto;background:#fffbd8;border:3px solid #55416b;padding:22px;box-shadow:6px 6px #ffd5e8}
        h1{font-size:16px;margin:0 0 12px}.row{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:4px 10px}.label{overflow-wrap:anywhere}
        .bar{grid-column:1/-1;height:4px;overflow:hidden}.fill{height:100%}.group h2{margin:0 0 6px}small{display:block;margin-top:16px}</style>
        <main><h1>艾莲布 · 电脑状态</h1><div id="rows"></div><small id="status">连接中…</small></main>
        <script>const key=new URLSearchParams(location.search).get('session'),values=new Map();let disconnected='已断开',generation=0;
        function element(tag,text,cls){const e=document.createElement(tag);if(text!=null)e.textContent=text;if(cls)e.className=cls;return e}
        function missing(){generation++;document.getElementById('status').textContent=disconnected;for(const v of document.querySelectorAll('.value'))v.textContent=disconnected;for(const b of document.querySelectorAll('.bar'))b.hidden=true;values.clear()}
        async function update(){try{const response=await fetch('/api/snapshot?session='+encodeURIComponent(key));if(!response.ok)throw Error();
        const data=await response.json(),v=data.Visual,epoch=++generation;disconnected=data.Disconnected;document.documentElement.lang=data.Language;document.querySelector('h1').textContent=data.Title;document.title=data.Title;
        const main=document.querySelector('main'),title=document.querySelector('h1'),scale=data.UiScale||1;main.style.background=data.Background;main.style.padding=v.Padding*scale+'px';main.style.borderRadius=v.CornerRadius*scale+'px';main.style.maxWidth=data.PanelWidth*scale+'px';main.style.opacity=data.Opacity;
        document.body.style.color=data.Foreground;document.body.style.fontFamily=data.FontFamily;document.body.style.fontSize=data.FontSize*scale+'px';title.style.fontSize=v.TitleSize*scale+'px';title.style.color=v.TitleColor;
        const rows=document.getElementById('rows');rows.replaceChildren();let target=rows,last=null;const active=new Set();
        for(const m of data.Items||[]){active.add(m.Id);if(v.ShowGroups&&last!==m.DeviceId){last=m.DeviceId;target=element('section',null,'group');target.style.background=v.GroupBackground;target.style.padding=v.GroupPadding*scale+'px';target.style.borderRadius=v.GroupRadius*scale+'px';target.style.marginBottom=v.GroupSpacing*scale+'px';
        const heading=element('h2',m.Group||m.DeviceId||m.Name);heading.style.color=v.GroupColor;heading.style.fontSize=v.GroupSize*scale+'px';heading.style.marginTop=v.GroupTitleOffset*scale+'px';heading.style.marginBottom=v.GroupBottom*scale+'px';target.append(heading);rows.append(target)}
        const row=element('div',null,'row');row.style.minHeight=v.RowHeight*scale+'px';row.style.marginBottom=data.RowSpacing*scale+'px';row.style.fontWeight=v.Bold?'bold':'normal';const label=element('span',m.Name,'label'),value=element('span',m.Display,'value');value.style.color=m.Color;value.style.fontFamily=v.ValueFamily;value.style.fontSize=v.ValueSize*scale+'px';row.append(label,value);
        if(v.ShowBars&&m.Bar!=null){const bar=element('div',null,'bar'),fill=element('div',null,'fill');bar.style.background=v.BarBackground;fill.style.background=m.BarColor;fill.style.width=m.Bar+'%';if(v.SmoothValues)fill.style.transition='width '+v.SmoothMs+'ms';bar.append(fill);row.append(bar)}
        if(v.SmoothValues&&m.Value!=null&&values.has(m.Id)&&!m.Unit.includes('B/s')&&/^[-+\d.,\s%℃°CFPS]+$/.test(m.Display)){const from=values.get(m.Id),start=performance.now();function animate(now){if(!value.isConnected||epoch!==generation)return;const f=Math.min(1,(now-start)/v.SmoothMs);value.textContent=(from+(m.Value-from)*f).toLocaleString(data.Language,{maximumFractionDigits:2})+' '+m.Unit;if(f<1)requestAnimationFrame(animate);else value.textContent=m.Display}requestAnimationFrame(animate)}
        if(m.Value!=null)values.set(m.Id,m.Value);else values.delete(m.Id);target.append(row)}for(const id of values.keys())if(!active.has(id))values.delete(id);
        document.getElementById('status').textContent=new Date(data.Timestamp).toLocaleTimeString(data.Language);if(Date.now()-new Date(data.Timestamp)>5000)missing()}
        catch{missing()}}update();setInterval(update,1000)</script></html>
        """;
    public void Stop() { _listener?.Dispose(); _listener = null; }
    public void Dispose() => Stop();
}
