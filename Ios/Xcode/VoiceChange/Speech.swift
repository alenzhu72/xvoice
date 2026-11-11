import Foundation

struct Voice: Identifiable {
    let name: String
    let id: String

    static let presets = [
        Voice(name: "温柔女声", id: "Chinese (Mandarin)_Soft_Girl"),
        Voice(name: "甜美女声", id: "Chinese (Mandarin)_Sweet_Lady"),
        Voice(name: "俏皮童趣", id: "Chinese (Mandarin)_Cute_Spirit"),
        Voice(name: "青年男声", id: "male-qn-qingse"),
        Voice(name: "暖心闺蜜", id: "Chinese (Mandarin)_Warm_Bestie"),
        Voice(name: "清脆少女", id: "Chinese (Mandarin)_Crisp_Girl"),
        Voice(name: "温润青年", id: "Chinese (Mandarin)_Gentle_Youth"),
        Voice(name: "直爽少年", id: "Chinese (Mandarin)_Straightforward_Boy"),
        Voice(name: "电台主播", id: "Chinese (Mandarin)_Radio_Host"),
        Voice(name: "慈祥长辈", id: "Chinese (Mandarin)_Kind-hearted_Elder")
    ]
}

struct SpeechError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

struct SpeechConfig: Decodable {
    let aliyunKey: String
    let miniMaxKey: String

    static func load() throws -> Self {
        guard let url = Bundle.main.url(forResource: "SpeechConfig", withExtension: "plist") else {
            throw SpeechError("找不到语音服务配置。")
        }
        let config = try PropertyListDecoder().decode(Self.self, from: Data(contentsOf: url))
        guard config.aliyunKey.hasPrefix("sk-"), config.miniMaxKey.hasPrefix("sk-") else {
            throw SpeechError("请在 Xcode 工程的 SpeechConfig.plist 中填写两把 API Key。")
        }
        return config
    }
}

enum Speech {
    typealias Message = [String: Any]

    static func recognize(config: SpeechConfig, audio: AsyncStream<Data>,
                          ready: @escaping () async -> Void,
                          result: @escaping (String, Bool) async -> Void) async throws {
        let socket = connect("wss://dashscope.aliyuncs.com/api-ws/v1/inference", key: config.aliyunKey)
        defer { socket.cancel(with: .normalClosure, reason: nil) }
        try await withTaskCancellationHandler {
            try await send(socket, [
                "header": ["action": "run-task", "task_id": UUID().uuidString, "streaming": "duplex"],
                "payload": [
                    "task_group": "audio", "task": "asr", "function": "recognition",
                    "model": "paraformer-realtime-v2", "input": Message(),
                    "parameters": ["format": "pcm", "sample_rate": 16000, "language_hints": ["zh"],
                                   "max_sentence_silence": 250, "multi_threshold_mode_enabled": true, "heartbeat": true]
                ]
            ])
            let start = try await receive(socket)
            guard try aliyunEvent(start) == "task-started" else { throw SpeechError("阿里云未启动识别任务。") }
            await ready()
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask {
                    for await frame in audio { try await socket.send(.data(frame)) }
                }
                group.addTask {
                    while !Task.isCancelled {
                        let message = try await receive(socket)
                        let event = try aliyunEvent(message)
                        if event == "task-finished" { return }
                        guard event == "result-generated",
                              let payload = message["payload"] as? Message,
                              let output = payload["output"] as? Message,
                              let sentence = output["sentence"] as? Message,
                              sentence["heartbeat"] as? Bool != true else { continue }
                        await result(sentence["text"] as? String ?? "", sentence["sentence_end"] as? Bool == true)
                    }
                }
                defer {
                    socket.cancel(with: .normalClosure, reason: nil)
                    group.cancelAll()
                }
                _ = try await group.next()
            }
        } onCancel: {
            socket.cancel(with: .goingAway, reason: nil)
        }
    }

    static func synthesize(config: SpeechConfig, voice: Voice, text: String,
                           play: @escaping (Data) async throws -> Void) async throws {
        let socket = connect("wss://api.minimax.cn/ws/v1/t2a_v2", key: config.miniMaxKey)
        defer { socket.cancel(with: .normalClosure, reason: nil) }
        try await withTaskCancellationHandler {
            guard try miniMaxEvent(await receive(socket)) == "connected_success" else {
                throw SpeechError("MiniMax 未确认连接。")
            }
            try await send(socket, [
                "event": "task_start", "model": "speech-2.8-turbo", "language_boost": "Chinese",
                "voice_setting": ["voice_id": voice.id],
                "audio_setting": ["format": "pcm", "sample_rate": 24000, "channel": 1]
            ])
            guard try miniMaxEvent(await receive(socket)) == "task_started" else {
                throw SpeechError("MiniMax 未启动合成任务。")
            }
            try await send(socket, ["event": "task_continue", "text": text])
            var receivedAudio = false
            while !Task.isCancelled {
                let message = try await receive(socket)
                _ = try miniMaxEvent(message)
                if let data = message["data"] as? Message, let hex = data["audio"] as? String, !hex.isEmpty {
                    try await play(decodePCM(hex))
                    receivedAudio = true
                }
                if message["is_final"] as? Bool == true {
                    guard receivedAudio else { throw SpeechError("MiniMax 未返回音频。") }
                    try await send(socket, ["event": "task_finish"])
                    return
                }
            }
            try Task.checkCancellation()
        } onCancel: {
            socket.cancel(with: .goingAway, reason: nil)
        }
    }

    private static func connect(_ address: String, key: String) -> URLSessionWebSocketTask {
        var request = URLRequest(url: URL(string: address)!, timeoutInterval: 20)
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        let socket = URLSession.shared.webSocketTask(with: request)
        socket.maximumMessageSize = 2 * 1024 * 1024
        socket.resume()
        return socket
    }

    private static func send(_ socket: URLSessionWebSocketTask, _ payload: Message) async throws {
        let data = try JSONSerialization.data(withJSONObject: payload)
        try await socket.send(.string(String(decoding: data, as: UTF8.self)))
    }

    private static func receive(_ socket: URLSessionWebSocketTask) async throws -> Message {
        guard case let .string(text) = try await socket.receive(),
              let payload = try JSONSerialization.jsonObject(with: Data(text.utf8)) as? Message else {
            throw SpeechError("语音服务返回了无法读取的消息。")
        }
        return payload
    }

    private static func aliyunEvent(_ message: Message) throws -> String? {
        let header = message["header"] as? Message
        let event = header?["event"] as? String
        if event == "task-failed" {
            let code = header?["error_code"] as? String ?? "未知"
            throw SpeechError("阿里云识别失败（\(code)）。")
        }
        return event
    }

    private static func miniMaxEvent(_ message: Message) throws -> String? {
        let response = message["base_resp"] as? Message
        let code = response?["status_code"] as? Int ?? 0
        let event = message["event"] as? String
        if code != 0 || event == "task_failed" { throw SpeechError("MiniMax 合成失败（\(code)）。") }
        return event
    }

    private static func decodePCM(_ hex: String) throws -> Data {
        var bytes = Data()
        var high: UInt8?
        for character in hex.utf8 {
            let digit: UInt8
            switch character {
            case 48...57: digit = character - 48
            case 65...70: digit = character - 55
            case 97...102: digit = character - 87
            default: throw SpeechError("MiniMax 返回了无效音频。")
            }
            if let first = high {
                bytes.append(first << 4 | digit)
                high = nil
            } else { high = digit }
        }
        guard high == nil, bytes.count.isMultiple(of: 2) else { throw SpeechError("MiniMax 返回了不完整的音频。") }
        return bytes
    }
}

struct PhraseStream {
    private let punctuation = Set("，。！？；、,.!?;")
    private var previous: [Character] = []
    private var spoken: [Character] = []

    mutating func update(_ value: String, final: Bool) -> String? {
        let text = Array(value.trimmingCharacters(in: .whitespacesAndNewlines))
        if final {
            let offset: Int
            if text.starts(with: spoken) { offset = spoken.count }
            else {
                var remaining = spoken.filter { !$0.isPunctuation && !$0.isWhitespace }.count
                offset = text.prefix { character in
                    guard remaining > 0 else { return false }
                    if !character.isPunctuation && !character.isWhitespace { remaining -= 1 }
                    return true
                }.count
            }
            let fragment = clean(text.dropFirst(offset))
            previous = []
            spoken = []
            return fragment
        }
        let stable = zip(previous, text).prefix { $0.0 == $0.1 }.count
        previous = text
        guard stable > spoken.count, text.starts(with: spoken) else { return nil }
        let boundary = (spoken.count..<stable).last { punctuation.contains(text[$0]) }.map { $0 + 1 }
        let end: Int
        if let boundary = boundary, boundary - spoken.count >= 4 { end = boundary }
        else {
            guard stable - spoken.count >= 9 else { return nil }
            var split = stable - 2
            while split > spoken.count && text[split].isASCII && text[split - 1].isASCII
                    && !text[split].isWhitespace && !text[split - 1].isWhitespace { split -= 1 }
            guard split - spoken.count >= 4 else { return nil }
            end = split
        }
        let fragment = clean(text[spoken.count..<end])
        spoken = Array(text[..<end])
        return fragment
    }

    private func clean(_ characters: ArraySlice<Character>) -> String? {
        let value = String(characters.drop { punctuation.contains($0) || $0.isWhitespace })
        return value.isEmpty ? nil : value
    }
}
