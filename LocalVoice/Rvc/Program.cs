using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceChange;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new RvcForm());
    }
}

internal sealed class RvcForm : Form
{
    private readonly TextBox runtime = new() { Dock = DockStyle.Fill };
    private readonly ComboBox voices = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox model = new() { Dock = DockStyle.Fill };
    private readonly TextBox index = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(630, 0) };
    private readonly Button start = new() { Text = "打开 RVC 实时变声", AutoSize = true };
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoiceChange", "rvc-settings.json");

    public RvcForm()
    {
        Text = "声线工坊 · RVC Windows";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(660, 300);
        MinimumSize = new Size(560, 310);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 3, AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(layout);
        AddPicker(layout, "RVC 目录", runtime, () =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = runtime.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                runtime.Text = dialog.SelectedPath;
        });
        var voiceRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "音色模板", AutoSize = true, Anchor = AnchorStyles.Left }, 0, voiceRow);
        layout.Controls.Add(voices, 1, voiceRow);
        voices.SelectedIndexChanged += (_, _) =>
        {
            if (voices.SelectedItem is not string name || name == "自选文件")
                return;
            model.Text = Path.Combine("assets", "weights", name + ".pth");
            index.Text = Path.Combine("assets", "indices", name + ".index");
        };
        AddPicker(layout, "音色模型", model, () => Pick(model, "RVC 音色模型 (*.pth)|*.pth"));
        AddPicker(layout, "检索索引", index, () => Pick(index, "RVC 检索索引 (*.index)|*.index"));
        layout.Controls.Add(start, 1, layout.RowCount++);
        layout.Controls.Add(status, 0, layout.RowCount++);
        layout.SetColumnSpan(status, 3);
        status.Text = "打开后，在 RVC 窗口选择麦克风和耳机，再点击开始音频转换。";
        start.Click += (_, _) => Launch();
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonNode.Parse(File.ReadAllText(SettingsPath));
                runtime.Text = settings?["runtime"]?.GetValue<string>();
                model.Text = settings?["model"]?.GetValue<string>();
                index.Text = settings?["index"]?.GetValue<string>();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            status.Text = "无法读取上次的设置，请重新选择目录和模型。";
        }
    }

    private static void AddPicker(TableLayoutPanel layout, string name, TextBox field, Action browse)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(field, 1, row);
        var button = new Button { Text = "浏览…", AutoSize = true };
        button.Click += (_, _) => browse();
        layout.Controls.Add(button, 2, row);
    }

    private void Pick(TextBox field, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            field.Text = dialog.FileName;
    }

    private async void Launch()
    {
        start.Enabled = false;
        try
        {
            var root = Path.GetFullPath(runtime.Text.Trim().Trim((char)34));
            var script = Path.Combine(root, "realtime_gui.py");
            var python = Path.Combine(root, "runtime", "python.exe");
            if (!File.Exists(python))
                python = Path.Combine(root, ".venv", "Scripts", "python.exe");
            if (!File.Exists(script) || !File.Exists(python))
                throw new InvalidOperationException("请选择含 realtime_gui.py 和 Python 环境的 RVC 目录。");
            var checkpoint = RequireFile(root, model.Text, ".pth");
            var retrieval = RequireFile(root, index.Text, ".index");

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, new JsonObject
            {
                ["runtime"] = root, ["model"] = checkpoint, ["index"] = retrieval
            }.ToJsonString());
            WriteRvcConfig(root, checkpoint, retrieval);
            var info = new ProcessStartInfo(python) { WorkingDirectory = root, UseShellExecute = false };
            if (Path.GetDirectoryName(python) == Path.Combine(root, "runtime"))
                info.ArgumentList.Add("-I");
            info.ArgumentList.Add(script);
            info.Environment["PATH"] = Path.GetDirectoryName(python) + Path.PathSeparator + info.Environment["PATH"];
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 RVC。");
            status.Text = "RVC 已启动。首次加载可能需要一段时间。";
            await process.WaitForExitAsync();
            status.Text = process.ExitCode == 0 ? "RVC 已关闭。" : $"RVC 退出，错误码 {process.ExitCode}。请检查 RVC 运行环境。";
        }
        catch (Exception error)
        {
            status.Text = error.Message;
        }
        finally
        {
            start.Enabled = true;
        }
    }

    private static string RequireFile(string root, string input, string extension)
    {
        var path = Path.GetFullPath(input.Trim().Trim((char)34), root);
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"请选择有效的 {extension} 文件。");
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Any(character => character > 127))
            throw new InvalidOperationException("RVC 要求模型和索引使用英文路径。");
        return fullPath;
    }

    private static void WriteRvcConfig(string root, string checkpoint, string retrieval)
    {
        var directory = Path.Combine(root, "configs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        foreach (var entry in JsonNode.Parse("""
            {"sg_hostapi":"","sg_input_device":"","sg_output_device":"","sg_wasapi_exclusive":false,
            "sr_type":"sr_model","threhold":-60,"pitch":0,"formant":0,"index_rate":0,
            "rms_mix_rate":0,"block_time":0.25,"crossfade_length":0.05,"extra_time":2.5,"f0method":"rmvpe"}
            """)!.AsObject())
        {
            if (!settings.ContainsKey(entry.Key))
                settings[entry.Key] = entry.Value?.DeepClone();
        }
        var changedIndex = settings["index_path"]?.GetValue<string>() != retrieval;
        settings["pth_path"] = checkpoint;
        settings["index_path"] = retrieval;
        if (changedIndex || retrieval.Length == 0)
            settings["index_rate"] = retrieval.Length == 0 ? 0 : 0.35;
        File.WriteAllText(path + ".voicechange.tmp", settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".voicechange.tmp", path, overwrite: true);
    }
}
