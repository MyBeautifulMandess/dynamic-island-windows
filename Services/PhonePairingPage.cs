using System.Net;

namespace DynamicIsland.Services;

/// <summary>
/// Страница сопряжения, которую телефон открывает в браузере.
///
/// Камера телефона умеет распознавать QR-код, но не всякая открывает
/// нестандартную схему <c>dilink://</c> — некоторые только предлагают
/// скопировать ссылку. Поэтому в QR кладётся обычный http-адрес этой
/// страницы: камера гарантированно откроет браузер, а страница сама
/// запустит приложение по схеме <c>dilink://</c> и покажет данные для
/// ручного ввода, если автоматический запуск не сработал.
/// </summary>
internal static class PhonePairingPage
{
    /// <summary>Путь страницы сопряжения на локальном сервере.</summary>
    public const string Path = "/di/pair";

    public static string BuildHtml(string address, int port, string token)
    {
        var deepLink = $"dilink://v1?ip={Uri.EscapeDataString(address)}&port={port}&token={Uri.EscapeDataString(token)}";
        var escapedDeepLink = Html(deepLink);
        var escapedAddress = Html($"{address}:{port}");
        var escapedToken = Html(token);
        var shortened = token.Length > 12 ? token[..6] + "…" + token[^4..] : token;
        var escapedShort = Html(shortened);
        var scriptLink = Js(deepLink);

        return $$"""
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>DI Link — сопряжение</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
    padding: 24px; background: #14141c; color: #f2f2f7;
    font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
  }
  .card {
    width: 100%; max-width: 420px; background: #1c1c26; border-radius: 22px;
    padding: 30px 24px 26px; text-align: center;
    box-shadow: 0 18px 50px rgba(0,0,0,.45); border: 1px solid #2c2c3a;
  }
  .logo {
    width: 58px; height: 58px; margin: 0 auto 16px; border-radius: 17px;
    background: linear-gradient(135deg, #8B7CFF, #5AC8FA);
    display: flex; align-items: center; justify-content: center;
    font-size: 24px; font-weight: 700; color: #14141c;
  }
  h1 { margin: 0; font-size: 21px; font-weight: 600; }
  .sub { margin: 6px 0 22px; font-size: 13px; color: #9a9aa8; }
  .btn {
    display: block; padding: 15px 20px; border-radius: 14px; text-decoration: none;
    background: #8B7CFF; color: #14141c; font-size: 16px; font-weight: 600;
  }
  .btn:active { background: #7a6bf0; }
  .hint { margin: 14px 0 0; font-size: 12px; color: #9a9aa8; line-height: 1.5; }
  .rows { margin-top: 22px; text-align: left; border-top: 1px solid #2c2c3a; padding-top: 16px; }
  .row { display: flex; justify-content: space-between; gap: 12px; padding: 7px 0; font-size: 13px; }
  .row span { color: #9a9aa8; }
  .row b { font-weight: 600; font-family: "Cascadia Mono", Consolas, monospace; word-break: break-all; text-align: right; }
</style>
</head>
<body>
  <div class="card">
    <div class="logo">DI</div>
    <h1>Dynamic Island</h1>
    <p class="sub">Сопряжение с компьютером</p>
    <a class="btn" id="open" href="{{escapedDeepLink}}">Открыть DI Link</a>
    <p class="hint">Если приложение не открылось автоматически, нажмите кнопку выше.<br>
      Установить DI Link можно из репозитория проекта.</p>
    <div class="rows">
      <div class="row"><span>Адрес</span><b>{{escapedAddress}}</b></div>
      <div class="row"><span>Ключ</span><b title="{{escapedToken}}">{{escapedShort}}</b></div>
    </div>
  </div>
<script>
  (function () {
    var link = "{{scriptLink}}";
    var done = false;
    function open() { if (done) return; done = true; window.location.href = link; }
    setTimeout(open, 600);
    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState === "visible") setTimeout(open, 200);
    });
  })();
</script>
</body>
</html>
""";
    }

    private static string Html(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    /// <summary>
    /// Экранирование для строки внутри &lt;script&gt;: там амперсанд остаётся
    /// амперсандом, а не превращается в сущность — иначе ссылка уедет битой.
    /// </summary>
    private static string Js(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
}
