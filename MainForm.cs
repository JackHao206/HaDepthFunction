using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace DepthFusionApp
{

/// <summary>
/// 景深融合主界面：选择多张不同对焦位置的图像 -> 点击“执行景深融合” -> 显示/保存全焦结果图。
/// </summary>
public class MainForm : Form
{
    private readonly ListBox _lstImages;
    private readonly Button _btnAddFiles;
    private readonly Button _btnAddFolder;
    private readonly Button _btnRemove;
    private readonly Button _btnClear;
    private readonly ComboBox _cboFilter;
    private readonly NumericUpDown _numMask;
    private readonly Button _btnRun;
    private readonly ComboBox _cboView;
    private readonly Button _btnSave;
    private readonly HWindowControl _view;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _lblStatus;
    private readonly ToolStripStatusLabel _lblInfo;

    private readonly List<string> _paths = new();
    private FusionResult _result;
    private bool _running;

    private const string ViewFused = "融合结果图";
    private const string ViewDepth = "深度索引图";
    private const string ViewConfidence = "置信度图";

    public MainForm()
    {
        Text = "景深融合工具（Depth From Focus）";
        Width = 1240;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);

        // ---------- 左侧操作面板 ----------
        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(10) };

        var grpImages = new GroupBox { Text = "1. 选择图像（按对焦顺序）", Left = 10, Top = 10, Width = 272, Height = 330 };
        _lstImages = new ListBox { Left = 12, Top = 24, Width = 246, Height = 220, IntegralHeight = false };
        _btnAddFiles = new Button { Text = "添加图像…", Left = 12, Top = 252, Width = 118, Height = 30 };
        _btnAddFolder = new Button { Text = "添加文件夹", Left = 140, Top = 252, Width = 118, Height = 30 };
        _btnRemove = new Button { Text = "移除选中", Left = 12, Top = 288, Width = 118, Height = 30 };
        _btnClear = new Button { Text = "清空列表", Left = 140, Top = 288, Width = 118, Height = 30 };
        grpImages.Controls.AddRange(new Control[] { _lstImages, _btnAddFiles, _btnAddFolder, _btnRemove, _btnClear });

        var grpParams = new GroupBox { Text = "2. 融合参数", Left = 10, Top = 348, Width = 272, Height = 100 };
        var lblFilter = new Label { Text = "清晰度滤波：", Left = 12, Top = 28, Width = 90, TextAlign = ContentAlignment.MiddleLeft };
        _cboFilter = new ComboBox { Left = 108, Top = 25, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        // depth_from_focus 的 Filter 参数仅支持 highpass / bandpass（HALCON 文档），其他值会报 #1301
        _cboFilter.Items.AddRange(new object[] { "highpass", "bandpass" });
        _cboFilter.SelectedIndex = 0;
        var tipFilter = new ToolTip();
        tipFilter.SetToolTip(_cboFilter, "highpass：高通滤波（默认）\r\nbandpass：带通滤波，抑制高频噪声，适合噪声较强的图像");
        var lblMask = new Label { Text = "深度平滑窗口：", Left = 12, Top = 62, Width = 100, TextAlign = ContentAlignment.MiddleLeft };
        _numMask = new NumericUpDown { Left = 118, Top = 59, Width = 60, Minimum = 1, Maximum = 99, Increment = 2, Value = 9 };
        var lblMaskUnit = new Label { Text = "像素（奇数）", Left = 184, Top = 62, Width = 80, TextAlign = ContentAlignment.MiddleLeft };
        grpParams.Controls.AddRange(new Control[] { lblFilter, _cboFilter, lblMask, _numMask, lblMaskUnit });

        _btnRun = new Button { Text = "执行景深融合", Left = 10, Top = 458, Width = 272, Height = 44 };
        _btnRun.Font = new Font(_btnRun.Font, FontStyle.Bold);

        var grpView = new GroupBox { Text = "3. 结果显示与保存", Left = 10, Top = 512, Width = 272, Height = 110 };
        _cboView = new ComboBox { Left = 12, Top = 28, Width = 246, DropDownStyle = ComboBoxStyle.DropDownList };
        _cboView.Items.AddRange(new object[] { ViewFused, ViewDepth, ViewConfidence });
        _cboView.SelectedIndex = 0;
        _btnSave = new Button { Text = "保存当前显示图像…", Left = 12, Top = 64, Width = 246, Height = 32, Enabled = false };
        grpView.Controls.AddRange(new Control[] { _cboView, _btnSave });

        leftPanel.Controls.AddRange(new Control[] { grpImages, grpParams, _btnRun, grpView });

        // ---------- 右侧 Halcon 显示窗口 ----------
        _view = new HWindowControl
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };

        // ---------- 底部状态栏 ----------
        _status = new StatusStrip();
        _lblStatus = new ToolStripStatusLabel("就绪：请添加至少 2 张不同对焦位置的图像。") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _lblInfo = new ToolStripStatusLabel("");
        _status.Items.AddRange(new ToolStripItem[] { _lblStatus, _lblInfo });

        Controls.Add(_view);
        Controls.Add(leftPanel);
        Controls.Add(_status);

        _btnAddFiles.Click += BtnAddFiles_Click;
        _btnAddFolder.Click += BtnAddFolder_Click;
        _btnRemove.Click += (s, e) => RemoveSelected();
        _btnClear.Click += (s, e) => ClearList();
        _btnRun.Click += BtnRun_Click;
        _cboView.SelectedIndexChanged += (s, e) => Redraw();
        _btnSave.Click += BtnSave_Click;
        _view.SizeChanged += (s, e) => Redraw();
    }

    // ---------------------------------------------------------------- 选图

    private static readonly string[] SupportedExtensions =
        { ".bmp", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".gif", ".jp2" };

    private void BtnAddFiles_Click(object sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择多张不同对焦位置的图像",
            Filter = "图像文件|*.bmp;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.gif;*.jp2|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            AddPaths(dlg.FileNames);
        }
    }

    private void BtnAddFolder_Click(object sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "选择包含一组对焦图像的文件夹（自动按文件名排序）" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var files = Directory.EnumerateFiles(dlg.SelectedPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, NaturalStringComparer.Instance)
                .ToArray();
            if (files.Length == 0)
            {
                MessageBox.Show(this, "所选文件夹中没有找到支持的图像文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AddPaths(files);
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (_paths.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            _paths.Add(p);
            _lstImages.Items.Add(Path.GetFileName(p));
            added++;
        }
        UpdateCountLabel();
        if (added > 0)
        {
            _lblStatus.Text = $"新增 {added} 张图像，共 {_paths.Count} 张。";
        }
    }

    private void RemoveSelected()
    {
        while (_lstImages.SelectedIndices.Count > 0)
        {
            int idx = _lstImages.SelectedIndices[0];
            _lstImages.Items.RemoveAt(idx);
            _paths.RemoveAt(idx);
        }
        UpdateCountLabel();
    }

    private void ClearList()
    {
        _paths.Clear();
        _lstImages.Items.Clear();
        UpdateCountLabel();
    }

    private void UpdateCountLabel()
    {
        _lblInfo.Text = $"图像数量：{_paths.Count}";
        _lblStatus.Text = _paths.Count < 2
            ? "就绪：请添加至少 2 张不同对焦位置的图像。"
            : $"已选择 {_paths.Count} 张图像，可以执行融合。";
    }

    // ---------------------------------------------------------------- 融合

    private async void BtnRun_Click(object sender, EventArgs e)
    {
        if (_running)
        {
            return;
        }
        if (_paths.Count < 2)
        {
            MessageBox.Show(this, "请先添加至少 2 张不同对焦位置的图像。", "无法融合",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetRunning(true);
        var progress = new Progress<string>(msg => _lblStatus.Text = msg);
        var paths = _paths.ToList();
        var filter = (string)_cboFilter.SelectedItem;
        var mask = (int)_numMask.Value;

        try
        {
            var result = await Task.Run(() => DepthFusionService.Run(paths, filter, mask, progress));

            _result?.Dispose();
            _result = result;
            _btnSave.Enabled = true;
            _cboView.SelectedItem = ViewFused;
            Redraw();
            _lblStatus.Text = $"融合完成：{result.ImageCount} 张图像，用时 {result.ElapsedMs} ms，结果 {result.Width}×{result.Height}。";
        }
        catch (HalconException hex)
        {
            MessageBox.Show(this, "Halcon 算子执行失败：\n" + hex.Message, "融合失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblStatus.Text = "融合失败（Halcon 异常）。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "融合失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblStatus.Text = "融合失败。";
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _btnRun.Enabled = !running;
        _btnAddFiles.Enabled = _btnAddFolder.Enabled = _btnRemove.Enabled = _btnClear.Enabled = !running;
        _btnRun.Text = running ? "融合中，请稍候…" : "执行景深融合";
        Cursor = running ? Cursors.WaitCursor : Cursors.Default;
    }

    // ---------------------------------------------------------------- 显示

    private void Redraw()
    {
        if (_result == null || !_view.IsHandleCreated)
        {
            return;
        }

        HObject toShow = (string)_cboView.SelectedItem switch
        {
            ViewDepth => _result.Depth,
            ViewConfidence => _result.Confidence,
            _ => _result.Fused,
        };

        try
        {
            HTuple hwnd = _view.HalconID;
            HOperatorSet.GetImageSize(toShow, out HTuple imgW, out HTuple imgH);

            // 保持长宽比，计算 SetPart 的可视区域（居中留边）
            double cw = Math.Max(1, _view.Width);
            double ch = Math.Max(1, _view.Height);
            double scale = Math.Min(cw / imgW.D, ch / imgH.D);
            double partW = cw / scale;
            double partH = ch / scale;
            double row1 = (imgH.D - partH) / 2.0;
            double col1 = (imgW.D - partW) / 2.0;

            HOperatorSet.SetPart(hwnd, row1, col1, row1 + partH - 1, col1 + partW - 1);
            HOperatorSet.ClearWindow(hwnd);
            HOperatorSet.DispObj(toShow, hwnd);
        }
        catch (HalconException)
        {
            // 窗口尚未就绪时忽略重绘异常
        }
    }

    // ---------------------------------------------------------------- 保存

    private void BtnSave_Click(object sender, EventArgs e)
    {
        if (_result == null)
        {
            return;
        }

        bool isFused = (string)_cboView.SelectedItem == ViewFused;
        using var dlg = new SaveFileDialog
        {
            Title = "保存图像",
            Filter = isFused ? "PNG 图像|*.png|TIFF 图像|*.tif|BMP 图像|*.bmp" : "TIFF 图像|*.tif|PNG 图像|*.png",
            FileName = isFused ? "fused.png" : ((string)_cboView.SelectedItem == ViewDepth ? "depth.tif" : "confidence.tif"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        HObject toSave = isFused ? _result.Fused
            : (string)_cboView.SelectedItem == ViewDepth ? _result.Depth : _result.Confidence;

        try
        {
            // 深度图/置信度图为 real 类型，用 tiff 保存可保留全部数值；融合图用 png
            string format = Path.GetExtension(dlg.FileName).ToLowerInvariant() == ".tif" ? "tiff" : "png";
            HOperatorSet.WriteImage(toSave, format, 0, dlg.FileName);
            _lblStatus.Text = "已保存：" + dlg.FileName;
        }
        catch (HalconException hex)
        {
            MessageBox.Show(this, "保存失败：\n" + hex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _result?.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>
/// 文件名自然排序（img2 &lt; img10），基于系统 StrCmpLogicalW。
/// </summary>
internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string x, string y) => StrCmpLogicalW(x, y);
}
}
