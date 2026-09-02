# DeepSeek 翻译 API：官方最新规范研究笔记

> 核对日期：2026-08-31（America/Los_Angeles）  
> 范围：DeepSeek 官方 API 文档的一手资料；本文不包含对仓库实现的代码审查。  
> 时效提醒：DeepSeek 当前文档已进入 V4 时代，旧的 `deepseek-chat` / `deepseek-reasoner` 资料已过时，不能继续作为当前实现依据。

当前规范页没有逐页展示 `Last-Modified`；“最新”以核对日可见的规范页和[更新日志](https://api-docs.deepseek.com/updates/)为准。更新日志截至 2026-08-21（Vision Exp 发布），其中 V4 Flash 当前版本于 2026-07-31 更新、V4 Pro 当前版本于 2026-08-13 发布。搜索引擎仍可能返回旧版缓存页，不能以旧缓存覆盖当前规范。

## 结论摘要

1. OpenAI Chat Completions 的规范入口是 `https://api.deepseek.com/chat/completions`，OpenAI SDK 的 `base_url` 是 `https://api.deepseek.com`，鉴权是 `Authorization: Bearer <API_KEY>`。官方当前示例没有 `/v1`。[首次 API 调用](https://api-docs.deepseek.com/)、[API 鉴权参考](https://api-docs.deepseek.com/api/deepseek-api/)
2. 截至核对日，文本翻译应使用当前模型 ID `deepseek-v4-flash` 或 `deepseek-v4-pro`。`deepseek-chat` 与 `deepseek-reasoner` 已于 2026-07-24 15:59 UTC 退役；当前官方模型列表也只返回 V4 模型。[更新日志](https://api-docs.deepseek.com/updates/)、[模型列表 API](https://api-docs.deepseek.com/api/list-models/)
3. V4 默认开启 thinking，默认 effort 为 `high`。Chat Completions 用 `thinking: {"type":"enabled|disabled"}` 切换，并用 `reasoning_effort: "low|high|max"` 调节；通过 OpenAI Python SDK 传 `thinking` 时应放入 `extra_body`。[Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)
4. 官方仍给出翻译场景 `temperature: 1.3` 的建议，但 thinking 模式下 `temperature`、`top_p`、`presence_penalty`、`frequency_penalty` 全部无效。因此若实现希望让翻译温度真正生效，必须明确关闭 thinking；这是由两页官方规范合并得到的直接结论。[温度建议](https://api-docs.deepseek.com/quick_start/parameter_settings/)、[Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)
5. JSON Output 需同时传 `response_format: {"type":"json_object"}`，并在 system/user prompt 中明确要求 JSON（官方指南还建议给出 JSON 示例）；否则可能持续输出空白直到 token 上限。还必须检查 `finish_reason === "length"`，避免把截断 JSON 当作成功。[JSON Output](https://api-docs.deepseek.com/guides/json_mode/)、[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)
6. 当前文本模型上下文为 1M，最大输出为 384K；输入与生成合计仍受上下文长度约束。不要从旧 V3 文档继续沿用 64K/8K 限制。[模型与价格](https://api-docs.deepseek.com/quick_start/pricing/)、[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)

## 1. OpenAI 兼容入口与鉴权

官方说明 DeepSeek API 与 OpenAI API 格式兼容，可直接使用 OpenAI SDK，只替换配置：[首次 API 调用](https://api-docs.deepseek.com/)。

| 项目 | 当前官方规范 |
| --- | --- |
| OpenAI 格式 `base_url` | `https://api.deepseek.com` |
| Chat Completions HTTP 地址 | `POST https://api.deepseek.com/chat/completions` |
| Content-Type | `application/json` |
| 鉴权 | `Authorization: Bearer ${DEEPSEEK_API_KEY}` |
| 模型枚举查询 | `GET https://api.deepseek.com/models` |

鉴权类型在 API 参考中明确为 HTTP Bearer Auth：[DeepSeek API](https://api-docs.deepseek.com/api/deepseek-api/)。生产实现不应把 key 放在 query、请求体、日志或源码中。

官方当前示例将 `base_url` 写为 `https://api.deepseek.com`，没有 `/v1`。兼容层若自行追加 `/chat/completions`，应避免重复路径；若项目允许用户填写完整 endpoint，也应区分“base URL”和“完整 URL”。

普通翻译不需要 `https://api.deepseek.com/beta`；该入口用于 Chat Prefix Completion、FIM 等 Beta 功能。[Chat Prefix Completion](https://api-docs.deepseek.com/guides/chat_prefix_completion/)、[FIM Completion API](https://api-docs.deepseek.com/api/create-completion/)

最小的当前规范请求：

```http
POST /chat/completions HTTP/1.1
Host: api.deepseek.com
Authorization: Bearer <DEEPSEEK_API_KEY>
Content-Type: application/json

{
  "model": "deepseek-v4-flash",
  "messages": [
    {"role": "system", "content": "Translate the user text into Simplified Chinese. Output only the translation."},
    {"role": "user", "content": "Hello, world."}
  ],
  "thinking": {"type": "disabled"},
  "temperature": 1.3,
  "stream": false
}
```

请求入口、Bearer header、消息形状及 thinking 参数均见[官方首次调用示例](https://api-docs.deepseek.com/)和[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)。上例翻译 prompt 是工程示例，不是官方固定模板。

## 2. 当前模型名与迁移要求

官方当前列出的模型 ID 为：[首页](https://api-docs.deepseek.com/)、[模型与价格](https://api-docs.deepseek.com/quick_start/pricing/)

| 模型 ID | 当前版本 | 翻译接入相关说明 |
| --- | --- | --- |
| `deepseek-v4-flash` | DeepSeek-V4-Flash-0731 | 文本可用；支持 thinking/non-thinking、JSON、stream |
| `deepseek-v4-pro` | DeepSeek-V4-Pro-0813 | 文本可用；支持 thinking/non-thinking、JSON、stream |
| `deepseek-v4-flash-vision-exp` | DeepSeek-V4-Flash-Vision-Exp | 实验性视觉模型；纯文本翻译没有必要依赖它 |

`deepseek-chat` 与 `deepseek-reasoner` 曾在过渡期分别路由到 V4 Flash 的 non-thinking / thinking 模式，但官方已公告它们于 2026-07-24 15:59 UTC 停用。因此当前代码或默认配置里若仍写这两个 ID，属于必须迁移项。[2026-04-24 V4 公告](https://api-docs.deepseek.com/news/news260424/)、[更新日志](https://api-docs.deepseek.com/updates/)

实现也可以在诊断或启动检查中调用 `GET /models`，以官方返回的 `data[].id` 判断账号当前可用模型，避免依赖陈旧的硬编码模型列表。[模型列表 API](https://api-docs.deepseek.com/api/list-models/)

## 3. Chat Completions 请求与响应

### 请求

`POST /chat/completions` 的 body 至少需要：[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)

- `model`：当前允许 `deepseek-v4-flash`、`deepseek-v4-pro`、`deepseek-v4-flash-vision-exp`。
- `messages`：至少一条，角色支持 `system`、`user`、`assistant`、`tool`；文本翻译通常发送 system 指令与 user 原文。

Chat Completions 规范没有 OpenAI 新接口中的 `developer` role；系统级翻译指令应使用 `system`。同理，输出上限字段是 `max_tokens`，不是 `max_completion_tokens`。所谓 OpenAI 兼容不代表支持 OpenAI 的每一个新字段，应只发送 DeepSeek Chat Completions 参考中明确列出的参数。[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)

翻译实现常用的可选字段：

- `thinking.type`：`enabled` 或 `disabled`，默认 `enabled`。
- `reasoning_effort`：`low`、`high`、`max`，默认 `high`；兼容传入的 `medium`/`xhigh` 实际映射为 `high`。
- `max_tokens`：生成 token 上限；输入与生成总量仍受模型上下文限制。
- `response_format`：`text`（默认）或 `json_object`。
- `stream`：是否以 SSE 增量返回。
- `stream_options.include_usage`：只应与 `stream: true` 一起使用。
- `temperature`：范围 0–2，默认 1；官方建议只调整 `temperature` 或 `top_p` 之一。
- `stop`：最多 16 个停止序列。
- `user_id`：仅允许 `[a-zA-Z0-9\-_]+`，最长 512；不得包含用户隐私信息。通过 OpenAI SDK 时放在 `extra_body`。[Rate Limit & Isolation](https://api-docs.deepseek.com/quick_start/rate_limit/)

`frequency_penalty` 与 `presence_penalty` 已标记 deprecated，传入也不生效，不应再作为翻译调优参数。[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)

### 非流式响应

成功响应是 `chat.completion` 对象。翻译正文位于 `choices[0].message.content`；thinking 模式的推理文本位于同级 `choices[0].message.reasoning_content`，不要把它拼进最终译文。应读取并验证 `choices[0].finish_reason`：[Chat Completions 响应结构](https://api-docs.deepseek.com/api/create-chat-completion/)

| `finish_reason` | 含义 | 翻译调用应如何处理 |
| --- | --- | --- |
| `stop` | 正常停止或命中 stop 序列 | 可进入格式/内容校验 |
| `length` | 达到 `max_tokens` 或上下文限制 | 视为截断，不应当作完整译文 |
| `content_filter` | 内容过滤导致省略 | 作为未完成/受限结果处理 |
| `tool_calls` | 模型发起工具调用 | 纯翻译通常不应出现 |
| `insufficient_system_resource` | 推理资源不足而中断 | 作为可重试的临时失败处理 |

`usage` 包含 `prompt_tokens`、`completion_tokens`、`total_tokens`，还提供 `prompt_cache_hit_tokens`、`prompt_cache_miss_tokens`；thinking 时 `completion_tokens_details.reasoning_tokens` 可用于单独观察推理成本。[Chat Completions 响应结构](https://api-docs.deepseek.com/api/create-chat-completion/)

## 4. 流式 SSE

设置 `stream: true` 后，Chat Completions 返回 `text/event-stream`：每个 `data:` 事件是一个 `chat.completion.chunk`，正文增量在 `choices[0].delta.content`，thinking 增量在 `choices[0].delta.reasoning_content`，最终以 `data: [DONE]` 结束。[Chat Completions API — streaming](https://api-docs.deepseek.com/api/create-chat-completion/)

必须注意：

- 增量是 token/文本片段，不保证按单词、句子、Unicode 字符或 JSON 字段边界切分；应顺序拼接 `delta.content`。
- thinking 模式下分别收集 `reasoning_content` 与 `content`，只把 `content` 交付为译文。
- `stream_options.include_usage: true` 时，普通 chunk 的 `usage` 为 `null`，最后一个内容 chunk 带总 usage；其后才是 `[DONE]`。官方当前说明即使不设置该选项，最后一个 chunk 仍携带 usage。[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)
- 服务等待期间可能发送 SSE 注释 `: keep-alive`，解析器必须忽略注释；非流式请求则可能收到空行。若 10 分钟仍未开始推理，服务端会关闭连接。[Rate Limit & Isolation — Keep-Alive](https://api-docs.deepseek.com/quick_start/rate_limit/)
- 若使用 JSON Output，完成前不要逐 chunk 解析 JSON；收到正常完成信号并确认 `finish_reason` 后再整体解析。

## 5. JSON Output

Chat Completions 的 JSON 模式规范：[JSON Output 指南](https://api-docs.deepseek.com/guides/json_mode/)、[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)

1. 传 `response_format: {"type":"json_object"}`。
2. system 或 user prompt 中明确出现并要求 `json` 输出；官方指南建议同时给出目标 JSON 示例。
3. 合理设置 `max_tokens`，防止 JSON 被中途截断。
4. 检查 `finish_reason`；`length` 时内容可能只是部分 JSON。
5. 官方说明 JSON Output 偶尔可能返回空内容，可通过调整 prompt 缓解；实现仍需为空响应准备失败处理或受限重试。

只需要纯译文字符串时，普通 `text` 更简单。若调用方确实需要 `{ "translation": "..." }` 之类的契约，再启用 JSON Output，并在解析后校验字段类型和业务 schema；`json_object` 只保证有效 JSON，并不等价于固定业务字段完全正确。

## 6. Thinking / reasoning 模式

V4 的当前默认行为与旧的“通过两个模型名区分 chat/reasoner”不同：[Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)

```json
{
  "thinking": {"type": "enabled"},
  "reasoning_effort": "high"
}
```

- 默认 thinking 为 enabled，默认 effort 为 high。
- effort 的有效档位为 `low`、`high`、`max`；`medium` 与 `xhigh` 会映射为 `high`。
- OpenAI Python SDK 的 Chat Completions 调用需用 `extra_body={"thinking": {"type": "..."}}` 传 DeepSeek 扩展字段；`reasoning_effort` 可作为 SDK 已知字段传递。
- thinking 模式不支持 `temperature`、`top_p`、`presence_penalty`、`frequency_penalty`。为兼容，这些字段不会报错，但不会产生效果。
- 非工具调用的多轮对话无需回传 `reasoning_content`；即使回传也会被忽略。带 `tools` 的对话则必须完整回传历次 `reasoning_content`，否则 API 返回 400。纯翻译一般不应携带 `tools`。

对常规逐段翻译，建议明确 `thinking: {"type":"disabled"}`：这能避免默认 thinking 引入额外推理延迟/输出成本，并使官方翻译温度 `1.3` 真正生效。这是基于官方参数行为的工程建议，而不是 DeepSeek 对翻译模型档位的官方强制要求。需要复杂语境推断、术语消歧或文学改写时，可以评估开启 thinking，但最终仍只消费 `content`。

## 7. 上下文、输出与 token

当前三个 V4 模型均为 1M context，最大输出 384K：[模型与价格](https://api-docs.deepseek.com/quick_start/pricing/)。Chat Completions 另明确“输入 token + 生成 token”受 context length 限制：[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)。

官方给出的粗略估算是：1 个英文字符约 0.3 token、1 个中文字符约 0.6 token，不同模型 tokenizer 仍会有差异；容量和计费判断应优先使用实际响应的 `usage`。[Token & Token Usage](https://api-docs.deepseek.com/quick_start/token_usage/)

翻译实现应据此：

- 不要把 1M 当成可全部用于输入；必须给 system prompt、术语表、历史上下文、输出及可能的 reasoning 留预算。
- `max_tokens` 应覆盖目标语言可能比原文更长的情况，并以 `finish_reason` 检查是否真正完成。
- 大文件宜在语义边界分段，并保留少量上下文/术语表；不要仅因理论 1M context 就一次塞满。
- 记录 `usage` 与 cache hit/miss 数据，便于发现重复前缀是否命中官方自动上下文缓存。[Context Caching](https://api-docs.deepseek.com/guides/kv_cache/)、[Chat Completions 响应结构](https://api-docs.deepseek.com/api/create-chat-completion/)

## 8. 错误码、重试与超时

官方错误表：[Error Codes](https://api-docs.deepseek.com/quick_start/error_codes/)

| HTTP | 官方原因/处置 | 实现分类建议 |
| --- | --- | --- |
| 400 | 请求格式无效；按错误提示修正 body | 不自动盲重试；记录安全脱敏后的参数错误 |
| 401 | API key 错误 | 不重试；提示检查 key |
| 402 | 余额不足 | 不重试；提示充值/切换账号 |
| 422 | 参数无效；按错误提示修改 | 不自动盲重试 |
| 429 | 发送过快；官方要求合理降速 | 可退避重试，并限制并发；持续失败应降载 |
| 500 | 服务端错误；官方要求短暂等待后重试 | 可退避重试；持续失败联系支持 |
| 503 | 高流量过载；官方要求短暂等待后重试 | 可退避重试 |

官方文档没有在该错误页规定具体重试次数、指数、抖动或 `Retry-After` 契约。因此“指数退避 + jitter + 最大次数/总时限”属于调用方工程策略，应可配置，不能写成 DeepSeek 的官方固定值。请求已经返回 HTTP 200 但 `finish_reason` 为 `insufficient_system_resource` 时，也应按临时失败处理，而不是交付不完整译文。[Chat Completions 响应结构](https://api-docs.deepseek.com/api/create-chat-completion/)

超时不要只设很短的“首字节超时”：官方说明排队时会以空行或 `: keep-alive` 保持连接，最迟可等待到 10 分钟未开始推理后由服务端关闭。[Rate Limit & Isolation](https://api-docs.deepseek.com/quick_start/rate_limit/)

## 9. 速率与并发限制

当前限制按“并发连接数”而不是文档中给出的 RPM/TPM 表述：[Rate Limit & Isolation](https://api-docs.deepseek.com/quick_start/rate_limit/)。

| 模型 | 账户并发上限 |
| --- | ---: |
| `deepseek-v4-pro` | 500 |
| `deepseek-v4-flash` | 2500 |
| `deepseek-v4-flash-vision-exp` | 2500 |

- 从请求发出到模型响应完成，计作一个并发连接。
- 限制按账户汇总，而不是按 API key 分开。
- 超限返回 HTTP 429。
- 可向官方申请扩容；官方称扩容不额外收费，但会按实际业务匹配。
- `user_id` 可用于内容安全、KV cache 与调度隔离，但普通账户所有 `user_id` 仍合并计算账户并发；扩容账户还可能同时受每个 `user_id` 的并发上限约束。

因此，多 key 轮换不能绕过同账户并发限制。翻译批处理应以账户级 semaphore/队列控制并发，并把单请求整个流式生命周期都算作占用。

## 10. 翻译场景检查清单

以下清单可用于逐项审查项目实现。带“官方”的条目直接来自 DeepSeek 文档；其余为根据该规范形成的工程检查项。

- [ ] **官方**：`base_url` 为 `https://api.deepseek.com`，请求路径正确拼成 `/chat/completions`。[首次 API 调用](https://api-docs.deepseek.com/)
- [ ] **官方**：使用 Bearer 鉴权，key 不进入请求体。[API 鉴权参考](https://api-docs.deepseek.com/api/deepseek-api/)
- [ ] **官方**：模型是 `deepseek-v4-flash` 或 `deepseek-v4-pro`，没有继续使用已退役的 `deepseek-chat` / `deepseek-reasoner`。[更新日志](https://api-docs.deepseek.com/updates/)
- [ ] **官方**：普通翻译使用正式入口，不把 `base_url` 指向只用于 Prefix/FIM 的 `/beta`。
- [ ] **官方**：输出上限使用 `max_tokens`，system 指令使用 `system` role；没有直接套用 `max_completion_tokens` 或 `developer` role。
- [ ] 显式决定 thinking，而不是误用 V4 默认 enabled；普通翻译建议 disabled。
- [ ] **官方**：若配置 `temperature: 1.3` 作为翻译温度，同时确认 thinking 已 disabled，否则 temperature 无效。[温度建议](https://api-docs.deepseek.com/quick_start/parameter_settings/)、[Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)
- [ ] prompt 明确源语言、目标语言、仅输出译文、专有名词/术语规则，以及 HTML/字幕时间码/占位符/换行是否必须原样保留。
- [ ] 将不可信原文作为数据边界包裹，防止原文中的命令式文本改变翻译规则。
- [ ] **官方**：读取 `message.content`；thinking 时不把 `reasoning_content` 混入译文。[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)
- [ ] **官方**：检查 `finish_reason`，仅把符合业务要求的完成结果交付；`length` 不能算成功。[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)
- [ ] **官方**：JSON 模式同时设置 `response_format` 和 JSON prompt，并处理空内容、截断、整体 JSON 解析失败。[JSON Output](https://api-docs.deepseek.com/guides/json_mode/)
- [ ] **官方**：流式处理忽略 keep-alive，分别拼接 `delta.content` / `delta.reasoning_content`，识别 `[DONE]`。[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)、[Rate Limit & Isolation](https://api-docs.deepseek.com/quick_start/rate_limit/)
- [ ] 429/500/503 有有界退避重试；400/401/402/422 不盲重试；重试不会导致结果重复入库。
- [ ] 并发在账户级受控，多 API key 不被误认为各有独立额度。
- [ ] 记录脱敏后的模型、thinking 状态、耗时、HTTP 状态、finish reason、token usage 和重试次数；绝不记录 API key，原文/译文日志需遵守项目隐私策略。

## 官方资料索引

- [Your First API Call](https://api-docs.deepseek.com/)
- [Models & Pricing](https://api-docs.deepseek.com/quick_start/pricing/)
- [Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)
- [Lists Models](https://api-docs.deepseek.com/api/list-models/)
- [Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)
- [JSON Output](https://api-docs.deepseek.com/guides/json_mode/)
- [The Temperature Parameter](https://api-docs.deepseek.com/quick_start/parameter_settings/)
- [Rate Limit & Isolation](https://api-docs.deepseek.com/quick_start/rate_limit/)
- [Error Codes](https://api-docs.deepseek.com/quick_start/error_codes/)
- [Change Log](https://api-docs.deepseek.com/updates/)
