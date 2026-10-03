# ClickZen 变量同步协议（protocol 2）

ClickZen 可以把自动化方案中的变量通过 TCP 共享给其他程序：外部脚本可以读取变量、修改变量，并在变量变化时收到推送。典型用途是多台电脑/多个程序之间协作（例如一台机器统计结果，另一台根据结果决定下一步）。

服务在 **变量** 页的「同步服务」卡片中开启，可设置端口（默认 `9527`）、令牌和最大连接数。

## 传输

- TCP，UTF-8 编码的 JSON，**每条消息一行**，以 `\n` 结尾（`\r\n` 也接受）。空行会被忽略。
- 一条消息是一个 JSON 对象，`type` 字段表示消息类型。
- 任何请求都可以带 `id`（字符串或数字），服务器在对应回复中原样带回，便于对应请求与回复。
- 单条消息最大 64 KiB，超过会收到 `too_large` 错误并断开。

## 握手

连接后的**第一条消息必须是 `hello`**，否则服务器回复错误并断开：

```json
{"type":"hello","token":"<令牌>","client":"my-script","protocol":2}
```

| 字段 | 说明 |
|---|---|
| `token` | 在 ClickZen 中设置的令牌。服务器未设置令牌时可省略。 |
| `client` | 客户端名称，显示在 ClickZen 的已连接客户端列表中（最长 64 字符）。 |
| `protocol` | 协议版本，当前为 `2`。省略时视为 2；其他值会被拒绝。 |

成功：

```json
{"type":"welcome","protocol":2,"server":"ClickZen 2.0.0"}
```

失败（随后连接被关闭）：

| `code` | 原因 |
|---|---|
| `hello_required` | 第一条消息不是 `hello` |
| `auth` | 令牌错误 |
| `protocol` | 协议版本不支持 |
| `bad_json` | 不是合法 JSON 对象 |
| `too_many_clients` | 已达到最大连接数（连接时即回复） |
| `timeout` | 10 秒内没有发送 `hello` |

## 心跳

服务器在 **30 秒**内没有收到客户端的任何消息就会断开（回复 `timeout` 错误）。没有其它请求时，请定期发送 `ping`（建议每 10 秒）：

```json
{"type":"ping"}
→ {"type":"pong"}
```

## 变量的可见性与同步方向

每个变量在方案中有「网络同步」方向，决定外部程序能做什么：

| 方向 | 读取 / 订阅 | 写入 |
|---|---|---|
| 不同步（None） | 否（如同不存在） | 否 |
| 发送（Send） | 是 | 否 |
| 接收（Receive） | 否 | 是 |
| 双向（Both） | 是 | 是 |

变量是强类型的（整数、小数、布尔、字符串）。写入的值会转换成变量声明的类型，例如向整数变量写入 `"3"` 会得到 `3`。

## 请求

### get —— 读取一个变量

```json
{"type":"get","name":"score","id":1}
→ {"type":"value","id":1,"name":"score","value":5}
```

变量不存在或不可读时回复 `{"type":"error","code":"not_found",...}`。

### getAll —— 读取全部可读变量

```json
{"type":"getAll"}
→ {"type":"values","values":{"score":5,"status":"idle"}}
```

### set —— 写入变量

```json
{"type":"set","name":"command","value":"go","id":2}
→ {"type":"ok","id":2}
```

`value` 可以是数字、字符串或布尔值。变量不接受写入时回复 `read_only` 错误；缺少字段时回复 `bad_request`。

### subscribe / unsubscribe —— 订阅变化

```json
{"type":"subscribe","names":["score","status"]}
→ {"type":"ok"}
→ {"type":"changed","name":"score","value":5,"source":"reset"}
→ {"type":"changed","name":"status","value":"idle","source":"reset"}
```

- 可以用 `names`（数组）或 `name`（单个）。`"*"` 表示订阅全部可读变量。
- 订阅后服务器立即为每个（非 `*`）变量推送一次当前值，保证客户端从一致的状态开始。
- `unsubscribe` 用法相同。

## 推送

已订阅的变量发生变化时，服务器主动发送：

```json
{"type":"changed","name":"score","value":6,"source":"engine"}
```

| `source` | 含义 |
|---|---|
| `engine` | 自动化方案运行时修改 |
| `user` | 用户在变量页手动修改 |
| `network` | 另一个同步客户端写入 |
| `reset` | 方案启动/停止时重置为初值，或订阅时的初始值 |

- 只有值真正改变时才推送；同一个值重复写入不会推送。
- 客户端自己写入的变化**不会**推送回该客户端。
- 推送与请求的回复共用同一条连接，请按 `type` 区分。

## 错误

```json
{"type":"error","id":3,"code":"read_only","message":"Variable 'status' does not accept writes."}
```

握手之后的错误（`not_found`、`read_only`、`bad_request`、`bad_json`、`unknown_type`）不会断开连接；`timeout`、`too_large` 会断开。

## 示例客户端

### Python

```python
import json, socket, threading, time

HOST, PORT, TOKEN = "127.0.0.1", 9527, "your-token"

sock = socket.create_connection((HOST, PORT))
reader = sock.makefile("r", encoding="utf-8")

def send(msg):
    sock.sendall((json.dumps(msg) + "\n").encode("utf-8"))

send({"type": "hello", "token": TOKEN, "client": "python-demo", "protocol": 2})
welcome = json.loads(reader.readline())
assert welcome["type"] == "welcome", welcome

def heartbeat():
    while True:
        time.sleep(10)
        send({"type": "ping"})

threading.Thread(target=heartbeat, daemon=True).start()

send({"type": "subscribe", "names": ["score"]})
send({"type": "set", "name": "command", "value": "start"})

for line in reader:
    msg = json.loads(line)
    if msg["type"] == "changed":
        print(f"{msg['name']} = {msg['value']} ({msg['source']})")
    elif msg["type"] == "error":
        print("error:", msg["code"], msg["message"])
```

### C#

```csharp
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using var tcp = new TcpClient("127.0.0.1", 9527);
var stream = tcp.GetStream();
var reader = new StreamReader(stream, Encoding.UTF8);
var writeLock = new SemaphoreSlim(1, 1);

async Task SendAsync(object msg)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg) + "\n");
    await writeLock.WaitAsync();
    try { await stream.WriteAsync(bytes); } finally { writeLock.Release(); }
}

await SendAsync(new { type = "hello", token = "your-token", client = "csharp-demo", protocol = 2 });
var welcome = JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement;
if (welcome.GetProperty("type").GetString() != "welcome")
    throw new Exception(welcome.GetRawText());

_ = Task.Run(async () =>
{
    while (true) { await Task.Delay(10_000); await SendAsync(new { type = "ping" }); }
});

await SendAsync(new { type = "subscribe", names = new[] { "score" } });
await SendAsync(new { type = "set", name = "command", value = "start" });

while (await reader.ReadLineAsync() is { } line)
{
    var msg = JsonDocument.Parse(line).RootElement;
    if (msg.GetProperty("type").GetString() == "changed")
        Console.WriteLine($"{msg.GetProperty("name")} = {msg.GetProperty("value")}");
}
```

## 与旧版（1.x）的区别

- 旧版连接后发送 `auth` 消息；新版第一条必须是 `hello`，令牌错误会立即断开。
- 旧版按固定间隔轮询比较后推送；新版在变量变化时立即推送，且不会把客户端自己的写入回推。
- 同步方向真正生效：只读变量拒绝写入，只写变量不会被读取或推送。
- 旧版的 `sync_variables`、`broadcast`、`delete_variable`、`clear_all` 不再提供。
