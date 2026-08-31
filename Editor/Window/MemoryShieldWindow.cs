// Editor/Window/MemoryShieldWindow.cs
// UI Toolkit. Resizable, min 1100x700, 6px gold left-bar. ListView, not an
// IMGUI loop — three thousand findings through IMGUI freezes the editor.
//
// Visual language: Builder Notes editorial (cream page, navy text, gold accent,
// taupe hairlines). Generous padding, one idea per line, ellipsis over wrap.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameDistrict.MemoryShield.Analyzers;
using GameDistrict.MemoryShield.Brand;
using GameDistrict.MemoryShield.Core;
using GameDistrict.MemoryShield.Export;
using GameDistrict.MemoryShield.Model;
using GameDistrict.MemoryShield.Telemetry;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameDistrict.MemoryShield.Window
{
    public class MemoryShieldWindow : EditorWindow
    {
        private MemoryScanner _scanner;
        private MemoryReport _report;

        // ui refs
        private Label _gradeLabel, _scoreLabel, _timestampLabel, _statusLabel, _verdictLabel;
        private ScanProgressBar _progress;
        private VisualElement _railButtons;
        private ListView _findingsList;
        private VisualElement _detailPane;
        private TextField _searchField;

        // filter state
        private string _selectedCategory;
        private readonly HashSet<Severity> _severityFilter = new HashSet<Severity>
            { Severity.Blocker, Severity.High, Severity.Medium, Severity.Low, Severity.Info };
        private string _search = "";
        private List<Finding> _visible = new List<Finding>();

        [MenuItem("Tools/GD MemoryShield")]
        public static void Open()
        {
            var w = GetWindow<MemoryShieldWindow>();
            w.titleContent = new GUIContent("GD MemoryShield");
            w.minSize = new Vector2(1100, 700);
        }

        private void OnEnable()
        {
            _scanner = new MemoryScanner();
            _scanner.Updated += Repaint;
            _scanner.Completed += OnScanCompleted;
        }

        private void OnDisable()
        {
            if (_scanner != null)
            {
                _scanner.Updated -= Repaint;
                _scanner.Completed -= OnScanCompleted;
                _scanner.Cancel();
            }
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Row;
            root.style.backgroundColor = MSBrandTokens.Cream;

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.gamedistrict.memoryshield/Editor/Window/MemoryShield.uss");
            if (uss != null) root.styleSheets.Add(uss);

            // gold left-bar
            var goldBar = new VisualElement();
            goldBar.style.width = MSBrandTokens.GoldBarWidth;
            goldBar.style.flexShrink = 0;
            goldBar.style.backgroundColor = MSBrandTokens.Gold;
            root.Add(goldBar);

            var main = new VisualElement();
            main.style.flexGrow = 1;
            main.style.flexDirection = FlexDirection.Column;
            root.Add(main);

            var header = BuildHeader();
            header.style.flexShrink = 0;
            main.Add(header);

            _progress = new ScanProgressBar();
            _progress.style.marginLeft = 28;
            _progress.style.marginRight = 28;
            _progress.style.flexShrink = 0;
            _progress.style.display = DisplayStyle.None;
            main.Add(_progress);

            // minHeight 0 everywhere on the grow chain: without it, a ListView
            // with thousands of rows expands the column past the window, crushing
            // the filters and pushing the footer off-screen.
            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1;
            body.style.minHeight = 0;
            main.Add(body);

            body.Add(BuildLeftRail());
            var pane = BuildMainPane();
            pane.style.minHeight = 0;
            body.Add(pane);

            var footer = BuildFooter();
            footer.style.flexShrink = 0;
            main.Add(footer);

            RefreshRail();
            RefreshFindings();
            EditorApplication.update += Tick;
        }

        private void OnDestroy()
        {
            EditorApplication.update -= Tick;
        }

        private void Tick()
        {
            if (_scanner == null) return;
            bool running = _scanner.Running;
            if (_progress != null)
            {
                _progress.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
                if (running) _progress.Set(_scanner.Progress, _scanner.CurrentStep);
            }
        }

        // ── small style helpers ───────────────────────────────────────────────

        private static Label Eyebrow(string text)
        {
            var l = new Label(text.ToUpperInvariant());
            l.style.fontSize = 10;
            l.style.letterSpacing = 1.5f;
            l.style.color = MSBrandTokens.WarmGray;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            if (MSBrandTokens.Inter != null) l.style.unityFont = MSBrandTokens.Inter;
            return l;
        }

        private static void Rounded(VisualElement e, float radius)
        {
            e.style.borderTopLeftRadius = radius;
            e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = radius;
            e.style.borderBottomRightRadius = radius;
        }

        private static void Pad(VisualElement e, float top, float right, float bottom, float left)
        {
            e.style.paddingTop = top;
            e.style.paddingRight = right;
            e.style.paddingBottom = bottom;
            e.style.paddingLeft = left;
        }

        private static Button MakeButton(string text, System.Action onClick, bool primary)
        {
            var b = new Button(onClick) { text = text };
            b.style.marginLeft = 8;
            Pad(b, 6, 16, 6, 16);
            b.style.backgroundColor = primary ? MSBrandTokens.Gold : Color.clear;
            b.style.color = MSBrandTokens.Navy;
            b.style.fontSize = 12;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.borderTopWidth = 1;
            b.style.borderBottomWidth = 1;
            b.style.borderLeftWidth = 1;
            b.style.borderRightWidth = 1;
            var borderColor = primary ? MSBrandTokens.Gold : MSBrandTokens.Taupe;
            b.style.borderTopColor = borderColor;
            b.style.borderBottomColor = borderColor;
            b.style.borderLeftColor = borderColor;
            b.style.borderRightColor = borderColor;
            Rounded(b, 4);
            return b;
        }

        // ── header ───────────────────────────────────────────────────────────

        private VisualElement BuildHeader()
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            Pad(header, 22, 28, 18, 28);
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = MSBrandTokens.Taupe;

            // left block: eyebrow + project name + timestamp
            var titleBlock = new VisualElement();
            titleBlock.Add(Eyebrow("GD MemoryShield"));

            var title = new Label(Application.productName);
            title.style.fontSize = 26;
            title.style.color = MSBrandTokens.Navy;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginTop = 2;
            if (MSBrandTokens.Fraunces != null) title.style.unityFont = MSBrandTokens.Fraunces;
            titleBlock.Add(title);

            _timestampLabel = new Label("No scan yet — hit Rescan to audit this project.");
            _timestampLabel.style.fontSize = 11;
            _timestampLabel.style.color = MSBrandTokens.WarmGray;
            _timestampLabel.style.marginTop = 3;
            titleBlock.Add(_timestampLabel);
            header.Add(titleBlock);

            var spacerLeft = new VisualElement();
            spacerLeft.style.flexGrow = 1;
            header.Add(spacerLeft);

            // grade block: big letter + score underneath
            var gradeBlock = new VisualElement();
            gradeBlock.style.alignItems = Align.Center;
            gradeBlock.style.marginRight = 28;
            var gradeEyebrow = Eyebrow("Grade");
            gradeBlock.Add(gradeEyebrow);
            _gradeLabel = new Label("—");
            _gradeLabel.style.fontSize = 34;
            _gradeLabel.style.color = MSBrandTokens.WarmGray;
            _gradeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _gradeLabel.style.marginTop = -2;
            if (MSBrandTokens.Fraunces != null) _gradeLabel.style.unityFont = MSBrandTokens.Fraunces;
            gradeBlock.Add(_gradeLabel);
            _scoreLabel = new Label("");
            _scoreLabel.style.fontSize = 11;
            _scoreLabel.style.color = MSBrandTokens.WarmGray;
            gradeBlock.Add(_scoreLabel);
            header.Add(gradeBlock);

            header.Add(MakeButton("Rescan", () => StartScan(false), primary: true));
            header.Add(MakeButton("Full Rescan", () => StartScan(true), primary: false));
            return header;
        }

        private void StartScan(bool full)
        {
            if (_scanner.Running) return;
            MemoryShieldTelemetry.Event(full ? "scan.full" : "scan", Application.unityVersion);
            _scanner.StartScan(full);
        }

        // Lets SampleReport (the pipeline check) inject a fake report.
        internal void SetReport(MemoryReport report)
        {
            OnScanCompleted(report);
        }

        private void OnScanCompleted(MemoryReport report)
        {
            _report = report;
            MemoryShieldTelemetry.Event("scan.done",
                string.Format("grade={0} score={1:0} findings={2}", report.grade, report.score, report.AllFindings().Count));
            _gradeLabel.text = report.grade;
            _gradeLabel.style.color = GradeColor(report.grade);
            _scoreLabel.text = report.score.ToString("0") + " / 100";
            _timestampLabel.text = "Scanned " + report.scanDateUtc.Replace("T", " ").Replace("Z", " UTC");
            if (_verdictLabel != null) _verdictLabel.text = report.verdict;
            _selectedCategory = report.categories.Count > 0 ? report.categories[0].category : null;
            RefreshRail();
            RefreshFindings();
        }

        // ── left rail ─────────────────────────────────────────────────────────

        private VisualElement BuildLeftRail()
        {
            var rail = new VisualElement();
            rail.style.width = 240;
            rail.style.flexShrink = 0;
            rail.style.borderRightWidth = 1;
            rail.style.borderRightColor = MSBrandTokens.Taupe;
            Pad(rail, 18, 0, 0, 0);

            var label = Eyebrow("Categories");
            label.style.marginLeft = 28;
            label.style.marginBottom = 8;
            rail.Add(label);

            _railButtons = new VisualElement();
            rail.Add(_railButtons);
            return rail;
        }

        private void RefreshRail()
        {
            if (_railButtons == null) return;
            _railButtons.Clear();
            var categories = _report != null
                ? _report.categories.Select(c => c.category).ToList()
                : new List<string> { "Textures", "Sprite Atlases", "Audio", "Scenes", "Retention", "Update Loops" };

            foreach (var cat in categories)
            {
                string catName = cat;
                bool selected = catName == _selectedCategory;

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.height = 40;
                Pad(row, 0, 14, 0, 24);
                if (selected)
                {
                    row.style.backgroundColor = MSBrandTokens.GoldTint;
                    row.style.borderLeftWidth = 3;
                    row.style.borderLeftColor = MSBrandTokens.Gold;
                }
                else
                {
                    row.style.borderLeftWidth = 3;
                    row.style.borderLeftColor = Color.clear;
                }

                var name = new Label(catName);
                name.style.color = selected ? MSBrandTokens.Navy : MSBrandTokens.Ink;
                name.style.fontSize = 13;
                name.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
                name.style.flexGrow = 1;
                row.Add(name);

                var result = _report != null ? _report.Category(catName) : null;
                var pill = new Label(PillText(result));
                pill.style.fontSize = 10;
                pill.style.unityFontStyleAndWeight = FontStyle.Bold;
                pill.style.unityTextAlign = TextAnchor.MiddleCenter;
                pill.style.minWidth = 52;
                Pad(pill, 2, 8, 2, 8);
                pill.style.color = Color.white;
                pill.style.backgroundColor = PillColor(result);
                Rounded(pill, 9);
                row.Add(pill);

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    _selectedCategory = catName;
                    RefreshRail();
                    RefreshFindings();
                });
                _railButtons.Add(row);
            }
        }

        private static string PillText(CategoryResult r)
        {
            if (r == null) return "—";
            if (r.State == CategoryState.Skipped) return "SKIP";
            if (r.State == CategoryState.Incomplete) return "PART";
            // "0/100", not "0" — a bare number reads as a file count
            return r.subscore.ToString("0") + "/100";
        }

        private static Color PillColor(CategoryResult r)
        {
            if (r == null) return MSBrandTokens.WarmGray;
            if (r.State != CategoryState.Complete) return MSBrandTokens.Amber;
            if (r.subscore >= 85) return MSBrandTokens.Shipped;
            if (r.subscore >= 55) return MSBrandTokens.Amber;
            return MSBrandTokens.Overdue;
        }

        // ── main pane ─────────────────────────────────────────────────────────

        private VisualElement BuildMainPane()
        {
            var pane = new VisualElement();
            pane.style.flexGrow = 1;
            Pad(pane, 14, 28, 0, 24);

            // filters row — flexShrink 0 so a long findings list can't crush it
            var filters = new VisualElement();
            filters.style.flexDirection = FlexDirection.Row;
            filters.style.alignItems = Align.Center;
            filters.style.flexShrink = 0;
            filters.style.height = 34;
            filters.style.marginBottom = 4;

            var sevLabel = Eyebrow("Show");
            sevLabel.style.marginRight = 10;
            filters.Add(sevLabel);

            foreach (var sev in new[] { Severity.High, Severity.Medium, Severity.Low, Severity.Info })
            {
                var s = sev;
                var chip = new Button(() => { }) { text = SeverityUtil.Label(s) };
                StyleChip(chip, true, s);
                chip.clicked += () =>
                {
                    if (_severityFilter.Contains(s)) _severityFilter.Remove(s);
                    else _severityFilter.Add(s);
                    StyleChip(chip, _severityFilter.Contains(s), s);
                    RefreshFindings();
                };
                filters.Add(chip);
            }

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            filters.Add(spacer);

            var searchLabel = Eyebrow("Filter by path");
            searchLabel.style.marginRight = 8;
            filters.Add(searchLabel);
            _searchField = new TextField();
            _searchField.style.width = 260;
            _searchField.style.height = 22;
            _searchField.RegisterValueChangedCallback(e =>
            {
                _search = e.newValue ?? "";
                RefreshFindings();
            });
            filters.Add(_searchField);
            pane.Add(filters);

            _statusLabel = new Label("Run a scan to populate findings.");
            _statusLabel.style.color = MSBrandTokens.WarmGray;
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.marginBottom = 8;
            _statusLabel.style.flexShrink = 0;
            pane.Add(_statusLabel);

            _findingsList = new ListView();
            _findingsList.style.flexGrow = 1;
            _findingsList.style.minHeight = 0;
            _findingsList.fixedItemHeight = 58;
            _findingsList.makeItem = MakeFindingRow;
            _findingsList.bindItem = BindFindingRow;
#if UNITY_2022_2_OR_NEWER
            _findingsList.selectionChanged += OnSelection;
#else
            _findingsList.onSelectionChange += OnSelection;
#endif
            pane.Add(_findingsList);

            _detailPane = new VisualElement();
            _detailPane.style.flexShrink = 0;
            _detailPane.style.display = DisplayStyle.None;
            _detailPane.style.maxHeight = 190;
            _detailPane.style.marginTop = 10;
            _detailPane.style.marginBottom = 12;
            _detailPane.style.backgroundColor = new Color(1f, 1f, 1f, 0.55f);
            _detailPane.style.borderLeftWidth = 3;
            _detailPane.style.borderLeftColor = MSBrandTokens.Gold;
            Pad(_detailPane, 12, 16, 12, 16);
            Rounded(_detailPane, 4);
            pane.Add(_detailPane);

            return pane;
        }

        private static void StyleChip(Button chip, bool on, Severity s)
        {
            chip.style.fontSize = 10;
            chip.style.unityFontStyleAndWeight = FontStyle.Bold;
            chip.style.marginRight = 6;
            Pad(chip, 3, 12, 3, 12);
            Rounded(chip, 10);
            chip.style.borderTopWidth = 1;
            chip.style.borderBottomWidth = 1;
            chip.style.borderLeftWidth = 1;
            chip.style.borderRightWidth = 1;
            var edge = on ? SevColor(s) : MSBrandTokens.Taupe;
            chip.style.borderTopColor = edge;
            chip.style.borderBottomColor = edge;
            chip.style.borderLeftColor = edge;
            chip.style.borderRightColor = edge;
            chip.style.backgroundColor = on ? SevTint(s) : Color.clear;
            chip.style.color = on ? SevColor(s) : MSBrandTokens.WarmGray;
        }

        // ── finding rows ──────────────────────────────────────────────────────

        private VisualElement MakeFindingRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Column;
            row.style.justifyContent = Justify.Center;
            Pad(row, 6, 12, 6, 4);
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = MSBrandTokens.Taupe;

            var top = new VisualElement { name = "top" };
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 4;

            var sev = new Label { name = "sev" };
            sev.style.fontSize = 9;
            sev.style.unityFontStyleAndWeight = FontStyle.Bold;
            sev.style.unityTextAlign = TextAnchor.MiddleCenter;
            sev.style.width = 58;
            sev.style.flexShrink = 0;
            Pad(sev, 2, 0, 2, 0);
            Rounded(sev, 8);
            sev.style.color = Color.white;
            top.Add(sev);

            var id = new Label { name = "id" };
            id.style.fontSize = 11;
            id.style.unityFontStyleAndWeight = FontStyle.Bold;
            id.style.color = MSBrandTokens.WarmGray;
            id.style.width = 64;
            id.style.flexShrink = 0;
            id.style.marginLeft = 10;
            top.Add(id);

            var path = new Label { name = "path" };
            path.style.fontSize = 11;
            path.style.color = MSBrandTokens.Sky;
            path.style.flexGrow = 1;
            path.style.flexShrink = 1;
            path.style.overflow = Overflow.Hidden;
            path.style.textOverflow = TextOverflow.Ellipsis;
            path.style.whiteSpace = WhiteSpace.NoWrap;
            top.Add(path);

            var mb = new Label { name = "mb" };
            mb.style.fontSize = 11;
            mb.style.unityFontStyleAndWeight = FontStyle.Bold;
            mb.style.color = MSBrandTokens.Navy;
            mb.style.marginLeft = 12;
            mb.style.flexShrink = 0;
            top.Add(mb);
            row.Add(top);

            var msg = new Label { name = "msg" };
            msg.style.fontSize = 12;
            msg.style.color = MSBrandTokens.Ink;
            msg.style.marginLeft = 2;
            msg.style.overflow = Overflow.Hidden;
            msg.style.textOverflow = TextOverflow.Ellipsis;
            msg.style.whiteSpace = WhiteSpace.NoWrap;
            row.Add(msg);
            return row;
        }

        private void BindFindingRow(VisualElement el, int i)
        {
            if (i < 0 || i >= _visible.Count) return;
            var f = _visible[i];

            var sev = el.Q<Label>("sev");
            sev.text = f.severityLabel;
            sev.style.backgroundColor = SevColor(f.Sev);

            el.Q<Label>("id").text = f.id;
            el.Q<Label>("path").text = string.IsNullOrEmpty(f.path) ? "(project-wide)"
                : f.path + (f.line > 0 ? ":" + f.line : "");
            el.Q<Label>("mb").text = f.estimatedBytes > 0 ? "~" + TextureAnalyzer.Fmt(f.estimatedBytes) : "";
            el.Q<Label>("msg").text = f.message;
        }

        // ── detail card ───────────────────────────────────────────────────────

        private void OnSelection(IEnumerable<object> selection)
        {
            _detailPane.Clear();
            var f = selection.FirstOrDefault() as Finding;
            if (f == null)
            {
                _detailPane.style.display = DisplayStyle.None;
                return;
            }
            _detailPane.style.display = DisplayStyle.Flex;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            _detailPane.Add(row);

            // asset preview thumbnail, when the finding points at a previewable asset
            var target = string.IsNullOrEmpty(f.path) ? null : AssetDatabase.LoadMainAssetAtPath(f.path);
            if (target != null)
            {
                var img = new Image { scaleMode = ScaleMode.ScaleToFit };
                img.style.width = 84;
                img.style.height = 84;
                img.style.flexShrink = 0;
                img.style.marginRight = 14;
                img.style.backgroundColor = new Color(0f, 0f, 0f, 0.05f);
                Rounded(img, 4);
                SetPreview(img, target);
                row.Add(img);
            }

            var content = new VisualElement();
            content.style.flexGrow = 1;
            content.style.flexShrink = 1;
            row.Add(content);

            var headline = new Label(f.severityLabel + "  ·  " + f.id
                + (f.instances > 1 ? "  ·  " + f.instances + " instances" : "")
                + (f.estimatedBytes > 0 ? "  ·  ~" + TextureAnalyzer.Fmt(f.estimatedBytes) + " recoverable" : ""));
            headline.style.fontSize = 10;
            headline.style.letterSpacing = 1f;
            headline.style.unityFontStyleAndWeight = FontStyle.Bold;
            headline.style.color = SevColor(f.Sev);
            headline.style.marginBottom = 6;
            content.Add(headline);

            var msg = new Label(f.message);
            msg.style.whiteSpace = WhiteSpace.Normal;
            msg.style.color = MSBrandTokens.Ink;
            msg.style.fontSize = 12;
            content.Add(msg);

            if (!string.IsNullOrEmpty(f.fix))
            {
                var fix = new Label("Fix — " + f.fix);
                fix.style.whiteSpace = WhiteSpace.Normal;
                fix.style.color = MSBrandTokens.WarmGray;
                fix.style.fontSize = 12;
                fix.style.marginTop = 4;
                content.Add(fix);
            }

            if (!string.IsNullOrEmpty(f.path))
            {
                var actions = new VisualElement();
                actions.style.flexDirection = FlexDirection.Row;
                actions.style.marginTop = 8;
                var ping = MakeButton(f.line > 0 ? "Open at line " + f.line : "Ping asset", () =>
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(f.path);
                    if (obj == null) return;
                    if (f.line > 0) AssetDatabase.OpenAsset(obj, f.line);
                    else EditorGUIUtility.PingObject(obj);
                }, primary: false);
                ping.style.marginLeft = 0;
                actions.Add(ping);
                content.Add(actions);
            }
        }

        // AssetPreview renders asynchronously — show the mini thumbnail immediately
        // and poll briefly for the real preview.
        private static void SetPreview(Image img, UnityEngine.Object target)
        {
            var tex = AssetPreview.GetAssetPreview(target);
            if (tex != null)
            {
                img.image = tex;
                return;
            }
            img.image = AssetPreview.GetMiniThumbnail(target);
            img.schedule.Execute(() =>
            {
                var t = AssetPreview.GetAssetPreview(target);
                if (t != null) img.image = t;
            }).Every(200).ForDuration(4000);
        }

        private void RefreshFindings()
        {
            _visible = new List<Finding>();
            if (_report != null && _selectedCategory != null)
            {
                var cat = _report.Category(_selectedCategory);
                if (cat != null)
                {
                    _visible = cat.findings
                        .Where(f => _severityFilter.Contains(f.Sev) || f.Sev == Severity.Blocker)
                        .Where(f => string.IsNullOrEmpty(_search)
                            || (f.path != null && f.path.ToLowerInvariant().Contains(_search.ToLowerInvariant())))
                        .OrderBy(f => OwnRank(f.path))
                        .ThenBy(f => f.severity)
                        .ThenByDescending(f => f.estimatedBytes)
                        .ToList();
                    _statusLabel.text = cat.State == CategoryState.Complete
                        ? _visible.Count + " of " + cat.findings.Count + " findings shown — your own code and assets first, worst first"
                        : cat.stateNote;
                }
            }
            if (_detailPane != null)
            {
                _detailPane.Clear();
                _detailPane.style.display = DisplayStyle.None;
            }
            if (_findingsList != null)
            {
                _findingsList.itemsSource = _visible;
#if UNITY_2021_2_OR_NEWER
                _findingsList.Rebuild();
#else
                _findingsList.Refresh();
#endif
            }
        }

        // Own work ranks above bought/third-party content: a singleton in the
        // team's own Assets folders is fixable today; one inside an imported SDK
        // or store pack is a vendor conversation. 0 = own, 1 = third-party.
        private static int OwnRank(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;              // project-wide
            if (!path.StartsWith("Assets/")) return 1;             // Packages/, Library/
            string low = path.ToLowerInvariant();
            if (low.Contains("/plugins/") || low.Contains("/thirdparty/")
                || low.Contains("/third party/") || low.Contains("/standard assets/")
                || low.Contains("/external/") || low.Contains("/sdk/") || low.Contains("/sdks/")
                || low.StartsWith("assets/plugins") || low.StartsWith("assets/thirdparty"))
                return 1;
            return 0;
        }

        // ── footer ────────────────────────────────────────────────────────────

        private VisualElement BuildFooter()
        {
            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.alignItems = Align.Center;
            Pad(footer, 12, 28, 14, 28);
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = MSBrandTokens.Taupe;

            var exportBtn = MakeButton("Export Markdown", ExportMarkdown, primary: true);
            exportBtn.style.marginLeft = 0;
            footer.Add(exportBtn);
            footer.Add(MakeButton("Export JSON", ExportJson, primary: false));
            footer.Add(MakeButton("Copy Summary", CopySummary, primary: false));

            _verdictLabel = new Label("");
            _verdictLabel.style.fontSize = 11;
            _verdictLabel.style.color = MSBrandTokens.WarmGray;
            _verdictLabel.style.marginLeft = 18;
            _verdictLabel.style.flexGrow = 1;
            _verdictLabel.style.flexShrink = 1;
            _verdictLabel.style.overflow = Overflow.Hidden;
            _verdictLabel.style.textOverflow = TextOverflow.Ellipsis;
            _verdictLabel.style.whiteSpace = WhiteSpace.NoWrap;
            if (MSBrandTokens.FrauncesItalic != null)
                _verdictLabel.style.unityFont = MSBrandTokens.FrauncesItalic;
            footer.Add(_verdictLabel);

            footer.Add(MakeButton("Calibrate Atlas Padding", RunCalibration, primary: false));
            return footer;
        }

        private void ExportMarkdown()
        {
            if (!EnsureReport()) return;
            string path = EditorUtility.SaveFilePanel("Export MemoryShield report",
                "", SafeName() + "-memory-report.md", "md");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, MarkdownExporter.Export(_report));
            MemoryShieldTelemetry.Event("export.md");
            EditorUtility.RevealInFinder(path);
        }

        private void ExportJson()
        {
            if (!EnsureReport()) return;
            string path = EditorUtility.SaveFilePanel("Export MemoryShield JSON",
                "", SafeName() + "-memory-report.json", "json");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, JsonExporter.Export(_report));
            MemoryShieldTelemetry.Event("export.json");
            EditorUtility.RevealInFinder(path);
        }

        private void CopySummary()
        {
            if (!EnsureReport()) return;
            EditorGUIUtility.systemCopyBuffer = string.Format(
                "{0} — MemoryShield grade {1} ({2:0}/100). {3}",
                _report.projectName, _report.grade, _report.score, _report.executiveSummary);
            MemoryShieldTelemetry.Event("copy.summary");
            ShowNotification(new GUIContent("Summary copied"));
        }

        private void RunCalibration()
        {
            if (!EditorUtility.DisplayDialog("Calibrate atlas padding",
                "This packs a throwaway atlas per format to measure real page padding on this Unity version. " +
                "It dirties the Library and takes a minute. Run it?", "Run", "Cancel"))
                return;
            var cal = SpriteAtlasAnalyzer.RunCalibration();
            MemoryShieldTelemetry.Event("calibration", cal.verified ? "ok" : "failed");
            ShowNotification(new GUIContent(cal.verified
                ? "Calibration saved for " + Application.unityVersion
                : "Calibration failed — defaults kept"));
        }

        private bool EnsureReport()
        {
            if (_report != null) return true;
            ShowNotification(new GUIContent("Run a scan first"));
            return false;
        }

        private string SafeName()
        {
            var name = Application.productName;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name.Replace(' ', '-').ToLowerInvariant();
        }

        // ── colors ────────────────────────────────────────────────────────────

        private static Color SevColor(Severity s)
        {
            switch (s)
            {
                case Severity.Blocker:
                case Severity.High: return MSBrandTokens.Overdue;
                case Severity.Medium: return MSBrandTokens.Amber;
                case Severity.Low: return MSBrandTokens.WarmGray;
                default: return MSBrandTokens.Sky;
            }
        }

        private static Color SevTint(Severity s)
        {
            var c = SevColor(s);
            return new Color(c.r, c.g, c.b, 0.12f);
        }

        private static Color GradeColor(string grade)
        {
            switch (grade)
            {
                case "A": return MSBrandTokens.Shipped;
                case "B": return MSBrandTokens.Gold;
                case "C": return MSBrandTokens.Amber;
                default: return MSBrandTokens.Overdue;
            }
        }
    }
}
