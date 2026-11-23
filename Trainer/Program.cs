using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
ApplicationConfiguration.Initialize();
Application.Run(new TrainerForm());

sealed class TrainerForm : Form
{
    readonly TextBox root = new(), data = new(), name = new() { Text = "voice01" }, output = new();
    readonly NumericUpDown gpu = new() { Maximum = 15 }, batch = new() { Minimum = 1, Maximum = 32, Value = 2 },
        epochs = new() { Minimum = 10, Maximum = 1000, Value = 100 }, workers = new() { Minimum = 1, Maximum = 32, Value = 8 };
    readonly TextBox log = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Dock = DockStyle.Fill };
    readonly Button start = new() { Text = "一键训练并导出", AutoSize = true }, stop = new() { Text = "停止", Enabled = false };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 100 };
    readonly Label progressText = new() { Text = "等待开始", AutoSize = true };
    readonly System.Windows.Forms.Timer poll = new() { Interval = 1500 };
    int stage, totalEpochs;
    string trainingLog = "";
    Process? process;
    readonly string deps;
    public TrainerForm()
    {
        Font = new Font("Microsoft YaHei UI", 10);
        log.Font = new Font("Microsoft YaHei UI", 10);
        Text = "声线工坊 · 一键音色训练"; Width = 820; Height = 720;
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..",".."));
        root.Text = Path.Combine(workspace,"bin","rvc-runtime","RVC20260718Nvidia");
        deps = Path.Combine(workspace,"obj","rvc-export-deps");
        output.Text = Path.Combine(workspace,"bin","trained-voices");
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(15) };
        Controls.Add(log); Controls.Add(panel);
        Row("RVC 目录",root,()=>Folder(root));
        Row("WAV 文件目录", data, () => Folder(data));
        Row("音色名称（英文）",name);
        Row("输出目录",output,()=>Folder(output));
        Row("GPU 编号 · 默认 GTX 1060 / GPU 0",gpu);
        Row("Batch · 1060 默认 2",batch); Row("训练轮数",epochs); Row("CPU 进程数",workers);
        panel.Controls.Add(start,1,panel.RowCount);
        panel.Controls.Add(stop,2,panel.RowCount++);
        panel.Controls.Add(progressText,0,panel.RowCount++);
        panel.SetColumnSpan(progressText,3);
        panel.Controls.Add(progress,0,panel.RowCount++);
        panel.SetColumnSpan(progress,3);
        poll.Tick += (_,_) =>
        {
            if(stage!=3 || !File.Exists(trainingLog)) return;
            try
            {
                using var file=new FileStream(trainingLog,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                file.Seek(Math.Max(0,file.Length-32768),SeekOrigin.Begin);
                using var reader=new StreamReader(file,Encoding.UTF8);
                UpdateEpoch(reader.ReadToEnd());
            }
            catch(IOException) { }
        };
        start.Click+=async(_,_)=>await Run();
        stop.Click+=(_,_)=> { if(process is {HasExited:false})process.Kill(true); };
        FormClosing+=(_,e)=> {if(process is {HasExited:false}) {e.Cancel=true;MessageBox.Show("请先停止训练后关闭。");}};
        void Row(string label,Control field,Action? browse=null)
        {
            int r=panel.RowCount++;
            panel.Controls.Add(new Label{Text=label,AutoSize=true},0,r);
            field.Width=430;panel.Controls.Add(field,1,r);
            if(browse is null)return;
            var button=new Button{Text="浏览"};
            button.Click+=(_,_)=>browse();
            panel.Controls.Add(button,2,r);
        }
    }

    void Folder(TextBox field)
    {
        using var dialog=new DirectoryPicker(field.Text);
        if(dialog.ShowDialog(this)==DialogResult.OK)field.Text=dialog.SelectedPath;
    }

    void Print(string? text)
    {
        if(text is null || IsDisposed)return;
        BeginInvoke(() =>
        {
            if(text.StartsWith("@XVOICE_STAGE:") && int.TryParse(text[14..],out int next))
            {
                if(next==stage)return;
                stage=next;progress.Style=ProgressBarStyle.Marquee;
                progressText.Text=$"步骤 {stage}/5 · "+(stage switch {
                    1=>"处理录音",2=>"提取 RMVPE 和人声特征",3=>"训练模型 · 等待轮次信息",
                    4=>"生成索引",5=>"导出声线文件",_=>"处理中"});
                return;
            }
            log.AppendText(text+Environment.NewLine);
            if(stage==3)UpdateEpoch(text);
        });
    }

    void UpdateEpoch(string text)
    {
        var matches=Regex.Matches(text,@"(?:轮次[：:]|Epoch[：:]?|epoch[=:])\s*(\d+)",RegexOptions.IgnoreCase);
        if(matches.Count==0)return;
        int current=int.Parse(matches[^1].Groups[1].Value);
        int percent=Math.Clamp(current*100/Math.Max(1,totalEpochs),0,100);
        progress.Style=ProgressBarStyle.Continuous;progress.Value=percent;
        progressText.Text=$"步骤 3/5 · 模型训练 {current}/{totalEpochs} 轮 · {percent}%";
    }

    async Task Run()
    {
        try
        {
            if(!Regex.IsMatch(name.Text,"^[A-Za-z][A-Za-z0-9_]{0,40}$"))throw new Exception("音色名称请使用英文、数字、下划线。");
            if(!Directory.Exists(data.Text)||!Directory.EnumerateFiles(data.Text,"*.wav").Any())throw new Exception("请选择包含 WAV 文件的文件夹。");
            if(Directory.Exists(Path.Combine(root.Text,"logs",name.Text)))throw new Exception("实验名已存在，请换一个名称。");
            var python=Path.Combine(root.Text,"runtime","python.exe");
            if(!File.Exists(python)||!Directory.Exists(deps))throw new Exception("请确认 RVC 开发环境和导出依赖目录。");
            var info=new ProcessStartInfo(python){WorkingDirectory=root.Text,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            foreach(var arg in new[]{"-X","utf8","-u",Path.Combine(AppContext.BaseDirectory,"train.py"),"--root",root.Text,"--data",data.Text,"--name",name.Text,"--output",Path.Combine(output.Text,name.Text),"--deps",deps,"--gpu",gpu.Value.ToString(),"--batch",batch.Value.ToString(),"--epochs",epochs.Value.ToString(),"--workers",workers.Value.ToString()})info.ArgumentList.Add(arg);
            info.Environment["PYTHONIOENCODING"]="utf-8";
            info.Environment["PYTHONUTF8"]="1";
            info.Environment["PATH"]=Path.GetDirectoryName(python)+";"+info.Environment["PATH"];
            process=new Process{StartInfo=info};process.OutputDataReceived+=(_,e)=>Print(e.Data);process.ErrorDataReceived+=(_,e)=>Print(e.Data);
            stage = 0;
            totalEpochs = (int)epochs.Value;
            trainingLog = Path.Combine(root.Text, "logs", name.Text, "train.log");
            progress.Style = ProgressBarStyle.Marquee;
            progressText.Text = "正在启动…";
            poll.Start();
            start.Enabled=false;stop.Enabled=true;log.Clear();process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();await process.WaitForExitAsync();Print("退出码："+process.ExitCode);
            progress.Style=ProgressBarStyle.Continuous;
            progress.Value=process.ExitCode==0?100:0;
            progressText.Text=process.ExitCode==0?"全部完成 · 声线文件已输出":"任务已停止或失败，请查看日志";
        } catch(Exception e){MessageBox.Show(e.Message);} finally{poll.Stop();process?.Dispose();process=null;start.Enabled=true;stop.Enabled=false;}
    }
}




