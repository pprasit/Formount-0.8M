using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;

namespace NARIT.Mount.API.Tester
{
    public partial class MainWindow
    {
        private bool _changeLogLoaded;
        private bool _userManualLoaded;

        private async void WbChangeLog_Loaded(object sender, RoutedEventArgs e)
        {
            if (_changeLogLoaded)
                return;

            _changeLogLoaded = true;
            await wbChangeLog.EnsureCoreWebView2Async();
            wbChangeLog.NavigateToString(BuildChangeLogDocument());
        }

        private async void WbUserManual_Loaded(object sender, RoutedEventArgs e)
        {
            if (_userManualLoaded)
                return;

            _userManualLoaded = true;
            await wbUserManual.EnsureCoreWebView2Async();
            string guidePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserGuide.html");
            if (File.Exists(guidePath))
            {
                wbUserManual.Source = new Uri(guidePath);
            }
            else
            {
                wbUserManual.NavigateToString("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>User Manual</title>
<style>
body { font-family: "Segoe UI", system-ui, sans-serif; background: #0f1419; color: #e5e7eb; padding: 24px; }
.card { background: #1e1e1e; border: 1px solid #3f3f46; border-radius: 12px; padding: 20px; }
</style>
</head>
<body>
  <div class="card">
    <h1>User Manual</h1>
    <p>UserGuide.html was not found in the tester output folder. Please rebuild the tester so the guide file is copied next to the executable.</p>
  </div>
</body>
</html>
""");
            }
        }

        private string BuildChangeLogDocument()
        {
            string userGuideHtml = LoadUserGuideBody();

            return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>NARIT Mount API - Change Log</title>
<style>
  :root {
    --bg: #0f1419;
    --card: #1e1e1e;
    --card-alt: #252526;
    --border: #3f3f46;
    --accent: #3b82f6;
    --green: #10b981;
    --red: #ef4444;
    --amber: #f59e0b;
    --purple: #8b5cf6;
    --text: #e5e7eb;
    --text-sec: #9ca3af;
    --muted: #111827;
  }

  * { box-sizing: border-box; }
  html { scroll-behavior: smooth; }
  body {
    margin: 0;
    font-family: "Segoe UI", system-ui, sans-serif;
    background: var(--bg);
    color: var(--text);
    line-height: 1.65;
  }

  a { color: #93c5fd; text-decoration: none; }
  a:hover { text-decoration: underline; }

  .shell {
    display: grid;
    grid-template-columns: 280px 1fr;
    min-height: 100vh;
  }

  .sidebar {
    position: sticky;
    top: 0;
    align-self: start;
    height: 100vh;
    overflow-y: auto;
    padding: 24px 18px;
    background: linear-gradient(180deg, #151b23 0%, #11161d 100%);
    border-right: 1px solid var(--border);
  }

  .brand {
    margin-bottom: 20px;
    padding-bottom: 18px;
    border-bottom: 1px solid var(--border);
  }

  .brand h1 {
    font-size: 17px;
    margin: 0 0 6px;
    color: #fff;
  }

  .brand p {
    margin: 0;
    color: var(--text-sec);
    font-size: 12px;
  }

  .nav-group {
    margin: 18px 0;
  }

  .nav-label {
    font-size: 10px;
    font-weight: 700;
    letter-spacing: 1.2px;
    text-transform: uppercase;
    color: var(--amber);
    margin-bottom: 8px;
    padding: 0 6px;
  }

  .nav-link {
    display: block;
    padding: 8px 10px;
    border-radius: 8px;
    color: var(--text-sec);
    font-size: 13px;
    margin-bottom: 4px;
  }

  .nav-link:hover {
    background: rgba(59,130,246,.12);
    color: #fff;
    text-decoration: none;
  }

  .content {
    padding: 28px 36px 48px;
  }

  .hero {
    background:
      radial-gradient(circle at top right, rgba(59,130,246,.22), transparent 34%),
      linear-gradient(135deg, rgba(37,99,235,.18), rgba(139,92,246,.08));
    border: 1px solid var(--border);
    border-radius: 18px;
    padding: 28px;
    margin-bottom: 22px;
  }

  .hero h2 {
    margin: 0 0 10px;
    font-size: 28px;
    color: #fff;
  }

  .hero p {
    margin: 0 0 14px;
    color: var(--text-sec);
    max-width: 960px;
  }

  .hero-chips {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
  }

  .chip {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 7px 12px;
    border-radius: 999px;
    background: rgba(17,24,39,.78);
    border: 1px solid rgba(148,163,184,.18);
    color: #dbeafe;
    font-size: 12px;
    font-weight: 600;
  }

  .toggle-bar {
    display: flex;
    gap: 10px;
    margin-bottom: 18px;
    flex-wrap: wrap;
  }

  .toggle-btn {
    border: 1px solid var(--border);
    background: var(--card);
    color: var(--text);
    border-radius: 10px;
    padding: 10px 14px;
    font-size: 13px;
    font-weight: 700;
    cursor: pointer;
  }

  .toggle-btn.active {
    background: rgba(59,130,246,.14);
    color: #fff;
    border-color: rgba(96,165,250,.65);
  }

  .panel {
    display: none;
  }

  .panel.active {
    display: block;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 14px;
    margin-bottom: 18px;
  }

  .metric, .card {
    background: var(--card);
    border: 1px solid var(--border);
    border-radius: 14px;
    padding: 16px 18px;
  }

  .metric .label {
    color: var(--text-sec);
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 1px;
    margin-bottom: 8px;
  }

  .metric .value {
    font-size: 28px;
    font-weight: 800;
    color: #fff;
  }

  .metric .hint {
    margin-top: 6px;
    color: var(--text-sec);
    font-size: 12px;
  }

  .section-title {
    font-size: 20px;
    margin: 26px 0 12px;
    color: #fff;
  }

  .section-copy {
    color: var(--text-sec);
    margin: 0 0 16px;
  }

  .timeline {
    display: grid;
    gap: 12px;
  }

  details.release {
    background: var(--card);
    border: 1px solid var(--border);
    border-radius: 14px;
    overflow: hidden;
  }

  details.release[open] {
    border-color: rgba(96,165,250,.55);
    box-shadow: 0 0 0 1px rgba(59,130,246,.15);
  }

  details.release summary {
    list-style: none;
    cursor: pointer;
    padding: 16px 18px;
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 16px;
  }

  details.release summary::-webkit-details-marker { display: none; }

  .release-title {
    font-size: 15px;
    font-weight: 700;
    color: #fff;
  }

  .release-meta {
    color: var(--text-sec);
    font-size: 12px;
  }

  .release-body {
    padding: 0 18px 18px;
    color: var(--text-sec);
  }

  .badge {
    display: inline-block;
    padding: 3px 8px;
    border-radius: 999px;
    font-size: 11px;
    font-weight: 800;
    letter-spacing: .4px;
  }

  .badge-blue { background: rgba(59,130,246,.16); color: #bfdbfe; }
  .badge-green { background: rgba(16,185,129,.16); color: #a7f3d0; }
  .badge-purple { background: rgba(139,92,246,.16); color: #ddd6fe; }
  .badge-amber { background: rgba(245,158,11,.16); color: #fde68a; }

  .release-body ul {
    margin: 10px 0 0 20px;
    padding: 0;
  }

  .release-body li {
    margin-bottom: 8px;
  }

  .guide-shell {
    background: var(--card);
    border: 1px solid var(--border);
    border-radius: 16px;
    padding: 18px 20px;
  }

  .guide-note {
    background: rgba(59,130,246,.08);
    border: 1px solid rgba(96,165,250,.22);
    border-radius: 12px;
    padding: 14px 16px;
    color: var(--text-sec);
    margin-bottom: 16px;
  }

  .guide-shell .wrapper {
    display: block !important;
    min-height: auto !important;
  }

  .guide-shell nav {
    display: none !important;
  }

  .guide-shell main {
    margin-left: 0 !important;
    max-width: none !important;
    padding: 0 !important;
  }

  .guide-shell .hero {
    margin-top: 0;
  }

  .guide-shell h2 {
    margin-top: 32px;
  }

  @media (max-width: 980px) {
    .shell { grid-template-columns: 1fr; }
    .sidebar {
      position: relative;
      height: auto;
      border-right: none;
      border-bottom: 1px solid var(--border);
    }
    .content {
      padding: 20px;
    }
  }
</style>
<script>
  function showPanel(panelId) {
    var panels = document.querySelectorAll('.panel');
    var buttons = document.querySelectorAll('.toggle-btn');

    panels.forEach(function(panel) {
      panel.classList.toggle('active', panel.id === panelId);
    });

    buttons.forEach(function(button) {
      button.classList.toggle('active', button.dataset.panel === panelId);
    });
  }

  window.addEventListener('DOMContentLoaded', function() {
    showPanel('panel-changelog');
  });
</script>
</head>
<body>
  <div class="shell">
    <aside class="sidebar">
      <div class="brand">
        <h1>NARIT Mount API</h1>
        <p>Interactive release notes, project history, and updated user guide for the test client.</p>
      </div>

      <div class="nav-group">
        <div class="nav-label">Quick View</div>
        <a class="nav-link" href="#summary">Summary</a>
        <a class="nav-link" href="#timeline">Release Timeline</a>
        <a class="nav-link" href="#highlights">Feature Highlights</a>
        <a class="nav-link" href="#guide-start">Integrated User Guide</a>
      </div>

      <div class="nav-group">
        <div class="nav-label">Recent Work</div>
        <a class="nav-link" href="#release-jog">Dual-axis Jog</a>
        <a class="nav-link" href="#release-offset">Offset Commands</a>
        <a class="nav-link" href="#release-smallbody">Comet / Asteroid Tracking</a>
        <a class="nav-link" href="#release-axisstatus">Axis Status + RMS</a>
        <a class="nav-link" href="#release-pointing">Persistent Pointing Model</a>
        <a class="nav-link" href="#release-tester">Tester UX & Stability</a>
      </div>
    </aside>

    <main class="content">
      <section class="hero" id="summary">
        <h2>Project Change Log + User Guide</h2>
        <p>This page consolidates the work completed in the current development cycle: API enhancements, tester coverage, UI polish, stability fixes, and the updated operational guide for mount control. Use the buttons below to switch between the release history view and the integrated guide view.</p>
        <div class="hero-chips">
          <span class="chip">Dual-axis jog in one command</span>
          <span class="chip">Offset control API + tester pad</span>
          <span class="chip">Asteroid / comet tracking</span>
          <span class="chip">Axis status + RMS rules</span>
          <span class="chip">Persistent pointing model path</span>
          <span class="chip">Tester documentation tab</span>
        </div>
      </section>

      <div class="toggle-bar">
        <button class="toggle-btn active" data-panel="panel-changelog" onclick="showPanel('panel-changelog')">Release History</button>
        <button class="toggle-btn" data-panel="panel-guide" onclick="showPanel('panel-guide')">Integrated User Guide</button>
      </div>

      <section id="panel-changelog" class="panel active">
        <div class="grid">
          <div class="metric">
            <div class="label">API Areas Updated</div>
            <div class="value">7</div>
            <div class="hint">Motion, offset, tracking, status, settings persistence, docs, and tester tooling.</div>
          </div>
          <div class="metric">
            <div class="label">Tester Upgrades</div>
            <div class="value">6</div>
            <div class="hint">New command surfaces, status display, file browser, docs tab, styling, and startup fixes.</div>
          </div>
          <div class="metric">
            <div class="label">Stability Fixes</div>
            <div class="value">4</div>
            <div class="hint">Refresh timing, null guards, startup sequencing, and runtime status refresh behavior.</div>
          </div>
        </div>

        <h3 class="section-title" id="timeline">Release Timeline</h3>
        <p class="section-copy">This timeline summarizes the work completed across the current implementation cycle. Expand any item to see scope, behavior, and tester impact.</p>

        <div class="timeline">
          <details class="release" id="release-jog" open>
            <summary>
              <div>
                <div class="release-title">Dual-axis Jog Command</div>
                <div class="release-meta">Motion control • API + Tester</div>
              </div>
              <span class="badge badge-blue">Implemented</span>
            </summary>
            <div class="release-body">
              <p>The API now supports coordinated jogging of Azimuth and Altitude in a single call, matching the intended mount-level control flow.</p>
              <ul>
                <li><code>Jog(azVelocityDegPerSec, altVelocityDegPerSec)</code> explicitly works as one dual-axis command.</li>
                <li>Manual jog now stops active tracking before sending motion so operator input does not fight the tracking loop.</li>
                <li>The tester gained a dedicated dual-axis jog panel for immediate verification.</li>
              </ul>
            </div>
          </details>

          <details class="release" id="release-offset">
            <summary>
              <div>
                <div class="release-title">Offset Command Surface</div>
                <div class="release-meta">Pointing correction • API + Tester</div>
              </div>
              <span class="badge badge-green">Implemented</span>
            </summary>
            <div class="release-body">
              <p>The API now exposes a telescope offset workflow equivalent to the operational UI used in the main mount application.</p>
              <ul>
                <li>Added offset command inputs for <strong>Offset Type</strong>, <strong>Step Size</strong>, and <strong>Direction</strong>.</li>
                <li>Supported the 8-direction pad and accumulated offset reset flow.</li>
                <li>The tester now includes an operator-friendly offset section with accumulated values visible on screen.</li>
              </ul>
            </div>
          </details>

          <details class="release" id="release-smallbody">
            <summary>
              <div>
                <div class="release-title">Comet and Asteroid Tracking</div>
                <div class="release-meta">Small-body tracking • API + Tester</div>
              </div>
              <span class="badge badge-purple">Implemented</span>
            </summary>
            <div class="release-body">
              <p>Tracking can now be driven from user-supplied orbital elements for small solar-system bodies.</p>
              <ul>
                <li>Added <code>TrackAsteroid(...)</code> and <code>TrackComet(...)</code> command surfaces.</li>
                <li>Created orbital element models and propagation logic for API-side target updates.</li>
                <li>Extended the tester with input forms for asteroid and comet elements so the behavior can be exercised without writing code first.</li>
              </ul>
            </div>
          </details>

          <details class="release" id="release-axisstatus">
            <summary>
              <div>
                <div class="release-title">Axis Status and RMS Display Logic</div>
                <div class="release-meta">Telemetry • API + Tester</div>
              </div>
              <span class="badge badge-amber">Updated</span>
            </summary>
            <div class="release-body">
              <p>The API now reports higher-level axis behavior that is easier for clients to interpret than raw enable and RMS numbers alone.</p>
              <ul>
                <li>Added <code>Axis_Status</code> output for each axis.</li>
                <li>Status is derived as <strong>Disabled</strong>, <strong>Idle</strong>, <strong>Slewing</strong>, or <strong>Tracking</strong> using drive-enabled state plus tracking RMS against sensitivity.</li>
                <li>RMS now reports <code>0</code> whenever an axis is not in <code>Tracking</code> or <code>Slewing</code>, which keeps telemetry cleaner for operator displays.</li>
                <li>The tester status panel now shows Azimuth and Altitude status fields directly.</li>
              </ul>
            </div>
          </details>

          <details class="release" id="release-pointing">
            <summary>
              <div>
                <div class="release-title">Persistent Pointing Model Path</div>
                <div class="release-meta">Configuration persistence • API + Tester</div>
              </div>
              <span class="badge badge-blue">Implemented</span>
            </summary>
            <div class="release-body">
              <p>Pointing model selection is now part of the persistent configuration workflow instead of being a one-session action.</p>
              <ul>
                <li><code>SetPointingModelPath(string filePath)</code> now validates, saves, and applies the file path through the API.</li>
                <li>The path is stored in <code>AppSetting.json</code> so the next startup can restore it automatically.</li>
                <li>The tester now includes a file picker and a one-click action button for this API call.</li>
              </ul>
            </div>
          </details>

          <details class="release" id="release-tester">
            <summary>
              <div>
                <div class="release-title">Tester Stability and UX Refinements</div>
                <div class="release-meta">Operator experience • Tester</div>
              </div>
              <span class="badge badge-green">Refined</span>
            </summary>
            <div class="release-body">
              <p>The tester has been hardened to behave better during startup and routine operator workflows.</p>
              <ul>
                <li>Fixed startup null reference issues related to offset controls and auto-refresh timer initialization.</li>
                <li>Forced immediate status refresh after key commands such as connect, enable, disable, stop, goto, jog, and tracking actions.</li>
                <li>Improved dropdown styling for readability and visual consistency across the entire test client.</li>
                <li>Added this interactive documentation tab so operator-facing release history and guide content are available inside the tester itself.</li>
              </ul>
            </div>
          </details>
        </div>

        <h3 class="section-title" id="highlights">Feature Highlights</h3>
        <div class="grid">
          <div class="card">
            <strong>Mount Motion</strong>
            <p>We now cover goto, stop, dual-axis jog, and offset workflows from the tester without needing extra harness code.</p>
          </div>
          <div class="card">
            <strong>Tracking Expansion</strong>
            <p>Object, sidereal, satellite, asteroid, and comet scenarios all have explicit API coverage and a tester path.</p>
          </div>
          <div class="card">
            <strong>Telemetry Clarity</strong>
            <p>Axis status and RMS presentation are easier to interpret for UI clients and automation layers.</p>
          </div>
          <div class="card">
            <strong>Persistent Configuration</strong>
            <p>Pointing model file location now survives restart, reducing repeated setup time for operators.</p>
          </div>
        </div>
      </section>

      <section id="panel-guide" class="panel">
        <div class="guide-note" id="guide-start">
          <strong>Integrated Guide</strong><br>
          This section renders the current <code>UserGuide.html</code> from the API project inside the tester, after updating it to reflect the latest motion, pointing, telemetry, and tracking capabilities.
        </div>
        <div class="guide-shell">
          {{userGuideHtml}}
        </div>
      </section>
    </main>
  </div>
</body>
</html>
""";
        }

        private string LoadUserGuideBody()
        {
            try
            {
                string guidePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserGuide.html");
                if (!File.Exists(guidePath))
                {
                    return "<div class=\"card\"><strong>User guide file not found.</strong><p>Please rebuild the tester so UserGuide.html is copied to the output folder.</p></div>";
                }

                string html = File.ReadAllText(guidePath, Encoding.UTF8);
                Match bodyMatch = Regex.Match(html, "<body[^>]*>([\\s\\S]*?)</body>", RegexOptions.IgnoreCase);
                string body = bodyMatch.Success ? bodyMatch.Groups[1].Value : html;

                return body.Replace("</script>", "<\\/script>", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                return $"<div class=\"card\"><strong>Failed to load user guide.</strong><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></div>";
            }
        }
    }
}
