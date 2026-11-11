# 声线工坊

一个普通话 Windows 变声应用。选好音色和音频设备后，点击「开始」便持续采集麦克风。Azure Speech 在说话停顿时确认短句，应用收到合成音频后边下载边播放。点击「结束」停止采集和播放，待播片段随本次会话清除。也可编辑文字后用「朗读文字」试听。

## 国内版

国内版使用阿里云百炼北京地域的 `paraformer-realtime-v2` 做实时识别，用 MiniMax 中国站的 `speech-2.8-turbo` 做流式合成。

1. 打开[百炼北京 API Key 页面](https://bailian.console.aliyun.com/cn-beijing/model/settings/api-key)，按页面提示开通模型服务，选择华北2（北京）和目标业务空间，点击「创建 API Key」。创建后立即复制完整密钥。
2. 如需使用业务空间专属地址，从百炼右上角的业务空间入口[复制业务空间 ID](https://help.aliyun.com/zh/model-studio/obtain-the-app-id-and-workspace-id)，与密钥使用同一个业务空间。留空时应用使用百炼北京的公共地址。
3. 在 [MiniMax 中国站 API Key 页面](https://platform.minimax.cn/console/keys)创建或复制普通开放平台 API Key。
4. 打开国内版「设置」，填入两把密钥和可选的业务空间 ID。点击「测试连接」会尝试启动阿里云识别任务，并用 MiniMax 合成一句「你好」，合成可能产生少量费用。点击「保存设置」后，Windows 会为当前账户加密保存。

麦克风音频发给阿里云识别。国内版会在识别结果逐步稳定时提前合成长句的前半段，停顿后补播剩余文字。已播出的片段无法随最终识别结果回改。输入设备、输出设备和开始、结束操作与 Azure 版相同。国内版提供温柔女声、甜美女声、俏皮童趣和青年男声。

构建国内版时可用 `dotnet publish -c Release -r win-x64 --self-contained false -p:DomesticSpeechBuild=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin/domestic`。

## 启动

在 Windows 上安装 .NET 10 SDK，从项目目录运行：

```powershell
dotnet run
```

也可以双击发布的 `VoiceChange.exe`。框架依赖发布需要 .NET 10 Desktop Runtime。首次构建从 NuGet 安装 Azure Speech SDK 和 Windows 音频依赖。Azure Speech SDK 在 Windows 上需要 Microsoft Visual C++ Redistributable（x64）。

1. 在 Azure 公有云中创建或使用已有的 Speech 资源，确认其公有网络和资源密钥访问已启用。
2. 在「设置」填写 Azure Speech 区域代码和同一资源的密钥。Southeast Asia 填 `southeastasia`，也可以粘贴门户的 `https://southeastasia.api.cognitive.microsoft.com/`。已配置自定义域名的资源可以填写完整的 `https://<自定义域名>.cognitiveservices.azure.com/`。
3. 点击「测试连接」验证密钥和音色列表接口，再点击「保存设置」。区域和密钥由 Windows 加密保存在当前账户中，重启应用后会自动读取。
4. 选择物理麦克风和耳机，点「测试输出」检查本地播放，再点击「开始」说话。

也可在启动前用 `SPEECH_REGION` 和 `SPEECH_KEY` 环境变量传入。已有 `SPEECH_RESOURCE` 的自定义域名配置仍可使用。

测试音无需联网。实时识别和合成需要联网，按所用 Azure 资源的配额及计费规则执行。「开始」后麦克风音频会持续发送给 Azure Speech 识别，确认后的文字会提交给 Azure Speech 合成。应用不会将录音或文字写入磁盘。

## 音色模板

| 模板 | Azure 普通话音色 |
| --- | --- |
| 温柔女声 | 晓晓 · `zh-CN-XiaoxiaoNeural` |
| 活泼女声 | 晓伊 · `zh-CN-XiaoyiNeural` |
| 清亮童声 | 晓双 · `zh-CN-XiaoshuangNeural` |
| 青年男声 | 云希 · `zh-CN-YunxiNeural` |

## 让对方听到合成音

1. 安装 [VB-CABLE](https://vb-audio.com/Cable/index.htm)，然后在声线工坊点「刷新设备」。
2. 在声线工坊选择物理麦克风作为输入，选择 **CABLE Input** 作为播放设备。
3. 在聊天、游戏或直播软件中选择 **CABLE Output** 作为麦克风，保留耳机作为聊天软件的扬声器。
4. 用声线工坊的「测试输出」观察聊天软件的麦克风电平，再用一段中文录音试通话。若合成音被过滤，可检查聊天软件的降噪与自动静音设置。

输出以短句为单位，延迟取决于说话停顿、网络及服务响应。为减少长时间排队，待播列表满时会跳过最早的待播片段。虚拟麦克风由外部驱动提供。

[Azure 短音频识别](https://learn.microsoft.com/zh-cn/azure/ai-services/speech-service/rest-speech-to-text-short) · [Azure 语音合成](https://learn.microsoft.com/zh-cn/azure/ai-services/speech-service/rest-text-to-speech) · [Azure 音色列表](https://learn.microsoft.com/zh-cn/azure/ai-services/speech-service/language-support?tabs=tts)

## RVC Windows 版

用 `LocalVoice/Rvc/VoiceChange.Rvc.csproj` 编译启动器。选好 RVC 目录后，菜单会列出 `assets/weights` 和 `assets/indices` 中可配对的音色，也可以选择自定义模型和索引。便携版可从 Release 下载，解压到英文路径后双击 `Start-XVoice.cmd`。包内为官方 NVIDIA 整合环境。

GitHub 分卷版需将所有 `.zip.001`、`.zip.002` 等文件下载到同一目录，然后用 7-Zip 打开第一卷。
