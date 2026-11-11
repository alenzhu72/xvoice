# 声线工坊 iPhone 版

应用支持 iOS 15 及以上，使用阿里云北京实时识别和 MiniMax 流式合成。启动后允许麦克风访问，选择音色，再点「开始变声」。应用离开前台时会结束录音。

## 在 Mac 上构建

1. 安装 .NET 10 和与 iOS workload 匹配的 Xcode，然后运行 `dotnet workload install ios`。 [Xcode 版本要求](https://learn.microsoft.com/en-us/dotnet/ios/troubleshooting/xcode-requirement)
2. 准备一个 UTF-8 配置文件。第一行留空以使用阿里云北京公共地址，第二行填写阿里云 API Key，第三行填写 MiniMax API Key。构建时会把这三行打进应用，发布包可被提取出密钥。
3. 在 Mac 的钥匙串安装 Apple 签名证书，并配置与 Bundle ID 对应的描述文件。若使用自己的 Bundle ID，先更新 `Ios/Info.plist`。 [设备签名说明](https://learn.microsoft.com/en-us/dotnet/maui/ios/device-provisioning/manual-provisioning)
4. 在源码包根目录执行以下命令，替换配置文件、签名证书和描述文件的值。

```bash
dotnet publish Ios/VoiceChange.iOS.csproj -c Release -r ios-arm64 \
  -p:BuildIpa=true -p:ArchiveOnBuild=true \
  -p:SpeechConfigFile=/absolute/path/to/speech-config.txt \
  -p:CodesignKey="Apple Development: Your Name (TEAMID)" \
  -p:CodesignProvision="Your Provisioning Profile"
```
