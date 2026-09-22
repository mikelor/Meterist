namespace Meterist.Core.Reporting;

/// <summary>
/// The design system established for artifacts/client-spend-benchmark-report*.html
/// (see docs/client-report-generation.md) — reused near-verbatim per that doc's
/// own instruction, rather than re-derived. The KPI grid is the one deliberate
/// change: auto-fit columns instead of a fixed 2-column grid, since this
/// renderer supports an arbitrary number of tenants, not just two.
/// </summary>
internal static class ReportStyles
{
    public const string Css = """
    :root {
      --bg: #f1f2ed;
      --bg-elevated: #fbfbf8;
      --line: #dadcd3;
      --line-strong: #c3c6ba;
      --text: #1c2027;
      --text-dim: #565b62;
      --text-faint: #83877e;
      --accent: #b8863b;
      --accent-ink: #6b4c1f;
      --good: #3f7a5c;
      --good-bg: #e3ede7;
      --risk: #b14a3d;
      --risk-bg: #f5e4e1;
      --vendor-chatgpt: #3f7c86;
      --vendor-gemini: #6b5b95;
      --vendor-claude: #8a8465;
      --vendor-claude-ent: #5b7495;

      --font-display: Georgia, "Iowan Old Style", "Palatino Linotype", "Book Antiqua", serif;
      --font-body: "Segoe UI", "Helvetica Neue", Arial, sans-serif;
      --font-mono: "SF Mono", "Cascadia Code", Consolas, "Liberation Mono", monospace;
    }

    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #14171b;
        --bg-elevated: #1c2027;
        --line: #2c3138;
        --line-strong: #3a404a;
        --text: #ece9e1;
        --text-dim: #a3a8ae;
        --text-faint: #767b82;
        --accent: #d2a257;
        --accent-ink: #f2dcb3;
        --good: #74b494;
        --good-bg: rgba(63, 122, 92, 0.16);
        --risk: #e3897a;
        --risk-bg: rgba(177, 74, 61, 0.18);
        --vendor-chatgpt: #6fa9b3;
        --vendor-gemini: #9d8cc4;
        --vendor-claude: #b7ae82;
        --vendor-claude-ent: #8fadc7;
      }
    }
    :root[data-theme="dark"] {
      --bg: #14171b;
      --bg-elevated: #1c2027;
      --line: #2c3138;
      --line-strong: #3a404a;
      --text: #ece9e1;
      --text-dim: #a3a8ae;
      --text-faint: #767b82;
      --accent: #d2a257;
      --accent-ink: #f2dcb3;
      --good: #74b494;
      --good-bg: rgba(63, 122, 92, 0.16);
      --risk: #e3897a;
      --risk-bg: rgba(177, 74, 61, 0.18);
      --vendor-chatgpt: #6fa9b3;
      --vendor-gemini: #9d8cc4;
      --vendor-claude: #b7ae82;
      --vendor-claude-ent: #8fadc7;
    }
    :root[data-theme="light"] {
      --bg: #f1f2ed;
      --bg-elevated: #fbfbf8;
      --line: #dadcd3;
      --line-strong: #c3c6ba;
      --text: #1c2027;
      --text-dim: #565b62;
      --text-faint: #83877e;
      --accent: #b8863b;
      --accent-ink: #6b4c1f;
      --good: #3f7a5c;
      --good-bg: #e3ede7;
      --risk: #b14a3d;
      --risk-bg: #f5e4e1;
      --vendor-chatgpt: #3f7c86;
      --vendor-gemini: #6b5b95;
      --vendor-claude: #8a8465;
      --vendor-claude-ent: #5b7495;
    }

    * { box-sizing: border-box; }

    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-family: var(--font-body);
      font-size: 15px;
      line-height: 1.55;
    }

    .page {
      max-width: 1200px;
      margin: 0 auto;
      padding: 48px 28px 64px;
      display: flex;
      flex-direction: column;
      gap: 40px;
    }

    .tabular { font-variant-numeric: tabular-nums; }

    .masthead {
      display: flex;
      flex-direction: column;
      gap: 14px;
    }
    .masthead-top {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: 16px;
      flex-wrap: wrap;
    }
    .wordmark {
      font-family: var(--font-mono);
      font-size: 12px;
      letter-spacing: 0.14em;
      text-transform: uppercase;
      color: var(--text-faint);
    }
    .draft-chip {
      font-family: var(--font-mono);
      font-size: 11px;
      letter-spacing: 0.1em;
      text-transform: uppercase;
      color: var(--risk);
      background: var(--risk-bg);
      border: 1px solid var(--risk);
      border-radius: 3px;
      padding: 3px 8px;
      white-space: nowrap;
    }
    h1 {
      font-family: var(--font-display);
      font-weight: 400;
      font-size: 32px;
      line-height: 1.15;
      margin: 0;
      text-wrap: balance;
      letter-spacing: 0.005em;
    }
    .masthead-meta {
      display: flex;
      gap: 18px;
      flex-wrap: wrap;
      font-family: var(--font-mono);
      font-size: 12.5px;
      color: var(--text-dim);
    }
    .masthead-meta span::before {
      content: "· ";
      color: var(--line-strong);
    }
    .masthead-meta span:first-child::before { content: ""; }
    .generated-at {
      font-family: var(--font-mono);
      font-size: 11.5px;
      color: var(--text-faint);
    }

    .tick-rule {
      height: 7px;
      background-image: repeating-linear-gradient(
        to right, var(--line-strong) 0 1px, transparent 1px 14px
      );
      background-position: bottom left;
      background-size: 100% 7px;
      background-repeat: repeat-x;
      border-bottom: 1px solid var(--line);
    }

    section { display: flex; flex-direction: column; gap: 16px; }
    .eyebrow {
      font-family: var(--font-mono);
      font-size: 11.5px;
      letter-spacing: 0.12em;
      text-transform: uppercase;
      color: var(--accent-ink);
    }
    h2 {
      font-family: var(--font-display);
      font-weight: 400;
      font-size: 21px;
      margin: 0;
      text-wrap: balance;
    }

    .kpi-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 16px;
    }
    .kpi-card {
      background: var(--bg-elevated);
      border: 1px solid var(--line);
      border-radius: 4px;
      padding: 20px 22px;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .kpi-label {
      font-family: var(--font-mono);
      font-size: 11.5px;
      letter-spacing: 0.1em;
      text-transform: uppercase;
      color: var(--text-faint);
    }
    .kpi-value {
      font-family: var(--font-mono);
      font-size: 34px;
      font-weight: 600;
      letter-spacing: -0.01em;
    }
    .kpi-sub {
      font-size: 13px;
      color: var(--text-dim);
      font-family: var(--font-mono);
    }

    .table-scroll { overflow-x: auto; }
    table {
      width: 100%;
      border-collapse: collapse;
      min-width: 620px;
    }
    th, td {
      text-align: left;
      padding: 11px 14px;
      border-bottom: 1px solid var(--line);
      vertical-align: top;
    }
    thead th {
      font-family: var(--font-mono);
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: var(--text-faint);
      border-bottom: 1px solid var(--line-strong);
      font-weight: 500;
    }
    tbody th {
      font-family: var(--font-body);
      font-weight: 600;
      white-space: nowrap;
    }
    td.figure, th.figure {
      font-family: var(--font-mono);
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
    }
    .breakdown {
      display: block;
      white-space: normal;
      font-size: 11.5px;
      color: var(--text-faint);
      margin-top: 2px;
    }
    .muted-dash { color: var(--text-faint); }
    .mech-rate { font-family: var(--font-mono); font-weight: 600; }
    td.note, th.note-col { color: var(--text-dim); font-size: 13px; }
    tbody tr:last-child td, tbody tr:last-child th { border-bottom: none; }

    .concentration-grid {
      display: flex;
      flex-direction: column;
      gap: 22px;
    }
    .concentration-row {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .concentration-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      flex-wrap: wrap;
    }
    .concentration-tenant {
      font-family: var(--font-mono);
      font-weight: 600;
      letter-spacing: 0.03em;
      text-transform: uppercase;
      font-size: 13px;
    }
    .risk-flag, .good-flag {
      font-family: var(--font-mono);
      font-size: 11.5px;
      padding: 2px 8px;
      border-radius: 3px;
      letter-spacing: 0.02em;
    }
    .risk-flag { color: var(--risk); background: var(--risk-bg); border: 1px solid var(--risk); }
    .good-flag { color: var(--good); background: var(--good-bg); border: 1px solid var(--good); }

    .audit-intro { color: var(--text-dim); margin: 0; }

    .subsection-tenant {
      font-family: var(--font-mono);
      font-size: 13px;
      font-weight: 600;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      color: var(--text);
      margin: 4px 0 0;
    }
    .weekly-table { table-layout: fixed; }
    .weekly-table th, .weekly-table td { padding: 8px 9px; font-size: 13px; }
    .weekly-table thead th { font-size: 10px; white-space: normal; line-height: 1.4; }
    .weekly-table tbody th { white-space: normal; line-height: 1.3; }
    .weekly-table td.note { word-wrap: break-word; }
    tr.total-row th, tr.total-row td { font-weight: 600; border-top: 2px solid var(--line-strong); }
    tr.cumulative-row th, tr.cumulative-row td { color: var(--text-faint); font-style: italic; font-size: 12.5px; }
    .small-note { font-size: 12px; color: var(--text-faint); margin: 0; }
    h3.section-sub {
      font-family: var(--font-display);
      font-weight: 400;
      font-size: 17px;
      margin: 6px 0 0;
      text-wrap: balance;
    }

    .pending-note {
      border: 1px dashed var(--line-strong);
      border-radius: 3px;
      padding: 14px 18px;
      color: var(--text-faint);
      font-size: 13px;
    }

    .bar {
      display: flex;
      width: 100%;
      height: 26px;
      border-radius: 3px;
      overflow: hidden;
      border: 1px solid var(--line);
    }
    .bar-segment { height: 100%; }
    .bar-seg-chatgpt { background: var(--vendor-chatgpt); }
    .bar-seg-gemini { background: var(--vendor-gemini); }
    .bar-seg-claude { background: var(--vendor-claude); }
    .bar-seg-claude-ent { background: var(--vendor-claude-ent); }

    .legend {
      display: flex;
      gap: 18px;
      flex-wrap: wrap;
      font-family: var(--font-mono);
      font-size: 12.5px;
      color: var(--text-dim);
    }
    .legend-item { display: flex; align-items: center; gap: 6px; }
    .legend-swatch {
      width: 10px;
      height: 10px;
      border-radius: 2px;
      display: inline-block;
      flex: none;
    }

    .footnote {
      border-top: 1px solid var(--line);
      padding-top: 20px;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .footnote ul {
      margin: 0;
      padding-left: 18px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .footnote li {
      font-size: 13px;
      color: var(--text-dim);
    }
    .footnote li b { color: var(--text); font-weight: 600; }
    .colophon {
      font-family: var(--font-mono);
      font-size: 11.5px;
      color: var(--text-faint);
      letter-spacing: 0.03em;
    }

    @media (max-width: 640px) {
      .kpi-grid { grid-template-columns: 1fr; }
      h1 { font-size: 26px; }
    }

    a { color: var(--accent-ink); }
    ::selection { background: var(--accent); color: var(--bg-elevated); }
    """;
}
